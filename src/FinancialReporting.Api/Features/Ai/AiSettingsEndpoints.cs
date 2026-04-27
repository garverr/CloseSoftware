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
/// /api/settings/ai-runtime (GET, PUT) and /api/ai/models (GET).
/// Manages per-module AI runtime configuration and model discovery. Cat 29.
/// </summary>
public static class AiSettingsEndpoints
{
    public static IEndpointRouteBuilder MapAiSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings/ai-runtime", async (AppDbContext db, CancellationToken ct) =>
        {
            var settings = await db.AiRuntimeSettings.AsNoTracking().OrderBy(x => x.Module).ToListAsync(ct);
            return Results.Ok(settings);
        });

        app.MapPut("/api/settings/ai-runtime", async (List<AiRuntimeSettingRequest> requests, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin"))
            {
                return Results.Forbid();
            }

            var before = JsonSerializer.Serialize(await db.AiRuntimeSettings.AsNoTracking().OrderBy(x => x.Module).ToListAsync(ct));
            foreach (var request in requests)
            {
                var setting = await db.AiRuntimeSettings.FirstOrDefaultAsync(x => x.Module == request.Module, ct);
                if (setting is null)
                {
                    db.AiRuntimeSettings.Add(new AiRuntimeSetting
                    {
                        Id = Guid.NewGuid(),
                        Module = request.Module,
                        Model = request.Model,
                        ReasoningEffort = request.ReasoningEffort,
                        Profile = request.Profile,
                        Enabled = request.Enabled,
                        UpdatedAt = DateTimeOffset.UtcNow
                    });
                }
                else
                {
                    setting.Model = request.Model;
                    setting.ReasoningEffort = request.ReasoningEffort;
                    setting.Profile = request.Profile;
                    setting.Enabled = request.Enabled;
                    setting.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            await EndpointHelpers.AuditAsync(db, http, "ai.settings.update", "AiRuntimeSetting", null, null, "Updated AI runtime settings", before, JsonSerializer.Serialize(requests), ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        app.MapGet("/api/ai/models", async (CodexModelDiscovery discovery, CancellationToken ct) =>
            Results.Ok(await discovery.DiscoverAsync(ct)));

        return app;
    }
}
