# Financial Reporting Software

A CFO-grade reporting platform with AI baked in. Three product pillars:

1. **Xero importing** — multi-tenant OAuth, COA + journal-line capture, void-aware reconciliation.
2. **Flux analysis with AI investigation** — month/quarter/year/budget bases, deterministic vendor + cadence labels fed to Codex CLI for ranked-hypothesis explanations with journal-line citations.
3. **AI-generated monthly Board Package** — prior month is the baseline; the diff engine emits typed Keep / Modify / Add / Remove decisions filtered through a per-package board materiality threshold, then renders a board-grade PDF (QuestPDF), XLSX (ClosedXML), and a CFO approval gate that locks the snapshot before distribution.

See `docs/superpowers/specs/2026-04-27-best-in-class-review/99-rollup.md` for the full audit and remediation history.

## Stack

| Layer | Tech |
| --- | --- |
| API | ASP.NET Core 10 + EF Core 10 |
| AI | Codex CLI (server-side; no OpenAI API key) |
| DB | SQLite (dev). Postgres provider option in csproj (deferred until Npgsql ships GA on EF Core 10). |
| Frontend | React 19 + Vite + TypeScript |
| Real-time | SignalR (per-run groups; no broadcast-to-all) |
| Logging | Serilog → JSON stdout |
| Observability | OpenTelemetry SDK with OTLP exporter |
| PDF | QuestPDF (Community license) |
| XLSX | ClosedXML |

## Run locally

```bash
# 1. Start the API
dotnet run --project src/FinancialReporting.Api --urls http://localhost:5264

# 2. Start the frontend
cd src/FinancialReporting.Web
npm install
npm run dev -- --host 127.0.0.1
```

Open <http://127.0.0.1:5173>.

## Configuration

See `src/FinancialReporting.Api/appsettings.Development.json`.

| Section | Key | Notes |
| --- | --- | --- |
| `UseSqlite` | `true` | Set false to swap providers (Postgres pending). |
| `ConnectionStrings:SqliteConnection` | `Data Source=…` | Defaults to a relative `financial-reporting-dev.db`. |
| `Ai:UseMockRunner` | `true` (dev) / `false` (real Codex) | Mock returns the new `hypotheses[]` schema so the validator stays satisfied without launching jobs. |
| `Ai:CodexPath` | absolute path | Required when `UseMockRunner=false`. The service must run under the OS account logged into Codex CLI. |
| `Ai:TimeoutMinutes` | `8` | The worker kills the entire process tree on timeout (P3.35). |
| `Xero:ClientId` | string | PKCE-only public client; no secret required. |
| `Xero:RedirectUri` | url | Must match the Xero developer-portal callback. |
| `Xero:Scopes` | space-separated | Default scopes are minimum needed for ledger sync + reports + COA. |
| `Xero:EnableLedgerSyncWorker` | `true` | Background tenant ledger sync. |
| `Xero:EnableBackfillWorker` | `true` | Historical backfill orchestrator. |
| `Xero:LiveSyncMaxJournalPagesPerCycle` | `50` | Pagination cap; raise for very-active tenants (P0.4). |
| `Backup:Enabled` | `true` | Nightly hot-backup of the SQLite file (P0.9). |
| `Backup:HourUtc` | `3` | Hour (0–23) at which the nightly backup runs. |
| `Backup:Directory` | `backups` | Relative to the content root unless absolute. |
| `Backup:RetainCopies` | `14` | Old backups beyond this count are pruned. |
| `DataProtection:ApplicationName` | `FinanceApp.Api` | Must match the app whose tokens you want to import via the V2 path. |
| `DataProtection:KeysPath` | path | Where the key ring is persisted. **Back this up alongside the SQLite file** — losing it makes every Xero token unrecoverable. |

OpenTelemetry destination is read from the standard `OTEL_EXPORTER_OTLP_ENDPOINT` env var.

## First-time developer setup

### 1. Tooling

- .NET 10 SDK
- Node 20+ and npm
- (For real AI runs) Codex CLI authenticated as the OS user that will run the API service

### 2. Xero developer-portal app

1. Sign in to <https://developer.xero.com/myapps/>.
2. Create a "Web app". Type: **PKCE** (public client; no secret).
3. Redirect URI: `http://localhost:5264/api/xero/callback` (dev) or your production callback.
4. Copy the Client ID into `appsettings.Development.json:Xero:ClientId`.
5. Default scopes already in `appsettings.Development.json` are sufficient for COA + journals + reports + offline_access.

### 3. Database init

The first run of the API:

- Creates the SQLite file via `EnsureCreatedAsync` (or runs migrations if `Database:UseMigrations=true`).
- Applies the WAL pragma (P0.8).
- Runs `SqliteSchemaPatch.EnsureAsync` to add any columns missing from a previous schema.
- Purges runtime mock data.

No manual seed step is required.

### 4. Connecting your first Xero org

1. From the running web UI go to **Settings → Xero Connection** and click *Connect*.
2. Approve the OAuth consent in the Xero popup.
3. The first ledger sync starts automatically; the COA cache (P0.6) populates first, then the journal pagination loop.

### 5. First Board Package

1. Create a `ReportingPeriod` for the period you want.
2. POST `/api/packages/ensure` with `{ organizationKey, periodKey }`. The new package is auto-linked to the most recent prior package via `PriorPackageId` so the AI baseline diff has an anchor.
3. POST `/api/packages/{id}/refresh-flux` (existing endpoint) to compute MoM/YoY/YTD/Budget flux groups.
4. POST `/api/packages/{id}/ai-package-draft` to produce typed Keep/Modify/Add/Remove draft suggestions filtered through the board materiality threshold.
5. Review and accept drafts in the UI (or via `/api/ai-package-drafts/{id}/accept`). Accepted blocks carry `IsAiAuthored = true` (P1.17).
6. POST `/api/packages/{id}/approve` to lock the package and stamp `ApprovedBy`/`ApprovedAt`. Distribution send is blocked until this happens (P1.16).
7. POST `/api/exports/pdf` and `/api/exports/excel` to render board-grade artifacts via QuestPDF / ClosedXML.

## Auth

- **Development**: header-based bypass (`X-FR-Role: Admin`, `X-FR-User: …`) is active. Missing headers default to Admin / dev-admin.
- **Non-Development**: `/api/*` requires bearer JWT auth except `/api/health` and `/api/xero/callback`. `X-FR-*` headers are ignored outside Development.
- Authenticated users without an `org` claim fail closed; `PlatformAdmin` is the explicit unscoped role. Xero tenant metadata is further limited by `xero_tenants` claims when present.

## Backups + DR

- SQLite WAL mode is enforced at startup so a hot backup is safe.
- `SqliteBackupService` runs nightly at the configured UTC hour and rotates copies (P0.9).
- **Always back up the DataProtection key ring directory**. Without those keys the encrypted Xero refresh tokens in the DB cannot be decrypted, and every tenant must reconnect.

## Tests

```bash
dotnet test
```

The numeric regression suite covers variance edge cases (sign flips, zero-prior, both-zero), dual-threshold AND vs OR logic, materiality defaults, the baseline diff engine across all four decision kinds, and the YearToDate flux path. The workflow tests cover auth/org fail-closed behavior, AI citation validation, Xero OAuth callback, period sync, ledger journal upsert behavior, ledger retention, runtime mock cleanup, AI draft staging, and the V2 import.

## Layout

```
src/
  FinancialReporting.Api/
    Program.cs                        # being incrementally drained into Features/
    AuthBypass.cs                     # P0.1 — env-gated dev bypass
    GlobalExceptionHandler.cs         # P3.32 — RFC 7807
    OrganizationContext.cs            # P3.30 — tenant scope accessor
    Domain/Entities.cs                # all entities
    Data/
      AppDbContext.cs                 # incl. global query filters (P3.30)
      SqliteSchemaPatch.cs            # ALTER TABLE migrations for SQLite dev
      SeedData.cs
      RealDataCleanupService.cs
    Features/
      Packages/
        PackageApprovalEndpoints.cs   # P3.27 — feature-folder pattern starter
    Services/
      FinancialServices.cs            # CodexWorker, ExportService, XeroIntegrationService
      XeroLedgerServices.cs           # tenant ledger sync, FluxReviewService, AiPackageDraftService
      XeroBackfillServices.cs         # XeroBackfillService + XeroApiRequestScheduler
      FinancialStatementGroupingService.cs
      PackageDiffService.cs           # P1.1 — marquee baseline diff
      SqliteBackupService.cs          # P0.9 — nightly hot backup
      XeroTokenRefreshLock.cs         # P0.3 — per-tenant refresh lock
    Hubs/AiHub.cs                     # SignalR per-run groups (P3.35)
  FinancialReporting.Web/
    src/App.tsx                       # being incrementally split — see CONTRIBUTING.md
    src/App.css

docs/
  superpowers/specs/2026-04-27-best-in-class-review/   # full audit + rollup
  superpowers/specs/                  # any future design docs land here
```

## Documentation

- `docs/superpowers/specs/2026-04-27-best-in-class-review/00-review-plan.md` — the 50-category audit plan
- `docs/superpowers/specs/2026-04-27-best-in-class-review/99-rollup.md` — synthesized findings + sequenced remediation
- `docs/superpowers/specs/2026-04-27-best-in-class-review/0[1-20]-*.md` — per-agent reports with file:line evidence

## Contributing

See `CONTRIBUTING.md`.
