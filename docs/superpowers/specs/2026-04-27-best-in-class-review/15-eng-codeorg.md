# Engineering: Code Organization Review

**Categories:** 29 (Backend — Program.cs), 30 (Frontend — App.tsx), 31 (Domain model integrity)

**Pillar verdict:** Engineering organization is foundational — decomposing Program.cs and App.tsx is a prerequisite for any team scaling. Domain anemia means invariants are enforced nowhere, which quietly undermines all three pillars (Xero import, Flux/AI GL, AI Board Package).

---

## Category 29 — Backend: Program.cs is too big

| Severity | Finding | Evidence | Best-in-class gap | Recommendation |
|---|---|---|---|---|
| Blocker | 5,118-line single file contains 125 Minimal API endpoint lambdas, 88 DTO/record types, 3 background workers registered, and every service registration | `Program.cs:1–5118`; endpoint count via grep | Industry standard: feature-folder controllers or endpoint groups, each < 200 lines | Introduce feature folders with `IEndpointRouteBuilderExtensions` per domain area |
| Major | Authorization is a bespoke header check (`X-FR-Role`) duplicated across dozens of handlers with no policy abstraction | `Program.cs:3632–3639` (`Can()` helper), called at `Program.cs:443, 480, 1891, 1910, ...`) | ASP.NET Core `AddAuthorization` with named policies; `RequireAuthorization("FinanceEditor")` per group | Replace `Can()` calls with ASP.NET Core authorization policies; map role header via a custom `IClaimsTransformation` |
| Major | 88 DTO records and mapping (`From()` factories) live inside Program.cs alongside route handlers | `Program.cs:4795–5118` | DTOs in `Features/{Feature}/Dtos/` with AutoMapper or explicit mappers in service layer | Move all records and `From()` factories into feature-scoped files |
| Minor | Service registrations are a 20-line flat list with no grouping | `Program.cs:48–66` | `IServiceCollection` extension methods: `services.AddXeroFeature()`, `services.AddFluxFeature()`, etc. | Extract `AddXeroFeature`, `AddFluxFeature`, `AddAiFeature`, `AddPackageFeature` extension methods into `/Features/{Feature}/ServiceCollectionExtensions.cs` |

**Recommended folder layout:**

```
src/FinancialReporting.Api/
  Features/
    Xero/
      XeroEndpoints.cs          // ~25 endpoints (auth, sync, backfill, ledger)
      XeroServiceExtensions.cs  // AddXeroFeature()
      Dtos/                     // XeroConnectionDto, etc.
    Flux/
      FluxEndpoints.cs          // ~10 endpoints
      Dtos/
    Packages/
      PackageEndpoints.cs       // ~20 endpoints (CRUD, slides, blocks, versions)
      Dtos/
    Ai/
      AiEndpoints.cs            // ~6 endpoints + AiRun DTOs
      Dtos/
    Mapping/
      MappingEndpoints.cs       // ~12 endpoints
      Dtos/
    Reporting/
      ReportingEndpoints.cs     // studio, templates, export, share
      Dtos/
    Planning/
      PlanningEndpoints.cs      // forecast, benchmarks, FX, KPIs
      Dtos/
    Auth/
      AuthorizationExtensions.cs // policies replacing Can()
  Program.cs                    // < 80 lines: builder, middleware, app.Run()
```

Each `*Endpoints.cs` uses `RouteGroupBuilder`: `var g = app.MapGroup("/api/xero").RequireAuthorization("XeroAdmin");`

---

## Category 30 — Frontend: App.tsx is too big

| Severity | Finding | Evidence | Best-in-class gap | Recommendation |
|---|---|---|---|---|
| Blocker | 5,423-line single component file with 16 named views, 78 top-level component/function declarations, 123 React hooks (`useState`, `useEffect`, `useCallback`, `useMemo`) all co-located | `App.tsx:45–63` (View union type); `App.tsx:810` (`useState<View>`); hook count via grep | Industry standard: route-level page components (< 300 lines each), feature folders, custom hooks extracting all data-fetching | Split each view into its own `pages/` file; one route = one file |
| Major | All API fetch logic is inline inside the single component or as ad-hoc closures — no separation between data layer and presentation | No dedicated `hooks/` or `api/` directory (only `App.tsx` and `App.css` under `src/`) | Custom hooks (`useFluxGroups`, `usePackage`, `useXeroStatus`) with `useSWR` or React Query | Extract all `fetch` calls into `src/hooks/use{Feature}.ts` files |
| Major | 2,679-line `App.css` is a flat global stylesheet with no scoping | `src/FinancialReporting.Web/src/App.css` (2679 lines) | CSS Modules (`.module.css`) per component, or Tailwind utility classes; design tokens in `:root` variables | Introduce CSS Modules for each new page component; migrate globals into `tokens.css` |
| Minor | 51 view-switch conditionals (`view === '...'`) render all screens inline in one JSX block | `App.tsx:979–1019` | React Router `<Routes>` with lazy-loaded pages | Introduce `react-router-dom` with `React.lazy()` per page for code-splitting |

**Recommended folder layout:**

```
src/FinancialReporting.Web/src/
  pages/
    DashboardPage.tsx
    SlidePage.tsx
    FluxPage.tsx
    MappingPage.tsx
    PlanningPage.tsx
    BenchmarksPage.tsx
    ReportStudioPage.tsx
    XeroSettingsPage.tsx
    AiSettingsPage.tsx
    OutputPage.tsx
    KpisPage.tsx
    ...
  features/
    flux/
      FluxReviewPanel.tsx
      useFluxGroups.ts
    xero/
      XeroSettings.tsx
      useXeroStatus.ts
    packages/
      usePackage.ts
      PackageSelector.tsx
    ai/
      AiRunBadge.tsx
      useAiRuns.ts
  components/           // shared primitives (SegmentButton, Toast, etc.)
  hooks/                // cross-feature hooks (useReportingContext, useToast)
  api/                  // typed fetch wrappers per feature
  tokens.css            // design tokens (:root vars)
  App.tsx               // < 100 lines: Router, layout shell, nav
```

---

## Category 31 — Domain Model Integrity

| Severity | Finding | Evidence | Best-in-class gap | Recommendation |
|---|---|---|---|---|
| Major | Fully anemic domain: no entity has a single method, computed property, or private setter; all mutation logic is in Program.cs lambdas | `Entities.cs:1–791` — zero methods found; status transitions at `Program.cs:454, 543, 581` | Rich domain: `package.Finalize()`, `package.Block(reason)`, `issue.Resolve()` enforcing state machine rules on the aggregate | Add behavior methods to `ReportPackage`, `FluxReviewGroup`, `PackageIssue` (at minimum); use `private set` to remove public mutation surface |
| Major | 31 occurrences of `string` discriminators (`Status`, `Type`, `Kind`, `Source`, `Direction`, `Severity`) spread across 14+ entities — none backed by enums | `Entities.cs`: `KpiAlert.Severity` (line 153), `SlideBlock.Kind` (line 127), `XeroLedgerSyncCursor.Status` (line 354), `FluxReviewGroup.Status` (line 609), etc. | Discriminated enums for all state fields; string only at DB/serialization boundary via value converters | Convert `Status`, `Kind`, `Type`, `Source`, `Direction`, `Severity` fields to enums; add EF Core `HasConversion<string>()` |
| Major | No period-locking enforcement: `ReportingPeriod.IsClosed` is a plain bool but nothing prevents writes to entities (slides, GL accounts, flux groups) that belong to a closed period | `Entities.cs:76`; no guard found anywhere in `Program.cs` for `IsClosed` before mutations | Service-layer or aggregate guard: `if (period.IsClosed) throw PeriodClosedException` | Add `PeriodGuard` service called from every endpoint that mutates period-scoped data; expose `ReportingPeriod.EnsureOpen()` method |
| Minor | Two nearly duplicate Xero connection entities (`XeroConnection` and `XeroTenantConnection`) with overlapping columns | `Entities.cs:288–323`; both carry `TenantId`, `EncryptedAccessToken`, `ConnectionStatus`, etc. | Single `XeroConnection` aggregate with a `ConnectionScope` enum (SingleOrg vs AllTenants) | Consolidate into one entity or document the intentional split with a code comment |
| Minor | `AuditRecord` has no aggregate root back-reference integrity — `ReportPackageId` is nullable and `EntityId` is a freeform `Guid?` | `Entities.cs:778–791` | Typed audit event records or at minimum a non-nullable `EntityType` enum | Introduce an `AuditTarget` value object (EntityType enum + EntityId); enforce at construction |

---

## Top 3 Fixes

1. **[Blocker — Cat 29] Extract endpoint groups into feature folders.** Create `Features/{Xero,Flux,Packages,Ai,Mapping,Reporting,Planning}/` directories with `*Endpoints.cs` and `ServiceCollectionExtensions.cs`. Migrate endpoints in priority order: Xero (most complex, ~25 endpoints) first. This makes the codebase navigable for any new engineer and unblocks parallel feature development without merge conflicts on `Program.cs`.

2. **[Blocker — Cat 30] Split App.tsx into route-level pages.** Install `react-router-dom`, create `src/pages/` with one file per view, and extract all fetch logic into `src/hooks/`. Start with the three highest-churn views: `FluxPage`, `MappingPage`, and `DashboardPage`. This eliminates the 5k-line file smell and enables lazy-loading.

3. **[Major — Cat 31] Enforce period locking and convert string statuses to enums.** Introduce `ReportingPeriod.EnsureOpen()` called from every write endpoint that touches period-scoped data, and convert the ~10 most-used `string Status` fields to enums (`XeroBackfillRun.Status`, `FluxReviewGroup.Status`, `StatementRun.Status`, `AiRun.Status` already has an enum — extend this pattern). Both changes can be done in a single focused PR without touching endpoint logic.
