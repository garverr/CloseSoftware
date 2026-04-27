# AI Flux Investigation: Prompt Context & Output Structure

**Categories:** 12, 13
**Reviewer:** Agent 07 of 20
**Date:** 2026-04-27

---

## Category 12 — AI Prompt Context for GL Investigation

The prompt is assembled in `BuildPrompt()` (FinancialServices.cs:956) and the snapshot in `BuildAiExplanationSnapshotAsync()` (XeroLedgerServices.cs:1446).

| Severity | Finding | Evidence | Best-in-class gap | Recommendation |
|----------|---------|----------|-------------------|----------------|
| **Minor** | Account name, code, type, current/prior amounts, $ and % variance are present in `FluxReviewAccountDto` passed via drilldown | XeroLedgerServices.cs:2273-2283; drilldown serialized at :1450-1462 | Numeric/Closecore include normal sign convention (debit-normal vs credit-normal) so the AI knows whether a positive move is favorable | Add `normalSign` field to `FluxReviewAccountDto` derived from `AccountType` |
| **Blocker** | No vendor/contact name on journal lines. `FluxLedgerTransactionDto` carries only `Reference`, `Description`, `SourceType`, and amounts. Xero journal lines have no `ContactId` or `ContactName` joined | XeroLedgerServices.cs:1894-1902; `ParsedJournalLine` at :977 has no contact field; `LoadLedgerTransactionsAsync` at :1881 does not join XeroContacts | Best-in-class tools include the payee name as a first-class field so the AI can identify "recurring vendor" patterns. Without it the AI is limited to raw memo strings | Join `XeroContacts` table (or Xero Contact embedded in journal payload) when constructing `FluxLedgerTransactionDto`; expose `ContactName` to the AI snapshot |
| **Major** | No journal-line ID is surfaced to the AI. `FluxLedgerTransactionDto` has no `JournalLineId` or `XeroJournalId` field | XeroLedgerServices.cs:2285-2293; `XeroJournalId` exists in DB (`XeroJournalLines`) but is not projected | Without a parseable ID, evidence citations cannot be machine-verified back to a source journal entry — CFO cannot click through | Project `XeroJournalId` (and `SourceLineId`) into `FluxLedgerTransactionDto`; reference in AI output `evidence[]` |
| **Minor** | Prior-period trend array is built (`BuildTrendJsonAsync`, XeroLedgerServices.cs:1625) and included in the group DTO, and is passed in the snapshot via `FluxReviewGroupDto.TrendJson` | XeroLedgerServices.cs:1566, 1452 | Trend is present but not explicitly labeled (no period-by-period keys) in the instructions list sent to the AI | Annotate the trend array with period labels in the instructions block so the AI is directed to reference it |
| **Minor** | No close-checklist or preparer-note context is injected into the snapshot | BuildAiExplanationSnapshotAsync at XeroLedgerServices.cs:1450-1462; `instructions[]` is generic | Closecore includes open close-checklist items for the account so the AI can cite a pending accrual, etc. | Add an optional `closeContext` field sourced from linked issues or checklist items if available |

---

## Category 13 — AI Output Structure: Hypothesis, Evidence, Confidence, Citations

The required output schema is specified in `BuildPrompt()` (FinancialServices.cs:958-963) and validated in `TryValidateAiJson()` (:981-1051). Retry logic is at :824-834.

| Severity | Finding | Evidence | Best-in-class gap | Recommendation |
|----------|---------|----------|-------------------|----------------|
| **Blocker** | No ranked hypotheses in the schema. The contract demands only `summary`, `suggestedExplanation`, `confidence`, `evidence[]`, `operations[]`. There is no `hypotheses[]` array or `ranked` field | FinancialServices.cs:959-963; `TryValidateAiJson` :998-1019 checks exactly these five keys | Numeric/Closecore return a ranked list of hypotheses (e.g., "Timing accrual 0.82, New recurring vendor 0.65, One-time item 0.41") each with supporting evidence. The app collapses this to a single prose string | Add `hypotheses[]` with `rank`, `label`, `confidence`, `journalLineIds[]` to the contract and validator |
| **Blocker** | `evidence[]` is schema-unvalidated. The validator only checks the top-level key exists and is an array (`JsonValueKind.Array`). It does not enforce that each element contains a journal-line reference | FinancialServices.cs:998-1019; no per-element check on `evidence` for flux-explain (contrast with `issues[]` check at :1030-1040 which is also shallow) | Best-in-class tools require `evidence[].journalLineId` (machine-parseable citation) so auditors can verify | Add per-element schema enforcement requiring at minimum a `journalLineId` or `accountCode` + `date` triple; validate in `TryValidateAiJson` |
| **Major** | Mock output sets `confidence: 0.72` (hardcoded) and provides group-level `evidence` only — no per-line citations | FinancialServices.cs:1076-1082 (`MockFluxExplanation`) | Mock shapes developer expectations; a mock without journal-line IDs normalises the gap | Fix mock to emit `journalLineIds` once the DTO carries them |
| **Major** | Retry instruction is generic: "Return only strict JSON matching the requested schema." It does not re-state the schema, making a second failure likely if the model misunderstood a field type | FinancialServices.cs:828 | Best-in-class retry includes the verbatim field schema in the retry prompt | Pass the full contract string into the retry instruction |
| **Minor** | Single retry only (one pass, then hard fail). No exponential backoff or structured parse-then-repair | FinancialServices.cs:824-834 | Acceptable for v1 but top tools do two retries with schema extraction hints | Low priority; single retry is an acceptable v1 pattern |
| **Minor** | `operations[].op` is constrained to `"set_flux_explanation"` via `allowedOperations` in the snapshot, which is good. But the validator does not enforce the op value or target fields | XeroLedgerServices.cs:1461; FinancialServices.cs:1012-1016 | Schema-validate each operation's `targetType` and `targetId` at validation time | Extend `TryValidateAiJson` to verify at least one operation has `op == "set_flux_explanation"` with a non-empty `targetId` |

---

## Pillar Verdict — Flux AI

**Rating: Functional but Shallow.** The pipeline is structurally sound: snapshot → Codex CLI → validated JSON → retry → apply. The drilldown correctly assembles account-level variance with current and prior journal lines, trend data, risk flags, and prior-period explanation. However, the AI sees no vendor/contact names (making recurring-vendor pattern detection impossible), journal lines carry no machine-parseable IDs (citations are unverifiable), and the output schema enforces a single prose explanation rather than ranked hypotheses with evidence. This puts the app well below Numeric/Closecore on AI-explainability and auditability.

---

## Top 3 Fixes

1. **[Blocker] Add journal-line IDs + contact names to `FluxLedgerTransactionDto`** (XeroLedgerServices.cs:1894). Project `XeroJournalId`, `SourceLineId`, and `ContactName` so the AI snapshot carries verifiable, payee-identified evidence. This unblocks both citation integrity and recurring-vendor detection.

2. **[Blocker] Replace single-string output with ranked `hypotheses[]` schema** (FinancialServices.cs:959). Extend the prompt contract and `TryValidateAiJson` to require `hypotheses[]{rank, label, confidence, journalLineIds[]}`. This is the single biggest gap versus Numeric/Closecore and is the feature CFOs and auditors will demand first.

3. **[Major] Enforce per-element `evidence[]` validation with required citation fields** (FinancialServices.cs:998-1019). The validator accepts any array as valid evidence. Add a check that each element carries a `journalLineId` (or `accountCode` + `date`), making AI citations machine-parseable and audit-ready.
