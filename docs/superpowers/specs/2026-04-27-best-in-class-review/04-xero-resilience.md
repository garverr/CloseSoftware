# Best-in-Class Review — Xero Resilience

**Date:** 2026-04-27
**Agent:** 04 of 20
**Categories:** 6 (Incremental sync / change detection / idempotency / dedupe), 7 (Rate-limit / retry / backoff / partial-failure recovery), 8 (Historical backfill / re-sync / period-locking)

---

## Category 6 — Incremental Sync, Change Detection, Idempotency, Dedupe

| Severity | Finding | Evidence | Best-in-class gap | Recommendation |
|----------|---------|----------|-------------------|----------------|
| **Major** | Incremental sync issues a single-page fetch; Xero returns max 100 journals per call. If more than 100 new journals arrive between sync cycles the cursor advances to the last number seen in that one page, silently leaving a gap. | `XeroLedgerServices.cs:560–586` — `FetchJournalsPayloadAsync` calls `GET /Journals?offset={cursor}` once, no loop | Numeric/Closecore paginate until an empty page is returned before advancing the high-water mark. Backfill correctly loops (`XeroBackfillServices.cs:570–596`) but the live incremental path does not. | Replace `FetchJournalsPayloadAsync` with a loop (identical to `ImportJournalsAsync` in the backfill service) that pages until `Count == 0`. |
| **Moderate** | Upsert keyed on `XeroJournalId` (GUID) not `JournalNumber`. Xero's journal GUID is stable, so this is correct, but the cursor advances on `MaxJournalNumber`, not on `XeroJournalId`. Any gap between the two values is silently ignored. | `XeroLedgerServices.cs:407–461` — `FirstOrDefaultAsync` by `XeroJournalId`; cursor update at line 366–368 | Best practice: treat `JournalNumber` as the monotonic offset (which Xero documents as such) and confirm every number in range was received before advancing. | Add a database unique constraint on `(TenantId, XeroJournalId)` and assert `MaxJournalNumber == min(offset + returnedCount, actualMax)` before advancing cursor. |
| **Low** | No concurrent-sync guard. Two simultaneous trigger paths (background worker + manual `/api/xero/sync` call) can race; cursor is read optimistically and two writes may each save stale journal counts. | `XeroLedgerServices.cs:332–389` — no `SemaphoreSlim` or DB-level advisory lock wrapping `SyncTenantAsync`; `SyncEveryMinutes` guard at line 351 only covers the worker interval, not concurrent HTTP-triggered syncs | Numeric uses a per-tenant distributed lock (Postgres advisory lock). | Add a `SemaphoreSlim(1,1)` keyed by `tenantId` in `XeroTenantLedgerService` (singleton-scoped) or enforce via a `cursor.Status == "Running"` early-exit check. |

---

## Category 7 — Rate-Limit / Retry / Backoff / Partial-Failure Recovery

| Severity | Finding | Evidence | Best-in-class gap | Recommendation |
|----------|---------|----------|-------------------|----------------|
| **Good** | `XeroApiRequestScheduler` (backfill only) implements proactive per-tenant sliding-window budgets for minute (soft 45, hard 60) and day (soft 4000, hard 5000), respects `Retry-After` on 429, and retries transient HTTP errors with exponential backoff (`Math.Pow(2, attempt)`, max 60 s). | `XeroBackfillServices.cs:50–224` — `WaitForBudgetAsync`, `DelayForRetryAsync`, `TooManyRequests` branch | Fully production-grade for the backfill path. | — |
| **Blocker** | The live incremental sync path (`SyncTenantAsync`) does **not** use `XeroApiRequestScheduler`. It calls `client.GetAsync` directly with zero retry, zero rate-limit awareness, and no 429 handling — a raw `InvalidOperationException` is thrown on any non-success status code. | `XeroLedgerServices.cs:569–586` — bare `client.GetAsync` without scheduler; contrast with `XeroBackfillServices.cs:578` which calls `scheduler.GetStringAsync` | Every competitor that targets Xero (Numeric, Closecore) runs all Xero traffic through a single gateway with back-pressure. | Inject `XeroApiRequestScheduler` into `XeroTenantLedgerService` and route `FetchJournalsPayloadAsync` through `scheduler.GetStringAsync`. |
| **Major** | Backoff formula has no jitter. Under simultaneous multi-tenant syncs all tenants back off to the same second, causing thundering-herd retry bursts. | `XeroBackfillServices.cs:123–128` — `Math.Pow(2, attempt)` with no random spread | AWS and Azure SDK guidance mandates `+Random(0, baseDelay)` jitter. Closecore uses full-jitter exponential. | Add `+ Random.Shared.NextDouble() * seconds` to `DelayForRetryAsync`. |
| **Moderate** | Token refresh in the incremental path has no retry. A transient 5xx from `identity.xero.com` during token refresh throws immediately, failing the entire tenant sync run. | `XeroLedgerServices.cs:588–607` — `RefreshTokenAsync` posts once; any non-success throws | Token endpoint outages are common during Xero maintenance windows (~monthly). | Wrap `RefreshTokenAsync` in a 2–3 attempt retry loop with 2 s / 4 s delays. |

---

## Category 8 — Historical Backfill, Re-sync, Period-Locking

| Severity | Finding | Evidence | Best-in-class gap | Recommendation |
|----------|---------|----------|-------------------|----------------|
| **Blocker** | `IsClosed` on `ReportingPeriod` is never checked before overwriting financial statement lines. A re-sync or backfill will silently delete and rewrite `FinancialStatementLines` for closed/locked periods. | `XeroBackfillServices.cs:658–664` — `ExecuteDeleteAsync` runs unconditionally; `ReportingPeriod.IsClosed` exists in `Entities.cs:77` but is never read in any sync or backfill code path. `XeroLedgerServices.cs:822` and `XeroBackfillServices.cs:1189` always set `IsClosed = false` on newly created periods. | Fathom, Numeric, and Closecore all hard-block writes to user-locked periods and surface a clear error in the UI. This is an audit-critical control. | Guard every delete/insert in `ImportMonthlyStatementsAsync` and `ProjectGlForPeriodAsync` with `if (period.IsClosed) throw` (or skip + warn). Add a UI badge showing locked periods in the backfill coverage view. |
| **Major** | Backfill per-tenant task does not resume mid-month on restart — it resets all counters (`task.JournalsImported = 0` etc.) and re-processes all months from the beginning of the task. A failed task on month 18 of 24 restarts from month 1. | `XeroBackfillServices.cs:409–419` — counters zeroed unconditionally on task retry; no `lastCompletedMonth` cursor stored on `XeroBackfillTenantTask` | Closecore stores a `lastProcessedPeriod` watermark per task for exact resumption. | Add a `LastCompletedPeriodKey` column to `XeroBackfillTenantTask` and skip months `<= LastCompletedPeriodKey` at the start of `ProcessTenantTaskAsync`. |
| **Low** | Backfill re-sync does not dedupe raw snapshots. Each re-run appends a new `XeroRawReportSnapshot` row for the same tenant+period+type without removing the previous one first. Query latency grows unboundedly with repeated backfills. | `XeroBackfillServices.cs:645–657` — `db.XeroRawReportSnapshots.Add` with no prior delete; contrast with `FinancialStatementLines` which correctly deletes before re-insert (line 658) | Append-only snapshots are fine for auditing; point queries need a "current" index. | Add a unique index or a `IsCurrent` boolean on `XeroRawReportSnapshot` and clear the flag on re-run, or delete old snapshots during re-import. |

---

## Pillar Verdict: Xero Integration

The **backfill path** is well-engineered: proactive rate budgets, pause/resume/cancel controls, per-tenant progress tracking, and reconciliation via trial-balance snapshots. The **incremental live sync path** is the weak link — it bypasses every resilience mechanism built for backfill, lacks pagination, and has no rate-limit or retry logic. The **period-locking gap** is an audit-critical correctness bug that affects both paths.

---

## Top 3 Fixes (Priority Order)

1. **[Blocker — Cat 8] Guard `IsClosed` periods before any write.** One guard in `ImportMonthlyStatementsAsync` and one in `ProjectGlForPeriodAsync` prevents silent corruption of audited data. Without this, a re-sync can invalidate a signed-off close packet.

2. **[Blocker — Cat 7] Route incremental sync through `XeroApiRequestScheduler`.** `FetchJournalsPayloadAsync` in `XeroLedgerServices` must call `scheduler.GetStringAsync` to inherit 429 handling, exponential backoff, and rate-budget enforcement. Current code will fail hard on any Xero rate event during scheduled sync cycles.

3. **[Major — Cat 6] Paginate incremental sync until empty page.** A single-page fetch silently drops journals when > 100 arrive between cycles. Mirror the loop already present in `ImportJournalsAsync` (`XeroBackfillServices.cs:570–596`) into the live sync path.

---

**Blocker count: 2 | Major count: 3 | Moderate count: 2 | Low count: 2**
