# Best-in-Class Audit — Report 08: Counterparty/Vendor Pattern Detection & Recurring vs One-Time Classification

**Categories:** 14 (Counterparty / vendor pattern detection), 15 (Recurring vs one-time classification of transactions)

**Pillar:** Flux + AI GL Investigation

---

## Category 14: Counterparty / Vendor Pattern Detection

### What Best-in-Class Looks Like
Tools like Closecore and Numeric deterministically tag each journal line with a counterparty label before the AI sees it: `established_vendor`, `new_vendor`, `anomalous_vendor`, etc. These labels are computed from multi-period contact-frequency tables and fed as structured context into the AI prompt — the AI is never asked to invent them.

### What This App Does

| Signal | Present? | Evidence |
|---|---|---|
| Contact/vendor name stored on journal lines | **No** | `XeroJournalLine` has `Description`, `AccountCode`, `AccountName` — no `ContactName`/`ContactId` field (`Entities.cs:387-401`) |
| Contact name stored on `XeroJournal` | **No** | `XeroJournal` has `SourceType`, `Reference`, `PayloadJson` only (`Entities.cs:371-385`). Xero's `ContactName` is dropped at import time |
| Vendor frequency table / cross-period contact index | **No** | No table, service method, or query that counts how many prior periods a contact appeared in |
| "New vendor" / "established vendor" label computed deterministically | **No** | `GlAccount.IsFirstSeen` (`Entities.cs:562`) detects a **new GL account code** across periods (`FinancialServices.cs:341-343`, `XeroBackfillServices.cs:796`), not a new counterparty/contact |
| Anomaly score or outlier flag per vendor | **No** | No such computation anywhere in source |
| Vendor labels injected into AI prompt | **No** | `BuildAiExplanationSnapshotAsync` (`XeroLedgerServices.cs:1446-1463`) sends variance amounts, account-level deltas, and free-text descriptions only; no counterparty classification is included |

**Key gap:** `IsFirstSeen` on `GlAccount` is the closest approximation in the codebase, but it flags a *new account code* (chart-of-accounts novelty), not a new vendor/payee. The ledger transaction snapshot shown to the AI (`FluxLedgerTransactionDto`, `XeroLedgerServices.cs:1894-1902`) includes `Reference` and `Description` but no structured vendor identity. The AI explanation prompt instructs the model to "identify whether the movement is recurring or timing-related" (template at `XeroLedgerServices.cs:1550`) — this is asking the AI to invent the classification, not providing it deterministically.

### Verdict: MISSING — Blocker

---

## Category 15: Recurring vs One-Time Classification of Transactions

### What Best-in-Class Looks Like
Best tools compute cadence labels per account or per counterparty across 6–12 months of ledger history (e.g., "monthly, consistent ±5%", "one-time", "quarterly", "reversal"). These labels are attached to each flux group as structured metadata before the AI generates an explanation.

### What This App Does

| Signal | Present? | Evidence |
|---|---|---|
| Per-transaction recurring/one-time label | **No** | `GlTransaction` (`Entities.cs:573-583`) has no cadence or classification field |
| Cadence detection from historical ledger data | **No** | No service computes posting frequency, standard deviation, or regularity score |
| `IsRecurring` flag on transactions | **No** | `IsRecurring` exists only on `ForecastEvent` (forecasting module, `Entities.cs:212`) and as `IsRecurringRule` on elimination entries (`Entities.cs:671`) — both are user-manual flags, not computed from ledger history |
| `RecurringEliminationRule` used for transaction classification | **No** | These rules (`Entities.cs:675-688`, `Program.cs:1405-1442`) govern inter-company elimination entries, not GL transaction cadence labeling |
| Trend data fed to AI as cadence context | **Partial** | `TrendJson` (6-month P&L balance trend, `XeroLedgerServices.cs:1625-1666`) is stored on `FluxReviewGroup` and included in `EvidenceJson` (`XeroLedgerServices.cs:1566`). This gives raw balance trend, not a computed label |
| Recurring/one-time label in AI prompt | **No** | The explanation template (`XeroLedgerServices.cs:1550`) asks the user/AI to "identify whether the movement is recurring or timing-related" — this is the AI inferring from raw numbers, not being provided a deterministic label |
| Reversal / reclass detection | **No** | No logic detects equal-and-opposite journal pairs or MEMO source types as reversals |

**Key gap:** The 6-month `TrendJson` attached to each `FluxReviewGroup` is a useful building block, but no code converts that trend into a categorical label. The AI receives raw balance arrays and is expected to determine cadence itself — exactly the anti-pattern benchmarks avoid.

### Verdict: MISSING — Blocker

---

## Pillar Verdict: Flux + AI GL Investigation

The flux engine is competently built for **variance detection** (MoM and YoY thresholds, risk flags, 3-month running amounts, sign-off workflow). However, it is entirely missing the two counterparty/cadence layers that differentiate best-in-class tools:

1. No vendor/contact identity is captured or propagated from Xero into any stored entity or AI context.
2. No deterministic recurring/one-time/reversal label is computed from ledger history and fed to the AI; the AI is asked to infer these from raw numbers.

This means the AI explanation quality is limited by what the model can guess from account names, amounts, and free-text descriptions — with no structured signal to anchor it.

---

## Top 3 Fixes

### Fix 1 (Blocker): Capture and persist `ContactName` on `XeroJournalLine`

**Where:** `XeroJournalLine` (`Entities.cs:387`) and Xero import logic in `XeroBackfillServices.cs` and `XeroLedgerServices.cs`.

Add `public string ContactName { get; set; } = "";` to `XeroJournalLine`. At import time, extract `ContactName` from the Xero journal payload (already stored in `PayloadJson`). Then build a cross-period contact frequency table that counts how many months each `ContactName` appeared in prior periods. Attach labels (`established`, `new`, `anomalous`) to each transaction before the AI snapshot is built.

### Fix 2 (Blocker): Compute a deterministic cadence label per `FluxReviewGroup`

**Where:** `HydrateFluxReviewContextAsync` (`XeroLedgerServices.cs:1574`) and `UpsertFluxGroup` (`XeroLedgerServices.cs:1487`).

The 6-month `TrendJson` is already computed. Add a `CadenceLabel` field to `FluxReviewGroup` and compute it from the trend: if 5+ of 6 months are non-zero with coefficient of variation < 15%, label `Recurring`; if only 1 month is non-zero, label `OneTime`; if two consecutive months sum to ~zero, label `Reversal`; otherwise `Irregular`. Serialize this label into `EvidenceJson` so it is present when `BuildAiExplanationSnapshotAsync` runs.

### Fix 3 (Major): Inject vendor and cadence labels into the AI prompt as structured fields, not as free-text instructions

**Where:** `BuildAiExplanationSnapshotAsync` (`XeroLedgerServices.cs:1446-1463`).

Replace the open-ended instruction "identify whether the movement is recurring or timing-related" with pre-computed fields:

```json
{
  "cadenceLabel": "Recurring",
  "topVendors": [
    { "name": "Acme Corp", "status": "established", "priorMonthsActive": 11 },
    { "name": "New Co Ltd", "status": "new", "priorMonthsActive": 0 }
  ]
}
```

This converts the AI's job from open-ended inference to structured narration of pre-classified facts — the pattern used by Closecore and Numeric.
