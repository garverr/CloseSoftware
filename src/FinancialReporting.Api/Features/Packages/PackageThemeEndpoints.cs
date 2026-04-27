using System.Text.Json;
using System.Text.Json.Nodes;
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
/// Package theme and reporting-studio endpoints: PUT theme, GET/PUT/POST reporting-studio settings,
/// and GET competitive-gaps.
/// Migrated from Program.cs inline registrations. Cat 29.
/// </summary>
public static class PackageThemeEndpoints
{
    public static IEndpointRouteBuilder MapPackageThemeEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/competitive-gaps — returns the competitive feature matrix.
        app.MapGet("/api/competitive-gaps", () => Results.Ok(CompetitiveFeatureGroups()));

        // GET /api/packages/{packageId:guid}/reporting-studio — full reporting-studio snapshot.
        app.MapGet("/api/packages/{packageId:guid}/reporting-studio", async (
            Guid packageId,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var package = await db.ReportPackages
                .AsNoTracking()
                .Include(x => x.Organization)
                .Include(x => x.ReportingPeriod)
                .Include(x => x.Slides)
                .Include(x => x.Issues)
                .FirstOrDefaultAsync(x => x.Id == packageId, ct);
            if (package is null)
            {
                return Results.NotFound();
            }

            var settings = ReadReportingStudioSettings(package);
            var fsSectionRows = await db.FsLineDefinitions
                .AsNoTracking()
                .Where(x => x.OrganizationId == package.OrganizationId && x.IsActive)
                .GroupBy(x => new { x.StatementType, x.Section })
                .Select(x => new { x.Key.StatementType, x.Key.Section, LineCount = x.Count() })
                .OrderBy(x => x.StatementType)
                .ThenBy(x => x.Section)
                .ToListAsync(ct);
            var fsSections = fsSectionRows
                .Select(x => new ReportingStudioStatementSectionDto(x.StatementType, x.Section, x.LineCount))
                .ToList();

            var statementSectionRows = await db.FinancialStatementLines
                .AsNoTracking()
                .Where(x => x.OrganizationId == package.OrganizationId && x.ReportingPeriodId == package.ReportingPeriodId)
                .GroupBy(x => new { x.StatementType, x.Section })
                .Select(x => new { x.Key.StatementType, x.Key.Section, LineCount = x.Count() })
                .OrderBy(x => x.StatementType)
                .ThenBy(x => x.Section)
                .ToListAsync(ct);
            var statementSections = statementSectionRows
                .Select(x => new ReportingStudioStatementSectionDto(x.StatementType, string.IsNullOrWhiteSpace(x.Section) ? "Unsectioned" : x.Section, x.LineCount))
                .ToList();

            var qualityChecks = await BuildReportingQualityChecksAsync(db, package, fsSections, statementSections, ct);
            var score = qualityChecks.Count == 0
                ? 0
                : (int)Math.Round(qualityChecks.Count(x => x.Status == "Pass") / (decimal)qualityChecks.Count * 100m, MidpointRounding.AwayFromZero);

            return Results.Ok(new ReportingStudioDto(
                package.Id,
                package.Organization?.Name ?? "",
                package.ReportingPeriod?.Key ?? "",
                settings,
                ReportingContentLibrary(),
                fsSections,
                statementSections,
                qualityChecks,
                score,
                CompetitiveFeatureGroups()));
        });

        // PUT /api/packages/{packageId:guid}/reporting-studio — update reporting studio customization settings.
        app.MapPut("/api/packages/{packageId:guid}/reporting-studio", async (
            Guid packageId,
            ReportingStudioSettingsDto request,
            HttpContext http,
            AppDbContext db,
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
            if (EndpointHelpers.RejectIfApproved(package) is { } locked)
            {
                return locked;
            }

            var before = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct);
            var settings = NormalizeReportingStudioSettings(request);
            package.ThemeJson = WriteReportingStudioSettings(package.ThemeJson, settings);
            package.UpdatedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AddVersionAndAuditAsync(db, http, snapshotBuilder, packageId, "reporting-studio.settings", "ReportPackage", package.Id, "Updated reporting studio customization", before, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(settings);
        });

        // POST /api/packages/{packageId:guid}/reporting-studio/apply — apply reporting studio sections
        // as new slides on the package.
        app.MapPost("/api/packages/{packageId:guid}/reporting-studio/apply", async (
            Guid packageId,
            ReportingStudioApplyRequest request,
            HttpContext http,
            AppDbContext db,
            PackageSnapshotBuilder snapshotBuilder,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var package = await db.ReportPackages
                .Include(x => x.Slides)
                    .ThenInclude(x => x.Blocks)
                .FirstOrDefaultAsync(x => x.Id == packageId, ct);
            if (package is null)
            {
                return Results.NotFound();
            }
            if (EndpointHelpers.RejectIfApproved(package) is { } locked)
            {
                return locked;
            }

            var before = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct);
            var settings = ReadReportingStudioSettings(package);
            var selectedSections = request.Sections is { Length: > 0 }
                ? request.Sections
                : settings.ReportSections;
            selectedSections = selectedSections
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var existingSubjects = package.Slides
                .Select(x => x.Subject)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var nextSortOrder = package.Slides.Count == 0 ? 1 : package.Slides.Max(x => x.SortOrder) + 1;
            var created = 0;

            foreach (var section in selectedSections.Where(x => !existingSubjects.Contains(x)))
            {
                var slide = BuildReportingStudioSlide(package.Id, nextSortOrder++, section, settings);
                db.PackageSlides.Add(slide);
                existingSubjects.Add(section);
                created++;
            }

            package.ThemeJson = WriteReportingStudioSettings(package.ThemeJson, settings with { ReportSections = selectedSections });
            package.UpdatedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AddVersionAndAuditAsync(db, http, snapshotBuilder, packageId, "reporting-studio.apply", "ReportPackage", package.Id, $"Applied reporting studio sections ({created} new slides)", before, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { package.Id, created, sections = selectedSections });
        });

        // PUT /api/packages/{packageId:guid}/theme — update branding and layout settings.
        app.MapPut("/api/packages/{packageId:guid}/theme", async (
            Guid packageId,
            UpdatePackageThemeRequest request,
            HttpContext http,
            AppDbContext db,
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
            if (EndpointHelpers.RejectIfApproved(package) is { } locked)
            {
                return locked;
            }

            var before = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct);
            var previousReportingStudio = ReadThemeObject(package.ThemeJson)["ReportingStudio"]?.DeepClone();
            var theme = new JsonObject
            {
                ["Primary"] = request.Primary,
                ["Accent"] = request.Accent,
                ["LogoFileName"] = request.LogoFileName,
                ["FontFamily"] = request.FontFamily,
                ["CoverStyle"] = request.CoverStyle,
                ["PageOrder"] = JsonSerializer.SerializeToNode(request.PageOrder),
                ["HeaderText"] = request.HeaderText,
                ["FooterText"] = request.FooterText,
                ["ExportSettings"] = request.ExportSettings.HasValue ? JsonNode.Parse(request.ExportSettings.Value.GetRawText()) : null
            };
            if (previousReportingStudio is not null)
            {
                theme["ReportingStudio"] = previousReportingStudio;
            }

            package.ThemeJson = theme.ToJsonString();
            package.UpdatedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AddVersionAndAuditAsync(db, http, snapshotBuilder, packageId, "package.theme", "ReportPackage", package.Id, "Updated branding/layout settings", before, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { package.Id, package.ThemeJson });
        });

        return app;
    }

    // --- Local copies of helpers still in Program.cs; remove once Program.cs is fully drained. ---

    /// <summary>
    /// LOCAL COPY from Program.cs — ReadReportingStudioSettings.
    /// Reads and normalizes the ReportingStudio sub-object from a package's ThemeJson.
    /// </summary>
    private static ReportingStudioSettingsDto ReadReportingStudioSettings(ReportPackage package)
    {
        var root = ReadThemeObject(package.ThemeJson);
        var node = root["ReportingStudio"];
        if (node is not null)
        {
            try
            {
                var saved = JsonSerializer.Deserialize<ReportingStudioSettingsDto>(node.ToJsonString());
                if (saved is not null)
                {
                    return NormalizeReportingStudioSettings(saved);
                }
            }
            catch
            {
                // Ignore malformed customization and fall back to a safe reporting default.
            }
        }

        return DefaultReportingStudioSettings();
    }

    /// <summary>
    /// LOCAL COPY from Program.cs — DefaultReportingStudioSettings.
    /// </summary>
    private static ReportingStudioSettingsDto DefaultReportingStudioSettings()
        => new(
            "Board-ready",
            "$#,0;($#,0)",
            "Whole dollars",
            "Grouped financial statements",
            "Direct CFO narrative",
            ["Executive Summary", "KPI Scorecard", "Profit & Loss", "Trended P&L", "Balance Sheet", "Flux Review", "Action Plan", "Appendix"],
            ShowPriorMonth: true,
            ShowPriorYear: true,
            ShowBudget: false,
            ShowForecast: false,
            ShowYtd: true,
            ShowRollingTwelve: true,
            ShowVarianceDollar: true,
            ShowVariancePercent: true,
            ShowZeroRows: false,
            LandscapeForWideTables: true,
            IncludeFluxNarratives: true,
            IncludeLedgerEvidence: true,
            IncludeFinalReview: true,
            IncludeActionPlan: true);

    /// <summary>
    /// LOCAL COPY from Program.cs — NormalizeReportingStudioSettings.
    /// Trims and de-duplicates section names; falls back to defaults for blank fields.
    /// </summary>
    private static ReportingStudioSettingsDto NormalizeReportingStudioSettings(ReportingStudioSettingsDto settings)
    {
        var sections = (settings.ReportSections ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sections.Length == 0)
        {
            sections = DefaultReportingStudioSettings().ReportSections;
        }

        return settings with
        {
            ReportStyle = string.IsNullOrWhiteSpace(settings.ReportStyle) ? "Board-ready" : settings.ReportStyle.Trim(),
            NumberFormat = string.IsNullOrWhiteSpace(settings.NumberFormat) ? "$#,0;($#,0)" : settings.NumberFormat.Trim(),
            Rounding = string.IsNullOrWhiteSpace(settings.Rounding) ? "Whole dollars" : settings.Rounding.Trim(),
            StatementLayout = string.IsNullOrWhiteSpace(settings.StatementLayout) ? "Grouped financial statements" : settings.StatementLayout.Trim(),
            CommentaryTone = string.IsNullOrWhiteSpace(settings.CommentaryTone) ? "Direct CFO narrative" : settings.CommentaryTone.Trim(),
            ReportSections = sections
        };
    }

    /// <summary>
    /// LOCAL COPY from Program.cs — ReadThemeObject.
    /// Parses the ThemeJson blob into a mutable JsonObject; returns empty object on failure.
    /// </summary>
    private static JsonObject ReadThemeObject(string themeJson)
    {
        if (string.IsNullOrWhiteSpace(themeJson))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(themeJson)?.AsObject() ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// LOCAL COPY from Program.cs — WriteReportingStudioSettings.
    /// Merges normalized settings back into the existing ThemeJson blob.
    /// </summary>
    private static string WriteReportingStudioSettings(string themeJson, ReportingStudioSettingsDto settings)
    {
        var root = ReadThemeObject(themeJson);
        root["ReportingStudio"] = JsonSerializer.SerializeToNode(NormalizeReportingStudioSettings(settings));
        return root.ToJsonString();
    }

    /// <summary>
    /// LOCAL COPY from Program.cs — BuildReportingStudioSlide.
    /// Constructs a PackageSlide with default blocks for the given reporting-studio section.
    /// </summary>
    private static PackageSlide BuildReportingStudioSlide(Guid packageId, int sortOrder, string section, ReportingStudioSettingsDto settings)
    {
        var lower = section.ToLowerInvariant();
        var kind = lower switch
        {
            var x when x.Contains("summary", StringComparison.OrdinalIgnoreCase) => "executive-summary",
            var x when x.Contains("kpi", StringComparison.OrdinalIgnoreCase) => "kpi-scorecard",
            var x when x.Contains("flux", StringComparison.OrdinalIgnoreCase) => "flux-review",
            var x when x.Contains("balance", StringComparison.OrdinalIgnoreCase) => "balance-sheet",
            var x when x.Contains("cash", StringComparison.OrdinalIgnoreCase) => "cash-flow",
            var x when x.Contains("appendix", StringComparison.OrdinalIgnoreCase) || x.Contains("ledger", StringComparison.OrdinalIgnoreCase) => "appendix",
            _ => "financial-statement"
        };

        var blocks = new List<SlideBlock>
        {
            new()
            {
                Id = Guid.NewGuid(),
                SortOrder = 1,
                Kind = "callout",
                ContentJson = JsonSerializer.Serialize(new
                {
                    text = $"{section} uses {settings.StatementLayout}, {settings.Rounding}, and {settings.CommentaryTone} commentary.",
                    source = "Reporting Studio"
                })
            },
            new()
            {
                Id = Guid.NewGuid(),
                SortOrder = 2,
                Kind = lower.Contains("kpi", StringComparison.OrdinalIgnoreCase) || lower.Contains("trend", StringComparison.OrdinalIgnoreCase) ? "chart" : "table",
                ContentJson = JsonSerializer.Serialize(new
                {
                    source = "reporting-studio",
                    section,
                    style = settings.ReportStyle,
                    comparisons = new
                    {
                        settings.ShowPriorMonth,
                        settings.ShowPriorYear,
                        settings.ShowBudget,
                        settings.ShowForecast,
                        settings.ShowYtd,
                        settings.ShowRollingTwelve,
                        settings.ShowVarianceDollar,
                        settings.ShowVariancePercent
                    }
                })
            },
            new()
            {
                Id = Guid.NewGuid(),
                SortOrder = 3,
                Kind = "text",
                ContentJson = JsonSerializer.Serialize(new
                {
                    text = settings.IncludeFluxNarratives
                        ? "Narrative will use completed flux explanations, ledger evidence, and final-review findings."
                        : "Narrative is manually authored and may cite statement and KPI evidence.",
                    tone = settings.CommentaryTone
                })
            }
        };

        if (settings.IncludeActionPlan && (lower.Contains("summary", StringComparison.OrdinalIgnoreCase) || lower.Contains("review", StringComparison.OrdinalIgnoreCase)))
        {
            blocks.Add(new SlideBlock
            {
                Id = Guid.NewGuid(),
                SortOrder = 4,
                Kind = "action-plan",
                ContentJson = JsonSerializer.Serialize(new { source = "flux-and-final-review", status = "Staged" })
            });
        }

        return new PackageSlide
        {
            Id = Guid.NewGuid(),
            ReportPackageId = packageId,
            SortOrder = sortOrder,
            Subject = section,
            KpiLabel = section,
            CurrentValue = 0m,
            PriorValue = 0m,
            VarianceAmount = 0m,
            VariancePercent = 0m,
            AccountCodesCsv = "",
            MonthlyJson = "[]",
            PriorMonthlyJson = "[]",
            ChartConfigJson = JsonSerializer.Serialize(new { type = kind, source = "Reporting Studio" }),
            Blocks = blocks
        };
    }

    /// <summary>
    /// LOCAL COPY from Program.cs — BuildReportingQualityChecksAsync.
    /// Runs a set of quality checks for the reporting-studio readiness panel.
    /// </summary>
    private static async Task<List<ReportingStudioQualityCheckDto>> BuildReportingQualityChecksAsync(
        AppDbContext db,
        ReportPackage package,
        List<ReportingStudioStatementSectionDto> fsSections,
        List<ReportingStudioStatementSectionDto> statementSections,
        CancellationToken ct)
    {
        var checks = new List<ReportingStudioQualityCheckDto>();
        var hasProfitAndLoss = statementSections.Any(x => x.StatementType.Equals("ProfitAndLoss", StringComparison.OrdinalIgnoreCase)
                                                          || x.StatementType.Equals("TrendedProfitAndLoss", StringComparison.OrdinalIgnoreCase)
                                                          || x.StatementType.Equals("TrendedPL", StringComparison.OrdinalIgnoreCase));
        var hasBalanceSheet = statementSections.Any(x => x.StatementType.Equals("BalanceSheet", StringComparison.OrdinalIgnoreCase));
        var periodKey = package.ReportingPeriod?.Key ?? "";
        var ledgerLineCount = await db.XeroLedgerMonthlySummaries
            .AsNoTracking()
            .CountAsync(x => x.OrganizationId == package.OrganizationId
                             && x.MonthKey == periodKey,
                ct);
        var unmappedAccountCount = await db.GlAccounts
            .AsNoTracking()
            .CountAsync(x => x.OrganizationId == package.OrganizationId
                             && x.ReportingPeriodId == package.ReportingPeriodId
                             && (x.FsLine == "" || x.FsLine == "Unmapped"),
                ct);
        var fluxGroups = await db.FluxReviewGroups
            .AsNoTracking()
            .Where(x => x.ReportPackageId == package.Id)
            .ToListAsync(ct);
        var unexplainedFlux = fluxGroups.Count(x => x.RequiresExplanation && string.IsNullOrWhiteSpace(x.Explanation));
        var openMaterialIssues = package.Issues.Count(x => x.Status == IssueStatus.Open && (x.Severity == IssueSeverity.Critical || x.Severity == IssueSeverity.High));
        var activeSectionCount = fsSections.Sum(x => x.LineCount);

        checks.Add(new ReportingStudioQualityCheckDto(
            "Source freshness",
            package.IsSourceDataStale ? "Warning" : "Pass",
            package.IsSourceDataStale ? package.SourceDataStaleReason ?? "Package source data is stale." : "Package is current with imported source data.",
            "Refresh Xero and rebuild package before final distribution if stale."));
        checks.Add(new ReportingStudioQualityCheckDto(
            "Statement coverage",
            hasProfitAndLoss && hasBalanceSheet ? "Pass" : "Fail",
            $"{(hasProfitAndLoss ? "P&L imported" : "P&L missing")} · {(hasBalanceSheet ? "Balance sheet imported" : "Balance sheet missing")}",
            "Import both P&L and balance sheet snapshots for every reporting period."));
        checks.Add(new ReportingStudioQualityCheckDto(
            "FS line library",
            activeSectionCount >= 25 ? "Pass" : "Warning",
            $"{activeSectionCount} active mapped FS lines across {fsSections.Count} statement sections.",
            "Use Group from financials or add custom FS lines before relying on board-level grouping."));
        checks.Add(new ReportingStudioQualityCheckDto(
            "Account grouping",
            unmappedAccountCount == 0 ? "Pass" : "Fail",
            unmappedAccountCount == 0 ? "Every imported account is assigned to an FS line." : $"{unmappedAccountCount} accounts are still ungrouped.",
            "Finish Mapping & Eliminations so statements, flux, and AI commentary cover every account."));
        checks.Add(new ReportingStudioQualityCheckDto(
            "Ledger evidence",
            ledgerLineCount > 0 ? "Pass" : "Warning",
            $"{ledgerLineCount} monthly ledger summary rows available for drilldown and commentary.",
            "Pull ledger detail for material flux lines so AI commentary has evidence."));
        checks.Add(new ReportingStudioQualityCheckDto(
            "Flux explanations",
            fluxGroups.Count == 0 ? "Warning" : unexplainedFlux == 0 ? "Pass" : "Warning",
            fluxGroups.Count == 0 ? "Flux review has not been generated." : $"{unexplainedFlux} required explanations remain open.",
            "Complete flux review before AI package drafting."));
        checks.Add(new ReportingStudioQualityCheckDto(
            "Final review readiness",
            openMaterialIssues == 0 ? "Pass" : "Warning",
            $"{openMaterialIssues} critical/high final review issues are open.",
            "Run and clear Final AI Review before distribution."));
        checks.Add(new ReportingStudioQualityCheckDto(
            "Package structure",
            package.Slides.Count >= 6 ? "Pass" : "Warning",
            $"{package.Slides.Count} slides currently in the board package.",
            "Apply a reporting studio section set or a board template for a complete package."));

        return checks;
    }

    /// <summary>
    /// LOCAL COPY from Program.cs — ReportingContentLibrary.
    /// Returns the static content library catalogue for the reporting-studio picker.
    /// </summary>
    private static IReadOnlyList<ReportingStudioContentGroupDto> ReportingContentLibrary()
        =>
        [
            new("Executive narrative", "Tell the story without spreadsheet noise.", [
                new("Executive Summary", "Narrative", "CFO-ready current month story, key movements, decisions needed, and next steps."),
                new("Action Plan", "Workflow", "Owner, due date, status, and decision log pulled from flux and final review."),
                new("AI Commentary", "Narrative", "Codex-generated draft commentary with traceable citations and no secret data.")
            ]),
            new("Financial statements", "Flexible statements with clean board formatting.", [
                new("Profit & Loss", "Statement", "Current, prior month, prior year, YTD, budget, forecast, and variance columns."),
                new("Trended P&L", "Trend", "Up to 12 monthly periods with variance markers and materiality badges."),
                new("Balance Sheet", "Statement", "Grouped assets, liabilities, equity, working capital, and cash movement callouts."),
                new("Cash Flow", "Statement", "Direct/indirect view fed by ledger, AR/AP, and forecast assumptions."),
                new("Financial Statement Notes", "Narrative", "Accounting-policy, liquidity, debt, and unusual-item notes for board and lender packs.")
            ]),
            new("Analysis", "Explain what changed and why.", [
                new("Flux Review", "Variance", "Threshold-driven P&L and balance sheet variance explanations with ledger evidence."),
                new("Waterfall Bridge", "Visual", "Prior to current bridge for revenue, gross profit, EBITDA, or cash."),
                new("Common-size Analysis", "Ratio", "Percent-of-revenue and percent-of-assets views for trend diagnosis."),
                new("Scenario/Budget Variance", "Planning", "Actual vs budget/forecast and driver variance callouts."),
                new("Driver Tree", "Analysis", "Revenue, margin, working-capital, and cash drivers connected to KPIs and ledger evidence.")
            ]),
            new("Dashboards and KPIs", "Interactive executive view.", [
                new("KPI Scorecard", "KPI", "Targets, trend, status, commentary, and owner for financial/non-financial KPIs."),
                new("Entity Benchmark", "Benchmark", "Side-by-side entity ranking, margins, issues, and operational metrics."),
                new("Runway and Working Capital", "Cash", "Cash, AR, AP, DSO, DPO, burn/runway, and forecast sensitivities."),
                new("Interactive Dashboard", "Dashboard", "Executive web view for entity KPIs, statements, flux, packages, and data freshness.")
            ]),
            new("Assurance appendix", "Evidence and QA without cluttering the board deck.", [
                new("Final Review Summary", "QA", "Open/closed AI review issues, approved fixes, and unresolved risks."),
                new("Ledger Evidence", "Drilldown", "Material accounts, source transactions, and TB reconciliation status."),
                new("Mapping Changes", "Audit", "New accounts, FS line mappings, eliminations, and reviewer reasons."),
                new("Data Quality Dashboard", "QA", "Coverage, reconciliation, stale-source, rate-limit, and missing-evidence checks.")
            ])
        ];

    /// <summary>
    /// LOCAL COPY from Program.cs — CompetitiveFeatureGroups.
    /// Returns the competitive feature matrix for the /api/competitive-gaps endpoint.
    /// </summary>
    private static IReadOnlyList<CompetitiveFeatureGroupDto> CompetitiveFeatureGroups()
        =>
        [
            new("Reporting", "Fathom, Syft, Spotlight, Reach, and similar tools emphasize editable reports, reusable content libraries, management packs, scheduled delivery, branded exports, comments, and access control.", [
                new("Reporting Studio", "Implemented", "Non-spreadsheet report builder with package-level sections, presentation style, comparisons, commentary rules, and readiness checks."),
                new("Report editor", "Implemented", "Slide/block editor with reorder, update, history, and AI review."),
                new("Template library", "Implemented", "Built-in report templates can now be applied to packages."),
                new("Content library", "Implemented", "Reusable executive narrative, statement, KPI, analysis, and assurance sections."),
                new("Scheduled reports", "Implemented", "Distribution schedules exist with test-send tracking."),
                new("PDF/Excel exports", "Implemented", "Artifacts are generated with QA metadata."),
                new("Comments", "Implemented", "Slide and block comments can be captured, tracked, and resolved.")
            ]),
            new("Analysis", "Competitors cover KPI libraries, custom formulas, alerts, targets, non-financial metrics, benchmarking, variance analysis, and trend analysis.", [
                new("KPI goals and targets", "Implemented", "Editable KPI targets and status scoring."),
                new("KPI alerts", "Implemented", "Persisted threshold alerts with trigger status."),
                new("Non-financial metrics", "Implemented", "Manual datasheet-style metrics sit beside financial KPIs."),
                new("Benchmarking and ranking", "Implemented", "Entity comparison and ranking by period."),
                new("Formula builder", "Implemented", "Finance formulas evaluate arithmetic plus fs(), kpi(), and metric() references."),
                new("Threshold flux review", "Implemented", "P&L and balance-sheet flux with prior month, prior year, running three-month balance, ledger drilldown, AI explanation, and sign-off.")
            ]),
            new("Planning", "Fathom, Reach, Jirav, Float, and Syft all push scenario planning, driver forecasts, cash runway, 3-way financials, and assumptions.", [
                new("3-way forecast", "Implemented", "P&L, cash flow, and balance sheet projections are generated for up to 36 months."),
                new("Scenario planning", "Implemented", "Base/upside/downside/custom scenarios with editable drivers."),
                new("Microforecasts", "Implemented", "Scenario events model hires, renewals, and one-time cash impacts."),
                new("Budget variance", "Implemented", "Actual-vs-budget rows are generated from scenario drivers."),
                new("Daily cash forecasting", "Implemented", "Forecast cash flows can be spread into weekly or daily timing rows.")
            ]),
            new("Consolidation", "The strongest tools include eliminations, multi-system data, multi-currency, side-by-side financials, group forecasts, and entity drilldown.", [
                new("Eliminations", "Implemented", "Account-level eliminations and recurring rules exist."),
                new("Entity drilldown", "Implemented", "Ledger and mapping drilldowns are available."),
                new("Side-by-side financials", "Implemented", "Benchmarking view compares entities by period."),
                new("Group forecasts", "Implemented", "Consolidated entities can use the same 3-way planning surface."),
                new("Custom FX rates", "Implemented", "User-managed FX rate tables are persisted by entity and period.")
            ])
        ];
}
