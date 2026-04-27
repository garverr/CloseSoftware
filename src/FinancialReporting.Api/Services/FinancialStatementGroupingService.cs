using System.Globalization;
using System.Text.RegularExpressions;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Services;

public sealed class FinancialStatementGroupingService(AppDbContext db)
{
    private const string GroupingReason = "Financial statement grouping pass based on imported Xero financials";

    public async Task<FinancialStatementGroupingResult> GroupFromImportedFinancialsAsync(
        Guid? organizationId,
        bool includeReviewed,
        CancellationToken cancellationToken)
    {
        var organizations = await db.Organizations
            .Where(x => organizationId == null || x.Id == organizationId.Value)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
        var organizationIds = organizations.Select(x => x.Id).ToList();

        if (organizationIds.Count == 0)
        {
            return new FinancialStatementGroupingResult(0, 0, 0, 0, 0, 0, []);
        }

        var statementRows = await db.FinancialStatementLines
            .AsNoTracking()
            .Where(x => organizationIds.Contains(x.OrganizationId)
                        && (x.StatementType == "ProfitAndLoss"
                            || x.StatementType == "TrendedProfitAndLoss"
                            || x.StatementType == "TrendedPL"
                            || x.StatementType == "BalanceSheet"))
            .Select(x => new StatementLineSeed(
                x.OrganizationId,
                x.StatementType,
                x.Section,
                x.LineName,
                x.AccountCode,
                x.SortOrder))
            .ToListAsync(cancellationToken);

        var candidates = statementRows
            .Select(ToCandidate)
            .Where(x => x is not null)
            .Cast<FsLineCandidate>()
            .GroupBy(x => new { x.OrganizationId, x.StatementType, x.NormalizedName })
            .Select(group =>
            {
                var best = group
                    .OrderByDescending(x => !string.IsNullOrWhiteSpace(x.Section))
                    .ThenBy(x => x.SortOrder)
                    .First();
                return best with
                {
                    Frequency = group.Count(),
                    SortOrder = group.Min(x => x.SortOrder)
                };
            })
            .OrderBy(x => x.OrganizationId)
            .ThenBy(x => x.StatementType)
            .ThenBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .ToList();

        var existingLines = await db.FsLineDefinitions
            .Where(x => organizationIds.Contains(x.OrganizationId))
            .ToListAsync(cancellationToken);
        var lineIndex = new Dictionary<string, FsLineDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in existingLines)
        {
            lineIndex[LineIndexKey(line)] = line;
        }
        var nextSortByOrgStatement = existingLines
            .GroupBy(x => $"{x.OrganizationId:N}|{x.StatementType}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Max(line => line.SortOrder) + 10, StringComparer.OrdinalIgnoreCase);

        var resultByOrg = organizations.ToDictionary(
            x => x.Id,
            x => new MutableGroupingOrgResult(x.Id, x.Key, x.Name));

        foreach (var candidate in candidates)
        {
            EnsureLineDefinition(candidate, lineIndex, nextSortByOrgStatement, resultByOrg);
        }

        var candidatesByOrgName = candidates
            .GroupBy(x => x.OrganizationId)
            .ToDictionary(
                x => x.Key,
                x => x.GroupBy(candidate => candidate.NormalizedName)
                    .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase));

        var accounts = await db.GlAccounts
            .Where(x => organizationIds.Contains(x.OrganizationId))
            .ToListAsync(cancellationToken);

        foreach (var account in accounts)
        {
            if (!includeReviewed
                && account.ReviewStatus == MappingReviewStatus.Reviewed
                && !account.AuditReason.StartsWith(GroupingReason, StringComparison.OrdinalIgnoreCase))
            {
                resultByOrg[account.OrganizationId].Unchanged++;
                continue;
            }

            candidatesByOrgName.TryGetValue(account.OrganizationId, out var orgCandidateMap);
            var match = MatchAccount(account, orgCandidateMap);
            if (match is null)
            {
                resultByOrg[account.OrganizationId].Unmatched++;
                continue;
            }

            EnsureLineDefinition(match.Candidate, lineIndex, nextSortByOrgStatement, resultByOrg);

            var changed = !string.Equals(account.FsLine, match.Candidate.Name, StringComparison.OrdinalIgnoreCase)
                          || !string.Equals(account.AiSuggestedFsLine, match.Candidate.Name, StringComparison.OrdinalIgnoreCase)
                          || account.MappingConfidence != match.Confidence;
            if (!changed)
            {
                resultByOrg[account.OrganizationId].Unchanged++;
                continue;
            }

            account.FsLine = match.Candidate.Name;
            account.AiSuggestedFsLine = match.Candidate.Name;
            account.MappingConfidence = match.Confidence;
            account.ReviewStatus = account.ReviewStatus == MappingReviewStatus.Reviewed
                ? MappingReviewStatus.Reviewed
                : account.IsFirstSeen ? MappingReviewStatus.New : MappingReviewStatus.Suggested;
            account.AuditReason = $"{GroupingReason}; source={match.Source}";
            account.UpdatedAt = DateTimeOffset.UtcNow;

            var orgResult = resultByOrg[account.OrganizationId];
            orgResult.AccountsUpdated++;
            if (match.Source == "statement")
            {
                orgResult.StatementMatched++;
            }
            else
            {
                orgResult.FallbackMatched++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        var orgResults = resultByOrg.Values
            .OrderBy(x => x.OrganizationName)
            .Select(x => x.ToResult())
            .ToList();

        return new FinancialStatementGroupingResult(
            orgResults.Sum(x => x.FsLinesCreated),
            orgResults.Sum(x => x.FsLinesReactivated),
            orgResults.Sum(x => x.AccountsUpdated),
            orgResults.Sum(x => x.StatementMatched),
            orgResults.Sum(x => x.FallbackMatched),
            orgResults.Sum(x => x.Unmatched),
            orgResults);
    }

    private static FsLineCandidate? ToCandidate(StatementLineSeed line)
    {
        var name = CleanLineName(line.LineName);
        if (string.IsNullOrWhiteSpace(name) || IsSummaryLine(name))
        {
            return null;
        }

        var statementType = NormalizeStatementType(line.StatementType);
        if (statementType is null)
        {
            return null;
        }

        var section = string.IsNullOrWhiteSpace(line.Section)
            ? InferSection(name, statementType)
            : CleanLineName(line.Section);

        return new FsLineCandidate(
            line.OrganizationId,
            statementType,
            section,
            name,
            NormalizeNormalBalance(null, statementType, section, name),
            Math.Max(line.SortOrder, 10),
            1,
            NormalizeName(name));
    }

    private static FsLineMatch? MatchAccount(
        GlAccount account,
        Dictionary<string, List<FsLineCandidate>>? candidatesByName)
    {
        var normalizedName = NormalizeName(account.Name);
        if (candidatesByName is not null && candidatesByName.TryGetValue(normalizedName, out var candidates))
        {
            return new FsLineMatch(ChooseBestCandidate(account, candidates), 0.95m, "statement");
        }

        var fallback = BuildFallbackCandidate(account);
        return fallback is null ? null : new FsLineMatch(fallback, 0.72m, "fallback");
    }

    private static FsLineCandidate ChooseBestCandidate(GlAccount account, List<FsLineCandidate> candidates)
    {
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var preferredStatement = InferPreferredStatementType(account);
        return candidates
            .OrderByDescending(x => string.Equals(x.StatementType, preferredStatement, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Frequency)
            .ThenBy(x => x.SortOrder)
            .First();
    }

    private static FsLineCandidate? BuildFallbackCandidate(GlAccount account)
    {
        var name = account.Name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var lower = $"{account.Code} {account.Name} {account.Type} {account.Class}".ToLowerInvariant();
        var statementType = InferPreferredStatementType(account);
        string section;
        string lineName;

        if (statementType == "BalanceSheet")
        {
            if (HasAny(lower, "intercompany", "i/c", "due to", "due from"))
            {
                section = "Intercompany";
                lineName = lower.Contains("payable", StringComparison.OrdinalIgnoreCase) || lower.Contains("due to", StringComparison.OrdinalIgnoreCase)
                    ? "Intercompany Payable"
                    : "Intercompany Receivable";
            }
            else if (HasAny(lower, "cash", "bank", "checking", "savings"))
            {
                section = "Cash and Cash Equivalents";
                lineName = "Cash and Cash Equivalents";
            }
            else if (HasAny(lower, "receivable", "a/r", "accounts receivable"))
            {
                section = "Current Assets";
                lineName = "Accounts Receivable";
            }
            else if (HasAny(lower, "prepaid", "prepayment"))
            {
                section = "Current Assets";
                lineName = "Prepaid Expenses";
            }
            else if (HasAny(lower, "inventory"))
            {
                section = "Current Assets";
                lineName = "Inventory";
            }
            else if (HasAny(lower, "equipment", "furniture", "fixed asset", "leasehold", "property"))
            {
                section = "Property, Plant and Equipment";
                lineName = "Property and Equipment";
            }
            else if (HasAny(lower, "accumulated depreciation"))
            {
                section = "Property, Plant and Equipment";
                lineName = "Accumulated Depreciation";
            }
            else if (HasAny(lower, "payable", "a/p", "accounts payable"))
            {
                section = "Current Liabilities";
                lineName = "Accounts Payable";
            }
            else if (HasAny(lower, "accrued", "liabil", "sales tax", "payroll liabilities", "unpaid"))
            {
                section = "Current Liabilities";
                lineName = "Accrued Expenses";
            }
            else if (HasAny(lower, "debt", "loan", "note"))
            {
                section = "Debt";
                lineName = "Debt";
            }
            else if (HasAny(lower, "equity", "retained", "member", "owner", "current year earnings"))
            {
                section = "Equity";
                lineName = name.Contains("current year", StringComparison.OrdinalIgnoreCase) ? "Current Year Earnings" : "Equity";
            }
            else
            {
                section = "Other Assets / Liabilities";
                lineName = $"Other Balance Sheet - {name}";
            }
        }
        else
        {
            var isExpenseSource = HasAny($"{account.Type} {account.Class}".ToLowerInvariant(), "expense", "directcost", "cost");
            if (HasAny(lower, "cost of goods sold", "cost of sales", "cost of revenue", "cogs", "claim", "rebate", "drug", "ingredient", "direct cost"))
            {
                section = "Cost of Revenue";
                lineName = "Cost of Revenue";
            }
            else if (HasAny(lower, "commission") && isExpenseSource)
            {
                section = "Operating Expenses";
                lineName = "Commissions";
            }
            else if (HasAny(lower, "utility", "utilities"))
            {
                section = "Operating Expenses";
                lineName = "Utilities";
            }
            else if (HasAny(lower, "telephone", "cellular phone", "phone"))
            {
                section = "Operating Expenses";
                lineName = "Telephone Expense";
            }
            else if (HasAny(lower, "revenue", "sales", "income", "fees", "commission"))
            {
                section = "Revenue";
                lineName = "Revenue";
            }
            else if (HasAny(lower, "salary", "salaries", "wage", "payroll"))
            {
                section = "Operating Expenses";
                lineName = lower.Contains("tax", StringComparison.OrdinalIgnoreCase) ? "Payroll Taxes" : "Salaries and Wages";
            }
            else if (HasAny(lower, "benefit", "health insurance", "health savings", "hsa", "401"))
            {
                section = "Operating Expenses";
                lineName = "Employee Benefits";
            }
            else if (HasAny(lower, "professional", "consulting", "legal", "accounting"))
            {
                section = "Operating Expenses";
                lineName = "Professional Services";
            }
            else if (HasAny(lower, "software", "internet", "online", "saas", "subscription"))
            {
                section = "Operating Expenses";
                lineName = "Software Expense";
            }
            else if (HasAny(lower, "rent", "lease"))
            {
                section = "Operating Expenses";
                lineName = "Rent";
            }
            else if (HasAny(lower, "travel", "meals", "entertainment"))
            {
                section = "Operating Expenses";
                lineName = "Travel and Meals";
            }
            else if (HasAny(lower, "insurance", "e&o"))
            {
                section = "Operating Expenses";
                lineName = "Insurance Expense";
            }
            else if (HasAny(lower, "depreciation", "amortization"))
            {
                section = "Operating Expenses";
                lineName = "Depreciation and Amortization";
            }
            else if (HasAny(lower, "tax"))
            {
                section = "Operating Expenses";
                lineName = "Taxes";
            }
            else if (HasAny(lower, "interest"))
            {
                section = "Other Income / Expense";
                lineName = "Interest Expense";
            }
            else
            {
                section = "Operating Expenses";
                lineName = "Other Operating Expenses";
            }
        }

        return new FsLineCandidate(
            account.OrganizationId,
            statementType,
            section,
            lineName,
            NormalizeNormalBalance(null, statementType, section, lineName),
            9000,
            1,
            NormalizeName(lineName));
    }

    private static string InferPreferredStatementType(GlAccount account)
    {
        var name = account.Name.ToLowerInvariant();
        if (HasAny(name, "cash", "bank", "receivable", "prepaid", "prepayment", "inventory", "equipment", "furniture", "fixed asset", "leasehold", "accumulated depreciation", "payable", "accrued", "liabil", "debt", "loan", "note", "equity", "retained", "member", "owner", "current year earnings", "intercompany", "i/c", "due to", "due from"))
        {
            return "BalanceSheet";
        }

        var source = $"{account.Type} {account.Class}".ToLowerInvariant();
        if (HasAny(source, "asset", "liability", "equity", "bank", "current", "prepayment", "fixed"))
        {
            return "BalanceSheet";
        }

        return "IncomeStatement";
    }

    private void EnsureLineDefinition(
        FsLineCandidate candidate,
        Dictionary<string, FsLineDefinition> lineIndex,
        Dictionary<string, int> nextSortByOrgStatement,
        Dictionary<Guid, MutableGroupingOrgResult> resultByOrg)
    {
        var key = LineIndexKey(candidate.OrganizationId, candidate.StatementType, candidate.Name);
        if (lineIndex.TryGetValue(key, out var existing))
        {
            if (!existing.IsActive)
            {
                existing.IsActive = true;
                existing.Section = candidate.Section;
                existing.NormalBalance = candidate.NormalBalance;
                existing.AiGuidance = BuildGuidance(candidate);
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                resultByOrg[candidate.OrganizationId].FsLinesReactivated++;
            }

            return;
        }

        var sortKey = $"{candidate.OrganizationId:N}|{candidate.StatementType}";
        var sortOrder = candidate.SortOrder > 0 && candidate.SortOrder < 9000
            ? candidate.SortOrder
            : nextSortByOrgStatement.GetValueOrDefault(sortKey, 10);
        nextSortByOrgStatement[sortKey] = Math.Max(nextSortByOrgStatement.GetValueOrDefault(sortKey, 10), sortOrder + 10);

        var line = new FsLineDefinition
        {
            Id = Guid.NewGuid(),
            OrganizationId = candidate.OrganizationId,
            StatementType = candidate.StatementType,
            Section = candidate.Section,
            Name = candidate.Name,
            NormalBalance = candidate.NormalBalance,
            AiGuidance = BuildGuidance(candidate),
            SortOrder = sortOrder,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.FsLineDefinitions.Add(line);
        lineIndex[key] = line;
        resultByOrg[candidate.OrganizationId].FsLinesCreated++;
    }

    private static string BuildGuidance(FsLineCandidate candidate)
        => candidate.SortOrder >= 9000
            ? $"Fallback grouping created by imported financials pass. Use for accounts that match {candidate.Name}."
            : $"Imported from Xero financial statement line in {candidate.Section}. Use for accounts that roll to {candidate.Name}.";

    private static bool IsSummaryLine(string name)
    {
        var normalized = NormalizeName(name);
        if (normalized.StartsWith("total ", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("less ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalized is "gross profit"
            or "operating income loss"
            or "operating income"
            or "net income"
            or "net income loss"
            or "net income loss before tax"
            or "net profit"
            or "net profit loss"
            or "total comprehensive income"
            or "profit for the year";
    }

    private static string? NormalizeStatementType(string statementType)
    {
        if (statementType.Equals("BalanceSheet", StringComparison.OrdinalIgnoreCase)
            || statementType.Equals("Balance Sheet", StringComparison.OrdinalIgnoreCase))
        {
            return "BalanceSheet";
        }

        if (statementType.Equals("ProfitAndLoss", StringComparison.OrdinalIgnoreCase)
            || statementType.Equals("Profit and Loss", StringComparison.OrdinalIgnoreCase)
            || statementType.Equals("TrendedPL", StringComparison.OrdinalIgnoreCase)
            || statementType.Equals("TrendedProfitAndLoss", StringComparison.OrdinalIgnoreCase))
        {
            return "IncomeStatement";
        }

        return null;
    }

    private static string InferSection(string name, string statementType)
    {
        if (statementType == "BalanceSheet")
        {
            var lower = name.ToLowerInvariant();
            if (HasAny(lower, "payable", "liability", "accrued", "debt", "loan"))
            {
                return "Liabilities";
            }

            if (HasAny(lower, "equity", "retained", "earnings", "member"))
            {
                return "Equity";
            }

            return "Assets";
        }

        if (HasAny(name.ToLowerInvariant(), "revenue", "sales", "income"))
        {
            return "Revenue";
        }

        if (HasAny(name.ToLowerInvariant(), "cost", "cogs"))
        {
            return "Cost of Revenue";
        }

        return "Operating Expenses";
    }

    private static string NormalizeNormalBalance(string? normalBalance, string statementType, string section, string name)
    {
        if (string.Equals(normalBalance, "Debit", StringComparison.OrdinalIgnoreCase) || string.Equals(normalBalance, "Credit", StringComparison.OrdinalIgnoreCase))
        {
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalBalance!.ToLowerInvariant());
        }

        var text = $"{statementType} {section} {name}".ToLowerInvariant();
        if (statementType == "BalanceSheet")
        {
            return HasAny(text, "liability", "payable", "equity", "retained", "earnings", "debt", "loan")
                ? "Credit"
                : "Debit";
        }

        if (HasAny(text, "tax", "expense", "cost", "salary", "salaries", "wage", "payroll", "rent", "insurance", "depreciation", "amortization", "travel", "meals", "dues", "office", "software", "professional"))
        {
            return "Debit";
        }

        return HasAny(text, "revenue", "sales", "income", "fees", "commission")
            ? "Credit"
            : "Debit";
    }

    private static string CleanLineName(string value)
        => Regex.Replace(value.Trim(), @"\s+", " ");

    private static string NormalizeName(string value)
    {
        var withoutCode = Regex.Replace(value, @"\s*\([A-Za-z0-9._-]+\)\s*$", "");
        var normalized = withoutCode.Replace("&", " and ", StringComparison.Ordinal);
        normalized = Regex.Replace(normalized, @"[^A-Za-z0-9]+", " ").Trim().ToLowerInvariant();
        return Regex.Replace(normalized, @"\s+", " ");
    }

    private static bool HasAny(string source, params string[] terms)
        => terms.Any(term => source.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string LineIndexKey(FsLineDefinition line)
        => LineIndexKey(line.OrganizationId, line.StatementType, line.Name);

    private static string LineIndexKey(Guid organizationId, string statementType, string name)
        => $"{organizationId:N}|{statementType}|{NormalizeName(name)}";

    private sealed record StatementLineSeed(Guid OrganizationId, string StatementType, string Section, string LineName, string AccountCode, int SortOrder);
    private sealed record FsLineCandidate(Guid OrganizationId, string StatementType, string Section, string Name, string NormalBalance, int SortOrder, int Frequency, string NormalizedName);
    private sealed record FsLineMatch(FsLineCandidate Candidate, decimal Confidence, string Source);

    private sealed class MutableGroupingOrgResult(Guid organizationId, string organizationKey, string organizationName)
    {
        public Guid OrganizationId { get; } = organizationId;
        public string OrganizationKey { get; } = organizationKey;
        public string OrganizationName { get; } = organizationName;
        public int FsLinesCreated { get; set; }
        public int FsLinesReactivated { get; set; }
        public int AccountsUpdated { get; set; }
        public int StatementMatched { get; set; }
        public int FallbackMatched { get; set; }
        public int Unmatched { get; set; }
        public int Unchanged { get; set; }

        public FinancialStatementGroupingOrgResult ToResult()
            => new(OrganizationId, OrganizationKey, OrganizationName, FsLinesCreated, FsLinesReactivated, AccountsUpdated, StatementMatched, FallbackMatched, Unmatched, Unchanged);
    }

}

public sealed record FinancialStatementGroupingResult(
    int FsLinesCreated,
    int FsLinesReactivated,
    int AccountsUpdated,
    int StatementMatched,
    int FallbackMatched,
    int Unmatched,
    List<FinancialStatementGroupingOrgResult> Organizations);

public sealed record FinancialStatementGroupingOrgResult(
    Guid OrganizationId,
    string OrganizationKey,
    string OrganizationName,
    int FsLinesCreated,
    int FsLinesReactivated,
    int AccountsUpdated,
    int StatementMatched,
    int FallbackMatched,
    int Unmatched,
    int Unchanged);
