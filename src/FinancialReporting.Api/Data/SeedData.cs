using System.Text.Json;
using FinancialReporting.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Data;

public static class SeedData
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task EnsureSeededAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.Organizations.AnyAsync(cancellationToken))
        {
            return;
        }

        var period = new ReportingPeriod
        {
            Id = Id("00000000-0000-0000-0000-000000000101"),
            Key = "2025-11",
            Label = "November 2025",
            PeriodStart = new DateOnly(2025, 11, 1),
            PeriodEnd = new DateOnly(2025, 11, 30),
            IsClosed = true
        };

        var rx = new Organization
        {
            Id = Id("00000000-0000-0000-0000-000000000201"),
            Key = "rxlc",
            Name = "RxLinc, LLC",
            Abbreviation = "RXLC",
            PrimaryColor = "#0E6B47",
            AccentColor = "#1A1A1A",
            Tagline = "Pharmacy switch & transmission · Confidential"
        };

        var cons = new Organization
        {
            Id = Id("00000000-0000-0000-0000-000000000202"),
            Key = "cons",
            Name = "PPOk Consolidated",
            Abbreviation = "CONS",
            IsConsolidated = true,
            PrimaryColor = "#0F2A4A",
            AccentColor = "#8B1A1A",
            Tagline = "Consolidated reporting · PPOk family of companies"
        };

        var package = new ReportPackage
        {
            Id = Id("00000000-0000-0000-0000-000000000301"),
            OrganizationId = rx.Id,
            ReportingPeriodId = period.Id,
            Status = PackageStatus.Review,
            VersionLabel = "v3 (in progress)",
            BaseFrom = "October 2025",
            LastXeroSyncAt = DateTimeOffset.UtcNow.AddMinutes(-8),
            ThemeJson = Json(new { primary = rx.PrimaryColor, accent = rx.AccentColor, coverStyle = rx.CoverStyle })
        };

        var slides = new[]
        {
            Slide(package.Id, "00000000-0000-0000-0000-000000000401", 1, "PTPL", "RXL PTPL", 7_592_706m, 7_105_342m, ["3400110", "3400120", "3400140"],
                [677900m,633700m,687000m,634900m,676600m,706700m,662400m,710300m,779700m,702800m,568600m],
                ["RxLinc PTPL is up year-over-year; however, the majority of the increase occurred in Q1 2025.",
                 "RxLinc has seen significant growth in customer counts due to the work of the sales department, which has offset vendor-partner headwinds."]),
            Slide(package.Id, "00000000-0000-0000-0000-000000000402", 2, "ePrescribe", "EPRESCRIBE", 2_092_239m, 2_414_449m, ["3415100", "3415110"],
                [191600m,200800m,202400m,195600m,190100m,183600m,175500m,183300m,187100m,161000m,221300m],
                ["RxLinc ePrescribe revenue has decreased significantly following the loss of a vendor partner in late 2024.",
                 "YTD ePrescribe revenue of $2,092,239 is down $322,210 or 13.4% versus prior year."]),
            Slide(package.Id, "00000000-0000-0000-0000-000000000403", 3, "Switch / Transmission", "SWITCH / TRANSMISSION", 7_199_786m, 6_693_400m, ["3415100"],
                [606300m,662600m,640400m,629500m,634500m,668400m,648400m,680800m,721500m,629800m,677400m],
                ["Switch revenue is up year-over-year, and gross profit is up significantly as well.",
                 "Starting in May 2025, revenue increased past the previous highs achieved under short-term contracts."]),
            Slide(package.Id, "00000000-0000-0000-0000-000000000404", 4, "Salaries and Wages", "SALARIES AND WAGES", 1_683_478m, 1_360_666m, ["3600110", "3600130"],
                [145000m,151300m,171700m,145800m,145200m,157800m,171000m,151600m,166600m,152400m,125000m],
                ["Salaries and wages are up year-over-year for RxLinc.",
                 "In November 2025, the longevity bonus experienced a decrease due to a true-up of the beginning-of-year estimate."])
        };

        var issues = new[]
        {
            new PackageIssue
            {
                Id = Id("00000000-0000-0000-0000-000000000501"),
                ReportPackageId = package.Id,
                PackageSlideId = slides[1].Id,
                Severity = IssueSeverity.Medium,
                Category = "Narrative",
                Title = "PY April spike not annotated",
                Description = "April 2024 SureScripts discount distorts PY gross profit comparison.",
                EvidenceJson = Json(new { slide = "ePrescribe", rule = "QA Section 5", basis = "One-time PY event materially affects YOY trend" }),
                RecommendedFixJson = Json(new
                {
                    operations = new[]
                    {
                        new { op = "add_callout", targetType = "slide", targetId = slides[1].Id, value = new { text = "Prior year included a one-time SureScripts discount benefit in April 2024." } }
                    }
                }),
                Confidence = 0.83m
            },
            new PackageIssue
            {
                Id = Id("00000000-0000-0000-0000-000000000502"),
                ReportPackageId = package.Id,
                PackageSlideId = slides[3].Id,
                Severity = IssueSeverity.High,
                Category = "Trend",
                Title = "PY November anomaly not addressed",
                Description = "PY November showed $195K versus the typical $110K-$130K range.",
                EvidenceJson = Json(new { slide = "Salaries and Wages", period = "November 2024", amount = 195100 }),
                RecommendedFixJson = Json(new
                {
                    operations = new[]
                    {
                        new { op = "append_narrative", targetType = "slide", targetId = slides[3].Id, value = new { text = "Prior-year November included a one-time bonus accrual that should be treated separately from the current-year true-up." } }
                    }
                }),
                Confidence = 0.91m
            }
        };

        var accounts = new[]
        {
            Account(rx.Id, period.Id, "tenant-rxlc", "3415100", "Switch / Transmission Fees", "Revenue", "Revenue — Switch / Transmission", "Revenue — Switch / Transmission", 0.97m, false, MappingReviewStatus.Reviewed, ConsolidationTreatment.Include, [606300m,662600m,640400m,629500m,634500m,668400m,648400m,680800m,721500m,629800m,677400m]),
            Account(rx.Id, period.Id, "tenant-rxlc", "3415110", "ePrescribe Gross Profit", "Revenue", "Revenue — ePrescribe", "Revenue — ePrescribe", 0.82m, true, MappingReviewStatus.New, ConsolidationTreatment.Include, [97700m,102400m,103300m,100000m,97200m,93800m,89300m,93800m,96200m,82600m,116900m]),
            Account(rx.Id, period.Id, "tenant-rxlc", "3600110", "Salaries and Wages", "Expense", "Operating Expense — Payroll", "Operating Expense — Payroll", 0.94m, false, MappingReviewStatus.Suggested, ConsolidationTreatment.Include, [145000m,151300m,171700m,145800m,145200m,157800m,171000m,151600m,166600m,152400m,125000m]),
            Account(rx.Id, period.Id, "tenant-rxlc", "499999", "Intercompany Management Fee", "Revenue", "Other Income", "Intercompany Revenue", 0.76m, true, MappingReviewStatus.New, ConsolidationTreatment.Intercompany, [0m,0m,0m,0m,0m,0m,0m,0m,25000m,25000m,25000m])
        };

        var transactions = new[]
        {
            Tx(accounts[0].Id, "2025-11-05", "Switch — Aetna Nov batch 1", 0m, 124200m),
            Tx(accounts[0].Id, "2025-11-12", "Switch — UnitedHealth Nov", 0m, 182300m),
            Tx(accounts[0].Id, "2025-11-19", "Switch — Cigna Nov", 0m, 88650m),
            Tx(accounts[1].Id, "2025-11-30", "SureScripts gross profit activity", 0m, 116900m),
            Tx(accounts[2].Id, "2025-11-15", "Payroll PE 11/15/2025", 62500m, 0m),
            Tx(accounts[2].Id, "2025-11-30", "Payroll PE 11/30/2025", 62500m, 0m),
            Tx(accounts[3].Id, "2025-11-30", "Intercompany management fee", 0m, 25000m)
        };

        var settings = new[]
        {
            Runtime("slide-chat", "gpt-5.4-mini", "medium", "Slide scoped chat and rewrite suggestions"),
            Runtime("narrative-rewrite", "gpt-5.5", "high", "Board-friendly narrative rewrite"),
            Runtime("mapping-suggestions", "gpt-5.4", "high", "GL account mapping and first-seen review"),
            Runtime("final-review", "gpt-5.5", "xhigh", "Whole package QA review"),
            Runtime("export-qa", "gpt-5.4", "high", "PDF/Excel export QA pass")
        };

        db.Add(period);
        db.AddRange(rx, cons);
        db.Add(package);
        db.AddRange(slides);
        db.AddRange(issues);
        db.AddRange(accounts);
        db.AddRange(transactions);
        db.AddRange(settings);
        var kpis = new[]
        {
            new KpiDefinition { Id = Id("00000000-0000-0000-0000-000000000701"), OrganizationId = rx.Id, Name = "Gross Profit Margin", Category = "Profitability", Formula = "Gross Profit / Revenue", Unit = "%", CurrentValue = 38.4m, TargetValue = 35m, IsPinned = true, Status = "good" },
            new KpiDefinition { Id = Id("00000000-0000-0000-0000-000000000702"), OrganizationId = rx.Id, Name = "DSO", Category = "Working Capital", Formula = "AR / Revenue * Days", Unit = "days", CurrentValue = 42m, TargetValue = 38m, IsPinned = true, Status = "warn" },
            new KpiDefinition { Id = Id("00000000-0000-0000-0000-000000000703"), OrganizationId = rx.Id, Name = "Cash Runway", Category = "Liquidity", Formula = "Cash / Monthly Burn", Unit = "months", CurrentValue = 8.6m, TargetValue = 6m, IsPinned = true, Status = "good" }
        };
        db.AddRange(kpis);
        db.AddRange(
            new KpiAlert { Id = Id("00000000-0000-0000-0000-000000000704"), KpiDefinitionId = kpis[0].Id, Direction = "Below", ThresholdValue = 35m, Severity = "High", Message = "Gross margin fell below the board target." },
            new KpiAlert { Id = Id("00000000-0000-0000-0000-000000000705"), KpiDefinitionId = kpis[1].Id, Direction = "Above", ThresholdValue = 40m, Severity = "Medium", Message = "DSO is above the working-capital tolerance." });
        db.AddRange(
            new NonFinancialMetric { Id = Id("00000000-0000-0000-0000-000000000706"), OrganizationId = rx.Id, ReportingPeriodId = period.Id, Name = "Customer Count", Category = "Operations", Unit = "customers", CurrentValue = 184m, PriorValue = 162m, TargetValue = 190m, ValuesJson = Json(new[] { 162, 166, 168, 171, 173, 176, 178, 181, 183, 184, 184 }), Source = "Manual datasheet", IsPinned = true },
            new NonFinancialMetric { Id = Id("00000000-0000-0000-0000-000000000707"), OrganizationId = rx.Id, ReportingPeriodId = period.Id, Name = "Vendor Partner Retention", Category = "Operations", Unit = "%", CurrentValue = 94m, PriorValue = 91m, TargetValue = 95m, ValuesJson = Json(new[] { 91, 92, 92, 93, 93, 94, 94, 94, 94, 94, 94 }), Source = "Manual datasheet", IsPinned = true });
        var baseScenario = new ForecastScenario
        {
            Id = Id("00000000-0000-0000-0000-000000000708"),
            OrganizationId = rx.Id,
            ReportingPeriodId = period.Id,
            Name = "Board base case",
            Description = "Rolling 36-month forecast from current YTD trend.",
            ScenarioType = "Base",
            HorizonMonths = 36,
            RevenueGrowthPercent = 7.5m,
            GrossMarginPercent = 39m,
            OpexGrowthPercent = 4m,
            CashConversionPercent = 86m,
            StartingCash = 3_250_000m,
            CashThreshold = 1_500_000m,
            AssumptionsJson = Json(new[] { "Revenue grows from current customer-count trend", "Payroll follows approved hiring plan", "Cash conversion excludes non-recurring true-ups" }),
            IsBase = true
        };
        db.Add(baseScenario);
        db.AddRange(
            new ForecastEvent { Id = Id("00000000-0000-0000-0000-000000000709"), ForecastScenarioId = baseScenario.Id, MonthOffset = 3, Name = "Sales hire", Category = "People", ExpenseImpact = 12000m, CashImpact = -12000m, IsRecurring = true, Notes = "Microforecast for approved sales headcount." },
            new ForecastEvent { Id = Id("00000000-0000-0000-0000-000000000710"), ForecastScenarioId = baseScenario.Id, MonthOffset = 6, Name = "Vendor incentive renewal", Category = "Revenue", RevenueImpact = 35000m, CashImpact = 25000m, IsRecurring = true, Notes = "Scenario driver for expected vendor renewal." });
        db.AddRange(BuiltInTemplates().Select(template => new ReportTemplate
        {
            Id = Guid.NewGuid(),
            Name = template.Name,
            Category = template.Category,
            Description = template.Description,
            SectionsJson = Json(template.Sections),
            IsBuiltIn = true
        }));
        db.AddRange(
            new AccountMapping { Id = Id("00000000-0000-0000-0000-000000000801"), OrganizationId = rx.Id, ReportingPeriodId = period.Id, FsLine = "Revenue — Switch / Transmission", AccountCodesCsv = "3415100", EntityKeysCsv = "RXLC", Reason = "Seed mapping from current board package" },
            new AccountMapping { Id = Id("00000000-0000-0000-0000-000000000802"), OrganizationId = rx.Id, ReportingPeriodId = period.Id, FsLine = "Revenue — ePrescribe", AccountCodesCsv = "3415110", EntityKeysCsv = "RXLC", Reason = "Seed mapping from current board package" });
        db.Add(new EliminationEntry { Id = Id("00000000-0000-0000-0000-000000000901"), OrganizationId = rx.Id, ReportingPeriodId = period.Id, GlAccountId = accounts[3].Id, Type = "Intercompany", Description = "Intercompany management fee elimination", Amount = 25000m, Status = "Review", Reason = "Seed intercompany example" });
        db.Add(new XeroConnection
        {
            Id = Id("00000000-0000-0000-0000-000000001001"),
            OrganizationId = rx.Id,
            TenantId = "tenant-rxlc",
            TenantName = "RxLinc, LLC",
            TenantType = "ORGANISATION",
            ConnectionStatus = "NeedsReconnect",
            Scopes = "offline_access accounting.reports.read accounting.transactions.read accounting.settings.read",
            TokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            LastError = "No imported Finance App V2 token yet"
        });
        db.Add(new PackageVersion { Id = Id("00000000-0000-0000-0000-000000001101"), ReportPackageId = package.Id, VersionLabel = "v3", CreatedBy = "Seed", ChangeSummary = "Initial seeded package based on supplied prototype", SnapshotJson = "{}" });

        await db.SaveChangesAsync(cancellationToken);
    }

    private static PackageSlide Slide(Guid packageId, string id, int sort, string subject, string kpiLabel, decimal current, decimal prior, string[] accounts, decimal[] monthly, string[] paragraphs)
    {
        var slideId = Id(id);
        var blocks = new List<SlideBlock>
        {
            new() { Id = Guid.NewGuid(), PackageSlideId = slideId, SortOrder = 1, Kind = "kpi", ContentJson = Json(new { label = kpiLabel, current, prior, variance = current - prior }) },
            new() { Id = Guid.NewGuid(), PackageSlideId = slideId, SortOrder = 2, Kind = "chart", ContentJson = Json(new { type = "clustered", showPY = true, showLegend = true, showGrid = true }) },
            new() { Id = Guid.NewGuid(), PackageSlideId = slideId, SortOrder = 3, Kind = "drivers", ContentJson = Json(new { accounts }) }
        };

        var order = 4;
        blocks.AddRange(paragraphs.Select(text => new SlideBlock
        {
            Id = Guid.NewGuid(),
            PackageSlideId = slideId,
            SortOrder = order++,
            Kind = "text",
            ContentJson = Json(new { text })
        }));

        return new PackageSlide
        {
            Id = slideId,
            ReportPackageId = packageId,
            SortOrder = sort,
            Subject = subject,
            KpiLabel = kpiLabel,
            CurrentValue = current,
            PriorValue = prior,
            VarianceAmount = current - prior,
            VariancePercent = prior == 0m ? 0m : Math.Round((current - prior) / prior * 100m, 1),
            AccountCodesCsv = string.Join(",", accounts),
            MonthlyJson = Json(monthly),
            PriorMonthlyJson = Json(monthly.Select(x => Math.Round(x * 0.92m, 2)).ToArray()),
            ChartConfigJson = Json(new { type = "clustered", dataset = "ytd", showPY = true, showLegend = true }),
            Blocks = blocks
        };
    }

    private static GlAccount Account(Guid orgId, Guid periodId, string tenantId, string code, string name, string type, string fsLine, string aiLine, decimal confidence, bool firstSeen, MappingReviewStatus review, ConsolidationTreatment treatment, decimal[] monthly)
        => new()
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            ReportingPeriodId = periodId,
            TenantId = tenantId,
            Code = code,
            Name = name,
            Type = type,
            Class = type == "Revenue" ? "Income Statement" : "Operating Expense",
            FsLine = fsLine,
            AiSuggestedFsLine = aiLine,
            MappingConfidence = confidence,
            IsFirstSeen = firstSeen,
            ReviewStatus = review,
            ConsolidationTreatment = treatment,
            MonthlyBalancesJson = Json(monthly),
            PriorPeriodHistoryJson = Json(new[] { "2025-08", "2025-09", "2025-10" })
        };

    private static GlTransaction Tx(Guid accountId, string date, string description, decimal debit, decimal credit)
        => new()
        {
            Id = Guid.NewGuid(),
            GlAccountId = accountId,
            TransactionDate = DateOnly.Parse(date),
            Description = description,
            Debit = debit,
            Credit = credit
        };

    private static AiRuntimeSetting Runtime(string module, string model, string effort, string profile)
        => new()
        {
            Id = Guid.NewGuid(),
            Module = module,
            Model = model,
            ReasoningEffort = effort,
            Profile = profile
        };

    private static IEnumerable<(string Name, string Category, string Description, string[] Sections)> BuiltInTemplates()
        =>
        [
            ("Executive board pack", "Management reporting", "Monthly board package with KPI scorecard, financial statements, flux review, and appendix.", ["Executive Summary", "KPI Scorecard", "P&L Trend", "Balance Sheet Snapshot", "Cash Flow", "Flux Review", "Appendix"]),
            ("3-way forecast pack", "Planning", "Forward-looking package with P&L, balance sheet, cash flow, scenario comparison, and assumptions.", ["Forecast Summary", "P&L Forecast", "Balance Sheet Forecast", "Cash Flow Forecast", "Scenario Comparison", "Assumptions"]),
            ("Consolidation pack", "Consolidation", "Group reporting pack for entity drilldown, side-by-side financials, eliminations, and FX review.", ["Group Summary", "Side-by-side Financials", "Entity Drilldown", "Eliminations", "FX Rates", "Consolidated Forecast"]),
            ("Advisory dashboard", "Client portal", "Interactive dashboard for goals, alerts, non-financial drivers, and AI narrative.", ["Goals", "KPI Alerts", "Non-financial Metrics", "AI Commentary", "Action Plan"])
        ];

    private static Guid Id(string value) => Guid.Parse(value);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
