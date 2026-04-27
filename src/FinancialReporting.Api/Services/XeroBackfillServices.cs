using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Services;

public static class XeroDateParser
{
    public static DateOnly? ReadDateOnly(string? value)
    {
        var dto = ReadDateTimeOffset(value);
        return dto is null ? null : DateOnly.FromDateTime(dto.Value.UtcDateTime);
    }

    public static DateTimeOffset? ReadDateTimeOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.StartsWith("/Date(", StringComparison.Ordinal) && value.EndsWith(")/", StringComparison.Ordinal))
        {
            var inner = value[6..^2];
            var signIndex = inner.IndexOfAny(['+', '-'], 1);
            var millisText = signIndex > 0 ? inner[..signIndex] : inner;
            if (long.TryParse(millisText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var millis))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(millis);
            }
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}

public sealed class XeroApiRequestScheduler(IConfiguration configuration, ILogger<XeroApiRequestScheduler> logger)
{
    private readonly ConcurrentDictionary<string, TenantBudget> _tenantBudgets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<DateTimeOffset> _appMinuteWindow = new();
    private readonly SemaphoreSlim _concurrency = new(configuration.GetValue("Xero:HardConcurrentLimit", 5), configuration.GetValue("Xero:HardConcurrentLimit", 5));

    public int SoftMinuteLimit => configuration.GetValue("Xero:SoftMinuteLimit", 45);
    public int SoftDailyLimit => configuration.GetValue("Xero:SoftDailyLimit", 4000);
    public int HardMinuteLimit => configuration.GetValue("Xero:HardMinuteLimit", 60);
    public int HardDailyLimit => configuration.GetValue("Xero:HardDailyLimit", 5000);
    public int AppMinuteLimit => configuration.GetValue("Xero:AppMinuteLimit", 10000);

    public async Task<XeroApiResponse> GetStringAsync(HttpClient client, string tenantId, string url, CancellationToken cancellationToken)
    {
        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            var maxAttempts = Math.Clamp(configuration.GetValue("Xero:RequestRetryAttempts", 4), 1, 8);
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                await WaitForBudgetAsync(tenantId, cancellationToken);
                try
                {
                    using var response = await client.GetAsync(url, cancellationToken);
                    var content = await response.Content.ReadAsStringAsync(cancellationToken);
                    var rate = CaptureRateLimit(response);
                    RecordCall(tenantId);

                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        var retryAfterSeconds = response.Headers.RetryAfter?.Delta?.TotalSeconds
                                                ?? (response.Headers.TryGetValues("Retry-After", out var retryValues)
                                                    && int.TryParse(retryValues.FirstOrDefault(), out var retryHeader)
                                                    ? retryHeader
                                                    : 60);
                        logger.LogWarning("Xero tenant {TenantId} hit rate limits. Pausing for {Seconds} seconds.", tenantId, retryAfterSeconds);
                        await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(retryAfterSeconds, 1, 600)), cancellationToken);
                        continue;
                    }

                    if (IsTransient(response.StatusCode) && attempt < maxAttempts)
                    {
                        await DelayForRetryAsync(tenantId, attempt, response.StatusCode.ToString(), cancellationToken);
                        continue;
                    }

                    return new XeroApiResponse(response.IsSuccessStatusCode, response.StatusCode, content, rate, response.Headers.RetryAfter?.Delta);
                }
                catch (HttpRequestException ex) when (attempt < maxAttempts)
                {
                    await DelayForRetryAsync(tenantId, attempt, ex.GetType().Name, cancellationToken);
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < maxAttempts)
                {
                    await DelayForRetryAsync(tenantId, attempt, ex.GetType().Name, cancellationToken);
                }
            }

            throw new HttpRequestException($"Xero request failed after {maxAttempts} attempts.");
        }
        finally
        {
            _concurrency.Release();
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private async Task DelayForRetryAsync(string tenantId, int attempt, string reason, CancellationToken cancellationToken)
    {
        var seconds = Math.Min(60, Math.Pow(2, attempt));
        logger.LogWarning("Transient Xero request failure for tenant {TenantId} on attempt {Attempt}: {Reason}. Retrying in {Seconds} seconds.", tenantId, attempt, reason, seconds);
        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
    }

    public XeroRateBudget GetBudgetSnapshot(string tenantId)
    {
        var budget = _tenantBudgets.GetOrAdd(tenantId, _ => new TenantBudget());
        lock (budget)
        {
            TrimWindows(budget, DateTimeOffset.UtcNow);
            return new XeroRateBudget(
                tenantId,
                budget.MinuteCalls.Count,
                budget.DailyCalls.Count,
                SoftMinuteLimit,
                SoftDailyLimit,
                HardMinuteLimit,
                HardDailyLimit);
        }
    }

    private async Task WaitForBudgetAsync(string tenantId, CancellationToken cancellationToken)
    {
        var budget = _tenantBudgets.GetOrAdd(tenantId, _ => new TenantBudget());
        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            lock (budget)
            {
                TrimWindows(budget, now);
                TrimAppWindow(now);
                if (budget.MinuteCalls.Count < SoftMinuteLimit
                    && budget.DailyCalls.Count < SoftDailyLimit
                    && _appMinuteWindow.Count < AppMinuteLimit)
                {
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    private void RecordCall(string tenantId)
    {
        var now = DateTimeOffset.UtcNow;
        var budget = _tenantBudgets.GetOrAdd(tenantId, _ => new TenantBudget());
        lock (budget)
        {
            TrimWindows(budget, now);
            budget.MinuteCalls.Enqueue(now);
            budget.DailyCalls.Enqueue(now);
        }

        _appMinuteWindow.Enqueue(now);
        TrimAppWindow(now);
    }

    private static void TrimWindows(TenantBudget budget, DateTimeOffset now)
    {
        while (budget.MinuteCalls.TryPeek(out var value) && value < now.AddMinutes(-1))
        {
            budget.MinuteCalls.TryDequeue(out _);
        }

        while (budget.DailyCalls.TryPeek(out var value) && value < now.AddDays(-1))
        {
            budget.DailyCalls.TryDequeue(out _);
        }
    }

    private void TrimAppWindow(DateTimeOffset now)
    {
        while (_appMinuteWindow.TryPeek(out var value) && value < now.AddMinutes(-1))
        {
            _appMinuteWindow.TryDequeue(out _);
        }
    }

    private static Dictionary<string, string> CaptureRateLimit(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "X-DayLimit-Remaining", "X-MinLimit-Remaining", "X-AppMinLimit-Remaining", "Retry-After" })
        {
            if (response.Headers.TryGetValues(name, out var values))
            {
                headers[name] = string.Join(",", values);
            }
        }

        return headers;
    }

    private sealed class TenantBudget
    {
        public ConcurrentQueue<DateTimeOffset> MinuteCalls { get; } = new();
        public ConcurrentQueue<DateTimeOffset> DailyCalls { get; } = new();
    }
}

public sealed class XeroBackfillService(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IDataProtectionProvider dataProtectionProvider,
    XeroTenantLedgerService ledgerService,
    XeroApiRequestScheduler scheduler,
    ILogger<XeroBackfillService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("FinanceApp.Secrets.v1");

    public async Task<XeroBackfillPreviewDto> PreviewAsync(AppDbContext db, XeroBackfillRequest request, CancellationToken cancellationToken)
    {
        var (from, to) = ResolveRange(request.FromPeriodKey, request.ToPeriodKey);
        var tenants = await ConnectedMappedTenantsAsync(db, request.TenantIds, cancellationToken);
        var months = Months(from, to).ToArray();
        var tenantPreviews = tenants.Select(x =>
        {
            var calls = EstimateCalls(months.Length, request.HydrateLedger);
            var budget = scheduler.GetBudgetSnapshot(x.Tenant.TenantId);
            var risk = calls + budget.CallsInDay >= scheduler.SoftDailyLimit
                ? "Projected calls approach the conservative daily tenant budget; run will queue or pause."
                : "Within conservative tenant budget.";
            return new XeroBackfillTenantPreviewDto(x.Tenant.TenantId, x.Tenant.TenantName, x.Organization.Name, calls, risk);
        }).ToArray();

        return new XeroBackfillPreviewDto(
            from.Key,
            to.Key,
            months.Length,
            tenantPreviews.Sum(x => x.EstimatedCalls),
            scheduler.SoftMinuteLimit,
            scheduler.SoftDailyLimit,
            request.HydrateLedger,
            tenantPreviews);
    }

    public async Task<XeroBackfillRunDto> QueueAsync(AppDbContext db, XeroBackfillRequest request, CancellationToken cancellationToken)
    {
        var preview = await PreviewAsync(db, request, cancellationToken);
        var tenants = await ConnectedMappedTenantsAsync(db, request.TenantIds, cancellationToken);
        var run = new XeroBackfillRun
        {
            Id = Guid.NewGuid(),
            FromPeriodKey = preview.FromPeriodKey,
            ToPeriodKey = preview.ToPeriodKey,
            Status = "Queued",
            EstimatedCalls = preview.EstimatedCalls,
            SummaryJson = JsonSerializer.Serialize(preview, JsonOptions),
            RateLimitJson = JsonSerializer.Serialize(new
            {
                scheduler.SoftMinuteLimit,
                scheduler.SoftDailyLimit,
                scheduler.HardMinuteLimit,
                scheduler.HardDailyLimit,
                scheduler.AppMinuteLimit
            }, JsonOptions)
        };
        db.XeroBackfillRuns.Add(run);
        foreach (var tenant in tenants)
        {
            db.XeroBackfillTenantTasks.Add(new XeroBackfillTenantTask
            {
                Id = Guid.NewGuid(),
                XeroBackfillRunId = run.Id,
                TenantId = tenant.Tenant.TenantId,
                TenantName = tenant.Tenant.TenantName,
                OrganizationId = tenant.Organization.Id,
                EstimatedCalls = EstimateCalls(preview.MonthCount, request.HydrateLedger)
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetRunAsync(db, run.Id, cancellationToken) ?? throw new InvalidOperationException("Queued backfill run could not be read.");
    }

    public async Task<XeroBackfillRunDto?> GetRunAsync(AppDbContext db, Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.XeroBackfillRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        var tasks = await db.XeroBackfillTenantTasks
            .AsNoTracking()
            .Where(x => x.XeroBackfillRunId == runId)
            .OrderBy(x => x.TenantName)
            .ToListAsync(cancellationToken);
        return XeroBackfillRunDto.From(run, tasks);
    }

    public async Task<XeroBackfillRunDto?> SetStatusAsync(AppDbContext db, Guid runId, string status, CancellationToken cancellationToken)
    {
        var run = await db.XeroBackfillRuns.FirstOrDefaultAsync(x => x.Id == runId, cancellationToken);
        if (run is null)
        {
            return null;
        }

        if (status == "Paused" && run.Status is "Queued" or "Running")
        {
            run.Status = "Paused";
        }
        else if (status == "Queued" && run.Status == "Paused")
        {
            run.Status = "Queued";
        }
        else if (status == "Cancelled" && run.Status is not ("Completed" or "Failed"))
        {
            run.Status = "Cancelled";
            run.CompletedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetRunAsync(db, runId, cancellationToken);
    }

    public async Task ProcessNextQueuedRunAsync(CancellationToken cancellationToken)
    {
        await using var db = await CreateDbContextAsync(cancellationToken);
        var queuedRuns = await db.XeroBackfillRuns
            .Where(x => x.Status == "Queued" || x.Status == "Running")
            .ToListAsync(cancellationToken);
        var run = queuedRuns.OrderBy(x => x.CreatedAt).FirstOrDefault();
        if (run is null)
        {
            return;
        }

        run.Status = "Running";
        run.StartedAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var tasks = await db.XeroBackfillTenantTasks
                .Where(x => x.XeroBackfillRunId == run.Id && x.Status != "Completed" && x.Status != "CompletedWithWarnings")
                .OrderBy(x => x.TenantName)
                .ToListAsync(cancellationToken);

            foreach (var task in tasks)
            {
                await db.Entry(run).ReloadAsync(cancellationToken);
                if (run.Status is "Paused" or "Cancelled")
                {
                    return;
                }

                await ProcessTenantTaskAsync(db, run, task, cancellationToken);
            }

            await db.Entry(run).ReloadAsync(cancellationToken);
            var allTasks = await db.XeroBackfillTenantTasks.Where(x => x.XeroBackfillRunId == run.Id).ToListAsync(cancellationToken);
            run.Status = allTasks.Any(x => x.Status == "Failed")
                ? "CompletedWithErrors"
                : allTasks.Any(x => x.Status == "CompletedWithWarnings")
                    ? "CompletedWithWarnings"
                    : "Completed";
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.ActualCalls = allTasks.Sum(x => x.ActualCalls);
            run.SummaryJson = JsonSerializer.Serialize(new
            {
                tenants = allTasks.Count,
                completed = allTasks.Count(x => x.Status is "Completed" or "CompletedWithWarnings"),
                warnings = allTasks.Count(x => x.Status == "CompletedWithWarnings"),
                failed = allTasks.Count(x => x.Status == "Failed"),
                statements = allTasks.Sum(x => x.StatementsImported),
                journals = allTasks.Sum(x => x.JournalsImported),
                journalLines = allTasks.Sum(x => x.JournalLinesImported)
            }, JsonOptions);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            run.Status = "Failed";
            run.Error = "Historical Xero backfill failed. Review tenant task details and retry.";
            run.CompletedAt = DateTimeOffset.UtcNow;
            logger.LogError(ex, "Xero backfill run {RunId} failed.", run.Id);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task ProcessTenantTaskAsync(AppDbContext db, XeroBackfillRun run, XeroBackfillTenantTask task, CancellationToken cancellationToken)
    {
        var actualCalls = 0;
        task.Status = "Running";
        task.UpdatedAt = DateTimeOffset.UtcNow;
        task.Error = null;
        task.ActualCalls = 0;
        task.JournalsImported = 0;
        task.JournalLinesImported = 0;
        task.StatementsImported = 0;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var tenant = await db.XeroTenantConnections.FirstAsync(x => x.TenantId == task.TenantId, cancellationToken);
            var organization = await db.Organizations.FirstAsync(x => x.Id == task.OrganizationId, cancellationToken);
            var accessToken = await EnsureValidTokenAsync(db, tenant, cancellationToken);
            var client = httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            client.DefaultRequestHeaders.Remove("xero-tenant-id");
            client.DefaultRequestHeaders.Add("xero-tenant-id", tenant.TenantId);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var (from, to) = ResolveRange(run.FromPeriodKey, run.ToPeriodKey);
            var months = Months(from, to).ToArray();
            var rawCallHeaders = new List<Dictionary<string, string>>();
            var statements = 0;

            foreach (var month in months)
            {
                await db.Entry(run).ReloadAsync(cancellationToken);
                if (run.Status == "Paused")
                {
                    task.Status = "Queued";
                    task.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }

                if (run.Status == "Cancelled")
                {
                    task.Status = "Cancelled";
                    task.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    return;
                }

                statements += await ImportMonthlyStatementsAsync(db, client, tenant, organization, month, rawCallHeaders, cancellationToken);
                actualCalls += 3;
                task.StatementsImported = statements;
                task.ActualCalls = actualCalls;
                task.RateLimitJson = JsonSerializer.Serialize(rawCallHeaders.TakeLast(20), JsonOptions);
                task.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }

            var hydrateLedger = ShouldHydrateLedger(run);
            if (!hydrateLedger)
            {
                task.Status = "Completed";
                task.ActualCalls = actualCalls;
                task.StatementsImported = statements;
                task.JournalsImported = 0;
                task.JournalLinesImported = 0;
                task.RateLimitJson = JsonSerializer.Serialize(rawCallHeaders.TakeLast(20), JsonOptions);
                task.CoverageJson = JsonSerializer.Serialize(new
                {
                    from = run.FromPeriodKey,
                    to = run.ToPeriodKey,
                    months = months.Length,
                    ledgerHydration = "Deferred"
                }, JsonOptions);
                task.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            try
            {
                var journals = await ImportJournalsAsync(db, client, tenant, from.Start, to.End, rawCallHeaders, cancellationToken);
                actualCalls += journals.Calls;
                task.JournalsImported += journals.JournalsImported;
                task.JournalLinesImported += journals.LinesImported;
                task.ActualCalls = actualCalls;
                task.RateLimitJson = JsonSerializer.Serialize(rawCallHeaders.TakeLast(20), JsonOptions);
                task.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);

                foreach (var month in months)
                {
                    await db.Entry(run).ReloadAsync(cancellationToken);
                    if (run.Status == "Paused")
                    {
                        task.Status = "Queued";
                        task.UpdatedAt = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync(cancellationToken);
                        return;
                    }

                    if (run.Status == "Cancelled")
                    {
                        task.Status = "Cancelled";
                        task.UpdatedAt = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync(cancellationToken);
                        return;
                    }

                    await ProjectGlForPeriodAsync(db, tenant.TenantId, organization.Id, month, cancellationToken);
                    await ReconcileTrialBalanceAsync(db, tenant.TenantId, organization.Id, month, cancellationToken);
                }

                await RebuildMonthlySummariesAsync(db, tenant.TenantId, organization.Id, from.Start, to.End, cancellationToken);
            }
            catch (Exception ledgerEx)
            {
                task.Status = statements > 0 ? "CompletedWithWarnings" : "Failed";
                task.ActualCalls = actualCalls;
                task.StatementsImported = statements;
                task.RateLimitJson = JsonSerializer.Serialize(rawCallHeaders.TakeLast(20), JsonOptions);
                task.CoverageJson = JsonSerializer.Serialize(new { from = run.FromPeriodKey, to = run.ToPeriodKey, months = months.Length }, JsonOptions);
                task.Error = $"Statement backfill completed; ledger hydration failed and can be retried: {ledgerEx.GetType().Name}: {ledgerEx.Message}";
                task.UpdatedAt = DateTimeOffset.UtcNow;
                logger.LogWarning(ledgerEx, "Xero ledger hydration failed for tenant {TenantId} after statements imported.", task.TenantId);
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            task.Status = "Completed";
            task.ActualCalls = actualCalls;
            task.StatementsImported = statements;
            task.RateLimitJson = JsonSerializer.Serialize(rawCallHeaders.TakeLast(20), JsonOptions);
            task.CoverageJson = JsonSerializer.Serialize(new { from = run.FromPeriodKey, to = run.ToPeriodKey, months = months.Length }, JsonOptions);
            task.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            task.Status = "Failed";
            task.ActualCalls += actualCalls;
            task.Error = $"Tenant backfill failed: {ex.GetType().Name}: {ex.Message}";
            task.UpdatedAt = DateTimeOffset.UtcNow;
            logger.LogError(ex, "Xero tenant backfill failed for {TenantId}.", task.TenantId);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<JournalImportStats> ImportJournalsAsync(
        AppDbContext db,
        HttpClient client,
        XeroTenantConnection tenant,
        DateOnly from,
        DateOnly to,
        List<Dictionary<string, string>> rateHeaders,
        CancellationToken cancellationToken)
    {
        var apiBase = configuration["Xero:ApiBaseUrl"] ?? "https://api.xero.com/api.xro/2.0";
        var offset = 0;
        var calls = 0;
        var journalCount = 0;
        var lineCount = 0;
        var maxPages = configuration.GetValue("Xero:BackfillMaxJournalPagesPerTenant", 500);
        for (var page = 0; page < maxPages; page++)
        {
            var url = $"{apiBase.TrimEnd('/')}/Journals?paymentsOnly=false";
            if (offset > 0)
            {
                url += $"&offset={offset}";
            }

            var response = await scheduler.GetStringAsync(client, tenant.TenantId, url, cancellationToken);
            calls++;
            rateHeaders.Add(response.RateLimitHeaders);
            if (!response.IsSuccess)
            {
                throw new InvalidOperationException($"Xero journals endpoint returned {response.StatusCode}.");
            }

            var pageInfo = InspectJournalPayload(response.Content);
            if (pageInfo.Count == 0 || pageInfo.MaxJournalNumber <= offset)
            {
                break;
            }

            var imported = await ledgerService.UpsertJournalsFromPayloadAsync(db, tenant.TenantId, response.Content, from, to, cancellationToken);
            journalCount += imported.JournalsImported;
            lineCount += imported.LinesImported;
            offset = pageInfo.MaxJournalNumber;
        }

        var cursor = await db.XeroLedgerSyncCursors.FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId, cancellationToken)
                     ?? new XeroLedgerSyncCursor { Id = Guid.NewGuid(), TenantId = tenant.TenantId };
        if (db.Entry(cursor).State == EntityState.Detached)
        {
            db.XeroLedgerSyncCursors.Add(cursor);
        }

        if (offset > cursor.LastJournalNumber.GetValueOrDefault())
        {
            cursor.LastJournalNumber = offset;
        }
        cursor.Status = "Completed";
        cursor.LastSuccessfulSyncAt = DateTimeOffset.UtcNow;
        cursor.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return new JournalImportStats(calls, journalCount, lineCount);
    }

    private async Task<int> ImportMonthlyStatementsAsync(
        AppDbContext db,
        HttpClient client,
        XeroTenantConnection tenant,
        Organization organization,
        BackfillMonth month,
        List<Dictionary<string, string>> rateHeaders,
        CancellationToken cancellationToken)
    {
        var period = await EnsureReportingPeriodAsync(db, month.Start, cancellationToken);
        var apiBase = configuration["Xero:ApiBaseUrl"] ?? "https://api.xero.com/api.xro/2.0";
        var reports = new[]
        {
            new ReportRequest("ProfitAndLoss", $"{apiBase.TrimEnd('/')}/Reports/ProfitAndLoss?fromDate={DateParam(month.Start)}&toDate={DateParam(month.End)}&standardLayout=true&paymentsOnly=false"),
            new ReportRequest("BalanceSheet", $"{apiBase.TrimEnd('/')}/Reports/BalanceSheet?date={DateParam(month.End)}&standardLayout=true&paymentsOnly=false"),
            new ReportRequest("TrialBalance", $"{apiBase.TrimEnd('/')}/Reports/TrialBalance?date={DateParam(month.End)}&paymentsOnly=false")
        };

        var imported = 0;
        foreach (var report in reports)
        {
            var response = await scheduler.GetStringAsync(client, tenant.TenantId, report.Url, cancellationToken);
            rateHeaders.Add(response.RateLimitHeaders);
            if (!response.IsSuccess)
            {
                throw new InvalidOperationException($"{report.Type} returned {response.StatusCode}.");
            }

            var snapshot = new XeroRawReportSnapshot
            {
                Id = Guid.NewGuid(),
                OrganizationId = organization.Id,
                ReportingPeriodId = period.Id,
                TenantId = tenant.TenantId,
                ReportType = report.Type,
                Basis = "accrual",
                RequestUrl = report.Url,
                PayloadJson = response.Content,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.XeroRawReportSnapshots.Add(snapshot);
            await db.FinancialStatementLines
                .Where(x => x.OrganizationId == organization.Id
                            && x.ReportingPeriodId == period.Id
                            && x.TenantId == tenant.TenantId
                            && x.StatementType == report.Type
                            && x.ReportPackageId == null)
                .ExecuteDeleteAsync(cancellationToken);

            foreach (var line in XeroIntegrationService.ParseStatementLines(report.Type, response.Content, tenant.TenantId, snapshot.Id))
            {
                db.FinancialStatementLines.Add(new FinancialStatementLine
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organization.Id,
                    ReportingPeriodId = period.Id,
                    ReportPackageId = null,
                    XeroRawReportSnapshotId = snapshot.Id,
                    TenantId = tenant.TenantId,
                    StatementType = line.StatementType,
                    Section = line.Section,
                    RowPath = line.RowPath,
                    LineName = line.LineName,
                    AccountCode = line.AccountCode,
                    CurrentAmount = line.CurrentAmount,
                    PriorAmount = line.PriorAmount,
                    AmountsJson = JsonSerializer.Serialize(line.Amounts, JsonOptions),
                    SortOrder = line.SortOrder
                });
            }

            if (report.Type == "TrialBalance")
            {
                var balances = XeroIntegrationService.ParseStatementLines("TrialBalance", response.Content, tenant.TenantId, snapshot.Id)
                    .Where(x => !string.IsNullOrWhiteSpace(x.AccountCode))
                    .GroupBy(x => x.AccountCode, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(x => x.Key, x => x.Sum(line => line.CurrentAmount), StringComparer.OrdinalIgnoreCase);
                db.XeroTrialBalanceSnapshots.Add(new XeroTrialBalanceSnapshot
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenant.TenantId,
                    OrganizationId = organization.Id,
                    ReportingPeriodId = period.Id,
                    SnapshotDate = month.End,
                    Basis = "accrual",
                    PayloadJson = response.Content,
                    AccountBalancesJson = JsonSerializer.Serialize(balances, JsonOptions)
                });
            }

            imported++;
        }

        await db.StatementRuns
            .Where(x => x.OrganizationId == organization.Id && x.ReportingPeriodId == period.Id && x.TenantId == tenant.TenantId && x.ReportPackageId == null)
            .ExecuteDeleteAsync(cancellationToken);
        db.StatementRuns.Add(new StatementRun
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            ReportingPeriodId = period.Id,
            TenantId = tenant.TenantId,
            Basis = "accrual",
            Status = "Completed",
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            SummaryJson = JsonSerializer.Serialize(new { imported, month = month.Key }, JsonOptions)
        });
        await db.SaveChangesAsync(cancellationToken);
        return imported;
    }

    private async Task ProjectGlForPeriodAsync(AppDbContext db, string tenantId, Guid organizationId, BackfillMonth month, CancellationToken cancellationToken)
    {
        var period = await EnsureReportingPeriodAsync(db, month.Start, cancellationToken);
        var journals = await db.XeroJournals
            .AsNoTracking()
            .Include(x => x.Lines)
            .Where(x => x.TenantId == tenantId && x.JournalDate >= month.Start && x.JournalDate <= month.End)
            .ToListAsync(cancellationToken);
        var rows = journals
            .SelectMany(x => x.Lines.Select(line => new
            {
                x.JournalDate,
                x.Reference,
                line.AccountCode,
                line.AccountName,
                line.Description,
                line.NetAmount
            }))
            .ToList();

        var priorAccounts = await db.GlAccounts
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.TenantId == tenantId && x.ReportingPeriodId != period.Id)
            .ToListAsync(cancellationToken);
        var fsLineDefinitions = await db.FsLineDefinitions
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsActive)
            .OrderBy(x => x.StatementType)
            .ThenBy(x => x.Section)
            .ThenBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);

        foreach (var group in rows.GroupBy(x => new { x.AccountCode, x.AccountName }))
        {
            if (string.IsNullOrWhiteSpace(group.Key.AccountCode))
            {
                continue;
            }

            var account = await db.GlAccounts
                .Include(x => x.Transactions)
                .FirstOrDefaultAsync(x => x.OrganizationId == organizationId
                                          && x.ReportingPeriodId == period.Id
                                          && x.TenantId == tenantId
                                          && x.Code == group.Key.AccountCode,
                    cancellationToken);
            if (account is null)
            {
                account = new GlAccount
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = organizationId,
                    ReportingPeriodId = period.Id,
                    TenantId = tenantId,
                    Code = group.Key.AccountCode,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.GlAccounts.Add(account);
            }

            account.Name = string.IsNullOrWhiteSpace(group.Key.AccountName) ? group.Key.AccountCode : group.Key.AccountName;
            account.Type = GuessTypeFromAmount(group.Sum(x => x.NetAmount));
            account.Class = account.Type == "Expense" ? "Operating Expense" : "Income Statement";
            var suggestedFsLine = SuggestFsLineFromDefinitions(account.Name, account.Type, fsLineDefinitions) ?? GuessFsLine(account.Name, account.Type);
            account.FsLine = string.IsNullOrWhiteSpace(account.FsLine) ? suggestedFsLine : account.FsLine;
            account.AiSuggestedFsLine = suggestedFsLine;
            account.MappingConfidence = string.Equals(account.FsLine, account.AiSuggestedFsLine, StringComparison.OrdinalIgnoreCase) ? 0.85m : 0.7m;
            account.IsFirstSeen = !priorAccounts.Any(x => string.Equals(x.TenantId, tenantId, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Code, account.Code, StringComparison.OrdinalIgnoreCase));
            account.ReviewStatus = account.ReviewStatus == MappingReviewStatus.Reviewed
                ? MappingReviewStatus.Reviewed
                : account.IsFirstSeen ? MappingReviewStatus.New : MappingReviewStatus.Suggested;
            account.MonthlyBalancesJson = JsonSerializer.Serialize(new[] { group.Sum(x => x.NetAmount) }, JsonOptions);
            account.PriorPeriodHistoryJson = JsonSerializer.Serialize(priorAccounts.Where(x => x.Code == account.Code).Select(x => x.ReportingPeriodId).Take(12), JsonOptions);
            account.UpdatedAt = DateTimeOffset.UtcNow;

            db.GlTransactions.RemoveRange(account.Transactions);
            foreach (var row in group)
            {
                var amount = row.NetAmount;
                db.GlTransactions.Add(new GlTransaction
                {
                    Id = Guid.NewGuid(),
                    GlAccountId = account.Id,
                    TransactionDate = row.JournalDate,
                    Description = string.IsNullOrWhiteSpace(row.Description) ? row.Reference : row.Description,
                    Debit = amount < 0 ? Math.Abs(amount) : 0m,
                    Credit = amount > 0 ? amount : 0m,
                    Source = "Xero Journal"
                });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task ReconcileTrialBalanceAsync(AppDbContext db, string tenantId, Guid organizationId, BackfillMonth month, CancellationToken cancellationToken)
    {
        var period = await db.ReportingPeriods.AsNoTracking().FirstAsync(x => x.Key == month.Key, cancellationToken);
        var tbSnapshots = await db.XeroTrialBalanceSnapshots
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SnapshotDate == month.End)
            .ToListAsync(cancellationToken);
        var latestTb = tbSnapshots
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefault();
        if (latestTb is null)
        {
            return;
        }

        var tbBalances = ToCaseInsensitiveBalances(latestTb.AccountBalancesJson);
        var openingCandidates = await db.XeroTrialBalanceSnapshots
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.SnapshotDate < month.Start)
            .ToListAsync(cancellationToken);
        var openingTb = SelectOpeningTrialBalance(openingCandidates, month.Start);
        var openingBalances = openingTb is null
            ? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase)
            : ToCaseInsensitiveBalances(openingTb.AccountBalancesJson);
        if (openingTb is not null)
        {
            await ApplyYearEndCloseToOpeningBalancesAsync(db, tenantId, openingTb, openingBalances, cancellationToken);
        }
        var movementStart = openingTb?.SnapshotDate;
        var journals = await db.XeroJournals
            .AsNoTracking()
            .Include(x => x.Lines)
            .Where(x => x.TenantId == tenantId
                        && x.JournalDate <= month.End
                        && (movementStart == null || x.JournalDate > movementStart.Value))
            .ToListAsync(cancellationToken);
        var movementRows = journals
            .SelectMany(x => x.Lines)
            .GroupBy(x => x.AccountCode)
            .Select(x => new { AccountCode = x.Key, Amount = x.Sum(line => line.NetAmount) })
            .ToList();
        var movements = movementRows.ToDictionary(x => x.AccountCode, x => x.Amount, StringComparer.OrdinalIgnoreCase);
        var ledgerBalances = new Dictionary<string, decimal>(openingBalances, StringComparer.OrdinalIgnoreCase);
        foreach (var movement in movements)
        {
            ledgerBalances[movement.Key] = ledgerBalances.GetValueOrDefault(movement.Key) + movement.Value;
        }

        var diffs = tbBalances.Keys.Union(ledgerBalances.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(code => new
            {
                code,
                tb = tbBalances.GetValueOrDefault(code),
                ledger = ledgerBalances.GetValueOrDefault(code)
            })
            .Select(x => new { x.code, x.tb, x.ledger, diff = x.tb - x.ledger })
            .Where(x => Math.Abs(x.diff) >= 0.01m)
            .OrderByDescending(x => Math.Abs(x.diff))
            .Take(100)
            .ToArray();

        db.XeroLedgerReconciliationRuns.Add(new XeroLedgerReconciliationRun
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            OrganizationId = organizationId,
            ReportingPeriodId = period.Id,
            SnapshotDate = month.End,
            Status = openingTb is null || diffs.Length > 0 ? "Review" : "Passed",
            DifferenceAmount = diffs.Sum(x => Math.Abs(x.diff)),
            MissingAccountsJson = JsonSerializer.Serialize(diffs.Select(x => x.code), JsonOptions),
            SummaryJson = JsonSerializer.Serialize(new
            {
                month = month.Key,
                openingSnapshotDate = openingTb?.SnapshotDate.ToString("yyyy-MM-dd"),
                openingAccountCount = openingBalances.Count,
                movementJournalCount = journals.Count,
                differences = diffs,
                note = openingTb is null
                    ? "No opening trial balance snapshot was available before this period."
                    : "Ledger balance equals opening trial balance plus imported Xero journal movements."
            }, JsonOptions)
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RebuildMonthlySummariesAsync(AppDbContext db, string tenantId, Guid organizationId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var journals = await db.XeroJournals
            .AsNoTracking()
            .Include(x => x.Lines)
            .Where(x => x.TenantId == tenantId && x.JournalDate >= from && x.JournalDate <= to)
            .ToListAsync(cancellationToken);
        var rows = journals
            .SelectMany(x => x.Lines.Select(line => new
            {
                MonthKey = $"{x.JournalDate.Year:D4}-{x.JournalDate.Month:D2}",
                line.AccountCode,
                line.AccountName,
                line.NetAmount
            }))
            .ToList();

        foreach (var group in rows.GroupBy(x => new { x.MonthKey, x.AccountCode, x.AccountName }))
        {
            var summary = await db.XeroLedgerMonthlySummaries.FirstOrDefaultAsync(x =>
                x.TenantId == tenantId
                && x.MonthKey == group.Key.MonthKey
                && x.AccountCode == group.Key.AccountCode,
                cancellationToken);
            if (summary is null)
            {
                summary = new XeroLedgerMonthlySummary
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    MonthKey = group.Key.MonthKey,
                    AccountCode = group.Key.AccountCode
                };
                db.XeroLedgerMonthlySummaries.Add(summary);
            }

            summary.OrganizationId = organizationId;
            summary.AccountName = group.Key.AccountName;
            summary.NetAmount = group.Sum(x => x.NetAmount);
            summary.LastRolledUpAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<XeroDataCoverageDto> BuildCoverageAsync(AppDbContext db, string? fromPeriodKey, string? toPeriodKey, CancellationToken cancellationToken)
    {
        var (from, to) = ResolveRange(fromPeriodKey, toPeriodKey);
        var tenants = await ConnectedMappedTenantsAsync(db, null, cancellationToken);
        var months = Months(from, to).ToArray();
        var rows = new List<XeroDataCoverageRowDto>();

        foreach (var tenant in tenants)
        {
            foreach (var month in months)
            {
                var period = await db.ReportingPeriods.AsNoTracking().FirstOrDefaultAsync(x => x.Key == month.Key, cancellationToken);
                var journalCount = await db.XeroJournals.CountAsync(x => x.TenantId == tenant.Tenant.TenantId && x.JournalDate >= month.Start && x.JournalDate <= month.End, cancellationToken);
                var journalRows = await db.XeroJournals
                    .AsNoTracking()
                    .Include(x => x.Lines)
                    .Where(x => x.TenantId == tenant.Tenant.TenantId && x.JournalDate >= month.Start && x.JournalDate <= month.End)
                    .ToListAsync(cancellationToken);
                var journalLineCount = journalRows.Sum(x => x.Lines.Count);
                var snapshotTypes = period is null
                    ? []
                    : await db.XeroRawReportSnapshots
                        .AsNoTracking()
                        .Where(x => x.TenantId == tenant.Tenant.TenantId && x.ReportingPeriodId == period.Id)
                        .Select(x => x.ReportType)
                        .Distinct()
                        .ToListAsync(cancellationToken);
                var statementLineCount = period is null
                    ? 0
                    : await db.FinancialStatementLines.CountAsync(x => x.TenantId == tenant.Tenant.TenantId && x.ReportingPeriodId == period.Id, cancellationToken);
                var reconciliations = await db.XeroLedgerReconciliationRuns
                    .AsNoTracking()
                    .Where(x => x.TenantId == tenant.Tenant.TenantId && x.SnapshotDate == month.End)
                    .ToListAsync(cancellationToken);
                var reconciliation = reconciliations
                    .OrderByDescending(x => x.CreatedAt)
                    .FirstOrDefault();

                var hasReports = snapshotTypes.Contains("ProfitAndLoss") && snapshotTypes.Contains("BalanceSheet") && snapshotTypes.Contains("TrialBalance");
                var status = journalCount == 0 && statementLineCount == 0
                    ? "No activity"
                    : hasReports && statementLineCount > 0
                        ? "Complete"
                        : "Needs retry";
                rows.Add(new XeroDataCoverageRowDto(
                    tenant.Tenant.TenantId,
                    tenant.Tenant.TenantName,
                    tenant.Organization.Key,
                    tenant.Organization.Name,
                    month.Key,
                    status,
                    journalCount,
                    journalLineCount,
                    snapshotTypes.ToArray(),
                    statementLineCount,
                    reconciliation?.Status,
                    reconciliation?.DifferenceAmount ?? 0m));
            }
        }

        return new XeroDataCoverageDto(from.Key, to.Key, rows);
    }

    private async Task<List<MappedTenant>> ConnectedMappedTenantsAsync(AppDbContext db, IReadOnlyCollection<string>? tenantIds, CancellationToken cancellationToken)
    {
        var requestedTenantIds = tenantIds?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tenants = await db.XeroTenantConnections
            .AsNoTracking()
            .Where(x => x.ConnectionStatus == "Connected")
            .ToListAsync(cancellationToken);
        var mappingRows = await db.XeroTenantEntityMappings
            .AsNoTracking()
            .Where(x => !x.IsIgnored)
            .ToListAsync(cancellationToken);
        var mappings = mappingRows.ToDictionary(x => x.TenantId, StringComparer.OrdinalIgnoreCase);
        var organizations = await db.Organizations
            .AsNoTracking()
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        return tenants
            .Where(x => requestedTenantIds is null || requestedTenantIds.Count == 0 || requestedTenantIds.Contains(x.TenantId))
            .Where(x => mappings.ContainsKey(x.TenantId) && organizations.ContainsKey(mappings[x.TenantId].OrganizationId))
            .Select(x => new MappedTenant(x, organizations[mappings[x.TenantId].OrganizationId]))
            .OrderBy(x => x.Tenant.TenantName)
            .ToList();
    }

    private static Dictionary<string, decimal> ToCaseInsensitiveBalances(string balancesJson)
    {
        var parsed = JsonSerializer.Deserialize<Dictionary<string, decimal>>(balancesJson, JsonOptions)
                     ?? new Dictionary<string, decimal>();
        return new Dictionary<string, decimal>(parsed, StringComparer.OrdinalIgnoreCase);
    }

    private static XeroTrialBalanceSnapshot? SelectOpeningTrialBalance(IEnumerable<XeroTrialBalanceSnapshot> candidates, DateOnly periodStart)
    {
        var rows = candidates
            .Where(x => x.SnapshotDate < periodStart)
            .ToList();
        var latestYearEnd = rows
            .Where(x => x.SnapshotDate.Month == 12 && x.SnapshotDate.Day == 31)
            .OrderByDescending(x => x.SnapshotDate)
            .ThenByDescending(x => x.CreatedAt)
            .FirstOrDefault();
        return latestYearEnd ?? rows
            .OrderByDescending(x => x.SnapshotDate)
            .ThenByDescending(x => x.CreatedAt)
            .FirstOrDefault();
    }

    private static async Task ApplyYearEndCloseToOpeningBalancesAsync(
        AppDbContext db,
        string tenantId,
        XeroTrialBalanceSnapshot openingTb,
        Dictionary<string, decimal> openingBalances,
        CancellationToken cancellationToken)
    {
        if (openingTb.ReportingPeriodId is null || openingTb.SnapshotDate.Month != 12)
        {
            return;
        }

        var openingLines = await db.FinancialStatementLines
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId
                        && x.ReportingPeriodId == openingTb.ReportingPeriodId.Value
                        && x.ReportPackageId == null
                        && x.StatementType == "TrialBalance"
                        && x.AccountCode != "")
            .ToListAsync(cancellationToken);
        if (openingLines.Count == 0)
        {
            return;
        }

        var profitAndLossLines = openingLines
            .Where(x => IsProfitAndLossTrialBalanceSection(x.Section))
            .ToList();
        var closingAmount = profitAndLossLines.Sum(x => x.CurrentAmount);
        if (closingAmount == 0m)
        {
            return;
        }

        foreach (var line in profitAndLossLines)
        {
            openingBalances[line.AccountCode] = 0m;
        }

        var equityLine = openingLines
            .Where(x => x.Section.Contains("equity", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.LineName.Contains("retained", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.LineName.Contains("member", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => Math.Abs(x.CurrentAmount))
            .FirstOrDefault();
        if (equityLine is null)
        {
            return;
        }

        openingBalances[equityLine.AccountCode] = openingBalances.GetValueOrDefault(equityLine.AccountCode) + closingAmount;
    }

    private static bool IsProfitAndLossTrialBalanceSection(string section)
        => section.Contains("revenue", StringComparison.OrdinalIgnoreCase)
           || section.Contains("income", StringComparison.OrdinalIgnoreCase)
           || section.Contains("expense", StringComparison.OrdinalIgnoreCase)
           || section.Contains("cost", StringComparison.OrdinalIgnoreCase);

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
            tenant.ConnectionStatus = "NeedsReconnect";
            tenant.LastError = $"Token refresh failed: {response.StatusCode}";
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("Xero refresh token failed; reconnect the tenant.");
        }

        var token = JsonSerializer.Deserialize<XeroTokenResponse>(content, JsonOptions)
                    ?? throw new InvalidOperationException("Xero refresh response could not be parsed.");
        tenant.EncryptedAccessToken = Protect(token.AccessToken);
        tenant.EncryptedRefreshToken = Protect(token.RefreshToken);
        tenant.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
        tenant.ConnectionStatus = "Connected";
        tenant.LastError = null;
        tenant.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return token.AccessToken;
    }

    private async Task<ReportingPeriod> EnsureReportingPeriodAsync(AppDbContext db, DateOnly date, CancellationToken cancellationToken)
    {
        var key = $"{date.Year:D4}-{date.Month:D2}";
        var existing = await db.ReportingPeriods.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var start = new DateOnly(date.Year, date.Month, 1);
        var period = new ReportingPeriod
        {
            Id = Guid.NewGuid(),
            Key = key,
            Label = new DateTime(date.Year, date.Month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            PeriodStart = start,
            PeriodEnd = start.AddMonths(1).AddDays(-1),
            IsClosed = false
        };
        db.ReportingPeriods.Add(period);
        await db.SaveChangesAsync(cancellationToken);
        return period;
    }

    private async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(configuration.GetConnectionString("SqliteConnection") ?? "Data Source=financial-reporting-dev.db")
            .Options;
        var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await SqliteSchemaPatch.EnsureAsync(db, cancellationToken);
        return db;
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

    private static JournalPageInfo InspectJournalPayload(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("Journals", out var journals) || journals.ValueKind != JsonValueKind.Array)
        {
            return new JournalPageInfo(0, 0);
        }

        var count = 0;
        var max = 0;
        foreach (var journal in journals.EnumerateArray())
        {
            count++;
            if (journal.TryGetProperty("JournalNumber", out var number) && number.TryGetInt32(out var parsed))
            {
                max = Math.Max(max, parsed);
            }
        }

        return new JournalPageInfo(count, max);
    }

    private static bool ShouldHydrateLedger(XeroBackfillRun run)
    {
        if (string.IsNullOrWhiteSpace(run.SummaryJson))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(run.SummaryJson);
            return document.RootElement.TryGetProperty("hydrateLedger", out var hydrateLedger)
                   && hydrateLedger.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int EstimateCalls(int monthCount, bool hydrateLedger)
        => monthCount * 3 + (hydrateLedger ? 25 : 0);

    private static (BackfillPeriod From, BackfillPeriod To) ResolveRange(string? fromPeriodKey, string? toPeriodKey)
    {
        var from = ParsePeriodKey(string.IsNullOrWhiteSpace(fromPeriodKey) ? "2025-01" : fromPeriodKey!);
        var to = string.Equals(toPeriodKey, "current", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(toPeriodKey)
            ? new BackfillPeriod(DateOnly.FromDateTime(DateTime.UtcNow.Date))
            : ParsePeriodKey(toPeriodKey!);
        if (to.Start < from.Start)
        {
            throw new InvalidOperationException("Backfill end period must be after start period.");
        }

        return (from, to);
    }

    private static BackfillPeriod ParsePeriodKey(string periodKey)
    {
        var parts = periodKey.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var year) || !int.TryParse(parts[1], out var month) || month is < 1 or > 12)
        {
            throw new InvalidOperationException("Period key must be formatted as yyyy-MM.");
        }

        return new BackfillPeriod(new DateOnly(year, month, 1));
    }

    private static IEnumerable<BackfillMonth> Months(BackfillPeriod from, BackfillPeriod to)
    {
        for (var start = from.Start; start <= to.Start; start = start.AddMonths(1))
        {
            yield return new BackfillMonth(
                $"{start.Year:D4}-{start.Month:D2}",
                start,
                start.AddMonths(1).AddDays(-1));
        }
    }

    private static string DateParam(DateOnly date)
        => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string GuessTypeFromAmount(decimal amount)
        => amount < 0 ? "Expense" : "Revenue";

    private static string GuessFsLine(string name, string type)
        => type == "Expense" ? $"Operating Expense - {name}" : $"Revenue - {name}";

    private static string? SuggestFsLineFromDefinitions(string accountName, string accountType, IReadOnlyList<FsLineDefinition> definitions)
    {
        if (definitions.Count == 0)
        {
            return null;
        }

        var wantsBalanceSheet = accountType.Contains("asset", StringComparison.OrdinalIgnoreCase)
                                || accountType.Contains("liabil", StringComparison.OrdinalIgnoreCase)
                                || accountType.Contains("equity", StringComparison.OrdinalIgnoreCase);
        var scoped = definitions
            .Where(x => wantsBalanceSheet ? x.StatementType == "BalanceSheet" : x.StatementType == "IncomeStatement")
            .ToArray();
        if (scoped.Length == 0)
        {
            scoped = definitions.ToArray();
        }

        var direct = scoped.FirstOrDefault(x =>
            accountName.Contains(x.Name, StringComparison.OrdinalIgnoreCase)
            || x.Name.Contains(accountName, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
        {
            return direct.Name;
        }

        if (accountType.Contains("expense", StringComparison.OrdinalIgnoreCase) || accountType.Contains("cost", StringComparison.OrdinalIgnoreCase))
        {
            return scoped.FirstOrDefault(x => x.NormalBalance == "Debit")?.Name;
        }

        return scoped.FirstOrDefault(x => x.NormalBalance == "Credit")?.Name ?? scoped.First().Name;
    }

    private sealed record MappedTenant(XeroTenantConnection Tenant, Organization Organization);
    private sealed record BackfillPeriod(DateOnly Start)
    {
        public string Key => $"{Start.Year:D4}-{Start.Month:D2}";
        public DateOnly End => Start.AddMonths(1).AddDays(-1);
    }
    private sealed record BackfillMonth(string Key, DateOnly Start, DateOnly End);
    private sealed record ReportRequest(string Type, string Url);
    private sealed record JournalPageInfo(int Count, int MaxJournalNumber);
    private sealed record JournalImportStats(int Calls, int JournalsImported, int LinesImported);
}

public sealed class XeroBackfillWorker(IServiceScopeFactory scopeFactory, ILogger<XeroBackfillWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<XeroBackfillService>();
                await service.ProcessNextQueuedRunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Xero backfill worker loop failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        }
    }
}

public sealed record XeroApiResponse(bool IsSuccess, HttpStatusCode StatusCode, string Content, Dictionary<string, string> RateLimitHeaders, TimeSpan? RetryAfter);
public sealed record XeroRateBudget(string TenantId, int CallsInMinute, int CallsInDay, int SoftMinuteLimit, int SoftDailyLimit, int HardMinuteLimit, int HardDailyLimit);
public sealed record XeroBackfillRequest(string? FromPeriodKey, string? ToPeriodKey, string[]? TenantIds = null, bool HydrateLedger = false);
public sealed record XeroBackfillTenantPreviewDto(string TenantId, string TenantName, string OrganizationName, int EstimatedCalls, string Risk);
public sealed record XeroBackfillPreviewDto(string FromPeriodKey, string ToPeriodKey, int MonthCount, int EstimatedCalls, int SoftMinuteLimit, int SoftDailyLimit, bool HydrateLedger, XeroBackfillTenantPreviewDto[] Tenants);
public sealed record XeroBackfillTenantTaskDto(Guid Id, string TenantId, string TenantName, Guid OrganizationId, string Status, int EstimatedCalls, int ActualCalls, int JournalsImported, int JournalLinesImported, int StatementsImported, string CoverageJson, string RateLimitJson, string? Error)
{
    public static XeroBackfillTenantTaskDto From(XeroBackfillTenantTask task)
        => new(task.Id, task.TenantId, task.TenantName, task.OrganizationId, task.Status, task.EstimatedCalls, task.ActualCalls, task.JournalsImported, task.JournalLinesImported, task.StatementsImported, task.CoverageJson, task.RateLimitJson, task.Error);
}

public sealed record XeroBackfillRunDto(Guid Id, string FromPeriodKey, string ToPeriodKey, string Status, int EstimatedCalls, int ActualCalls, string SummaryJson, string RateLimitJson, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, XeroBackfillTenantTaskDto[] Tasks)
{
    public static XeroBackfillRunDto From(XeroBackfillRun run, IEnumerable<XeroBackfillTenantTask> tasks)
        => new(run.Id, run.FromPeriodKey, run.ToPeriodKey, run.Status, run.EstimatedCalls, run.ActualCalls, run.SummaryJson, run.RateLimitJson, run.Error, run.CreatedAt, run.StartedAt, run.CompletedAt, tasks.Select(XeroBackfillTenantTaskDto.From).ToArray());
}

public sealed record XeroDataCoverageRowDto(string TenantId, string TenantName, string OrganizationKey, string OrganizationName, string PeriodKey, string Status, int JournalCount, int JournalLineCount, string[] RawSnapshotTypes, int StatementLineCount, string? ReconciliationStatus, decimal ReconciliationDifference);
public sealed record XeroDataCoverageDto(string FromPeriodKey, string ToPeriodKey, List<XeroDataCoverageRowDto> Rows);
