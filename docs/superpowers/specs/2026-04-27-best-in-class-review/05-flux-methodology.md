# Best-in-Class Audit — Agent 05: Flux Methodology

**Categories:** 9 (Flux calc methodology), 10 (Materiality thresholds)
**Date:** 2026-04-27
**Reviewer:** Agent 05 of 20

---

## Category 9 — Flux Calc Methodology

### Findings

| Severity | Finding | Evidence | Best-in-class gap | Recommendation |
|----------|---------|----------|-------------------|----------------|
| **Blocker** | Only two comparison bases exist: MonthOverMonth (prior calendar month) and YearOverYear (same month prior year). No quarter-over-quarter, no vs-budget flux, no prior-quarter, no YTD. | `XeroLedgerServices.cs:1017-1018` — constants `MonthOverMonth` and `YearOverYear`; `line 1078-1079` — only two `UpsertFluxGroup` calls per statement group. | Closecore and Numeric compute flux against at least four bases simultaneously: prior month, prior quarter, prior year, and budget/forecast. The quarter-over-quarter view is critical for seasonally adjusted close reviews. | Add `PriorQuarter` and `VsBudget` flux types. Wire budget amounts from `ForecastScenario` into the flux engine so each FS-line group gets a third comparison column. |
| **Major** | Variance formula is raw `current − prior` with no sign-convention normalization for account type. Expense increases are presented as positive variances (unfavorable), which is the opposite of standard P&L presentation. | `FinancialServices.cs:23-28` — `Variance(current, prior)` returns `amount = current - prior`; no flip for expense/liability accounts. `XeroLedgerServices.cs:1498` — `FinancialMath.Variance(group.CurrentAmount, prior)` called without account-type context. | Closecore shows favorable/unfavorable tags and flips variance sign for expense accounts so reviewers see increases in cost as red/negative and decreases as green/positive, matching board-pack convention. | Pass account type (revenue/expense/asset/liability) into `RequiresInvestigation` and the DTO; flip sign for expense and liability groups; surface a `favorable` boolean field in `FluxReviewGroupDto`. |
| **Major** | No YTD (year-to-date) flux comparison is computed or surfaced. The feature list (`Program.cs:4672`) mentions "YTD" in package slide columns but there is no YTD variance in the flux review engine. | `Program.cs:4514-4516` — `ShowPriorMonth`, `ShowPriorYear`, `ShowBudget` settings exist for packages; `App.tsx:1469` — `showYtd` is a package settings toggle; no YTD flux group computed anywhere in `XeroLedgerServices.cs`. | Numeric and Closecore display cumulative YTD vs prior-YTD flux, which is essential for accrual-heavy businesses where single-month swings are noise. | Add a `YearToDate` flux type that accumulates FS-line amounts from period start to current month and computes variance against same prior-year YTD slice. |
| Minor | Running three-month balance is only tracked for MonthOverMonth flux and is stored but not surfaced as a separate comparison column with its own threshold gate. | `XeroLedgerServices.cs:1531` — `RunningThreeMonthAmount` set only when `fluxType == MonthOverMonth`; no threshold evaluation uses this value. | Closecore uses rolling 3-month average as a comparison basis to smooth one-off timing items. | Surface `RunningThreeMonthAmount` as a labeled comparison column in the drill-down and allow threshold triggers against the rolling average. |

---

## Category 10 — Materiality Thresholds

### Findings

| Severity | Finding | Evidence | Best-in-class gap | Recommendation |
|----------|---------|----------|-------------------|----------------|
| **Blocker** | Dollar threshold defaults to `0`, which means the dollar leg of the dual-threshold gate is **disabled by default** on every new group. Only the 10 % percent threshold fires unless a user manually configures a dollar amount per group. | `Entities.cs:602-603` — `DollarThreshold = 0m` (no default); `XeroLedgerServices.cs:1514` — new groups created with `DollarThreshold = 0m`; `line 1960` — `dollarHit` short-circuits when `DollarThreshold <= 0m`. | Closecore and Numeric ship with sensible default dollar floors (e.g. $5 k for OpEx lines, $25 k for balance-sheet lines) so large-balance accounts with tiny percentage swings are not missed, and small-balance accounts with large percentage swings are not over-flagged. | Set non-zero dollar defaults on group creation, differentiated by statement type (e.g. $5,000 for P&L, $10,000 for balance sheet) or pull from an org-level config table. |
| **Blocker** | No account-type-aware or entity-level global threshold configuration exists. Every threshold must be set manually, per group, per package. There is no way to set "revenue lines: 5 % / $10 k; expense lines: 10 % / $5 k" across an entity or across all packages. | `Entities.cs` — no `FluxMaterialityConfig`, `OrgFluxThreshold`, or `AccountTypeThreshold` entity exists. `XeroLedgerServices.cs:1504-1518` — new group hardcodes `DollarThreshold = 0m, PercentThreshold = 10m`. Settings can be propagated forward (`ApplyScope = "future"`, `line 1175-1185`) but only for the same GroupKey, not by account type. | Closecore lets controllers configure a global materiality matrix by account class (revenue, COGS, SG&A, asset, liability) and entity size. Numeric ties thresholds to a benchmark percentage of total revenue or net assets. | Add an `OrgFluxThresholdConfig` table with columns `(OrganizationId, StatementType, AccountClass, DollarThreshold, PercentThreshold, ThresholdLogic)`. Seed new flux groups from this config instead of hardcoded defaults. |
| **Major** | OR logic with a disabled dollar leg is equivalent to "% only" logic. The `AND` mode (`line 1961-1963`) is the professionally correct "both must exceed" dual-threshold gate, but it is not the default and the UI initializes with OR and $0. | `App.tsx:1820-1822` — UI defaults: `dollarThreshold: '0'`, `percentThreshold: '10'`, `thresholdLogic: 'OR'`. | Best-in-class flux engines default to AND logic with meaningful $ and % values so that both conditions must be breached, preventing noise from small absolute swings that happen to be large percentages. | Change default to AND with `DollarThreshold = $5,000` and `PercentThreshold = 10 %`. Expose the dual-threshold concept prominently in the onboarding flow and the settings panel. |
| Minor | No per-entity (multi-entity/consolidation) threshold scaling. A $5 k threshold appropriate for a $2 M revenue entity will generate hundreds of spurious alerts for a $50 M entity. | `Organization` entity (`Entities.cs:53-67`) has no revenue or size field that the flux engine reads. Threshold is set at group level only. | Pigment/Anaplan-style engines scale thresholds as a percentage of trailing twelve-month revenue or net assets per entity. | Add an optional `MaterialityBasisAmount` field to `Organization` (e.g. TTM revenue) and multiply the dollar threshold by a configurable materiality percentage (e.g. 0.25 %) when computing `RequiresInvestigation`. |

---

## Pillar Verdict — Flux

**Status: Below par for a Closecore/Numeric competitor.** The structural workflow — OR/AND dual-threshold gates, per-group settings, prior-explanation roll-forward, ledger drilldown, AI explain, CSV export, sign-off chain — is genuinely solid and competitive. However, the engine is missing two of the four standard comparison bases (prior quarter and vs-budget), sign conventions are not account-type-aware, and the materiality configuration is flat and entity-agnostic. These are the exact dimensions Closecore markets as differentiators.

---

## Top 3 Fixes (Ordered by Impact)

1. **Add vs-budget flux type** (`XeroLedgerServices.cs` — extend `RefreshAsync` to call `UpsertFluxGroup` with a `VsBudget` flux type using actual vs `ForecastScenario` budget amounts). This is the single biggest competitive gap against Closecore, whose tagline is "budget vs actuals flux at close."

2. **Introduce `OrgFluxThresholdConfig` table** with account-class defaults seeded on org creation. Apply these defaults when creating new `FluxReviewGroup` rows (`XeroLedgerServices.cs:1504-1518`). Switch the hardcoded defaults to AND logic with `$5,000` / `10 %`. This fixes both Blocker findings in Category 10 in one migration.

3. **Normalize variance sign by account type** in `FinancialMath.Variance` or in the FS-line grouping layer: flip the sign (or add a `isFavorable` field) for expense and liability groups so that cost increases surface as negative/red and cost decreases as positive/green, matching board-pack presentation standards used by Fathom, Syft, and Closecore.
