# Claude Code Change Review - 2026-04-27

## Scope

Reviewed the post-initial changes on `main`:

- `ff04fd2` - Backend feature-folder decomposition, auth, infrastructure
- `de39315` - Frontend App.tsx extraction
- `d4b1e73` - Numeric regression suite expansion
- `9eb50da` - README/CONTRIBUTING/review archive updates

Three sub-agents reviewed backend, frontend, and docs/tests independently. This file merges their findings with local verification.

## Resolution Update

Status: resolved in the current working tree. The original findings below are kept as the pre-fix review record.

Fixes applied:

- Production auth no longer trusts `X-FR-*` headers outside Development; `/api/*` and `/hubs/*` challenge unauthenticated production callers, with health and Xero callback left public.
- Authenticated users without `org` fail closed, Xero tenant metadata is filtered through scoped tenant mappings / `xero_tenants`, and AI run polling / hub subscriptions check package visibility.
- Approved packages now reject slide, block, theme, reporting-studio, lifecycle, issue, flux, and AI draft state mutations.
- Flux AI citations now require `journalLineId` values that appear in the supplied drilldown snapshot; the drilldown DTO emits source journal line IDs.
- Frontend AI settings uses `PUT`, flux type controls include YTD / prior quarter / budget, smoke scans extracted source files, and lint/build are clean.
- Docs were standardized on local API port `5264` and narrowed test-coverage claims to what is actually pinned.

Post-fix verification:

- `dotnet test` - passed 60/60; emitted existing NU1902 warnings for `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.15.1.
- `npm run lint` - passed.
- `npm run build` - passed.
- `npm run test:smoke` - passed 60 checks against local API + Vite servers.
- Production auth probe on a temp SQLite DB: `/api/health` returned `200`, forged `X-FR-Role: Admin` on `POST /api/packages/ensure` returned `401`, and unauthenticated `/hubs/ai` returned `401`.

## Findings

### P0 - Production auth still trusts caller-supplied role headers

`EndpointHelpers.Can()` falls back to `X-FR-Role` for any unauthenticated request. `AuthBypass.AllowDevAdminBypass` only disables the missing-header default; it does not disable supplied headers outside Development.

Evidence:

- `src/FinancialReporting.Api/Common/EndpointHelpers.cs:21`
- `src/FinancialReporting.Api/Common/EndpointHelpers.cs:29`
- `src/FinancialReporting.Api/Common/EndpointHelpers.cs:39`
- `src/FinancialReporting.Api/Program.cs:227`

Dynamic check in `Production` on a temp SQLite DB:

- No auth header on `POST /api/packages/ensure` returned `403`.
- Adding `X-FR-Role: Admin` reached the protected handler and returned request validation `400`.

Impact: an external caller can forge Admin/Finance Editor/Reviewer access on protected mutations.

Recommended fix: only read `X-FR-*` headers when `AuthBypass.AllowDevAdminBypass` is true, add a fallback authorization policy or endpoint groups with `RequireAuthorization()`, and add non-Development auth integration tests.

### P1 - Authenticated tokens without org scope see all tenant data

`HttpContextOrganizationContext.CurrentOrganizationId` returns null when the authenticated token has no valid `org` claim, and the EF filters treat null as "no filter." `AllowedTenantIds` is defined but unused.

Evidence:

- `src/FinancialReporting.Api/OrganizationContext.cs:43`
- `src/FinancialReporting.Api/Data/AppDbContext.cs:124`
- `src/FinancialReporting.Api/Data/AppDbContext.cs:128`

Impact: a token from an external IdP or a local dev token without `OrganizationId` can query cross-organization data instead of failing closed.

Recommended fix: fail closed for authenticated principals without an org claim unless they have an explicit platform-admin role, and add tests for missing/malformed org claims.

### P1 - Xero tenant metadata leaks across organizations

`/api/xero/tenants` and related status paths query all `XeroTenantConnections`, which has tenant IDs and token metadata but no org query filter. The mapping lookup is scoped, but the tenant list itself is not.

Evidence:

- `src/FinancialReporting.Api/Features/Xero/XeroEndpoints.cs:44`
- `src/FinancialReporting.Api/Features/Xero/XeroEndpoints.cs:46`
- `src/FinancialReporting.Api/Domain/Entities.cs:328`
- `src/FinancialReporting.Api/OrganizationContext.cs:19`

Impact: an org-scoped user can enumerate other tenants' names, statuses, token expiry timestamps, reconnect flags, sources, and errors.

Recommended fix: filter tenant-only Xero entities through `AllowedTenantIds` or a join to scoped `XeroTenantEntityMappings`; add query filters or helper methods for tenant-scoped tables.

### P1 - Approved packages remain mutable

Approval marks a package approved and final, but slide/block/theme/reporting-studio mutation endpoints do not reject approved packages.

Evidence:

- `src/FinancialReporting.Api/Features/Packages/PackageApprovalEndpoints.cs:87`
- `src/FinancialReporting.Api/Features/Packages/SlideBlockVersionEndpoints.cs:91`
- `src/FinancialReporting.Api/Features/Packages/SlideBlockVersionEndpoints.cs:117`
- `src/FinancialReporting.Api/Features/Packages/SlideBlockVersionEndpoints.cs:206`
- `src/FinancialReporting.Api/Features/Packages/PackageThemeEndpoints.cs:88`
- `src/FinancialReporting.Api/Features/Packages/PackageThemeEndpoints.cs:118`
- `src/FinancialReporting.Api/Features/Packages/PackageThemeEndpoints.cs:173`

Impact: a CFO-approved package can be changed after approval while distribution still treats the package as approved.

Recommended fix: centralize an approved-package guard for every package mutation except explicit unapprove/admin recovery flows.

### P1 - New backend flux bases are hidden in the UI

The backend now emits `YearToDate`, `PriorQuarter`, and `VsBudget`, but `FluxReviewPanel` only allows `MonthOverMonth` and `YearOverYear`.

Evidence:

- `src/FinancialReporting.Api/Services/XeroLedgerServices.cs:1174`
- `src/FinancialReporting.Api/Services/XeroLedgerServices.cs:1177`
- `src/FinancialReporting.Web/src/features/flux/FluxReviewPanel.tsx:330`
- `src/FinancialReporting.Web/src/features/flux/FluxReviewPanel.tsx:570`

Impact: the expanded backend functionality is unreachable from the UI.

Recommended fix: extend the frontend flux type union and segmented control, and add a UI-level regression or smoke check for each backend flux type.

### P2 - AI settings save uses POST while the API exposes PUT

The frontend calls `postJson('/api/settings/ai-runtime', normalized)`, but the backend route is `MapPut`.

Evidence:

- `src/FinancialReporting.Web/src/pages/AiSettingsPage.tsx:38`
- `src/FinancialReporting.Api/Features/Ai/AiSettingsEndpoints.cs:27`

Impact: clicking Save settings fails instead of persisting model/reasoning choices.

Recommended fix: switch the UI to `putJson` and add a small interaction test or smoke assertion.

### P2 - Materiality matrix ignores AccountClass and can throw

The schema/index allows multiple threshold rows by account class, but the service caches by `(OrganizationId, StatementType)` only.

Evidence:

- `src/FinancialReporting.Api/Data/AppDbContext.cs:100`
- `src/FinancialReporting.Api/Services/XeroLedgerServices.cs:1173`
- `src/FinancialReporting.Api/Services/XeroLedgerServices.cs:1189`

Impact: two rows for the same statement with different account classes can throw during `ToDictionary`; even one row cannot apply class-specific thresholds.

Recommended fix: include `AccountClass` in the key and pass the account class into threshold resolution.

### P2 - Frontend smoke test is stale after extraction

`scripts/frontend-smoke.mjs` reads only `src/App.tsx`, but many asserted markers moved into `src/pages` and `src/features`.

Evidence:

- `src/FinancialReporting.Web/scripts/frontend-smoke.mjs:31`
- `src/FinancialReporting.Web/scripts/frontend-smoke.mjs:58`

Dynamic check:

- `npm run test:smoke` failed on `frontend wired to /api/slides/${slide.id}/reorder-blocks`.

Impact: the smoke gate fails for healthy extracted code and encourages moving wiring back into `App.tsx`.

Recommended fix: scan all frontend source files or replace marker checks with runtime endpoint/UI checks.

### P2 - Frontend lint gate is red

`npm run lint` fails with 10 errors and 2 warnings.

First failure:

- `src/FinancialReporting.Web/src/api/client.ts:30`
- `src/FinancialReporting.Web/src/features/flux/FluxReviewPanel.tsx:499`

Additional errors were reported in `MappingView.tsx`, `SlideEditor.tsx`, `KpisPage.tsx`, `PlanningPage.tsx`, and `ReportingStudioPage.tsx`.

Impact: CI/local validation cannot pass if lint is part of the gate.

Recommended fix: decide whether React 19 compiler lint rules are intended to be enforced. If yes, fix purity/set-state/immutability issues; if not, adjust ESLint config intentionally.

### P2 - AI citation guarantee is not enforced

README promises journal-line citations, but validation only checks `evidence[].journalLineId` when an `evidence` array exists. Hypotheses can pass without evidence.

Evidence:

- `README.md:2`
- `src/FinancialReporting.Api/Services/FinancialServices.cs:1107`
- `src/FinancialReporting.Api/Services/FinancialServices.cs:1124`

Impact: AI output can satisfy validation without the cited journal-line evidence the product promises.

Recommended fix: require non-empty evidence for the relevant AI modules, or soften docs until validation and prompts enforce the contract.

### P2 - Test/docs coverage claims overstate current tests

README says workflow tests cover journal pagination, but the tests cover fixed-payload upsert behavior rather than HTTP pagination, cursor advancement, max-page behavior, or empty-page stop conditions.

Evidence:

- `README.md:126`
- `tests/FinancialReporting.Api.Tests/CoreWorkflowTests.cs`

Impact: a reader may believe Xero pagination is guarded when a regression could still ship.

Recommended fix: either add pagination tests around the sync path or narrow the README claim.

### P3 - Local API port docs conflict

Root README starts the API on `localhost:5198`, while launch settings, Vite proxy, and frontend smoke default to `localhost:5264`.

Evidence:

- `README.md:29`
- `README.md:77`
- `src/FinancialReporting.Web/README.md:12`
- `src/FinancialReporting.Web/vite.config.ts:4`
- `src/FinancialReporting.Api/Properties/launchSettings.json:7`

Impact: following the docs leaves the frontend pointed at a different API port unless environment variables are manually overridden.

Recommended fix: standardize on one local API port in docs, Vite config, smoke defaults, and launch settings.

## Original Verification

Commands run locally:

- `dotnet test` - passed 54/54; emitted NU1902 warnings for `OpenTelemetry.Exporter.OpenTelemetryProtocol` 1.15.1.
- `npm run build` - passed.
- `npm run lint` - failed with 10 errors and 2 warnings.
- `npm run test:smoke` - failed on stale `App.tsx` marker scanning.
- Production auth probe on a temp SQLite DB - confirmed forged `X-FR-Role: Admin` reaches protected handler.

The worktree was clean before this review note was added.
