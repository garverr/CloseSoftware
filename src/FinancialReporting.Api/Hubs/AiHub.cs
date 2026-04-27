using Microsoft.AspNetCore.SignalR;

namespace FinancialReporting.Api.Hubs;

/// <summary>
/// SignalR hub for streaming AI run progress. Clients subscribe to a specific run via
/// SubscribeToRun(runId) so a user watching package A doesn't see updates from package B's
/// AI runs. Cat 34. The previous implementation broadcast every tick to every client,
/// which both leaked information and burned bandwidth at scale.
/// </summary>
public sealed class AiHub : Hub
{
    public Task SubscribeToRun(string runId)
        => Groups.AddToGroupAsync(Context.ConnectionId, GroupName(runId));

    public Task UnsubscribeFromRun(string runId)
        => Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(runId));

    public static string GroupName(string runId) => $"ai-run::{runId}";
    public static string GroupName(Guid runId) => GroupName(runId.ToString());
}
