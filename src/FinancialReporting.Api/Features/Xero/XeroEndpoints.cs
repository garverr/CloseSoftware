using System.Text.Json;
using FinancialReporting.Api;
using FinancialReporting.Api.Common;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using FinancialReporting.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Features.Xero;

/// <summary>
/// All /api/xero/* endpoints extracted from Program.cs.
/// Covers connection management, OAuth callback, sync, ledger-sync, backfill, data-coverage,
/// reconciliations, and the admin test probe. Cat 45.
/// </summary>
public static class XeroEndpoints
{
    public static IEndpointRouteBuilder MapXeroEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Status ───────────────────────────────────────────────────────────────────────
        app.MapGet("/api/xero/status", async (AppDbContext db, IOrganizationContext orgContext, XeroIntegrationService xero, XeroTenantLedgerService ledger, CancellationToken ct) =>
        {
            var connections = await db.XeroConnections.AsNoTracking().OrderBy(x => x.TenantName).ToListAsync(ct);
            var tenants = await VisibleTenantsAsync(db, orgContext, ct);
            var mappings = await db.XeroTenantEntityMappings.AsNoTracking().ToListAsync(ct);
            var runs = (await db.XeroSyncRuns.AsNoTracking().ToListAsync(ct))
                .OrderByDescending(x => x.StartedAt)
                .Take(50)
                .ToList();
            var ledgerStatus = await ledger.GetSyncStatusAsync(db, ct);
            return Results.Ok(xero.GetStatus(connections, runs, tenants, mappings, ledgerStatus));
        });

        // ── Connections ──────────────────────────────────────────────────────────────────
        app.MapGet("/api/xero/connections", async (AppDbContext db, CancellationToken ct) =>
        {
            var connections = await db.XeroConnections.AsNoTracking().OrderBy(x => x.TenantName).ToListAsync(ct);
            return Results.Ok(connections.Select(XeroConnectionDto.From));
        });

        // ── Tenants ──────────────────────────────────────────────────────────────────────
        app.MapGet("/api/xero/tenants", async (AppDbContext db, IOrganizationContext orgContext, CancellationToken ct) =>
        {
            var tenants = await VisibleTenantsAsync(db, orgContext, ct);
            var mappings = await db.XeroTenantEntityMappings.AsNoTracking().ToDictionaryAsync(x => x.TenantId, ct);
            return Results.Ok(tenants.Select(t =>
            {
                mappings.TryGetValue(t.TenantId, out var mapping);
                return new
                {
                    t.Id,
                    t.TenantId,
                    t.TenantName,
                    t.TenantType,
                    t.ConnectionStatus,
                    t.RequiresReconnectForLedger,
                    t.TokenExpiresAt,
                    t.LastConnectedAt,
                    t.LastError,
                    t.Source,
                    mappedOrganizationId = mapping?.OrganizationId,
                    isIgnored = mapping?.IsIgnored ?? false
                };
            }));
        });

        app.MapPut("/api/xero/tenants/{tenantId}/entity-map", async (
            string tenantId,
            TenantEntityMapRequest request,
            HttpContext http,
            AppDbContext db,
            IOrganizationContext orgContext,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin"))
            {
                return Results.Forbid();
            }

            var tenant = await db.XeroTenantConnections.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
            if (tenant is null)
            {
                return Results.NotFound();
            }
            if (!await TenantIsVisibleAsync(db, orgContext, tenantId, ct))
            {
                return Results.NotFound();
            }

            var org = await db.Organizations.FirstOrDefaultAsync(x => x.Id == request.OrganizationId, ct);
            if (org is null)
            {
                return Results.BadRequest(new { message = "Mapped organization was not found." });
            }

            var mapping = await db.XeroTenantEntityMappings.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct)
                          ?? new XeroTenantEntityMapping { Id = Guid.NewGuid(), TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow };
            var before = JsonSerializer.Serialize(mapping);
            mapping.OrganizationId = request.OrganizationId;
            mapping.IsIgnored = request.IsIgnored;
            mapping.Reason = request.Reason ?? "Updated tenant/entity mapping";
            mapping.UpdatedAt = DateTimeOffset.UtcNow;
            if (db.Entry(mapping).State == EntityState.Detached)
            {
                db.XeroTenantEntityMappings.Add(mapping);
            }

            await EndpointHelpers.AuditAsync(db, http, "xero.tenant-map", "XeroTenantEntityMapping", mapping.Id, null, mapping.Reason, before, JsonSerializer.Serialize(mapping), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(mapping);
        });

        // ── Connect / Reconnect ──────────────────────────────────────────────────────────
        app.MapGet("/api/xero/connect", async (
            Guid? organizationId,
            HttpContext http,
            AppDbContext db,
            XeroIntegrationService xero,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin"))
            {
                return Results.Forbid();
            }

            var orgId = organizationId ?? await db.Organizations.OrderBy(x => x.Name).Select(x => x.Id).FirstAsync(ct);
            var response = await xero.BuildConnectUrlAsync(db, orgId, ct);
            return response.Error is null ? Results.Ok(response) : Results.BadRequest(response);
        });

        app.MapPost("/api/xero/connections/{connectionId:guid}/reconnect", async (
            Guid connectionId,
            HttpContext http,
            AppDbContext db,
            XeroIntegrationService xero,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin"))
            {
                return Results.Forbid();
            }

            var connection = await db.XeroConnections.FirstOrDefaultAsync(x => x.Id == connectionId, ct);
            if (connection is null)
            {
                return Results.NotFound();
            }

            var response = await xero.BuildConnectUrlAsync(db, connection.OrganizationId, ct);
            return response.Error is null ? Results.Ok(response) : Results.BadRequest(response);
        });

        app.MapPost("/api/xero/tenants/{tenantId}/reconnect", async (
            string tenantId,
            HttpContext http,
            AppDbContext db,
            XeroIntegrationService xero,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin"))
            {
                return Results.Forbid();
            }

            var mapping = await db.XeroTenantEntityMappings.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
            var orgId = mapping?.OrganizationId ?? await db.Organizations.OrderBy(x => x.Name).Select(x => x.Id).FirstAsync(ct);
            var response = await xero.BuildConnectUrlAsync(db, orgId, ct);
            return response.Error is null ? Results.Ok(response) : Results.BadRequest(response);
        });

        // ── OAuth Callback ───────────────────────────────────────────────────────────────
        app.MapGet("/api/xero/callback", async (
            string? code,
            string? state,
            string? error,
            string? error_description,
            AppDbContext db,
            XeroIntegrationService xero,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!string.IsNullOrWhiteSpace(error))
            {
                var message = string.IsNullOrWhiteSpace(error_description) ? error : error_description;
                return Results.Content(XeroCallbackHtml(false, "Xero authorization failed", message), "text/html");
            }

            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            {
                return Results.Content(XeroCallbackHtml(false, "Xero authorization failed", "Missing Xero code or state. Start the connection again from Xero Settings."), "text/html");
            }

            try
            {
                var connection = await xero.CompleteCallbackAsync(db, code, state, ct);
                return Results.Content(
                    XeroCallbackHtml(true, "Xero connected", $"{connection.TenantName} is connected. You can return to Financial Reporting Software."),
                    "text/html");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Xero callback failed.");
                return Results.Content(
                    XeroCallbackHtml(false, "Xero authorization failed", SafeXeroCallbackMessage(ex)),
                    "text/html");
            }
        });

        // ── Sync ─────────────────────────────────────────────────────────────────────────
        app.MapPost("/api/xero/sync", async (
            XeroSyncRequest request,
            HttpContext http,
            AppDbContext db,
            XeroIntegrationService xero,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var packageId = request.ReportPackageId ?? await db.ReportPackages.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync(ct);
            var run = await xero.SyncPackageAsync(db, packageId, ct);
            await EndpointHelpers.AuditAsync(db, http, "xero.sync", "XeroSyncRun", run.Id, packageId, "Manual Xero sync", "{}", JsonSerializer.Serialize(run), ct);
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/xero/sync/{run.Id}", run);
        });

        app.MapPost("/api/xero/sync-period", async (
            XeroSyncPeriodRequest request,
            HttpContext http,
            AppDbContext db,
            XeroIntegrationService xero,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var options = new XeroPeriodSyncOptions(
                string.IsNullOrWhiteSpace(request.PeriodKey) ? "2026-01" : request.PeriodKey,
                string.IsNullOrWhiteSpace(request.Basis) ? "accrual" : request.Basis,
                request.IncludeAllTenants,
                request.CreateConsolidation);
            var result = await xero.SyncPeriodAsync(db, options, ct);
            await EndpointHelpers.AuditAsync(db, http, "xero.sync-period", "ReportingPeriod", result.ReportingPeriodId, null, $"Synced {result.PeriodKey} Xero financials", "{}", JsonSerializer.Serialize(result), ct);
            await db.SaveChangesAsync(ct);
            return Results.Accepted("/api/xero/sync-period", result);
        });

        app.MapGet("/api/xero/sync/{syncRunId:guid}", async (Guid syncRunId, AppDbContext db, CancellationToken ct) =>
        {
            var run = await db.XeroSyncRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == syncRunId, ct);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        // ── Finance App V2 Token Import ──────────────────────────────────────────────────
        app.MapPost("/api/xero/import-v2-tokens/preview", async (
            HttpContext http,
            AppDbContext db,
            XeroTenantLedgerService ledger,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin"))
            {
                return Results.Forbid();
            }

            var result = await ledger.PreviewFinanceAppV2ImportAsync(ct);
            await EndpointHelpers.AuditAsync(db, http, "xero.import-v2-preview", "XeroTenantConnection", null, null, "Previewed Finance App V2 token import", "{}", JsonSerializer.Serialize(result), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        });

        app.MapPost("/api/xero/import-v2-tokens", async (
            HttpContext http,
            AppDbContext db,
            XeroTenantLedgerService ledger,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin"))
            {
                return Results.Forbid();
            }

            var result = await ledger.ImportFinanceAppV2TokensAsync(db, ct);
            await EndpointHelpers.AuditAsync(db, http, "xero.import-v2-tokens", "XeroTenantConnection", null, null, "Imported Finance App V2 Xero tenants", "{}", JsonSerializer.Serialize(result), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        });

        // ── Ledger-Sync Settings ─────────────────────────────────────────────────────────
        app.MapGet("/api/xero/ledger-sync-settings", async (AppDbContext db, XeroTenantLedgerService ledger, CancellationToken ct) =>
        {
            var settings = await ledger.GetSettingsAsync(db, ct);
            return Results.Ok(settings);
        });

        app.MapPut("/api/xero/ledger-sync-settings", async (
            XeroLedgerSyncSettingsRequest request,
            HttpContext http,
            AppDbContext db,
            XeroTenantLedgerService ledger,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin"))
            {
                return Results.Forbid();
            }

            var settings = await ledger.UpdateSettingsAsync(db, request, ct);
            await EndpointHelpers.AuditAsync(db, http, "xero.ledger-settings", "XeroLedgerSyncSetting", settings.Id, null, "Updated ledger sync settings", "{}", JsonSerializer.Serialize(settings), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(settings);
        });

        // ── Ledger-Sync Run / Status ─────────────────────────────────────────────────────
        app.MapPost("/api/xero/ledger-sync/run", async (
            XeroLedgerRunRequest request,
            HttpContext http,
            AppDbContext db,
            IOrganizationContext orgContext,
            XeroTenantLedgerService ledger,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }
            if (!string.IsNullOrWhiteSpace(request.TenantId) && !await TenantIsVisibleAsync(db, orgContext, request.TenantId, ct))
            {
                return Results.NotFound();
            }

            var result = await ledger.RunIncrementalLedgerSyncAsync(db, request.TenantId, request.Force, ct);
            await EndpointHelpers.AuditAsync(db, http, "xero.ledger-sync", "XeroLedgerSyncCursor", null, null, "Manual Xero ledger sync", "{}", JsonSerializer.Serialize(result), ct);
            await db.SaveChangesAsync(ct);
            return Results.Accepted("/api/xero/ledger-sync/status", result);
        });

        app.MapGet("/api/xero/ledger-sync/status", async (AppDbContext db, XeroTenantLedgerService ledger, CancellationToken ct) =>
        {
            var result = await ledger.GetSyncStatusAsync(db, ct);
            return Results.Ok(result);
        });

        // ── Backfill ─────────────────────────────────────────────────────────────────────
        app.MapPost("/api/xero/backfill/preview", async (
            XeroBackfillRequest request,
            HttpContext http,
            AppDbContext db,
            XeroBackfillService backfill,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var result = await backfill.PreviewAsync(db, request, ct);
            await EndpointHelpers.AuditAsync(db, http, "xero.backfill-preview", "XeroBackfillRun", null, null, "Previewed historical Xero backfill", "{}", JsonSerializer.Serialize(result), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        });

        app.MapPost("/api/xero/backfill", async (
            XeroBackfillRequest request,
            HttpContext http,
            AppDbContext db,
            XeroBackfillService backfill,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var result = await backfill.QueueAsync(db, request, ct);
            await EndpointHelpers.AuditAsync(db, http, "xero.backfill-queue", "XeroBackfillRun", result.Id, null, "Queued historical Xero backfill", "{}", JsonSerializer.Serialize(result), ct);
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/xero/backfill/{result.Id}", result);
        });

        app.MapGet("/api/xero/backfill/{runId:guid}", async (Guid runId, AppDbContext db, XeroBackfillService backfill, CancellationToken ct) =>
        {
            var result = await backfill.GetRunAsync(db, runId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/xero/backfill/{runId:guid}/pause", async (Guid runId, HttpContext http, AppDbContext db, XeroBackfillService backfill, CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var result = await backfill.SetStatusAsync(db, runId, "Paused", ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/xero/backfill/{runId:guid}/resume", async (Guid runId, HttpContext http, AppDbContext db, XeroBackfillService backfill, CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var result = await backfill.SetStatusAsync(db, runId, "Queued", ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/xero/backfill/{runId:guid}/cancel", async (Guid runId, HttpContext http, AppDbContext db, XeroBackfillService backfill, CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var result = await backfill.SetStatusAsync(db, runId, "Cancelled", ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        // ── Data Coverage ────────────────────────────────────────────────────────────────
        app.MapGet("/api/xero/data-coverage", async (string? from, string? to, AppDbContext db, XeroBackfillService backfill, CancellationToken ct) =>
        {
            var result = await backfill.BuildCoverageAsync(db, from, to, ct);
            return Results.Ok(result);
        });

        // ── Ledger Reconciliations ───────────────────────────────────────────────────────
        app.MapGet("/api/xero/tenants/{tenantId}/ledger-reconciliations", async (string tenantId, AppDbContext db, IOrganizationContext orgContext, XeroTenantLedgerService ledger, CancellationToken ct) =>
        {
            if (!await TenantIsVisibleAsync(db, orgContext, tenantId, ct))
            {
                return Results.NotFound();
            }
            var result = await ledger.GetReconciliationsAsync(db, tenantId, ct);
            return Results.Ok(result);
        });

        app.MapPost("/api/xero/tenants/{tenantId}/ledger-reconciliations/run", async (
            string tenantId,
            XeroReconciliationRunRequest request,
            HttpContext http,
            AppDbContext db,
            IOrganizationContext orgContext,
            XeroTenantLedgerService ledger,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }
            if (!await TenantIsVisibleAsync(db, orgContext, tenantId, ct))
            {
                return Results.NotFound();
            }

            var date = request.SnapshotDate ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);
            var result = await ledger.RunTrialBalanceReconciliationAsync(db, tenantId, date, ct);
            await EndpointHelpers.AuditAsync(db, http, "xero.ledger-reconcile", "XeroLedgerReconciliationRun", result.Id, null, "Ran Trial Balance reconciliation", "{}", JsonSerializer.Serialize(result), ct);
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/xero/tenants/{tenantId}/ledger-reconciliations", result);
        });

        // ── Test Probe ───────────────────────────────────────────────────────────────────
        app.MapPost("/api/xero/test", async (HttpContext http, AppDbContext db, IOrganizationContext orgContext, XeroIntegrationService xero, XeroTenantLedgerService ledger, CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin"))
            {
                return Results.Forbid();
            }

            var connections = await db.XeroConnections.AsNoTracking().ToListAsync(ct);
            var tenants = await VisibleTenantsAsync(db, orgContext, ct);
            var mappings = await db.XeroTenantEntityMappings.AsNoTracking().ToListAsync(ct);
            var runs = (await db.XeroSyncRuns.AsNoTracking().ToListAsync(ct))
                .OrderByDescending(x => x.StartedAt)
                .Take(50)
                .ToList();
            var ledgerStatus = await ledger.GetSyncStatusAsync(db, ct);
            return Results.Ok(xero.GetStatus(connections, runs, tenants, mappings, ledgerStatus));
        });

        return app;
    }

    // ── Private helpers ──────────────────────────────────────────────────────────────────

    private static async Task<bool> TenantIsVisibleAsync(AppDbContext db, IOrganizationContext orgContext, string tenantId, CancellationToken ct)
    {
        var visible = await VisibleTenantsAsync(db, orgContext, ct);
        return visible.Any(x => string.Equals(x.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<List<XeroTenantConnection>> VisibleTenantsAsync(AppDbContext db, IOrganizationContext orgContext, CancellationToken ct)
    {
        var query = db.XeroTenantConnections.AsNoTracking().AsQueryable();
        if (orgContext.CurrentOrganizationId is not null || orgContext.AllowedTenantIds is not null)
        {
            var tenantIds = await db.XeroTenantEntityMappings
                .AsNoTracking()
                .Where(x => !x.IsIgnored)
                .Select(x => x.TenantId)
                .ToListAsync(ct);
            var visible = tenantIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (orgContext.AllowedTenantIds is { } allowed)
            {
                visible.IntersectWith(allowed);
            }
            query = query.Where(x => visible.Contains(x.TenantId));
        }

        return await query.OrderBy(x => x.TenantName).ToListAsync(ct);
    }

    // Local copy of Program.cs helper; will dedupe once Program.cs is fully drained.
    private static string XeroCallbackHtml(bool success, string title, string message)
    {
        var encodedTitle = System.Net.WebUtility.HtmlEncode(title);
        var encodedMessage = System.Net.WebUtility.HtmlEncode(message);
        var type = success ? "xero-connected" : "xero-error";
        var color = success ? "#0f7a57" : "#b91c1c";
        var messagePayload = JsonSerializer.Serialize(new { type, message });

        return $"""
            <!doctype html>
            <html>
            <head>
                <title>{encodedTitle}</title>
                <meta name="viewport" content="width=device-width, initial-scale=1" />
            </head>
            <body style="margin:0;background:#f7f6f4;color:#1a1a1a;font-family:Inter,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;">
                <main style="max-width:520px;margin:72px auto;padding:28px;border:1px solid #e5e5e8;border-radius:8px;background:white;">
                    <div style="width:36px;height:36px;border-radius:8px;background:{color};color:white;display:grid;place-items:center;font-weight:800;">{(success ? "✓" : "!")}</div>
                    <h1 style="margin:18px 0 8px;font-size:24px;line-height:1.1;">{encodedTitle}</h1>
                    <p style="margin:0;color:#6b6b70;line-height:1.5;">{encodedMessage}</p>
                </main>
                <script>
                    window.opener?.postMessage({messagePayload}, '*');
                </script>
            </body>
            </html>
            """;
    }

    // Local copy of Program.cs helper; will dedupe once Program.cs is fully drained.
    private static string SafeXeroCallbackMessage(Exception ex)
    {
        var message = EndpointHelpers.RedactSensitive(ex.Message);
        if (string.IsNullOrWhiteSpace(message))
        {
            return "The callback could not be completed. Return to Xero Settings and start a fresh reconnect.";
        }

        if (message.Length > 280)
        {
            message = $"{message[..280]}...";
        }

        return $"The callback could not be completed: {message}";
    }
}
