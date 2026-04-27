using FinancialReporting.Api.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Hubs;

/// <summary>
/// SignalR hub for streaming AI run progress. Clients subscribe to a specific run via
/// SubscribeToRun(runId) so a user watching package A doesn't see updates from package B's
/// AI runs. Cat 34. The previous implementation broadcast every tick to every client,
/// which both leaked information and burned bandwidth at scale.
/// </summary>
public sealed class AiHub(AppDbContext db) : Hub
{
    public async Task SubscribeToRun(string runId)
    {
        if (!Guid.TryParse(runId, out var parsedRunId))
        {
            throw new HubException("AI run was not found.");
        }

        var packageId = await db.AiRuns
            .AsNoTracking()
            .Where(x => x.Id == parsedRunId)
            .Select(x => x.ReportPackageId)
            .FirstOrDefaultAsync(Context.ConnectionAborted);
        if (packageId is null || !await db.ReportPackages.AsNoTracking().AnyAsync(x => x.Id == packageId.Value, Context.ConnectionAborted))
        {
            throw new HubException("AI run was not found.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(runId));
    }

    public Task UnsubscribeFromRun(string runId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(runId));

    public static string GroupName(string runId) => $"ai-run::{runId}";
    public static string GroupName(Guid runId) => GroupName(runId.ToString());
}
