using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Services;

public sealed class XeroTenantLedgerService(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<XeroTenantLedgerService> logger)
{
    private const string LedgerScope = "accounting.journals.read";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("FinanceApp.Secrets.v1");

    public async Task<XeroImportPreview> PreviewFinanceAppV2ImportAsync(CancellationToken cancellationToken)
    {
        var rows = await ReadFinanceAppV2RowsAsync(cancellationToken);
        return new XeroImportPreview(
            rows.Source,
            rows.Connections.Count,
            rows.Connections.Select(x => new XeroImportTenantPreview(
                x.TenantId,
                x.TenantName,
                x.OrgName,
                HasLedgerScope(x.Scopes),
                x.TokenExpiresAt)).ToArray(),
            rows.Connections.Count == 0
                ? "No Finance App V2 Xero tenants were found."
                : $"Found {rows.Connections.Count} Finance App V2 Xero tenant(s).");
    }

    public async Task<XeroImportResult> ImportFinanceAppV2TokensAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var rows = await ReadFinanceAppV2RowsAsync(cancellationToken);
        if (rows.Connections.Count == 0)
        {
            return new XeroImportResult(0, $"No Finance App V2 Xero tenants were found at {rows.Source}.");
        }

        var imported = 0;
        var reconnectRequired = 0;
        foreach (var row in rows.Connections)
        {
            var accessToken = Unprotect(row.AccessToken);
            var refreshToken = Unprotect(row.RefreshToken);
            var hasTokens = !string.IsNullOrWhiteSpace(accessToken) && !string.IsNullOrWhiteSpace(refreshToken);
            var hasLedgerScope = HasLedgerScope(row.Scopes);
            var status = hasTokens ? "Connected" : "NeedsReconnect";
            string? lastError = hasTokens ? null : "Finance App V2 token could not be decrypted with the configured DataProtection key ring.";

            var tokenExpiresAt = row.TokenExpiresAt;
            if (hasTokens && tokenExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(5))
            {
                try
                {
                    var refreshed = await RefreshTokenAsync(refreshToken, cancellationToken);
                    accessToken = refreshed.AccessToken;
                    refreshToken = refreshed.RefreshToken;
                    tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn);
                    status = "Connected";
                    lastError = null;
                }
                catch (Exception ex)
                {
                    status = "NeedsReconnect";
                    lastError = "Imported refresh token could not be refreshed. Reconnect this Xero tenant.";
                    logger.LogWarning(ex, "Finance App V2 token import could not refresh tenant {TenantId}.", row.TenantId);
                }
            }

            var organization = await EnsureOrganizationAsync(db, row.OrgCode, row.OrgName, row.TenantName, cancellationToken);
            var tenant = await db.XeroTenantConnections.FirstOrDefaultAsync(x => x.TenantId == row.TenantId, cancellationToken)
                         ?? new XeroTenantConnection
                         {
                             Id = Guid.NewGuid(),
                             TenantId = row.TenantId,
                             CreatedAt = DateTimeOffset.UtcNow
                         };

            tenant.TenantName = row.TenantName;
            tenant.TenantType = row.TenantType;
            tenant.EncryptedAccessToken = hasTokens ? Protect(accessToken) : "";
            tenant.EncryptedRefreshToken = hasTokens ? Protect(refreshToken) : "";
            tenant.TokenExpiresAt = tokenExpiresAt;
            tenant.Scopes = row.Scopes;
            tenant.ConnectionStatus = status;
            tenant.RequiresReconnectForLedger = !hasLedgerScope;
            tenant.LastConnectedAt = row.LastConnectedAt ?? DateTimeOffset.UtcNow;
            tenant.LastError = !hasLedgerScope
                ? "Reconnect required for GL sync because Finance App V2 token lacks accounting.journals.read."
                : lastError;
            tenant.Source = "Finance App V2";
            tenant.UpdatedAt = DateTimeOffset.UtcNow;
            if (db.Entry(tenant).State == EntityState.Detached)
            {
                db.XeroTenantConnections.Add(tenant);
            }

            await UpsertTenantMappingAsync(db, row.TenantId, organization.Id, "Imported from Finance App V2", cancellationToken);
            await UpsertLegacyConnectionAsync(db, organization.Id, tenant, cancellationToken);

            imported++;
            if (!hasTokens || !hasLedgerScope || status != "Connected")
            {
                reconnectRequired++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new XeroImportResult(imported, reconnectRequired == 0
            ? $"Imported {imported} Finance App V2 Xero tenant(s)."
            : $"Imported {imported} tenant(s); {reconnectRequired} require reconnect or expanded Xero scopes for GL sync.");
    }

    public async Task<XeroLedgerSyncSetting> GetSettingsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var setting = await db.XeroLedgerSyncSettings.FirstOrDefaultAsync(cancellationToken);
        if (setting is not null)
        {
            return setting;
        }

        setting = new XeroLedgerSyncSetting
        {
            Id = Guid.NewGuid(),
            Enabled = true,
            SyncEveryMinutes = 15,
            DailyTrialBalanceHourUtc = 11,
            RetentionYears = 3,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.XeroLedgerSyncSettings.Add(setting);
        await db.SaveChangesAsync(cancellationToken);
        return setting;
    }

    public async Task<XeroLedgerSyncSetting> UpdateSettingsAsync(AppDbContext db, XeroLedgerSyncSettingsRequest request, CancellationToken cancellationToken)
    {
        var setting = await GetSettingsAsync(db, cancellationToken);
        setting.Enabled = request.Enabled;
        setting.SyncEveryMinutes = Math.Clamp(request.SyncEveryMinutes, 5, 240);
        setting.DailyTrialBalanceHourUtc = Math.Clamp(request.DailyTrialBalanceHourUtc, 0, 23);
        setting.RetentionYears = Math.Clamp(request.RetentionYears, 1, 10);
        setting.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return setting;
    }

    public async Task<XeroLedgerSyncStatus> GetSyncStatusAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var setting = await GetSettingsAsync(db, cancellationToken);
        var tenants = await db.XeroTenantConnections.AsNoTracking().OrderBy(x => x.TenantName).ToListAsync(cancellationToken);
        var cursors = await db.XeroLedgerSyncCursors.AsNoTracking().ToDictionaryAsync(x => x.TenantId, cancellationToken);
        return new XeroLedgerSyncStatus(
            setting.Enabled,
            setting.SyncEveryMinutes,
            setting.DailyTrialBalanceHourUtc,
            setting.RetentionYears,
            tenants.Select(t =>
            {
                cursors.TryGetValue(t.TenantId, out var cursor);
                return new XeroTenantLedgerStatus(
                    t.TenantId,
                    t.TenantName,
                    t.ConnectionStatus,
                    t.RequiresReconnectForLedger,
                    cursor?.LastJournalNumber,
                    cursor?.LastSuccessfulSyncAt,
                    cursor?.Status ?? "Pending",
                    cursor?.LastError ?? t.LastError);
            }).ToArray());
    }

    public async Task<XeroLedgerSyncResult> RunIncrementalLedgerSyncAsync(AppDbContext db, string? tenantId, bool force, CancellationToken cancellationToken)
    {
        var query = db.XeroTenantConnections.AsQueryable();
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            query = query.Where(x => x.TenantId == tenantId);
        }

        var tenants = await query.OrderBy(x => x.TenantName).ToListAsync(cancellationToken);
        var results = new List<XeroTenantLedgerSyncResult>();
        foreach (var tenant in tenants)
        {
            results.Add(await SyncTenantAsync(db, tenant, force, cancellationToken));
        }

        await ApplyRetentionAsync(db, cancellationToken);
        return new XeroLedgerSyncResult(DateTimeOffset.UtcNow, results.Sum(x => x.JournalsImported), results.Sum(x => x.LinesImported), results);
    }

    public async Task<XeroLedgerReconciliationRun> RunTrialBalanceReconciliationAsync(AppDbContext db, string tenantId, DateOnly snapshotDate, CancellationToken cancellationToken)
    {
        var tenant = await db.XeroTenantConnections.FirstAsync(x => x.TenantId == tenantId, cancellationToken);
        var mapping = await db.XeroTenantEntityMappings.FirstAsync(x => x.TenantId == tenantId && !x.IsIgnored, cancellationToken);
        var organization = await db.Organizations.FirstAsync(x => x.Id == mapping.OrganizationId, cancellationToken);
        var period = await db.ReportingPeriods.FirstOrDefaultAsync(x => x.PeriodStart <= snapshotDate && x.PeriodEnd >= snapshotDate, cancellationToken);

        var accessToken = await EnsureValidTokenAsync(db, tenant, cancellationToken);
        var apiBase = configuration["Xero:ApiBaseUrl"] ?? "https://api.xero.com/api.xro/2.0";
        var url = $"{apiBase}/Reports/TrialBalance?date={snapshotDate:yyyy-MM-dd}&paymentsOnly=false";
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Remove("xero-tenant-id");
        client.DefaultRequestHeaders.Add("xero-tenant-id", tenant.TenantId);
        var payload = await client.GetStringAsync(url, cancellationToken);
        var tbBalances = ParseTrialBalanceBalances(payload);

        db.XeroTrialBalanceSnapshots.Add(new XeroTrialBalanceSnapshot
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            OrganizationId = organization.Id,
            ReportingPeriodId = period?.Id,
            SnapshotDate = snapshotDate,
            Basis = "accrual",
            PayloadJson = payload,
            AccountBalancesJson = JsonSerializer.Serialize(tbBalances, JsonOptions)
        });

        var ledgerBalances = await BuildLedgerBalancesAsync(db, tenant.TenantId, snapshotDate, organization.Id, cancellationToken);
        var diffs = tbBalances.Keys.Union(ledgerBalances.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(code => new
            {
                code,
                tb = tbBalances.TryGetValue(code, out var tb) ? tb : 0m,
                ledger = ledgerBalances.TryGetValue(code, out var ledger) ? ledger : 0m
            })
            .Select(x => new { x.code, x.tb, x.ledger, diff = x.tb - x.ledger })
            .Where(x => Math.Abs(x.diff) >= 0.01m)
            .OrderByDescending(x => Math.Abs(x.diff))
            .ToArray();

        var run = new XeroLedgerReconciliationRun
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.TenantId,
            OrganizationId = organization.Id,
            ReportingPeriodId = period?.Id,
            SnapshotDate = snapshotDate,
            Status = diffs.Length == 0 ? "Passed" : "Review",
            DifferenceAmount = diffs.Sum(x => Math.Abs(x.diff)),
            MissingAccountsJson = JsonSerializer.Serialize(diffs.Select(x => x.code), JsonOptions),
            SummaryJson = JsonSerializer.Serialize(new { tenant.TenantName, snapshotDate, differences = diffs.Take(100) }, JsonOptions)
        };
        db.XeroLedgerReconciliationRuns.Add(run);

        if (period is not null && diffs.Length > 0)
        {
            var package = await db.ReportPackages.FirstOrDefaultAsync(x => x.OrganizationId == organization.Id && x.ReportingPeriodId == period.Id, cancellationToken);
            if (package is not null)
            {
                db.PackageIssues.Add(new PackageIssue
                {
                    Id = Guid.NewGuid(),
                    ReportPackageId = package.Id,
                    Severity = run.DifferenceAmount >= 1000m ? IssueSeverity.High : IssueSeverity.Medium,
                    Category = "Ledger reconciliation",
                    Title = "Trial Balance does not tie to rolling ledger",
                    Description = $"The {snapshotDate:yyyy-MM-dd} Trial Balance differs from the rolling journal ledger.",
                    EvidenceJson = run.SummaryJson,
                    RecommendedFixJson = JsonSerializer.Serialize(new { operation = "review_ledger_sync", tenantId }, JsonOptions),
                    Confidence = 0.95m
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return run;
    }

    public async Task<IReadOnlyList<XeroLedgerReconciliationRun>> GetReconciliationsAsync(AppDbContext db, string tenantId, CancellationToken cancellationToken)
        => await db.XeroLedgerReconciliationRuns
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken);

    public async Task MarkPackagesStaleForTenantActivityAsync(AppDbContext db, string tenantId, IReadOnlyCollection<DateOnly> activityDates, CancellationToken cancellationToken)
    {
        if (activityDates.Count == 0)
        {
            return;
        }

        var mapping = await db.XeroTenantEntityMappings.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && !x.IsIgnored, cancellationToken);
        if (mapping is null)
        {
            return;
        }

        var packages = await db.ReportPackages
            .Where(x => x.OrganizationId == mapping.OrganizationId && x.Status != PackageStatus.Final)
            .ToListAsync(cancellationToken);
        var periodIds = packages.Select(x => x.ReportingPeriodId).Distinct().ToArray();
        var periods = await db.ReportingPeriods
            .AsNoTracking()
            .Where(x => periodIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        foreach (var package in packages)
        {
            if (!periods.TryGetValue(package.ReportingPeriodId, out var period))
            {
                continue;
            }

            if (activityDates.Any(date => date >= period.PeriodStart && date <= period.PeriodEnd))
            {
                package.IsSourceDataStale = true;
                package.SourceDataChangedAt = DateTimeOffset.UtcNow;
                package.SourceDataStaleReason = "New Xero ledger activity was imported after this package or flux review was built.";
                package.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<XeroTenantLedgerSyncResult> SyncTenantAsync(AppDbContext db, XeroTenantConnection tenant, bool force, CancellationToken cancellationToken)
    {
        var cursor = await db.XeroLedgerSyncCursors.FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId, cancellationToken)
                     ?? new XeroLedgerSyncCursor { Id = Guid.NewGuid(), TenantId = tenant.TenantId };
        if (db.Entry(cursor).State == EntityState.Detached)
        {
            db.XeroLedgerSyncCursors.Add(cursor);
        }

        if (tenant.RequiresReconnectForLedger || !HasLedgerScope(tenant.Scopes))
        {
            cursor.Status = "NeedsReconnect";
            cursor.LastError = "Tenant does not have accounting.journals.read scope.";
            tenant.RequiresReconnectForLedger = true;
            tenant.LastError = cursor.LastError;
            await db.SaveChangesAsync(cancellationToken);
            return new XeroTenantLedgerSyncResult(tenant.TenantId, tenant.TenantName, cursor.Status, 0, 0, cursor.LastError);
        }

        if (!force && cursor.LastSyncedAt is not null && cursor.LastSyncedAt > DateTimeOffset.UtcNow.AddMinutes(-5))
        {
            return new XeroTenantLedgerSyncResult(tenant.TenantId, tenant.TenantName, "Skipped", 0, 0, null);
        }

        try
        {
            cursor.Status = "Running";
            cursor.LastSyncedAt = DateTimeOffset.UtcNow;
            cursor.LastError = null;
            await db.SaveChangesAsync(cancellationToken);

            var accessToken = await EnsureValidTokenAsync(db, tenant, cancellationToken);
            var payload = await FetchJournalsPayloadAsync(tenant, accessToken, cursor.LastJournalNumber, cancellationToken);
            var imported = await UpsertJournalsFromPayloadAsync(db, tenant.TenantId, payload, cancellationToken);
            if (imported.MaxJournalNumber > cursor.LastJournalNumber.GetValueOrDefault())
            {
                cursor.LastJournalNumber = imported.MaxJournalNumber;
            }

            cursor.Status = "Completed";
            cursor.LastSuccessfulSyncAt = DateTimeOffset.UtcNow;
            cursor.UpdatedAt = DateTimeOffset.UtcNow;
            tenant.LastError = null;
            tenant.UpdatedAt = DateTimeOffset.UtcNow;
            await MarkPackagesStaleForTenantActivityAsync(db, tenant.TenantId, imported.ActivityDates, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return new XeroTenantLedgerSyncResult(tenant.TenantId, tenant.TenantName, "Completed", imported.JournalsImported, imported.LinesImported, null);
        }
        catch (Exception ex)
        {
            cursor.Status = "Failed";
            cursor.LastError = "Xero ledger sync failed. Reconnect tenant or retry later.";
            cursor.UpdatedAt = DateTimeOffset.UtcNow;
            tenant.LastError = cursor.LastError;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning(ex, "Ledger sync failed for tenant {TenantId}.", tenant.TenantId);
            return new XeroTenantLedgerSyncResult(tenant.TenantId, tenant.TenantName, "Failed", 0, 0, cursor.LastError);
        }
    }

    public async Task<XeroJournalImportResult> UpsertJournalsFromPayloadAsync(AppDbContext db, string tenantId, string payload, CancellationToken cancellationToken)
        => await UpsertJournalsFromPayloadAsync(db, tenantId, payload, null, null, cancellationToken);

    public async Task<XeroJournalImportResult> UpsertJournalsFromPayloadAsync(AppDbContext db, string tenantId, string payload, DateOnly? fromDate, DateOnly? toDate, CancellationToken cancellationToken)
    {
        var journals = ParseJournals(payload)
            .Where(x => (fromDate is null || x.JournalDate >= fromDate.Value) && (toDate is null || x.JournalDate <= toDate.Value))
            .ToList();
        var imported = 0;
        var importedLines = 0;
        var maxJournalNumber = 0;
        var activityDates = new HashSet<DateOnly>();

        foreach (var source in journals)
        {
            var existing = await db.XeroJournals
                .Include(x => x.Lines)
                .FirstOrDefaultAsync(x => x.TenantId == tenantId && x.XeroJournalId == source.XeroJournalId, cancellationToken);
            var journal = existing ?? new XeroJournal
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                XeroJournalId = source.XeroJournalId,
                CreatedAt = DateTimeOffset.UtcNow
            };

            journal.JournalNumber = source.JournalNumber;
            journal.JournalDate = source.JournalDate;
            journal.CreatedDateUtc = source.CreatedDateUtc;
            journal.SourceType = source.SourceType;
            journal.Reference = source.Reference;
            journal.PayloadJson = source.PayloadJson;
            journal.UpdatedAt = DateTimeOffset.UtcNow;
            maxJournalNumber = Math.Max(maxJournalNumber, source.JournalNumber);
            activityDates.Add(source.JournalDate);

            if (existing is null)
            {
                db.XeroJournals.Add(journal);
            }
            else
            {
                db.XeroJournalLines.RemoveRange(existing.Lines);
            }

            foreach (var line in source.Lines)
            {
                db.XeroJournalLines.Add(new XeroJournalLine
                {
                    Id = Guid.NewGuid(),
                    XeroJournalId = journal.Id,
                    TenantId = tenantId,
                    SourceLineId = line.SourceLineId,
                    AccountCode = line.AccountCode,
                    AccountName = line.AccountName,
                    Description = line.Description,
                    NetAmount = line.NetAmount,
                    GrossAmount = line.GrossAmount,
                    TaxAmount = line.TaxAmount,
                    TrackingJson = line.TrackingJson
                });
                importedLines++;
            }

            imported++;
        }

        await EnsureReportingPeriodsForActivityDatesAsync(db, activityDates, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return new XeroJournalImportResult(imported, importedLines, maxJournalNumber, activityDates.ToArray());
    }

    public async Task<IReadOnlyList<ReportingPeriod>> EnsureReportingPeriodsForActivityDatesAsync(AppDbContext db, IReadOnlyCollection<DateOnly> activityDates, CancellationToken cancellationToken)
    {
        if (activityDates.Count == 0)
        {
            return [];
        }

        var periodKeys = activityDates
            .Select(ToPeriodKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var existingKeys = await db.ReportingPeriods
            .Where(x => periodKeys.Contains(x.Key))
            .Select(x => x.Key)
            .ToListAsync(cancellationToken);
        var existing = existingKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = new List<ReportingPeriod>();

        foreach (var date in activityDates.OrderBy(x => x).Distinct())
        {
            var key = ToPeriodKey(date);
            if (existing.Contains(key))
            {
                continue;
            }

            var period = BuildReportingPeriod(date.Year, date.Month);
            db.ReportingPeriods.Add(period);
            created.Add(period);
            existing.Add(key);
        }

        return created;
    }

    public async Task ApplyRetentionAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var settings = await GetSettingsAsync(db, cancellationToken);
        var cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-settings.RetentionYears));
        var mappings = await db.XeroTenantEntityMappings.AsNoTracking().ToDictionaryAsync(x => x.TenantId, x => x.OrganizationId, cancellationToken);
        var oldJournals = await db.XeroJournals
            .Include(x => x.Lines)
            .Where(x => x.JournalDate < cutoff)
            .ToListAsync(cancellationToken);

        var grouped = oldJournals
            .SelectMany(j => j.Lines.Select(line => new { journal = j, line }))
            .GroupBy(x => new
            {
                x.journal.TenantId,
                MonthKey = $"{x.journal.JournalDate.Year:D4}-{x.journal.JournalDate.Month:D2}",
                x.line.AccountCode,
                x.line.AccountName
            });

        foreach (var group in grouped)
        {
            if (!mappings.TryGetValue(group.Key.TenantId, out var organizationId))
            {
                organizationId = Guid.Empty;
            }

            var summary = await db.XeroLedgerMonthlySummaries.FirstOrDefaultAsync(x =>
                x.TenantId == group.Key.TenantId
                && x.MonthKey == group.Key.MonthKey
                && x.AccountCode == group.Key.AccountCode,
                cancellationToken);
            if (summary is null)
            {
                summary = new XeroLedgerMonthlySummary
                {
                    Id = Guid.NewGuid(),
                    TenantId = group.Key.TenantId,
                    MonthKey = group.Key.MonthKey,
                    AccountCode = group.Key.AccountCode
                };
                db.XeroLedgerMonthlySummaries.Add(summary);
            }

            summary.OrganizationId = organizationId;
            summary.AccountName = group.Key.AccountName;
            summary.NetAmount = group.Sum(x => x.line.NetAmount);
            summary.LastRolledUpAt = DateTimeOffset.UtcNow;
        }

        db.XeroJournalLines.RemoveRange(oldJournals.SelectMany(x => x.Lines));
        db.XeroJournals.RemoveRange(oldJournals);

        var snapshotCutoff = DateTimeOffset.UtcNow.AddYears(-settings.RetentionYears);
        var oldSnapshots = (await db.XeroRawReportSnapshots.ToListAsync(cancellationToken))
            .Where(x => x.CreatedAt < snapshotCutoff)
            .ToList();
        db.XeroRawReportSnapshots.RemoveRange(oldSnapshots);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> FetchJournalsPayloadAsync(XeroTenantConnection tenant, string accessToken, int? offset, CancellationToken cancellationToken)
    {
        var apiBase = configuration["Xero:ApiBaseUrl"] ?? "https://api.xero.com/api.xro/2.0";
        var url = $"{apiBase}/Journals?paymentsOnly=false";
        if (offset.GetValueOrDefault() > 0)
        {
            url += $"&offset={offset.GetValueOrDefault()}";
        }

        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Remove("xero-tenant-id");
        client.DefaultRequestHeaders.Add("xero-tenant-id", tenant.TenantId);
        using var response = await client.GetAsync(url, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return """{"Journals":[]}""";
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Xero journals endpoint returned {response.StatusCode}.");
        }

        return payload;
    }

    private async Task<XeroTokenResponse> RefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var clientId = configuration["Xero:ClientId"] ?? throw new InvalidOperationException("Xero:ClientId is not configured.");
        var tokenUrl = configuration["Xero:TokenUrl"] ?? "https://identity.xero.com/connect/token";
        var client = httpClientFactory.CreateClient();
        using var response = await client.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken
        }), cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Xero token refresh failed with {response.StatusCode}.");
        }

        return JsonSerializer.Deserialize<XeroTokenResponse>(content, JsonOptions)
               ?? throw new InvalidOperationException("Xero refresh response could not be parsed.");
    }

    private async Task<string> EnsureValidTokenAsync(AppDbContext db, XeroTenantConnection tenant, CancellationToken cancellationToken)
    {
        if (tenant.TokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return Unprotect(tenant.EncryptedAccessToken);
        }

        var refreshToken = Unprotect(tenant.EncryptedRefreshToken);
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            tenant.ConnectionStatus = "NeedsReconnect";
            tenant.LastError = "Refresh token is unavailable. Reconnect this Xero tenant.";
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("Refresh token is unavailable.");
        }

        var refreshed = await RefreshTokenAsync(refreshToken, cancellationToken);
        tenant.EncryptedAccessToken = Protect(refreshed.AccessToken);
        tenant.EncryptedRefreshToken = Protect(refreshed.RefreshToken);
        tenant.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn);
        tenant.ConnectionStatus = "Connected";
        tenant.LastError = null;
        tenant.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return refreshed.AccessToken;
    }

    private async Task<Dictionary<string, decimal>> BuildLedgerBalancesAsync(AppDbContext db, string tenantId, DateOnly snapshotDate, Guid organizationId, CancellationToken cancellationToken)
    {
        var liveBalanceRows = await db.XeroJournalLines
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.XeroJournal!.JournalDate <= snapshotDate)
            .GroupBy(x => x.AccountCode)
            .Select(x => new { AccountCode = x.Key, Amount = x.Sum(line => line.NetAmount) })
            .ToListAsync(cancellationToken);
        var liveBalances = liveBalanceRows.ToDictionary(x => x.AccountCode, x => x.Amount, StringComparer.OrdinalIgnoreCase);

        var monthLimit = $"{snapshotDate.Year:D4}-{snapshotDate.Month:D2}";
        var summaryBalances = await db.XeroLedgerMonthlySummaries
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OrganizationId == organizationId && string.Compare(x.MonthKey, monthLimit, StringComparison.Ordinal) <= 0)
            .GroupBy(x => x.AccountCode)
            .Select(x => new { AccountCode = x.Key, Amount = x.Sum(summary => summary.NetAmount) })
            .ToListAsync(cancellationToken);
        foreach (var summary in summaryBalances)
        {
            liveBalances[summary.AccountCode] = liveBalances.TryGetValue(summary.AccountCode, out var existing)
                ? existing + summary.Amount
                : summary.Amount;
        }

        return liveBalances;
    }

    private async Task UpsertLegacyConnectionAsync(AppDbContext db, Guid organizationId, XeroTenantConnection tenant, CancellationToken cancellationToken)
    {
        var connection = await db.XeroConnections.FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId, cancellationToken)
                         ?? new XeroConnection { Id = Guid.NewGuid(), TenantId = tenant.TenantId, CreatedAt = DateTimeOffset.UtcNow };
        connection.OrganizationId = organizationId;
        connection.TenantName = tenant.TenantName;
        connection.TenantType = tenant.TenantType;
        connection.EncryptedAccessToken = tenant.EncryptedAccessToken;
        connection.EncryptedRefreshToken = tenant.EncryptedRefreshToken;
        connection.TokenExpiresAt = tenant.TokenExpiresAt;
        connection.Scopes = tenant.Scopes;
        connection.ConnectionStatus = tenant.ConnectionStatus;
        connection.LastConnectedAt = tenant.LastConnectedAt;
        connection.LastError = tenant.LastError;
        connection.UpdatedAt = DateTimeOffset.UtcNow;
        if (db.Entry(connection).State == EntityState.Detached)
        {
            db.XeroConnections.Add(connection);
        }
    }

    private async Task UpsertTenantMappingAsync(AppDbContext db, string tenantId, Guid organizationId, string reason, CancellationToken cancellationToken)
    {
        var mapping = await db.XeroTenantEntityMappings.FirstOrDefaultAsync(x => x.TenantId == tenantId, cancellationToken)
                      ?? new XeroTenantEntityMapping { Id = Guid.NewGuid(), TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow };
        mapping.OrganizationId = organizationId;
        mapping.IsIgnored = false;
        mapping.Reason = reason;
        mapping.UpdatedAt = DateTimeOffset.UtcNow;
        if (db.Entry(mapping).State == EntityState.Detached)
        {
            db.XeroTenantEntityMappings.Add(mapping);
        }
    }

    private async Task<Organization> EnsureOrganizationAsync(AppDbContext db, string orgCode, string orgName, string tenantName, CancellationToken cancellationToken)
    {
        var key = SlugKey(string.IsNullOrWhiteSpace(orgCode) ? tenantName : orgCode);
        var organization = await db.Organizations.FirstOrDefaultAsync(x =>
            x.Key == key
            || x.Name == orgName
            || x.Name == tenantName
            || x.Abbreviation == orgCode,
            cancellationToken);
        if (organization is not null)
        {
            organization.IsConsolidated = false;
            organization.UpdatedAt = DateTimeOffset.UtcNow;
            return organization;
        }

        organization = new Organization
        {
            Id = Guid.NewGuid(),
            Key = key,
            Name = string.IsNullOrWhiteSpace(orgName) ? tenantName : orgName,
            Abbreviation = string.IsNullOrWhiteSpace(orgCode) ? BuildAbbreviation(tenantName) : orgCode,
            IsConsolidated = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Organizations.Add(organization);
        return organization;
    }

    private async Task<FinanceAppV2Rows> ReadFinanceAppV2RowsAsync(CancellationToken cancellationToken)
    {
        var source = ResolveFinanceAppV2ConnectionString();
        var rows = new List<FinanceAppV2ConnectionRow>();
        await using var connection = new SqliteConnection(source);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT x.TenantId,
                   COALESCE(x.TenantName, '') AS TenantName,
                   COALESCE(x.TenantType, 'ORGANISATION') AS TenantType,
                   x.AccessToken,
                   x.RefreshToken,
                   x.TokenExpiresAt,
                   COALESCE(x.Scopes, '') AS Scopes,
                   COALESCE(x.ConnectionStatus, '') AS ConnectionStatus,
                   x.LastConnectedAt,
                   COALESCE(o.Name, x.TenantName, '') AS OrgName,
                   COALESCE(o.Code, '') AS OrgCode
            FROM XeroConnections x
            LEFT JOIN Organizations o ON o.Id = x.OrgId
            ORDER BY o.Name, x.TenantName;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new FinanceAppV2ConnectionRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                ReadDateTimeOffset(reader.GetValue(5)),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : ReadDateTimeOffset(reader.GetValue(8)),
                reader.GetString(9),
                reader.GetString(10)));
        }

        return new FinanceAppV2Rows(source, rows);
    }

    private string ResolveFinanceAppV2ConnectionString()
    {
        var configured = configuration["Xero:FinanceAppV2ConnectionString"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var configuredPath = configuration["Xero:FinanceAppV2DbPath"];
        var path = !string.IsNullOrWhiteSpace(configuredPath)
            ? configuredPath
            : "/Users/rickygarver/Projects/Finance App V2/Garver-Finance-App/FinanceApp.Api/financeapp.db";
        return $"Data Source={path}";
    }

    private string Protect(string value) => string.IsNullOrEmpty(value) ? value : _protector.Protect(value);

    private string Unprotect(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        try
        {
            return _protector.Unprotect(value);
        }
        catch (CryptographicException)
        {
            return value.StartsWith("ey", StringComparison.Ordinal) ? value : "";
        }
    }

    private static bool HasLedgerScope(string scopes)
        => scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => string.Equals(x, LedgerScope, StringComparison.OrdinalIgnoreCase));

    private static string ToPeriodKey(DateOnly date)
        => $"{date.Year:D4}-{date.Month:D2}";

    private static ReportingPeriod BuildReportingPeriod(int year, int month)
    {
        var start = new DateOnly(year, month, 1);
        return new ReportingPeriod
        {
            Id = Guid.NewGuid(),
            Key = $"{year:D4}-{month:D2}",
            Label = new DateTime(year, month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            PeriodStart = start,
            PeriodEnd = start.AddMonths(1).AddDays(-1),
            IsClosed = false
        };
    }

    private static IReadOnlyList<ParsedJournal> ParseJournals(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("Journals", out var journalsElement) || journalsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var journals = new List<ParsedJournal>();
        foreach (var journal in journalsElement.EnumerateArray())
        {
            var id = ReadString(journal, "JournalID") ?? ReadString(journal, "JournalId") ?? ReadString(journal, "ID") ?? Guid.NewGuid().ToString("N");
            var number = ReadInt(journal, "JournalNumber");
            var date = ReadDateOnly(journal, "JournalDate") ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var lines = new List<ParsedJournalLine>();
            if (journal.TryGetProperty("JournalLines", out var lineElement) && lineElement.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var line in lineElement.EnumerateArray())
                {
                    lines.Add(new ParsedJournalLine(
                        ReadString(line, "JournalLineID") ?? $"{id}:{index++}",
                        ReadString(line, "AccountCode") ?? "",
                        ReadString(line, "AccountName") ?? "",
                        ReadString(line, "Description") ?? "",
                        ReadDecimal(line, "NetAmount"),
                        ReadDecimal(line, "GrossAmount"),
                        ReadDecimal(line, "TaxAmount"),
                        line.TryGetProperty("TrackingCategories", out var tracking) ? tracking.GetRawText() : "[]"));
                }
            }

            journals.Add(new ParsedJournal(
                id,
                number,
                date,
                ReadDateTimeOffset(journal, "CreatedDateUTC"),
                ReadString(journal, "SourceType") ?? "",
                ReadString(journal, "Reference") ?? "",
                journal.GetRawText(),
                lines));
        }

        return journals;
    }

    private static Dictionary<string, decimal> ParseTrialBalanceBalances(string payload)
    {
        var balances = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in XeroIntegrationService.ParseStatementLines("TrialBalance", payload, "tenant"))
        {
            if (!string.IsNullOrWhiteSpace(line.AccountCode))
            {
                balances[line.AccountCode] = line.CurrentAmount;
            }
        }

        return balances;
    }

    private static DateTimeOffset ReadDateTimeOffset(object value)
    {
        if (value is DateTimeOffset dto)
        {
            return dto;
        }

        if (DateTimeOffset.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.MinValue;
    }

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? XeroDateParser.ReadDateTimeOffset(value.GetString())
            : null;

    private static DateOnly? ReadDateOnly(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var raw = value.GetString();
        if (DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed;
        }

        return XeroDateParser.ReadDateOnly(raw);
    }

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static int ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(property.GetString(), out var number) => number,
            _ => 0
        };
    }

    private static decimal ReadDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return 0m;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDecimal(out var value) => value,
            JsonValueKind.String when decimal.TryParse(property.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) => value,
            _ => 0m
        };
    }

    private static string SlugKey(string value)
    {
        var slug = string.Concat(value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(slug) ? $"xero-{Guid.NewGuid():N}"[..13] : slug;
    }

    private static string BuildAbbreviation(string value)
    {
        var parts = value.Split([' ', '-', ',', '.', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var abbreviation = string.Concat(parts.Take(4).Select(x => char.ToUpperInvariant(x[0])));
        return abbreviation.Length == 0 ? "XERO" : abbreviation;
    }

    private sealed record FinanceAppV2Rows(string Source, List<FinanceAppV2ConnectionRow> Connections);
    private sealed record FinanceAppV2ConnectionRow(string TenantId, string TenantName, string TenantType, string AccessToken, string RefreshToken, DateTimeOffset TokenExpiresAt, string Scopes, string ConnectionStatus, DateTimeOffset? LastConnectedAt, string OrgName, string OrgCode);
    private sealed record ParsedJournal(string XeroJournalId, int JournalNumber, DateOnly JournalDate, DateTimeOffset? CreatedDateUtc, string SourceType, string Reference, string PayloadJson, List<ParsedJournalLine> Lines);
    private sealed record ParsedJournalLine(string SourceLineId, string AccountCode, string AccountName, string Description, decimal NetAmount, decimal GrossAmount, decimal TaxAmount, string TrackingJson);
}

public sealed class XeroLedgerSyncWorker(IServiceScopeFactory scopeFactory, ILogger<XeroLedgerSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMinutes(15);
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var service = scope.ServiceProvider.GetRequiredService<XeroTenantLedgerService>();
                var settings = await service.GetSettingsAsync(db, stoppingToken);
                delay = TimeSpan.FromMinutes(settings.SyncEveryMinutes);
                if (settings.Enabled)
                {
                    await service.RunIncrementalLedgerSyncAsync(db, null, false, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Background Xero ledger sync loop failed.");
            }

            await Task.Delay(delay, stoppingToken);
        }
    }
}

public sealed class FluxReviewService(AppDbContext db, XeroTenantLedgerService? ledgerService = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string MonthOverMonth = "MonthOverMonth";
    private const string YearOverYear = "YearOverYear";

    public async Task<FluxReviewDto> GetOrBuildAsync(Guid packageId, CancellationToken cancellationToken)
    {
        if (!await db.FluxReviewGroups.AnyAsync(x => x.ReportPackageId == packageId, cancellationToken))
        {
            await RefreshAsync(packageId, cancellationToken);
        }

        return await BuildDtoAsync(packageId, cancellationToken);
    }

    public async Task<FluxReviewDto> RefreshAsync(Guid packageId, CancellationToken cancellationToken)
        => await RefreshAsync(packageId, true, cancellationToken);

    public async Task<FluxReviewDto> RefreshAsync(Guid packageId, bool hydrateLedgerForMaterialGroups, CancellationToken cancellationToken)
    {
        var package = await db.ReportPackages.Include(x => x.ReportingPeriod).FirstAsync(x => x.Id == packageId, cancellationToken);
        var period = package.ReportingPeriod ?? throw new InvalidOperationException("Package period is required.");
        var priorMonthKey = PeriodKey(period.PeriodStart.AddMonths(-1));
        var currentLines = await db.FinancialStatementLines
            .AsNoTracking()
            .Where(x => x.ReportPackageId == packageId && (x.StatementType == "ProfitAndLoss" || x.StatementType == "BalanceSheet"))
            .ToListAsync(cancellationToken);
        var priorMonth = await db.ReportingPeriods.AsNoTracking().FirstOrDefaultAsync(x => x.Key == priorMonthKey, cancellationToken);
        var priorMonthLines = priorMonth is null
            ? []
            : await db.FinancialStatementLines
                .AsNoTracking()
                .Where(x => x.OrganizationId == package.OrganizationId &&
                            x.ReportingPeriodId == priorMonth.Id &&
                            x.ReportPackageId == null &&
                            (x.StatementType == "ProfitAndLoss" || x.StatementType == "BalanceSheet"))
                .ToListAsync(cancellationToken);
        var threeMonthPeriodIds = await db.ReportingPeriods
            .AsNoTracking()
            .Where(x => x.PeriodStart >= period.PeriodStart.AddMonths(-2) && x.PeriodStart <= period.PeriodStart)
            .OrderBy(x => x.PeriodStart)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var threeMonthLines = threeMonthPeriodIds.Count == 0
            ? []
            : await db.FinancialStatementLines
                .AsNoTracking()
                .Where(x => x.OrganizationId == package.OrganizationId &&
                            threeMonthPeriodIds.Contains(x.ReportingPeriodId) &&
                            x.ReportPackageId == null &&
                            (x.StatementType == "ProfitAndLoss" || x.StatementType == "BalanceSheet"))
                .ToListAsync(cancellationToken);

        var existing = await db.FluxReviewGroups.Where(x => x.ReportPackageId == packageId).ToListAsync(cancellationToken);
        var byKey = existing.ToDictionary(x => $"{x.FluxType}|{x.StatementType}|{x.GroupKey}", StringComparer.OrdinalIgnoreCase);
        var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var priorMonthGroups = BuildStatementGroups(priorMonthLines).ToDictionary(x => $"{x.StatementType}|{x.GroupKey}", StringComparer.OrdinalIgnoreCase);
        var threeMonthGroups = BuildStatementGroups(threeMonthLines).ToDictionary(x => $"{x.StatementType}|{x.GroupKey}", StringComparer.OrdinalIgnoreCase);

        foreach (var group in BuildStatementGroups(currentLines))
        {
            priorMonthGroups.TryGetValue($"{group.StatementType}|{group.GroupKey}", out var priorMonthGroup);
            threeMonthGroups.TryGetValue($"{group.StatementType}|{group.GroupKey}", out var threeMonthGroup);
            UpsertFluxGroup(package, period.Key, priorMonthKey, MonthOverMonth, group, priorMonthGroup?.CurrentAmount ?? 0m, threeMonthGroup?.CurrentAmount ?? group.CurrentAmount, byKey, currentKeys);
            UpsertFluxGroup(package, period.Key, period.PeriodStart.AddYears(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture), YearOverYear, group, group.PriorAmount, 0m, byKey, currentKeys);
        }

        await UpsertUngroupedAccountFluxGroupsAsync(package, period, priorMonthKey, byKey, currentKeys, cancellationToken);

        var obsoleteGroups = existing
            .Where(x => !currentKeys.Contains($"{x.FluxType}|{x.StatementType}|{x.GroupKey}"))
            .ToList();
        if (obsoleteGroups.Count > 0)
        {
            db.FluxReviewGroups.RemoveRange(obsoleteGroups);
        }

        await db.SaveChangesAsync(cancellationToken);
        await HydrateFluxReviewContextAsync(package.Id, cancellationToken);

        if (hydrateLedgerForMaterialGroups && ledgerService is not null)
        {
            var materialGroups = await db.FluxReviewGroups
                .Where(x => x.ReportPackageId == packageId && x.RequiresLedgerDetail)
                .ToListAsync(cancellationToken);
            if (materialGroups.Count > 0 && await ShouldPullLedgerDetailAsync(package, materialGroups, cancellationToken))
            {
                await PullLedgerDetailAsync(packageId, cancellationToken);
            }
        }

        package.IsSourceDataStale = false;
        package.SourceDataStaleReason = null;
        package.SourceDataChangedAt = null;
        package.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await BuildDtoAsync(packageId, cancellationToken);
    }

    public async Task<FluxReviewDto> PullLedgerDetailAsync(Guid packageId, CancellationToken cancellationToken)
    {
        if (ledgerService is null)
        {
            return await BuildDtoAsync(packageId, cancellationToken);
        }

        var package = await db.ReportPackages.Include(x => x.ReportingPeriod).FirstAsync(x => x.Id == packageId, cancellationToken);
        var tenantId = await db.XeroTenantEntityMappings
            .AsNoTracking()
            .Where(x => x.OrganizationId == package.OrganizationId && !x.IsIgnored)
            .Select(x => x.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return await BuildDtoAsync(packageId, cancellationToken);
        }

        var result = await ledgerService.RunIncrementalLedgerSyncAsync(db, tenantId, true, cancellationToken);
        var materialGroups = await db.FluxReviewGroups.Where(x => x.ReportPackageId == packageId && x.RequiresLedgerDetail).ToListAsync(cancellationToken);
        foreach (var group in materialGroups)
        {
            group.LedgerDetailPulledAt = result.RanAt;
            group.LedgerDetailStatus = "Pulled";
            group.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        await RefreshLedgerEvidenceAsync(packageId, cancellationToken);
        return await BuildDtoAsync(packageId, cancellationToken);
    }

    public async Task<FluxReviewGroup> UpdateExplanationAsync(Guid groupId, string explanation, string actor, CancellationToken cancellationToken)
    {
        var group = await db.FluxReviewGroups.FirstAsync(x => x.Id == groupId, cancellationToken);
        group.Explanation = explanation;
        group.ExplanationBy = actor;
        group.ExplainedAt = DateTimeOffset.UtcNow;
        group.Status = group.RequiresExplanation ? "Explained" : "Reviewed";
        group.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return group;
    }

    public async Task<FluxReviewGroup> ApproveAsync(Guid groupId, string actor, CancellationToken cancellationToken)
    {
        var group = await db.FluxReviewGroups.FirstAsync(x => x.Id == groupId, cancellationToken);
        group.Status = "Approved";
        group.ExplanationBy = string.IsNullOrWhiteSpace(group.ExplanationBy) ? actor : group.ExplanationBy;
        group.ExplainedAt ??= DateTimeOffset.UtcNow;
        group.ReviewedBy = actor;
        group.ReviewedAt = DateTimeOffset.UtcNow;
        group.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return group;
    }

    public async Task<FluxReviewGroup> UpdateSettingsAsync(Guid groupId, FluxReviewGroupSettingsRequest request, CancellationToken cancellationToken)
    {
        var group = await db.FluxReviewGroups.FirstAsync(x => x.Id == groupId, cancellationToken);
        var targetGroups = new List<FluxReviewGroup> { group };
        if (string.Equals(request.ApplyScope, "future", StringComparison.OrdinalIgnoreCase))
        {
            var futureGroups = await db.FluxReviewGroups
                .Where(x => x.Id != group.Id &&
                            x.OrganizationId == group.OrganizationId &&
                            x.FluxType == group.FluxType &&
                            x.StatementType == group.StatementType &&
                            x.GroupKey == group.GroupKey &&
                            string.Compare(x.CurrentPeriodKey, group.CurrentPeriodKey, StringComparison.Ordinal) >= 0)
                .ToListAsync(cancellationToken);
            targetGroups.AddRange(futureGroups);
        }

        foreach (var target in targetGroups)
        {
            ApplySettings(target, request);
        }

        await db.SaveChangesAsync(cancellationToken);
        return group;
    }

    private static void ApplySettings(FluxReviewGroup group, FluxReviewGroupSettingsRequest request)
    {
        if (request.DollarThreshold is not null)
        {
            group.DollarThreshold = Math.Max(0m, request.DollarThreshold.Value);
        }

        if (request.PercentThreshold is not null)
        {
            group.PercentThreshold = Math.Max(0m, request.PercentThreshold.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.ThresholdLogic))
        {
            group.ThresholdLogic = string.Equals(request.ThresholdLogic, "AND", StringComparison.OrdinalIgnoreCase) ? "AND" : "OR";
        }

        if (request.Assignee is not null)
        {
            group.Assignee = request.Assignee.Trim();
        }

        if (request.Reviewer is not null)
        {
            group.Reviewer = request.Reviewer.Trim();
        }

        if (request.DueDate is not null)
        {
            group.DueDate = request.DueDate;
        }

        if (request.ExplanationTemplate is not null)
        {
            group.ExplanationTemplate = request.ExplanationTemplate.Trim();
        }

        if (request.Tags is not null)
        {
            group.Tags = request.Tags.Trim();
        }

        group.RequiresExplanation = RequiresInvestigation(group, group.PriorAmount);
        group.RequiresLedgerDetail = group.RequiresExplanation;
        group.AutoSignedOff = !group.RequiresExplanation;
        group.Status = group.AutoSignedOff
            ? "Auto signed-off"
            : string.IsNullOrWhiteSpace(group.Explanation) ? "Needs explanation" : group.Status;
        group.RiskFlagsJson = JsonSerializer.Serialize(BuildRiskFlags(group), JsonOptions);
        group.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public async Task<FluxReviewGroup> SignOffAsync(Guid groupId, string action, string actor, CancellationToken cancellationToken)
    {
        var group = await db.FluxReviewGroups.FirstAsync(x => x.Id == groupId, cancellationToken);
        if (string.Equals(action, "review", StringComparison.OrdinalIgnoreCase) || string.Equals(action, "approve", StringComparison.OrdinalIgnoreCase))
        {
            group.ReviewedBy = actor;
            group.ReviewedAt = DateTimeOffset.UtcNow;
            group.Status = "Approved";
        }
        else
        {
            group.PreparedBy = actor;
            group.PreparedAt = DateTimeOffset.UtcNow;
            group.Status = string.IsNullOrWhiteSpace(group.Explanation) && group.RequiresExplanation ? "Needs explanation" : "Prepared";
        }

        group.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return group;
    }

    public async Task<FluxReviewGroup> RollForwardExplanationAsync(Guid groupId, string actor, CancellationToken cancellationToken)
    {
        var group = await db.FluxReviewGroups.FirstAsync(x => x.Id == groupId, cancellationToken);
        if (string.IsNullOrWhiteSpace(group.PriorExplanation))
        {
            return group;
        }

        group.Explanation = group.PriorExplanation;
        group.ExplanationBy = actor;
        group.ExplainedAt = DateTimeOffset.UtcNow;
        group.PreparedBy = actor;
        group.PreparedAt = DateTimeOffset.UtcNow;
        group.Status = "Prepared";
        group.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return group;
    }

    public async Task<string> ExportCsvAsync(Guid packageId, CancellationToken cancellationToken)
    {
        var review = await BuildDtoAsync(packageId, cancellationToken);
        var builder = new StringBuilder();
        builder.AppendLine("Flux Type,Statement,Group,Current Period,Prior Period,Prior,Current,Variance,Variance %,3 Month,Threshold $,Threshold %,Threshold Logic,Status,Assignee,Reviewer,Due Date,Tags,Prepared By,Prepared At,Reviewed By,Reviewed At,Explanation");
        foreach (var group in review.Groups)
        {
            builder.AppendLine(string.Join(",", new[]
            {
                Csv(group.FluxType),
                Csv(group.StatementType),
                Csv(group.GroupName),
                Csv(group.CurrentPeriodKey),
                Csv(group.PriorPeriodKey),
                group.PriorAmount.ToString(CultureInfo.InvariantCulture),
                group.CurrentAmount.ToString(CultureInfo.InvariantCulture),
                group.VarianceAmount.ToString(CultureInfo.InvariantCulture),
                group.VariancePercent.ToString(CultureInfo.InvariantCulture),
                group.RunningThreeMonthAmount.ToString(CultureInfo.InvariantCulture),
                group.DollarThreshold.ToString(CultureInfo.InvariantCulture),
                group.PercentThreshold.ToString(CultureInfo.InvariantCulture),
                Csv(group.ThresholdLogic),
                Csv(group.Status),
                Csv(group.Assignee),
                Csv(group.Reviewer),
                Csv(group.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""),
                Csv(group.Tags),
                Csv(group.PreparedBy),
                Csv(group.PreparedAt?.ToString("O", CultureInfo.InvariantCulture) ?? ""),
                Csv(group.ReviewedBy),
                Csv(group.ReviewedAt?.ToString("O", CultureInfo.InvariantCulture) ?? ""),
                Csv(group.Explanation)
            }));
        }

        return builder.ToString();
    }

    public async Task<FluxReviewDrilldownDto?> GetDrilldownAsync(Guid groupId, CancellationToken cancellationToken)
    {
        var group = await db.FluxReviewGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == groupId, cancellationToken);
        if (group is null)
        {
            return null;
        }

        var package = await db.ReportPackages.AsNoTracking().FirstAsync(x => x.Id == group.ReportPackageId, cancellationToken);
        var currentPeriod = await db.ReportingPeriods.AsNoTracking().FirstOrDefaultAsync(x => x.Key == group.CurrentPeriodKey, cancellationToken);
        var priorPeriod = await db.ReportingPeriods.AsNoTracking().FirstOrDefaultAsync(x => x.Key == group.PriorPeriodKey, cancellationToken);
        var tenantId = await db.XeroTenantEntityMappings
            .AsNoTracking()
            .Where(x => x.OrganizationId == package.OrganizationId && !x.IsIgnored)
            .Select(x => x.TenantId)
            .FirstOrDefaultAsync(cancellationToken);

        var currentLines = await LoadStatementLinesForGroupAsync(group, package.OrganizationId, currentPeriod?.Id, group.ReportPackageId, cancellationToken);
        var priorLines = priorPeriod is null
            ? []
            : await LoadStatementLinesForGroupAsync(group, package.OrganizationId, priorPeriod.Id, null, cancellationToken);

        var accountCodes = ExtractAccountCodes(group.EvidenceJson);
        foreach (var line in currentLines.Concat(priorLines))
        {
            if (!string.IsNullOrWhiteSpace(line.AccountCode))
            {
                accountCodes.Add(line.AccountCode);
            }
        }

        var currentAccounts = currentPeriod is null || accountCodes.Count == 0
            ? []
            : await db.GlAccounts
                .AsNoTracking()
                .Include(x => x.Transactions)
                .Where(x => x.OrganizationId == package.OrganizationId &&
                            x.ReportingPeriodId == currentPeriod.Id &&
                            accountCodes.Contains(x.Code))
                .ToListAsync(cancellationToken);
        foreach (var account in currentAccounts.Where(x => string.Equals(x.FsLine, group.GroupName, StringComparison.OrdinalIgnoreCase) ||
                                                           string.Equals(x.AiSuggestedFsLine, group.GroupName, StringComparison.OrdinalIgnoreCase)))
        {
            accountCodes.Add(account.Code);
        }

        var accountNames = currentLines.Concat(priorLines)
            .Where(x => !string.IsNullOrWhiteSpace(x.AccountCode))
            .GroupBy(x => x.AccountCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(line => line.LineName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? x.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var account in currentAccounts)
        {
            accountNames[account.Code] = account.Name;
        }

        var accounts = new List<FluxReviewAccountDto>();
        foreach (var accountCode in accountCodes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var account = currentAccounts.FirstOrDefault(x => string.Equals(x.Code, accountCode, StringComparison.OrdinalIgnoreCase));
            var currentAmount = currentLines.Where(x => string.Equals(x.AccountCode, accountCode, StringComparison.OrdinalIgnoreCase)).Sum(x => x.CurrentAmount);
            if (currentAmount == 0m && account is not null)
            {
                currentAmount = FinancialEngine.AccountSignedBalance(account);
            }

            var priorAmount = group.FluxType == YearOverYear
                ? currentLines.Where(x => string.Equals(x.AccountCode, accountCode, StringComparison.OrdinalIgnoreCase)).Sum(x => x.PriorAmount)
                : priorLines.Where(x => string.Equals(x.AccountCode, accountCode, StringComparison.OrdinalIgnoreCase)).Sum(x => x.CurrentAmount);

            if (priorAmount == 0m && priorPeriod is not null)
            {
                var priorAccount = await db.GlAccounts
                    .AsNoTracking()
                    .Include(x => x.Transactions)
                    .FirstOrDefaultAsync(x => x.OrganizationId == package.OrganizationId &&
                                              x.ReportingPeriodId == priorPeriod.Id &&
                                              x.Code == accountCode, cancellationToken);
                if (priorAccount is not null)
                {
                    priorAmount = FinancialEngine.AccountSignedBalance(priorAccount);
                }
            }

            var variance = FinancialMath.Variance(currentAmount, priorAmount);
            var currentTransactions = string.IsNullOrWhiteSpace(tenantId) || currentPeriod is null
                ? []
                : await LoadLedgerTransactionsAsync(tenantId, accountCode, currentPeriod, cancellationToken);
            var priorTransactions = string.IsNullOrWhiteSpace(tenantId) || priorPeriod is null
                ? []
                : await LoadLedgerTransactionsAsync(tenantId, accountCode, priorPeriod, cancellationToken);

            accounts.Add(new FluxReviewAccountDto(
                accountCode,
                accountNames.GetValueOrDefault(accountCode, account?.Name ?? accountCode),
                account?.Type ?? "",
                account?.FsLine ?? "",
                currentAmount,
                priorAmount,
                variance.Amount,
                variance.Percent,
                currentTransactions,
                priorTransactions));
        }

        return new FluxReviewDrilldownDto(
            group.Id,
            group.FluxType,
            group.StatementType,
            group.GroupName,
            group.CurrentPeriodKey,
            group.PriorPeriodKey,
            group.CurrentAmount,
            group.PriorAmount,
            group.RunningThreeMonthAmount,
            group.VarianceAmount,
            group.VariancePercent,
            accounts);
    }

    public async Task<string> BuildAiExplanationSnapshotAsync(Guid groupId, CancellationToken cancellationToken)
    {
        var group = await db.FluxReviewGroups.AsNoTracking().FirstAsync(x => x.Id == groupId, cancellationToken);
        var drilldown = await GetDrilldownAsync(groupId, cancellationToken);
        return JsonSerializer.Serialize(new
        {
            group = FluxReviewGroupDto.From(group),
            drilldown,
            instructions = new[]
            {
                "Explain the variance in plain financial-review language.",
                "Use the current period, prior period, account-level changes, and transaction evidence supplied here.",
                "Do not mention missing credentials, tokens, or any data outside this JSON snapshot.",
                "Return a concise explanation that a finance reviewer can approve or edit."
            },
            allowedOperations = new[] { "set_flux_explanation" }
        }, JsonOptions);
    }

    private async Task<FluxReviewDto> BuildDtoAsync(Guid packageId, CancellationToken cancellationToken)
    {
        var package = await db.ReportPackages.AsNoTracking().FirstAsync(x => x.Id == packageId, cancellationToken);
        var groups = (await db.FluxReviewGroups
            .AsNoTracking()
            .Where(x => x.ReportPackageId == packageId)
            .OrderBy(x => x.FluxType)
            .ThenBy(x => x.StatementType)
            .ThenByDescending(x => x.RequiresExplanation)
            .ToListAsync(cancellationToken))
            .OrderByDescending(x => Math.Abs(x.VarianceAmount))
            .ToList();
        var progress = new FluxReviewProgressDto(
            groups.Count,
            groups.Count(x => x.RequiresExplanation),
            groups.Count(x => x.RequiresExplanation && string.IsNullOrWhiteSpace(x.Explanation) && x.Status != "Approved"),
            groups.Count(x => !x.RequiresExplanation || x.AutoSignedOff),
            groups.Count(x => !string.IsNullOrWhiteSpace(x.PreparedBy) || x.Status == "Prepared" || x.Status == "Approved"),
            groups.Count(x => !string.IsNullOrWhiteSpace(x.ReviewedBy) || x.Status == "Approved"));
        return new FluxReviewDto(packageId, package.IsSourceDataStale, package.SourceDataStaleReason, progress, groups.Select(FluxReviewGroupDto.From).ToArray());
    }

    private void UpsertFluxGroup(
        ReportPackage package,
        string currentPeriodKey,
        string priorPeriodKey,
        string fluxType,
        StatementGroup group,
        decimal prior,
        decimal runningThreeMonthAmount,
        Dictionary<string, FluxReviewGroup> byKey,
        HashSet<string> currentKeys)
    {
        var variance = FinancialMath.Variance(group.CurrentAmount, prior);
        var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{fluxType}|{group.CurrentAmount}|{prior}|{runningThreeMonthAmount}|{group.Lines.Count}")));
        var lookupKey = $"{fluxType}|{group.StatementType}|{group.GroupKey}";
        currentKeys.Add(lookupKey);
        if (!byKey.TryGetValue(lookupKey, out var reviewGroup))
        {
            reviewGroup = new FluxReviewGroup
            {
                Id = Guid.NewGuid(),
                ReportPackageId = package.Id,
                OrganizationId = package.OrganizationId,
                ReportingPeriodId = package.ReportingPeriodId,
                FluxType = fluxType,
                StatementType = group.StatementType,
                GroupKey = group.GroupKey,
                GroupName = group.GroupName,
                DollarThreshold = 0m,
                PercentThreshold = 10m,
                ThresholdLogic = "OR",
                CreatedAt = DateTimeOffset.UtcNow
            };
            byKey[lookupKey] = reviewGroup;
            db.FluxReviewGroups.Add(reviewGroup);
        }

        var changed = reviewGroup.SourceDataHash != "" && reviewGroup.SourceDataHash != sourceHash;
        reviewGroup.DollarThreshold = reviewGroup.DollarThreshold == 10_000m ? 0m : reviewGroup.DollarThreshold;
        reviewGroup.PercentThreshold = reviewGroup.PercentThreshold == 0m ? 10m : reviewGroup.PercentThreshold;
        reviewGroup.ThresholdLogic = string.Equals(reviewGroup.ThresholdLogic, "AND", StringComparison.OrdinalIgnoreCase) ? "AND" : "OR";
        reviewGroup.CurrentPeriodKey = currentPeriodKey;
        reviewGroup.PriorPeriodKey = priorPeriodKey;
        reviewGroup.CurrentAmount = group.CurrentAmount;
        reviewGroup.PriorAmount = prior;
        reviewGroup.RunningThreeMonthAmount = fluxType == MonthOverMonth ? runningThreeMonthAmount : 0m;
        reviewGroup.VarianceAmount = variance.Amount;
        reviewGroup.VariancePercent = variance.Percent;
        reviewGroup.RequiresExplanation = RequiresInvestigation(reviewGroup, prior);
        reviewGroup.RequiresLedgerDetail = reviewGroup.RequiresExplanation;
        reviewGroup.AutoSignedOff = !reviewGroup.RequiresExplanation;
        reviewGroup.LedgerDetailStatus = reviewGroup.RequiresLedgerDetail
            ? reviewGroup.LedgerDetailStatus is "Pulled" or "Available" ? reviewGroup.LedgerDetailStatus : "Needed"
            : "Not required";
        reviewGroup.Status = changed
            ? "Source data changed"
            : reviewGroup.Status == "Approved"
                ? "Approved"
                : reviewGroup.AutoSignedOff
                    ? "Auto signed-off"
                    : reviewGroup.RequiresExplanation && string.IsNullOrWhiteSpace(reviewGroup.Explanation)
                        ? "Needs explanation"
                        : string.IsNullOrWhiteSpace(reviewGroup.PreparedBy) ? "Open" : "Prepared";
        reviewGroup.ExplanationTemplate = string.IsNullOrWhiteSpace(reviewGroup.ExplanationTemplate)
            ? "Explain the business driver, quantify the largest account or vendor changes, and identify whether the movement is recurring or timing-related."
            : reviewGroup.ExplanationTemplate;
        reviewGroup.RiskFlagsJson = JsonSerializer.Serialize(BuildRiskFlags(reviewGroup), JsonOptions);
        reviewGroup.EvidenceJson = JsonSerializer.Serialize(new
        {
            package = package.Id,
            fluxType,
            currentPeriodKey,
            priorPeriodKey,
            runningThreeMonthAmount = reviewGroup.RunningThreeMonthAmount,
            comparison = new { currentAmount = reviewGroup.CurrentAmount, priorAmount = reviewGroup.PriorAmount, varianceAmount = reviewGroup.VarianceAmount, variancePercent = reviewGroup.VariancePercent },
            threshold = new { percent = reviewGroup.PercentThreshold, dollar = reviewGroup.DollarThreshold, logic = reviewGroup.ThresholdLogic },
            ledgerDetail = new { required = reviewGroup.RequiresLedgerDetail, status = reviewGroup.LedgerDetailStatus, pulledAt = reviewGroup.LedgerDetailPulledAt },
            explanationTemplate = reviewGroup.ExplanationTemplate,
            priorExplanation = reviewGroup.PriorExplanation,
            tags = reviewGroup.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            trend = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(reviewGroup.TrendJson) ? "[]" : reviewGroup.TrendJson),
            riskFlags = JsonSerializer.Deserialize<JsonElement>(reviewGroup.RiskFlagsJson),
            lines = group.Lines.Select(x => new { x.LineName, x.AccountCode, x.CurrentAmount, x.PriorAmount }).ToArray()
        }, JsonOptions);
        reviewGroup.SourceDataHash = sourceHash;
        reviewGroup.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task HydrateFluxReviewContextAsync(Guid packageId, CancellationToken cancellationToken)
    {
        var package = await db.ReportPackages.Include(x => x.ReportingPeriod).FirstAsync(x => x.Id == packageId, cancellationToken);
        if (package.ReportingPeriod is null)
        {
            return;
        }

        var groups = await db.FluxReviewGroups.Where(x => x.ReportPackageId == packageId).ToListAsync(cancellationToken);
        foreach (var group in groups)
        {
            group.PriorExplanation = await FindPriorExplanationAsync(package, group, cancellationToken);
            group.TrendJson = await BuildTrendJsonAsync(package, group, cancellationToken);
            group.RiskFlagsJson = JsonSerializer.Serialize(BuildRiskFlags(group), JsonOptions);
            group.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> FindPriorExplanationAsync(ReportPackage package, FluxReviewGroup group, CancellationToken cancellationToken)
    {
        if (package.ReportingPeriod is null)
        {
            return "";
        }

        var priorPeriodKey = PeriodKey(package.ReportingPeriod.PeriodStart.AddMonths(-1));
        var priorPackage = await db.ReportPackages
            .AsNoTracking()
            .Include(x => x.ReportingPeriod)
            .Where(x => x.OrganizationId == package.OrganizationId && x.ReportingPeriod != null && x.ReportingPeriod.Key == priorPeriodKey)
            .Select(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (priorPackage == Guid.Empty)
        {
            return "";
        }

        return await db.FluxReviewGroups
            .AsNoTracking()
            .Where(x => x.ReportPackageId == priorPackage &&
                        x.FluxType == group.FluxType &&
                        x.StatementType == group.StatementType &&
                        x.GroupKey == group.GroupKey &&
                        x.Explanation != "")
            .OrderByDescending(x => x.UpdatedAt)
            .Select(x => x.Explanation)
            .FirstOrDefaultAsync(cancellationToken) ?? "";
    }

    private async Task<string> BuildTrendJsonAsync(ReportPackage package, FluxReviewGroup group, CancellationToken cancellationToken)
    {
        if (package.ReportingPeriod is null)
        {
            return "[]";
        }

        var periods = await db.ReportingPeriods
            .AsNoTracking()
            .Where(x => x.PeriodStart <= package.ReportingPeriod.PeriodStart && x.PeriodStart >= package.ReportingPeriod.PeriodStart.AddMonths(-5))
            .OrderBy(x => x.PeriodStart)
            .ToListAsync(cancellationToken);
        var trend = new List<object>();
        foreach (var period in periods)
        {
            decimal amount;
            if (group.GroupKey == "Ungrouped")
            {
                var ungrouped = await LoadUngroupedAccountsAsync(package.OrganizationId, period.Id, cancellationToken);
                amount = ungrouped.Where(x => InferStatementType(x) == group.StatementType).Sum(FinancialEngine.AccountSignedBalance);
            }
            else
            {
                var lines = await db.FinancialStatementLines
                    .AsNoTracking()
                    .Where(x => x.OrganizationId == package.OrganizationId &&
                                x.ReportingPeriodId == period.Id &&
                                x.StatementType == group.StatementType &&
                                x.ReportPackageId == null)
                    .ToListAsync(cancellationToken);
                amount = BuildStatementGroups(lines).FirstOrDefault(x => x.GroupKey == group.GroupKey)?.CurrentAmount ?? 0m;
                if (period.Id == package.ReportingPeriodId && amount == 0m)
                {
                    amount = group.CurrentAmount;
                }
            }

            trend.Add(new { periodKey = period.Key, amount });
        }

        return JsonSerializer.Serialize(trend, JsonOptions);
    }

    private async Task UpsertUngroupedAccountFluxGroupsAsync(
        ReportPackage package,
        ReportingPeriod period,
        string priorMonthKey,
        Dictionary<string, FluxReviewGroup> byKey,
        HashSet<string> currentKeys,
        CancellationToken cancellationToken)
    {
        var currentAccounts = await LoadUngroupedAccountsAsync(package.OrganizationId, period.Id, cancellationToken);
        if (currentAccounts.Count == 0)
        {
            return;
        }

        var priorMonth = await db.ReportingPeriods.AsNoTracking().FirstOrDefaultAsync(x => x.Key == priorMonthKey, cancellationToken);
        var priorYearKey = PeriodKey(period.PeriodStart.AddYears(-1));
        var priorYear = await db.ReportingPeriods.AsNoTracking().FirstOrDefaultAsync(x => x.Key == priorYearKey, cancellationToken);
        var threeMonthPeriodIds = await db.ReportingPeriods
            .AsNoTracking()
            .Where(x => x.PeriodStart >= period.PeriodStart.AddMonths(-2) && x.PeriodStart <= period.PeriodStart)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var statementGroup in currentAccounts.GroupBy(InferStatementType))
        {
            var accounts = statementGroup.OrderBy(x => x.Code, StringComparer.OrdinalIgnoreCase).ToList();
            var currentAmount = accounts.Sum(FinancialEngine.AccountSignedBalance);
            var group = UngroupedStatementGroup(statementGroup.Key, accounts, currentAmount, 0m);
            var priorMonthAmount = priorMonth is null
                ? 0m
                : await LoadAccountBalancesAsync(package.OrganizationId, priorMonth.Id, accounts.Select(x => x.Code).ToArray(), cancellationToken);
            var priorYearAmount = priorYear is null
                ? 0m
                : await LoadAccountBalancesAsync(package.OrganizationId, priorYear.Id, accounts.Select(x => x.Code).ToArray(), cancellationToken);
            var runningThreeMonth = 0m;
            foreach (var periodId in threeMonthPeriodIds)
            {
                runningThreeMonth += periodId == period.Id
                    ? currentAmount
                    : await LoadAccountBalancesAsync(package.OrganizationId, periodId, accounts.Select(x => x.Code).ToArray(), cancellationToken);
            }

            UpsertFluxGroup(package, period.Key, priorMonthKey, MonthOverMonth, group, priorMonthAmount, runningThreeMonth, byKey, currentKeys);
            UpsertFluxGroup(package, period.Key, priorYearKey, YearOverYear, group, priorYearAmount, 0m, byKey, currentKeys);
        }
    }

    private async Task<bool> ShouldPullLedgerDetailAsync(ReportPackage package, IReadOnlyCollection<FluxReviewGroup> materialGroups, CancellationToken cancellationToken)
    {
        if (package.ReportingPeriod is null)
        {
            return false;
        }

        var tenantId = await db.XeroTenantEntityMappings
            .AsNoTracking()
            .Where(x => x.OrganizationId == package.OrganizationId && !x.IsIgnored)
            .Select(x => x.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return false;
        }

        var periodStart = package.ReportingPeriod.PeriodStart;
        var periodEnd = package.ReportingPeriod.PeriodEnd;
        var hasPeriodLedger = await db.XeroJournals
            .AsNoTracking()
            .AnyAsync(x => x.TenantId == tenantId && x.JournalDate >= periodStart && x.JournalDate <= periodEnd, cancellationToken);
        if (!hasPeriodLedger)
        {
            return true;
        }

        var tbSnapshotDates = await db.XeroTrialBalanceSnapshots
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ReportingPeriodId == package.ReportingPeriodId)
            .Select(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        var latestTbSnapshot = tbSnapshotDates.Count == 0
            ? (DateTimeOffset?)null
            : tbSnapshotDates.Max();
        var latestPull = materialGroups
            .Where(x => x.LedgerDetailPulledAt is not null)
            .Select(x => x.LedgerDetailPulledAt!.Value)
            .DefaultIfEmpty(DateTimeOffset.MinValue)
            .Max();

        return latestTbSnapshot is not null && latestTbSnapshot > latestPull;
    }

    private async Task RefreshLedgerEvidenceAsync(Guid packageId, CancellationToken cancellationToken)
    {
        var package = await db.ReportPackages.Include(x => x.ReportingPeriod).FirstAsync(x => x.Id == packageId, cancellationToken);
        if (package.ReportingPeriod is null)
        {
            return;
        }

        var tenantId = await db.XeroTenantEntityMappings
            .AsNoTracking()
            .Where(x => x.OrganizationId == package.OrganizationId && !x.IsIgnored)
            .Select(x => x.TenantId)
            .FirstOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return;
        }

        var periodStart = package.ReportingPeriod.PeriodStart;
        var periodEnd = package.ReportingPeriod.PeriodEnd;
        var groups = await db.FluxReviewGroups.Where(x => x.ReportPackageId == packageId && x.RequiresLedgerDetail).ToListAsync(cancellationToken);
        foreach (var group in groups)
        {
            var accountCodes = ExtractAccountCodes(group.EvidenceJson);
            var transactionCount = accountCodes.Count == 0
                ? await db.XeroJournals.CountAsync(x => x.TenantId == tenantId && x.JournalDate >= periodStart && x.JournalDate <= periodEnd, cancellationToken)
                : await db.XeroJournalLines
                    .Join(db.XeroJournals,
                        line => line.XeroJournalId,
                        journal => journal.Id,
                        (line, journal) => new { line, journal })
                    .CountAsync(x => x.journal.TenantId == tenantId &&
                                     x.journal.JournalDate >= periodStart &&
                                     x.journal.JournalDate <= periodEnd &&
                                     accountCodes.Contains(x.line.AccountCode), cancellationToken);

            group.LedgerDetailStatus = transactionCount > 0 ? "Available" : group.LedgerDetailStatus;
            group.EvidenceJson = MergeLedgerEvidence(group.EvidenceJson, group.RequiresLedgerDetail, group.LedgerDetailStatus, group.LedgerDetailPulledAt, transactionCount);
            group.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<List<FinancialStatementLine>> LoadStatementLinesForGroupAsync(
        FluxReviewGroup group,
        Guid organizationId,
        Guid? reportingPeriodId,
        Guid? reportPackageId,
        CancellationToken cancellationToken)
    {
        if (reportingPeriodId is null)
        {
            return [];
        }

        var accountCodes = ExtractAccountCodes(group.EvidenceJson);
        var lines = await db.FinancialStatementLines
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                        x.ReportingPeriodId == reportingPeriodId &&
                        x.StatementType == group.StatementType &&
                        x.ReportPackageId == reportPackageId)
            .ToListAsync(cancellationToken);

        return lines
            .Where(x => string.Equals($"{x.Section}/{x.LineName}".Trim('/'), group.GroupKey, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(x.LineName, group.GroupName, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrWhiteSpace(x.AccountCode) && accountCodes.Contains(x.AccountCode)))
            .ToList();
    }

    private async Task<List<GlAccount>> LoadUngroupedAccountsAsync(Guid organizationId, Guid reportingPeriodId, CancellationToken cancellationToken)
    {
        var fsLineNames = await db.FsLineDefinitions
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsActive)
            .Select(x => x.Name)
            .ToListAsync(cancellationToken);
        var knownLines = fsLineNames.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return await db.GlAccounts
            .AsNoTracking()
            .Include(x => x.Transactions)
            .Where(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId)
            .ToListAsync(cancellationToken)
            .ContinueWith(task => task.Result
                .Where(x => string.IsNullOrWhiteSpace(x.FsLine)
                            || x.FsLine.Equals("Unmapped", StringComparison.OrdinalIgnoreCase)
                            || !knownLines.Contains(x.FsLine))
                .ToList(), cancellationToken);
    }

    private async Task<decimal> LoadAccountBalanceAsync(Guid organizationId, Guid reportingPeriodId, string accountCode, CancellationToken cancellationToken)
    {
        var account = await db.GlAccounts
            .AsNoTracking()
            .Include(x => x.Transactions)
            .FirstOrDefaultAsync(x => x.OrganizationId == organizationId &&
                                      x.ReportingPeriodId == reportingPeriodId &&
                                      x.Code == accountCode, cancellationToken);
        return account is null ? 0m : FinancialEngine.AccountSignedBalance(account);
    }

    private async Task<decimal> LoadAccountBalancesAsync(Guid organizationId, Guid reportingPeriodId, IReadOnlyCollection<string> accountCodes, CancellationToken cancellationToken)
    {
        if (accountCodes.Count == 0)
        {
            return 0m;
        }

        var accounts = await db.GlAccounts
            .AsNoTracking()
            .Include(x => x.Transactions)
            .Where(x => x.OrganizationId == organizationId &&
                        x.ReportingPeriodId == reportingPeriodId &&
                        accountCodes.Contains(x.Code))
            .ToListAsync(cancellationToken);
        return accounts.Sum(FinancialEngine.AccountSignedBalance);
    }

    private async Task<IReadOnlyList<FluxLedgerTransactionDto>> LoadLedgerTransactionsAsync(string tenantId, string accountCode, ReportingPeriod period, CancellationToken cancellationToken)
        => await db.XeroJournalLines
            .AsNoTracking()
            .Join(db.XeroJournals.AsNoTracking(),
                line => line.XeroJournalId,
                journal => journal.Id,
                (line, journal) => new { line, journal })
            .Where(x => x.journal.TenantId == tenantId &&
                        x.journal.JournalDate >= period.PeriodStart &&
                        x.journal.JournalDate <= period.PeriodEnd &&
                        x.line.AccountCode == accountCode)
            .OrderByDescending(x => x.journal.JournalDate)
            .ThenByDescending(x => x.journal.JournalNumber)
            .Take(150)
            .Select(x => new FluxLedgerTransactionDto(
                x.journal.JournalDate,
                x.journal.JournalNumber,
                x.journal.SourceType,
                x.journal.Reference,
                string.IsNullOrWhiteSpace(x.line.Description) ? x.journal.Reference : x.line.Description,
                x.line.NetAmount,
                x.line.GrossAmount,
                x.line.TaxAmount))
            .ToListAsync(cancellationToken);

    private static IReadOnlyList<StatementGroup> BuildStatementGroups(IEnumerable<FinancialStatementLine> lines)
        => lines
            .GroupBy(x => new { x.StatementType, x.Section, x.LineName })
            .OrderBy(x => x.Key.StatementType)
            .ThenBy(x => x.Key.Section)
            .ThenBy(x => x.Key.LineName)
            .Select(group =>
            {
                var groupKey = $"{group.Key.Section}/{group.Key.LineName}".Trim('/');
                return new StatementGroup(
                    group.Key.StatementType,
                    groupKey,
                    string.IsNullOrWhiteSpace(group.Key.LineName) ? group.Key.Section : group.Key.LineName,
                    group.Sum(x => x.CurrentAmount),
                    group.Sum(x => x.PriorAmount),
                    group.ToList());
            })
            .ToList();

    private static StatementGroup UngroupedStatementGroup(string statementType, IReadOnlyList<GlAccount> accounts, decimal currentAmount, decimal priorAmount)
    {
        var lines = accounts.Select(account => new FinancialStatementLine
        {
            StatementType = statementType,
            Section = "Ungrouped",
            LineName = account.Name,
            AccountCode = account.Code,
            CurrentAmount = FinancialEngine.AccountSignedBalance(account),
            PriorAmount = 0m
        }).ToList();
        return new StatementGroup(statementType, "Ungrouped", "Ungrouped accounts", currentAmount, priorAmount, lines);
    }

    private static string InferStatementType(GlAccount account)
    {
        var text = $"{account.Type} {account.Class} {account.FsLine} {account.Name}";
        return text.Contains("asset", StringComparison.OrdinalIgnoreCase)
               || text.Contains("liabil", StringComparison.OrdinalIgnoreCase)
               || text.Contains("equity", StringComparison.OrdinalIgnoreCase)
               || text.Contains("cash", StringComparison.OrdinalIgnoreCase)
               || text.Contains("receivable", StringComparison.OrdinalIgnoreCase)
               || text.Contains("payable", StringComparison.OrdinalIgnoreCase)
               || text.Contains("bank", StringComparison.OrdinalIgnoreCase)
            ? "BalanceSheet"
            : "ProfitAndLoss";
    }

    private static bool RequiresInvestigation(FluxReviewGroup group, decimal prior)
    {
        if (prior == 0m && group.CurrentAmount != 0m)
        {
            return true;
        }

        var percentHit = Math.Abs(group.VariancePercent) >= Math.Abs(group.PercentThreshold);
        var dollarHit = group.DollarThreshold > 0m && Math.Abs(group.VarianceAmount) >= Math.Abs(group.DollarThreshold);
        return string.Equals(group.ThresholdLogic, "AND", StringComparison.OrdinalIgnoreCase)
            ? percentHit && (group.DollarThreshold <= 0m || dollarHit)
            : percentHit || dollarHit;
    }

    private static string PeriodKey(DateOnly date) => date.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static IReadOnlyList<string> BuildRiskFlags(FluxReviewGroup group)
    {
        var flags = new List<string>();
        if (group.GroupKey == "Ungrouped")
        {
            flags.Add("Ungrouped accounts need mapping review");
        }

        if (group.PriorAmount == 0m && group.CurrentAmount != 0m)
        {
            flags.Add("No prior-period balance");
        }

        if (Math.Abs(group.VariancePercent) >= 50m)
        {
            flags.Add("High percentage swing");
        }

        if (group.RequiresLedgerDetail && group.LedgerDetailStatus is not ("Pulled" or "Available"))
        {
            flags.Add("Ledger detail needed");
        }

        if (group.DueDate is not null && group.DueDate < DateOnly.FromDateTime(DateTime.UtcNow.Date) && group.Status != "Approved")
        {
            flags.Add("Past due");
        }

        return flags;
    }

    private static string Csv(string value)
    {
        var safe = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return safe.Contains(',', StringComparison.Ordinal) || safe.Contains('"', StringComparison.Ordinal) || safe.Contains('\n', StringComparison.Ordinal)
            ? $"\"{safe}\""
            : safe;
    }

    private static HashSet<string> ExtractAccountCodes(string evidenceJson)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var lines = JsonNode.Parse(evidenceJson)?["lines"]?.AsArray();
            if (lines is null)
            {
                return codes;
            }

            foreach (var line in lines)
            {
                var code = line?["accountCode"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(code))
                {
                    codes.Add(code);
                }
            }
        }
        catch
        {
            return codes;
        }

        return codes;
    }

    private static string MergeLedgerEvidence(string evidenceJson, bool required, string status, DateTimeOffset? pulledAt, int transactionCount)
    {
        try
        {
            var root = JsonNode.Parse(evidenceJson)?.AsObject() ?? [];
            root["ledgerDetail"] = JsonSerializer.SerializeToNode(new { required, status, pulledAt, transactionCount }, JsonOptions);
            return root.ToJsonString(JsonOptions);
        }
        catch
        {
            return JsonSerializer.Serialize(new { ledgerDetail = new { required, status, pulledAt, transactionCount } }, JsonOptions);
        }
    }

    private sealed record StatementGroup(string StatementType, string GroupKey, string GroupName, decimal CurrentAmount, decimal PriorAmount, List<FinancialStatementLine> Lines);
}

public sealed class AiPackageDraftService(AppDbContext db)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AiPackageDraftSuggestion>> CreateDraftsAsync(Guid packageId, CancellationToken cancellationToken)
    {
        var package = await db.ReportPackages.Include(x => x.Slides).FirstAsync(x => x.Id == packageId, cancellationToken);
        var slideId = package.Slides.OrderBy(x => x.SortOrder).FirstOrDefault()?.Id;
        var fluxGroups = (await db.FluxReviewGroups
            .AsNoTracking()
            .Where(x => x.ReportPackageId == packageId && !string.IsNullOrWhiteSpace(x.Explanation))
            .ToListAsync(cancellationToken))
            .OrderByDescending(x => Math.Abs(x.VarianceAmount))
            .Take(8)
            .ToList();

        var created = new List<AiPackageDraftSuggestion>();
        foreach (var group in fluxGroups)
        {
            var suggestion = new AiPackageDraftSuggestion
            {
                Id = Guid.NewGuid(),
                ReportPackageId = packageId,
                Kind = "FluxContext",
                Title = $"Add context for {group.GroupName}",
                Description = group.Explanation,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    operation = "add_context_block",
                    slideId,
                    kind = "callout",
                    content = new
                    {
                        title = group.GroupName,
                        body = group.Explanation,
                        variance = group.VarianceAmount,
                        variancePercent = group.VariancePercent
                    }
                }, JsonOptions)
            };
            db.AiPackageDraftSuggestions.Add(suggestion);
            created.Add(suggestion);
        }

        if (created.Count == 0)
        {
            var suggestion = new AiPackageDraftSuggestion
            {
                Id = Guid.NewGuid(),
                ReportPackageId = packageId,
                Kind = "Readiness",
                Title = "Complete flux review before AI package drafting",
                Description = "No approved flux explanations are available yet. Complete flux review to provide package context.",
                PayloadJson = JsonSerializer.Serialize(new { operation = "context_only" }, JsonOptions)
            };
            db.AiPackageDraftSuggestions.Add(suggestion);
            created.Add(suggestion);
        }

        await db.SaveChangesAsync(cancellationToken);
        return created;
    }

    public async Task<IReadOnlyList<AiPackageDraftSuggestion>> GetDraftsAsync(Guid packageId, CancellationToken cancellationToken)
    {
        var drafts = await db.AiPackageDraftSuggestions.AsNoTracking()
            .Where(x => x.ReportPackageId == packageId)
            .ToListAsync(cancellationToken);

        return drafts.OrderByDescending(x => x.CreatedAt).ToList();
    }

    public async Task<AiPackageDraftSuggestion> AcceptAsync(Guid draftId, CancellationToken cancellationToken)
    {
        var draft = await db.AiPackageDraftSuggestions.FirstAsync(x => x.Id == draftId, cancellationToken);
        if (draft.Status != "Staged")
        {
            return draft;
        }

        var root = JsonNode.Parse(draft.PayloadJson);
        var operation = root?["operation"]?.GetValue<string>();
        if (operation == "add_context_block" && Guid.TryParse(root?["slideId"]?.GetValue<string>(), out var slideId))
        {
            var maxSort = await db.SlideBlocks.Where(x => x.PackageSlideId == slideId).Select(x => (int?)x.SortOrder).MaxAsync(cancellationToken) ?? 0;
            db.SlideBlocks.Add(new SlideBlock
            {
                Id = Guid.NewGuid(),
                PackageSlideId = slideId,
                SortOrder = maxSort + 1,
                Kind = root?["kind"]?.GetValue<string>() ?? "callout",
                ContentJson = root?["content"]?.ToJsonString() ?? "{}"
            });
        }

        draft.Status = "Accepted";
        draft.DecidedAt = DateTimeOffset.UtcNow;
        db.PackageVersions.Add(new PackageVersion
        {
            Id = Guid.NewGuid(),
            ReportPackageId = draft.ReportPackageId,
            VersionLabel = $"AI Draft Accepted {DateTimeOffset.UtcNow:yyyyMMdd-HHmm}",
            CreatedBy = "AI package draft",
            ChangeSummary = draft.Title,
            SnapshotJson = draft.PayloadJson
        });
        await db.SaveChangesAsync(cancellationToken);
        return draft;
    }

    public async Task<AiPackageDraftSuggestion> RejectAsync(Guid draftId, string? reason, CancellationToken cancellationToken)
    {
        var draft = await db.AiPackageDraftSuggestions.FirstAsync(x => x.Id == draftId, cancellationToken);
        draft.Status = "Rejected";
        draft.DecidedAt = DateTimeOffset.UtcNow;
        draft.DecisionReason = reason;
        await db.SaveChangesAsync(cancellationToken);
        return draft;
    }
}

public sealed record XeroImportPreview(string Source, int TenantCount, IReadOnlyList<XeroImportTenantPreview> Tenants, string Message);
public sealed record XeroImportTenantPreview(string TenantId, string TenantName, string OrganizationName, bool HasLedgerScope, DateTimeOffset TokenExpiresAt);
public sealed record XeroLedgerSyncSettingsRequest(bool Enabled, int SyncEveryMinutes, int DailyTrialBalanceHourUtc, int RetentionYears);
public sealed record XeroLedgerSyncStatus(bool Enabled, int SyncEveryMinutes, int DailyTrialBalanceHourUtc, int RetentionYears, IReadOnlyList<XeroTenantLedgerStatus> Tenants);
public sealed record XeroTenantLedgerStatus(string TenantId, string TenantName, string ConnectionStatus, bool RequiresReconnectForLedger, int? LastJournalNumber, DateTimeOffset? LastSuccessfulSyncAt, string Status, string? LastError);
public sealed record XeroLedgerSyncResult(DateTimeOffset RanAt, int JournalsImported, int LinesImported, IReadOnlyList<XeroTenantLedgerSyncResult> Tenants);
public sealed record XeroTenantLedgerSyncResult(string TenantId, string TenantName, string Status, int JournalsImported, int LinesImported, string? Error);
public sealed record XeroJournalImportResult(int JournalsImported, int LinesImported, int MaxJournalNumber, IReadOnlyList<DateOnly> ActivityDates);
public sealed record FluxReviewDto(Guid ReportPackageId, bool IsSourceDataStale, string? SourceDataStaleReason, FluxReviewProgressDto Progress, IReadOnlyList<FluxReviewGroupDto> Groups);
public sealed record FluxReviewProgressDto(int TotalGroups, int RequiredExplanations, int OpenExplanations, int AutoSignedOff, int Prepared, int Reviewed);
public sealed record FluxReviewGroupDto(
    Guid Id,
    string FluxType,
    string StatementType,
    string GroupName,
    string CurrentPeriodKey,
    string PriorPeriodKey,
    decimal CurrentAmount,
    decimal PriorAmount,
    decimal RunningThreeMonthAmount,
    decimal VarianceAmount,
    decimal VariancePercent,
    decimal DollarThreshold,
    decimal PercentThreshold,
    string ThresholdLogic,
    bool RequiresExplanation,
    bool RequiresLedgerDetail,
    string LedgerDetailStatus,
    DateTimeOffset? LedgerDetailPulledAt,
    string Status,
    string Assignee,
    string Reviewer,
    DateOnly? DueDate,
    string ExplanationTemplate,
    string PriorExplanation,
    string Tags,
    string TrendJson,
    string DriverSummaryJson,
    string RiskFlagsJson,
    bool AutoSignedOff,
    string Explanation,
    string PreparedBy,
    DateTimeOffset? PreparedAt,
    string ReviewedBy,
    DateTimeOffset? ReviewedAt,
    string EvidenceJson)
{
    public static FluxReviewGroupDto From(FluxReviewGroup group)
        => new(
            group.Id,
            group.FluxType,
            group.StatementType,
            group.GroupName,
            group.CurrentPeriodKey,
            group.PriorPeriodKey,
            group.CurrentAmount,
            group.PriorAmount,
            group.RunningThreeMonthAmount,
            group.VarianceAmount,
            group.VariancePercent,
            group.DollarThreshold,
            group.PercentThreshold,
            group.ThresholdLogic,
            group.RequiresExplanation,
            group.RequiresLedgerDetail,
            group.LedgerDetailStatus,
            group.LedgerDetailPulledAt,
            group.Status,
            group.Assignee,
            group.Reviewer,
            group.DueDate,
            group.ExplanationTemplate,
            group.PriorExplanation,
            group.Tags,
            group.TrendJson,
            group.DriverSummaryJson,
            group.RiskFlagsJson,
            group.AutoSignedOff,
            group.Explanation,
            group.PreparedBy,
            group.PreparedAt,
            group.ReviewedBy,
            group.ReviewedAt,
            group.EvidenceJson);
}

public sealed record FluxReviewDrilldownDto(
    Guid GroupId,
    string FluxType,
    string StatementType,
    string GroupName,
    string CurrentPeriodKey,
    string PriorPeriodKey,
    decimal CurrentAmount,
    decimal PriorAmount,
    decimal RunningThreeMonthAmount,
    decimal VarianceAmount,
    decimal VariancePercent,
    IReadOnlyList<FluxReviewAccountDto> Accounts);

public sealed record FluxReviewAccountDto(
    string AccountCode,
    string AccountName,
    string AccountType,
    string FsLine,
    decimal CurrentAmount,
    decimal PriorAmount,
    decimal VarianceAmount,
    decimal VariancePercent,
    IReadOnlyList<FluxLedgerTransactionDto> CurrentTransactions,
    IReadOnlyList<FluxLedgerTransactionDto> PriorTransactions);

public sealed record FluxLedgerTransactionDto(
    DateOnly Date,
    int JournalNumber,
    string SourceType,
    string Reference,
    string Description,
    decimal NetAmount,
    decimal GrossAmount,
    decimal TaxAmount);
