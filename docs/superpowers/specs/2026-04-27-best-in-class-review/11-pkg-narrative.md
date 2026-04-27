# AI Board Package Audit — Categories 21 & 23

**Categories:** 21 (Narrative quality & executive tone), 23 (KPI selection logic & relevance to current period)

**Auditor:** Agent 11 of 20
**Date:** 2026-04-27
**Pillar:** AI Board Package

---

## Category 21 — Narrative Quality & Executive Tone

### Findings

| # | Finding | Severity | Evidence |
|---|---------|----------|----------|
| 21-A | `BuildPackageNarrative` produces a mechanical template sentence with no CFO-grade synthesis. It follows a rigid pattern — `"{line} was {$} for {period} versus {$} in {prior}, {up/down} {$} ({%}%). Primary linked accounts: {account list}."` — with no business context, no decisions-needed language, no thematic framing. | **Blocker** | `FinancialServices.cs:2532–2538` |
| 21-B | `BuildPrompt` passes `promptProfile` as a raw string label ("Direct CFO narrative") with no structural instruction beyond a short contract block. There is no prompt text specifying: avoid passive filler, cite exact dollar figures, state decisions required, limit to ≤3 sentences per driver. The tone label is advisory only. | **Major** | `FinancialServices.cs:956–978` |
| 21-C | The `narrative-rewrite` module is declared as a registered AI_SETTING module (`App.tsx:750`) but there is no corresponding `BuildPrompt` contract branch for it (only `flux-explain` gets a unique contract at `FinancialServices.cs:958–963`). All other modules — including `narrative-rewrite` and `slide-chat` — fall through to a generic QA issue schema (`summary, issues[]`) that cannot produce prose. | **Blocker** | `FinancialServices.cs:958–967`; `App.tsx:750` |
| 21-D | The mock Codex output (`FinancialServices.cs:866–894`) returns a mapping issue, not prose commentary. If `Ai:UseMockRunner=true` (the **default** at line 810), every narrative-rewrite call returns a mapping stub. CFOs testing the system see no narrative output at all. | **Major** | `FinancialServices.cs:810`, `866–894` |
| 21-E | `CommentaryTone` is stored in theme JSON and surfaced as a dropdown with two options: "Direct CFO narrative" and "Board concise" (`App.tsx:1559–1560`). Neither option is ever injected into the Codex prompt body; only `module` and `promptProfile` labels travel to `BuildPrompt`. The tone setting has zero effect on AI output. | **Major** | `Program.cs:4599`, `4634`; `FinancialServices.cs:974` |
| 21-F | The content library describes "CFO-ready current month story, key movements, decisions needed, and next steps" (`Program.cs:4667`) but no upstream data (current-period highlights, variance ranking by materiality, open action items) is fed into the executive summary slide's narrative block as structured context. The slide receives only raw account codes and monthly arrays. | **Major** | `Program.cs:4667`; `FinancialServices.cs:601–615` |

### Fathom Gap

Fathom's narrative templates pre-inject period-specific KPI deltas, prior-year comparisons, and user-edited "story sentences" directly into the draft paragraph. The system here has no equivalent: seed narrative is a template string, the AI call has no access to the current-period structured delta context beyond the raw package snapshot JSON.

---

## Category 23 — KPI Selection Logic & Relevance to Current Period

### Findings

| # | Finding | Severity | Evidence |
|---|---------|----------|----------|
| 23-A | `KpiDefinition` has no `ReportingPeriodId` field. All KPIs are org-scoped only (`OrganizationId`). A KPI defined in January is identical to the same KPI in March; there is no per-period snapshot, no historical series on the entity, and no way for the system to select "which KPIs are relevant to *this* period's story." | **Blocker** | `Domain/Entities.cs:131–143` |
| 23-B | The KPI scorecard query sorts by `IsPinned DESC, Name ASC` (`Program.cs:2492`). Pinning is a manual static boolean — no algorithm considers variance magnitude, breach of target, or whether the KPI moved materially in the current period. A KPI breaching target by 40% and one stable for six months appear identically unless the user manually re-pins. | **Major** | `Program.cs:2492`; `Domain/Entities.cs:141` |
| 23-C | The package snapshot sent to Codex for final-review includes `kpis` as a flat list (`FinancialServices.cs:631`), but does not include period-over-period delta, trend direction, or status change (good→bad). Codex cannot infer which KPIs are "telling the story" of this period without computed delta signals. | **Major** | `FinancialServices.cs:582–632` |
| 23-D | `NonFinancialMetric` is period-scoped (`ReportingPeriodId` at `Entities.cs:165`) but `KpiDefinition` is not. The two metric types are treated inconsistently: non-financial metrics can be pulled "for this period" but financial KPIs cannot. The KPI library screen (`App.tsx:3774–3777`) fetches all org KPIs and all period metrics in the same call, masking the asymmetry. | **Major** | `Domain/Entities.cs:131–143`, `161–177`; `App.tsx:3774–3777` |
| 23-E | There is no de-duplication check between KPI scorecard values and statement financials. Revenue and EBITDA are computable from the `StatementLine` data and also live as manually-entered `KpiDefinition` rows. The board sees both without any reconciliation warning. | **Minor** | `Program.cs:2484–2493`, `4685–4686` |

### Fathom Gap

Fathom's KPI library is period-sensitive: each KPI card shows the current-period value, prior-period delta, and a sparkline computed from the live data source. KPI visibility in the narrative is ranked by materiality signal, not by a static pin flag.

---

## Pillar Verdict — AI Board Package

The board package infrastructure (slides, blocks, PDF export, template library) is architecturally solid. However, the two most board-visible capabilities — AI-generated narrative and KPI selection — are not functional in their current state. Narrative output defaults to a one-sentence formula string or a mock mapping issue; the `narrative-rewrite` module has no working prompt contract. KPIs are static, org-scoped, and ranked by a manual pin with no period context or materiality signal.

**Overall rating: Not board-ready.** The package can deliver formatted financials and flux evidence, but cannot produce CFO-grade prose or a context-sensitive KPI scorecard automatically.

---

## Top 3 Fixes

1. **[Blocker — Cat 21-C] Add a `narrative-rewrite` prompt contract in `BuildPrompt`.** Add an `else if` branch at `FinancialServices.cs:958` for `narrative-rewrite` and `slide-chat` that: (a) instructs Codex to return `{ narrative: string }` prose only; (b) injects current-period deltas ranked by absolute variance; (c) enforces tone rules inline ("active voice, cite amounts, state one decision per driver, no filler phrases"). Remove the `issues[]` schema fallback for narrative modules.

2. **[Blocker — Cat 21-A + 23-A] Bind `BuildPackageNarrative` to period context and replace the template string.** Extend `KpiDefinition` with a `ReportingPeriodId` (nullable, for backward compat) so KPIs can be period-snapshotted. Feed the top-3 KPIs by variance magnitude into `BuildPackageNarrative` alongside the line delta. Replace the template sentence with structured context passed to the Codex narrative-rewrite call so each executive summary paragraph names the period, the top driver, the KPI impact, and an explicit board action.

3. **[Major — Cat 23-B] Replace static pin-sort with a materiality-ranked KPI selection algorithm.** At `Program.cs:2492`, compute `|CurrentValue - TargetValue| / TargetValue` as a `MaterialityScore` on read (no schema change required). Sort the KPI scorecard and package snapshot KPI list by `IsPinned DESC, MaterialityScore DESC` so the most off-target, highest-variance KPIs surface first. Pass `status_changed_this_period` and `variance_vs_target` signals in the snapshot JSON to give Codex enough signal to select period-relevant KPIs in AI commentary.
