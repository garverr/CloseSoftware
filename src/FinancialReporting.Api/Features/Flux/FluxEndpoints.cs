using System.Text.Json;
using FinancialReporting.Api.Common;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using FinancialReporting.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Features.Flux;

/// <summary>
/// Flux-review endpoints: build/refresh the quarter-over-quarter variance analysis,
/// pull on-demand Xero ledger detail, manage group settings/explanations/sign-off/approval,
/// queue AI explanations, export CSV, and handle staged AI package-draft suggestions.
/// Cat 29.
/// </summary>
public static class FluxEndpoints
{
    public static IEndpointRouteBuilder MapFluxEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Flux review ─────────────────────────────────────────────────────────────────

        app.MapGet("/api/packages/{packageId:guid}/flux-review", async (
            Guid packageId,
            FluxReviewService flux,
            CancellationToken ct) =>
        {
            var result = await flux.GetOrBuildAsync(packageId, ct);
            return Results.Ok(result);
        });

        app.MapPost("/api/packages/{packageId:guid}/refresh-flux", async (
            Guid packageId,
            HttpContext http,
            AppDbContext db,
            FluxReviewService flux,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }

            var result = await flux.RefreshAsync(packageId, ct);
            await EndpointHelpers.AuditAsync(db, http, "flux.refresh", "ReportPackage", packageId, packageId, "Refreshed flux review from current ledger/statement data", "{}", JsonSerializer.Serialize(result), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        });

        app.MapPost("/api/packages/{packageId:guid}/pull-ledger-detail", async (
            Guid packageId,
            HttpContext http,
            AppDbContext db,
            FluxReviewService flux,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }

            var result = await flux.PullLedgerDetailAsync(packageId, ct);
            await EndpointHelpers.AuditAsync(db, http, "flux.ledger-detail.pull", "ReportPackage", packageId, packageId, "Pulled on-demand Xero ledger detail for active entity-period flux review", "{}", JsonSerializer.Serialize(result), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        });

        app.MapGet("/api/flux-review/groups/{groupId:guid}/drilldown", async (
            Guid groupId,
            FluxReviewService flux,
            CancellationToken ct) =>
        {
            var result = await flux.GetDrilldownAsync(groupId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapGet("/api/packages/{packageId:guid}/flux-review/export.csv", async (
            Guid packageId,
            FluxReviewService flux,
            CancellationToken ct) =>
        {
            var csv = await flux.ExportCsvAsync(packageId, ct);
            return Results.Text(csv, "text/csv");
        });

        // ── Group mutations ──────────────────────────────────────────────────────────────

        app.MapPut("/api/flux-review/groups/{groupId:guid}/settings", async (
            Guid groupId,
            FluxReviewGroupSettingsRequest request,
            HttpContext http,
            AppDbContext db,
            FluxReviewService flux,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }

            var before = await db.FluxReviewGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == groupId, ct);
            var result = await flux.UpdateSettingsAsync(groupId, request, ct);
            await EndpointHelpers.AuditAsync(db, http, "flux.settings.update", "FluxReviewGroup", groupId, result.ReportPackageId, request.Reason ?? "Updated flux thresholds and workflow settings", JsonSerializer.Serialize(before), JsonSerializer.Serialize(result), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(FluxReviewGroupDto.From(result));
        });

        app.MapPost("/api/flux-review/groups/{groupId:guid}/sign-off", async (
            Guid groupId,
            FluxSignOffRequest request,
            HttpContext http,
            AppDbContext db,
            FluxReviewService flux,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }

            var before = await db.FluxReviewGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == groupId, ct);
            var result = await flux.SignOffAsync(groupId, request.Action ?? "prepare", EndpointHelpers.Actor(http), ct);
            await EndpointHelpers.AuditAsync(db, http, "flux.signoff", "FluxReviewGroup", groupId, result.ReportPackageId, request.Reason ?? "Flux sign-off", JsonSerializer.Serialize(before), JsonSerializer.Serialize(result), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(FluxReviewGroupDto.From(result));
        });

        app.MapPost("/api/flux-review/groups/{groupId:guid}/roll-forward-explanation", async (
            Guid groupId,
            HttpContext http,
            AppDbContext db,
            FluxReviewService flux,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }

            var before = await db.FluxReviewGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == groupId, ct);
            var result = await flux.RollForwardExplanationAsync(groupId, EndpointHelpers.Actor(http), ct);
            await EndpointHelpers.AuditAsync(db, http, "flux.roll-forward", "FluxReviewGroup", groupId, result.ReportPackageId, "Rolled forward prior flux explanation", JsonSerializer.Serialize(before), JsonSerializer.Serialize(result), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(FluxReviewGroupDto.From(result));
        });

        app.MapPut("/api/flux-review/groups/{groupId:guid}/explanation", async (
            Guid groupId,
            FluxExplanationRequest request,
            HttpContext http,
            AppDbContext db,
            FluxReviewService flux,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }

            if (string.IsNullOrWhiteSpace(request.Explanation))
            {
                return Results.BadRequest(new { message = "Explanation is required." });
            }

            var before = await db.FluxReviewGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == groupId, ct);
            var result = await flux.UpdateExplanationAsync(groupId, request.Explanation, EndpointHelpers.Actor(http), ct);
            await EndpointHelpers.AuditAsync(db, http, "flux.explain", "FluxReviewGroup", groupId, result.ReportPackageId, request.Reason ?? "Updated flux explanation", JsonSerializer.Serialize(before), JsonSerializer.Serialize(result), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(FluxReviewGroupDto.From(result));
        });

        app.MapPost("/api/flux-review/groups/{groupId:guid}/approve", async (
            Guid groupId,
            HttpContext http,
            AppDbContext db,
            FluxReviewService flux,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }

            var before = await db.FluxReviewGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == groupId, ct);
            var result = await flux.ApproveAsync(groupId, EndpointHelpers.Actor(http), ct);
            await EndpointHelpers.AuditAsync(db, http, "flux.approve", "FluxReviewGroup", groupId, result.ReportPackageId, "Approved flux review group", JsonSerializer.Serialize(before), JsonSerializer.Serialize(result), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(FluxReviewGroupDto.From(result));
        });

        // ── AI explanation ───────────────────────────────────────────────────────────────

        app.MapPost("/api/flux-review/groups/{groupId:guid}/ai-explain", async (
            Guid groupId,
            HttpContext http,
            AppDbContext db,
            FluxReviewService flux,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }

            var group = await db.FluxReviewGroups.FirstOrDefaultAsync(x => x.Id == groupId, ct);
            if (group is null)
            {
                return Results.NotFound();
            }

            var setting = await db.AiRuntimeSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Module == "flux-explain", ct);
            var snapshot = await flux.BuildAiExplanationSnapshotAsync(groupId, ct);
            var run = new AiRun
            {
                Id = Guid.NewGuid(),
                ReportPackageId = group.ReportPackageId,
                Module = "flux-explain",
                PromptProfile = setting?.Profile ?? "variance-review",
                Model = setting?.Model ?? "gpt-5.5",
                ReasoningEffort = setting?.ReasoningEffort ?? "high",
                Status = AiRunStatus.Queued,
                InputJson = snapshot
            };
            db.AiRuns.Add(run);
            await EndpointHelpers.AuditAsync(db, http, "flux.ai-explain", "AiRun", run.Id, group.ReportPackageId, "Queued AI flux explanation", "{}", snapshot, ct);
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/ai/runs/{run.Id}", AiRunDto.From(run));
        });

        // ── AI package drafts ────────────────────────────────────────────────────────────

        app.MapGet("/api/packages/{packageId:guid}/ai-package-drafts", async (
            Guid packageId,
            AiPackageDraftService drafts,
            CancellationToken ct) =>
        {
            var result = await drafts.GetDraftsAsync(packageId, ct);
            return Results.Ok(result);
        });

        app.MapPost("/api/packages/{packageId:guid}/ai-package-draft", async (
            Guid packageId,
            HttpContext http,
            AppDbContext db,
            AiPackageDraftService drafts,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var result = await drafts.CreateDraftsAsync(packageId, ct);
            await EndpointHelpers.AuditAsync(db, http, "ai-package-draft.create", "ReportPackage", packageId, packageId, "Created staged AI package draft suggestions", "{}", JsonSerializer.Serialize(result), ct);
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/packages/{packageId}/ai-package-drafts", result);
        });

        app.MapPost("/api/ai-package-drafts/{draftId:guid}/accept", async (
            Guid draftId,
            HttpContext http,
            AppDbContext db,
            AiPackageDraftService drafts,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var before = await db.AiPackageDraftSuggestions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == draftId, ct);
            var result = await drafts.AcceptAsync(draftId, ct);
            await EndpointHelpers.AuditAsync(db, http, "ai-package-draft.accept", "AiPackageDraftSuggestion", draftId, result.ReportPackageId, "Accepted staged AI package suggestion", JsonSerializer.Serialize(before), JsonSerializer.Serialize(result), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        });

        app.MapPost("/api/ai-package-drafts/{draftId:guid}/reject", async (
            Guid draftId,
            RejectDraftRequest request,
            HttpContext http,
            AppDbContext db,
            AiPackageDraftService drafts,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }

            var before = await db.AiPackageDraftSuggestions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == draftId, ct);
            var result = await drafts.RejectAsync(draftId, request.Reason, ct);
            await EndpointHelpers.AuditAsync(db, http, "ai-package-draft.reject", "AiPackageDraftSuggestion", draftId, result.ReportPackageId, request.Reason ?? "Rejected staged AI package suggestion", JsonSerializer.Serialize(before), JsonSerializer.Serialize(result), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        });

        return app;
    }
}
