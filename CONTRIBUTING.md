# Contributing

## Layout philosophy

The project is mid-migration from a two-monolith codebase (one giant `Program.cs`, one giant `App.tsx`) toward feature folders. New work should land in feature folders so the monoliths shrink rather than grow:

- **Backend**: `src/FinancialReporting.Api/Features/<Feature>/<Feature>Endpoints.cs` plus a `MapXxxEndpoints` extension method called from `Program.cs`. Follow the pattern in `Features/Packages/PackageApprovalEndpoints.cs`.
- **Frontend**: extract new screens to their own files under `src/pages/` and shared widgets to `src/components/`. Don't add new screens directly to `App.tsx`.

When you touch an existing endpoint or screen that's still in the monolith, prefer migrating it to a feature folder over adding more to the monolith — but don't sprawl the change beyond what your task requires.

## Don't

- Don't introduce a sign-heuristic for account `Type`. The `XeroChartOfAccount` cache is the source of truth.
- Don't ship a query path that doesn't respect `IOrganizationContext`. Use the EF global query filters.
- Don't add new top-level error responses. Throw a typed exception; `GlobalExceptionHandler` produces the RFC 7807 envelope.
- Don't add Xero API callers that bypass `XeroApiRequestScheduler` (rate-limit + retry) and `XeroTokenRefreshLock` (concurrency-safe refresh).
- Don't write to closed `ReportingPeriod` rows. Honor `IsClosed` in any new sync/backfill path.
- Don't broadcast to `Clients.All` on the SignalR hub. Use per-run groups via `AiHub.GroupName(runId)`.
- Don't return `new { error = "..." }` envelopes. Use `Results.Problem(...)` or throw.

## Tests

```bash
dotnet test
```

Add a numeric regression test for any change that affects:

- `FinancialMath.Variance`
- Flux materiality thresholds or sign conventions
- Roll-up / consolidation logic
- The baseline diff engine

The current suite uses xUnit with `[Theory] + [InlineData]` for parameterized cases.

## Adding a new flux comparison base

1. Add a constant alongside `MonthOverMonth`, `YearOverYear`, `YearToDate`, `PriorQuarter`, `VsBudget` in `FluxReviewService`.
2. Pre-load any baseline data (similar to `ytdLines` or `priorMonthLines`).
3. Call `UpsertFluxGroup` inside the per-group loop with the new flux type.
4. Update `BuildAiExplanationSnapshotAsync` if the snapshot needs new context fields.
5. Add a `[Fact]` regression test that exercises the new path.

## Adding a new AI module

1. Add a branch in `BuildPrompt` for the new module name with a JSON contract.
2. Add a corresponding branch in `TryValidateAiJson`.
3. Update `MockCodexOutputAsync` so the dev mock produces an output that satisfies the contract — without this, dev runs always fail validation when `Ai:UseMockRunner=true`.
4. Cover the schema with a unit test.

## Working with the Codex CLI runtime

- Set `Ai:UseMockRunner=false` to launch real Codex jobs. The service must run as the OS user authenticated to Codex.
- Cancellation: `POST /api/ai/runs/{runId}/cancel` flips `Status` to Cancelled if Queued; if Running, the worker reads `CancellationRequested` between progress updates and kills the live process tree on the next iteration (P3.35).
- Crash recovery: any `AiRun` left `Running` at startup is reset to `Queued` so the run is re-attempted.

## Backup / DR

- Back up `financial-reporting-dev.db*` AND the DataProtection key ring directory (`appsettings.DataProtection:KeysPath`).
- The DataProtection keys are required to decrypt the Xero refresh tokens stored in the DB. Lose them and every tenant must reconnect.

## Commit style

Follow the existing commit log. Short imperative subject, optional body explaining why a change is non-obvious, reference the audit category (e.g. `Cat 19`) when it tracks back to an audit finding.

Avoid co-authoring lines and AI-generated boilerplate. Prefer one focused commit per concern; split refactors from behavioral changes.

## Code review checklist

- [ ] Build green, all tests pass
- [ ] No new `new { error = ... }` envelopes
- [ ] No new `hub.Clients.All` broadcasts
- [ ] No new direct Xero API calls bypassing the scheduler
- [ ] EF query filter coverage for any new entity with `OrganizationId` or `TenantId`
- [ ] Audit-record write where the change is a state transition
- [ ] If the change adds a new AI module, mock + validator + prompt contract are all in sync
