using System.Text.Json;
using FinancialReporting.Api.Common;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using FinancialReporting.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Features.Ai;

/// <summary>
/// /api/ai/runs/* and /api/ai-package-drafts/* — queue + cancel AI runs and accept/reject
/// staged board-package draft suggestions. Cat 29.
/// </summary>
public static class AiRunEndpoints
{
    public static IEndpointRouteBuilder MapAiRunEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ai/runs", async (CreateAiRunRequest request, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }
            var setting = await db.AiRuntimeSettings.FirstOrDefaultAsync(x => x.Module == request.Module, ct);
            var run = new AiRun
            {
                Id = Guid.NewGuid(),
                ReportPackageId = request.ReportPackageId,
                Module = request.Module,
                PromptProfile = request.PromptProfile ?? setting?.Profile ?? request.Module,
                Model = request.Model ?? setting?.Model ?? "gpt-5.5",
                ReasoningEffort = request.ReasoningEffort ?? setting?.ReasoningEffort ?? "high",
                InputJson = request.InputJson
            };
            db.AiRuns.Add(run);
            await EndpointHelpers.AuditAsync(db, http, "ai.run.queue", "AiRun", run.Id, request.ReportPackageId, $"Queued {request.Module}", "{}", request.InputJson, ct);
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/ai/runs/{run.Id}", AiRunDto.From(run));
        });

        app.MapGet("/api/ai/runs/{runId:guid}", async (Guid runId, AppDbContext db, CancellationToken ct) =>
        {
            var run = await db.AiRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == runId, ct);
            return run is null ? Results.NotFound() : Results.Ok(AiRunDto.From(run));
        });

        app.MapPost("/api/ai/runs/{runId:guid}/cancel", async (Guid runId, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }
            var run = await db.AiRuns.FirstOrDefaultAsync(x => x.Id == runId, ct);
            if (run is null)
            {
                return Results.NotFound();
            }
            run.CancellationRequested = true;
            if (run.Status == AiRunStatus.Queued)
            {
                run.Status = AiRunStatus.Cancelled;
                run.CompletedAt = DateTimeOffset.UtcNow;
            }
            await EndpointHelpers.AuditAsync(db, http, "ai.run.cancel", "AiRun", run.Id, run.ReportPackageId, "Cancelled AI run", "{}", JsonSerializer.Serialize(run), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(AiRunDto.From(run));
        });

        return app;
    }
}

public sealed record CreateAiRunRequest(Guid? ReportPackageId, string Module, string? PromptProfile, string? Model, string? ReasoningEffort, string InputJson);
