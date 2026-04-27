# Agent 20 — Ops & Quality Review

**Categories:** 47 (Backup/DR/PITR), 48 (Performance Benchmarks), 49 (Accessibility/Responsive), 50 (Documentation/Onboarding)
**Reviewed:** 2026-04-27
**Source files:** `README.md`, `FRONTEND_REVIEW.md`, `src/FinancialReporting.Web/README.md`, `Program.cs`, `AppDbContext.cs`, `App.tsx` (5,423 lines), `App.css` (3,081 lines), `XeroLedgerServices.cs`, `XeroBackfillServices.cs`, `Domain/Entities.cs`, `scripts/frontend-smoke.mjs`, `appsettings.json`, `appsettings.Development.json`

---

## Category 47 — Backup / DR / Point-in-Time Recovery

| Finding | Severity | Evidence |
|---|---|---|
| No backup mechanism of any kind | **Blocker** | No Litestream config, no `.backup` command, no cron script, no hosted service — zero file evidence across entire repo |
| SQLite WAL mode not configured | **Major** | `AppDbContext.cs` has no `PRAGMA journal_mode=WAL`; default rollback journal gives no hot-backup capability |
| `EnsureCreatedAsync` instead of migrations | **Major** | `Program.cs:85-92` — `UseMigrations=false` by default; schema changes risk silent data loss on restart with incompatible db file |
| DataProtection keys stored in OS local-app-data with no backup doc | **Major** | `Program.cs:22-31` — keys land in `%LOCALAPPDATA%/FinanceApp/DataProtection-Keys`; loss = all Xero OAuth tokens permanently broken, tenants must reconnect; README only says "use same key ring" with no backup instruction |
| Version restore is application-level only | **Major** | `Program.cs:599-636` — `PackageVersion.SnapshotJson` is whole-package JSON in SQLite; if the SQLite file is lost or corrupted there are zero recovery options |
| No tenant-level "undo delete" for packages | **Major** | `Program.cs:739` has `MapDelete("/api/blocks/{blockId}")` as hard delete; no soft-delete (`IsDeleted`, `DeletedAt`) on any entity in `Domain/Entities.cs` |
| No documented RTO/RPO | **Blocker** | README.md has no DR section; no runbook, no recovery procedure |

**Summary:** The entire persistence layer is a single SQLite file with zero backup, zero PITR, and no WAL mode. This is a single point of total data loss. DataProtection keys are ephemeral OS files that would break all Xero OAuth on machine failure.

---

## Category 48 — Performance Benchmarks

| Finding | Severity | Evidence |
|---|---|---|
| N+1 queries in `/api/benchmarking` | **Major** | `Program.cs:3033-3059` — `foreach (var organization in organizations)` issues 3 separate `await db.*` calls per org (package lookup, `BuildBenchmarkRollupAsync`, KPI fetch); for 10 orgs = 30+ round-trips |
| N+1 in `UpsertJournalsFromPayloadAsync` | **Major** | `XeroLedgerServices.cs:405-460` — `foreach (var source in journals)` calls `db.XeroJournals.FirstOrDefaultAsync` per journal; 5-year GL backfill = tens of thousands of individual SELECT queries before bulk INSERT |
| N+1 in AI settings update | **Minor** | `Program.cs:813-837` — `foreach (var request in requests)` calls `db.AiRuntimeSettings.FirstOrDefaultAsync` per module |
| No benchmarks or perf budgets documented | **Blocker** | Zero benchmark files, no BenchmarkDotNet project, no load test scripts, no perf budget in README or CI |
| MonthlyBalancesJson is a JSON blob, not indexed | **Major** | `Domain/Entities.cs:565` — `GlAccount.MonthlyBalancesJson` is a serialized decimal array; every financial calculation reads and deserializes all rows in memory with no columnar index |
| No pagination on `/api/mapping/accounts` | **Major** | `Program.cs` issues unbounded `ToListAsync` on `GlAccounts`; large entity with 2,000+ accounts returns full dataset each request |
| `SaveChangesAsync` called inside journal upsert loop | **Major** | `XeroLedgerServices.cs:460` — single `SaveChangesAsync` after a full payload, but each payload is fetched and processed page-by-page; a 5-year backfill with thousands of journal pages means thousands of `SaveChangesAsync` calls, each flushing EF change tracker |
| No SQLite connection string options for performance | **Major** | `appsettings.json:7` — `"Data Source=financial-reporting-dev.db"` has no `Cache=Shared`, no `Mode=ReadWrite`, no WAL pragma; single-writer default will serialize all API requests |

**Summary:** The GL ingest path is a serial-per-row N+1 loop. A 5-year multi-tenant backfill could take hours and locks the SQLite file during `SaveChangesAsync`. No benchmarks exist to measure or detect regression.

---

## Category 49 — Accessibility (WCAG) & Responsive Design

| Finding | Severity | Evidence |
|---|---|---|
| Only 7 `aria-*` attributes across 5,423 lines | **Blocker** | `grep -n "aria-"` → 7 hits total: `aria-label` on context-switcher (1067), tablist (2542), width pills (2708), sparkline (4807); `aria-live` on AI popover (4900); `aria-hidden` on progress track (4918); `aria-label` on dismiss button (4911) |
| No `:focus-visible` rules | **Blocker** | `App.css` — only `.context-field select:focus` at line 181; zero `:focus-visible` rules; tab navigation is invisible across all 17 views |
| Sidebar has no `<nav>` landmark role | **Major** | `App.tsx:1145` — `<aside className="sidebar">` with no `role="navigation"`; screen reader cannot jump to navigation |
| Side panel (`<aside className="side-panel">`) not declared as dialog | **Major** | `App.tsx:4719` — modal-like panel has no `role="dialog"`, no `aria-modal`, no Escape-to-close handler |
| Segmented controls missing ARIA radio pattern | **Major** | `FRONTEND_REVIEW.md` confirms segmented controls (App.tsx:1582-1587) have `.active` class but no `role="radiogroup"`, `role="radio"`, or `aria-checked` |
| `--muted` colour fails WCAG AA | **Major** | `FRONTEND_REVIEW.md:View 10` — `#6b6b70` on `#f7f6f4` ≈ 3.8:1; AA requires 4.5:1; used widely on small financial text including confidence scores (App.tsx:1409) |
| Only 2 responsive breakpoints (1120px, 780px) | **Major** | `App.css:2832,2906` — two `@media` rules only; below 1024px the three-column shell degrades without graceful collapse; no mobile-first design |
| Icon-only buttons use `title` not `aria-label` | **Major** | `FRONTEND_REVIEW.md:View 14` — e.g. App.tsx:1021; `title` is tooltip, not accessible name |
| No `prefers-reduced-motion` media query | **Minor** | `App.css` — confirmed absent; transitions (lines 370, 1132, 2003) fire unconditionally |
| No keyboard shortcuts for mapping inbox | **Major** | `FRONTEND_REVIEW.md:View 18` — 5 clicks per account, no j/k/A/S/R keyboard navigation |
| Variance conveyed by colour alone in some views | **Minor** | `FRONTEND_REVIEW.md:View 10` — missing icon/glyph at App.tsx:2082 |

**Summary:** WCAG AA is not met. Keyboard navigation is broken by missing `:focus-visible`. Screen reader support is minimal. The app is effectively inaccessible to keyboard-only and AT users.

---

## Category 50 — Documentation & Onboarding

| Finding | Severity | Evidence |
|---|---|---|
| Developer setup guide is incomplete | **Blocker** | `README.md` — 51 lines total; covers `dotnet run` and `npm run dev`; no Xero OAuth app registration steps, no Codex CLI install/auth instructions, no DB seed instructions, no `.env` / secrets setup |
| No end-user documentation | **Blocker** | No user guide, no help pages, no in-app tooltip explanations for financial concepts (flux, eliminations, intercompany) anywhere in repo |
| No first-run onboarding tour | **Major** | `App.tsx:1211-1236` — `PackagePlaceholder` shows one "Create package" button and a single `<p>` sentence; no step-by-step guide connecting Xero → sync → mapping → first package |
| `src/FinancialReporting.Web/README.md` is Vite boilerplate | **Major** | File is the unmodified Vite scaffold template; zero project-specific content |
| No CONTRIBUTING.md, no architecture doc | **Major** | `docs/` contains only superpowers review files; no architecture overview, no data model diagram, no ADR (Architecture Decision Records) |
| Xero OAuth app setup not documented | **Blocker** | `appsettings.json:18` has a real `ClientId` hardcoded; README mentions "use same Xero OAuth app settings as Finance App V2" without explaining how to create or configure one |
| Codex CLI setup not documented | **Major** | README says "Run the service under the OS account that is logged into Codex CLI" — no link to Codex install, no auth steps |
| No changelog or version history doc | **Minor** | Commits are the only history; no CHANGELOG.md |

**Summary:** The developer setup requires tribal knowledge (Xero app, Codex CLI, DataProtection key ring, DB seed). An engineer joining cold cannot run the app end-to-end from the README alone. There is no end-user documentation at all.

---

## Cross-Cutting Verdict

The four categories form two clusters of risk:

**Operational risk (47, 48):** The app stores all data in a single SQLite file with no backup, no WAL mode, no PITR, and no DR plan. The GL ingest is a serial N+1 loop that will degrade severely at production scale (5-year multi-tenant backfill). These are pre-production blockers — not polish items.

**Quality & adoption risk (49, 50):** The product is effectively keyboard-inaccessible and fails WCAG AA contrast in core financial text. Documentation is insufficient to onboard a second developer or a first-time user. These compound the engineering debt already documented in other lenses.

| Category | Blockers | Majors |
|---|---|---|
| 47 Backup/DR | 2 | 5 |
| 48 Performance | 1 | 7 |
| 49 Accessibility | 2 | 8 |
| 50 Documentation | 3 | 5 |
| **Total** | **8** | **25** |

---

## Top 3 Fixes (Ordered by Risk × Effort)

**Fix 1 — SQLite WAL mode + daily `.backup` cron (Blocker, ~1 day)**
Enable `PRAGMA journal_mode=WAL` at startup and add a nightly `sqlite3 financial-reporting-dev.db ".backup backup/financial-reporting-$(date +%Y%m%d).db"` cron or Litestream sidecar. Document the DataProtection keys path backup alongside. This is the single highest-risk gap — total data loss with zero recovery path.

**Fix 2 — Batch GL upsert + bulk insert (Blocker, ~2 days)**
Replace the per-journal `FirstOrDefaultAsync` in `XeroLedgerServices.cs:405-460` with a pre-fetch of existing `XeroJournalId` values for the batch, then use `AddRangeAsync` for new journals and explicit update for changed ones. Add `PRAGMA journal_mode=WAL` and `CommandTimeout` to the connection. This converts an O(N) serial query loop into O(1) batch operations, making 5-year backfill feasible.

**Fix 3 — `:focus-visible` global rules + `--muted` contrast fix + `aria-label` on icon buttons (Blocker, ~0.5 day)**
Add a 20-line `:focus-visible` block to `App.css`, change `--muted` from `#6b6b70` to `#5a5a60` (passes 4.5:1 on both backgrounds), and add `aria-label` to every icon-only button. These three changes move the app from failing WCAG AA to a defensible AA baseline and unblock keyboard navigation for all users.
