using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FinancialReporting.Api.Common;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using FinancialReporting.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Features.Kpis;

/// <summary>
/// /api/kpis (GET, POST), /api/kpis/{id} (PUT), /api/formulas/evaluate (POST),
/// /api/kpi-alerts (GET, POST), /api/kpi-alerts/{id} (PUT). Cat 27.
/// </summary>
public static class KpiEndpoints
{
    public static IEndpointRouteBuilder MapKpiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/kpis", async (Guid? organizationId, AppDbContext db, CancellationToken ct) =>
        {
            var query = db.KpiDefinitions.AsNoTracking().AsQueryable();
            if (organizationId is not null)
            {
                query = query.Where(x => x.OrganizationId == organizationId);
            }

            var kpis = await query.OrderByDescending(x => x.IsPinned).ThenBy(x => x.Name).ToListAsync(ct);
            return Results.Ok(kpis.Select(KpiDto.From));
        });

        app.MapPost("/api/kpis", async (
            UpsertKpiRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var kpi = new KpiDefinition
            {
                Id = Guid.NewGuid(),
                OrganizationId = request.OrganizationId,
                Name = request.Name,
                Category = request.Category,
                Formula = request.Formula,
                Unit = request.Unit,
                CurrentValue = request.CurrentValue,
                TargetValue = request.TargetValue,
                IsPinned = request.IsPinned,
                Status = FinancialMath.KpiStatus(request.CurrentValue, request.TargetValue, request.HigherIsBetter)
            };
            db.KpiDefinitions.Add(kpi);
            await EndpointHelpers.AuditAsync(db, http, "kpi.create", "KpiDefinition", kpi.Id, null, "Created KPI", "{}", JsonSerializer.Serialize(kpi), ct);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/kpis/{kpi.Id}", KpiDto.From(kpi));
        });

        app.MapPut("/api/kpis/{kpiId:guid}", async (
            Guid kpiId,
            UpsertKpiRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var kpi = await db.KpiDefinitions.FirstOrDefaultAsync(x => x.Id == kpiId, ct);
            if (kpi is null)
            {
                return Results.NotFound();
            }

            var before = JsonSerializer.Serialize(kpi);
            kpi.Name = request.Name;
            kpi.Category = request.Category;
            kpi.Formula = request.Formula;
            kpi.Unit = request.Unit;
            kpi.CurrentValue = request.CurrentValue;
            kpi.TargetValue = request.TargetValue;
            kpi.IsPinned = request.IsPinned;
            kpi.Status = FinancialMath.KpiStatus(request.CurrentValue, request.TargetValue, request.HigherIsBetter);
            await EndpointHelpers.AuditAsync(db, http, "kpi.update", "KpiDefinition", kpi.Id, null, "Updated KPI", before, JsonSerializer.Serialize(kpi), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(KpiDto.From(kpi));
        });

        app.MapPost("/api/formulas/evaluate", async (
            EvaluateFormulaRequest request,
            AppDbContext db,
            CancellationToken ct) =>
        {
            try
            {
                var result = await EvaluateFormulaAsync(db, request.OrganizationId, request.ReportingPeriodId, request.Formula, ct);
                return Results.Ok(result);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/api/kpi-alerts", async (Guid? organizationId, AppDbContext db, CancellationToken ct) =>
        {
            var query = db.KpiAlerts
                .AsNoTracking()
                .Include(x => x.KpiDefinition)
                .AsQueryable();

            if (organizationId is not null)
            {
                query = query.Where(x => x.KpiDefinition != null && x.KpiDefinition.OrganizationId == organizationId);
            }

            var alerts = await query.OrderByDescending(x => x.IsActive).ThenBy(x => x.KpiDefinition!.Name).ToListAsync(ct);
            return Results.Ok(alerts.Select(KpiAlertDto.From));
        });

        app.MapPost("/api/kpi-alerts", async (
            UpsertKpiAlertRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var kpi = await db.KpiDefinitions.FirstOrDefaultAsync(x => x.Id == request.KpiDefinitionId, ct);
            if (kpi is null)
            {
                return Results.NotFound();
            }

            var alert = new KpiAlert
            {
                Id = Guid.NewGuid(),
                KpiDefinitionId = kpi.Id,
                Direction = NormalizeAlertDirection(request.Direction),
                ThresholdValue = request.ThresholdValue,
                Severity = string.IsNullOrWhiteSpace(request.Severity) ? "Medium" : request.Severity,
                Message = string.IsNullOrWhiteSpace(request.Message) ? $"{kpi.Name} crossed {request.ThresholdValue:n1}{kpi.Unit}." : request.Message,
                IsActive = request.IsActive
            };
            if (IsKpiAlertTriggered(kpi.CurrentValue, alert.Direction, alert.ThresholdValue))
            {
                alert.LastTriggeredAt = DateTimeOffset.UtcNow;
            }

            db.KpiAlerts.Add(alert);
            await EndpointHelpers.AuditAsync(db, http, "kpi-alert.create", "KpiAlert", alert.Id, null, "Created KPI alert", "{}", JsonSerializer.Serialize(SafeKpiAlertAudit(alert)), ct);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/kpi-alerts/{alert.Id}", KpiAlertDto.From(alert, kpi));
        });

        app.MapPut("/api/kpi-alerts/{alertId:guid}", async (
            Guid alertId,
            UpsertKpiAlertRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var alert = await db.KpiAlerts.Include(x => x.KpiDefinition).FirstOrDefaultAsync(x => x.Id == alertId, ct);
            if (alert is null || alert.KpiDefinition is null)
            {
                return Results.NotFound();
            }

            var before = JsonSerializer.Serialize(SafeKpiAlertAudit(alert));
            alert.Direction = NormalizeAlertDirection(request.Direction);
            alert.ThresholdValue = request.ThresholdValue;
            alert.Severity = string.IsNullOrWhiteSpace(request.Severity) ? alert.Severity : request.Severity;
            alert.Message = string.IsNullOrWhiteSpace(request.Message) ? alert.Message : request.Message;
            alert.IsActive = request.IsActive;
            alert.UpdatedAt = DateTimeOffset.UtcNow;
            if (alert.IsActive && IsKpiAlertTriggered(alert.KpiDefinition.CurrentValue, alert.Direction, alert.ThresholdValue))
            {
                alert.LastTriggeredAt = DateTimeOffset.UtcNow;
            }

            await EndpointHelpers.AuditAsync(db, http, "kpi-alert.update", "KpiAlert", alert.Id, null, "Updated KPI alert", before, JsonSerializer.Serialize(SafeKpiAlertAudit(alert)), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(KpiAlertDto.From(alert));
        });

        return app;
    }

    // --- Local copies of Program.cs top-level helpers ---
    // TODO: remove once Program.cs is fully drained and helpers are promoted to a shared service.

    private static string NormalizeAlertDirection(string? direction)
        => string.Equals(direction, "Above", StringComparison.OrdinalIgnoreCase) ? "Above" : "Below";

    private static bool IsKpiAlertTriggered(decimal currentValue, string direction, decimal threshold)
        => string.Equals(direction, "Above", StringComparison.OrdinalIgnoreCase)
            ? currentValue > threshold
            : currentValue < threshold;

    private static object SafeKpiAlertAudit(KpiAlert alert)
        => new
        {
            alert.Id,
            alert.KpiDefinitionId,
            alert.Direction,
            alert.ThresholdValue,
            alert.Severity,
            alert.Message,
            alert.IsActive,
            alert.LastTriggeredAt,
            alert.CreatedAt,
            alert.UpdatedAt
        };

    private static async Task<FormulaEvaluationDto> EvaluateFormulaAsync(AppDbContext db, Guid organizationId, Guid reportingPeriodId, string formula, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(formula))
        {
            throw new ArgumentException("Formula is required.");
        }

        var dependencies = new List<string>();
        var fsLines = await db.FinancialStatementLines
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId)
            .ToListAsync(ct);
        var fsValues = fsLines
            .GroupBy(x => x.LineName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Sum(line => line.CurrentAmount), StringComparer.OrdinalIgnoreCase);
        foreach (var rowPathGroup in fsLines.GroupBy(x => x.RowPath, StringComparer.OrdinalIgnoreCase))
        {
            fsValues.TryAdd(rowPathGroup.Key, rowPathGroup.Sum(line => line.CurrentAmount));
        }

        var kpiValues = await db.KpiDefinitions
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .ToDictionaryAsync(x => x.Name, x => x.CurrentValue, StringComparer.OrdinalIgnoreCase, ct);
        var metricValues = await db.NonFinancialMetrics
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId)
            .ToDictionaryAsync(x => x.Name, x => x.CurrentValue, StringComparer.OrdinalIgnoreCase, ct);

        var normalized = Regex.Replace(
            formula,
            """(?<fn>fs|kpi|metric)\(\s*["'](?<name>[^"']+)["']\s*\)""",
            match =>
            {
                var name = match.Groups["name"].Value.Trim();
                var fn = match.Groups["fn"].Value.ToLowerInvariant();
                var value = fn switch
                {
                    "fs" when fsValues.TryGetValue(name, out var fsValue) => fsValue,
                    "kpi" when kpiValues.TryGetValue(name, out var kpiValue) => kpiValue,
                    "metric" when metricValues.TryGetValue(name, out var metricValue) => metricValue,
                    _ => throw new InvalidOperationException($"Unknown {fn} reference '{name}'.")
                };
                dependencies.Add($"{fn}:{name}");
                return value.ToString(CultureInfo.InvariantCulture);
            },
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (Regex.IsMatch(normalized, "[A-Za-z_]", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException("Only numbers, arithmetic operators, parentheses, and fs/kpi/metric references are supported.");
        }

        var value = FormulaMath.Evaluate(normalized);
        return new FormulaEvaluationDto(formula, normalized, value, dependencies.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
