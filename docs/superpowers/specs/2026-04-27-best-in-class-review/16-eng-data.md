# Engineering Audit — Data, SignalR, and Codex Job Queue

**Categories:** 32, 33, 34, 37
**Reviewer:** Agent 16 of 20
**Date:** 2026-04-27

---

## Category 32 — EF Core Schema, Indexes, Migrations Strategy

| Area | Finding | Severity |
|---|---|---|
| GlTransaction index | No index on `(GlAccountId, TransactionDate)`. Date-range scans across journal lines do a full table scan. | **Major** |
| XeroJournalLine index | `IX_XeroJournalLines_TenantId_AccountCode` exists (AppDbContext.cs:75) but there is no `JournalDate` column on `XeroJournalLine` — date filtering requires a join back to `XeroJournals.JournalDate`, which is not indexed for date-range traversal. | **Major** |
| AiRun index | No index on `AiRun.Status`. The worker polls `WHERE Status = 'Queued'` every 2 seconds (FinancialServices.cs:794–797). | **Major** |
| AuditRecord index | `(EntityType, EntityId)` and `ReportPackageId` indexed (AppDbContext.cs:92–93). Adequate. | OK |
| FluxReviewGroup index | Unique on `(ReportPackageId, FluxType, StatementType, GroupKey)` (SqliteSchemaPatch.cs:528). Covers look-ups. | OK |
| Migrations disabled | `UseMigrations=false` (appsettings.json:4); schema is patched via `SqliteSchemaPatch.EnsureAsync`. Each new column is an individual `ALTER TABLE` inside `AddColumnIfMissingAsync` (SqliteSchemaPatch.cs:541–569). The patch file has grown to ~35 `ALTER TABLE` calls on `FluxReviewGroups` alone, with no ordering guarantees or idempotency test on index recreation. | **Major** |
| Decimal columns | Global precision/scale set (AppDbContext.cs:103–109). Consistent. | OK |

**Key gap:** there are no composite indexes involving a date column on the two highest-volume tables (`GlTransaction`, `XeroJournalLine`). Any flux variance query that joins journal lines by account and date range hits full scans.

---

## Category 33 — SQLite Suitability

| Dimension | Finding | Severity |
|---|---|---|
| WAL mode | Dev DB is ~260 MB + 4 MB WAL. WAL supports one writer / many readers — acceptable for current dev scale. | OK |
| Growth trajectory | Xero backfill imports raw `PayloadJson` into `XeroJournals.PayloadJson` (SqliteSchemaPatch.cs:126) and `XeroRawReportSnapshots.PayloadJson`. Each raw snapshot is a full Xero report payload. At multi-tenant / multi-year backfills these blobs compound rapidly. 260 MB in dev with a fraction of production data is a warning sign. | **Major** |
| Production plan | No evidence of a production database target, connection string override, or PostgreSQL/SQL Server migration path. `UseSqlite` is a flat boolean in `appsettings.json` (line 2) with no corresponding production `appsettings.Production.json` visible. | **Blocker** |
| Concurrent writes | SQLite serialises all writes. The Xero ledger sync worker and the Codex background worker can both write at the same time. Under busy backfill conditions (XeroBackfillService) this produces write contention and can surface `SQLITE_BUSY` without a retry policy. | **Major** |
| Schema patching at startup | `SqliteSchemaPatch.EnsureAsync` is called on every startup and issues ~50 DDL statements serially. Idempotent (`IF NOT EXISTS`) but each statement takes an exclusive write lock. On a 260 MB WAL file startup is noticeably slow. | Minor |

---

## Category 34 — SignalR Real-Time AI Status Design

| Area | Finding | Severity |
|---|---|---|
| Hub definition | `AiHub` (Hubs/AiHub.cs:5) is a bare `Hub` subclass with no methods, groups, or auth. All push comes from `IHubContext<AiHub>` in `CodexWorker`. | Minor |
| Broadcast scope | `hub.Clients.All.SendAsync("aiRunUpdated", ...)` (FinancialServices.cs:855, 863) broadcasts every progress tick to every connected client. Any user watching any other package sees every AI run update. No group or connection filtering by `runId` or `packageId`. | **Major** |
| Scale-out | `builder.Services.AddSignalR()` (Program.cs:17) with no Redis backplane. A second API instance would not receive messages from the Codex worker running on the first instance. No sticky-session configuration is documented. | **Blocker** (if ever horizontally scaled) |
| Auth on hub | Hub has no `[Authorize]` attribute. Any unauthenticated WebSocket client can subscribe and receive all AI run progress events. | **Major** |
| Reconnect / missed events | Client reconnect after disconnection has no replay or state sync; the client must re-poll `/api/ai/runs/{runId}` manually. | Minor |

---

## Category 37 — Codex CLI Job Queue, Concurrency, Cancellation, Isolation

| Area | Finding | Severity |
|---|---|---|
| Concurrency bound | `CodexWorker.ProcessNextAsync` picks one queued run, executes it, then loops with a 2-second delay (FinancialServices.cs:786). Effective concurrency = 1. No `SemaphoreSlim`, no `Channel<T>`. This is safe but sequentialises all AI work. | OK (safe but limited) |
| Cancellation (DB-level) | `/api/ai/runs/{runId}/cancel` sets `Status = Cancelled` only if `Status == Queued` (Program.cs:891–893). A `Running` job cannot be cancelled via the API. | **Major** |
| Cancellation (process-level) | `CancellationRequested` is read from the DB field (FinancialServices.cs:841) only after the process exits. There is no mid-run signal to the live `Process`. A hung Codex CLI will run until the 8-minute timeout fires (line 937) regardless of the user clicking cancel. | **Major** |
| Timeout | 8-minute `CancelAfter` via linked `CancellationTokenSource` (FinancialServices.cs:936–938). Timeout is enforced at the `WaitForExitAsync` call. If the token fires, `process.WaitForExitAsync` throws; however there is no explicit `process.Kill()` in the catch path, so the Codex subprocess may linger as an orphan. | **Major** |
| Working directory isolation | All runs share a single temp directory `financial-reporting-codex/` (line 899). Output files are named by `run.Id` (line 901), so collision is unlikely, but the directory is never cleaned up. Output files from previous runs accumulate indefinitely. | Minor |
| Job persistence across restart | `AiRun` records with `Status = Running` are never reset to `Queued` on startup. After a crash the worker skips them (only queries `Status == Queued`), leaving orphaned `Running` runs in the DB forever. | **Major** |
| Queue persistence | Jobs are persisted in SQLite — restarts do not lose queued items. Queued runs re-enter processing on next startup. This is a strength. | OK |
| Sandbox flags | `--ephemeral --sandbox read-only` (FinancialServices.cs:437–439) limits Codex filesystem access. Good defence-in-depth. | OK |

---

## Cross-Cutting Verdict

The data layer is **not production-ready**: no date-based indexes on the two highest-volume tables, no migration strategy, no production database target, and SQLite with no retry policy for concurrent writers. SignalR broadcasts to all clients with no auth on the hub and no scale-out plan. The Codex worker is safe (concurrency=1) but has a stuck-run leak on crash and cannot actually kill a running process when the user cancels.

**Blocker count: 2** (no production DB plan; no SignalR Redis backplane for multi-instance)
**Major count: 10** (missing indexes on GlTransaction/AiRun, schema patch drift, SQLite write contention, raw-payload blob growth, SignalR broadcast scope, hub auth, Running-run cancel, orphaned process on timeout, orphaned Running records on restart)

---

## Top 3 Fixes

1. **Add composite indexes for query hot paths** — `CREATE INDEX IX_GlTransactions_GlAccountId_TransactionDate ON GlTransactions (GlAccountId, TransactionDate)` and an index on `AiRuns(Status)`. These are single SQL statements addable via `SqliteSchemaPatch`.

2. **Implement startup run-recovery and in-process cancellation** — On `CodexWorker` startup, reset all `Status = Running` rows back to `Queued`. Store the live `Process` reference and call `process.Kill(entireProcessTree: true)` inside the catch after timeout or when `CancellationRequested` is detected mid-poll.

3. **Scope SignalR messages to the relevant run** — Replace `hub.Clients.All.SendAsync(...)` with group-scoped sends (`await hub.Groups.AddToGroupAsync(connectionId, $"run-{run.Id}")`) and add `[Authorize]` to `AiHub`. Document a Redis backplane requirement for any multi-instance deployment before it is attempted.
