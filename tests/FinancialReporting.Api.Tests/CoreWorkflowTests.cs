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
            NullLogger<XeroIntegrationService>.Instance);

        var result = await service.SyncPeriodAsync(db, new XeroPeriodSyncOptions("2026-01", "accrual", true, false), CancellationToken.None);

        Assert.Equal("Completed", result.Status);
        Assert.Equal(2, result.PackageCount);
        Assert.Equal(2, await db.ReportPackages.CountAsync(x => x.ReportingPeriod!.Key == "2026-01"));
        Assert.False(await db.Organizations.Where(x => x.Name == "PPOk Consolidated").Select(x => x.IsConsolidated).SingleAsync());
        Assert.True(await db.FinancialStatementLines.AnyAsync(x => x.StatementType == "TrendedProfitAndLoss"));
        Assert.True(await db.StatementQaResults.AnyAsync());
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
        Assert.Equal(0m, monthOverMonth.DollarThreshold);
        Assert.Equal(10m, monthOverMonth.PercentThreshold);
        var group = Assert.Single(flux.Groups, x => x.FluxType == "YearOverYear");
        Assert.Equal(0m, group.DollarThreshold);
        Assert.Equal(10m, group.PercentThreshold);
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

        var draftService = new AiPackageDraftService(db);
        var drafts = await draftService.CreateDraftsAsync(package.Id, CancellationToken.None);

        Assert.Single(drafts);
        Assert.Empty(await db.SlideBlocks.ToListAsync());
        await draftService.AcceptAsync(drafts[0].Id, CancellationToken.None);
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
        return new XeroTenantLedgerService(configuration, new StaticHttpClientFactory(), provider, NullLogger<XeroTenantLedgerService>.Instance);
    }
}
