using System.Globalization;
using System.Text.Json;
using FinancialReporting.Api.Common;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using FinancialReporting.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Features.Planning;

/// <summary>
/// Planning, forecasting, FX rates, non-financial metrics, cash timing, benchmarking,
/// and report templates. Extracted from Program.cs. Cat 47.
/// </summary>
public static class PlanningEndpoints
{
    public static IEndpointRouteBuilder MapPlanningEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Non-financial metrics ────────────────────────────────────────────────────────
        app.MapGet("/api/non-financial-metrics", async (
            Guid organizationId,
            string? periodKey,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var query = db.NonFinancialMetrics.AsNoTracking().Where(x => x.OrganizationId == organizationId);
            if (!string.IsNullOrWhiteSpace(periodKey))
            {
                query = query.Join(db.ReportingPeriods.AsNoTracking().Where(x => x.Key == periodKey), metric => metric.ReportingPeriodId, period => period.Id, (metric, _) => metric);
            }

            var metrics = await query.OrderByDescending(x => x.IsPinned).ThenBy(x => x.Category).ThenBy(x => x.Name).ToListAsync(ct);
            return Results.Ok(metrics.Select(NonFinancialMetricDto.From));
        });

        app.MapPost("/api/non-financial-metrics", async (
            UpsertNonFinancialMetricRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var metric = new NonFinancialMetric
            {
                Id = Guid.NewGuid(),
                OrganizationId = request.OrganizationId,
                ReportingPeriodId = request.ReportingPeriodId,
                Name = request.Name,
                Category = request.Category,
                Unit = request.Unit,
                CurrentValue = request.CurrentValue,
                PriorValue = request.PriorValue,
                TargetValue = request.TargetValue,
                ValuesJson = request.ValuesJson ?? "[]",
                Source = string.IsNullOrWhiteSpace(request.Source) ? "Manual datasheet" : request.Source,
                IsPinned = request.IsPinned
            };
            db.NonFinancialMetrics.Add(metric);
            await EndpointHelpers.AuditAsync(db, http, "non-financial-metric.create", "NonFinancialMetric", metric.Id, null, "Created non-financial metric", "{}", JsonSerializer.Serialize(metric), ct);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/non-financial-metrics/{metric.Id}", NonFinancialMetricDto.From(metric));
        });

        app.MapPut("/api/non-financial-metrics/{metricId:guid}", async (
            Guid metricId,
            UpsertNonFinancialMetricRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var metric = await db.NonFinancialMetrics.FirstOrDefaultAsync(x => x.Id == metricId, ct);
            if (metric is null)
            {
                return Results.NotFound();
            }

            var before = JsonSerializer.Serialize(metric);
            metric.Name = request.Name;
            metric.Category = request.Category;
            metric.Unit = request.Unit;
            metric.CurrentValue = request.CurrentValue;
            metric.PriorValue = request.PriorValue;
            metric.TargetValue = request.TargetValue;
            metric.ValuesJson = request.ValuesJson ?? metric.ValuesJson;
            metric.Source = string.IsNullOrWhiteSpace(request.Source) ? metric.Source : request.Source;
            metric.IsPinned = request.IsPinned;
            metric.UpdatedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AuditAsync(db, http, "non-financial-metric.update", "NonFinancialMetric", metric.Id, null, "Updated non-financial metric", before, JsonSerializer.Serialize(metric), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(NonFinancialMetricDto.From(metric));
        });

        // ── FX rates ─────────────────────────────────────────────────────────────────────
        app.MapGet("/api/fx-rates", async (
            Guid organizationId,
            string? periodKey,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var period = string.IsNullOrWhiteSpace(periodKey)
                ? await db.ReportingPeriods.AsNoTracking().OrderByDescending(x => x.PeriodStart).FirstOrDefaultAsync(ct)
                : await db.ReportingPeriods.AsNoTracking().FirstOrDefaultAsync(x => x.Key == periodKey, ct);
            if (period is null)
            {
                return Results.Ok(Array.Empty<FxRateDto>());
            }

            await EnsureFxDefaultsAsync(db, organizationId, period.Id, ct);
            var rates = await db.FxRates
                .AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.ReportingPeriodId == period.Id)
                .OrderBy(x => x.CurrencyCode)
                .ToListAsync(ct);
            return Results.Ok(rates.Select(FxRateDto.From));
        });

        app.MapPost("/api/fx-rates", async (
            UpsertFxRateRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var rate = new FxRate
            {
                Id = Guid.NewGuid(),
                OrganizationId = request.OrganizationId,
                ReportingPeriodId = request.ReportingPeriodId,
                CurrencyCode = NormalizeCurrencyCode(request.CurrencyCode),
                RateToPresentation = request.RateToPresentation <= 0m ? 1m : request.RateToPresentation,
                Source = string.IsNullOrWhiteSpace(request.Source) ? "Manual" : request.Source.Trim()
            };
            db.FxRates.Add(rate);
            await EndpointHelpers.AuditAsync(db, http, "fx-rate.create", "FxRate", rate.Id, null, "Created FX rate", "{}", JsonSerializer.Serialize(rate), ct);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/fx-rates/{rate.Id}", FxRateDto.From(rate));
        });

        app.MapPut("/api/fx-rates/{rateId:guid}", async (
            Guid rateId,
            UpsertFxRateRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var rate = await db.FxRates.FirstOrDefaultAsync(x => x.Id == rateId, ct);
            if (rate is null)
            {
                return Results.NotFound();
            }

            var before = JsonSerializer.Serialize(rate);
            rate.CurrencyCode = NormalizeCurrencyCode(request.CurrencyCode);
            rate.RateToPresentation = request.RateToPresentation <= 0m ? 1m : request.RateToPresentation;
            rate.Source = string.IsNullOrWhiteSpace(request.Source) ? "Manual" : request.Source.Trim();
            rate.UpdatedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AuditAsync(db, http, "fx-rate.update", "FxRate", rate.Id, null, "Updated FX rate", before, JsonSerializer.Serialize(rate), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(FxRateDto.From(rate));
        });

        // ── Planning overview ────────────────────────────────────────────────────────────
        app.MapGet("/api/planning/{packageId:guid}/overview", async (
            Guid packageId,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var package = await db.ReportPackages
                .AsNoTracking()
                .Include(x => x.Organization)
                .Include(x => x.ReportingPeriod)
                .FirstOrDefaultAsync(x => x.Id == packageId, ct);
            if (package is null || package.ReportingPeriod is null)
            {
                return Results.NotFound();
            }

            await EnsurePlanningDefaultsAsync(db, package.OrganizationId, package.ReportingPeriodId, ct);
            var scenarios = await db.ForecastScenarios
                .AsNoTracking()
                .Include(x => x.Events.OrderBy(e => e.MonthOffset))
                .Where(x => x.OrganizationId == package.OrganizationId && x.ReportingPeriodId == package.ReportingPeriodId)
                .OrderByDescending(x => x.IsBase)
                .ThenBy(x => x.Name)
                .ToListAsync(ct);
            var actuals = await BuildForecastActualsAsync(db, package.OrganizationId, package.ReportingPeriodId, ct);
            var budgetRows = await BuildBudgetVarianceRowsAsync(db, package.OrganizationId, package.ReportingPeriodId, scenarios.FirstOrDefault(), ct);
            var metrics = await db.NonFinancialMetrics
                .AsNoTracking()
                .Where(x => x.OrganizationId == package.OrganizationId && x.ReportingPeriodId == package.ReportingPeriodId)
                .OrderByDescending(x => x.IsPinned)
                .ThenBy(x => x.Name)
                .ToListAsync(ct);

            var overview = new PlanningOverviewDto(
                package.Id,
                package.OrganizationId,
                package.Organization?.Name ?? "",
                package.ReportingPeriod.Key,
                actuals.MonthlyRevenue,
                actuals.MonthlyOperatingExpense,
                actuals.StartMonth,
                scenarios.Select(x => ForecastScenarioDto.From(x, actuals)).ToList(),
                budgetRows,
                metrics.Select(NonFinancialMetricDto.From).ToList());
            return Results.Ok(overview);
        });

        // ── Cash timing ──────────────────────────────────────────────────────────────────
        app.MapGet("/api/planning/{packageId:guid}/cash-timing", async (
            Guid packageId,
            Guid? scenarioId,
            string? granularity,
            int? months,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var package = await db.ReportPackages
                .AsNoTracking()
                .Include(x => x.ReportingPeriod)
                .FirstOrDefaultAsync(x => x.Id == packageId, ct);
            if (package is null)
            {
                return Results.NotFound();
            }

            await EnsurePlanningDefaultsAsync(db, package.OrganizationId, package.ReportingPeriodId, ct);
            var scenarioQuery = db.ForecastScenarios
                .AsNoTracking()
                .Include(x => x.Events.OrderBy(e => e.MonthOffset))
                .Where(x => x.OrganizationId == package.OrganizationId && x.ReportingPeriodId == package.ReportingPeriodId);
            var scenario = scenarioId is not null
                ? await scenarioQuery.FirstOrDefaultAsync(x => x.Id == scenarioId, ct)
                : await scenarioQuery.OrderByDescending(x => x.IsBase).ThenBy(x => x.Name).FirstOrDefaultAsync(ct);
            if (scenario is null)
            {
                return Results.NotFound();
            }

            var actuals = await BuildForecastActualsAsync(db, package.OrganizationId, package.ReportingPeriodId, ct);
            var dto = ForecastScenarioDto.From(scenario, actuals);
            var rows = BuildCashTimingRows(dto, granularity, months);
            return Results.Ok(new CashTimingDto(packageId, dto.Id, NormalizeCashGranularity(granularity), rows));
        });

        // ── Forecast scenarios ───────────────────────────────────────────────────────────
        app.MapPost("/api/planning/scenarios", async (
            UpsertForecastScenarioRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var scenario = new ForecastScenario
            {
                Id = Guid.NewGuid(),
                OrganizationId = request.OrganizationId,
                ReportingPeriodId = request.ReportingPeriodId,
                Name = request.Name,
                Description = request.Description ?? "",
                ScenarioType = request.ScenarioType ?? "Custom",
                HorizonMonths = Math.Clamp(request.HorizonMonths, 1, 36),
                RevenueGrowthPercent = request.RevenueGrowthPercent,
                GrossMarginPercent = request.GrossMarginPercent,
                OpexGrowthPercent = request.OpexGrowthPercent,
                CashConversionPercent = request.CashConversionPercent,
                StartingCash = request.StartingCash,
                CashThreshold = request.CashThreshold,
                AssumptionsJson = request.AssumptionsJson ?? "[]",
                IsBase = request.IsBase
            };
            db.ForecastScenarios.Add(scenario);
            await EndpointHelpers.AuditAsync(db, http, "forecast-scenario.create", "ForecastScenario", scenario.Id, null, "Created forecast scenario", "{}", JsonSerializer.Serialize(SafeForecastScenarioAudit(scenario)), ct);
            await db.SaveChangesAsync(ct);

            var actuals = await BuildForecastActualsAsync(db, scenario.OrganizationId, scenario.ReportingPeriodId, ct);
            return Results.Created($"/api/planning/scenarios/{scenario.Id}", ForecastScenarioDto.From(scenario, actuals));
        });

        app.MapPut("/api/planning/scenarios/{scenarioId:guid}", async (
            Guid scenarioId,
            UpsertForecastScenarioRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var scenario = await db.ForecastScenarios.Include(x => x.Events).FirstOrDefaultAsync(x => x.Id == scenarioId, ct);
            if (scenario is null)
            {
                return Results.NotFound();
            }

            var before = JsonSerializer.Serialize(SafeForecastScenarioAudit(scenario));
            scenario.Name = request.Name;
            scenario.Description = request.Description ?? "";
            scenario.ScenarioType = request.ScenarioType ?? scenario.ScenarioType;
            scenario.HorizonMonths = Math.Clamp(request.HorizonMonths, 1, 36);
            scenario.RevenueGrowthPercent = request.RevenueGrowthPercent;
            scenario.GrossMarginPercent = request.GrossMarginPercent;
            scenario.OpexGrowthPercent = request.OpexGrowthPercent;
            scenario.CashConversionPercent = request.CashConversionPercent;
            scenario.StartingCash = request.StartingCash;
            scenario.CashThreshold = request.CashThreshold;
            scenario.AssumptionsJson = request.AssumptionsJson ?? scenario.AssumptionsJson;
            scenario.IsBase = request.IsBase;
            scenario.UpdatedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AuditAsync(db, http, "forecast-scenario.update", "ForecastScenario", scenario.Id, null, "Updated forecast scenario", before, JsonSerializer.Serialize(SafeForecastScenarioAudit(scenario)), ct);
            await db.SaveChangesAsync(ct);

            var actuals = await BuildForecastActualsAsync(db, scenario.OrganizationId, scenario.ReportingPeriodId, ct);
            return Results.Ok(ForecastScenarioDto.From(scenario, actuals));
        });

        // ── Forecast events ──────────────────────────────────────────────────────────────
        app.MapPost("/api/planning/scenarios/{scenarioId:guid}/events", async (
            Guid scenarioId,
            UpsertForecastEventRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var scenario = await db.ForecastScenarios.FirstOrDefaultAsync(x => x.Id == scenarioId, ct);
            if (scenario is null)
            {
                return Results.NotFound();
            }

            var forecastEvent = new ForecastEvent
            {
                Id = Guid.NewGuid(),
                ForecastScenarioId = scenario.Id,
                MonthOffset = Math.Clamp(request.MonthOffset, 1, 36),
                Name = request.Name,
                Category = request.Category ?? "Microforecast",
                RevenueImpact = request.RevenueImpact,
                ExpenseImpact = request.ExpenseImpact,
                CashImpact = request.CashImpact,
                IsRecurring = request.IsRecurring,
                Notes = request.Notes ?? ""
            };
            db.ForecastEvents.Add(forecastEvent);
            scenario.UpdatedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AuditAsync(db, http, "forecast-event.create", "ForecastEvent", forecastEvent.Id, null, "Created forecast micro-event", "{}", JsonSerializer.Serialize(SafeForecastEventAudit(forecastEvent)), ct);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/planning/scenarios/{scenario.Id}/events/{forecastEvent.Id}", ForecastEventDto.From(forecastEvent));
        });

        // ── Benchmarking ─────────────────────────────────────────────────────────────────
        app.MapGet("/api/benchmarking", async (
            string? periodKey,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var selectedPeriod = string.IsNullOrWhiteSpace(periodKey)
                ? await db.ReportingPeriods.AsNoTracking().OrderByDescending(x => x.PeriodStart).FirstOrDefaultAsync(ct)
                : await db.ReportingPeriods.AsNoTracking().FirstOrDefaultAsync(x => x.Key == periodKey, ct);
            if (selectedPeriod is null)
            {
                return Results.Ok(new BenchmarkingDto("", []));
            }

            var organizations = await db.Organizations.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
            var rows = new List<BenchmarkRowDto>();
            foreach (var organization in organizations)
            {
                var package = await db.ReportPackages
                    .AsNoTracking()
                    .Include(x => x.Issues)
                    .FirstOrDefaultAsync(x => x.OrganizationId == organization.Id && x.ReportingPeriodId == selectedPeriod.Id, ct);
                var rollup = await BuildBenchmarkRollupAsync(db, organization.Id, selectedPeriod.Id, ct);
                var kpis = await db.KpiDefinitions
                    .AsNoTracking()
                    .Where(x => x.OrganizationId == organization.Id)
                    .OrderByDescending(x => x.IsPinned)
                    .ThenBy(x => x.Name)
                    .Take(6)
                    .ToListAsync(ct);
                rows.Add(new BenchmarkRowDto(
                    organization.Id,
                    organization.Name,
                    organization.Abbreviation,
                    organization.IsConsolidated,
                    package?.Id,
                    package?.Status.ToString() ?? "No package",
                    rollup.Revenue,
                    rollup.Expense,
                    rollup.Net,
                    rollup.GrossMarginPercent,
                    package?.Issues.Count(x => x.Status == IssueStatus.Open) ?? 0,
                    kpis.Select(KpiDto.From).ToList()));
            }

            var ranked = rows
                .OrderByDescending(x => x.NetIncome)
                .Select((row, index) => row with { Rank = index + 1 })
                .ToList();
            return Results.Ok(new BenchmarkingDto(selectedPeriod.Key, ranked));
        });

        // ── Report templates ──────────────────────────────────────────────────────────────
        app.MapGet("/api/report-templates", async (AppDbContext db, CancellationToken ct) =>
        {
            await EnsureReportTemplatesAsync(db, ct);
            var templates = await db.ReportTemplates.AsNoTracking().OrderBy(x => x.Category).ThenBy(x => x.Name).ToListAsync(ct);
            return Results.Ok(templates.Select(ReportTemplateDto.From));
        });

        return app;
    }

    // ── Private helpers (copied from Program.cs; dedup when Program.cs entries are removed) ──

    /// <summary>Normalizes a raw currency string to a 3-letter ISO code, defaulting to USD.</summary>
    private static string NormalizeCurrencyCode(string value)
    {
        var normalized = new string(value.Where(char.IsLetter).Take(3).ToArray()).ToUpperInvariant();
        return normalized.Length == 3 ? normalized : "USD";
    }

    /// <summary>Seeds a default USD presentation-currency FX rate row if none exist for the org+period.</summary>
    private static async Task EnsureFxDefaultsAsync(AppDbContext db, Guid organizationId, Guid reportingPeriodId, CancellationToken ct)
    {
        if (await db.FxRates.AnyAsync(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId, ct))
        {
            return;
        }

        db.FxRates.Add(new FxRate
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ReportingPeriodId = reportingPeriodId,
            CurrencyCode = "USD",
            RateToPresentation = 1m,
            Source = "Presentation currency"
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Normalizes cash granularity to "Daily" or "Weekly".</summary>
    private static string NormalizeCashGranularity(string? granularity)
        => string.Equals(granularity, "Daily", StringComparison.OrdinalIgnoreCase) ? "Daily" : "Weekly";

    private static List<CashTimingRowDto> BuildCashTimingRows(ForecastScenarioDto scenario, string? granularity, int? months)
    {
        var mode = NormalizeCashGranularity(granularity);
        var monthLimit = Math.Clamp(months ?? 3, 1, Math.Min(12, Math.Max(1, scenario.Rows.Count)));
        var rows = new List<CashTimingRowDto>();
        foreach (var month in scenario.Rows.Take(monthLimit))
        {
            var parts = month.MonthKey.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var year = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var monthNumber = int.Parse(parts[1], CultureInfo.InvariantCulture);
            var start = new DateOnly(year, monthNumber, 1);
            var days = DateTime.DaysInMonth(year, monthNumber);
            var periodCount = mode == "Daily" ? days : (int)Math.Ceiling(days / 7m);
            var startingCash = month.EndingCash - month.NetCashFlow;
            var runningCash = startingCash;
            for (var period = 0; period < periodCount; period++)
            {
                var periodStart = mode == "Daily" ? start.AddDays(period) : start.AddDays(period * 7);
                var periodEnd = mode == "Daily" ? periodStart : MinDate(start.AddDays(days - 1), periodStart.AddDays(6));
                var inflow = decimal.Round(month.CashInflow / periodCount, 2);
                var outflow = decimal.Round(month.CashOutflow / periodCount, 2);
                var net = decimal.Round(inflow - outflow, 2);
                runningCash += net;
                var label = mode == "Daily" ? periodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : $"{month.MonthKey} W{period + 1}";
                rows.Add(new CashTimingRowDto(
                    label,
                    periodStart,
                    periodEnd,
                    inflow,
                    outflow,
                    net,
                    decimal.Round(runningCash, 2),
                    scenario.CashThreshold > 0m && runningCash < scenario.CashThreshold));
            }
        }

        return rows;
    }

    private static DateOnly MinDate(DateOnly left, DateOnly right)
        => left <= right ? left : right;

    private static async Task EnsurePlanningDefaultsAsync(AppDbContext db, Guid organizationId, Guid reportingPeriodId, CancellationToken ct)
    {
        if (!await db.ForecastScenarios.AnyAsync(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId, ct))
        {
            var actuals = await BuildForecastActualsAsync(db, organizationId, reportingPeriodId, ct);
            db.ForecastScenarios.AddRange(
                BuildDefaultScenario(organizationId, reportingPeriodId, "Base case", "Base", 7m, 39m, 4m, actuals.EstimatedStartingCash, actuals.CashThreshold, true),
                BuildDefaultScenario(organizationId, reportingPeriodId, "Upside case", "Upside", 12m, 41m, 5m, actuals.EstimatedStartingCash, actuals.CashThreshold, false),
                BuildDefaultScenario(organizationId, reportingPeriodId, "Downside case", "Downside", -3m, 35m, 2m, actuals.EstimatedStartingCash, actuals.CashThreshold, false));
            await db.SaveChangesAsync(ct);
        }

        if (!await db.NonFinancialMetrics.AnyAsync(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId, ct))
        {
            db.NonFinancialMetrics.AddRange(
                new NonFinancialMetric
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    ReportingPeriodId = reportingPeriodId,
                    Name = "Customer Count",
                    Category = "Operations",
                    Unit = "customers",
                    CurrentValue = 120m,
                    PriorValue = 112m,
                    TargetValue = 128m,
                    ValuesJson = JsonSerializer.Serialize(new[] { 112, 114, 115, 117, 118, 119, 120 }),
                    Source = "Manual datasheet",
                    IsPinned = true
                },
                new NonFinancialMetric
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    ReportingPeriodId = reportingPeriodId,
                    Name = "Employee Count",
                    Category = "People",
                    Unit = "FTE",
                    CurrentValue = 34m,
                    PriorValue = 31m,
                    TargetValue = 36m,
                    ValuesJson = JsonSerializer.Serialize(new[] { 31, 31, 32, 33, 33, 34, 34 }),
                    Source = "Manual datasheet",
                    IsPinned = false
                });
            await db.SaveChangesAsync(ct);
        }
    }

    private static ForecastScenario BuildDefaultScenario(
        Guid organizationId,
        Guid reportingPeriodId,
        string name,
        string scenarioType,
        decimal revenueGrowth,
        decimal grossMargin,
        decimal opexGrowth,
        decimal startingCash,
        decimal cashThreshold,
        bool isBase)
        => new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            ReportingPeriodId = reportingPeriodId,
            Name = name,
            Description = $"{scenarioType} rolling 36-month scenario generated from the current package trend.",
            ScenarioType = scenarioType,
            HorizonMonths = 36,
            RevenueGrowthPercent = revenueGrowth,
            GrossMarginPercent = grossMargin,
            OpexGrowthPercent = opexGrowth,
            CashConversionPercent = 85m,
            StartingCash = startingCash,
            CashThreshold = cashThreshold,
            AssumptionsJson = JsonSerializer.Serialize(new[] { "Rolling forecast from latest imported actuals", "Working-capital conversion uses a simplified driver", "Edit assumptions as the board plan changes" }),
            IsBase = isBase
        };

    private static object SafeForecastScenarioAudit(ForecastScenario scenario)
        => new
        {
            scenario.Id,
            scenario.OrganizationId,
            scenario.ReportingPeriodId,
            scenario.Name,
            scenario.Description,
            scenario.ScenarioType,
            scenario.HorizonMonths,
            scenario.RevenueGrowthPercent,
            scenario.GrossMarginPercent,
            scenario.OpexGrowthPercent,
            scenario.CashConversionPercent,
            scenario.StartingCash,
            scenario.CashThreshold,
            scenario.AssumptionsJson,
            scenario.IsBase,
            scenario.CreatedAt,
            scenario.UpdatedAt,
            Events = scenario.Events.Select(SafeForecastEventAudit).ToList()
        };

    private static object SafeForecastEventAudit(ForecastEvent forecastEvent)
        => new
        {
            forecastEvent.Id,
            forecastEvent.ForecastScenarioId,
            forecastEvent.MonthOffset,
            forecastEvent.Name,
            forecastEvent.Category,
            forecastEvent.RevenueImpact,
            forecastEvent.ExpenseImpact,
            forecastEvent.CashImpact,
            forecastEvent.IsRecurring,
            forecastEvent.Notes,
            forecastEvent.CreatedAt
        };

    private static async Task<ForecastActuals> BuildForecastActualsAsync(AppDbContext db, Guid organizationId, Guid reportingPeriodId, CancellationToken ct)
    {
        var period = await db.ReportingPeriods.AsNoTracking().FirstAsync(x => x.Id == reportingPeriodId, ct);
        var accounts = await db.GlAccounts
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId)
            .ToListAsync(ct);
        var revenueByMonth = new decimal[12];
        var expenseByMonth = new decimal[12];

        foreach (var account in accounts)
        {
            var balances = FinancialEngine.ReadMonthlyBalances(account.MonthlyBalancesJson);
            for (var i = 0; i < Math.Min(12, balances.Length); i++)
            {
                var amount = Math.Abs(balances[i]);
                if (IsRevenueAccount(account))
                {
                    revenueByMonth[i] += amount;
                }
                else if (IsExpenseAccount(account))
                {
                    expenseByMonth[i] += amount;
                }
            }
        }

        var latestRevenue = LastNonZero(revenueByMonth);
        var latestExpense = LastNonZero(expenseByMonth);
        if (latestRevenue == 0m)
        {
            latestRevenue = await db.PackageSlides.AsNoTracking()
                .Where(x => x.ReportPackage != null && x.ReportPackage.OrganizationId == organizationId && x.ReportPackage.ReportingPeriodId == reportingPeriodId)
                .Select(x => x.CurrentValue)
                .DefaultIfEmpty()
                .AverageAsync(ct) / 12m;
        }
        if (latestExpense == 0m)
        {
            latestExpense = latestRevenue * 0.58m;
        }

        var startingCash = Math.Max(250_000m, Math.Abs(latestExpense) * 6m);
        var cashRunway = await db.KpiDefinitions
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.Name.Contains("Cash Runway"))
            .Select(x => (decimal?)x.CurrentValue)
            .FirstOrDefaultAsync(ct);
        if (cashRunway is not null && latestExpense > 0m)
        {
            startingCash = latestExpense * cashRunway.Value;
        }

        return new ForecastActuals(
            period.PeriodStart.AddMonths(1),
            decimal.Round(Math.Max(latestRevenue, 1m), 2),
            decimal.Round(Math.Max(latestExpense, 1m), 2),
            decimal.Round(startingCash, 2),
            decimal.Round(Math.Max(latestExpense * 3m, startingCash * 0.25m), 2));
    }

    private static bool IsRevenueAccount(GlAccount account)
        => account.Type.Contains("revenue", StringComparison.OrdinalIgnoreCase)
           || account.Type.Contains("income", StringComparison.OrdinalIgnoreCase)
           || account.FsLine.Contains("revenue", StringComparison.OrdinalIgnoreCase)
           || account.FsLine.Contains("income", StringComparison.OrdinalIgnoreCase);

    private static bool IsExpenseAccount(GlAccount account)
        => account.Type.Contains("expense", StringComparison.OrdinalIgnoreCase)
           || account.Type.Contains("cost", StringComparison.OrdinalIgnoreCase)
           || account.FsLine.Contains("expense", StringComparison.OrdinalIgnoreCase)
           || account.FsLine.Contains("cost", StringComparison.OrdinalIgnoreCase)
           || account.FsLine.Contains("payroll", StringComparison.OrdinalIgnoreCase);

    private static decimal LastNonZero(decimal[] values)
        => values.Reverse().FirstOrDefault(x => x != 0m);

    private static async Task<List<BudgetVarianceRowDto>> BuildBudgetVarianceRowsAsync(AppDbContext db, Guid organizationId, Guid reportingPeriodId, ForecastScenario? scenario, CancellationToken ct)
    {
        var accounts = await db.GlAccounts
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId)
            .ToListAsync(ct);
        var growth = scenario?.RevenueGrowthPercent ?? 5m;
        var expenseGrowth = scenario?.OpexGrowthPercent ?? 3m;

        return accounts
            .GroupBy(x => string.IsNullOrWhiteSpace(x.FsLine) ? "Unmapped" : x.FsLine)
            .OrderBy(x => x.Key)
            .Select(group =>
            {
                var actual = group.Sum(x => FinancialEngine.ReadMonthlyBalances(x.MonthlyBalancesJson).Sum());
                var factor = group.Any(IsRevenueAccount) ? 1m + growth / 100m : 1m + expenseGrowth / 100m;
                var budget = actual / factor;
                var variance = actual - budget;
                return new BudgetVarianceRowDto(group.Key, decimal.Round(actual, 2), decimal.Round(budget, 2), decimal.Round(variance, 2), budget == 0m ? 0m : decimal.Round(variance / budget * 100m, 1));
            })
            .ToList();
    }

    private static async Task<BenchmarkRollup> BuildBenchmarkRollupAsync(AppDbContext db, Guid organizationId, Guid reportingPeriodId, CancellationToken ct)
    {
        var accounts = await db.GlAccounts
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId)
            .ToListAsync(ct);
        var revenue = accounts.Where(IsRevenueAccount).Sum(x => FinancialEngine.ReadMonthlyBalances(x.MonthlyBalancesJson).Sum());
        var expense = accounts.Where(IsExpenseAccount).Sum(x => FinancialEngine.ReadMonthlyBalances(x.MonthlyBalancesJson).Sum());
        var net = revenue - expense;
        var margin = revenue == 0m ? 0m : decimal.Round(net / revenue * 100m, 1);
        return new BenchmarkRollup(decimal.Round(revenue, 2), decimal.Round(expense, 2), decimal.Round(net, 2), margin);
    }

    private static async Task EnsureReportTemplatesAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = await db.ReportTemplates.Select(x => x.Name).ToListAsync(ct);
        var existingNames = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var template in BuiltInReportTemplates().Where(x => !existingNames.Contains(x.Name)))
        {
            db.ReportTemplates.Add(new ReportTemplate
            {
                Id = Guid.NewGuid(),
                Name = template.Name,
                Category = template.Category,
                Description = template.Description,
                SectionsJson = JsonSerializer.Serialize(template.Sections),
                IsBuiltIn = true
            });
        }
        await db.SaveChangesAsync(ct);
    }

    private static IEnumerable<(string Name, string Category, string Description, string[] Sections)> BuiltInReportTemplates()
        =>
        [
            ("Executive board pack", "Management reporting", "Monthly board package with KPI scorecard, statements, flux review, action plan, and appendix.", ["Executive Summary", "KPI Scorecard", "Profit & Loss", "Trended P&L", "Balance Sheet", "Cash Flow", "Flux Review", "Action Plan", "Appendix"]),
            ("Best-in-class reporting pack", "Management reporting", "Fathom/Syft-style customizable package with statements, visuals, commentary, quality checks, and evidence.", ["Executive Summary", "KPI Scorecard", "Profit & Loss", "Trended P&L", "Balance Sheet", "Cash Flow", "Waterfall Bridge", "Common-size Analysis", "Driver Tree", "Flux Review", "Final Review Summary", "Action Plan", "Ledger Evidence", "Data Quality Dashboard", "Appendix"]),
            ("3-way forecast pack", "Planning", "Forward-looking package with P&L, balance sheet, cash flow, scenario comparison, and assumptions.", ["Forecast Summary", "P&L Forecast", "Balance Sheet Forecast", "Cash Flow Forecast", "Scenario Comparison", "Assumptions"]),
            ("Consolidation pack", "Consolidation", "Group reporting pack for entity drilldown, side-by-side financials, eliminations, and FX review.", ["Group Summary", "Side-by-side Financials", "Entity Drilldown", "Eliminations", "FX Rates", "Consolidated Forecast"]),
            ("Advisory dashboard", "Client portal", "Interactive dashboard for goals, alerts, non-financial drivers, and AI narrative.", ["Goals", "KPI Alerts", "Non-financial Metrics", "AI Commentary", "Action Plan"])
        ];
}
