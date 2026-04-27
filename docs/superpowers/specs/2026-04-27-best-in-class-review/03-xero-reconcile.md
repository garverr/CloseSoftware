# 03 — Trial Balance vs GL-Derived Reconciliation Rigor

**Categories:** 5 (Trial balance vs GL-derived reconciliation rigor)
**Auditor:** Agent 03 of 20
**Date:** 2026-04-27

---

## What the App Does

The app runs two complementary reconciliation passes.

**Pass 1 — Backfill reconciliation** (`XeroBackfillServices.cs:824–907`): For each historical month, the backfill pipeline re-derives an account-level closing balance by taking the most recent `XeroTrialBalanceSnapshot` before the period (`SelectOpeningTrialBalance`) and rolling it forward with all imported `XeroJournalLine.NetAmount` values up to month-end. It compares the result against Xero's TB report snapshot at `0.01` tolerance (`line 880`). Results land in `XeroLedgerReconciliationRun` and are surfaced on the data-coverage grid.

**Pass 2 — On-demand / scheduled reconciliation** (`XeroLedgerServices.cs:204–282`): A manual or API-triggered run fetches a live TB from `GET /Reports/TrialBalance?date=…` , calls `BuildLedgerBalancesAsync` (all historical `XeroJournalLine` rows summed by `AccountCode` plus monthly summaries, `lines 636–661`), and diffs the two at the same `0.01` penny tolerance. Differences above `$1,000` create a `PackageIssue` at `IssueSeverity.High`; smaller gaps create `IssueSeverity.Medium`.

**Tie-out check** (`FinancialServices.cs:2751–2765`): At import time, `BuildTieOut` confirms that `Sum(TrialBalance.CurrentAmount) < $1` (i.e., the TB nets to zero) and records a `StatementQaResult` with status `Passed` or `Review`.

---

## Findings

### F1 — Blocker Issues

| # | Finding | Severity | File:Line |
|---|---------|----------|-----------|
| B1 | `DailyTrialBalanceHourUtc` is persisted and returned by the settings API but is **never consumed** by `XeroLedgerSyncWorker`. The worker loop calls only `RunIncrementalLedgerSyncAsync`; no daily TB reconciliation fires automatically. Drift between syncs goes undetected until a user or backfill job manually triggers it. | **Blocker** | `XeroLedgerServices.cs:980–1011`, `Entities.cs:342` |
| B2 | `BuildLedgerBalancesAsync` sums **all** `XeroJournalLine` rows from the beginning of time (`JournalDate <= snapshotDate`) without filtering voided journals. The `XeroJournal` and `XeroJournalLine` entities have no `IsVoided` / `Status` field (`Entities.cs:371–401`). Xero reverses voided entries with equal-and-opposite lines, so in many cases the math is net-correct, but voided AP invoices reprinted in the same period, or cross-period voids, can create silent phantom amounts that never exist in the Xero TB, producing false drift readings. | **Blocker** | `XeroLedgerServices.cs:636–661`, `Entities.cs:371–401` |

### F2 — Major Issues

| # | Finding | Severity | File:Line |
|---|---------|----------|-----------|
| M1 | Year-end close logic (`ApplyYearEndCloseToOpeningBalancesAsync`, `XeroBackfillServices.cs:1067–1118`) picks a single equity account by heuristic string match ("retained", "member") and posts the entire P&L close to it. Non-standard chart-of-accounts layouts (e.g., "Accumulated Surplus" for NFPs, or multi-currency retained earnings) may cause the P&L amount to zero out against the wrong account, shifting opening balances and producing spurious reconciliation differences for every subsequent period. | **Major** | `XeroBackfillServices.cs:1067–1118` |
| M2 | FX / multi-currency: the `XeroJournalLine` entity stores only `NetAmount` with no currency code or exchange-rate field. The TB snapshot is fetched with `paymentsOnly=false` in the base currency of the Xero org. For multi-currency tenants, the rolled-up ledger and the Xero TB both express amounts in home currency, but any FX translation differences booked as system journals in Xero would appear in the TB yet may not have a corresponding `XeroJournal` entry if those system journals are excluded by the journal-type filter. No FX-specific reconciliation or warning exists. | **Major** | `XeroLedgerServices.cs:213`, `Entities.cs:387–401` |
| M3 | When `openingTb is null` (i.e., no TB snapshot exists before the current period), the backfill reconciliation sets `Status = "Review"` and writes zero differences (`XeroBackfillServices.cs:892`). This is recorded as a non-pass but with `DifferenceAmount = 0`, making the coverage grid misleadingly show a reconciliation gap as "Review/$0" rather than "No Baseline". Users cannot distinguish between a genuine tie-out at $0 difference and a missing-baseline condition. | **Major** | `XeroBackfillServices.cs:885–906` |

---

## Pillar Verdict — Xero Reconciliation

🟡 **Partial** — The structural re-derive-from-journals logic exists and is correctly designed at `0.01` tolerance with `PackageIssue` escalation, but the daily automation is wired up in configuration only and never fires; voided journals and FX translation gaps create undetected drift vectors; and the missing-baseline state is invisible to users.

---

## Top 3 Fixes

1. **Wire `DailyTrialBalanceHourUtc` into `XeroLedgerSyncWorker`** (`XeroLedgerServices.cs:982–1011`): Inside the worker loop, after `RunIncrementalLedgerSyncAsync`, check whether `DateTime.UtcNow.Hour == settings.DailyTrialBalanceHourUtc` and whether a reconciliation has already been stored today (query `XeroLedgerReconciliationRuns` for `SnapshotDate == DateOnly.FromDateTime(DateTime.UtcNow)`). If not, call `RunTrialBalanceReconciliationAsync` for each tenant. This converts the setting from dead config into live automation.

2. **Add `IsVoided` and `SourceId` to `XeroJournal` / `XeroJournalLine`** (`Entities.cs:371`), populate them during `UpsertJournalsFromPayloadAsync`, and exclude voided journals from `BuildLedgerBalancesAsync` and the backfill roll-forward. For cross-period voids, record a reversal journal pair and confirm both sides cancel before including them in any open period.

3. **Distinguish "No Baseline" from "Passed at $0"** (`XeroBackfillServices.cs:892`): Introduce a distinct status value — e.g., `"NoBaseline"` — when `openingTb is null`, and surface it as amber in the data-coverage grid with a tooltip explaining that no opening TB snapshot exists before this period. This prevents the $0-difference false-positive masking a structural gap, consistent with how Closecore surfaces an explicit pre-close blocker for missing opening balances.
