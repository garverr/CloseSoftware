# Agent 19 — Security Audit: Audit Trail, Multi-Tenancy & Observability

**Categories:** 44 (Audit trail / SOX-relevant completeness), 45 (Multi-tenancy & data isolation), 46 (Logging & observability)

---

## Category 44 — Audit Trail / SOX-Relevant Completeness

### What exists

| Item | Evidence |
|---|---|
| `AuditRecord` entity with actor, role, action, entityType, entityId, reportPackageId, before/after JSON, createdAt | `Domain/Entities.cs:778-791` |
| `AuditAsync()` helper; `AddVersionAndAuditAsync()` for package-state changes | `Program.cs:3648-3698` |
| Before/after snapshots on every package-level write | `Program.cs:671, 706, 734, 760, 794` |
| Coverage: flux signoff, AI runs, exports, share links, distribution schedules, eliminations, mappings, account map/split/reject/review, FX rates, KPIs, forecasts | 75 `AuditAsync` call sites |
| `RedactSensitive()` applied to before/after JSON blobs | `Program.cs:3670-3671, 4778-4791` |
| Read-only audit endpoint (`GET /api/audit`) — no delete/update endpoint exists | `Program.cs:3332-3342` |

### Gaps

| Severity | Gap | Location |
|---|---|---|
| **Blocker** | `POST /api/packages` creates a package with no auth (`Can()`) check and no `AuditAsync` call — the primary "create package" path is fully blind | `Program.cs:353-375` |
| **Major** | `POST /api/xero/backfill/{runId}/pause` and `/resume` mutate `XeroBackfillRun.Status` with no audit record | `Program.cs:1796-1820` |
| **Major** | `POST /api/exports/{exportId}/qa` mutates `ExportArtifact.MetadataJson` with no audit and no `Can()` check | `Program.cs:2198-2215` |
| **Major** | `POST /api/xero/connections/{id}/reconnect` and `/tenants/{id}/reconnect` mutate OAuth state with no audit | `Program.cs:1547-1590` |
| **Minor** | `AuditRecord` lacks a `TenantId` / `OrganizationId` column — the full trail is queryable without org scope; a report that is not bound to a `ReportPackage` (e.g. mapping-level ops) has no tenant tag | `Domain/Entities.cs:778-791` |
| **Minor** | Audit is committed in the same `SaveChangesAsync` as the business change; if the transaction rolls back, the audit row is lost; there is no outbox or secondary store | `Program.cs:3648-3673` |

---

## Category 45 — Multi-Tenancy & Data Isolation

### What exists

| Item | Evidence |
|---|---|
| `TenantId` column present on all Xero-sourced tables: `XeroJournal`, `XeroJournalLine`, `XeroLedgerMonthlySummary`, `GlAccount`, `StatementRun`, etc. | `Data/AppDbContext.cs:69-88` |
| All financial queries filter by `OrganizationId` via LINQ `.Where()` inline | `Program.cs:154-160, 311, 334, 977, 1060, 2669` |
| Composite unique indexes prevent cross-tenant data collisions | `AppDbContext.cs:73-80` |

### Gaps

| Severity | Gap | Location |
|---|---|---|
| **Blocker** | No EF Core global query filters (`HasQueryFilter`) anywhere in `AppDbContext.OnModelCreating` — the entire isolation barrier is a per-query opt-in. A developer adding a new query can silently read all-tenant data | `Data/AppDbContext.cs:56-110` |
| **Blocker** | Auth identity is a trust-on-header model (`X-FR-Role`, `X-FR-User`) with hardcoded fallback to `"Admin"` / `"dev-admin"` — there is no signed claim, no session, no JWT. Any caller who omits the header gets Admin rights, making multi-tenant enforcement impossible to guarantee | `Program.cs:3632-3646` |
| **Major** | `AuditRecord` has no `OrganizationId` or `TenantId`; `GET /api/audit` without `reportPackageId` returns all records across all orgs with no auth check — a caller can read the complete audit history of every tenant | `Program.cs:3332-3342`, `Domain/Entities.cs:778-791` |
| **Major** | Slide/block endpoints (e.g. `PUT /api/blocks/{blockId}`) fetch the block by primary key only and do not verify the block belongs to a package the authenticated user owns — cross-org writes are possible if an attacker knows a block GUID | `Program.cs:711-737` |
| **Minor** | AI prompt construction (in `CodexCommandBuilder` / `AiPackageDraftService`) passes full `InputJson` from the request; no evidence of org boundary enforcement preventing cross-tenant data leakage into prompts | `Domain/Entities.cs:~` |

---

## Category 46 — Logging & Observability

### What exists

| Item | Evidence |
|---|---|
| ASP.NET Core default `ILogger<T>` used in services | `Services/XeroLedgerServices.cs:20, 387, 1006; Services/FinancialServices.cs:767, 783` |
| Structured `LogWarning` / `LogError` with message templates and parameter names | `XeroLedgerServices.cs:78, 387` |
| Basic appsettings `Logging.LogLevel` configuration | `appsettings.json:29-34` |

### Gaps

| Severity | Gap | Location |
|---|---|---|
| **Blocker** | No Serilog, OpenTelemetry, or any structured sink configured — logs emit as plain text to console only; no JSON output, no correlation ID enrichment, no external sink (Seq, Application Insights, Datadog, OTLP) | `FinancialReporting.Api.csproj` (no OTel packages); `Program.cs:1-80` |
| **Major** | No correlation/trace IDs propagated; `HttpContext.TraceIdentifier` is never logged or attached to `AuditRecord` — it is impossible to correlate a failed request with its audit row or AI job | `Program.cs` (no `Activity.Current`, no `BeginScope`) |
| **Major** | No metrics: Xero sync lag, AI job queue depth, error rates, export latency — all missing; health endpoint exists but returns a static JSON blob with no live counters | `Program.cs:100-105` |
| **Major** | `RedactSensitive()` only covers a hard-coded list of six token markers; field-level financial figures (account balances, GL amounts) that appear in `AfterJson` of audit records are stored unredacted in SQLite with no encryption at rest | `Program.cs:4778-4791` |
| **Minor** | Exception logging in the Xero callback (`logger.LogWarning(ex, ...)`) uses the framework logger; no structured properties (e.g. `{OrganizationId}`, `{TenantId}`) are attached to error log entries | `Program.cs:1617` |

---

## Cross-Cutting Verdict

The audit-record schema and call-site coverage are surprisingly thorough for a single-file API — roughly 75 write operations are traced with before/after snapshots. However, the entire security posture rests on three fragile assumptions that each, independently, constitute a SOC 2 / SOX blocker: (1) identity is fully spoofable via HTTP headers, (2) there are no EF global query filters to enforce org isolation at the ORM layer, and (3) there is no structured or remotely-shipped log, making an externally-verifiable audit trail impossible to demonstrate to auditors.

### Top 3 Fixes

1. **Replace header-based auth with signed JWTs and enforce org membership from the token claim** — until identity is trustworthy, every downstream control (audit actor, Can() checks, org scoping) is meaningless. This is an architectural blocker for SOC 2 readiness. (`Program.cs:3632-3646`)

2. **Add EF Core `HasQueryFilter` on every entity that has an `OrganizationId` column** and remove the three `HasQueryFilter`-less entities from the public `DbSet` surface, replacing them with scoped accessor methods. This closes the accidental cross-tenant read risk without requiring every developer to remember per-query `.Where()` clauses. (`Data/AppDbContext.cs:56-110`)

3. **Add OpenTelemetry SDK + a structured log sink (e.g. Serilog with JSON console + OTLP exporter)** and enrich every log scope with `OrganizationId`, `TenantId`, and `CorrelationId`; add the correlation ID as a column on `AuditRecord`. This is the minimum required to produce the externally-verifiable, immutable trail that SOX and Trullion-style audit-grade standards demand. (`FinancialReporting.Api.csproj`; `Program.cs:1-80`)
