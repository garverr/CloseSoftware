# Best-in-Class Review — Agent 09: Flux Prior-Period Detection & AI Auditability

**Date:** 2026-04-27
**Categories:** 17 (Prior-period adjustment / reclass / accrual reversal detection), 18 (Auditability: AI run logs, citations, reproducibility, redaction)
**Pillar:** Flux + AI GL Investigation

---

## Category 17 — Prior-Period Adjustment / Reclass / Accrual Reversal Detection

Best-in-class tools (Trullion, Numeric) tag journal entries by economic source type — MANJOURNAL, ACCREC, ACCPAY, REVERSAL — then exclude or separately surface period-adjusting entries so flux explanations are never contaminated by an accrual reversal cancelling itself across months.

### Findings

| # | Finding | Severity | Evidence |
|---|---------|----------|----------|
| 17-1 | `SourceType` is stored on `XeroJournal` and surfaced in the drilldown query, but **no flux logic reads it to tag, exclude, or flag prior-period adjustments, accrual reversals, or manual journals**. The AI snapshot sent to Codex CLI does not include SourceType filters or warnings. | **Blocker** | `Domain/Entities.cs:379` (`SourceType` field); `XeroLedgerServices.cs:421` (stored); `XeroLedgerServices.cs:1897` (queried in drilldown but never used to modify flux group amounts or risk flags) |
| 17-2 | `BuildRiskFlags()` checks for "No prior-period balance" and ">50% swing" but has **no flag for entries whose `SourceType == "MANJOURNAL"` (manual journal) or whose description matches accrual/reversal patterns**. A $500k accrual posted and reversed in adjacent months will appear as two separate flux anomalies with no system-level linkage. | **Blocker** | `XeroLedgerServices.cs:1968–1997` |
| 17-3 | `XeroLedgerMonthlySummary` rolls up `NetAmount` by `AccountCode`+`MonthKey` with no separation by SourceType. Accrual reversals collapse into the monthly net, making them invisible to the flux engine. | **Major** | `Domain/Entities.cs:403–413` |
| 17-4 | `FluxReviewGroup` entity has no `PriorPeriodAdjustmentFlag`, `AccrualReversalAmount`, or `ReclassFlag` field. The `RiskFlagsJson` blob stores generic strings; no structured detection flag is persisted. | **Major** | `Domain/Entities.cs:585–631` |
| 17-5 | The AI snapshot (`BuildAiExplanationSnapshotAsync`) passes raw variance figures and transaction lists to Codex CLI, but the instructions tell the model to "explain the variance" without explicitly noting whether any contributing entries are manual journals or accrual reversals. | **Major** | `XeroLedgerServices.cs:1446–1463` |

---

## Category 18 — Auditability: AI Run Logs, Citations, Reproducibility, Redaction

Best-in-class tools (Trullion, Closecore) record every AI call with: exact input prompt, model ID + version at runtime, token counts, output, citation links to specific journal-line IDs, who approved/rejected, and a cryptographic hash of the input data enabling reproduction. PII (names, amounts in error messages) is redacted before logging.

### Findings

| # | Finding | Severity | Evidence |
|---|---------|----------|----------|
| 18-1 | `AiRun` stores `Model`, `ReasoningEffort`, `PromptProfile`, `InputJson`, `OutputJson`, `Logs`, `CreatedAt`, `StartedAt`, `CompletedAt`. **Token/prompt-token counts are absent** — there is no way to reproduce cost or verify the model processed the full context. | **Major** | `Domain/Entities.cs:701–718`; `AiRunDto` at `FinancialServices.cs:1189` |
| 18-2 | The **`InputJson` is stored in the `AuditRecord.AfterJson` column** (via `AuditAsync` at `Program.cs:866`), giving a one-time snapshot. However, `AuditRecord` does **not link to `AiRun.Id`** — there is no FK or cross-reference between the audit entry and the run row, breaking the chain of evidence a CFO or auditor would trace. | **Major** | `Program.cs:866`; `Domain/Entities.cs:779–791` (no `AiRunId` on `AuditRecord`) |
| 18-3 | Evidence in `PackageIssue.EvidenceJson` is a free-form JSON blob copied from the AI output. It **does not contain XeroJournalLine IDs or database GUIDs** — only account codes and free-text. An auditor cannot click from an AI finding to the source transaction. | **Major** | `FinancialServices.cs:1171`; `Domain/Entities.cs:269` |
| 18-4 | Redaction covers OAuth tokens (`access_token`, `refresh_token`, `client_secret`, `EncryptedAccessToken`, `EncryptedRefreshToken`, `PasswordHash`, `ConnectionString`, `.codex/auth.json`). **No PII redaction** (entity names, dollar amounts in exception messages, or employee names appearing in journal descriptions). | **Major** | `Program.cs:4778–4791`; `FinancialServices.cs:1114–1128` |
| 18-5 | Actor identity is a plain HTTP header `X-FR-User` with a hardcoded fallback of `"dev-admin"` — **no authentication is enforced**. Any caller can forge the actor identity recorded in `AuditRecord.Actor`. | **Blocker** | `Program.cs:3642–3643` |
| 18-6 | `AiRunDto` (the DTO returned to clients and broadcast over SignalR) **omits `InputJson` and `StartedAt`**. Clients see `OutputJson` and `Logs` but cannot retrieve the original prompt from the DTO — they must make a separate DB query (no API endpoint returns `InputJson` directly). | **Minor** | `FinancialServices.cs:1189–1192` |
| 18-7 | One-retry-on-invalid mechanism is implemented (`FinancialServices.cs:824–834`) and the retry instruction is logged, which is good. However, **the retry prompt is hardcoded** and does not feed back the specific schema violation — reducing the chance of a successful correction for complex errors. | **Minor** | `FinancialServices.cs:826–828` |

---

## Pillar Verdict — Flux + AI GL Investigation

**NOT audit-grade.** The Flux pillar has threshold logic, sign-off workflow, and actor-stamped audit records, but is missing two structural capabilities demanded by Trullion/Numeric benchmarks:

1. **Prior-period/reversal isolation** — no SourceType-aware filtering, no accrual reversal pairing, no MANJOURNAL flags. Flux will misattribute reversal noise as genuine variance.
2. **Reproducible, citation-linked AI audit trail** — `AiRun` logs prompt and output but lacks token counts, a direct FK to `AuditRecord`, and journal-line-level citations. Actor identity is unauthenticated.

---

## Top 3 Fixes (Priority Order)

1. **[Blocker] Tag and isolate SourceType in flux** (`XeroLedgerServices.cs` — `BuildRiskFlags`, `UpsertFluxGroup`, `BuildAiExplanationSnapshotAsync`): Filter or separately bucket `MANJOURNAL` / `ACCPAY_REVERSAL` entries in the monthly roll-up; add a `RiskFlagsJson` entry and a dedicated `FluxReviewGroup.ManualJournalAmount` / `AccrualReversalAmount` field so the AI snapshot explicitly labels which portion of the variance is reversal-driven.

2. **[Blocker] Authenticate actor identity** (`Program.cs:3642`): Replace the `X-FR-User` header trust with a verified claim (JWT `sub` or `name` from ASP.NET Core Identity/OIDC). A forged actor in `AuditRecord` invalidates the entire audit trail for compliance and SOC 2 purposes.

3. **[Major] Link AI evidence to journal-line IDs** (`FinancialServices.cs:1171`, `Domain/Entities.cs`): When building the AI snapshot, include `XeroJournalLine.Id` (database GUID) and `SourceLineId` alongside each transaction. Require the AI output schema to echo these IDs in `evidence[].journalLineIds`. Persist them in `PackageIssue.EvidenceJson` so auditors can traverse from AI finding → specific ledger row.
