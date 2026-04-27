---
name: Best-in-class review — Rollup
description: Synthesis of 20-agent / 50-category audit benchmarked against Fathom, Closecore, Numeric, Tabs, FinOptimal, Trullion, Concourse, Pigment, Anaplan, Mosaic, Vena, Cube
type: project
date: 2026-04-27
---

# Best-in-Class Review — Final Rollup

**Scope:** 50 categories, 20 parallel agents, all read-only.
**Benchmarks:** Fathom (reporting), Closecore + Numeric + Tabs + FinOptimal + Trullion + Concourse (flux/AI), Pigment + Anaplan + Mosaic + Vena + Cube (FP&A).

---

## TL;DR

The product has **strong skeleton, weak organs.** Schema is comprehensive (packages, slides, blocks, versions, AI runs, audit records, eliminations, mappings, FX). The Xero backfill path, the flux workflow shell, the slide editor, and the audit-record coverage are all surprisingly thoughtful for a single-engineer codebase.

But the three pillars the user identified as core are **not yet best-in-class**:

| Pillar | Verdict | Headline |
| --- | --- | --- |
| **Xero importing** | 🔴 Far behind | Incremental sync silently drops journals (single-page fetch). Contact/vendor name is never captured (no Closecore-style vendor flux possible). Account `Type` is inferred from net-amount sign rather than read from Xero's COA. Closed periods are silently rewritten on re-sync. Token refresh has a concurrency race. |
| **Flux + AI GL investigation** | 🟡 Approaching | Workflow + threshold + sign-off scaffolding is real. But: only MoM and YoY (no vs-budget, no prior-quarter, no YTD). Materiality is flat and entity-agnostic. AI prompt has no vendor names, no journal-line IDs, no ranked hypotheses, no machine-parseable citations. Reversal/reclass detection absent. |
| **AI Board Package** | 🔴 Far behind | **The marquee differentiator does not exist.** No prior-package FK, no diff engine, no keep/modify/add/remove suggestions, no tiered board-vs-ops materiality, no CFO approval gate, no AI-provenance on accepted text. PDF is a 46-line ASCII text dump with no library, no charts, no branding. PPTX does not exist. |

Cross-cutting infrastructure is **production-unsafe**: Admin auth bypass is always on (no environment guard), DataProtection keys are unencrypted XML on disk, SQLite has no backup, no migrations strategy, no WAL mode. Test suite is 18 tests in 1 file. The `Unprotect` helper returns raw cleartext if decryption fails as long as the string starts with `"ey"` — a silent bypass of the entire encryption layer.

**Aggregate counts across all 20 agents: ~70 Blockers, ~133 Majors.**

---

## Top 10 — Most Critical Findings

Ranked by **(risk to data integrity or trust) × (impact on the three pillars)**.

| # | Severity | Finding | Cat | File:line |
|---|---|---|---|---|
| 1 | **Production-catastrophic** | Auth is `X-FR-Role` / `X-FR-User` headers with Admin fallback **active in every environment**. No `AddAuthentication`, no `AddAuthorization`, no JWT, no session. Any unauthenticated caller = Admin. Audit actor is forgeable — trail is not legally defensible. | 41, 44, 45 | `Program.cs:3632–3646` |
| 2 | **Marquee feature missing** | No baseline-diff engine. `ReportPackage.BaseFrom` is a display label, not a FK. `AiPackageDraftService` only emits "add context block" suggestions from current-month flux top-8 — last month's slides are never read or carried forward. The product's primary differentiator is unimplemented. | 19, 20 | `Program.cs:3401`, `XeroLedgerServices.cs:2056–2113` |
| 3 | **Data corruption risk** | Concurrent token refresh race — three `EnsureValidTokenAsync` implementations all read-then-refresh with no lock. Background ledger worker + manual sync = corrupted refresh tokens under concurrent load. Plus: `Unprotect` silently returns cleartext if decryption fails and the string starts with `"ey"`, bypassing DataProtection entirely. | 1 | `FinancialServices.cs:2887–2963`; `XeroLedgerServices.cs:609–801`; `XeroBackfillServices.cs:1126–1222` |
| 4 | **Silent data loss** | Incremental Xero journal sync fetches **one page** per cycle (Xero caps at 100). Any tenant with > 100 new journals between sync cycles silently loses journals — cursor advances and they're gone. The backfill path correctly paginates; the live path does not. | 4, 6 | `XeroLedgerServices.cs:560–586` |
| 5 | **Closed-period violation** | `ReportingPeriod.IsClosed` exists but is never checked before `ExecuteDeleteAsync` or `ProjectGlForPeriodAsync`. A re-sync silently wipes and rewrites financial statement lines for closed/locked periods. Audit-critical control absent. | 8, 31 | `XeroBackfillServices.cs:658–664`, `Entities.cs:77` |
| 6 | **Audit-grade flux gaps** | (a) No contact/vendor name on `XeroJournalLine` — vendor-level flux is impossible. (b) No `IsVoided` flag — voided journals contaminate roll-ups. (c) Account `Type`/`Class` is guessed from net-amount sign (`negative → Expense`) rather than read from Xero's `/Accounts` API. (d) No source-type filter for accrual reversals — every reversal is misread as variance. | 3, 4, 14, 15, 17 | `XeroBackfillServices.cs:790–791`; `XeroLedgerServices.cs:847–977` |
| 7 | **Board output is unusable** | PDF is hand-rolled raw PDF 1.4 bytes via `StringBuilder` — single page, 46 lines max, no charts, no branding, no font embedding, no page breaks. XLSX emits all amounts as inline strings (Excel cannot sum). PPTX does not exist. ThemeJson branding is stored but ignored on render. | 22, 24, 27 | `FinancialServices.cs:1384–1519` |
| 8 | **AI is shallow & unverifiable** | Prompt snapshot has no contact names, no journal-line IDs, no normal-sign convention, no pre-computed cadence labels — the AI is asked to invent recurring/one-time classifications from raw numbers. Output schema enforces a single `summary` string, not ranked hypotheses with per-line citations. Validator accepts any array as `evidence[]`. | 12, 13, 14, 15 | `FinancialServices.cs:956–1051`; `XeroLedgerServices.cs:1446–1463` |
| 9 | **Single point of total data loss** | Every byte of customer financial data is in one SQLite file. No backup, no WAL mode, no PITR, no Litestream, no documented RTO/RPO. DataProtection keys are unencrypted XML in `%LOCALAPPDATA%`; loss = all Xero OAuth tokens permanently broken. SQLite is ~260MB in dev. No production database plan exists. | 33, 47 | `appsettings.json:7`; `Program.cs:22–31` |
| 10 | **No CFO approval gate, no AI provenance** | `PackageStatus.Final` exists in the enum but no endpoint stamps `ApprovedBy`/`ApprovedAt` or locks the approved snapshot. `DistributionSchedule` can send a Draft. `SlideBlock` has no `IsAiAuthored`/`OriginatingAiRunId` — once accepted, AI-written prose is indistinguishable from human-written. | 25, 26 | `Entities.cs:79–129`, `Program.cs:599–636` |

---

## Heatmap — 50 Categories

| # | Category | Verdict | Pillar |
|---|---|---|---|
| 1 | OAuth lifecycle / refresh / revocation | 🔴 | Xero |
| 2 | Multi-tenant + V2 import | 🔴 | Xero |
| 3 | COA ingestion + first-seen | 🔴 | Xero |
| 4 | GL / journals sync | 🔴 | Xero |
| 5 | TB ↔ GL reconciliation | 🟡 | Xero |
| 6 | Incremental / idempotency | 🟡 | Xero |
| 7 | Rate-limit / retry | 🟡 (backfill ✅, live 🔴) | Xero |
| 8 | Backfill / period-locking | 🔴 | Xero |
| 9 | Flux calc methodology | 🔴 | Flux |
| 10 | Materiality thresholds | 🔴 | Flux |
| 11 | Drill-down + GL traversal | 🟡 | Flux |
| 12 | AI prompt context | 🟡 | Flux |
| 13 | AI output structure | 🔴 | Flux |
| 14 | Vendor pattern detection | 🔴 (absent) | Flux |
| 15 | Recurring vs one-time | 🔴 (absent) | Flux |
| 16 | Multi-period trend | 🔴 | Flux |
| 17 | Reclass / accrual reversal | 🔴 | Flux |
| 18 | AI auditability | 🔴 | Flux |
| 19 | Baseline diff engine | 🔴 (absent) | Pkg |
| 20 | Board materiality filter | 🔴 (absent) | Pkg |
| 21 | Narrative quality | 🔴 | Pkg |
| 22 | Chart auto-generation | 🔴 | Pkg |
| 23 | KPI selection | 🔴 | Pkg |
| 24 | Layout / branding | 🔴 | Pkg |
| 25 | Human-in-the-loop edits | 🔴 | Pkg |
| 26 | Versioning / approvals | 🔴 | Pkg |
| 27 | Export fidelity | 🔴 | Pkg |
| 28 | Re-gen / idempotency | 🔴 | Pkg |
| 29 | Backend code-org (5,118-line `Program.cs`) | 🔴 | Eng |
| 30 | Frontend code-org (4,723-line `App.tsx`) | 🔴 | Eng |
| 31 | Domain model (anemic, no period-lock) | 🔴 | Eng |
| 32 | EF schema / indexes | 🔴 | Eng |
| 33 | SQLite suitability | 🔴 | Eng |
| 34 | SignalR (broadcasts to all, no auth) | 🔴 | Eng |
| 35 | API contract / versioning / errors | 🔴 | Eng |
| 36 | Error handling / resilience | 🔴 | Eng |
| 37 | Codex job queue / cancellation | 🔴 | Eng |
| 38 | Caching | 🔴 | Eng |
| 39 | Frontend state mgmt | 🔴 | Eng |
| 40 | Test coverage (18 tests, 1 file) | 🔴 | Eng |
| 41 | Auth model | 🔴 (catastrophic) | Sec |
| 42 | Secret management | 🔴 | Sec |
| 43 | Encryption at rest | 🔴 | Sec |
| 44 | Audit trail completeness | 🟡 (rich coverage, fake actor) | Sec |
| 45 | Multi-tenancy isolation | 🔴 | Sec |
| 46 | Logging / observability | 🔴 | Sec |
| 47 | Backup / DR / PITR | 🔴 (absent) | Ops |
| 48 | Performance | 🔴 (N+1 GL ingest) | Ops |
| 49 | Accessibility (WCAG AA fails) | 🔴 | Ops |
| 50 | Documentation / onboarding | 🔴 | Ops |

**Tally:** 0 ✅ · 6 🟡 · 44 🔴.

---

## Sequenced Remediation Plan

The sequence matters. Phase 0 protects the data; Phase 1 makes the marquee feature real; Phase 2 makes the AI pipeline credible; Phase 3 lays the foundation for scale. **Don't skip Phase 0.**

### Phase 0 — Stop the bleed (~2–4 weeks)
The minimum to operate this on a real customer's financial data without losing it or being sued.

1. **Real authentication.** `AddAuthentication` + JWT (or OIDC), remove the Admin fallback, gate any dev bypass strictly on `IsDevelopment()`. Re-do every `Can()` site against `ClaimsPrincipal`. *(Cat 41)*
2. **Token refresh lock.** Singleton `ConcurrentDictionary<string, SemaphoreSlim>` keyed by `TenantId`, double-check expiry inside the lock. Apply to all three `EnsureValidTokenAsync` implementations. *(Cat 1)*
3. **Remove the `"ey"` plaintext fallback.** `CryptographicException` must surface as `NeedsReconnect`, never return the cleartext token. *(Cat 1)*
4. **Paginate incremental Xero sync.** `do { fetch; upsert } while (page.Count == 100)` inside `SyncTenantAsync`. Mirror the loop already in `XeroBackfillServices.ImportJournalsAsync`. *(Cat 4, 6)*
5. **Capture `ContactId`/`ContactName`, `IsVoided`, `CurrencyCode`, `CurrencyRate`, `XeroJournalLineId` on journal lines.** Read from the existing `PayloadJson` and persist. *(Cat 3, 4, 14)*
6. **Read `Type`/`Class`/`Status` from Xero's `/Accounts` API**; remove `GuessTypeFromAmount`. *(Cat 3)*
7. **Period-lock guard.** `ReportingPeriod.EnsureOpen()` called from every write touching period-scoped data. *(Cat 8, 31)*
8. **SQLite WAL + nightly `.backup` cron + DataProtection key backup.** Document RTO/RPO. *(Cat 47)*
9. **Encrypt the DataProtection key ring.** `ProtectKeysWith*` so keys are never plaintext on disk. *(Cat 42)*

### Phase 1 — Make the marquee feature real (~4–6 weeks)
The custom AI Board Package is the user's product. Today it doesn't actually exist.

10. **Baseline-diff engine.** Add `PriorPackageId` FK to `ReportPackage`. Build `PackageDiffService` that loads both snapshots, compares slides by `Subject`/`AccountCodesCsv`/metric value, emits typed `SlideDecision { keep | modify | add | remove }`. *(Cat 19)*
11. **Tiered materiality.** `BoardDollarThreshold` / `BoardPercentThreshold` distinct from ops thresholds. `AiPackageDraftService` filters by board threshold; below-threshold items are relegated to an appendix. Differentiated `Kind` values (`BoardMaterial`, `OpsDetail`, `FYI`). *(Cat 20)*
12. **Carry-forward + dedup guard.** Emit explicit `keep` decisions for unchanged slides (not silence). Hash-compare AI-generated blocks against existing slide blocks before append; skip if similarity above threshold. *(Cat 19)*
13. **Real PDF library.** Adopt QuestPDF (MIT). One page per `PackageSlide`, vector charts, embedded fonts, ThemeJson branding (logo/header/footer/cover-style), page numbers, landscape for wide tables. *(Cat 27)*
14. **Real XLSX (ClosedXML/EPPlus) + first-cut PPTX.** Numeric cells with formats, freeze panes, totals rows. PPTX is the format boards actually use. *(Cat 27)*
15. **Chart-type routing.** Switch on `componentVariant` so `monthly-trend`, `rolling-12`, `year-over-year`, `waterfall-bridge`, `budget-projection`, `scenario-chart` all render distinctly. Add a true waterfall using `varianceAmount` data already on the DTO. *(Cat 22)*
16. **CFO approval gate.** `POST /api/packages/{id}/approve` stamps `ApprovedBy`/`ApprovedAt`, creates a `PackageVersion` tagged as the approved snapshot, sets `PackageStatus.Final`. Block `DistributionSchedule` send unless `IsApproved`. *(Cat 26)*
17. **AI provenance.** `IsAiAuthored`, `OriginatingAiRunId`, `AiAuthoredAt` on `SlideBlock`. Set in `ApplyOperationAsync`. Surface in the editor tooltip. *(Cat 25)*
18. **Working `narrative-rewrite` prompt contract.** Branch in `BuildPrompt` that returns `{ narrative: string }` prose with current-period deltas + tone rules — instead of falling through to the QA-issues schema. Wire `CommentaryTone` into the prompt body. *(Cat 21)*

### Phase 2 — Make Flux + AI best-in-class (~3–4 weeks)
Catch up to Closecore / Numeric.

19. **Add comparison bases.** `PriorQuarter`, `YearToDate`, `VsBudget` flux types (the latter pulls from `ForecastScenario`). *(Cat 9)*
20. **Materiality matrix.** `OrgFluxThresholdConfig` table keyed by `(OrganizationId, StatementType, AccountClass)`. Default to AND logic with $5k / 10%. Sign-normalize variance for expense/liability accounts. *(Cat 10)*
21. **Vendor frequency labels (deterministic).** Compute `established` / `new` / `anomalous` per `ContactName` from cross-period frequency. Inject as structured fields into the AI snapshot. *(Cat 14)*
22. **Cadence labels (deterministic).** `Recurring` / `OneTime` / `Reversal` / `Irregular` computed from existing 6-month `TrendJson` per `FluxReviewGroup`. *(Cat 15)*
23. **Ranked hypotheses + evidence schema.** AI output: `hypotheses[] { rank, label, confidence, journalLineIds[] }`. Validator enforces `evidence[].journalLineId`. Mock output mirrors the new schema. *(Cat 13)*
24. **SourceType-aware variance.** Filter or separately bucket `MANJOURNAL` and reversal pairs in roll-ups; add `ManualJournalAmount` / `AccrualReversalAmount` to `FluxReviewGroup`. *(Cat 17)*
25. **Drilldown polish.** Wire the existing `Sparkline` component into the trend strip. 3/6/12-month window selector. Xero deep-link (`go.xero.com/.../{SourceID}`) on every GL row. Remove the 50-row client cap. Description filter. *(Cat 11, 16)*
26. **Audit-grade AI run records.** Token counts, `AiRun.Id` FK on `AuditRecord`, JWT `sub` for actor identity, journal-line citations in `PackageIssue.EvidenceJson`. *(Cat 18)*

### Phase 3 — Engineering & ops foundation (~6–8 weeks)
Lay the platform that the next 24 months of features will run on.

27. **Decompose `Program.cs`.** Feature folders (`Features/Xero`, `/Flux`, `/Packages`, `/Ai`, `/Mapping`, `/Reporting`, `/Planning`, `/Auth`) with `*Endpoints.cs` and `ServiceCollectionExtensions.cs` per feature. Target: `Program.cs` < 80 lines. *(Cat 29)*
28. **Decompose `App.tsx`.** `react-router-dom` + `pages/` + `features/` + custom hooks + `@tanstack/react-query`. Migrate three highest-churn views first (Flux, Mapping, Dashboard). *(Cat 30, 39)*
29. **Production database.** Postgres or SQL Server with `dotnet ef migrations`. Drop `EnsureCreatedAsync` + `SqliteSchemaPatch` runtime DDL. *(Cat 33, 32)*
30. **EF global query filters.** `HasQueryFilter` on every entity with `OrganizationId` / `TenantId`, sourced from the JWT claim via `IClaimsPrincipalAccessor`. *(Cat 45)*
31. **Composite indexes.** `(GlAccountId, TransactionDate)`, `(XeroJournal.JournalDate, TenantId)`, `AiRun(Status)`. *(Cat 32)*
32. **Global exception handler + RFC 7807.** `AddProblemDetails`, `UseExceptionHandler`, replace every `new { error = "..." }`. Add `Asp.Versioning.Http`, prefix routes with `/api/v1/`. *(Cat 35, 36)*
33. **Caching.** `AddOutputCache` on read-heavy endpoints with invalidation hooks on Xero sync completion. *(Cat 38)*
34. **OpenTelemetry + Serilog JSON sink.** Correlation ID propagation; enrich every log scope with `OrganizationId` and `TenantId`. Add correlation ID column on `AuditRecord`. *(Cat 46)*
35. **Codex worker hardening.** Reset `Status = Running` to `Queued` on startup. Hold the live `Process` reference; `process.Kill(entireProcessTree: true)` on cancel/timeout. Per-run SignalR groups + `[Authorize]` on the hub. *(Cat 37, 34)*
36. **Numeric regression test suite.** Parameterized tests for variance sign, MoM/YoY/QoQ, consolidation rounding, FX. WebApplicationFactory tests for every endpoint. Frontend: Vitest + Playwright smoke. *(Cat 40)*
37. **WCAG AA baseline.** Global `:focus-visible`, fix `--muted` contrast (`#5a5a60`), `aria-label` on every icon-only button, `role="navigation"` / `role="dialog"`, segmented controls as `role="radiogroup"`. *(Cat 49)*
38. **Onboarding docs.** Xero OAuth app registration walkthrough, Codex CLI install + auth, DataProtection key backup, dev-loop start guide, end-user "your first board package" tour. *(Cat 50)*

---

## Per-Pillar Verdict

### Xero Importing — 🔴 Far behind
Backfill engineering is genuinely strong (proactive rate budgets, pause/resume, reconciliation snapshots). The **live incremental path is the broken twin**: bypasses the rate scheduler, fetches one page per cycle, no retry on 401, no concurrency lock. Compounding that, the data captured per journal line is incomplete for the AI use cases the product is sold on (no contact name, no void flag, no currency, no journal-line ID). Fix the incremental path's parity with backfill, then capture the missing fields, and the foundation becomes solid.

### Flux + AI GL Investigation — 🟡 Approaching
The shape is right: dual-threshold gating, sign-off chain, AI explain queue, GL drilldown, prior-period explanation roll-forward. The gaps are specific and addressable: only two comparison bases (need vs-budget, prior-quarter, YTD), no entity-aware materiality, no deterministic vendor/cadence labels (the AI is asked to invent them), no ranked hypotheses, no journal-line citations. Phase 2 closes most of them.

### AI Board Package — 🔴 Far behind
**This is where the gap to "best-in-class" is widest.** The marquee feature — prior-month-as-baseline with material-only slide CRUD — is not implemented in any form. The PDF is a 46-line ASCII text dump. ThemeJson branding is stored but ignored. There is no CFO approval gate. Every AI suggestion is "add a context block at top-8 by abs variance" with no awareness of last month. Phase 1 is the path back.

---

## Per-Agent Index

| # | Agent | Cats | Report |
|---|---|---|---|
| 01 | Xero-Auth | 1, 2 | [01-xero-auth.md](01-xero-auth.md) |
| 02 | Xero-COA-GL | 3, 4 | [02-xero-coa-gl.md](02-xero-coa-gl.md) |
| 03 | Xero-Reconcile | 5 | [03-xero-reconcile.md](03-xero-reconcile.md) |
| 04 | Xero-Resilience | 6, 7, 8 | [04-xero-resilience.md](04-xero-resilience.md) |
| 05 | Flux-Methodology | 9, 10 | [05-flux-methodology.md](05-flux-methodology.md) |
| 06 | Flux-Drilldown | 11, 16 | [06-flux-drilldown.md](06-flux-drilldown.md) |
| 07 | Flux-AI-Prompts | 12, 13 | [07-flux-ai-prompts.md](07-flux-ai-prompts.md) |
| 08 | Flux-Patterns | 14, 15 | [08-flux-patterns.md](08-flux-patterns.md) |
| 09 | Flux-Audit | 17, 18 | [09-flux-audit.md](09-flux-audit.md) |
| 10 | Pkg-Diff | 19, 20 | [10-pkg-diff.md](10-pkg-diff.md) |
| 11 | Pkg-Narrative | 21, 23 | [11-pkg-narrative.md](11-pkg-narrative.md) |
| 12 | Pkg-Visuals | 22, 24 | [12-pkg-visuals.md](12-pkg-visuals.md) |
| 13 | Pkg-Workflow | 25, 26 | [13-pkg-workflow.md](13-pkg-workflow.md) |
| 14 | Pkg-Export | 27, 28 | [14-pkg-export.md](14-pkg-export.md) |
| 15 | Eng-CodeOrg | 29, 30, 31 | [15-eng-codeorg.md](15-eng-codeorg.md) |
| 16 | Eng-Data | 32, 33, 34, 37 | [16-eng-data.md](16-eng-data.md) |
| 17 | Eng-API-Test | 35, 36, 38, 39, 40 | [17-eng-api-test.md](17-eng-api-test.md) |
| 18 | Sec-AuthN | 41, 42, 43 | [18-sec-authn.md](18-sec-authn.md) |
| 19 | Sec-Audit | 44, 45, 46 | [19-sec-audit.md](19-sec-audit.md) |
| 20 | Ops-Quality | 47, 48, 49, 50 | [20-ops-quality.md](20-ops-quality.md) |

---

## Closing Note

This is a high-ambition product built mostly by one engineer in a small number of weeks. The bones are good. The two lowest-cost, highest-leverage moves right now are: **(1) close the auth bypass before any production data ever exists**, and **(2) build the prior-package diff engine so the marquee Board Package feature actually works**. Phase 0 + the first half of Phase 1 unblocks honest demos to a CFO without misrepresenting what the product does.
