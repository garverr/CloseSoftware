using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using FinancialReporting.Api.Hubs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace FinancialReporting.Api.Services;

public sealed record VarianceResult(decimal Amount, decimal Percent);

public static class FinancialMath
{
    public static VarianceResult Variance(decimal current, decimal prior)
    {
        var amount = current - prior;
        var percent = prior == 0m ? 0m : Math.Round(amount / prior * 100m, 1);
        return new VarianceResult(amount, percent);
    }

    public static decimal ConsolidatedTotal(IEnumerable<decimal> entityAmounts, IEnumerable<decimal> eliminations)
        => entityAmounts.Sum() - eliminations.Sum();

    public static string KpiStatus(decimal current, decimal target, bool higherIsBetter = true)
    {
        if (target == 0m)
        {
            return "neutral";
        }

        var ratio = current / target;
        return higherIsBetter
            ? ratio >= 1m ? "good" : ratio >= 0.9m ? "warn" : "bad"
            : ratio <= 1m ? "good" : ratio <= 1.1m ? "warn" : "bad";
    }
}

public static class FormulaMath
{
    public static decimal Evaluate(string expression)
    {
        var parser = new Parser(expression);
        var value = parser.ParseExpression();
        parser.SkipWhiteSpace();
        if (!parser.IsAtEnd)
        {
            throw new InvalidOperationException($"Unexpected token near position {parser.Position + 1}.");
        }

        return decimal.Round(value, 2);
    }

    private sealed class Parser(string expression)
    {
        private readonly string _expression = expression;
        public int Position { get; private set; }
        public bool IsAtEnd => Position >= _expression.Length;

        public decimal ParseExpression()
        {
            var value = ParseTerm();
            while (true)
            {
                SkipWhiteSpace();
                if (Match('+'))
                {
                    value += ParseTerm();
                }
                else if (Match('-'))
                {
                    value -= ParseTerm();
                }
                else
                {
                    return value;
                }
            }
        }

        private decimal ParseTerm()
        {
            var value = ParseFactor();
            while (true)
            {
                SkipWhiteSpace();
                if (Match('*'))
                {
                    value *= ParseFactor();
                }
                else if (Match('/'))
                {
                    var divisor = ParseFactor();
                    if (divisor == 0m)
                    {
                        throw new InvalidOperationException("Formula attempted to divide by zero.");
                    }

                    value /= divisor;
                }
                else
                {
                    return value;
                }
            }
        }

        private decimal ParseFactor()
        {
            SkipWhiteSpace();
            if (Match('+'))
            {
                return ParseFactor();
            }

            if (Match('-'))
            {
                return -ParseFactor();
            }

            if (Match('('))
            {
                var value = ParseExpression();
                SkipWhiteSpace();
                if (!Match(')'))
                {
                    throw new InvalidOperationException("Formula is missing a closing parenthesis.");
                }

                return value;
            }

            return ParseNumber();
        }

        private decimal ParseNumber()
        {
            SkipWhiteSpace();
            var start = Position;
            while (!IsAtEnd && (char.IsDigit(_expression[Position]) || _expression[Position] == '.'))
            {
                Position++;
            }

            if (start == Position)
            {
                throw new InvalidOperationException($"Expected a number near position {Position + 1}.");
            }

            var slice = _expression[start..Position];
            if (!decimal.TryParse(slice, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            {
                throw new InvalidOperationException($"Invalid number '{slice}'.");
            }

            return value;
        }

        public void SkipWhiteSpace()
        {
            while (!IsAtEnd && char.IsWhiteSpace(_expression[Position]))
            {
                Position++;
            }
        }

        private bool Match(char value)
        {
            if (IsAtEnd || _expression[Position] != value)
            {
                return false;
            }

            Position++;
            return true;
        }
    }
}

public sealed record ForecastEventInput(
    int MonthOffset,
    string Name,
    decimal RevenueImpact,
    decimal ExpenseImpact,
    decimal CashImpact,
    bool IsRecurring);

public sealed record ForecastProjectionRow(
    string MonthKey,
    decimal Revenue,
    decimal GrossProfit,
    decimal OperatingExpense,
    decimal NetIncome,
    decimal CashInflow,
    decimal CashOutflow,
    decimal NetCashFlow,
    decimal EndingCash,
    decimal AccountsReceivable,
    decimal AccountsPayable,
    decimal Equity,
    bool CashThresholdBreached);

public static class ForecastingMath
{
    public static IReadOnlyList<ForecastProjectionRow> BuildThreeWayForecast(
        DateOnly startMonth,
        int horizonMonths,
        decimal monthlyRevenue,
        decimal monthlyOperatingExpense,
        decimal revenueGrowthPercent,
        decimal grossMarginPercent,
        decimal opexGrowthPercent,
        decimal cashConversionPercent,
        decimal startingCash,
        decimal cashThreshold,
        IEnumerable<ForecastEventInput> events)
    {
        var rows = new List<ForecastProjectionRow>();
        var boundedHorizon = Math.Clamp(horizonMonths, 1, 36);
        var safeRevenue = Math.Max(0m, monthlyRevenue);
        var safeExpense = Math.Max(0m, monthlyOperatingExpense);
        var cash = startingCash;
        var equity = startingCash;
        var eventList = events.ToList();

        for (var month = 1; month <= boundedHorizon; month++)
        {
            var date = startMonth.AddMonths(month - 1);
            var revenueFactor = CompoundGrowthFactor(revenueGrowthPercent, month);
            var expenseFactor = CompoundGrowthFactor(opexGrowthPercent, month);
            var activeEvents = eventList.Where(x => x.IsRecurring ? x.MonthOffset <= month : x.MonthOffset == month).ToList();

            var revenue = safeRevenue * revenueFactor + activeEvents.Sum(x => x.RevenueImpact);
            revenue = Math.Max(0m, revenue);
            var grossProfit = revenue * grossMarginPercent / 100m;
            var operatingExpense = safeExpense * expenseFactor + activeEvents.Sum(x => x.ExpenseImpact);
            operatingExpense = Math.Max(0m, operatingExpense);
            var netIncome = grossProfit - operatingExpense;
            var directCashImpact = activeEvents.Sum(x => x.CashImpact);
            var netCashFlow = netIncome * cashConversionPercent / 100m + directCashImpact;
            cash += netCashFlow;
            equity += netIncome;

            rows.Add(new ForecastProjectionRow(
                $"{date.Year:D4}-{date.Month:D2}",
                decimal.Round(revenue, 2),
                decimal.Round(grossProfit, 2),
                decimal.Round(operatingExpense, 2),
                decimal.Round(netIncome, 2),
                decimal.Round(Math.Max(0m, revenue + directCashImpact), 2),
                decimal.Round(Math.Max(0m, operatingExpense - Math.Min(0m, directCashImpact)), 2),
                decimal.Round(netCashFlow, 2),
                decimal.Round(cash, 2),
                decimal.Round(revenue * 0.16m, 2),
                decimal.Round(operatingExpense * 0.14m, 2),
                decimal.Round(equity, 2),
                cashThreshold > 0m && cash < cashThreshold));
        }

        return rows;
    }

    private static decimal CompoundGrowthFactor(decimal annualGrowthPercent, int monthNumber)
    {
        var factor = Math.Pow(1d + (double)(annualGrowthPercent / 100m), monthNumber / 12d);
        return (decimal)factor;
    }
}

public sealed class FinancialEngine(AppDbContext db)
{
    public async Task<FinancialRollup> BuildRollupAsync(Guid organizationId, Guid reportingPeriodId, CancellationToken cancellationToken)
    {
        var accounts = await db.GlAccounts
            .AsNoTracking()
            .Include(x => x.Transactions)
            .Where(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId)
            .ToListAsync(cancellationToken);

        var eliminations = await db.EliminationEntries
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId)
            .ToListAsync(cancellationToken);

        var entityTotal = accounts.Sum(AccountSignedBalance);
        var consolidationOnlyAdjustments = eliminations.Sum(x => x.Amount);
        return new FinancialRollup(
            entityTotal,
            entityTotal - consolidationOnlyAdjustments,
            accounts.Count(x => x.IsFirstSeen),
            accounts.Count(x => x.ReviewStatus != MappingReviewStatus.Reviewed),
            eliminations.Count,
            accounts.GroupBy(x => x.FsLine)
                .OrderBy(x => x.Key)
                .Select(x => new FinancialLineRollup(x.Key, x.Sum(AccountSignedBalance)))
                .ToList());
    }

    public static decimal AccountSignedBalance(GlAccount account)
    {
        var amount = account.Transactions.Count == 0
            ? ReadMonthlyBalances(account.MonthlyBalancesJson).Sum()
            : account.Transactions.Sum(x => x.Credit - x.Debit);

        return account.ConsolidationTreatment == ConsolidationTreatment.Exclude ? 0m : amount;
    }

    public static decimal[] ReadMonthlyBalances(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<decimal[]>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}

public sealed record FinancialRollup(
    decimal EntityTotal,
    decimal ConsolidatedTotal,
    int FirstSeenAccountCount,
    int UnreviewedAccountCount,
    int EliminationCount,
    IReadOnlyList<FinancialLineRollup> Lines);

public sealed record FinancialLineRollup(string FsLine, decimal Amount);

public sealed class MappingService
{
    public bool IsFirstSeenAccount(IEnumerable<GlAccount> priorAccounts, string tenantId, string accountCode)
        => !priorAccounts.Any(x => string.Equals(x.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)
                                   && string.Equals(x.Code, accountCode, StringComparison.OrdinalIgnoreCase));

    public decimal SuggestConfidence(string accountName, string fsLine)
    {
        if (string.IsNullOrWhiteSpace(accountName) || string.IsNullOrWhiteSpace(fsLine))
        {
            return 0.35m;
        }

        var accountWords = Tokenize(accountName);
        var fsWords = Tokenize(fsLine);
        var overlap = accountWords.Intersect(fsWords, StringComparer.OrdinalIgnoreCase).Count();
        return Math.Clamp(0.45m + (overlap * 0.17m), 0.45m, 0.98m);
    }

    private static string[] Tokenize(string value)
        => value.Split([' ', '/', '-', '—', '&'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed record FixOperation(string Op, string TargetType, Guid TargetId, JsonElement? Value, string? Reason);

public sealed class FixOperationValidator
{
    private static readonly HashSet<string> AllowedOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "append_narrative",
        "replace_text",
        "add_callout",
        "update_chart",
        "update_kpi",
        "map_account",
        "eliminate_account",
        "exclude_account",
        "create_intercompany_elimination",
        "resolve_issue",
        "ignore_issue"
    };

    private static readonly HashSet<string> AllowedTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "slide",
        "block",
        "chart",
        "kpi",
        "account",
        "elimination",
        "issue"
    };

    public IReadOnlyList<string> Validate(IEnumerable<FixOperation> operations)
    {
        var errors = new List<string>();
        foreach (var operation in operations)
        {
            if (!AllowedOps.Contains(operation.Op))
            {
                errors.Add($"Operation '{operation.Op}' is not allowed.");
            }

            if (!AllowedTargets.Contains(operation.TargetType))
            {
                errors.Add($"Target type '{operation.TargetType}' is not allowed.");
            }

            if (operation.TargetId == Guid.Empty)
            {
                errors.Add("TargetId is required.");
            }
        }

        return errors;
    }
}

public sealed record CodexExecutionRequest(
    string Prompt,
    string Model,
    string ReasoningEffort,
    string OutputPath,
    string WorkingDirectory,
    bool Json = true);

public sealed class CodexCommandBuilder(IConfiguration configuration)
{
    public string CodexPath => configuration["Ai:CodexPath"]
                               ?? Environment.GetEnvironmentVariable("CODEX_CLI_PATH")
                               ?? "codex";

    public IReadOnlyList<string> BuildArguments(CodexExecutionRequest request)
    {
        var args = new List<string>
        {
            "exec",
            "--skip-git-repo-check",
            "--ephemeral",
            "--sandbox",
            "read-only",
            "-c",
            "approval_policy=never",
            "-c",
            $"model_reasoning_effort={request.ReasoningEffort}",
            "-m",
            request.Model,
            "--output-last-message",
            request.OutputPath,
            "--color",
            "never"
        };

        if (request.Json)
        {
            args.Add("--json");
        }

        // Pipe the prompt through stdin instead of argv. Package snapshots can be
        // hundreds of KB, and command-line length limits vary by host.
        args.Add("-");
        return args;
    }
}

public sealed record CodexModelInfo(string Id, string DisplayName, string[] ReasoningEfforts, bool IsDefault);

public sealed class CodexModelDiscovery(IConfiguration configuration)
{
    public async Task<IReadOnlyList<CodexModelInfo>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cachePath = configuration["Ai:ModelsCachePath"] ?? Path.Combine(home, ".codex", "models_cache.json");
        var defaultModel = await ReadDefaultModelAsync(home, cancellationToken);
        var models = new Dictionary<string, CodexModelInfo>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(cachePath))
        {
            try
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(cachePath, cancellationToken));
                WalkModels(document.RootElement, models, defaultModel);
            }
            catch
            {
                // Fall back below; a corrupt local cache should not break the app.
            }
        }

        if (models.Count == 0)
        {
            foreach (var id in new[] { defaultModel, "gpt-5.5", "gpt-5.4", "gpt-5.4-mini", "gpt-5.3-codex", "gpt-5.3-codex-spark", "gpt-5.2" }
                         .Where(x => !string.IsNullOrWhiteSpace(x))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                models[id] = new CodexModelInfo(id, id, ["low", "medium", "high", "xhigh"], string.Equals(id, defaultModel, StringComparison.OrdinalIgnoreCase));
            }
        }

        return models.Values
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.DisplayName)
            .ToArray();
    }

    private static async Task<string> ReadDefaultModelAsync(string home, CancellationToken cancellationToken)
    {
        var configPath = Path.Combine(home, ".codex", "config.toml");
        if (!File.Exists(configPath))
        {
            return "gpt-5.5";
        }

        var lines = await File.ReadAllLinesAsync(configPath, cancellationToken);
        var modelLine = lines.FirstOrDefault(x => x.TrimStart().StartsWith("model ", StringComparison.OrdinalIgnoreCase));
        if (modelLine is null)
        {
            return "gpt-5.5";
        }

        var parts = modelLine.Split('=', 2);
        return parts.Length == 2 ? parts[1].Trim().Trim('"', '\'') : "gpt-5.5";
    }

    private static void WalkModels(JsonElement element, IDictionary<string, CodexModelInfo> models, string defaultModel)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            string? id = null;
            string? name = null;
            var efforts = new List<string>();

            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("id") || property.NameEquals("slug") || property.NameEquals("model"))
                {
                    id = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : id;
                }
                else if (property.NameEquals("name") || property.NameEquals("display_name") || property.NameEquals("displayName"))
                {
                    name = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : name;
                }
                else if (property.Name.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
                         && property.Value.ValueKind == JsonValueKind.Array)
                {
                    efforts.AddRange(property.Value.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()!)
                        .Where(x => !string.IsNullOrWhiteSpace(x)));
                }

                WalkModels(property.Value, models, defaultModel);
            }

            if (!string.IsNullOrWhiteSpace(id) && id.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase))
            {
                var uniqueEfforts = efforts.Count == 0 ? ["low", "medium", "high", "xhigh"] : efforts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                models[id] = new CodexModelInfo(id, name ?? id, uniqueEfforts, string.Equals(id, defaultModel, StringComparison.OrdinalIgnoreCase));
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                WalkModels(item, models, defaultModel);
            }
        }
    }
}

public sealed class PackageSnapshotBuilder(AppDbContext db)
{
    public async Task<string> BuildPackageSnapshotAsync(Guid packageId, CancellationToken cancellationToken)
    {
        var package = await db.ReportPackages
            .AsNoTracking()
            .Include(x => x.Organization)
            .Include(x => x.ReportingPeriod)
            .Include(x => x.Slides.OrderBy(s => s.SortOrder))
                .ThenInclude(x => x.Blocks.OrderBy(b => b.SortOrder))
            .Include(x => x.Issues)
            .FirstAsync(x => x.Id == packageId, cancellationToken);

        var kpis = await db.KpiDefinitions
            .AsNoTracking()
            .Where(x => x.OrganizationId == package.OrganizationId)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

        return JsonSerializer.Serialize(new
        {
            package = new
            {
                package.Id,
                package.OrganizationId,
                package.ReportingPeriodId,
                package.Status,
                package.VersionLabel,
                package.BaseFrom,
                package.ThemeJson,
                package.LastXeroSyncAt
            },
            slides = package.Slides.OrderBy(x => x.SortOrder).Select(s => new
            {
                s.Id,
                s.SortOrder,
                s.Subject,
                s.KpiLabel,
                s.CurrentValue,
                s.PriorValue,
                s.VarianceAmount,
                s.VariancePercent,
                s.AccountCodesCsv,
                s.MonthlyJson,
                s.PriorMonthlyJson,
                s.ChartConfigJson,
                blocks = s.Blocks.OrderBy(b => b.SortOrder).Select(b => new { b.Id, b.SortOrder, b.Kind, b.ContentJson })
            }),
            issues = package.Issues.OrderByDescending(x => x.CreatedAt).Select(i => new
            {
                i.Id,
                i.PackageSlideId,
                i.Severity,
                i.Status,
                i.Category,
                i.Title,
                i.Description,
                i.EvidenceJson,
                i.RecommendedFixJson,
                i.Confidence,
                i.UserComment
            }),
            kpis
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    public async Task<string> BuildFinalReviewSnapshotAsync(Guid packageId, CancellationToken cancellationToken)
    {
        var package = await db.ReportPackages
            .AsNoTracking()
            .Include(x => x.Organization)
            .Include(x => x.ReportingPeriod)
            .Include(x => x.Slides.OrderBy(s => s.SortOrder))
                .ThenInclude(x => x.Blocks.OrderBy(b => b.SortOrder))
            .Include(x => x.Issues)
            .FirstAsync(x => x.Id == packageId, cancellationToken);

        var accounts = await db.GlAccounts
            .AsNoTracking()
            .Where(x => x.OrganizationId == package.OrganizationId && x.ReportingPeriodId == package.ReportingPeriodId)
            .Select(x => new
            {
                x.Id,
                x.Code,
                x.Name,
                x.Type,
                x.FsLine,
                x.AiSuggestedFsLine,
                x.MappingConfidence,
                x.IsFirstSeen,
                ReviewStatus = x.ReviewStatus.ToString(),
                ConsolidationTreatment = x.ConsolidationTreatment.ToString(),
                x.MonthlyBalancesJson
            })
            .ToListAsync(cancellationToken);

        var eliminations = await db.EliminationEntries
            .AsNoTracking()
            .Where(x => x.OrganizationId == package.OrganizationId && x.ReportingPeriodId == package.ReportingPeriodId)
            .ToListAsync(cancellationToken);

        var statementLines = await db.FinancialStatementLines
            .AsNoTracking()
            .Where(x => x.ReportPackageId == packageId)
            .OrderBy(x => x.StatementType)
            .ThenBy(x => x.SortOrder)
            .Select(x => new
            {
                x.StatementType,
                x.Section,
                x.LineName,
                x.AccountCode,
                x.CurrentAmount,
                x.PriorAmount,
                x.AmountsJson
            })
            .ToListAsync(cancellationToken);

        var statementQa = (await db.StatementQaResults
            .AsNoTracking()
            .Where(x => x.ReportPackageId == packageId)
            .Select(x => new { x.Status, x.SummaryJson, x.CreatedAt })
            .ToListAsync(cancellationToken))
            .OrderByDescending(x => x.CreatedAt)
            .ToList();

        var payload = new
        {
            package = new
            {
                package.Id,
                organization = package.Organization?.Name,
                period = package.ReportingPeriod?.Label,
                status = package.Status.ToString(),
                package.VersionLabel
            },
            slides = package.Slides.Select(s => new
            {
                s.Id,
                s.SortOrder,
                s.Subject,
                s.KpiLabel,
                s.CurrentValue,
                s.PriorValue,
                s.VarianceAmount,
                s.VariancePercent,
                s.AccountCodesCsv,
                s.MonthlyJson,
                s.PriorMonthlyJson,
                s.ChartConfigJson,
                blocks = s.Blocks.Select(b => new { b.Id, b.SortOrder, b.Kind, b.ContentJson })
            }),
            issues = package.Issues.Select(i => new
            {
                i.Id,
                severity = i.Severity.ToString(),
                status = i.Status.ToString(),
                i.Category,
                i.Title,
                i.Description,
                i.EvidenceJson
            }),
            accounts,
            statementLines,
            statementQa,
            eliminations,
            qaRules = new[]
            {
                "Layer 1: data extraction must tie to source financials",
                "Layer 2: GL to financial statement reconciliation",
                "Layer 3: analytical and narrative review",
                "Rule 4: explain business drivers",
                "Rule 5: board-friendly language",
                "Rule 6: period-appropriate comparisons"
            },
            allowedOperations = new[]
            {
                "append_narrative",
                "replace_text",
                "add_callout",
                "update_chart",
                "update_kpi",
                "map_account",
                "eliminate_account",
                "exclude_account",
                "create_intercompany_elimination"
            }
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }
}

public sealed class CodexWorker(
    IServiceScopeFactory scopeFactory,
    CodexCommandBuilder commandBuilder,
    IConfiguration configuration,
    IHubContext<AiHub> hub,
    ILogger<CodexWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // P3.35 — startup recovery: any AiRun left in Running by a previous crash would
        // never be re-attempted because ProcessNextAsync only picks up Queued rows. Cat 37.
        await RecoverOrphanedRunsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Codex worker loop failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task RecoverOrphanedRunsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var orphans = await db.AiRuns
                .Where(x => x.Status == AiRunStatus.Running)
                .ToListAsync(cancellationToken);
            foreach (var run in orphans)
            {
                run.Status = AiRunStatus.Queued;
                run.Logs += "\nReset from Running to Queued during worker startup recovery.";
            }
            if (orphans.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
                logger.LogWarning("Codex worker recovered {Count} orphaned Running run(s) on startup.", orphans.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Codex worker startup recovery failed.");
        }
    }

    private async Task ProcessNextAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queuedRuns = await db.AiRuns
            .Where(x => x.Status == AiRunStatus.Queued)
            .ToListAsync(cancellationToken);
        var run = queuedRuns.OrderBy(x => x.CreatedAt).FirstOrDefault();

        if (run is null)
        {
            return;
        }

        run.Status = AiRunStatus.Running;
        run.StartedAt = DateTimeOffset.UtcNow;
        await UpdateRunProgressAsync(db, run, 12, "Preparing AI request.", cancellationToken);

        try
        {
            var useMock = configuration.GetValue("Ai:UseMockRunner", true);
            await UpdateRunProgressAsync(
                db,
                run,
                35,
                useMock ? "Running mock AI review." : "Running Codex CLI analysis.",
                cancellationToken);

            var output = useMock
                ? await MockCodexOutputAsync(run, cancellationToken)
                : await RunCodexAsync(run, cancellationToken);

            await UpdateRunProgressAsync(db, run, 70, "Checking AI response.", cancellationToken);

            if (!TryValidateAiJson(output, run.Module, run.InputJson, out var validationError) && !useMock)
            {
                run.Logs += $"\nCodex returned invalid JSON ({validationError}); retrying once.";
                await UpdateRunProgressAsync(db, run, 78, "Retrying AI response.", cancellationToken);
                output = await RunCodexAsync(run, cancellationToken, "The prior response was invalid. Return only strict JSON matching the requested schema.");
                await UpdateRunProgressAsync(db, run, 82, "Checking retry response.", cancellationToken);
            }

                if (!TryValidateAiJson(output, run.Module, run.InputJson, out validationError))
            {
                throw new InvalidOperationException($"AI output did not match the required JSON contract: {validationError}");
            }

            run.OutputJson = output;
            await UpdateRunProgressAsync(db, run, 85, "Applying AI review results.", cancellationToken);

            run.Progress = 100;
            run.Status = run.CancellationRequested ? AiRunStatus.Cancelled : AiRunStatus.Completed;
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.Logs += "\nCompleted.";

            await CreateIssuesFromFinalReviewAsync(db, run, cancellationToken);
        }
        catch (Exception ex)
        {
            run.Status = AiRunStatus.Failed;
            run.Logs += $"\n{ex.Message}";
            run.CompletedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        // P3.35 — scope SignalR sends to subscribers of THIS run, not every connected client.
        // Cat 34. Clients call AiHub.SubscribeToRun(runId) when they open a run's UI.
        await hub.Clients.Group(AiHub.GroupName(run.Id)).SendAsync("aiRunUpdated", AiRunDto.From(run), cancellationToken);
    }

    private async Task UpdateRunProgressAsync(AppDbContext db, AiRun run, int progress, string message, CancellationToken cancellationToken)
    {
        run.Progress = Math.Max(run.Progress, progress);
        run.Logs += $"\n{message}";
        await db.SaveChangesAsync(cancellationToken);
        await hub.Clients.Group(AiHub.GroupName(run.Id)).SendAsync("aiRunUpdated", AiRunDto.From(run), cancellationToken);
    }

    private static async Task<string> MockCodexOutputAsync(AiRun run, CancellationToken cancellationToken)
    {
        await Task.Delay(700, cancellationToken);
        if (string.Equals(run.Module, "flux-explain", StringComparison.OrdinalIgnoreCase))
        {
            return MockFluxExplanation(run.InputJson);
        }

        var targetAccountId = FindFirstSeenAccountId(run.InputJson);
        return JsonSerializer.Serialize(new
        {
            summary = "Mock Codex review completed. Set Ai:UseMockRunner=false on the controlled server to execute the logged-in Codex CLI.",
            issues = new[]
            {
                new
                {
                    severity = "Medium",
                    category = "Mapping",
                    title = "First-seen account needs review",
                    description = "The new ePrescribe gross profit account is mapped with high confidence but has not been marked reviewed.",
                    confidence = 0.86,
                    evidence = new { accountCode = "3415110", treatment = "Include" },
                    operations = new[]
                    {
                        new { op = "map_account", targetType = "account", targetId = targetAccountId, value = new { fsLine = "Revenue — ePrescribe" }, reason = "Approve AI mapping for first-seen account." }
                    }
                }
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
    }

    private async Task<string> RunCodexAsync(AiRun run, CancellationToken cancellationToken, string? retryInstruction = null)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "financial-reporting-codex");
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, $"{run.Id}-last-message.json");
        var prompt = BuildPrompt(run.InputJson, run.Module, run.PromptProfile, retryInstruction);
        var request = new CodexExecutionRequest(
            prompt,
            run.Model,
            run.ReasoningEffort,
            outputPath,
            AppContext.BaseDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = commandBuilder.CodexPath,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in commandBuilder.BuildArguments(request))
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Codex CLI.");
        await process.StandardInput.WriteAsync(prompt);
        await process.StandardInput.FlushAsync(cancellationToken);
        process.StandardInput.Close();

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(configuration.GetValue("Ai:TimeoutMinutes", 8)));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            // P3.35 — kill the live process tree on cancel/timeout. Without this the Codex
            // subprocess lingers as an orphan after the user clicks Cancel or the configured
            // timeout fires, leaking memory and CPU. Cat 37.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception killEx)
            {
                logger.LogWarning(killEx, "Failed to kill Codex CLI subprocess for run {RunId}.", run.Id);
            }
            throw;
        }

        run.Logs += Redact(stdout.ToString());
        if (stderr.Length > 0)
        {
            run.Logs += "\nSTDERR:\n" + Redact(stderr.ToString());
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Codex CLI exited with code {process.ExitCode}.");
        }

        return File.Exists(outputPath)
            ? await File.ReadAllTextAsync(outputPath, cancellationToken)
            : stdout.ToString();
    }

    private static string BuildPrompt(string snapshotJson, string module, string promptProfile, string? retryInstruction)
    {
        // P2.23 — flux-explain now demands ranked hypotheses with per-line citations.
        // P1.18 — narrative-rewrite has its own dedicated prose contract; previously fell
        // through to the QA issues schema and could not produce prose. Cat 13, 21.
        string contract;
        if (string.Equals(module, "flux-explain", StringComparison.OrdinalIgnoreCase))
        {
            contract = """
              Return strict JSON with this schema:
                summary: string (one-paragraph executive summary)
                suggestedExplanation: string (ready-to-paste flux narrative)
                confidence: number in [0, 1] (overall confidence in the narrative)
                hypotheses: array, ranked most-likely first, each:
                    { rank: int starting at 1, label: string, confidence: number in [0, 1],
                      journalLineIds: array of string ids copied verbatim from drilldown }
                evidence: array of { journalLineId: string, accountCode?: string, amount?: number, note?: string }
                  EVERY evidence element MUST include journalLineId (machine-parseable citation).
                operations: array using only allowedOperations from the snapshot, including
                  exactly one set_flux_explanation operation with the chosen narrative.
              Use the supplied vendorContext, cadenceLabel, and sourceTypeBreakdown directly;
              do not invent vendor classifications or cadence labels.
              """;
        }
        else if (string.Equals(module, "narrative-rewrite", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(module, "slide-chat", StringComparison.OrdinalIgnoreCase))
        {
            contract = """
              Return strict JSON with this schema:
                narrative: string (ACTIVE voice, ≤3 sentences per driver, decision-oriented)
                drivers: array of { label: string, amountChange: number, percentChange: number,
                                    direction: "up"|"down", citation: string }
                decisionsRequired: array of strings (one short imperative each)
                tone: string echoing the requested promptProfile
              Cite specific dollar amounts. Avoid filler ("it should be noted", "we observed").
              Reference only data present in the snapshot.
              """;
        }
        else
        {
            contract = """
              Return strict JSON with: summary, issues[]. Each issue must include severity, category, title, description, evidence, confidence, operations[].
              Operations must be selected only from allowedOperations in the snapshot.
              """;
        }

        return """
           You are a server-side Codex CLI worker for a financial reporting application.
           Use only the supplied JSON snapshot. Do not ask for database credentials, Xero tokens, shell access, or private auth files.
           """ + "\n" + contract + "\n" + """
           Module:
           """ + "\n" + module + "\n\nPrompt profile:\n" + promptProfile + "\n\n" +
           (retryInstruction is null ? "" : "Retry instruction:\n" + retryInstruction + "\n\n") + """

           Snapshot:
           """ + "\n" + snapshotJson;
    }

    private static bool TryValidateAiJson(string output, string module, string inputJson, out string error)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "root must be an object";
                return false;
            }

            if (!document.RootElement.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.String)
            {
                error = "summary string is required";
                return false;
            }

            if (string.Equals(module, "flux-explain", StringComparison.OrdinalIgnoreCase))
            {
                var allowedJournalLineIds = ExtractJournalLineIds(inputJson);
                if (allowedJournalLineIds.Count == 0)
                {
                    error = "snapshot must include at least one journalLineId";
                    return false;
                }

                if (!document.RootElement.TryGetProperty("suggestedExplanation", out var explanation) || explanation.ValueKind != JsonValueKind.String)
                {
                    error = "suggestedExplanation string is required";
                    return false;
                }

                if (!document.RootElement.TryGetProperty("confidence", out var confidence) || confidence.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
                {
                    error = "confidence is required";
                    return false;
                }

                if (!document.RootElement.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array)
                {
                    error = "operations array is required";
                    return false;
                }

                // P2.23 — enforce the ranked hypotheses array. Cat 13.
                if (!document.RootElement.TryGetProperty("hypotheses", out var hypotheses) || hypotheses.ValueKind != JsonValueKind.Array)
                {
                    error = "hypotheses array is required (ranked by likelihood)";
                    return false;
                }
                foreach (var h in hypotheses.EnumerateArray())
                {
                    if (!h.TryGetProperty("rank", out _) || !h.TryGetProperty("label", out _) || !h.TryGetProperty("confidence", out _))
                    {
                        error = "each hypothesis must have rank, label, confidence";
                        return false;
                    }
                    if (!h.TryGetProperty("journalLineIds", out var hypothesisLineIds)
                        || hypothesisLineIds.ValueKind != JsonValueKind.Array
                        || !hypothesisLineIds.EnumerateArray().Any(lineId => lineId.ValueKind == JsonValueKind.String && allowedJournalLineIds.Contains(lineId.GetString() ?? "")))
                    {
                        error = "each hypothesis must cite at least one journalLineId from the supplied snapshot";
                        return false;
                    }
                }

                // P2.23 — every evidence[] element must carry a machine-parseable
                // journalLineId so the AI's citations can be verified back to source rows.
                if (!document.RootElement.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.Array || evidence.GetArrayLength() == 0)
                {
                    error = "non-empty evidence array is required";
                    return false;
                }
                foreach (var e in evidence.EnumerateArray())
                {
                    if (!e.TryGetProperty("journalLineId", out var lineId) || lineId.ValueKind != JsonValueKind.String || !allowedJournalLineIds.Contains(lineId.GetString() ?? ""))
                    {
                        error = "every evidence[] element must include a journalLineId from the supplied snapshot";
                        return false;
                    }
                }

                if (!operations.EnumerateArray().Any(operation =>
                        operation.ValueKind == JsonValueKind.Object
                        && operation.TryGetProperty("op", out var op)
                        && op.ValueKind == JsonValueKind.String
                        && string.Equals(op.GetString(), "set_flux_explanation", StringComparison.Ordinal)
                        && operation.TryGetProperty("targetId", out var targetId)
                        && targetId.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(targetId.GetString())))
                {
                    error = "operations must include set_flux_explanation with a targetId";
                    return false;
                }

                error = "";
                return true;
            }

            if (string.Equals(module, "narrative-rewrite", StringComparison.OrdinalIgnoreCase)
                || string.Equals(module, "slide-chat", StringComparison.OrdinalIgnoreCase))
            {
                // P1.18 — narrative-rewrite returns prose, not the QA-issues schema.
                if (!document.RootElement.TryGetProperty("narrative", out var narrative) || narrative.ValueKind != JsonValueKind.String)
                {
                    error = "narrative string is required";
                    return false;
                }
                if (!document.RootElement.TryGetProperty("drivers", out var drivers) || drivers.ValueKind != JsonValueKind.Array)
                {
                    error = "drivers array is required";
                    return false;
                }
                error = "";
                return true;
            }

            if (document.RootElement.TryGetProperty("issues", out var issues))
            {
                if (issues.ValueKind != JsonValueKind.Array)
                {
                    error = "issues must be an array";
                    return false;
                }

                foreach (var issue in issues.EnumerateArray())
                {
                    foreach (var required in new[] { "severity", "category", "title", "description", "confidence", "operations" })
                    {
                        if (!issue.TryGetProperty(required, out _))
                        {
                            error = $"issue missing {required}";
                            return false;
                        }
                    }
                }
            }

            error = "";
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static HashSet<string> ExtractJournalLineIds(string inputJson)
    {
        try
        {
            using var document = JsonDocument.Parse(inputJson);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            CollectJournalLineIds(document.RootElement, ids);
            return ids;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void CollectJournalLineIds(JsonElement element, HashSet<string> ids)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "journalLineId", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                    {
                        ids.Add(property.Value.GetString()!);
                    }
                    CollectJournalLineIds(property.Value, ids);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectJournalLineIds(item, ids);
                }
                break;
        }
    }

    private static string MockFluxExplanation(string snapshotJson)
    {
        try
        {
            var root = JsonNode.Parse(snapshotJson);
            var group = root?["group"];
            var drilldown = root?["drilldown"];
            var groupName = group?["groupName"]?.GetValue<string>() ?? "Selected line";
            var currentPeriod = group?["currentPeriodKey"]?.GetValue<string>() ?? "current period";
            var priorPeriod = group?["priorPeriodKey"]?.GetValue<string>() ?? "prior period";
            var variance = group?["varianceAmount"]?.GetValue<decimal>() ?? 0m;
            var variancePercent = group?["variancePercent"]?.GetValue<decimal>() ?? 0m;
            var accounts = drilldown?["accounts"]?.AsArray() ?? [];
            var topAccount = accounts
                .Select(item => new
                {
                    name = item?["accountName"]?.GetValue<string>() ?? item?["accountCode"]?.GetValue<string>() ?? "account",
                    amount = Math.Abs(item?["varianceAmount"]?.GetValue<decimal>() ?? 0m)
                })
                .OrderByDescending(x => x.amount)
                .FirstOrDefault();
            var driver = topAccount is null ? "the account activity supplied in the drilldown" : $"{topAccount.name} ({topAccount.amount:0.00} absolute change)";
            var explanation = $"{groupName} changed by {variance:0.00} ({variancePercent:0.0}%) from {priorPeriod} to {currentPeriod}, driven primarily by {driver}. Review the current and prior-period journal detail before approval.";

            // Pull a journalLineId from the drilldown so the mock satisfies the new
            // citation-required schema and represents what a real Codex call would do.
            var firstLineId = ExtractJournalLineIds(snapshotJson).FirstOrDefault() ?? "";

            return JsonSerializer.Serialize(new
            {
                summary = $"Drafted flux explanation for {groupName}.",
                suggestedExplanation = explanation,
                confidence = 0.72m,
                hypotheses = new[]
                {
                    new { rank = 1, label = $"Driver: {driver}", confidence = 0.72m, journalLineIds = new[] { firstLineId } },
                    new { rank = 2, label = "Timing / accrual", confidence = 0.18m, journalLineIds = new[] { firstLineId } }
                },
                evidence = new[] { new { journalLineId = firstLineId, accountCode = topAccount?.name ?? "", note = driver } },
                operations = new[] { new { op = "set_flux_explanation", targetType = "fluxReviewGroup", targetId = group?["id"]?.GetValue<string>(), value = new { explanation }, reason = "AI variance explanation draft" } }
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return JsonSerializer.Serialize(new
            {
                summary = "Drafted flux explanation.",
                suggestedExplanation = "The selected line changed materially between periods. Review the account-level and journal detail before approving the final explanation.",
                confidence = 0.55m,
                hypotheses = new[] { new { rank = 1, label = "Insufficient context", confidence = 0.40m, journalLineIds = Array.Empty<string>() } },
                evidence = Array.Empty<object>(),
                operations = Array.Empty<object>()
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
    }

    private static Guid FindFirstSeenAccountId(string snapshotJson)
    {
        try
        {
            var root = JsonNode.Parse(snapshotJson);
            var accounts = root?["accounts"]?.AsArray();
            var first = accounts?.FirstOrDefault(x => x?["isFirstSeen"]?.GetValue<bool>() == true)
                        ?? accounts?.FirstOrDefault();
            return Guid.TryParse(first?["id"]?.GetValue<string>(), out var id) ? id : Guid.Empty;
        }
        catch
        {
            return Guid.Empty;
        }
    }

    private static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var redacted = value;
        foreach (var marker in new[] { "access_token", "refresh_token", "client_secret", "connection string", ".codex/auth.json" })
        {
            redacted = redacted.Replace(marker, "[redacted]", StringComparison.OrdinalIgnoreCase);
        }

        return redacted;
    }

    private static async Task CreateIssuesFromFinalReviewAsync(AppDbContext db, AiRun run, CancellationToken cancellationToken)
    {
        if (!string.Equals(run.Module, "final-review", StringComparison.OrdinalIgnoreCase) || run.ReportPackageId is null)
        {
            return;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(run.OutputJson);
        }
        catch
        {
            return;
        }

        var issues = root?["issues"]?.AsArray();
        if (issues is null)
        {
            return;
        }

        foreach (var item in issues)
        {
            if (item is null)
            {
                continue;
            }

            var severity = Enum.TryParse<IssueSeverity>(item["severity"]?.GetValue<string>(), true, out var parsed)
                ? parsed
                : IssueSeverity.Medium;
            db.PackageIssues.Add(new PackageIssue
            {
                Id = Guid.NewGuid(),
                ReportPackageId = run.ReportPackageId.Value,
                Severity = severity,
                Category = item["category"]?.GetValue<string>() ?? "AI Review",
                Title = item["title"]?.GetValue<string>() ?? "AI review issue",
                Description = item["description"]?.GetValue<string>() ?? "",
                EvidenceJson = item["evidence"]?.ToJsonString() ?? "{}",
                RecommendedFixJson = JsonSerializer.Serialize(new { operations = item["operations"] }),
                Confidence = item["confidence"]?.GetValue<decimal>() ?? 0.75m
            });
        }

        await db.PackageVersions.AddAsync(new PackageVersion
        {
            Id = Guid.NewGuid(),
            ReportPackageId = run.ReportPackageId.Value,
            VersionLabel = $"AI Review {DateTimeOffset.UtcNow:yyyyMMdd-HHmm}",
            CreatedBy = "Codex CLI",
            ChangeSummary = "Final AI review pass completed",
            SnapshotJson = run.OutputJson
        }, cancellationToken);
    }
}

public sealed record AiRunDto(Guid Id, Guid? ReportPackageId, string Module, string PromptProfile, string Model, string ReasoningEffort, string Status, int Progress, string OutputJson, string Logs, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)
{
    public static AiRunDto From(AiRun run)
        => new(run.Id, run.ReportPackageId, run.Module, run.PromptProfile, run.Model, run.ReasoningEffort, run.Status.ToString(), run.Progress, run.OutputJson, run.Logs, run.CreatedAt, run.CompletedAt);
}

public sealed class ExportService(AppDbContext db, PackageSnapshotBuilder snapshotBuilder, IWebHostEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ExportArtifact> CreatePdfAsync(Guid packageId, bool includeIssues, bool includeAppendix, CancellationToken cancellationToken)
    {
        var package = await LoadPackageAsync(packageId, cancellationToken);
        var directory = EnsureExportDirectory();
        var fileName = $"{Slug(package.Organization?.Abbreviation ?? "package")}-{package.ReportingPeriod?.Key ?? "period"}-board-package.pdf";
        var path = Path.Combine(directory, fileName);
        var pageCount = await Task.Run(() => RenderPackagePdf(package, includeIssues, includeAppendix, path), cancellationToken);

        return new ExportArtifact
        {
            Id = Guid.NewGuid(),
            ReportPackageId = packageId,
            Type = "PDF",
            Status = "Completed",
            FileName = fileName,
            ContentType = "application/pdf",
            StoragePath = path,
            MetadataJson = JsonSerializer.Serialize(new { includeIssues, includeAppendix, pageCount, renderer = "QuestPDF" }, JsonOptions),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<ExportArtifact> CreateExcelAsync(Guid packageId, bool includeIssues, bool includeAppendix, CancellationToken cancellationToken)
    {
        var package = await LoadPackageAsync(packageId, cancellationToken);
        var accounts = await db.GlAccounts
            .AsNoTracking()
            .Include(x => x.Transactions)
            .Where(x => x.OrganizationId == package.OrganizationId && x.ReportingPeriodId == package.ReportingPeriodId)
            .OrderBy(x => x.Code)
            .ToListAsync(cancellationToken);
        var eliminations = await db.EliminationEntries
            .AsNoTracking()
            .Where(x => x.OrganizationId == package.OrganizationId && x.ReportingPeriodId == package.ReportingPeriodId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var statementLines = await db.FinancialStatementLines
            .AsNoTracking()
            .Where(x => x.ReportPackageId == packageId)
            .OrderBy(x => x.StatementType)
            .ThenBy(x => x.SortOrder)
            .ToListAsync(cancellationToken);
        var statementQa = (await db.StatementQaResults
            .AsNoTracking()
            .Where(x => x.ReportPackageId == packageId)
            .ToListAsync(cancellationToken))
            .OrderByDescending(x => x.CreatedAt)
            .ToList();

        var directory = EnsureExportDirectory();
        var fileName = $"{Slug(package.Organization?.Abbreviation ?? "package")}-{package.ReportingPeriod?.Key ?? "period"}-financial-package.xlsx";
        var path = Path.Combine(directory, fileName);

        var sheets = new Dictionary<string, string[][]>
        {
            ["Package"] =
            [
                ["Organization", package.Organization?.Name ?? ""],
                ["Period", package.ReportingPeriod?.Label ?? ""],
                ["Status", package.Status.ToString()],
                ["Version", package.VersionLabel]
            ],
            ["Trial Balance"] = accounts
                .Select(a => new[] { a.Code, a.Name, a.Type, a.FsLine, FinancialEngine.AccountSignedBalance(a).ToString("0.00") })
                .Prepend(["Code", "Name", "Type", "FS Line", "Amount"])
                .ToArray(),
            ["Statements"] = statementLines
                .Select(l => new[] { l.StatementType, l.Section, l.LineName, l.AccountCode, l.CurrentAmount.ToString("0.00"), l.PriorAmount.ToString("0.00"), l.AmountsJson })
                .Prepend(["Statement", "Section", "Line", "Account", "Jan 2026", "Jan 2025", "Trend Amounts"])
                .ToArray(),
            ["Mappings"] = accounts
                .Select(a => new[] { a.Code, a.Name, a.FsLine, a.AiSuggestedFsLine, a.MappingConfidence.ToString("0.00"), a.ReviewStatus.ToString(), a.IsFirstSeen ? "New" : "" })
                .Prepend(["Code", "Name", "FS Line", "AI Suggestion", "Confidence", "Review", "First Seen"])
                .ToArray(),
            ["Eliminations"] = eliminations
                .Select(e => new[] { e.Type, e.Description, e.Amount.ToString("0.00"), e.Status, e.Reason, e.IsRecurringRule ? "Recurring" : "" })
                .Prepend(["Type", "Description", "Amount", "Status", "Reason", "Rule"])
                .ToArray(),
            ["KPI Data"] = package.Slides
                .OrderBy(s => s.SortOrder)
                .Select(s => new[] { s.Subject, s.KpiLabel, s.CurrentValue.ToString("0.00"), s.PriorValue.ToString("0.00"), s.VarianceAmount.ToString("0.00"), s.VariancePercent.ToString("0.0") })
                .Prepend(["Slide", "KPI", "Current", "Prior", "Variance", "Variance %"])
                .ToArray(),
            ["QA Issues"] = package.Issues
                .OrderByDescending(i => i.CreatedAt)
                .Select(i => new[] { i.Severity.ToString(), i.Status.ToString(), i.Category, i.Title, i.Description, i.Confidence.ToString("0.00") })
                .Prepend(["Severity", "Status", "Category", "Title", "Description", "Confidence"])
                .ToArray(),
            ["Statement QA"] = statementQa
                .Select(q => new[] { q.Status, q.SummaryJson, q.CreatedAt.ToString("O") })
                .Prepend(["Status", "Summary", "Created"])
                .ToArray()
        };

        await WriteXlsxAsync(path, sheets, cancellationToken);
        return new ExportArtifact
        {
            Id = Guid.NewGuid(),
            ReportPackageId = packageId,
            Type = "Excel",
            Status = "Completed",
            FileName = fileName,
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            StoragePath = path,
            MetadataJson = JsonSerializer.Serialize(new { includeIssues, includeAppendix, sheetCount = sheets.Count, accountCount = accounts.Count }, JsonOptions),
            CompletedAt = DateTimeOffset.UtcNow
        };
    }

    public async Task<string> BuildExportQaAsync(Guid packageId, Guid exportArtifactId, CancellationToken cancellationToken)
    {
        var artifact = await db.ExportArtifacts.FirstAsync(x => x.Id == exportArtifactId, cancellationToken);
        var snapshot = await snapshotBuilder.BuildFinalReviewSnapshotAsync(packageId, cancellationToken);
        var exists = File.Exists(artifact.StoragePath);
        return JsonSerializer.Serialize(new
        {
            artifact.Id,
            artifact.Type,
            artifact.FileName,
            artifact.Status,
            fileExists = exists,
            nonBlank = exists && new FileInfo(artifact.StoragePath).Length > 128,
            includesIssueSummary = artifact.MetadataJson.Contains("includeIssues", StringComparison.OrdinalIgnoreCase),
            snapshotBytes = Encoding.UTF8.GetByteCount(snapshot)
        }, JsonOptions);
    }

    private async Task<ReportPackage> LoadPackageAsync(Guid packageId, CancellationToken cancellationToken)
        => await db.ReportPackages
            .Include(x => x.Organization)
            .Include(x => x.ReportingPeriod)
            .Include(x => x.Slides.OrderBy(s => s.SortOrder))
                .ThenInclude(x => x.Blocks.OrderBy(b => b.SortOrder))
            .Include(x => x.Issues)
            .FirstAsync(x => x.Id == packageId, cancellationToken);

    private string EnsureExportDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("FINANCIAL_REPORTING_EXPORT_DIR");
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "exports")
            : configured;
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static IEnumerable<string> BuildPackageLines(ReportPackage package, bool includeIssues, bool includeAppendix)
    {
        yield return $"{package.Organization?.Name} Board Package";
        yield return $"Period: {package.ReportingPeriod?.Label}";
        yield return $"Version: {package.VersionLabel}";
        yield return "";
        foreach (var slide in package.Slides.OrderBy(x => x.SortOrder))
        {
            yield return $"{slide.SortOrder}. {slide.Subject} - {slide.KpiLabel}";
            yield return $"Current {slide.CurrentValue:C0} | Prior {slide.PriorValue:C0} | Variance {slide.VarianceAmount:C0} ({slide.VariancePercent:0.0}%)";
            foreach (var block in slide.Blocks.Where(x => x.Kind is "text" or "callout"))
            {
                var text = ExtractTextFromJson(block.ContentJson);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return text;
                }
            }
            yield return "";
        }

        if (includeIssues)
        {
            yield return "QA Issues";
            foreach (var issue in package.Issues.OrderByDescending(x => x.CreatedAt))
            {
                yield return $"{issue.Severity} | {issue.Status} | {issue.Title}: {issue.Description}";
            }
        }

        if (includeAppendix)
        {
            yield return "";
            yield return "Appendix";
            yield return "Source: persisted package, GL mappings, elimination overlays, KPI data, and AI review records.";
        }
    }

    /// <summary>P1.13 — board-grade PDF rendering via QuestPDF. Cat 27.
    /// Replaces the prior 46-line ASCII Helvetica stub with: cover page, one section per
    /// slide with a KPI tile and current/prior/variance summary, narrative blocks rendered
    /// as paragraphs, optional QA Issues section, optional Appendix, branding from
    /// ThemeJson, page numbers in the footer.</summary>
    private static int RenderPackagePdf(ReportPackage package, bool includeIssues, bool includeAppendix, string path)
    {
        var theme = ParseTheme(package.ThemeJson);
        var primaryHex = theme.Primary;
        var accentHex = theme.Accent;
        var fontFamily = theme.FontFamily;
        var headerText = string.IsNullOrWhiteSpace(theme.HeaderText) ? package.Organization?.Name ?? "Board Package" : theme.HeaderText;
        var footerText = string.IsNullOrWhiteSpace(theme.FooterText) ? "Confidential — Board distribution" : theme.FooterText;

        var document = QuestPDF.Fluent.Document.Create(container =>
        {
            // ── Cover page ──────────────────────────────────────────────────────────
            container.Page(page =>
            {
                page.Size(QuestPDF.Helpers.PageSizes.Letter);
                page.Margin(36);
                page.DefaultTextStyle(t => t.FontFamily(fontFamily));
                page.Content().Column(col =>
                {
                    col.Spacing(12);
                    col.Item().Text(headerText).FontSize(28).Bold().FontColor(primaryHex);
                    col.Item().Text($"{package.ReportingPeriod?.Label ?? package.ReportingPeriod?.Key}").FontSize(18).FontColor(accentHex);
                    col.Item().Text($"Version: {package.VersionLabel}").FontSize(12);
                    if (package.IsApproved)
                    {
                        col.Item().Text($"Approved by {package.ApprovedBy} on {package.ApprovedAt:yyyy-MM-dd HH:mm}Z").FontSize(11).FontColor("#2A7A2A");
                    }
                    else
                    {
                        col.Item().Text("DRAFT — not yet approved").FontSize(11).FontColor("#A0522D");
                    }
                    col.Item().PaddingTop(12).Text(theme.Tagline).FontSize(11).Italic();
                });
                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span(footerText).FontSize(9).FontColor("#777");
                });
            });

            // ── One page per slide ──────────────────────────────────────────────────
            foreach (var slide in package.Slides.OrderBy(x => x.SortOrder))
            {
                container.Page(page =>
                {
                    page.Size(QuestPDF.Helpers.PageSizes.Letter);
                    page.Margin(36);
                    page.DefaultTextStyle(t => t.FontFamily(fontFamily));
                    page.Header().BorderBottom(1).BorderColor(primaryHex).PaddingBottom(8).Row(row =>
                    {
                        row.RelativeItem().Text(headerText).FontSize(11).Bold().FontColor(primaryHex);
                        row.ConstantItem(140).AlignRight().Text(package.ReportingPeriod?.Label ?? "").FontSize(10).FontColor("#666");
                    });
                    page.Content().PaddingVertical(14).Column(col =>
                    {
                        col.Spacing(10);
                        col.Item().Text(slide.Subject).FontSize(20).Bold().FontColor(primaryHex);
                        if (!string.IsNullOrWhiteSpace(slide.KpiLabel))
                        {
                            col.Item().Text(slide.KpiLabel).FontSize(12).FontColor("#555");
                        }
                        col.Item().Background(accentHex + "20").Padding(8).Row(metricsRow =>
                        {
                            metricsRow.RelativeItem().Column(cell => { cell.Item().Text("Current").FontSize(9).FontColor("#666"); cell.Item().Text(slide.CurrentValue.ToString("C0")).FontSize(14).Bold(); });
                            metricsRow.RelativeItem().Column(cell => { cell.Item().Text("Prior").FontSize(9).FontColor("#666"); cell.Item().Text(slide.PriorValue.ToString("C0")).FontSize(14); });
                            metricsRow.RelativeItem().Column(cell =>
                            {
                                cell.Item().Text("Variance").FontSize(9).FontColor("#666");
                                var color = slide.VarianceAmount >= 0m ? "#1F6F3A" : "#9B2C2C";
                                cell.Item().Text($"{slide.VarianceAmount:C0} ({slide.VariancePercent:0.0}%)").FontSize(14).Bold().FontColor(color);
                            });
                        });

                        foreach (var block in slide.Blocks
                                     .Where(x => x.Kind is "text" or "callout")
                                     .OrderBy(x => x.SortOrder))
                        {
                            var text = ExtractTextFromJson(block.ContentJson);
                            if (string.IsNullOrWhiteSpace(text))
                            {
                                continue;
                            }
                            // P1.17 — visually mark AI-authored blocks. Cat 25.
                            var prefix = block.IsAiAuthored ? "✨ " : "";
                            col.Item().Text($"{prefix}{text}").FontSize(11);
                        }
                    });
                    page.Footer().Row(footRow =>
                    {
                        footRow.RelativeItem().Text(footerText).FontSize(9).FontColor("#777");
                        footRow.ConstantItem(80).AlignRight().Text(text =>
                        {
                            text.Span("Page ").FontSize(9).FontColor("#777");
                            text.CurrentPageNumber().FontSize(9);
                            text.Span(" / ").FontSize(9).FontColor("#777");
                            text.TotalPages().FontSize(9);
                        });
                    });
                });
            }

            // ── QA Issues ───────────────────────────────────────────────────────────
            if (includeIssues && package.Issues.Count > 0)
            {
                container.Page(page =>
                {
                    page.Size(QuestPDF.Helpers.PageSizes.Letter);
                    page.Margin(36);
                    page.DefaultTextStyle(t => t.FontFamily(fontFamily));
                    page.Header().BorderBottom(1).BorderColor(primaryHex).PaddingBottom(8)
                        .Text("QA Issues").FontSize(16).Bold().FontColor(primaryHex);
                    page.Content().PaddingTop(12).Column(col =>
                    {
                        col.Spacing(6);
                        foreach (var issue in package.Issues.OrderByDescending(x => x.CreatedAt))
                        {
                            col.Item().Text($"[{issue.Severity} · {issue.Status}] {issue.Title}").FontSize(11).Bold();
                            col.Item().Text(issue.Description).FontSize(10).FontColor("#555");
                        }
                    });
                });
            }

            // ── Appendix ────────────────────────────────────────────────────────────
            if (includeAppendix)
            {
                container.Page(page =>
                {
                    page.Size(QuestPDF.Helpers.PageSizes.Letter);
                    page.Margin(36);
                    page.DefaultTextStyle(t => t.FontFamily(fontFamily));
                    page.Header().BorderBottom(1).BorderColor(primaryHex).PaddingBottom(8)
                        .Text("Appendix").FontSize(16).Bold().FontColor(primaryHex);
                    page.Content().PaddingTop(12).Column(col =>
                    {
                        col.Spacing(6);
                        col.Item().Text("Source data").FontSize(12).Bold();
                        col.Item().Text("Persisted package, GL mappings, elimination overlays, KPI data, and AI review records.").FontSize(10);
                        col.Item().PaddingTop(8).Text("Approval status").FontSize(12).Bold();
                        col.Item().Text(package.IsApproved
                            ? $"Approved by {package.ApprovedBy} at {package.ApprovedAt:yyyy-MM-dd HH:mm}Z"
                            : "Not yet approved.").FontSize(10);
                    });
                });
            }
        });

        document.GeneratePdf(path);
        // Approximate page count for export QA metadata; QuestPDF doesn't expose it cheaply
        // without re-rendering, so use slide-count + cover + optional sections.
        return 1 + package.Slides.Count + (includeIssues ? 1 : 0) + (includeAppendix ? 1 : 0);
    }

    private static (string Primary, string Accent, string FontFamily, string HeaderText, string FooterText, string Tagline) ParseTheme(string themeJson)
    {
        var primary = "#0F2A4A";
        var accent = "#6B4FA8";
        var family = "Inter";
        var header = "";
        var footer = "";
        var tagline = "Confidential financial reporting";
        try
        {
            using var doc = JsonDocument.Parse(themeJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.String) primary = p.GetString() ?? primary;
            if (root.TryGetProperty("accent", out var a) && a.ValueKind == JsonValueKind.String) accent = a.GetString() ?? accent;
            if (root.TryGetProperty("fontFamily", out var f) && f.ValueKind == JsonValueKind.String) family = f.GetString() ?? family;
            if (root.TryGetProperty("headerText", out var h) && h.ValueKind == JsonValueKind.String) header = h.GetString() ?? "";
            if (root.TryGetProperty("footerText", out var ft) && ft.ValueKind == JsonValueKind.String) footer = ft.GetString() ?? "";
            if (root.TryGetProperty("tagline", out var tg) && tg.ValueKind == JsonValueKind.String) tagline = tg.GetString() ?? tagline;
        }
        catch
        {
            // fall through to defaults
        }
        return (primary, accent, family, header, footer, tagline);
    }

    private static string ExtractTextFromJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>P1.14 — board-grade XLSX rendering via ClosedXML. Cat 27.
    /// Every column with a numeric header (Amount, Current, Prior, Variance, etc.) gets a
    /// real numeric cell with format codes; header rows are bold + frozen; columns
    /// auto-size. Replaces the prior all-string inlineStr stub that broke Excel formulas.</summary>
    private static async Task WriteXlsxAsync(string path, Dictionary<string, string[][]> sheets, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var workbook = new ClosedXML.Excel.XLWorkbook();
            foreach (var (sheetName, rows) in sheets)
            {
                if (rows.Length == 0)
                {
                    continue;
                }
                // ClosedXML caps sheet names at 31 chars and disallows certain symbols.
                var safeName = SanitizeSheetName(sheetName);
                var sheet = workbook.Worksheets.Add(safeName);
                var headers = rows[0];
                var numericColumns = DetectNumericColumns(headers);

                for (var c = 0; c < headers.Length; c++)
                {
                    var headerCell = sheet.Cell(1, c + 1);
                    headerCell.Value = headers[c];
                    headerCell.Style.Font.Bold = true;
                    headerCell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromArgb(15, 42, 74);
                    headerCell.Style.Font.FontColor = ClosedXML.Excel.XLColor.White;
                }

                for (var r = 1; r < rows.Length; r++)
                {
                    var row = rows[r];
                    for (var c = 0; c < row.Length; c++)
                    {
                        var cell = sheet.Cell(r + 1, c + 1);
                        if (numericColumns.Contains(c)
                            && decimal.TryParse(row[c], NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
                        {
                            cell.Value = amount;
                            cell.Style.NumberFormat.Format = headers[c].Contains("%", StringComparison.Ordinal)
                                ? "0.0%"
                                : "#,##0.00;(#,##0.00)";
                        }
                        else
                        {
                            cell.Value = row[c];
                        }
                    }
                }

                // Freeze the header row, auto-size columns, and add a thin filter on the header.
                if (rows.Length > 1 && headers.Length > 0)
                {
                    sheet.SheetView.FreezeRows(1);
                    sheet.RangeUsed()?.SetAutoFilter();
                    sheet.Columns().AdjustToContents();
                }
            }
            workbook.SaveAs(path);
        }, cancellationToken);
    }

    private static string SanitizeSheetName(string name)
    {
        var cleaned = string.Concat(name.Where(ch => ch != '/' && ch != '\\' && ch != '?' && ch != '*' && ch != '[' && ch != ']' && ch != ':'));
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    private static HashSet<int> DetectNumericColumns(string[] headers)
    {
        var set = new HashSet<int>();
        var hints = new[] { "amount", "current", "prior", "variance", "confidence", "% ", "%", "value", "balance", "total", "rate" };
        for (var i = 0; i < headers.Length; i++)
        {
            var lower = headers[i].ToLowerInvariant();
            if (hints.Any(hint => lower.Contains(hint, StringComparison.Ordinal)))
            {
                set.Add(i);
            }
        }
        return set;
    }

    private static string Slug(string value)
        => string.Concat(value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')).Trim('-');
}

public sealed class XeroIntegrationService(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    IDataProtectionProvider dataProtectionProvider,
    MappingService mappingService,
    XeroTokenRefreshLock refreshLock,
    ILogger<XeroIntegrationService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("FinanceApp.Secrets.v1");

    public object GetStatus(
        IEnumerable<XeroConnection>? connections = null,
        IEnumerable<XeroSyncRun>? syncRuns = null,
        IEnumerable<XeroTenantConnection>? tenants = null,
        IEnumerable<XeroTenantEntityMapping>? mappings = null,
        XeroLedgerSyncStatus? ledgerSync = null)
    {
        var clientConfigured = !string.IsNullOrWhiteSpace(configuration["Xero:ClientId"]);
        var redirectUri = configuration["Xero:RedirectUri"] ?? "not configured";
        var scopes = configuration["Xero:Scopes"] ?? "offline_access accounting.reports.read accounting.transactions.read accounting.settings.read";
        var latestByOrg = (syncRuns ?? [])
            .GroupBy(x => x.OrganizationId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(r => r.StartedAt).First());
        var mappingsByTenant = (mappings ?? []).ToDictionary(x => x.TenantId, StringComparer.OrdinalIgnoreCase);
        return new
        {
            clientConfigured,
            redirectUri,
            scopes,
            environment = configuration.GetValue("Ai:UseMockRunner", true) ? "Development" : "Production-like",
            allowLocalStubReports = configuration.GetValue("Xero:AllowLocalStubReports", false),
            connections = connections?.Select(c => new
            {
                c.Id,
                c.OrganizationId,
                c.TenantId,
                c.TenantName,
                c.TenantType,
                c.ConnectionStatus,
                c.LastConnectedAt,
                c.LastError,
                c.TokenExpiresAt,
                c.Scopes,
                isTokenExpired = DateTimeOffset.UtcNow >= c.TokenExpiresAt,
                lastSyncAt = latestByOrg.TryGetValue(c.OrganizationId, out var run) ? run.CompletedAt ?? run.StartedAt : (DateTimeOffset?)null,
                lastSyncStatus = latestByOrg.TryGetValue(c.OrganizationId, out var lastRun) ? lastRun.Status : null
            }),
            tenants = tenants?.Select(t =>
            {
                mappingsByTenant.TryGetValue(t.TenantId, out var mapping);
                return new
                {
                    t.Id,
                    t.TenantId,
                    t.TenantName,
                    t.TenantType,
                    t.ConnectionStatus,
                    t.LastConnectedAt,
                    t.LastError,
                    t.TokenExpiresAt,
                    t.Scopes,
                    t.RequiresReconnectForLedger,
                    t.Source,
                    isTokenExpired = DateTimeOffset.UtcNow >= t.TokenExpiresAt,
                    mappedOrganizationId = mapping?.OrganizationId,
                    isIgnored = mapping?.IsIgnored ?? false
                };
            }),
            ledgerSync,
            tokenImport = new
            {
                supported = true,
                requiresMatchingDataProtectionKeyRing = true,
                source = string.IsNullOrWhiteSpace(configuration["Xero:FinanceAppV2ConnectionString"])
                    && string.IsNullOrWhiteSpace(configuration["Xero:FinanceAppV2DbPath"])
                    ? "not configured"
                    : "Finance App V2"
            }
        };
    }

    public async Task<XeroConnectResponse> BuildConnectUrlAsync(AppDbContext db, Guid organizationId, CancellationToken cancellationToken)
    {
        var clientId = configuration["Xero:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new XeroConnectResponse(null, null, "Xero:ClientId is not configured. Add the Finance App V2 OAuth client id.");
        }

        var state = $"{organizationId:N}:{Base64Url(RandomNumberGenerator.GetBytes(32))}";
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var redirectUri = configuration["Xero:RedirectUri"] ?? "http://localhost:5264/api/xero/callback";
        var scopes = configuration["Xero:Scopes"] ?? "offline_access accounting.reports.read accounting.transactions.read accounting.settings.read";
        var authUrl = configuration["Xero:AuthUrl"] ?? "https://login.xero.com/identity/connect/authorize";

        db.XeroOAuthSessions.Add(new XeroOAuthSession
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            State = state,
            ProtectedCodeVerifier = Protect(verifier),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        });
        await db.SaveChangesAsync(cancellationToken);

        var url = $"{authUrl}?response_type=code" +
                  $"&client_id={Uri.EscapeDataString(clientId)}" +
                  $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
                  $"&scope={Uri.EscapeDataString(scopes)}" +
                  $"&state={Uri.EscapeDataString(state)}" +
                  $"&code_challenge={Uri.EscapeDataString(challenge)}" +
                  "&code_challenge_method=S256";

        return new XeroConnectResponse(url, state, null);
    }

    public async Task<XeroConnection> CompleteCallbackAsync(AppDbContext db, string code, string state, CancellationToken cancellationToken)
    {
        var session = await db.XeroOAuthSessions
            .FirstOrDefaultAsync(x => x.State == state && x.CodeConsumedAt == null, cancellationToken)
            ?? throw new InvalidOperationException("The Xero connection state is invalid or expired.");

        if (session.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("The Xero connection state is invalid or expired.");
        }

        var clientId = configuration["Xero:ClientId"]
            ?? throw new InvalidOperationException("Xero:ClientId is not configured.");
        var tokenUrl = configuration["Xero:TokenUrl"] ?? "https://identity.xero.com/connect/token";
        var redirectUri = configuration["Xero:RedirectUri"] ?? "http://localhost:5264/api/xero/callback";
        var codeVerifier = Unprotect(session.ProtectedCodeVerifier);

        var client = httpClientFactory.CreateClient();
        using var response = await client.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier
        }), cancellationToken);

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Xero token exchange failed with status {StatusCode}.", response.StatusCode);
            throw new InvalidOperationException($"Xero token exchange failed: {response.StatusCode}");
        }

        var token = JsonSerializer.Deserialize<XeroTokenResponse>(responseContent, JsonOptions)
            ?? throw new InvalidOperationException("Xero token response could not be parsed.");
        var tenants = await FetchTenantsAsync(token.AccessToken, cancellationToken);
        if (tenants.Count == 0)
        {
            throw new InvalidOperationException("Xero did not return any tenants for the OAuth connection.");
        }

        XeroConnection? firstConnection = null;
        foreach (var tenant in tenants)
        {
            var organization = await EnsureOrganizationForTenantAsync(db, tenant, cancellationToken);
            var global = await db.XeroTenantConnections.FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId, cancellationToken)
                         ?? new XeroTenantConnection
                         {
                             Id = Guid.NewGuid(),
                             TenantId = tenant.TenantId,
                             CreatedAt = DateTimeOffset.UtcNow
                         };
            global.TenantName = tenant.TenantName;
            global.TenantType = tenant.TenantType;
            global.EncryptedAccessToken = Protect(token.AccessToken);
            global.EncryptedRefreshToken = Protect(token.RefreshToken);
            global.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
            global.Scopes = configuration["Xero:Scopes"] ?? "";
            global.ConnectionStatus = "Connected";
            global.RequiresReconnectForLedger = !HasScope(global.Scopes, "accounting.journals.read");
            global.LastConnectedAt = DateTimeOffset.UtcNow;
            global.LastError = global.RequiresReconnectForLedger ? "Reconnect required for GL sync because accounting.journals.read is missing." : null;
            global.Source = "Xero OAuth";
            global.UpdatedAt = DateTimeOffset.UtcNow;
            if (db.Entry(global).State == EntityState.Detached)
            {
                db.XeroTenantConnections.Add(global);
            }

            var mapping = await db.XeroTenantEntityMappings.FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId, cancellationToken)
                          ?? new XeroTenantEntityMapping { Id = Guid.NewGuid(), TenantId = tenant.TenantId, CreatedAt = DateTimeOffset.UtcNow };
            mapping.OrganizationId = organization.Id;
            mapping.IsIgnored = false;
            mapping.Reason = "Connected through Xero OAuth";
            mapping.UpdatedAt = DateTimeOffset.UtcNow;
            if (db.Entry(mapping).State == EntityState.Detached)
            {
                db.XeroTenantEntityMappings.Add(mapping);
            }

            var existing = await db.XeroConnections
                .FirstOrDefaultAsync(x => x.TenantId == tenant.TenantId, cancellationToken);

            var connection = existing ?? new XeroConnection
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.TenantId,
                CreatedAt = DateTimeOffset.UtcNow
            };

            connection.OrganizationId = organization.Id;
            connection.TenantName = tenant.TenantName;
            connection.TenantType = tenant.TenantType;
            connection.EncryptedAccessToken = Protect(token.AccessToken);
            connection.EncryptedRefreshToken = Protect(token.RefreshToken);
            connection.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
            connection.Scopes = configuration["Xero:Scopes"] ?? "";
            connection.ConnectionStatus = "Connected";
            connection.LastConnectedAt = DateTimeOffset.UtcNow;
            connection.LastError = null;
            connection.UpdatedAt = DateTimeOffset.UtcNow;

            if (existing is null)
            {
                db.XeroConnections.Add(connection);
            }

            firstConnection ??= connection;
        }

        session.CodeConsumedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return firstConnection
            ?? throw new InvalidOperationException("Xero did not return any tenants for the OAuth connection.");
    }

    public async Task<XeroSyncRun> SyncPackageAsync(AppDbContext db, Guid packageId, CancellationToken cancellationToken)
    {
        var package = await db.ReportPackages
            .Include(x => x.Organization)
            .Include(x => x.ReportingPeriod)
            .FirstAsync(x => x.Id == packageId, cancellationToken);

        var connection = await db.XeroConnections
            .FirstOrDefaultAsync(x => x.OrganizationId == package.OrganizationId && x.ConnectionStatus == "Connected", cancellationToken)
            ?? await db.XeroConnections.FirstOrDefaultAsync(x => x.OrganizationId == package.OrganizationId, cancellationToken);
        if (connection is null || !string.Equals(connection.ConnectionStatus, "Connected", StringComparison.OrdinalIgnoreCase))
        {
            connection = await BuildGlobalTenantConnectionForPackageAsync(db, package, cancellationToken) ?? connection;
        }
        return await SyncPackageCoreAsync(db, package, connection, "accrual", cancellationToken);
    }

    private static async Task<XeroConnection?> BuildGlobalTenantConnectionForPackageAsync(AppDbContext db, ReportPackage package, CancellationToken cancellationToken)
    {
        var row = await db.XeroTenantEntityMappings
            .AsNoTracking()
            .Where(mapping => mapping.OrganizationId == package.OrganizationId && !mapping.IsIgnored)
            .Join(db.XeroTenantConnections.AsNoTracking().Where(tenant => tenant.ConnectionStatus == "Connected"),
                mapping => mapping.TenantId,
                tenant => tenant.TenantId,
                (mapping, tenant) => tenant)
            .OrderBy(tenant => tenant.TenantName)
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        return new XeroConnection
        {
            Id = row.Id,
            OrganizationId = package.OrganizationId,
            TenantId = row.TenantId,
            TenantName = row.TenantName,
            TenantType = row.TenantType,
            EncryptedAccessToken = row.EncryptedAccessToken,
            EncryptedRefreshToken = row.EncryptedRefreshToken,
            TokenExpiresAt = row.TokenExpiresAt,
            Scopes = row.Scopes,
            ConnectionStatus = row.ConnectionStatus,
            LastConnectedAt = row.LastConnectedAt,
            LastError = row.LastError
        };
    }

    public async Task<XeroPeriodSyncResult> SyncPeriodAsync(AppDbContext db, XeroPeriodSyncOptions options, CancellationToken cancellationToken)
    {
        var period = await EnsureReportingPeriodAsync(db, options.PeriodKey, cancellationToken);
        var allowStub = configuration.GetValue("Xero:AllowLocalStubReports", false);
        var connections = new List<XeroConnection>();
        var globalTenants = await db.XeroTenantConnections
            .Where(x => allowStub || x.ConnectionStatus == "Connected")
            .OrderBy(x => x.TenantName)
            .ToListAsync(cancellationToken);
        if (globalTenants.Count > 0)
        {
            var mappings = await db.XeroTenantEntityMappings
                .AsNoTracking()
                .Where(x => !x.IsIgnored)
                .ToDictionaryAsync(x => x.TenantId, cancellationToken);
            foreach (var tenant in options.IncludeAllTenants ? globalTenants : globalTenants.Take(1))
            {
                if (!mappings.TryGetValue(tenant.TenantId, out var mapping))
                {
                    continue;
                }

                connections.Add(new XeroConnection
                {
                    Id = tenant.Id,
                    OrganizationId = mapping.OrganizationId,
                    TenantId = tenant.TenantId,
                    TenantName = tenant.TenantName,
                    TenantType = tenant.TenantType,
                    EncryptedAccessToken = tenant.EncryptedAccessToken,
                    EncryptedRefreshToken = tenant.EncryptedRefreshToken,
                    TokenExpiresAt = tenant.TokenExpiresAt,
                    Scopes = tenant.Scopes,
                    ConnectionStatus = tenant.ConnectionStatus,
                    LastConnectedAt = tenant.LastConnectedAt,
                    LastError = tenant.LastError
                });
            }
        }
        else
        {
            var connectionsQuery = db.XeroConnections.AsQueryable();
            if (!allowStub)
            {
                connectionsQuery = connectionsQuery.Where(x => x.ConnectionStatus == "Connected");
            }

            connections = options.IncludeAllTenants
                ? await connectionsQuery.OrderBy(x => x.TenantName).ToListAsync(cancellationToken)
                : await connectionsQuery.OrderBy(x => x.TenantName).Take(1).ToListAsync(cancellationToken);
        }

        if (connections.Count == 0 && allowStub && configuration.GetValue("Xero:EnableTestFixtures", false))
        {
            var orgs = await db.Organizations
                .Where(x => !x.IsConsolidated)
                .OrderBy(x => x.Name)
                .ToListAsync(cancellationToken);
            foreach (var org in orgs)
            {
                connections.Add(new XeroConnection
                {
                    Id = Guid.Empty,
                    OrganizationId = org.Id,
                    TenantId = $"fixture-{org.Key}",
                    TenantName = org.Name,
                    TenantType = "ORGANISATION",
                    ConnectionStatus = "TestFixture"
                });
            }
        }

        var packageIds = new List<Guid>();
        var syncRunIds = new List<Guid>();
        var statementRunIds = new List<Guid>();
        var failed = new List<string>();

        foreach (var connection in connections)
        {
            var organization = connection.Id == Guid.Empty
                ? await db.Organizations.FirstAsync(x => x.Id == connection.OrganizationId, cancellationToken)
                : await EnsureOrganizationForTenantAsync(db, new XeroTenant(connection.TenantId, connection.TenantName, connection.TenantType), cancellationToken);
            if (connection.Id != Guid.Empty && connection.OrganizationId != organization.Id)
            {
                connection.OrganizationId = organization.Id;
                connection.UpdatedAt = DateTimeOffset.UtcNow;
            }

            var package = await EnsurePackageAsync(db, organization, period, cancellationToken);
            var run = await SyncPackageCoreAsync(db, package, connection, options.Basis, cancellationToken);
            packageIds.Add(package.Id);
            syncRunIds.Add(run.Id);
            var statementRun = (await db.StatementRuns
                .AsNoTracking()
                .Where(x => x.ReportPackageId == package.Id)
                .ToListAsync(cancellationToken))
                .OrderByDescending(x => x.StartedAt)
                .FirstOrDefault();
            if (statementRun is not null)
            {
                statementRunIds.Add(statementRun.Id);
            }
            if (run.Status != "Completed")
            {
                failed.Add($"{connection.TenantName}: {run.Error ?? run.Status}");
            }
        }

        if (!options.CreateConsolidation)
        {
            var consolidated = await db.Organizations
                .Where(x => x.IsConsolidated)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
            var accidentalPackages = await db.ReportPackages
                .Where(x => x.ReportingPeriodId == period.Id && consolidated.Contains(x.OrganizationId) && !packageIds.Contains(x.Id))
                .ToListAsync(cancellationToken);
            foreach (var package in accidentalPackages)
            {
                package.Status = PackageStatus.Blocked;
                package.BlockReason = "January entity sync explicitly skipped consolidated package creation.";
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return new XeroPeriodSyncResult(
            period.Id,
            period.Key,
            packageIds.Count,
            syncRunIds.Count,
            statementRunIds.Count,
            failed.Count == 0 ? "Completed" : "CompletedWithErrors",
            packageIds,
            syncRunIds,
            failed);
    }

    private async Task<XeroSyncRun> SyncPackageCoreAsync(AppDbContext db, ReportPackage package, XeroConnection? connection, string basis, CancellationToken cancellationToken)
    {
        var run = new XeroSyncRun
        {
            Id = Guid.NewGuid(),
            OrganizationId = package.OrganizationId,
            ReportingPeriodId = package.ReportingPeriodId,
            Status = "Running",
            StartedAt = DateTimeOffset.UtcNow
        };
        db.XeroSyncRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var statementRun = new StatementRun
            {
                Id = Guid.NewGuid(),
                OrganizationId = package.OrganizationId,
                ReportingPeriodId = package.ReportingPeriodId,
                ReportPackageId = package.Id,
                TenantId = connection?.TenantId ?? package.Organization?.Key ?? "",
                Basis = NormalizeBasis(basis),
                Status = "Running",
                StartedAt = DateTimeOffset.UtcNow
            };
            db.StatementRuns.Add(statementRun);
            await db.SaveChangesAsync(cancellationToken);

            var imported = connection is not null && connection.ConnectionStatus == "Connected"
                ? await FetchXeroFinancialImportAsync(db, package, connection, NormalizeBasis(basis), cancellationToken)
                : configuration.GetValue("Xero:EnableTestFixtures", false)
                    ? BuildTestFixtureFinancialImport(package, connection?.TenantId ?? $"fixture-{package.Organization?.Key ?? "entity"}", NormalizeBasis(basis))
                    : throw new InvalidOperationException("Live Xero connection is required. Test fixture financial imports are disabled.");

            var priorAccounts = await db.GlAccounts
                .AsNoTracking()
                .Where(x => x.OrganizationId == package.OrganizationId && x.ReportingPeriodId != package.ReportingPeriodId)
                .ToListAsync(cancellationToken);
            var fsLineDefinitions = await db.FsLineDefinitions
                .AsNoTracking()
                .Where(x => x.OrganizationId == package.OrganizationId && x.IsActive)
                .OrderBy(x => x.StatementType)
                .ThenBy(x => x.Section)
                .ThenBy(x => x.SortOrder)
                .ToListAsync(cancellationToken);

            var upserted = 0;
            var firstSeen = 0;
            foreach (var source in imported.Accounts)
            {
                var account = await db.GlAccounts
                    .FirstOrDefaultAsync(x =>
                        x.OrganizationId == package.OrganizationId
                        && x.ReportingPeriodId == package.ReportingPeriodId
                        && x.TenantId == source.TenantId
                        && x.Code == source.Code,
                        cancellationToken);

                if (account is null)
                {
                    account = new GlAccount
                    {
                        Id = Guid.NewGuid(),
                        OrganizationId = package.OrganizationId,
                        ReportingPeriodId = package.ReportingPeriodId,
                        TenantId = source.TenantId,
                        Code = source.Code,
                        CreatedAt = DateTimeOffset.UtcNow
                    };
                    db.GlAccounts.Add(account);
                }

                var isFirstSeen = mappingService.IsFirstSeenAccount(priorAccounts, source.TenantId, source.Code)
                                  && string.IsNullOrWhiteSpace(account.AuditReason);
                account.Name = source.Name;
                account.Type = source.Type;
                account.Class = source.Class;
                var suggestedFsLine = SuggestFsLineFromDefinitions(source.Name, source.Type, fsLineDefinitions) ?? source.AiSuggestedFsLine;
                account.FsLine = string.IsNullOrWhiteSpace(account.FsLine) ? (string.IsNullOrWhiteSpace(suggestedFsLine) ? source.FsLine : suggestedFsLine) : account.FsLine;
                account.AiSuggestedFsLine = string.IsNullOrWhiteSpace(suggestedFsLine) ? source.AiSuggestedFsLine : suggestedFsLine;
                account.MappingConfidence = mappingService.SuggestConfidence(source.Name, account.AiSuggestedFsLine);
                account.IsFirstSeen = isFirstSeen;
                account.ReviewStatus = account.ReviewStatus == MappingReviewStatus.Reviewed
                    ? MappingReviewStatus.Reviewed
                    : isFirstSeen ? MappingReviewStatus.New : MappingReviewStatus.Suggested;
                account.MonthlyBalancesJson = JsonSerializer.Serialize(source.MonthlyBalances, JsonOptions);
                account.PriorPeriodHistoryJson = JsonSerializer.Serialize(source.PriorPeriodHistory, JsonOptions);
                account.UpdatedAt = DateTimeOffset.UtcNow;

                await db.GlTransactions
                    .Where(x => x.GlAccountId == account.Id)
                    .ExecuteDeleteAsync(cancellationToken);
                foreach (var tx in source.Transactions)
                {
                    db.GlTransactions.Add(new GlTransaction
                    {
                        Id = Guid.NewGuid(),
                        GlAccountId = account.Id,
                        TransactionDate = tx.TransactionDate,
                        Description = tx.Description,
                        Debit = tx.Debit,
                        Credit = tx.Credit,
                        Source = tx.Source
                    });
                }

                upserted++;
                if (isFirstSeen)
                {
                    firstSeen++;
                }
            }

            await db.FinancialStatementLines
                .Where(x => x.ReportPackageId == package.Id)
                .ExecuteDeleteAsync(cancellationToken);
            foreach (var line in imported.StatementLines.OrderBy(x => x.SortOrder))
            {
                db.FinancialStatementLines.Add(new FinancialStatementLine
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = package.OrganizationId,
                    ReportingPeriodId = package.ReportingPeriodId,
                    ReportPackageId = package.Id,
                    XeroRawReportSnapshotId = line.RawSnapshotId,
                    TenantId = imported.TenantId,
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

            await RegeneratePackageSlidesAsync(db, package, imported.StatementLines, imported.Accounts, cancellationToken);
            var tieOut = BuildTieOut(imported.StatementLines);
            db.StatementQaResults.Add(new StatementQaResult
            {
                Id = Guid.NewGuid(),
                ReportPackageId = package.Id,
                StatementRunId = statementRun.Id,
                Status = tieOut.IsBalanced ? "Passed" : "Review",
                SummaryJson = JsonSerializer.Serialize(tieOut, JsonOptions)
            });

            package.LastXeroSyncAt = DateTimeOffset.UtcNow;
            package.Status = PackageStatus.Review;
            package.BaseFrom = BuildPriorYearLabel(package.ReportingPeriod);
            package.VersionLabel = $"{package.ReportingPeriod?.Label ?? "Financial"} draft";
            package.UpdatedAt = DateTimeOffset.UtcNow;
            run.Status = "Completed";
            run.CompletedAt = DateTimeOffset.UtcNow;
            run.SummaryJson = JsonSerializer.Serialize(new
            {
                accounts = upserted,
                firstSeen,
                statementLines = imported.StatementLines.Count,
                rawSnapshots = imported.RawSnapshotIds.Count,
                source = imported.Source,
                tenant = connection?.TenantName,
                basis = NormalizeBasis(basis),
                tieOut
            }, JsonOptions);
            statementRun.Status = "Completed";
            statementRun.CompletedAt = DateTimeOffset.UtcNow;
            statementRun.SummaryJson = run.SummaryJson;

            await db.SaveChangesAsync(cancellationToken);
            return run;
        }
        catch (Exception ex)
        {
            package.Status = package.LastXeroSyncAt is null ? PackageStatus.Blocked : PackageStatus.Review;
            package.BlockReason = "Latest Xero refresh failed. Existing package data was left in place; reconnect the tenant and retry.";
            package.IsSourceDataStale = true;
            package.SourceDataStaleReason = package.BlockReason;
            package.SourceDataChangedAt = DateTimeOffset.UtcNow;
            package.UpdatedAt = DateTimeOffset.UtcNow;
            run.Status = "Failed";
            run.Error = "Xero sync failed. Reconnect the tenant and retry.";
            run.CompletedAt = DateTimeOffset.UtcNow;
            logger.LogError(ex, "Xero sync failed for package {PackageId}", package.Id);
            await db.SaveChangesAsync(cancellationToken);
            return run;
        }
    }

    public async Task<XeroImportResult> ImportFinanceAppV2TokensAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var v2ConnectionString = configuration["Xero:FinanceAppV2ConnectionString"];
        if (string.IsNullOrWhiteSpace(v2ConnectionString))
        {
            return new XeroImportResult(0, "Finance App V2 connection string is not configured; reconnect in this app.");
        }

        // Token import is intentionally conservative. Without the matching V2 DataProtection
        // key ring, copied encrypted tokens are unusable and users should reconnect.
        return await Task.FromResult(new XeroImportResult(0, "Configured, but token import requires running with the same V2 DataProtection app name, key ring, and token purpose. Reconnect is the safe fallback."));
    }

    private async Task<List<XeroTenant>> FetchTenantsAsync(string accessToken, CancellationToken cancellationToken)
    {
        var url = configuration["Xero:ConnectionsUrl"] ?? "https://api.xero.com/connections";
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.GetAsync(url, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Failed to fetch Xero tenants: {response.StatusCode}");
        }

        return JsonSerializer.Deserialize<List<XeroTenant>>(content, JsonOptions) ?? [];
    }

    private async Task<XeroFinancialImport> FetchXeroFinancialImportAsync(AppDbContext db, ReportPackage package, XeroConnection connection, string basis, CancellationToken cancellationToken)
    {
        var accessToken = await EnsureValidTokenAsync(db, connection, cancellationToken);
        var apiBase = configuration["Xero:ApiBaseUrl"] ?? "https://api.xero.com/api.xro/2.0";
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        client.DefaultRequestHeaders.Add("xero-tenant-id", connection.TenantId);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var period = package.ReportingPeriod ?? throw new InvalidOperationException("Package period is required for Xero sync.");
        var currentStart = period.PeriodStart;
        var currentEnd = period.PeriodEnd;
        var priorStart = new DateOnly(period.PeriodStart.Year - 1, period.PeriodStart.Month, period.PeriodStart.Day);
        var priorEnd = new DateOnly(period.PeriodEnd.Year - 1, period.PeriodEnd.Month, period.PeriodEnd.Day);
        var paymentsOnly = string.Equals(basis, "cash", StringComparison.OrdinalIgnoreCase) ? "true" : "false";

        var accountsUrl = $"{apiBase.TrimEnd('/')}/Accounts";
        using var response = await client.GetAsync(accountsUrl, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            connection.ConnectionStatus = "Error";
            connection.LastError = $"Accounts fetch failed: {response.StatusCode}";
            throw new InvalidOperationException(connection.LastError);
        }

        var accountSnapshot = await AddRawSnapshotAsync(db, package, connection, "Accounts", basis, accountsUrl, content, cancellationToken);
        using var document = JsonDocument.Parse(content);
        var accountRows = document.RootElement.TryGetProperty("Accounts", out var accounts) && accounts.ValueKind == JsonValueKind.Array
            ? accounts.EnumerateArray().ToList()
            : [];
        var importedAccounts = accountRows
            .Where(x => ReadString(x, "Code") is { Length: > 0 })
            .Select((x, index) =>
            {
                var code = ReadString(x, "Code")!;
                var name = ReadString(x, "Name") ?? code;
                var type = ReadString(x, "Type") ?? "Unknown";
                var monthly = BuildDeterministicMonthlyBalances(code, index);
                return new XeroImportedAccount(
                    connection.TenantId,
                    code,
                    name,
                    type,
                    ReadString(x, "Class") ?? GuessClass(type),
                    GuessFsLine(name, type),
                    GuessFsLine(name, type),
                    monthly,
                    ["prior-sync-import"],
                    [new XeroImportedTransaction(currentEnd, $"Xero balance import for {name}", type.Contains("EXPENSE", StringComparison.OrdinalIgnoreCase) ? monthly.LastOrDefault() : 0m, type.Contains("EXPENSE", StringComparison.OrdinalIgnoreCase) ? 0m : monthly.LastOrDefault(), "Xero")]);
            })
            .ToList();

        var statementLines = new List<XeroStatementLineImport>();
        var rawSnapshotIds = new List<Guid> { accountSnapshot.Id };

        var currentPnlUrl = $"{apiBase.TrimEnd('/')}/Reports/ProfitAndLoss?fromDate={DateParam(currentStart)}&toDate={DateParam(currentEnd)}&standardLayout=true&paymentsOnly={paymentsOnly}";
        var currentPnl = await FetchRequiredSnapshotAsync(db, client, package, connection, "ProfitAndLoss", basis, currentPnlUrl, cancellationToken);
        rawSnapshotIds.Add(currentPnl.Id);

        var priorPnlUrl = $"{apiBase.TrimEnd('/')}/Reports/ProfitAndLoss?fromDate={DateParam(priorStart)}&toDate={DateParam(priorEnd)}&standardLayout=true&paymentsOnly={paymentsOnly}";
        var priorPnl = await FetchRequiredSnapshotAsync(db, client, package, connection, "ProfitAndLossPriorYear", basis, priorPnlUrl, cancellationToken);
        rawSnapshotIds.Add(priorPnl.Id);

        var trendedStart = currentStart.AddMonths(-11);
        var trendPnlUrl = $"{apiBase.TrimEnd('/')}/Reports/ProfitAndLoss?fromDate={DateParam(trendedStart)}&toDate={DateParam(currentEnd)}&periods=11&timeframe=MONTH&standardLayout=true&paymentsOnly={paymentsOnly}";
        var trendedPnl = await FetchRequiredSnapshotAsync(db, client, package, connection, "TrendedProfitAndLoss", basis, trendPnlUrl, cancellationToken);
        rawSnapshotIds.Add(trendedPnl.Id);

        var balanceSheetUrl = $"{apiBase.TrimEnd('/')}/Reports/BalanceSheet?date={DateParam(currentEnd)}&standardLayout=true&paymentsOnly={paymentsOnly}";
        var balanceSheet = await FetchRequiredSnapshotAsync(db, client, package, connection, "BalanceSheet", basis, balanceSheetUrl, cancellationToken);
        rawSnapshotIds.Add(balanceSheet.Id);

        var trialBalanceUrl = $"{apiBase.TrimEnd('/')}/Reports/TrialBalance?date={DateParam(currentEnd)}&paymentsOnly={paymentsOnly}";
        var trialBalance = await FetchRequiredSnapshotAsync(db, client, package, connection, "TrialBalance", basis, trialBalanceUrl, cancellationToken);
        rawSnapshotIds.Add(trialBalance.Id);

        if (configuration.GetValue("Xero:HydratePackageJournals", false))
        {
            try
            {
                var journalsUrl = $"{apiBase.TrimEnd('/')}/Journals";
                var journals = await FetchOptionalSnapshotAsync(db, client, package, connection, "Journals", basis, journalsUrl, cancellationToken);
                if (journals is not null)
                {
                    rawSnapshotIds.Add(journals.Id);
                }
            }
            catch (Exception ex)
            {
                logger.LogInformation(ex, "Optional Xero journals fetch skipped for tenant {TenantId}.", connection.TenantId);
            }
        }

        var priorLines = ParseStatementLines("ProfitAndLossPriorYear", priorPnl.PayloadJson, connection.TenantId, priorPnl.Id);
        var priorByPath = priorLines
            .GroupBy(LineKey)
            .ToDictionary(x => x.Key, x => x.First().CurrentAmount);
        foreach (var line in ParseStatementLines("ProfitAndLoss", currentPnl.PayloadJson, connection.TenantId, currentPnl.Id))
        {
            statementLines.Add(line with { PriorAmount = priorByPath.TryGetValue(LineKey(line), out var prior) ? prior : 0m });
        }
        statementLines.AddRange(ParseStatementLines("TrendedProfitAndLoss", trendedPnl.PayloadJson, connection.TenantId, trendedPnl.Id));
        statementLines.AddRange(ParseStatementLines("BalanceSheet", balanceSheet.PayloadJson, connection.TenantId, balanceSheet.Id));
        statementLines.AddRange(ParseStatementLines("TrialBalance", trialBalance.PayloadJson, connection.TenantId, trialBalance.Id));

        ApplyStatementAmountsToAccounts(importedAccounts, statementLines);
        return new XeroFinancialImport(connection.TenantId, "xero", importedAccounts, statementLines, rawSnapshotIds);
    }

    private async Task<XeroRawReportSnapshot> FetchRequiredSnapshotAsync(AppDbContext db, HttpClient client, ReportPackage package, XeroConnection connection, string reportType, string basis, string url, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            connection.ConnectionStatus = "Error";
            connection.LastError = $"{reportType} fetch failed: {response.StatusCode}";
            throw new InvalidOperationException(connection.LastError);
        }

        return await AddRawSnapshotAsync(db, package, connection, reportType, basis, url, content, cancellationToken);
    }

    private async Task<XeroRawReportSnapshot?> FetchOptionalSnapshotAsync(AppDbContext db, HttpClient client, ReportPackage package, XeroConnection connection, string reportType, string basis, string url, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return response.IsSuccessStatusCode
            ? await AddRawSnapshotAsync(db, package, connection, reportType, basis, url, content, cancellationToken)
            : null;
    }

    private async Task<XeroRawReportSnapshot> AddRawSnapshotAsync(AppDbContext db, ReportPackage package, XeroConnection connection, string reportType, string basis, string url, string payloadJson, CancellationToken cancellationToken)
    {
        var snapshot = new XeroRawReportSnapshot
        {
            Id = Guid.NewGuid(),
            OrganizationId = package.OrganizationId,
            ReportingPeriodId = package.ReportingPeriodId,
            XeroConnectionId = connection.Id == Guid.Empty ? null : connection.Id,
            TenantId = connection.TenantId,
            ReportType = reportType,
            Basis = basis,
            RequestUrl = url,
            PayloadJson = payloadJson,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.XeroRawReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync(cancellationToken);
        return snapshot;
    }

    private async Task<Organization> EnsureOrganizationForTenantAsync(AppDbContext db, XeroTenant tenant, CancellationToken cancellationToken)
    {
        var key = SlugKey(tenant.TenantName);
        var organization = await db.Organizations.FirstOrDefaultAsync(x => x.Key == key || x.Name == tenant.TenantName, cancellationToken);
        if (organization is null)
        {
            organization = new Organization
            {
                Id = Guid.NewGuid(),
                Key = key,
                Name = tenant.TenantName,
                Abbreviation = BuildAbbreviation(tenant.TenantName),
                IsConsolidated = false,
                PrimaryColor = "#0F2A4A",
                AccentColor = "#0E6B47",
                Tagline = "Xero entity financial reporting"
            };
            db.Organizations.Add(organization);
        }
        else
        {
            organization.Name = tenant.TenantName;
            organization.Abbreviation = string.IsNullOrWhiteSpace(organization.Abbreviation)
                ? BuildAbbreviation(tenant.TenantName)
                : organization.Abbreviation;
            // Xero tenants are imported as standalone entity packages. A separate cross-tenant
            // consolidation package must be created explicitly in a later phase.
            organization.IsConsolidated = false;
            organization.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return organization;
    }

    private static async Task<ReportingPeriod> EnsureReportingPeriodAsync(AppDbContext db, string periodKey, CancellationToken cancellationToken)
    {
        if (!TryParsePeriodKey(periodKey, out var year, out var month))
        {
            throw new InvalidOperationException("Period key must be formatted as yyyy-MM.");
        }

        var period = await db.ReportingPeriods.FirstOrDefaultAsync(x => x.Key == periodKey, cancellationToken);
        if (period is not null)
        {
            return period;
        }

        var start = new DateOnly(year, month, 1);
        period = new ReportingPeriod
        {
            Id = Guid.NewGuid(),
            Key = periodKey,
            Label = start.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            PeriodStart = start,
            PeriodEnd = start.AddMonths(1).AddDays(-1),
            IsClosed = false
        };
        db.ReportingPeriods.Add(period);
        await db.SaveChangesAsync(cancellationToken);
        return period;
    }

    private static async Task<ReportPackage> EnsurePackageAsync(AppDbContext db, Organization organization, ReportingPeriod period, CancellationToken cancellationToken)
    {
        var package = await db.ReportPackages
            .Include(x => x.Organization)
            .Include(x => x.ReportingPeriod)
            .FirstOrDefaultAsync(x => x.OrganizationId == organization.Id && x.ReportingPeriodId == period.Id, cancellationToken);
        if (package is not null)
        {
            return package;
        }

        package = new ReportPackage
        {
            Id = Guid.NewGuid(),
            OrganizationId = organization.Id,
            Organization = organization,
            ReportingPeriodId = period.Id,
            ReportingPeriod = period,
            Status = PackageStatus.Draft,
            VersionLabel = $"{period.Label} draft",
            BaseFrom = BuildPriorYearLabel(period),
            ThemeJson = JsonSerializer.Serialize(new { primary = organization.PrimaryColor, accent = organization.AccentColor, organization.CoverStyle }, JsonOptions)
        };
        db.ReportPackages.Add(package);
        await db.SaveChangesAsync(cancellationToken);
        return package;
    }

    private async Task RegeneratePackageSlidesAsync(AppDbContext db, ReportPackage package, IReadOnlyList<XeroStatementLineImport> lines, IReadOnlyList<XeroImportedAccount> accounts, CancellationToken cancellationToken)
    {
        await db.SlideBlocks
            .Where(x => db.PackageSlides.Where(s => s.ReportPackageId == package.Id).Select(s => s.Id).Contains(x.PackageSlideId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.PackageSlides
            .Where(x => x.ReportPackageId == package.Id)
            .ExecuteDeleteAsync(cancellationToken);

        var resolvedLines = lines
            .Where(x => x.StatementType is "ProfitAndLoss" or "BalanceSheet" && Math.Abs(x.CurrentAmount) > 0.01m)
            .Select(line => new
            {
                Line = line,
                Accounts = ResolveAccountsForStatementLine(line, accounts)
            })
            .ToList();

        var pnlLines = resolvedLines
            .Where(x => x.Line.StatementType == "ProfitAndLoss" && !IsSummaryStatementLine(x.Line))
            .OrderByDescending(x => x.Accounts.Count > 0)
            .ThenByDescending(x => Math.Abs(x.Line.CurrentAmount - x.Line.PriorAmount))
            .ThenByDescending(x => Math.Abs(x.Line.CurrentAmount))
            .Take(6)
            .ToList();
        if (pnlLines.Count == 0)
        {
            pnlLines = resolvedLines
                .Where(x => !IsSummaryStatementLine(x.Line))
                .OrderByDescending(x => x.Accounts.Count > 0)
                .ThenByDescending(x => Math.Abs(x.Line.CurrentAmount))
                .Take(4)
                .ToList();
        }
        if (pnlLines.Count == 0)
        {
            pnlLines = resolvedLines
                .OrderByDescending(x => Math.Abs(x.Line.CurrentAmount))
                .Take(4)
                .ToList();
        }

        var sort = 1;
        var periodLabel = package.ReportingPeriod?.Label ?? "current period";
        var priorLabel = package.ReportingPeriod is null
            ? "prior year"
            : new DateOnly(package.ReportingPeriod.PeriodStart.Year - 1, package.ReportingPeriod.PeriodStart.Month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        foreach (var resolved in pnlLines)
        {
            var line = resolved.Line;
            var linkedAccounts = resolved.Accounts;
            var variance = FinancialMath.Variance(line.CurrentAmount, line.PriorAmount);
            var monthly = FindTrendAmounts(lines, line);
            var accountCodes = linkedAccounts
                .Select(x => x.Code)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var topAccounts = linkedAccounts
                .OrderByDescending(x => Math.Abs(x.MonthlyBalances.LastOrDefault()))
                .Take(8)
                .Select(account => new
                {
                    account.Code,
                    account.Name,
                    account.Type,
                    account.FsLine,
                    account.AiSuggestedFsLine,
                    Current = account.MonthlyBalances.LastOrDefault(),
                    TransactionCount = account.Transactions.Length
                })
                .ToArray();
            var transactionPreview = linkedAccounts
                .SelectMany(account => account.Transactions.Select(tx => new
                {
                    account.Code,
                    account.Name,
                    Date = tx.TransactionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    tx.Description,
                    Amount = tx.Credit - tx.Debit,
                    tx.Source
                }))
                .OrderByDescending(x => Math.Abs(x.Amount))
                .ThenByDescending(x => x.Date)
                .Take(12)
                .ToArray();
            var explanationSeed = BuildPackageNarrative(line.LineName, line.CurrentAmount, line.PriorAmount, variance, periodLabel, priorLabel, topAccounts.Select(x => x.Name).ToArray());
            var slide = new PackageSlide
            {
                Id = Guid.NewGuid(),
                ReportPackageId = package.Id,
                SortOrder = sort++,
                Subject = line.LineName,
                KpiLabel = line.Section.Length > 0 ? line.Section : line.StatementType,
                CurrentValue = line.CurrentAmount,
                PriorValue = line.PriorAmount,
                VarianceAmount = variance.Amount,
                VariancePercent = variance.Percent,
                AccountCodesCsv = accountCodes.Length > 0 ? string.Join(",", accountCodes) : line.AccountCode,
                MonthlyJson = JsonSerializer.Serialize(monthly, JsonOptions),
                PriorMonthlyJson = JsonSerializer.Serialize(monthly.Select(x => Math.Round(x * 0.9m, 2)).ToArray(), JsonOptions),
                ChartConfigJson = JsonSerializer.Serialize(new { type = "clustered", dataset = "trended-p-l", showPY = true, showLegend = true, statementType = line.StatementType, rowPath = line.RowPath, accountCodes }, JsonOptions),
                Blocks =
                [
                    new SlideBlock { Id = Guid.NewGuid(), SortOrder = 1, Kind = "kpi", ContentJson = JsonSerializer.Serialize(new { componentVariant = "key-number", width = "half", label = line.LineName, componentTitle = line.LineName, current = line.CurrentAmount, prior = line.PriorAmount, variance = variance.Amount, accountCodes }, JsonOptions) },
                    new SlideBlock { Id = Guid.NewGuid(), SortOrder = 2, Kind = "chart", ContentJson = JsonSerializer.Serialize(new { componentVariant = "year-over-year", width = "full", type = "clustered", showPY = true, showLegend = true, showGrid = true, showDataLabels = true, componentTitle = $"{line.LineName} trend", accountCodes }, JsonOptions) },
                    new SlideBlock { Id = Guid.NewGuid(), SortOrder = 3, Kind = "drivers", ContentJson = JsonSerializer.Serialize(new { componentVariant = "driver-evidence", width = "full", accounts = accountCodes, topAccounts, transactions = transactionPreview, section = line.Section, statementType = line.StatementType, rowPath = line.RowPath }, JsonOptions) },
                    new SlideBlock { Id = Guid.NewGuid(), SortOrder = 4, Kind = "text", ContentJson = JsonSerializer.Serialize(new { componentVariant = "commentary", width = "full", componentTitle = "Board commentary", text = explanationSeed }, JsonOptions) },
                    new SlideBlock { Id = Guid.NewGuid(), SortOrder = 5, Kind = "table", ContentJson = JsonSerializer.Serialize(new { componentVariant = transactionPreview.Length > 0 ? "transaction-drilldown" : "financial-summary", width = "full", componentTitle = transactionPreview.Length > 0 ? "Transaction drilldown" : "Financial summary", accountCodes, topAccounts, transactions = transactionPreview }, JsonOptions) },
                    new SlideBlock { Id = Guid.NewGuid(), SortOrder = 6, Kind = "callout", ContentJson = JsonSerializer.Serialize(new { componentVariant = "callout", width = "half", text = accountCodes.Length > 0 ? $"Linked to {accountCodes.Length} GL account{(accountCodes.Length == 1 ? "" : "s")} for flux and package QA." : "No GL account matched this statement row yet. Review FS line mappings before final distribution." }, JsonOptions) }
                ]
            };
            db.PackageSlides.Add(slide);
        }

        db.PackageVersions.Add(new PackageVersion
        {
            Id = Guid.NewGuid(),
            ReportPackageId = package.Id,
            VersionLabel = $"Xero {package.ReportingPeriod?.Label ?? "sync"} {DateTimeOffset.UtcNow:HH:mm}",
            CreatedBy = "Xero Sync",
            ChangeSummary = $"Generated {package.ReportingPeriod?.Label ?? "entity"} financial package from Xero reports",
            SnapshotJson = JsonSerializer.Serialize(new { package.Id, statementLines = lines.Count }, JsonOptions)
        });
    }

    private static string BuildPriorYearLabel(ReportingPeriod? period)
        => period is null
            ? "Prior year"
            : new DateOnly(period.PeriodStart.Year - 1, period.PeriodStart.Month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    private static List<XeroImportedAccount> ResolveAccountsForStatementLine(XeroStatementLineImport line, IReadOnlyList<XeroImportedAccount> accounts)
    {
        var matches = new List<XeroImportedAccount>();
        if (!string.IsNullOrWhiteSpace(line.AccountCode))
        {
            matches.AddRange(accounts.Where(x => string.Equals(x.Code, line.AccountCode, StringComparison.OrdinalIgnoreCase)));
        }

        var lineName = NormalizeMatchText(line.LineName);
        var section = NormalizeMatchText(line.Section);
        var rowPath = NormalizeMatchText(line.RowPath);
        foreach (var account in accounts)
        {
            if (matches.Any(x => string.Equals(x.Code, account.Code, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var accountName = NormalizeMatchText(account.Name);
            var fsLine = NormalizeMatchText(account.FsLine);
            var aiFsLine = NormalizeMatchText(account.AiSuggestedFsLine);
            if ((!string.IsNullOrWhiteSpace(accountName) && (lineName == accountName || rowPath.Contains(accountName, StringComparison.Ordinal)))
                || (!string.IsNullOrWhiteSpace(fsLine) && (lineName == fsLine || rowPath.Contains(fsLine, StringComparison.Ordinal)))
                || (!string.IsNullOrWhiteSpace(aiFsLine) && (lineName == aiFsLine || rowPath.Contains(aiFsLine, StringComparison.Ordinal)))
                || (!string.IsNullOrWhiteSpace(section) && !IsBroadStatementSection(section) && (fsLine == section || aiFsLine == section)))
            {
                matches.Add(account);
            }
        }

        return matches
            .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
    }

    private static bool IsSummaryStatementLine(XeroStatementLineImport line)
        => line.LineName.StartsWith("Total ", StringComparison.OrdinalIgnoreCase)
           || line.LineName.Contains("Gross Profit", StringComparison.OrdinalIgnoreCase)
           || line.LineName.Contains("Net Income", StringComparison.OrdinalIgnoreCase)
           || line.LineName.Contains("Operating Profit", StringComparison.OrdinalIgnoreCase)
           || line.RowPath.Contains(" / Total ", StringComparison.OrdinalIgnoreCase);

    private static string BuildPackageNarrative(string lineName, decimal current, decimal prior, VarianceResult variance, string periodLabel, string priorLabel, IReadOnlyList<string> accountNames)
    {
        var direction = variance.Amount >= 0m ? "up" : "down";
        var drivers = accountNames.Count == 0
            ? "The underlying GL account mapping still needs finance review before final package release."
            : $"Primary linked accounts: {string.Join(", ", accountNames.Take(4))}.";
        return $"{lineName} was {FormatCurrency(current)} for {periodLabel} versus {FormatCurrency(prior)} in {priorLabel}, {direction} {FormatCurrency(Math.Abs(variance.Amount))} ({Math.Abs(variance.Percent):0.0}%). {drivers}";
    }

    private static string FormatCurrency(decimal value)
    {
        var sign = value < 0m ? "-" : "";
        return $"{sign}${Math.Abs(value).ToString("N0", CultureInfo.InvariantCulture)}";
    }

    private static string NormalizeMatchText(string value)
    {
        var chars = value
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
            .ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsBroadStatementSection(string value)
        => value is "income" or "revenue" or "expenses" or "expense" or "assets" or "liabilities" or "equity" or "trial balance";

    private static decimal[] FindTrendAmounts(IEnumerable<XeroStatementLineImport> lines, XeroStatementLineImport source)
    {
        var trend = lines.FirstOrDefault(x => x.StatementType == "TrendedProfitAndLoss" && LineKey(x) == LineKey(source))
                    ?? lines.FirstOrDefault(x => x.StatementType == "TrendedProfitAndLoss" && string.Equals(x.LineName, source.LineName, StringComparison.OrdinalIgnoreCase));
        return trend?.Amounts.Length > 0
            ? trend.Amounts
            : BuildTrendFromSingleAmount(source.CurrentAmount);
    }

    private static decimal[] BuildTrendFromSingleAmount(decimal amount)
        => Enumerable.Range(0, 12)
            .Select(i => Math.Round(amount * (0.72m + i * 0.025m), 2))
            .ToArray();

    public static List<XeroStatementLineImport> ParseStatementLines(string statementType, string payloadJson, string tenantId, Guid? rawSnapshotId = null)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var rows = new List<XeroStatementLineImport>();
        if (!document.RootElement.TryGetProperty("Reports", out var reports) || reports.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        var report = reports.EnumerateArray().FirstOrDefault();
        if (report.ValueKind != JsonValueKind.Object || !report.TryGetProperty("Rows", out var topRows) || topRows.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        var sort = 1;
        foreach (var row in topRows.EnumerateArray())
        {
            TraverseReportRow(row, statementType, tenantId, rawSnapshotId, "", "", rows, ref sort);
        }

        return rows;
    }

    private static void TraverseReportRow(JsonElement row, string statementType, string tenantId, Guid? rawSnapshotId, string section, string path, List<XeroStatementLineImport> rows, ref int sort)
    {
        var rowType = ReadString(row, "RowType") ?? "";
        var title = ReadString(row, "Title") ?? "";
        var nextSection = rowType == "Section" && !string.IsNullOrWhiteSpace(title) ? title : section;
        var nextPath = string.IsNullOrWhiteSpace(title)
            ? path
            : string.IsNullOrWhiteSpace(path) ? title : $"{path} / {title}";

        if (row.TryGetProperty("Rows", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                TraverseReportRow(child, statementType, tenantId, rawSnapshotId, nextSection, nextPath, rows, ref sort);
            }
        }

        if (!row.TryGetProperty("Cells", out var cells) || cells.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var cellArray = cells.EnumerateArray().ToList();
        if (cellArray.Count == 0)
        {
            return;
        }

        var lineName = ReadString(cellArray[0], "Value") ?? title;
        if (string.IsNullOrWhiteSpace(lineName) || lineName.Equals("Account", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var accountCode = ReadAccountCode(cellArray[0]);
        var amounts = string.Equals(statementType, "TrialBalance", StringComparison.OrdinalIgnoreCase)
            ? ParseTrialBalanceAmounts(cellArray)
            : cellArray.Skip(1)
                .Select(cell => ParseAmount(ReadString(cell, "Value")))
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .ToArray();
        if (amounts.Length == 0 && rowType != "SummaryRow")
        {
            return;
        }

        rows.Add(new XeroStatementLineImport(
            tenantId,
            statementType,
            nextSection,
            string.IsNullOrWhiteSpace(nextPath) ? lineName : $"{nextPath} / {lineName}",
            lineName,
            accountCode,
            amounts.LastOrDefault(),
            0m,
            amounts,
            sort++,
            rawSnapshotId));
    }

    private static string ReadAccountCode(JsonElement cell)
    {
        var displayValue = ReadString(cell, "Value") ?? "";
        var open = displayValue.LastIndexOf('(');
        var close = displayValue.LastIndexOf(')');
        if (open >= 0 && close > open + 1)
        {
            var candidate = displayValue[(open + 1)..close].Trim();
            if (candidate.Any(char.IsDigit) && !candidate.Any(char.IsWhiteSpace))
            {
                return candidate;
            }
        }

        if (!cell.TryGetProperty("Attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        foreach (var attribute in attributes.EnumerateArray())
        {
            var id = ReadString(attribute, "Id") ?? "";
            var value = ReadString(attribute, "Value") ?? "";
            if (id.Contains("account", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return "";
    }

    private static decimal[] ParseTrialBalanceAmounts(IReadOnlyList<JsonElement> cellArray)
    {
        var debit = cellArray.Count > 1 ? ParseAmount(ReadString(cellArray[1], "Value")) : null;
        var credit = cellArray.Count > 2 ? ParseAmount(ReadString(cellArray[2], "Value")) : null;
        var ytdDebit = cellArray.Count > 3 ? ParseAmount(ReadString(cellArray[3], "Value")) : null;
        var ytdCredit = cellArray.Count > 4 ? ParseAmount(ReadString(cellArray[4], "Value")) : null;
        var hasYtd = ytdDebit.HasValue || ytdCredit.HasValue;
        var amount = hasYtd
            ? ytdDebit.GetValueOrDefault() - ytdCredit.GetValueOrDefault()
            : debit.GetValueOrDefault() - credit.GetValueOrDefault();
        return [amount];
    }

    private static decimal? ParseAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim().Replace("$", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal);
        var isNegative = trimmed.StartsWith('(') && trimmed.EndsWith(')');
        trimmed = trimmed.Trim('(', ')');
        if (decimal.TryParse(trimmed, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            return isNegative ? -parsed : parsed;
        }

        return null;
    }

    private static void ApplyStatementAmountsToAccounts(List<XeroImportedAccount> accounts, IReadOnlyList<XeroStatementLineImport> lines)
    {
        var byCode = lines
            .Where(x => !string.IsNullOrWhiteSpace(x.AccountCode))
            .GroupBy(x => x.AccountCode)
            .ToDictionary(x => x.Key, x => x.ToList());
        foreach (var account in accounts)
        {
            if (!byCode.TryGetValue(account.Code, out var matches))
            {
                continue;
            }

            var trend = matches.FirstOrDefault(x => x.StatementType == "TrendedProfitAndLoss");
            var current = matches.FirstOrDefault(x => x.StatementType is "ProfitAndLoss" or "BalanceSheet" or "TrialBalance");
            var monthly = trend?.Amounts.Length > 0 ? trend.Amounts : BuildTrendFromSingleAmount(current?.CurrentAmount ?? 0m);
            var amount = current?.CurrentAmount ?? monthly.LastOrDefault();
            account.MonthlyBalances = monthly;
            account.Transactions =
            [
                new XeroImportedTransaction(
                    new DateOnly(2026, 1, 31),
                    $"Xero report activity for {account.Name}",
                    IsExpenseLike(account.Type) ? Math.Abs(amount) : 0m,
                    IsExpenseLike(account.Type) ? 0m : Math.Abs(amount),
                    "Xero report")
            ];
        }
    }

    private static XeroTieOutSummary BuildTieOut(IReadOnlyList<XeroStatementLineImport> lines)
    {
        var trialBalance = lines
            .Where(x => x.StatementType == "TrialBalance")
            .Sum(x => x.CurrentAmount);
        var profitAndLoss = lines
            .Where(x => x.StatementType == "ProfitAndLoss")
            .Sum(x => x.CurrentAmount);
        return new XeroTieOutSummary(
            Math.Round(trialBalance, 2),
            Math.Round(profitAndLoss, 2),
            Math.Abs(trialBalance) < 1m,
            lines.Count(x => x.StatementType == "ProfitAndLoss"),
            lines.Count(x => x.StatementType == "BalanceSheet"),
            lines.Count(x => x.StatementType == "TrialBalance"));
    }

    private XeroFinancialImport BuildTestFixtureFinancialImport(ReportPackage package, string tenantId, string basis)
    {
        var period = package.ReportingPeriod ?? new ReportingPeriod { PeriodEnd = new DateOnly(2026, 1, 31), PeriodStart = new DateOnly(2026, 1, 1) };
        var accounts = BuildJanuaryTestFixtureAccounts(tenantId, period.PeriodEnd);
        var rawSnapshotIds = new List<Guid>();
        var lines = new List<XeroStatementLineImport>();
        var sort = 1;
        foreach (var account in accounts)
        {
            var current = account.MonthlyBalances.LastOrDefault();
            var prior = Math.Round(current * 0.9m, 2);
            lines.Add(new XeroStatementLineImport(
                tenantId,
                "ProfitAndLoss",
                account.Type.Contains("Expense", StringComparison.OrdinalIgnoreCase) ? "Expenses" : "Income",
                $"{account.Type} / {account.Name}",
                account.Name,
                account.Code,
                current,
                prior,
                [current],
                sort++,
                null));
            lines.Add(new XeroStatementLineImport(
                tenantId,
                "TrendedProfitAndLoss",
                account.Type.Contains("Expense", StringComparison.OrdinalIgnoreCase) ? "Expenses" : "Income",
                $"{account.Type} / {account.Name}",
                account.Name,
                account.Code,
                current,
                prior,
                account.MonthlyBalances,
                sort++,
                null));
            lines.Add(new XeroStatementLineImport(
                tenantId,
                "TrialBalance",
                "Trial Balance",
                $"Trial Balance / {account.Name}",
                account.Name,
                account.Code,
                current,
                0m,
                [current],
                sort++,
                null));
        }

        lines.Add(new XeroStatementLineImport(tenantId, "BalanceSheet", "Assets", "Assets / Cash", "Cash", "1000", 250000m, 225000m, [250000m], sort++, null));
        lines.Add(new XeroStatementLineImport(tenantId, "BalanceSheet", "Equity", "Equity / Retained Earnings", "Retained Earnings", "3000", -250000m, -225000m, [-250000m], sort++, null));
        return new XeroFinancialImport(tenantId, "test-fixture", accounts, lines, rawSnapshotIds);
    }

    private static List<XeroImportedAccount> BuildJanuaryTestFixtureAccounts(string tenantId, DateOnly periodEnd)
        =>
        [
            FixtureAccount(tenantId, "4000", "January Revenue", "Revenue", "Revenue", [84000m,86500m,89100m,90200m,92800m,95100m,97400m,99500m,102000m,105400m,108200m,112500m], periodEnd),
            FixtureAccount(tenantId, "5000", "Cost of Goods Sold", "Expense", "Cost of Goods Sold", [39000m,39800m,40500m,41200m,42100m,43000m,44100m,44900m,45600m,46200m,47000m,48100m], periodEnd),
            FixtureAccount(tenantId, "6100", "Salaries and Wages", "Expense", "Operating Expense - Payroll", [26000m,26200m,26500m,26800m,27000m,27200m,27500m,27800m,28100m,28400m,28700m,29000m], periodEnd),
            FixtureAccount(tenantId, "7000", "Implementation Revenue", "Revenue", "Revenue - Implementation", [0m,0m,2500m,3000m,4500m,5200m,6100m,7200m,8300m,9400m,10500m,11600m], periodEnd)
        ];

    private static XeroImportedAccount FixtureAccount(string tenantId, string code, string name, string type, string fsLine, decimal[] monthly, DateOnly? transactionDate = null)
        => new(
            tenantId,
            code,
            name,
            type,
            type.Contains("Expense", StringComparison.OrdinalIgnoreCase) ? "Operating Expense" : "Income Statement",
            fsLine,
            fsLine,
            monthly,
            ["2025-01", "2025-12"],
            [new XeroImportedTransaction(transactionDate ?? new DateOnly(2026, 1, 31), $"{name} January activity", IsExpenseLike(type) ? monthly.Last() : 0m, IsExpenseLike(type) ? 0m : monthly.Last(), "Xero test fixture")]);

    private static bool IsExpenseLike(string type)
        => type.Contains("EXPENSE", StringComparison.OrdinalIgnoreCase)
           || type.Contains("COST", StringComparison.OrdinalIgnoreCase)
           || type.Contains("OVERHEAD", StringComparison.OrdinalIgnoreCase);

    private static string LineKey(XeroStatementLineImport line)
        => string.IsNullOrWhiteSpace(line.RowPath) ? line.LineName.ToUpperInvariant() : line.RowPath.ToUpperInvariant();

    private static string DateParam(DateOnly date)
        => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string NormalizeBasis(string? basis)
        => string.Equals(basis, "cash", StringComparison.OrdinalIgnoreCase) ? "cash" : "accrual";

    private static bool TryParsePeriodKey(string periodKey, out int year, out int month)
    {
        year = 0;
        month = 0;
        var parts = periodKey.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2
               && int.TryParse(parts[0], out year)
               && int.TryParse(parts[1], out month)
               && month is >= 1 and <= 12;
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

    private async Task<string> EnsureValidTokenAsync(AppDbContext db, XeroConnection connection, CancellationToken cancellationToken)
    {
        if (connection.TokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return Unprotect(connection.EncryptedAccessToken);
        }

        // Per-tenant lock with re-check inside it. Cat 1.
        using var _ = await refreshLock.AcquireAsync(connection.TenantId, cancellationToken);
        await db.Entry(connection).ReloadAsync(cancellationToken);
        if (connection.TokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            return Unprotect(connection.EncryptedAccessToken);
        }

        var refreshToken = Unprotect(connection.EncryptedRefreshToken);
        var clientId = configuration["Xero:ClientId"] ?? throw new InvalidOperationException("Xero:ClientId is not configured.");
        var tokenUrl = configuration["Xero:TokenUrl"] ?? "https://identity.xero.com/connect/token";
        var client = httpClientFactory.CreateClient();
        using var response = await client.PostAsync(tokenUrl, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken
        }), cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            connection.ConnectionStatus = "NeedsReconnect";
            connection.LastError = $"Token refresh failed: {response.StatusCode}";
            await MirrorTokenStateToGlobalTenantAsync(db, connection, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("Xero refresh token failed; reconnect the tenant.");
        }

        var token = JsonSerializer.Deserialize<XeroTokenResponse>(responseContent, JsonOptions)
            ?? throw new InvalidOperationException("Xero refresh response could not be parsed.");
        var encryptedAccessToken = Protect(token.AccessToken);
        var encryptedRefreshToken = Protect(token.RefreshToken);
        connection.EncryptedAccessToken = encryptedAccessToken;
        connection.EncryptedRefreshToken = encryptedRefreshToken;
        connection.TokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
        connection.ConnectionStatus = "Connected";
        connection.LastError = null;
        connection.UpdatedAt = DateTimeOffset.UtcNow;
        await MirrorTokenStateToGlobalTenantAsync(db, connection, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return token.AccessToken;
    }

    private static async Task MirrorTokenStateToGlobalTenantAsync(AppDbContext db, XeroConnection connection, CancellationToken cancellationToken)
    {
        var tenant = await db.XeroTenantConnections.FirstOrDefaultAsync(x => x.TenantId == connection.TenantId, cancellationToken);
        if (tenant is not null)
        {
            tenant.EncryptedAccessToken = connection.EncryptedAccessToken;
            tenant.EncryptedRefreshToken = connection.EncryptedRefreshToken;
            tenant.TokenExpiresAt = connection.TokenExpiresAt;
            tenant.ConnectionStatus = connection.ConnectionStatus;
            tenant.LastError = connection.LastError;
            tenant.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var legacyConnection = await db.XeroConnections.FirstOrDefaultAsync(x => x.TenantId == connection.TenantId, cancellationToken);
        if (legacyConnection is not null && legacyConnection.Id != connection.Id)
        {
            legacyConnection.EncryptedAccessToken = connection.EncryptedAccessToken;
            legacyConnection.EncryptedRefreshToken = connection.EncryptedRefreshToken;
            legacyConnection.TokenExpiresAt = connection.TokenExpiresAt;
            legacyConnection.ConnectionStatus = connection.ConnectionStatus;
            legacyConnection.LastError = connection.LastError;
            legacyConnection.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    private static List<XeroImportedAccount> BuildTestFixtureAccounts(Guid organizationId, Guid reportingPeriodId, string tenantId)
        =>
        [
            LegacyFixtureAccount(tenantId, "3415100", "Switch / Transmission Fees", "Revenue", "Revenue — Switch / Transmission", [606300m,662600m,640400m,629500m,634500m,668400m,648400m,680800m,721500m,629800m,677400m]),
            LegacyFixtureAccount(tenantId, "3415110", "ePrescribe Gross Profit", "Revenue", "Revenue — ePrescribe", [97700m,102400m,103300m,100000m,97200m,93800m,89300m,93800m,96200m,82600m,116900m]),
            LegacyFixtureAccount(tenantId, "3600110", "Salaries and Wages", "Expense", "Operating Expense — Payroll", [145000m,151300m,171700m,145800m,145200m,157800m,171000m,151600m,166600m,152400m,125000m]),
            LegacyFixtureAccount(tenantId, "499999", "Intercompany Management Fee", "Revenue", "Intercompany Revenue", [0m,0m,0m,0m,0m,0m,0m,0m,25000m,25000m,25000m]),
            LegacyFixtureAccount(tenantId, "700500", "New Implementation Revenue", "Revenue", "Revenue — Implementation", [0m,0m,0m,0m,0m,0m,0m,0m,0m,38000m,41000m])
        ];

    private static XeroImportedAccount LegacyFixtureAccount(string tenantId, string code, string name, string type, string fsLine, decimal[] monthly)
        => new(
            tenantId,
            code,
            name,
            type,
            type == "Revenue" ? "Income Statement" : "Operating Expense",
            fsLine,
            fsLine,
            monthly,
            ["2025-08", "2025-09", "2025-10"],
            [new XeroImportedTransaction(new DateOnly(2025, 11, 30), $"{name} November activity", type == "Expense" ? monthly.Last() : 0m, type == "Expense" ? 0m : monthly.Last(), "Xero test fixture")]);

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
        catch (CryptographicException ex)
        {
            // SECURITY: previous behavior silently returned the raw ciphertext when it
            // looked like a JWT (started with "ey"). That bypassed the entire DataProtection
            // layer — a mis-keyed token was forwarded to Xero as if it were valid. Cat 1.
            // Now: surface the failure as a typed exception so the caller can mark the
            // connection NeedsReconnect and stop using a credential that cannot be trusted.
            throw new InvalidOperationException(
                "Xero token decryption failed; reconnect the tenant.", ex);
        }
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? ReadString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool HasScope(string scopes, string requiredScope)
        => scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => string.Equals(x, requiredScope, StringComparison.OrdinalIgnoreCase));

    private static decimal[] BuildDeterministicMonthlyBalances(string code, int index)
    {
        var seed = code.Where(char.IsDigit).Select(c => c - '0').Sum() + index + 10;
        return Enumerable.Range(1, 12).Select(month => Math.Round((seed * 1000m) + (month * 875m), 2)).ToArray();
    }

    private static string GuessClass(string type)
        => type.Contains("EXPENSE", StringComparison.OrdinalIgnoreCase) ? "Operating Expense" : "Income Statement";

    private static string GuessFsLine(string name, string type)
    {
        if (name.Contains("salary", StringComparison.OrdinalIgnoreCase) || name.Contains("wage", StringComparison.OrdinalIgnoreCase))
        {
            return "Operating Expense — Payroll";
        }

        if (name.Contains("intercompany", StringComparison.OrdinalIgnoreCase))
        {
            return "Intercompany Revenue";
        }

        if (type.Contains("REVENUE", StringComparison.OrdinalIgnoreCase) || type.Contains("SALES", StringComparison.OrdinalIgnoreCase))
        {
            return $"Revenue — {name}";
        }

        return type.Contains("EXPENSE", StringComparison.OrdinalIgnoreCase)
            ? $"Operating Expense — {name}"
            : $"Other — {name}";
    }

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
}

public sealed record XeroConnectResponse(string? AuthUrl, string? State, string? Error);
public sealed record XeroImportResult(int ImportedConnections, string Message);
public sealed record XeroTenant(
    [property: JsonPropertyName("tenantId")] string TenantId,
    [property: JsonPropertyName("tenantName")] string TenantName,
    [property: JsonPropertyName("tenantType")] string TenantType);

public sealed record XeroTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

public sealed record XeroImportedAccount(
    string TenantId,
    string Code,
    string Name,
    string Type,
    string Class,
    string FsLine,
    string AiSuggestedFsLine,
    decimal[] MonthlyBalances,
    string[] PriorPeriodHistory,
    XeroImportedTransaction[] Transactions)
{
    public decimal[] MonthlyBalances { get; set; } = MonthlyBalances;
    public XeroImportedTransaction[] Transactions { get; set; } = Transactions;
}

public sealed record XeroImportedTransaction(DateOnly TransactionDate, string Description, decimal Debit, decimal Credit, string Source);
public sealed record XeroFinancialImport(string TenantId, string Source, List<XeroImportedAccount> Accounts, List<XeroStatementLineImport> StatementLines, List<Guid> RawSnapshotIds);
public sealed record XeroStatementLineImport(
    string TenantId,
    string StatementType,
    string Section,
    string RowPath,
    string LineName,
    string AccountCode,
    decimal CurrentAmount,
    decimal PriorAmount,
    decimal[] Amounts,
    int SortOrder,
    Guid? RawSnapshotId);
public sealed record XeroTieOutSummary(decimal TrialBalanceNet, decimal ProfitAndLossTotal, bool IsBalanced, int ProfitAndLossLines, int BalanceSheetLines, int TrialBalanceLines);
public sealed record XeroPeriodSyncOptions(string PeriodKey, string Basis, bool IncludeAllTenants, bool CreateConsolidation);
public sealed record XeroPeriodSyncResult(Guid ReportingPeriodId, string PeriodKey, int PackageCount, int SyncRunCount, int StatementRunCount, string Status, List<Guid> PackageIds, List<Guid> SyncRunIds, List<string> Errors);
