# Engineering Review: API Contract, Error Handling, Caching, Frontend State, Test Coverage

**Categories:** 35, 36, 38, 39, 40
**Agent:** 17 of 20 | **Date:** 2026-04-27

---

## Category 35 — API Contract Design, Versioning, Error Envelope

| Dimension | Finding | Severity |
|---|---|---|
| RESTful consistency | ~100 endpoints, all prefixed `/api/...`. Verbs are broadly correct (GET/POST/PUT/DELETE) with one outlier: `POST /api/packages/ensure` (non-RESTful, should be PUT-idempotent or GET-with-creation semantics). `/share/{token}` sits outside the `/api/` namespace without explanation. | Minor |
| Versioning | No versioning strategy whatsoever. No path prefix (`/api/v1/`), no header (`Api-Version`), no `Asp.Versioning` package. Any breaking schema change will silently break existing consumers. | **Blocker** |
| Error envelope | Errors returned as ad-hoc anonymous objects: `new { error = "..." }` (e.g., Program.cs:242, 359, 534), `new { message = "..." }` (Program.cs:1510), and `new { errors }` (Program.cs:534). No `Results.Problem()` or RFC 7807 `ProblemDetails` shape is used anywhere. Consumers cannot distinguish error type, detail, or `traceId` programmatically. `AddProblemDetails()` is not registered. | **Blocker** |

**Evidence:** Program.cs:100–3145 (routes), Program.cs:242/359/534/1510 (error shapes).

---

## Category 36 — Error Handling & Resilience Patterns

| Dimension | Finding | Severity |
|---|---|---|
| Global exception handler | No `UseExceptionHandler`, no `app.Use` middleware catch-all. Unhandled exceptions from any inline lambda will surface as raw 500 with ASP.NET default response — no structured envelope, no log correlation. | **Blocker** |
| Scoped try/catch coverage | Only 3 try/catch blocks exist across ~5,118 lines: Xero OAuth callback (Program.cs:1608), formula evaluation (Program.cs:2563), and a handful of internal helpers (Program.cs:3423, 3703, 4576, 4646, 4711, 4971). All other endpoint lambdas have no exception boundary. A DB timeout, a null-deref in `FinancialEngine`, or an AI service failure returns a 500 with the full stack trace in development. | **Blocker** |
| Retry / resilience | `builder.Services.AddHttpClient()` (Program.cs:18) with no `AddResilienceHandler` / Polly pipeline. Xero API calls and AI Codex calls have no retry, circuit-breaker, or timeout policy. `XeroApiRequestScheduler` is a singleton scheduler but does not enforce retries on transient failures. | Major |
| Idempotency on destructive POSTs | `POST /api/xero/sync` and `POST /api/packages/{id}/recompile` are non-idempotent write operations with no concurrency guard (no `If-Match`, no status check before launch). Double-click or network retry can trigger duplicate sync runs. | Major |

---

## Category 38 — Caching Strategy

| Dimension | Finding | Severity |
|---|---|---|
| Response / output cache | `AddMemoryCache`, `AddOutputCache`, `AddResponseCaching` are all absent from the DI registration (Program.cs:14–72). Every request to `/api/entities/{org}/periods/{period}/statements` and `/api/packages/{id}/flux-review` hits SQLite directly with no cache layer. | **Blocker** |
| ETag / conditional GET | No `ETag`, `Last-Modified`, or `Cache-Control` headers set on any endpoint. Heavy GL endpoints (`/api/mapping/accounts`, `/api/reporting-context`) re-query on every poll cycle. | Major |
| Computed-figure invalidation | No invalidation hook on Xero sync completion. After `POST /api/xero/sync` completes, any cached financial statement figures would remain stale — moot today because nothing is cached, but the invalidation seam is also absent. | Major |
| Read-heavy reporting pattern | `/api/entities/{org}/periods/{period}/ledger-summary` (Program.cs:319) does a full GL aggregation on every call. No memoisation, no short-TTL memory cache, no DB-level materialized view. | Major |

---

## Category 39 — Frontend State Management at Scale

| Dimension | Finding | Severity |
|---|---|---|
| State library | No Zustand, Redux, Recoil, or Context-based global store. All state is `useState` scattered across component functions inside a single 4,723-line `App.tsx`. Counted 60+ `useState` calls across ~15 logical views. | **Blocker** |
| Data fetching | No TanStack Query, SWR, or RTK Query. Four raw helpers (`fetchJson`, `postJson`, `putJson`, `deleteJson`) at App.tsx:5391–5421 with no request deduplication, no cache, no stale-while-revalidate, no loading/error state normalization. | Major |
| Error surfacing | Errors from fetches are either silently swallowed (`.catch(() => undefined)` — App.tsx:881, 884, 917) or set into local `useState` with no global toast-level classification. Only the root load path propagates `setLoadError`. Business-critical failures (e.g., failed flux refresh, failed AI draft) surface nowhere. | Major |
| Polling pattern | AI run polling at App.tsx:1944–1999 uses bare `useEffect` + `setInterval` inside a local variable with no ref stabilization. Multiple effects on the same run can race if `aiRun` state updates cause re-render before the interval is cleared. | Minor |
| Bundle structure | Entire application in one file. No code splitting, no lazy imports. Initial JS payload carries all views including rarely-used admin and AI config panels. | Minor |

---

## Category 40 — Test Coverage & Integration Depth

| Dimension | Finding | Severity |
|---|---|---|
| Test file count | 1 file, 828 lines, 18 test methods for the entire backend. No separate test projects for services, domain math, or HTTP-layer integration (WebApplicationFactory). | **Blocker** |
| Xero sync coverage | 5 tests touch Xero: OAuth URL build, OAuth callback, period sync, journal import, date parser. No test for token refresh failure, rate-limit handling, or multi-tenant fan-out correctness. | Major |
| Flux & financial math | 2 numeric tests: `Variance_ReturnsAmountAndRoundedPercent` (line 17) and `FinancialEngine_BuildRollup_AppliesConsolidationOverlaysWithoutMutatingRawTransactions` (line 197). No regression suite for rounding edge cases, currency conversion, multi-period rollup, or YoY vs MoM variance sign conventions — categories where Pigment/Anaplan maintain hundreds of parameterized cases. | **Blocker** |
| AI board package | 1 test: `FluxReviewAndAiDrafts_StageChangesUntilAccepted` (line 598). No test for fix-operation sandboxing bypass, AI output schema validation, or draft acceptance side-effects on package status. | Major |
| HTTP/API layer | Zero `WebApplicationFactory` or `TestServer` tests. All tests call services or domain objects directly; no endpoint routing, middleware, or auth authorization is covered. | Major |
| Frontend tests | No Jest, Vitest, Playwright, or Cypress tests. `frontend-smoke.mjs` is a build-output existence check, not a functional test. | Major |

---

## Cross-Cutting Verdict

The backend is a single-file Minimal API with strong domain logic but no defensive perimeter: no global exception handler, no versioning, no caching layer, and a non-standard error shape. The frontend doubles this risk with 60+ scattered `useState` calls, no data-fetching library, and silent error swallowing. The test suite (18 tests, 1 file) is inadequate for a financial application where numeric rounding, consolidation logic, and Xero sync idempotency carry real money risk. Industry-standard financial SaaS (Pigment, Anaplan, Cube) maintain multi-hundred-test numeric regression suites and typed API contracts with versioning from day one.

**Blockers: 6 | Majors: 10**

---

## Top 3 Fixes

1. **Global exception handler + RFC 7807 error envelope (Cat 35 & 36):** Register `app.UseExceptionHandler` and `builder.Services.AddProblemDetails()`. Replace all `new { error = "..." }` returns with `Results.Problem(...)`. This single change closes the most severe API contract and resilience gaps simultaneously and takes under a day.

2. **Introduce TanStack Query + split App.tsx (Cat 39):** Install `@tanstack/react-query`, wrap the app in `QueryClientProvider`, and migrate the 15+ logical views into separate component files. This eliminates the polling race conditions, adds automatic dedup/retry/stale-while-revalidate, and makes the codebase maintainable at scale. Silent `.catch(() => undefined)` error handlers should be replaced with `onError` callbacks surfaced to the global toast.

3. **Numeric regression test suite + API versioning (Cat 35 & 40):** Add `[Theory, InlineData(...)]` parameterized tests covering at minimum: variance sign conventions (positive/negative, MoM vs YoY), consolidation elimination rounding, multi-tenant rollup totals, and FX conversion. Simultaneously, prefix all routes with `/api/v1/` and add the `Asp.Versioning.Http` package so future schema changes are non-breaking.
