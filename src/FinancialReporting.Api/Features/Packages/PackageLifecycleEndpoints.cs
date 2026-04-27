using System.Text.Json;
using FinancialReporting.Api.Common;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using FinancialReporting.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Features.Packages;

/// <summary>
/// Package lifecycle endpoints: create, ensure, recompile, final-review, and apply-template.
/// Migrated from Program.cs inline registrations. Cat 29.
/// </summary>
public static class PackageLifecycleEndpoints
{
    public static IEndpointRouteBuilder MapPackageLifecycleEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /api/packages — create a new report package shell.
        app.MapPost("/api/packages", async (
            CreatePackageRequest request,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var org = await db.Organizations.FirstOrDefaultAsync(x => x.Key == request.OrganizationKey, ct);
            var period = await db.ReportingPeriods.FirstOrDefaultAsync(x => x.Key == request.PeriodKey, ct);
            if (org is null || period is null)
            {
                return Results.BadRequest(new { error = "OrganizationKey and PeriodKey must exist." });
            }

            var package = new ReportPackage
            {
                Id = Guid.NewGuid(),
                OrganizationId = org.Id,
                ReportingPeriodId = period.Id,
                Status = PackageStatus.Draft,
                VersionLabel = "v1",
                BaseFrom = request.BaseFrom ?? "",
                // Auto-link to the most recent prior package for this org so the AI baseline diff
                // engine has something to compare against. Cat 19.
                PriorPackageId = await ResolvePriorPackageIdAsync(db, org.Id, period.PeriodEnd, ct),
                ThemeJson = JsonSerializer.Serialize(new { primary = org.PrimaryColor, accent = org.AccentColor, org.CoverStyle })
            };

            db.ReportPackages.Add(package);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/packages/{package.Id}", new { package.Id });
        });

        // POST /api/packages/ensure — idempotent create-or-fetch a package for an org+period pair.
        app.MapPost("/api/packages/ensure", async (
            CreatePackageRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var org = await db.Organizations.FirstOrDefaultAsync(x => x.Key == request.OrganizationKey, ct);
            var period = await db.ReportingPeriods.FirstOrDefaultAsync(x => x.Key == request.PeriodKey, ct);
            if (org is null || period is null)
            {
                return Results.BadRequest(new { error = "OrganizationKey and PeriodKey must exist." });
            }

            var package = await db.ReportPackages
                .Include(x => x.Organization)
                .Include(x => x.ReportingPeriod)
                .Include(x => x.Slides.OrderBy(s => s.SortOrder))
                    .ThenInclude(s => s.Blocks.OrderBy(b => b.SortOrder))
                .Include(x => x.Issues)
                .FirstOrDefaultAsync(x => x.OrganizationId == org.Id && x.ReportingPeriodId == period.Id, ct);

            if (package is null)
            {
                package = new ReportPackage
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = org.Id,
                    Organization = org,
                    ReportingPeriodId = period.Id,
                    ReportingPeriod = period,
                    Status = PackageStatus.Draft,
                    VersionLabel = "v1",
                    BaseFrom = request.BaseFrom ?? EndpointHelpers.BuildBaseFrom(period),
                    // Auto-link to the most recent prior package so the diff engine can carry
                    // narrative forward and emit keep/modify/add/remove decisions. Cat 19.
                    PriorPackageId = await ResolvePriorPackageIdAsync(db, org.Id, period.PeriodEnd, ct),
                    ThemeJson = JsonSerializer.Serialize(new { primary = org.PrimaryColor, accent = org.AccentColor, org.CoverStyle })
                };
                db.ReportPackages.Add(package);
                await EndpointHelpers.AuditAsync(db, http, "package.create", "ReportPackage", package.Id, package.Id, "Created empty package shell", "{}", JsonSerializer.Serialize(new { organizationKey = org.Key, periodKey = period.Key }), ct);
                await db.SaveChangesAsync(ct);
            }

            package = await db.ReportPackages
                .AsNoTracking()
                .Include(x => x.Organization)
                .Include(x => x.ReportingPeriod)
                .Include(x => x.Slides.OrderBy(s => s.SortOrder))
                    .ThenInclude(s => s.Blocks.OrderBy(b => b.SortOrder))
                .Include(x => x.Issues)
                .FirstAsync(x => x.Id == package.Id, ct);

            return Results.Ok(PackageDto.From(package));
        });

        // POST /api/packages/{packageId:guid}/recompile — re-sync from Xero and rebuild the package.
        app.MapPost("/api/packages/{packageId:guid}/recompile", async (
            Guid packageId,
            HttpContext http,
            AppDbContext db,
            XeroIntegrationService xero,
            PackageSnapshotBuilder snapshotBuilder,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var package = await db.ReportPackages.FirstOrDefaultAsync(x => x.Id == packageId, ct);
            if (package is null)
            {
                return Results.NotFound();
            }

            package.Status = PackageStatus.Syncing;
            await db.SaveChangesAsync(ct);

            var before = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct);
            var run = await xero.SyncPackageAsync(db, packageId, ct);
            db.PackageVersions.Add(new PackageVersion
            {
                Id = Guid.NewGuid(),
                ReportPackageId = package.Id,
                VersionLabel = $"Recompile {DateTimeOffset.UtcNow:HH:mm}",
                CreatedBy = EndpointHelpers.Actor(http),
                ChangeSummary = "Recompiled package from Xero source data",
                SnapshotJson = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct)
            });
            await EndpointHelpers.AuditAsync(db, http, "xero.sync", "ReportPackage", packageId, packageId, "Recompile package from Xero source data", before, JsonSerializer.Serialize(run), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { package.Id, Status = package.Status.ToString(), package.LastXeroSyncAt, syncRun = run });
        });

        // POST /api/packages/{packageId:guid}/final-review — queue an AI final-review run.
        app.MapPost("/api/packages/{packageId:guid}/final-review", async (
            Guid packageId,
            HttpContext http,
            AppDbContext db,
            PackageSnapshotBuilder snapshotBuilder,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }

            var packageExists = await db.ReportPackages.AnyAsync(x => x.Id == packageId, ct);
            if (!packageExists)
            {
                return Results.NotFound();
            }

            var setting = await db.AiRuntimeSettings.FirstAsync(x => x.Module == "final-review", ct);
            var snapshot = await snapshotBuilder.BuildFinalReviewSnapshotAsync(packageId, ct);
            var run = new AiRun
            {
                Id = Guid.NewGuid(),
                ReportPackageId = packageId,
                Module = "final-review",
                PromptProfile = setting.Profile,
                Model = setting.Model,
                ReasoningEffort = setting.ReasoningEffort,
                InputJson = snapshot
            };
            db.AiRuns.Add(run);
            await EndpointHelpers.AuditAsync(db, http, "ai.final-review.queue", "AiRun", run.Id, packageId, "Queued final AI review", "{}", snapshot, ct);
            await db.SaveChangesAsync(ct);
            return Results.Accepted($"/api/ai/runs/{run.Id}", AiRunDto.From(run));
        });

        // POST /api/packages/{packageId:guid}/apply-template — stamp a report template's section
        // set onto the package, creating slides for any sections not already present.
        app.MapPost("/api/packages/{packageId:guid}/apply-template", async (
            Guid packageId,
            ApplyReportTemplateRequest request,
            HttpContext http,
            AppDbContext db,
            PackageSnapshotBuilder snapshotBuilder,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            await EnsureReportTemplatesAsync(db, ct);
            var package = await db.ReportPackages
                .Include(x => x.Slides)
                    .ThenInclude(x => x.Blocks)
                .FirstOrDefaultAsync(x => x.Id == packageId, ct);
            var template = await db.ReportTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.TemplateId, ct);
            if (package is null || template is null)
            {
                return Results.NotFound();
            }

            var before = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct);
            var sections = ReadStringArray(template.SectionsJson);
            var existingSubjects = package.Slides.Select(x => x.Subject).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nextSortOrder = package.Slides.Count == 0 ? 1 : package.Slides.Max(x => x.SortOrder) + 1;
            foreach (var section in sections.Where(x => !existingSubjects.Contains(x)))
            {
                var slide = new PackageSlide
                {
                    Id = Guid.NewGuid(),
                    ReportPackageId = package.Id,
                    SortOrder = nextSortOrder++,
                    Subject = section,
                    KpiLabel = section,
                    CurrentValue = 0m,
                    PriorValue = 0m,
                    VarianceAmount = 0m,
                    VariancePercent = 0m,
                    AccountCodesCsv = "",
                    MonthlyJson = "[]",
                    PriorMonthlyJson = "[]",
                    ChartConfigJson = JsonSerializer.Serialize(new { type = "template", source = template.Name }),
                    Blocks =
                    [
                        new SlideBlock { Id = Guid.NewGuid(), SortOrder = 1, Kind = "text", ContentJson = JsonSerializer.Serialize(new { text = $"{section} generated from {template.Name}." }) },
                        new SlideBlock { Id = Guid.NewGuid(), SortOrder = 2, Kind = "table", ContentJson = JsonSerializer.Serialize(new { source = "template" }) }
                    ]
                };
                db.PackageSlides.Add(slide);
            }

            package.UpdatedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AddVersionAndAuditAsync(db, http, snapshotBuilder, packageId, "report-template.apply", "ReportPackage", package.Id, $"Applied template {template.Name}", before, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(PackageDto.From(await db.ReportPackages
                .AsNoTracking()
                .Include(x => x.Organization)
                .Include(x => x.ReportingPeriod)
                .Include(x => x.Slides.OrderBy(s => s.SortOrder))
                    .ThenInclude(s => s.Blocks.OrderBy(b => b.SortOrder))
                .Include(x => x.Issues)
                .FirstAsync(x => x.Id == packageId, ct)));
        });

        return app;
    }

    // --- Local copies of helpers still in Program.cs; remove once Program.cs is fully drained. ---

    /// <summary>
    /// LOCAL COPY from Program.cs — ResolvePriorPackageIdAsync.
    /// Finds the most recent prior package for the same organization (by PeriodEnd).
    /// </summary>
    private static async Task<Guid?> ResolvePriorPackageIdAsync(
        AppDbContext db,
        Guid organizationId,
        DateOnly currentPeriodEnd,
        CancellationToken ct)
    {
        var prior = await db.ReportPackages
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId
                        && x.ReportingPeriod!.PeriodEnd < currentPeriodEnd)
            .OrderByDescending(x => x.ReportingPeriod!.PeriodEnd)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(ct);
        return prior;
    }

    /// <summary>
    /// LOCAL COPY from Program.cs — EnsureReportTemplatesAsync.
    /// Seeds built-in report templates on first access.
    /// </summary>
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

    /// <summary>
    /// LOCAL COPY from Program.cs — BuiltInReportTemplates.
    /// </summary>
    private static IEnumerable<(string Name, string Category, string Description, string[] Sections)> BuiltInReportTemplates()
        =>
        [
            ("Executive board pack", "Management reporting", "Monthly board package with KPI scorecard, statements, flux review, action plan, and appendix.", ["Executive Summary", "KPI Scorecard", "Profit & Loss", "Trended P&L", "Balance Sheet", "Cash Flow", "Flux Review", "Action Plan", "Appendix"]),
            ("Best-in-class reporting pack", "Management reporting", "Fathom/Syft-style customizable package with statements, visuals, commentary, quality checks, and evidence.", ["Executive Summary", "KPI Scorecard", "Profit & Loss", "Trended P&L", "Balance Sheet", "Cash Flow", "Waterfall Bridge", "Common-size Analysis", "Driver Tree", "Flux Review", "Final Review Summary", "Action Plan", "Ledger Evidence", "Data Quality Dashboard", "Appendix"]),
            ("3-way forecast pack", "Planning", "Forward-looking package with P&L, balance sheet, cash flow, scenario comparison, and assumptions.", ["Forecast Summary", "P&L Forecast", "Balance Sheet Forecast", "Cash Flow Forecast", "Scenario Comparison", "Assumptions"]),
            ("Consolidation pack", "Consolidation", "Group reporting pack for entity drilldown, side-by-side financials, eliminations, and FX review.", ["Group Summary", "Side-by-side Financials", "Entity Drilldown", "Eliminations", "FX Rates", "Consolidated Forecast"]),
            ("Advisory dashboard", "Client portal", "Interactive dashboard for goals, alerts, non-financial drivers, and AI narrative.", ["Goals", "KPI Alerts", "Non-financial Metrics", "AI Commentary", "Action Plan"])
        ];

    /// <summary>
    /// LOCAL COPY from Program.cs — ReadStringArray.
    /// </summary>
    private static string[] ReadStringArray(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}
