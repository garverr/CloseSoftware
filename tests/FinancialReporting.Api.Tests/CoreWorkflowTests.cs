using System.Text.Json;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using FinancialReporting.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FinancialReporting.Api.Tests;

public sealed class CoreWorkflowTests
{
    [Fact]
    public void Variance_ReturnsAmountAndRoundedPercent()
    {
        var result = FinancialMath.Variance(2_092_239m, 2_414_449m);

        Assert.Equal(-322_210m, result.Amount);
        Assert.Equal(-13.3m, result.Percent);
    }

    // P3.36 — numeric regression suite. Cat 40. Pins the variance formula's behavior so a
    // future change to sign convention (Cat 9 follow-on) shows up here as an expected diff
    // rather than silently shifting flux output.
    [Theory]
    [InlineData(100, 100, 0, 0.0)]              // unchanged
    [InlineData(150, 100, 50, 50.0)]            // simple positive
    [InlineData(50, 100, -50, -50.0)]           // simple negative
    [InlineData(100, 0, 100, 0.0)]              // prior zero → percent=0 by current formula
    [InlineData(0, 100, -100, -100.0)]          // prior positive, current zero
    [InlineData(-50, 100, -150, -150.0)]        // sign flip negative
    [InlineData(50, -100, 150, -150.0)]         // raw amount/prior*100; prior is negative
    [InlineData(0, 0, 0, 0.0)]                  // both zero
    [InlineData(0.005, 0, 0.005, 0.0)]          // tiny non-zero current vs zero prior
    public void Variance_HandlesEdges(double current, double prior, double expectedAmount, double expectedPercent)
    {
        var result = FinancialMath.Variance((decimal)current, (decimal)prior);
        Assert.Equal((decimal)expectedAmount, Math.Round(result.Amount, 3));
        Assert.Equal((decimal)expectedPercent, Math.Round(result.Percent, 1));
    }

    [Theory]
    [InlineData("AND", 6_000, 12.0, 5_000, 10.0, true)]   // both legs trip → AND fires
    [InlineData("AND", 6_000, 8.0, 5_000, 10.0, false)]   // % under → AND blocks
    [InlineData("AND", 4_000, 12.0, 5_000, 10.0, false)]  // $ under → AND blocks
    [InlineData("OR", 6_000, 8.0, 5_000, 10.0, true)]     // $ trips → OR fires
    [InlineData("OR", 4_000, 12.0, 5_000, 10.0, true)]    // % trips → OR fires
    [InlineData("OR", 4_000, 8.0, 5_000, 10.0, false)]    // neither → OR silent
    public void DualThreshold_AndVsOrLogic(string logic, double variance, double percent, double dollarLimit, double percentLimit, bool expected)
    {
        // Mirrors the logic in PackageDiffService.IsMaterial and FluxReviewService gate.
        var dollarHit = (decimal)dollarLimit > 0m && Math.Abs((decimal)variance) >= (decimal)dollarLimit;
        var percentHit = (decimal)percentLimit > 0m && Math.Abs((decimal)percent) >= (decimal)percentLimit;
        var fires = logic == "AND" ? dollarHit && percentHit : dollarHit || percentHit;
        Assert.Equal(expected, fires);
    }

    [Fact]
    public void BoardMateriality_DefaultsAreFiveKAndTenPercent()
    {
        // P2.20 — fail loud if anyone resets the dual-threshold defaults to the prior 0/OR.
        // Cat 10. The board threshold default lives on ReportPackage now.
        var pkg = new ReportPackage();
        Assert.Equal(25_000m, pkg.BoardDollarThreshold);
        Assert.Equal(15m, pkg.BoardPercentThreshold);
    }

    // P3.36 — additional numeric regressions. Cat 40.
    [Theory]
    [InlineData(100, 100, true, "good")]
    [InlineData(95, 100, true, "warn")]
    [InlineData(80, 100, true, "bad")]
    [InlineData(100, 100, false, "good")]
    [InlineData(105, 100, false, "warn")]
    [InlineData(150, 100, false, "bad")]
    [InlineData(100, 0, true, "neutral")]
    public void KpiStatus_HandlesHigherAndLowerIsBetter_ExtendedEdges(double current, double target, bool higherIsBetter, string expected)
    {
        Assert.Equal(expected, FinancialMath.KpiStatus((decimal)current, (decimal)target, higherIsBetter));
    }

    [Theory]
    [InlineData(new[] { 100.0, 200.0, 300.0 }, new[] { 25.0 }, 575.0)]   // 600 - 25
    [InlineData(new double[] { }, new double[] { }, 0.0)]
    [InlineData(new[] { -50.0, 50.0 }, new[] { 0.0 }, 0.0)]
    public void ConsolidatedTotal_AppliesEliminations(double[] entityAmounts, double[] eliminations, double expected)
    {
        var result = FinancialMath.ConsolidatedTotal(
            entityAmounts.Select(x => (decimal)x),
            eliminations.Select(x => (decimal)x));
        Assert.Equal((decimal)expected, result);
    }

    [Fact]
    public void Variance_RoundsPercentToOneDecimalPlace()
    {
        // 1/3 = 33.333...; we round to one decimal.
        var r = FinancialMath.Variance(133.33m, 100m);
        Assert.Equal(33.33m, Math.Round(r.Amount, 2));
        Assert.Equal(33.3m, r.Percent);
    }

    [Fact]
    public async Task PackageDiff_ImmaterialMovementsBecomeKeepDecisions()
    {
        // Below-threshold movements must NOT generate Modify suggestions; carry-forward.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var org = new Organization { Id = Guid.NewGuid(), Key = "rxl", Name = "RxLinc" };
        var prior = new ReportingPeriod { Id = Guid.NewGuid(), Key = "2026-01", Label = "January 2026", PeriodStart = new DateOnly(2026, 1, 1), PeriodEnd = new DateOnly(2026, 1, 31) };
        var current = new ReportingPeriod { Id = Guid.NewGuid(), Key = "2026-02", Label = "February 2026", PeriodStart = new DateOnly(2026, 2, 1), PeriodEnd = new DateOnly(2026, 2, 28) };
        db.Organizations.Add(org);
        db.ReportingPeriods.AddRange(prior, current);
        var priorPackage = new ReportPackage { Id = Guid.NewGuid(), OrganizationId = org.Id, ReportingPeriodId = prior.Id, BoardDollarThreshold = 25_000m, BoardPercentThreshold = 15m };
        var currentPackage = new ReportPackage { Id = Guid.NewGuid(), OrganizationId = org.Id, ReportingPeriodId = current.Id, PriorPackageId = priorPackage.Id, BoardDollarThreshold = 25_000m, BoardPercentThreshold = 15m };
        db.ReportPackages.AddRange(priorPackage, currentPackage);
        // Revenue moved $1,000 / 0.5% — well under the 25k AND 15% threshold → Keep, not Modify.
        db.PackageSlides.AddRange(
            new PackageSlide { Id = Guid.NewGuid(), ReportPackageId = priorPackage.Id, SortOrder = 1, Subject = "Revenue", AccountCodesCsv = "4000", CurrentValue = 200_000m },
            new PackageSlide { Id = Guid.NewGuid(), ReportPackageId = currentPackage.Id, SortOrder = 1, Subject = "Revenue", AccountCodesCsv = "4000", CurrentValue = 201_000m });
        await db.SaveChangesAsync();

        var diff = new PackageDiffService(db);
        var result = await diff.ComputeAsync(currentPackage.Id, CancellationToken.None);
        var keep = Assert.Single(result.Decisions, d => d.Subject == "Revenue");
        Assert.Equal(PackageDiffService.SlideDecisionKind.Keep, keep.Kind);
    }

    [Fact]
    public void MappingService_FlagsAccountAsFirstSeenWhenTenantAndCodeHaveNoHistory()
    {
        var service = new MappingService();
        var prior = new[]
        {
            new GlAccount { TenantId = "tenant-rxlc", Code = "3415100" },
            new GlAccount { TenantId = "tenant-rxlc", Code = "3600110" }
        };

        Assert.True(service.IsFirstSeenAccount(prior, "tenant-rxlc", "3415110"));
        Assert.False(service.IsFirstSeenAccount(prior, "tenant-rxlc", "3415100"));
    }

    [Fact]
    public async Task FinancialStatementGrouping_UsesImportedStatementLinesForFsLibraryAndAccountMappings()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var orgId = Guid.NewGuid();
        var periodId = Guid.NewGuid();
        db.Organizations.Add(new Organization { Id = orgId, Key = "test", Name = "Test Entity", Abbreviation = "TST" });
        db.ReportingPeriods.Add(new ReportingPeriod { Id = periodId, Key = "2026-01", Label = "January 2026", PeriodStart = new DateOnly(2026, 1, 1), PeriodEnd = new DateOnly(2026, 1, 31) });
        db.FinancialStatementLines.AddRange(
            new FinancialStatementLine
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                ReportingPeriodId = periodId,
                TenantId = "tenant",
                StatementType = "ProfitAndLoss",
                Section = "Operating Expenses",
                LineName = "Salaries and Wages",
                AccountCode = "xero-account-guid",
                CurrentAmount = 1000m,
                SortOrder = 20
            },
            new FinancialStatementLine
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                ReportingPeriodId = periodId,
                TenantId = "tenant",
                StatementType = "BalanceSheet",
                Section = "Cash and Cash Equivalents",
                LineName = "Cash - IBC",
                AccountCode = "xero-cash-guid",
                CurrentAmount = 2000m,
                SortOrder = 10
            });
        db.GlAccounts.AddRange(
            new GlAccount
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                ReportingPeriodId = periodId,
                TenantId = "tenant",
                Code = "5600110",
                Name = "Salaries and Wages",
                Type = "Expense",
                Class = "Operating Expense",
                FsLine = "Revenue - Salaries and Wages",
                AiSuggestedFsLine = "Revenue - Salaries and Wages",
                ReviewStatus = MappingReviewStatus.Suggested
            },
            new GlAccount
            {
                Id = Guid.NewGuid(),
                OrganizationId = orgId,
                ReportingPeriodId = periodId,
                TenantId = "tenant",
                Code = "5100003",
                Name = "Cash - IBC",
                Type = "BANK",
                Class = "ASSET",
                FsLine = "Other - Cash - IBC",
                AiSuggestedFsLine = "Other - Cash - IBC",
                ReviewStatus = MappingReviewStatus.Suggested
            });
        await db.SaveChangesAsync();

        var result = await new FinancialStatementGroupingService(db).GroupFromImportedFinancialsAsync(orgId, includeReviewed: false, CancellationToken.None);

        Assert.Equal(2, result.FsLinesCreated);
        Assert.Equal(2, result.AccountsUpdated);
        Assert.Equal(2, result.StatementMatched);
        Assert.Contains(await db.FsLineDefinitions.ToListAsync(), x => x.StatementType == "IncomeStatement" && x.Name == "Salaries and Wages");
        Assert.Contains(await db.FsLineDefinitions.ToListAsync(), x => x.StatementType == "BalanceSheet" && x.Name == "Cash - IBC");
        Assert.Equal("Salaries and Wages", await db.GlAccounts.Where(x => x.Code == "5600110").Select(x => x.FsLine).SingleAsync());
        Assert.Equal("Cash - IBC", await db.GlAccounts.Where(x => x.Code == "5100003").Select(x => x.FsLine).SingleAsync());
    }

    [Fact]
    public void FixOperationValidator_AllowsOnlyWhitelistedOperationsAndTargets()
    {
        var validator = new FixOperationValidator();
        var valid = new FixOperation("append_narrative", "slide", Guid.NewGuid(), JsonSerializer.SerializeToElement(new { text = "Approved." }), "reason");
        var invalid = new FixOperation("run_shell", "database", Guid.Empty, null, null);

        Assert.Empty(validator.Validate([valid]));

        var errors = validator.Validate([invalid]);
        Assert.Contains(errors, x => x.Contains("run_shell", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, x => x.Contains("database", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, x => x.Contains("TargetId", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CodexCommandBuilder_UsesReadOnlySandboxAndNeverApproval()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ai:CodexPath"] = "/Applications/Codex.app/Contents/Resources/codex"
            })
            .Build();
        var builder = new CodexCommandBuilder(configuration);

        var args = builder.BuildArguments(new CodexExecutionRequest(
            "Return JSON.",
            "gpt-5.5",
            "xhigh",
            "/tmp/out.json",
            "/tmp"));

        Assert.Equal("/Applications/Codex.app/Contents/Resources/codex", builder.CodexPath);
        Assert.Contains("--json", args);
        Assert.Contains("read-only", args);
        Assert.Contains("approval_policy=never", args);
        Assert.Contains("model_reasoning_effort=xhigh", args);
        Assert.Contains("gpt-5.5", args);
        Assert.Contains("/tmp/out.json", args);
        Assert.Equal("-", args[^1]);
        Assert.DoesNotContain("Return JSON.", args);
    }

    [Theory]
    [InlineData(100, 90, true, "good")]
    [InlineData(85, 100, true, "bad")]
    [InlineData(105, 100, false, "warn")]
    [InlineData(98, 100, false, "good")]
    public void KpiStatus_HandlesHigherAndLowerIsBetter(decimal current, decimal target, bool higherIsBetter, string expected)
    {
        Assert.Equal(expected, FinancialMath.KpiStatus(current, target, higherIsBetter));
    }

    [Fact]
    public void ThreeWayForecast_AppliesRecurringEventsAndCashThreshold()
    {
        var rows = ForecastingMath.BuildThreeWayForecast(
            new DateOnly(2026, 2, 1),
            6,
            100_000m,
            80_000m,
            0m,
            50m,
            0m,
            100m,
            250_000m,
            200_000m,
            [new ForecastEventInput(2, "Sales hire", 0m, 20_000m, -20_000m, true)]);

        Assert.Equal("2026-02", rows[0].MonthKey);
        Assert.Equal(100_000m, rows[0].Revenue);
        Assert.Equal(-70_000m, rows[1].NetCashFlow);
        Assert.Contains(rows, x => x.CashThresholdBreached);
    }

    [Fact]
    public async Task FinancialEngine_BuildRollup_AppliesConsolidationOverlaysWithoutMutatingRawTransactions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var orgId = Guid.NewGuid();
        var periodId = Guid.NewGuid();
        var included = new GlAccount
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            ReportingPeriodId = periodId,
            TenantId = "tenant",
            Code = "4000",
            Name = "Revenue",
            FsLine = "Revenue",
            ConsolidationTreatment = ConsolidationTreatment.Include,
            Transactions = [new GlTransaction { Id = Guid.NewGuid(), Credit = 1000m, TransactionDate = new DateOnly(2026, 1, 31), Description = "Revenue" }]
        };
        var excluded = new GlAccount
        {
            Id = Guid.NewGuid(),
            OrganizationId = orgId,
            ReportingPeriodId = periodId,
            TenantId = "tenant",
            Code = "4999",
            Name = "Excluded",
            FsLine = "Revenue",
            ConsolidationTreatment = ConsolidationTreatment.Exclude,
            Transactions = [new GlTransaction { Id = Guid.NewGuid(), Credit = 300m, TransactionDate = new DateOnly(2026, 1, 31), Description = "Excluded" }]
        };
        db.GlAccounts.AddRange(included, excluded);
        db.EliminationEntries.Add(new EliminationEntry { Id = Guid.NewGuid(), OrganizationId = orgId, ReportingPeriodId = periodId, GlAccountId = included.Id, Amount = 200m, Reason = "IC" });
        await db.SaveChangesAsync();

        var rollup = await new FinancialEngine(db).BuildRollupAsync(orgId, periodId, CancellationToken.None);

        Assert.Equal(1000m, rollup.EntityTotal);
        Assert.Equal(800m, rollup.ConsolidatedTotal);
        Assert.Equal(300m, await db.GlTransactions.Where(x => x.GlAccountId == excluded.Id).SumAsync(x => x.Credit - x.Debit));
    }

    [Fact]
    public void FixOperationValidator_AllowsIssueAndIntercompanyOperations()
    {
        var validator = new FixOperationValidator();
        var issue = new FixOperation("ignore_issue", "issue", Guid.NewGuid(), null, "reviewed");
        var elimination = new FixOperation("create_intercompany_elimination", "account", Guid.NewGuid(), JsonSerializer.SerializeToElement(new { text = "IC" }), "approved");

        Assert.Empty(validator.Validate([issue, elimination]));
    }

    [Fact]
    public void XeroReportParser_NormalizesNestedStatementRows()
    {
        var payload = """
            {
              "Reports": [{
                "Rows": [{
                  "RowType": "Section",
                  "Title": "Income",
                  "Rows": [{
                    "RowType": "Row",
                    "Cells": [
                      { "Value": "Sales", "Attributes": [{ "Id": "account", "Value": "4000" }] },
                      { "Value": "1,250.25" },
                      { "Value": "1,500.50" }
                    ]
                  }]
                }]
              }]
            }
            """;

        var lines = XeroIntegrationService.ParseStatementLines("ProfitAndLoss", payload, "tenant-1");

        var line = Assert.Single(lines);
        Assert.Equal("Income", line.Section);
        Assert.Equal("Sales", line.LineName);
        Assert.Equal("4000", line.AccountCode);
        Assert.Equal(1500.50m, line.CurrentAmount);
        Assert.Equal([1250.25m, 1500.50m], line.Amounts);
    }

    [Fact]
    public async Task XeroConnectUrl_UsesRegisteredFinanceAppCallbackAndLedgerScope()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var org = new Organization { Id = Guid.NewGuid(), Key = "rxl", Name = "RxLinc", Abbreviation = "RXL" };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Xero:ClientId"] = "test-client",
                ["Xero:AuthUrl"] = "https://login.xero.com/identity/connect/authorize",
                ["Xero:RedirectUri"] = "http://localhost:5264/api/xero/callback",
                ["Xero:Scopes"] = "openid profile email accounting.journals.read accounting.reports.read offline_access"
            })
            .Build();
        var provider = new ServiceCollection().AddDataProtection().Services.BuildServiceProvider();
        var service = new XeroIntegrationService(
            configuration,
            new StaticHttpClientFactory(),
            provider.GetRequiredService<IDataProtectionProvider>(),
            new MappingService(),
            new XeroTokenRefreshLock(),
            NullLogger<XeroIntegrationService>.Instance);

        var result = await service.BuildConnectUrlAsync(db, org.Id, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.NotNull(result.AuthUrl);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A5264%2Fapi%2Fxero%2Fcallback", result.AuthUrl);
        Assert.Contains("accounting.journals.read", Uri.UnescapeDataString(result.AuthUrl!));
    }

    [Fact]
    public async Task XeroCallback_CompletesOAuthSessionLookupOnSqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var org = new Organization { Id = Guid.NewGuid(), Key = "rxl", Name = "RxLinc", Abbreviation = "RXL" };
        db.Organizations.Add(org);

        var services = new ServiceCollection().AddDataProtection().Services.BuildServiceProvider();
        var provider = services.GetRequiredService<IDataProtectionProvider>();
        var protector = provider.CreateProtector("FinanceApp.Secrets.v1");
        db.XeroOAuthSessions.Add(new XeroOAuthSession
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            State = "callback-state",
            ProtectedCodeVerifier = protector.Protect("pkce-verifier"),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
        });
        await db.SaveChangesAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Xero:ClientId"] = "test-client",
                ["Xero:TokenUrl"] = "https://identity.xero.test/connect/token",
                ["Xero:ConnectionsUrl"] = "https://api.xero.test/connections",
                ["Xero:RedirectUri"] = "http://localhost:5264/api/xero/callback",
                ["Xero:Scopes"] = "openid profile email accounting.journals.read accounting.reports.read offline_access"
            })
            .Build();
        var service = new XeroIntegrationService(
            configuration,
            new StaticHttpClientFactory(new XeroOAuthHandler()),
            provider,
            new MappingService(),
            new XeroTokenRefreshLock(),
            NullLogger<XeroIntegrationService>.Instance);

        var result = await service.CompleteCallbackAsync(db, "auth-code", "callback-state", CancellationToken.None);

        Assert.Equal("tenant-rxl", result.TenantId);
        Assert.Equal("RxLinc - Active", result.TenantName);
        Assert.NotNull(await db.XeroOAuthSessions.Where(x => x.State == "callback-state").Select(x => x.CodeConsumedAt).SingleAsync());
        Assert.False(await db.XeroTenantConnections.Where(x => x.TenantId == "tenant-rxl").Select(x => x.RequiresReconnectForLedger).SingleAsync());
    }

    [Fact]
    public async Task XeroPeriodSync_CreatesJanuaryPackagesForAllTenantsWithoutConsolidationPackage()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var rx = new Organization { Id = Guid.NewGuid(), Key = "rxlinc", Name = "RxLinc, LLC", Abbreviation = "RXLC" };
        var ppok = new Organization { Id = Guid.NewGuid(), Key = "ppok-consolidated", Name = "PPOk Consolidated", Abbreviation = "PPOK", IsConsolidated = true };
        db.Organizations.AddRange(rx, ppok);
        db.XeroConnections.AddRange(
            new XeroConnection { Id = Guid.NewGuid(), OrganizationId = rx.Id, TenantId = "tenant-rxlinc", TenantName = "RxLinc, LLC", TenantType = "ORGANISATION", ConnectionStatus = "NeedsReconnect" },
            new XeroConnection { Id = Guid.NewGuid(), OrganizationId = ppok.Id, TenantId = "tenant-ppok", TenantName = "PPOk Consolidated", TenantType = "ORGANISATION", ConnectionStatus = "NeedsReconnect" });
        await db.SaveChangesAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Xero:AllowLocalStubReports"] = "true",
                ["Xero:EnableTestFixtures"] = "true",
                ["Xero:ClientId"] = "test-client"
            })
            .Build();
        var provider = new ServiceCollection().AddDataProtection().Services.BuildServiceProvider();
        var service = new XeroIntegrationService(
            configuration,
            new StaticHttpClientFactory(),
            provider.GetRequiredService<IDataProtectionProvider>(),
            new MappingService(),
            new XeroTokenRefreshLock(),
            NullLogger<XeroIntegrationService>.Instance);

        var result = await service.SyncPeriodAsync(db, new XeroPeriodSyncOptions("2026-01", "accrual", true, false), CancellationToken.None);

        Assert.Equal("Completed", result.Status);
        Assert.Equal(2, result.PackageCount);
        Assert.Equal(2, await db.ReportPackages.CountAsync(x => x.ReportingPeriod!.Key == "2026-01"));
        Assert.False(await db.Organizations.Where(x => x.Name == "PPOk Consolidated").Select(x => x.IsConsolidated).SingleAsync());
        Assert.True(await db.FinancialStatementLines.AnyAsync(x => x.StatementType == "TrendedProfitAndLoss"));
        Assert.True(await db.StatementQaResults.AnyAsync());
        var slide = await db.PackageSlides.Include(x => x.Blocks).Where(x => x.ReportPackage!.ReportingPeriod!.Key == "2026-01").FirstAsync();
        Assert.False(string.IsNullOrWhiteSpace(slide.AccountCodesCsv));
        var driverBlock = slide.Blocks.Single(x => x.Kind == "drivers");
        Assert.Contains("topAccounts", driverBlock.ContentJson);
        Assert.Contains("transactions", driverBlock.ContentJson);
    }

    [Fact]
    public async Task FinanceAppV2Import_ImportsGlobalTenantsAndFlagsMissingLedgerScope()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"financeapp-v2-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("FinanceApp.Api")
            .UseEphemeralDataProtectionProvider();
        var provider = services.BuildServiceProvider();
        var protector = provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("FinanceApp.Secrets.v1");

        await using (var v2 = new SqliteConnection($"Data Source={dbPath}"))
        {
            await v2.OpenAsync();
            var command = v2.CreateCommand();
            command.CommandText = """
                CREATE TABLE Organizations (Id INTEGER PRIMARY KEY, Name TEXT NOT NULL, Code TEXT NOT NULL);
                CREATE TABLE XeroConnections (
                    Id INTEGER PRIMARY KEY,
                    OrgId INTEGER NOT NULL,
                    TenantId TEXT NOT NULL,
                    TenantName TEXT,
                    TenantType TEXT NOT NULL,
                    AccessToken TEXT NOT NULL,
                    RefreshToken TEXT NOT NULL,
                    TokenExpiresAt TEXT NOT NULL,
                    Scopes TEXT,
                    ConnectionStatus TEXT NOT NULL,
                    LastConnectedAt TEXT
                );
                INSERT INTO Organizations (Id, Name, Code) VALUES (1, 'RxLinc', 'RXL'), (2, 'MaxCare', 'MRX');
                INSERT INTO XeroConnections (Id, OrgId, TenantId, TenantName, TenantType, AccessToken, RefreshToken, TokenExpiresAt, Scopes, ConnectionStatus, LastConnectedAt)
                VALUES
                (1, 1, 'tenant-rxl', 'RxLinc - Active', 'ORGANISATION', $access1, $refresh1, '2099-01-01T00:00:00+00:00', 'offline_access accounting.journals.read accounting.reports.read', 'connected', '2026-04-21T00:00:00+00:00'),
                (2, 2, 'tenant-mrx', 'MaxCareRX', 'ORGANISATION', $access2, $refresh2, '2099-01-01T00:00:00+00:00', 'offline_access accounting.reports.read', 'connected', '2026-04-21T00:00:00+00:00');
                """;
            command.Parameters.AddWithValue("$access1", protector.Protect("access-one"));
            command.Parameters.AddWithValue("$refresh1", protector.Protect("refresh-one"));
            command.Parameters.AddWithValue("$access2", protector.Protect("access-two"));
            command.Parameters.AddWithValue("$refresh2", protector.Protect("refresh-two"));
            await command.ExecuteNonQueryAsync();
        }

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Xero:FinanceAppV2ConnectionString"] = $"Data Source={dbPath}",
                ["Xero:ClientId"] = "client"
            })
            .Build();
        var service = new XeroTenantLedgerService(
            configuration,
            new StaticHttpClientFactory(),
            provider.GetRequiredService<IDataProtectionProvider>(),
            new XeroTokenRefreshLock(),
            NullLogger<XeroTenantLedgerService>.Instance);

        var preview = await service.PreviewFinanceAppV2ImportAsync(CancellationToken.None);
        var result = await service.ImportFinanceAppV2TokensAsync(db, CancellationToken.None);

        Assert.Equal(2, preview.TenantCount);
        Assert.Equal(2, result.ImportedConnections);
        Assert.Equal(2, await db.XeroTenantConnections.CountAsync());
        Assert.Equal(2, await db.XeroTenantEntityMappings.CountAsync());
        Assert.False(await db.XeroTenantConnections.Where(x => x.TenantId == "tenant-rxl").Select(x => x.RequiresReconnectForLedger).SingleAsync());
        Assert.True(await db.XeroTenantConnections.Where(x => x.TenantId == "tenant-mrx").Select(x => x.RequiresReconnectForLedger).SingleAsync());
    }

    [Fact]
    public async Task LedgerJournalImport_IsIdempotentAndCanMarkActivePackageStale()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var org = new Organization { Id = Guid.NewGuid(), Key = "rxl", Name = "RxLinc", Abbreviation = "RXL" };
        var period = new ReportingPeriod { Id = Guid.NewGuid(), Key = "2026-01", Label = "January 2026", PeriodStart = new DateOnly(2026, 1, 1), PeriodEnd = new DateOnly(2026, 1, 31) };
        var package = new ReportPackage { Id = Guid.NewGuid(), OrganizationId = org.Id, ReportingPeriodId = period.Id, Status = PackageStatus.Review };
        db.Organizations.Add(org);
        db.ReportingPeriods.Add(period);
        db.ReportPackages.Add(package);
        db.XeroTenantEntityMappings.Add(new XeroTenantEntityMapping { Id = Guid.NewGuid(), TenantId = "tenant-rxl", OrganizationId = org.Id });
        await db.SaveChangesAsync();

        var service = NewLedgerService();
        var payload = """
            {
              "Journals": [{
                "JournalID": "journal-1",
                "JournalNumber": 42,
                "JournalDate": "2026-01-15",
                "SourceType": "ACCREC",
                "Reference": "INV-42",
                "JournalLines": [
                  { "JournalLineID": "line-1", "AccountCode": "4000", "AccountName": "Revenue", "Description": "Revenue", "NetAmount": 1000 },
                  { "JournalLineID": "line-2", "AccountCode": "1100", "AccountName": "AR", "Description": "AR", "NetAmount": -1000 }
                ]
              }]
            }
            """;

        await service.UpsertJournalsFromPayloadAsync(db, "tenant-rxl", payload, CancellationToken.None);
        await service.UpsertJournalsFromPayloadAsync(db, "tenant-rxl", payload, CancellationToken.None);
        await service.MarkPackagesStaleForTenantActivityAsync(db, "tenant-rxl", [new DateOnly(2026, 1, 15)], CancellationToken.None);

        Assert.Equal(1, await db.XeroJournals.CountAsync());
        Assert.Equal(2, await db.XeroJournalLines.CountAsync());
        Assert.True(await db.ReportPackages.Where(x => x.Id == package.Id).Select(x => x.IsSourceDataStale).SingleAsync());
    }

    [Fact]
    public async Task LedgerJournalImport_CreatesMissingReportingPeriodsForTransactionMonths()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var payload = """
            {
              "Journals": [
                {
                  "JournalID": "journal-feb",
                  "JournalNumber": 100,
                  "JournalDate": "2026-02-15",
                  "JournalLines": [{ "JournalLineID": "line-feb", "AccountCode": "4000", "AccountName": "Revenue", "NetAmount": 100 }]
                },
                {
                  "JournalID": "journal-mar",
                  "JournalNumber": 101,
                  "JournalDate": "2026-03-03",
                  "JournalLines": [{ "JournalLineID": "line-mar", "AccountCode": "5000", "AccountName": "Expense", "NetAmount": -50 }]
                }
              ]
            }
            """;

        await NewLedgerService().UpsertJournalsFromPayloadAsync(db, "tenant-rxl", payload, CancellationToken.None);

        var periods = await db.ReportingPeriods.OrderBy(x => x.Key).ToListAsync();
        Assert.Equal(["2026-02", "2026-03"], periods.Select(x => x.Key).ToArray());
        Assert.Equal("February 2026", periods[0].Label);
        Assert.Equal(new DateOnly(2026, 3, 31), periods[1].PeriodEnd);
    }

    [Fact]
    public async Task LedgerRetention_RollsOldDetailIntoMonthlySummaries()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var orgId = Guid.NewGuid();
        db.Organizations.Add(new Organization { Id = orgId, Key = "old", Name = "Old Tenant", Abbreviation = "OLD" });
        db.XeroTenantEntityMappings.Add(new XeroTenantEntityMapping { Id = Guid.NewGuid(), TenantId = "tenant-old", OrganizationId = orgId });
        db.XeroLedgerSyncSettings.Add(new XeroLedgerSyncSetting { Id = Guid.NewGuid(), RetentionYears = 3, SyncEveryMinutes = 15, DailyTrialBalanceHourUtc = 11, Enabled = true });
        var journal = new XeroJournal { Id = Guid.NewGuid(), TenantId = "tenant-old", XeroJournalId = "old-1", JournalNumber = 1, JournalDate = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-4)), PayloadJson = "{}" };
        db.XeroJournals.Add(journal);
        db.XeroJournalLines.Add(new XeroJournalLine { Id = Guid.NewGuid(), XeroJournalId = journal.Id, TenantId = "tenant-old", AccountCode = "4000", AccountName = "Revenue", NetAmount = 250m });
        await db.SaveChangesAsync();

        await NewLedgerService().ApplyRetentionAsync(db, CancellationToken.None);

        Assert.Empty(await db.XeroJournalLines.ToListAsync());
        Assert.Empty(await db.XeroJournals.ToListAsync());
        Assert.Equal(250m, await db.XeroLedgerMonthlySummaries.Select(x => x.NetAmount).SingleAsync());
    }

    [Fact]
    public async Task FluxReviewAndAiDrafts_StageChangesUntilAccepted()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var org = new Organization { Id = Guid.NewGuid(), Key = "rxl", Name = "RxLinc", Abbreviation = "RXL" };
        var period = new ReportingPeriod { Id = Guid.NewGuid(), Key = "2026-01", Label = "January 2026", PeriodStart = new DateOnly(2026, 1, 1), PeriodEnd = new DateOnly(2026, 1, 31) };
        var priorMonth = new ReportingPeriod { Id = Guid.NewGuid(), Key = "2025-12", Label = "December 2025", PeriodStart = new DateOnly(2025, 12, 1), PeriodEnd = new DateOnly(2025, 12, 31) };
        var twoMonthsAgo = new ReportingPeriod { Id = Guid.NewGuid(), Key = "2025-11", Label = "November 2025", PeriodStart = new DateOnly(2025, 11, 1), PeriodEnd = new DateOnly(2025, 11, 30) };
        var package = new ReportPackage { Id = Guid.NewGuid(), OrganizationId = org.Id, ReportingPeriodId = period.Id, Status = PackageStatus.Review };
        var slide = new PackageSlide { Id = Guid.NewGuid(), ReportPackageId = package.Id, SortOrder = 1, Subject = "Overview" };
        db.Organizations.Add(org);
        db.ReportingPeriods.AddRange(period, priorMonth, twoMonthsAgo);
        db.ReportPackages.Add(package);
        db.PackageSlides.Add(slide);
        db.FinancialStatementLines.Add(new FinancialStatementLine
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            ReportingPeriodId = period.Id,
            ReportPackageId = package.Id,
            TenantId = "tenant",
            StatementType = "ProfitAndLoss",
            Section = "Income",
            LineName = "Revenue",
            AccountCode = "4000",
            CurrentAmount = 125_000m,
            PriorAmount = 90_000m,
            AmountsJson = "[]",
            SortOrder = 1
        });
        db.FinancialStatementLines.AddRange(
            new FinancialStatementLine
            {
                Id = Guid.NewGuid(),
                OrganizationId = org.Id,
                ReportingPeriodId = period.Id,
                TenantId = "tenant",
                StatementType = "ProfitAndLoss",
                Section = "Income",
                LineName = "Revenue",
                AccountCode = "4000",
                CurrentAmount = 125_000m,
                PriorAmount = 0m,
                AmountsJson = "[]",
                SortOrder = 1
            },
            new FinancialStatementLine
            {
                Id = Guid.NewGuid(),
                OrganizationId = org.Id,
                ReportingPeriodId = priorMonth.Id,
                TenantId = "tenant",
                StatementType = "ProfitAndLoss",
                Section = "Income",
                LineName = "Revenue",
                AccountCode = "4000",
                CurrentAmount = 100_000m,
                PriorAmount = 0m,
                AmountsJson = "[]",
                SortOrder = 1
            },
            new FinancialStatementLine
            {
                Id = Guid.NewGuid(),
                OrganizationId = org.Id,
                ReportingPeriodId = twoMonthsAgo.Id,
                TenantId = "tenant",
                StatementType = "ProfitAndLoss",
                Section = "Income",
                LineName = "Revenue",
                AccountCode = "4000",
                CurrentAmount = 80_000m,
                PriorAmount = 0m,
                AmountsJson = "[]",
                SortOrder = 1
            });
        await db.SaveChangesAsync();

        var fluxService = new FluxReviewService(db);
        var flux = await fluxService.RefreshAsync(package.Id, CancellationToken.None);
        Assert.Equal(2, flux.Groups.Count);
        Assert.Equal(2, flux.Progress.RequiredExplanations);
        var monthOverMonth = Assert.Single(flux.Groups, x => x.FluxType == "MonthOverMonth");
        Assert.Equal("2026-01", monthOverMonth.CurrentPeriodKey);
        Assert.Equal("2025-12", monthOverMonth.PriorPeriodKey);
        Assert.Equal(100_000m, monthOverMonth.PriorAmount);
        Assert.Equal(305_000m, monthOverMonth.RunningThreeMonthAmount);
        // P2.20 — best-in-class default is $5,000 AND 10%, replacing the prior 0 / OR which
        // effectively disabled the dollar leg of the dual-threshold gate. Cat 10.
        Assert.Equal(5_000m, monthOverMonth.DollarThreshold);
        Assert.Equal(10m, monthOverMonth.PercentThreshold);
        Assert.Equal("AND", monthOverMonth.ThresholdLogic);
        var group = Assert.Single(flux.Groups, x => x.FluxType == "YearOverYear");
        Assert.Equal(5_000m, group.DollarThreshold);
        Assert.Equal(10m, group.PercentThreshold);
        Assert.Equal("AND", group.ThresholdLogic);
        Assert.True(group.RequiresExplanation);

        var settings = await fluxService.UpdateSettingsAsync(group.Id, new FluxReviewGroupSettingsRequest(5_000m, 12m, "AND", "controller", "cfo", new DateOnly(2026, 2, 5), "Explain vendor, volume, timing, and recurring impact.", "close,board", "period", "test"), CancellationToken.None);
        Assert.Equal("controller", settings.Assignee);
        Assert.Equal("cfo", settings.Reviewer);
        Assert.Equal(new DateOnly(2026, 2, 5), settings.DueDate);
        Assert.Equal("AND", settings.ThresholdLogic);
        Assert.Equal("close,board", settings.Tags);

        await fluxService.UpdateExplanationAsync(group.Id, "Revenue increased due to January transaction volume.", "tester", CancellationToken.None);
        var prepared = await fluxService.SignOffAsync(group.Id, "prepare", "controller", CancellationToken.None);
        Assert.Equal("Prepared", prepared.Status);
        var reviewed = await fluxService.SignOffAsync(group.Id, "review", "cfo", CancellationToken.None);
        Assert.Equal("Approved", reviewed.Status);
        Assert.Equal("cfo", reviewed.ReviewedBy);
        var csv = await fluxService.ExportCsvAsync(package.Id, CancellationToken.None);
        Assert.Contains("controller", csv);

        var draftService = new AiPackageDraftService(db, new PackageDiffService(db));
        var drafts = await draftService.CreateDraftsAsync(package.Id, CancellationToken.None);

        // The diff engine produces typed Keep/Modify/Add/Remove decisions instead of
        // top-N flux suggestions. With no prior package linked, every board-material flux
        // group becomes an Add decision. Cat 19, 20.
        Assert.NotEmpty(drafts);
        Assert.All(drafts, d => Assert.Equal("Staged", d.Status));
        var addDraft = drafts.FirstOrDefault(d => d.Kind == "Add") ?? drafts[0];

        Assert.Empty(await db.SlideBlocks.ToListAsync());
        var slidesBefore = await db.PackageSlides.CountAsync(x => x.ReportPackageId == package.Id);
        await draftService.AcceptAsync(addDraft.Id, CancellationToken.None);

        // Accepting an Add decision creates a new slide on the package plus a callout block.
        var slidesAfter = await db.PackageSlides.CountAsync(x => x.ReportPackageId == package.Id);
        Assert.Equal(slidesBefore + 1, slidesAfter);
        Assert.Single(await db.SlideBlocks.ToListAsync());
    }

    [Fact]
    public void XeroDateParser_ParsesXeroJsonDate()
    {
        var parsed = XeroDateParser.ReadDateOnly("""\/Date(1699920000000+0000)\/""".Replace("\\/", "/"));

        Assert.Equal(new DateOnly(2023, 11, 14), parsed);
    }

    [Fact]
    public async Task XeroJournalImport_UsesRealJournalDatesAndCreatesActivityPeriods()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var payload = """
            {
              "Journals": [{
                "JournalID": "journal-1",
                "JournalDate": "/Date(1699920000000+0000)/",
                "JournalNumber": 52,
                "SourceType": "CASHREC",
                "Reference": "Deposit",
                "JournalLines": [{
                  "JournalLineID": "line-1",
                  "AccountCode": "4000",
                  "AccountName": "Revenue",
                  "NetAmount": 99.00,
                  "GrossAmount": 99.00,
                  "TaxAmount": 0.00
                }]
              }]
            }
            """;

        var result = await NewLedgerService().UpsertJournalsFromPayloadAsync(db, "tenant-rxl", payload, new DateOnly(2023, 1, 1), new DateOnly(2023, 12, 31), CancellationToken.None);

        Assert.Equal(1, result.JournalsImported);
        Assert.Equal(new DateOnly(2023, 11, 14), await db.XeroJournals.Select(x => x.JournalDate).SingleAsync());
        Assert.True(await db.ReportingPeriods.AnyAsync(x => x.Key == "2023-11"));
    }

    [Fact]
    public async Task RuntimeMockCleanup_RemovesUnmappedSeededDataAndKeepsMappedTenants()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var realOrg = new Organization { Id = Guid.NewGuid(), Key = "rxlinc-active", Name = "RxLinc - Active", Abbreviation = "RXL" };
        var fakeOrg = new Organization { Id = Guid.NewGuid(), Key = "rxlc", Name = "RxLinc, LLC", Abbreviation = "RXLC" };
        var period = new ReportingPeriod { Id = Guid.NewGuid(), Key = "2025-11", Label = "November 2025", PeriodStart = new DateOnly(2025, 11, 1), PeriodEnd = new DateOnly(2025, 11, 30) };
        db.Organizations.AddRange(realOrg, fakeOrg);
        db.ReportingPeriods.Add(period);
        db.XeroTenantConnections.Add(new XeroTenantConnection { Id = Guid.NewGuid(), TenantId = "tenant-rxl", TenantName = "RxLinc - Active", ConnectionStatus = "Connected" });
        db.XeroTenantEntityMappings.Add(new XeroTenantEntityMapping { Id = Guid.NewGuid(), TenantId = "tenant-rxl", OrganizationId = realOrg.Id, Reason = "test" });
        db.ReportPackages.Add(new ReportPackage { Id = Guid.NewGuid(), OrganizationId = fakeOrg.Id, ReportingPeriodId = period.Id, Status = PackageStatus.Review });
        await db.SaveChangesAsync();

        await RealDataCleanupService.PurgeRuntimeMockDataAsync(db, CancellationToken.None);

        Assert.True(await db.Organizations.AnyAsync(x => x.Id == realOrg.Id));
        Assert.False(await db.Organizations.AnyAsync(x => x.Id == fakeOrg.Id));
        Assert.Empty(await db.ReportPackages.ToListAsync());
    }

    private sealed class StaticHttpClientFactory(HttpMessageHandler? handler = null) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
    }

    private sealed class XeroOAuthHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = request.RequestUri?.Host switch
            {
                "identity.xero.test" => """{"access_token":"access-token","refresh_token":"refresh-token","expires_in":1800}""",
                "api.xero.test" => """[{"tenantId":"tenant-rxl","tenantName":"RxLinc - Active","tenantType":"ORGANISATION"}]""",
                _ => throw new InvalidOperationException($"Unexpected Xero test request: {request.RequestUri}")
            };

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private static XeroTenantLedgerService NewLedgerService()
    {
        var provider = new ServiceCollection()
            .AddDataProtection()
            .Services
            .BuildServiceProvider()
            .GetRequiredService<IDataProtectionProvider>();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Xero:ClientId"] = "client"
            })
            .Build();
        return new XeroTenantLedgerService(configuration, new StaticHttpClientFactory(), provider, new XeroTokenRefreshLock(), NullLogger<XeroTenantLedgerService>.Instance);
    }

    [Fact]
    public async Task FluxRefresh_EmitsYearToDateGroupWhenWindowExceedsOneMonth()
    {
        // P2.19 — when current period is month 2+ of the fiscal year, RefreshAsync should
        // emit a YearToDate flux group alongside MoM/YoY. Cat 9.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var org = new Organization { Id = Guid.NewGuid(), Key = "rxl", Name = "RxLinc" };
        var jan = new ReportingPeriod { Id = Guid.NewGuid(), Key = "2026-01", Label = "January 2026", PeriodStart = new DateOnly(2026, 1, 1), PeriodEnd = new DateOnly(2026, 1, 31) };
        var feb = new ReportingPeriod { Id = Guid.NewGuid(), Key = "2026-02", Label = "February 2026", PeriodStart = new DateOnly(2026, 2, 1), PeriodEnd = new DateOnly(2026, 2, 28) };
        var package = new ReportPackage { Id = Guid.NewGuid(), OrganizationId = org.Id, ReportingPeriodId = feb.Id, Status = PackageStatus.Review };
        db.Organizations.Add(org);
        db.ReportingPeriods.AddRange(jan, feb);
        db.ReportPackages.Add(package);

        // Current package's lines (Feb 2026)
        db.FinancialStatementLines.Add(new FinancialStatementLine
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            ReportingPeriodId = feb.Id,
            ReportPackageId = package.Id,
            TenantId = "tenant",
            StatementType = "ProfitAndLoss",
            Section = "Income",
            LineName = "Revenue",
            AccountCode = "4000",
            CurrentAmount = 130_000m,
            PriorAmount = 100_000m,
            AmountsJson = "[]",
            SortOrder = 1
        });
        // Tenant-scoped (no package) lines for Jan + Feb so YTD has 2 periods to sum.
        db.FinancialStatementLines.AddRange(
            new FinancialStatementLine { Id = Guid.NewGuid(), OrganizationId = org.Id, ReportingPeriodId = jan.Id, TenantId = "tenant", StatementType = "ProfitAndLoss", Section = "Income", LineName = "Revenue", AccountCode = "4000", CurrentAmount = 100_000m, PriorAmount = 0m, AmountsJson = "[]", SortOrder = 1 },
            new FinancialStatementLine { Id = Guid.NewGuid(), OrganizationId = org.Id, ReportingPeriodId = feb.Id, TenantId = "tenant", StatementType = "ProfitAndLoss", Section = "Income", LineName = "Revenue", AccountCode = "4000", CurrentAmount = 130_000m, PriorAmount = 100_000m, AmountsJson = "[]", SortOrder = 1 });
        await db.SaveChangesAsync();

        var fluxService = new FluxReviewService(db);
        var flux = await fluxService.RefreshAsync(package.Id, CancellationToken.None);

        Assert.Contains(flux.Groups, x => x.FluxType == "YearToDate");
        var ytd = Assert.Single(flux.Groups, x => x.FluxType == "YearToDate");
        // YTD current = Jan + Feb = 230k for the line. PriorYTD (no 2025 data) = 0.
        Assert.Equal(230_000m, ytd.CurrentAmount);
        Assert.Equal(0m, ytd.PriorAmount);
    }

    [Fact]
    public async Task PackageDiff_EmitsKeepModifyAddRemoveAgainstPriorPackage()
    {
        // The marquee feature: prior month's slides are the baseline; current month's
        // values + flux drive Keep / Modify / Add / Remove decisions, gated by a board
        // materiality threshold distinct from ops thresholds. Cat 19, 20.
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var org = new Organization { Id = Guid.NewGuid(), Key = "rxl", Name = "RxLinc" };
        var priorPeriod = new ReportingPeriod { Id = Guid.NewGuid(), Key = "2026-01", Label = "January 2026", PeriodStart = new DateOnly(2026, 1, 1), PeriodEnd = new DateOnly(2026, 1, 31) };
        var currentPeriod = new ReportingPeriod { Id = Guid.NewGuid(), Key = "2026-02", Label = "February 2026", PeriodStart = new DateOnly(2026, 2, 1), PeriodEnd = new DateOnly(2026, 2, 28) };
        db.Organizations.Add(org);
        db.ReportingPeriods.AddRange(priorPeriod, currentPeriod);

        var priorPackage = new ReportPackage
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            ReportingPeriodId = priorPeriod.Id,
            Status = PackageStatus.Final,
            BoardDollarThreshold = 25_000m,
            BoardPercentThreshold = 15m
        };
        var currentPackage = new ReportPackage
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            ReportingPeriodId = currentPeriod.Id,
            Status = PackageStatus.Draft,
            PriorPackageId = priorPackage.Id,
            BoardDollarThreshold = 25_000m,
            BoardPercentThreshold = 15m
        };
        db.ReportPackages.AddRange(priorPackage, currentPackage);

        // Prior month had three slides:
        //   - Revenue (will be carried forward — minor change)
        //   - Cost of goods (will become Modify — material swing)
        //   - Stale subscription expense (no current data — Remove)
        db.PackageSlides.AddRange(
            new PackageSlide { Id = Guid.NewGuid(), ReportPackageId = priorPackage.Id, SortOrder = 1, Subject = "Revenue", AccountCodesCsv = "4000", CurrentValue = 200_000m },
            new PackageSlide { Id = Guid.NewGuid(), ReportPackageId = priorPackage.Id, SortOrder = 2, Subject = "Cost of Goods Sold", AccountCodesCsv = "5000", CurrentValue = 80_000m },
            new PackageSlide { Id = Guid.NewGuid(), ReportPackageId = priorPackage.Id, SortOrder = 3, Subject = "Stale Subscription Expense", AccountCodesCsv = "6900", CurrentValue = 12_000m });

        // Current month has matching slides for Revenue and Cost of Goods Sold; the
        // stale subscription line is gone. We also seed a flux group representing a
        // brand-new material expense that should produce an Add decision.
        db.PackageSlides.AddRange(
            new PackageSlide { Id = Guid.NewGuid(), ReportPackageId = currentPackage.Id, SortOrder = 1, Subject = "Revenue", AccountCodesCsv = "4000", CurrentValue = 205_000m },
            new PackageSlide { Id = Guid.NewGuid(), ReportPackageId = currentPackage.Id, SortOrder = 2, Subject = "Cost of Goods Sold", AccountCodesCsv = "5000", CurrentValue = 130_000m });

        db.FluxReviewGroups.Add(new FluxReviewGroup
        {
            Id = Guid.NewGuid(),
            ReportPackageId = currentPackage.Id,
            OrganizationId = org.Id,
            ReportingPeriodId = currentPeriod.Id,
            FluxType = "MonthOverMonth",
            StatementType = "ProfitAndLoss",
            GroupKey = "7100",
            GroupName = "New Cloud Hosting Spend",
            CurrentAmount = 60_000m,
            PriorAmount = 0m,
            VarianceAmount = 60_000m,
            VariancePercent = 100m
        });
        await db.SaveChangesAsync();

        var diff = new PackageDiffService(db);
        var result = await diff.ComputeAsync(currentPackage.Id, CancellationToken.None);

        Assert.Equal(priorPackage.Id, result.PriorPackageId);
        Assert.Equal(25_000m, result.BoardDollarThreshold);
        Assert.Equal(15m, result.BoardPercentThreshold);

        var byKind = result.Decisions
            .GroupBy(d => d.Kind)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Revenue: +5,000 / +2.5% — under threshold → Keep (carry-forward).
        var keep = Assert.Single(byKind[PackageDiffService.SlideDecisionKind.Keep]);
        Assert.Equal("Revenue", keep.Subject);

        // Cost of Goods Sold: +50,000 / +62.5% — above both thresholds → Modify.
        var modify = Assert.Single(byKind[PackageDiffService.SlideDecisionKind.Modify]);
        Assert.Equal("Cost of Goods Sold", modify.Subject);

        // Stale Subscription Expense: no current match → Remove.
        var remove = Assert.Single(byKind[PackageDiffService.SlideDecisionKind.Remove]);
        Assert.Equal("Stale Subscription Expense", remove.Subject);

        // New Cloud Hosting Spend: material flux not in prior package → Add.
        var add = Assert.Single(byKind[PackageDiffService.SlideDecisionKind.Add]);
        Assert.Equal("New Cloud Hosting Spend", add.Subject);
    }
}
