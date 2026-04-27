---
name: Best-in-class review plan
description: 20-agent parallel audit of the Financial Reporting platform across 50 categories, benchmarked against Fathom (reporting), Closecore + Numeric/Tabs/FinOptimal/Trullion/Concourse (flux AI), and Pigment/Anaplan/Mosaic/Vena/Cube (FP&A platforms)
type: project
date: 2026-04-27
---

# Best-in-Class Review — Plan

## Objective
Audit the codebase against best-in-class standards for a CFO-grade, AI-native reporting platform. Three product pillars get the deepest scrutiny:

1. **Xero importing** — data fidelity is the foundation of everything
2. **Flux analysis** with AI investigation at the account / GL level
3. **AI-generated monthly Board Package** using prior month as baseline, material-only add/modify/delete

## Benchmarks
- **Reporting / board pack**: Fathom Reporting
- **Flux automation**: Closecore, Numeric, Tabs, FinOptimal, Trullion, Concourse
- **FP&A platform breadth**: Pigment, Anaplan, Mosaic, Vena, Cube

## Approach
- 20 agents run in parallel, each owning a coherent slice of categories
- All agents are READ-ONLY on source code; each writes one report file
- Fixed schema: Category · Severity · Finding · Evidence (file:line) · Best-in-class gap · Recommendation
- After all agents finish: rollup `99-rollup.md` with prioritized blockers, heatmap, sequenced remediation plan, and per-pillar verdict

## Categories (50)
### Cluster A — Xero Import
1. OAuth lifecycle, token refresh, revocation
2. Multi-tenant connection management & V2 token import
3. Chart of accounts ingestion & first-seen account workflow
4. General Ledger / journals sync completeness & accuracy
5. Trial balance vs GL-derived reconciliation
6. Incremental sync, change detection, idempotency
7. Rate-limit / retry / backoff / partial-failure recovery
8. Historical backfill, re-sync, period-locking

### Cluster B — Flux Analysis + AI GL Investigation
9. Flux calc methodology (PoP, YoY, vs budget; $ vs %)
10. Materiality thresholds (account-type / entity / configurable)
11. Account-level drill-down UX & GL traversal
12. AI prompt context for GL investigation
13. AI output structure: hypothesis + supporting txns + confidence + citations
14. Counterparty / vendor pattern detection
15. Recurring vs one-time classification
16. Multi-period trend integration (3 / 6 / 12 month)
17. Prior-period adjustment / reclass / accrual reversal detection
18. Auditability: AI run logs, citations, reproducibility, redaction

### Cluster C — AI Board Package Generation
19. Baseline diff engine (slide-level keep/modify/add/remove vs prior month)
20. Materiality filter for CFO/Board (only material — not everything)
21. Narrative quality & executive tone
22. Chart / visualization auto-generation
23. KPI selection logic & relevance to current period
24. Slide layout templating, branding, consistency
25. Human-in-the-loop editing of AI suggestions
26. Versioning & approval workflow (CFO sign-off → Board)
27. Export fidelity (PDF / XLSX / PPTX-equivalent)
28. Re-generation cost, idempotency, deterministic seeds

### Cluster D — Engineering / Architecture
29. Backend code organization (5,118-line `Program.cs`)
30. Frontend code organization (4,723-line `App.tsx`)
31. Domain model integrity (`Entities.cs`)
32. EF Core schema, indexes, migrations strategy
33. SQLite suitability (dev DB already 260MB)
34. SignalR real-time AI status design
35. API contract design, versioning, error envelope
36. Error handling & resilience patterns
37. Codex CLI job queue, concurrency, cancellation, isolation
38. Caching strategy
39. Frontend state management at this scale
40. Test coverage & integration depth

### Cluster E — Security / Compliance / Ops
41. Auth model (X-FR-Role headers + Admin dev-bypass)
42. Secret management
43. Encryption at rest for financial data & PII
44. Audit trail / SOX-relevant trail completeness
45. Multi-tenancy & data isolation
46. Logging & observability
47. Backup / DR / PITR
48. Performance benchmarks (large GL ingest, large package gen)
49. Accessibility (WCAG) & responsive design
50. Documentation & onboarding

## Agent Assignments (20)
| # | Agent | Cats | Focus |
|---|-------|------|-------|
| 01 | Xero-Auth | 1, 2 | OAuth, multi-tenant, V2 import |
| 02 | Xero-COA-GL | 3, 4 | Account ingest, journal sync correctness |
| 03 | Xero-Reconcile | 5 | TB-vs-GL reconciliation rigor |
| 04 | Xero-Resilience | 6, 7, 8 | Incremental, retries, backfill |
| 05 | Flux-Methodology | 9, 10 | Calc + materiality |
| 06 | Flux-Drilldown | 11, 16 | Account drill + multi-period |
| 07 | Flux-AI-Prompts | 12, 13 | Prompt context + output schema |
| 08 | Flux-Patterns | 14, 15 | Vendor / recurring detection |
| 09 | Flux-Audit | 17, 18 | Reclass detection + auditability |
| 10 | Pkg-Diff | 19, 20 | Baseline diff + materiality filter |
| 11 | Pkg-Narrative | 21, 23 | Executive prose + KPI selection |
| 12 | Pkg-Visuals | 22, 24 | Charts + branding |
| 13 | Pkg-Workflow | 25, 26 | Editing + approvals |
| 14 | Pkg-Export | 27, 28 | Fidelity + re-gen |
| 15 | Eng-CodeOrg | 29, 30, 31 | Decompose Program.cs / App.tsx, domain |
| 16 | Eng-Data | 32, 33, 34, 37 | EF, SQLite limits, SignalR, jobs |
| 17 | Eng-API-Test | 35, 36, 38, 39, 40 | API, errors, cache, state, tests |
| 18 | Sec-AuthN | 41, 42, 43 | Auth, secrets, encryption |
| 19 | Sec-Audit | 44, 45, 46 | Audit trail, tenancy, observability |
| 20 | Ops-Quality | 47, 48, 49, 50 | DR, perf, a11y, docs |

## Output
- One report per agent: `NN-<slug>.md`
- Final rollup: `99-rollup.md`
