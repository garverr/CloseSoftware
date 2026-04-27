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

namespace FinancialReporting.Api.Features.Mapping;

public static class MappingEndpoints
{
    public static IEndpointRouteBuilder MapMappingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/mapping/accounts", async (
            string? organizationKey,
            string? periodKey,
            string? status,
            bool? firstSeen,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var query = db.GlAccounts.AsNoTracking()
                .Include(x => x.Transactions)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(organizationKey))
            {
                var organizationId = await db.Organizations
                    .AsNoTracking()
                    .Where(x => x.Key == organizationKey)
                    .Select(x => (Guid?)x.Id)
                    .FirstOrDefaultAsync(ct);
                if (organizationId is null)
                {
                    return Results.Ok(Array.Empty<AccountDto>());
                }

                query = query.Where(x => x.OrganizationId == organizationId.Value);
            }

            if (!string.IsNullOrWhiteSpace(periodKey))
            {
                var reportingPeriodId = await db.ReportingPeriods
                    .AsNoTracking()
                    .Where(x => x.Key == periodKey)
                    .Select(x => (Guid?)x.Id)
                    .FirstOrDefaultAsync(ct);
                if (reportingPeriodId is null)
                {
                    return Results.Ok(Array.Empty<AccountDto>());
                }

                query = query.Where(x => x.ReportingPeriodId == reportingPeriodId.Value);
            }

            if (firstSeen is not null)
            {
                query = query.Where(x => x.IsFirstSeen == firstSeen);
            }

            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<MappingReviewStatus>(status, true, out var reviewStatus))
            {
                query = query.Where(x => x.ReviewStatus == reviewStatus);
            }

            var accounts = await query
                .OrderByDescending(x => x.IsFirstSeen)
                .ThenBy(x => x.Code)
                .Select(x => AccountDto.From(x))
                .ToListAsync(ct);
            return Results.Ok(accounts);
        });

        app.MapGet("/api/mapping/fs-lines", async (
            string organizationKey,
            bool? includeInactive,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var organization = await db.Organizations.AsNoTracking().FirstOrDefaultAsync(x => x.Key == organizationKey, ct);
            if (organization is null)
            {
                return Results.NotFound();
            }

            await EnsureFsLineDefinitionsAsync(db, organization.Id, ct);
            var query = db.FsLineDefinitions
                .AsNoTracking()
                .Where(x => x.OrganizationId == organization.Id);

            if (includeInactive != true)
            {
                query = query.Where(x => x.IsActive);
            }

            var lines = await query
                .OrderBy(x => x.StatementType)
                .ThenBy(x => x.Section)
                .ThenBy(x => x.SortOrder)
                .ThenBy(x => x.Name)
                .Select(x => FsLineDefinitionDto.From(x))
                .ToListAsync(ct);
            return Results.Ok(lines);
        });

        app.MapPost("/api/mapping/group-from-financials", async (
            GroupFromFinancialsRequest request,
            HttpContext http,
            AppDbContext db,
            FinancialStatementGroupingService groupingService,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            Guid? organizationId = null;
            if (!string.IsNullOrWhiteSpace(request.OrganizationKey))
            {
                organizationId = await db.Organizations
                    .Where(x => x.Key == request.OrganizationKey)
                    .Select(x => (Guid?)x.Id)
                    .FirstOrDefaultAsync(ct);
                if (organizationId is null)
                {
                    return Results.NotFound();
                }
            }

            var result = await groupingService.GroupFromImportedFinancialsAsync(organizationId, request.IncludeReviewed == true, ct);
            await EndpointHelpers.AuditAsync(
                db,
                http,
                "mapping.group-from-financials",
                "Organization",
                organizationId,
                null,
                "Grouped GL accounts from imported Xero financial statement lines",
                "{}",
                JsonSerializer.Serialize(result),
                ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(result);
        });

        app.MapPost("/api/mapping/fs-lines", async (
            UpsertFsLineDefinitionRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var organization = await db.Organizations.FirstOrDefaultAsync(x => x.Key == request.OrganizationKey, ct);
            if (organization is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "FS line name is required." });
            }

            var statementType = NormalizeStatementType(request.StatementType);
            var name = request.Name.Trim();
            var existing = await db.FsLineDefinitions.FirstOrDefaultAsync(x =>
                x.OrganizationId == organization.Id
                && x.StatementType == statementType
                && x.Name == name,
                ct);
            if (existing is not null)
            {
                if (!existing.IsActive)
                {
                    var before = JsonSerializer.Serialize(existing);
                    existing.Section = string.IsNullOrWhiteSpace(request.Section) ? InferFsLineSection(name, statementType) : request.Section.Trim();
                    existing.NormalBalance = NormalizeNormalBalance(request.NormalBalance, statementType);
                    existing.AiGuidance = request.AiGuidance?.Trim() ?? "";
                    existing.SortOrder = request.SortOrder ?? existing.SortOrder;
                    existing.IsActive = request.IsActive ?? true;
                    existing.UpdatedAt = DateTimeOffset.UtcNow;
                    await EndpointHelpers.AuditAsync(db, http, "mapping.fs-line.reactivate", "FsLineDefinition", existing.Id, null, request.Reason ?? "Reactivated FS line", before, JsonSerializer.Serialize(existing), ct);
                    await db.SaveChangesAsync(ct);
                    return Results.Ok(FsLineDefinitionDto.From(existing));
                }

                return Results.Conflict(new { error = "That FS line already exists for this entity and statement type." });
            }

            var sortOrder = request.SortOrder ?? await NextFsLineSortOrderAsync(db, organization.Id, statementType, request.Section, ct);
            var line = new FsLineDefinition
            {
                Id = Guid.NewGuid(),
                OrganizationId = organization.Id,
                StatementType = statementType,
                Section = string.IsNullOrWhiteSpace(request.Section) ? InferFsLineSection(name, statementType) : request.Section.Trim(),
                Name = name,
                NormalBalance = NormalizeNormalBalance(request.NormalBalance, statementType),
                AiGuidance = request.AiGuidance?.Trim() ?? "",
                SortOrder = sortOrder,
                IsActive = request.IsActive ?? true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.FsLineDefinitions.Add(line);
            await EndpointHelpers.AuditAsync(db, http, "mapping.fs-line.create", "FsLineDefinition", line.Id, null, request.Reason ?? "Created FS line", "{}", JsonSerializer.Serialize(line), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(FsLineDefinitionDto.From(line));
        });

        app.MapPut("/api/mapping/fs-lines/{lineId:guid}", async (
            Guid lineId,
            UpsertFsLineDefinitionRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var line = await db.FsLineDefinitions.FirstOrDefaultAsync(x => x.Id == lineId, ct);
            if (line is null)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest(new { error = "FS line name is required." });
            }

            var before = JsonSerializer.Serialize(line);
            line.StatementType = NormalizeStatementType(request.StatementType);
            line.Section = string.IsNullOrWhiteSpace(request.Section) ? InferFsLineSection(request.Name, line.StatementType) : request.Section.Trim();
            line.Name = request.Name.Trim();
            line.NormalBalance = NormalizeNormalBalance(request.NormalBalance, line.StatementType);
            line.AiGuidance = request.AiGuidance?.Trim() ?? "";
            line.SortOrder = request.SortOrder ?? line.SortOrder;
            line.IsActive = request.IsActive ?? line.IsActive;
            line.UpdatedAt = DateTimeOffset.UtcNow;

            await EndpointHelpers.AuditAsync(db, http, "mapping.fs-line.update", "FsLineDefinition", line.Id, null, request.Reason ?? "Updated FS line", before, JsonSerializer.Serialize(line), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(FsLineDefinitionDto.From(line));
        });

        app.MapDelete("/api/mapping/fs-lines/{lineId:guid}", async (
            Guid lineId,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var line = await db.FsLineDefinitions.FirstOrDefaultAsync(x => x.Id == lineId, ct);
            if (line is null)
            {
                return Results.NotFound();
            }

            var before = JsonSerializer.Serialize(line);
            line.IsActive = false;
            line.UpdatedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AuditAsync(db, http, "mapping.fs-line.deactivate", "FsLineDefinition", line.Id, null, "Deactivated FS line", before, JsonSerializer.Serialize(line), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(FsLineDefinitionDto.From(line));
        });

        app.MapGet("/api/mapping/accounts/{accountId:guid}", async (Guid accountId, AppDbContext db, CancellationToken ct) =>
        {
            var account = await db.GlAccounts
                .AsNoTracking()
                .Include(x => x.Transactions.OrderByDescending(t => t.TransactionDate))
                .FirstOrDefaultAsync(x => x.Id == accountId, ct);
            return account is null ? Results.NotFound() : Results.Ok(AccountDetailDto.From(account));
        });

        app.MapPost("/api/mapping/accounts/{accountId:guid}/map", async (
            Guid accountId,
            MapAccountRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }

            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.BadRequest(new { error = "Audit reason is required." });
            }

            var account = await db.GlAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null)
            {
                return Results.NotFound();
            }

            var fsLine = request.FsLine.Trim();
            if (!await FsLineDefinitionExistsAsync(db, account.OrganizationId, fsLine, ct))
            {
                return Results.BadRequest(new { error = "Create this FS line in the FS line library before mapping accounts to it." });
            }

            var before = JsonSerializer.Serialize(account);
            account.FsLine = fsLine;
            account.ReviewStatus = MappingReviewStatus.Reviewed;
            account.AuditReason = request.Reason;
            account.UpdatedAt = DateTimeOffset.UtcNow;
            db.AccountMappings.Add(new AccountMapping
            {
                Id = Guid.NewGuid(),
                OrganizationId = account.OrganizationId,
                ReportingPeriodId = account.ReportingPeriodId,
                FsLine = fsLine,
                AccountCodesCsv = account.Code,
                EntityKeysCsv = account.TenantId,
                Reason = request.Reason
            });
            await EndpointHelpers.AuditAsync(db, http, "mapping.map", "GlAccount", account.Id, null, request.Reason, before, JsonSerializer.Serialize(account), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(AccountDto.From(account));
        });

        app.MapPost("/api/mapping/accounts/{accountId:guid}/eliminate", async (
            Guid accountId,
            EliminateAccountRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.BadRequest(new { error = "Audit reason is required." });
            }

            var account = await db.GlAccounts.Include(x => x.Transactions).FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null)
            {
                return Results.NotFound();
            }

            var before = JsonSerializer.Serialize(AccountDetailDto.From(account));
            account.ConsolidationTreatment = request.Type.Equals("exclude", StringComparison.OrdinalIgnoreCase)
                ? ConsolidationTreatment.Exclude
                : request.Type.Equals("intercompany", StringComparison.OrdinalIgnoreCase)
                    ? ConsolidationTreatment.Intercompany
                    : ConsolidationTreatment.Eliminate;
            account.ReviewStatus = MappingReviewStatus.Reviewed;
            account.AuditReason = request.Reason;
            account.UpdatedAt = DateTimeOffset.UtcNow;

            var amount = account.Transactions.Sum(x => x.Credit - x.Debit);
            db.EliminationEntries.Add(new EliminationEntry
            {
                Id = Guid.NewGuid(),
                OrganizationId = account.OrganizationId,
                ReportingPeriodId = account.ReportingPeriodId,
                GlAccountId = account.Id,
                Type = request.Type,
                Description = request.Description,
                Amount = amount,
                Reason = request.Reason,
                IsRecurringRule = request.CreateRecurringRule
            });
            if (request.CreateRecurringRule)
            {
                db.RecurringEliminationRules.Add(new RecurringEliminationRule
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = account.OrganizationId,
                    ReportingPeriodId = account.ReportingPeriodId,
                    GlAccountId = account.Id,
                    Type = request.Type,
                    Description = request.Description,
                    CriteriaJson = JsonSerializer.Serialize(new { account.Code, account.TenantId }),
                    Reason = request.Reason
                });
            }
            await EndpointHelpers.AuditAsync(db, http, "mapping.eliminate", "GlAccount", account.Id, null, request.Reason, before, JsonSerializer.Serialize(AccountDetailDto.From(account)), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(AccountDetailDto.From(account));
        });

        app.MapPost("/api/mapping/accounts/{accountId:guid}/split", async (
            Guid accountId,
            SplitMappingRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            if (string.IsNullOrWhiteSpace(request.Reason) || request.Lines.Length == 0 || request.Lines.Sum(x => x.Percent) != 100m)
            {
                return Results.BadRequest(new { error = "Split mappings require a reason and lines totaling 100%." });
            }

            var account = await db.GlAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null)
            {
                return Results.NotFound();
            }

            foreach (var line in request.Lines)
            {
                if (!await FsLineDefinitionExistsAsync(db, account.OrganizationId, line.FsLine.Trim(), ct))
                {
                    return Results.BadRequest(new { error = $"Create FS line '{line.FsLine}' in the FS line library before using it in a split." });
                }
            }

            var before = JsonSerializer.Serialize(account);
            account.FsLine = string.Join(" / ", request.Lines.Select(x => $"{x.FsLine.Trim()} {x.Percent:0.#}%"));
            account.ReviewStatus = MappingReviewStatus.Reviewed;
            account.AuditReason = request.Reason;
            account.UpdatedAt = DateTimeOffset.UtcNow;
            db.AccountMappings.Add(new AccountMapping
            {
                Id = Guid.NewGuid(),
                OrganizationId = account.OrganizationId,
                ReportingPeriodId = account.ReportingPeriodId,
                FsLine = "Split mapping",
                AccountCodesCsv = account.Code,
                EntityKeysCsv = account.TenantId,
                Reason = JsonSerializer.Serialize(new { request.Reason, request.Lines })
            });
            await EndpointHelpers.AuditAsync(db, http, "mapping.split", "GlAccount", account.Id, null, request.Reason, before, JsonSerializer.Serialize(account), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(AccountDto.From(account));
        });

        app.MapPost("/api/mapping/accounts/{accountId:guid}/reject", async (
            Guid accountId,
            MappingReasonRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }

            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.BadRequest(new { error = "Audit reason is required." });
            }

            var account = await db.GlAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null)
            {
                return Results.NotFound();
            }

            var before = JsonSerializer.Serialize(account);
            account.ReviewStatus = MappingReviewStatus.Rejected;
            account.AuditReason = request.Reason;
            account.UpdatedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AuditAsync(db, http, "mapping.reject", "GlAccount", account.Id, null, request.Reason, before, JsonSerializer.Serialize(account), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(AccountDto.From(account));
        });

        app.MapPost("/api/mapping/accounts/{accountId:guid}/mark-reviewed", async (
            Guid accountId,
            MappingReasonRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }

            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.BadRequest(new { error = "Audit reason is required." });
            }

            var account = await db.GlAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
            if (account is null)
            {
                return Results.NotFound();
            }

            var before = JsonSerializer.Serialize(account);
            account.ReviewStatus = MappingReviewStatus.Reviewed;
            account.AuditReason = request.Reason;
            account.UpdatedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AuditAsync(db, http, "mapping.review", "GlAccount", account.Id, null, request.Reason, before, JsonSerializer.Serialize(account), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(AccountDto.From(account));
        });

        app.MapGet("/api/mapping/recurring-eliminations", async (
            string? organizationKey,
            string? periodKey,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var query = db.RecurringEliminationRules.AsNoTracking().AsQueryable();

            if (!string.IsNullOrWhiteSpace(organizationKey))
            {
                var organizationId = await db.Organizations
                    .AsNoTracking()
                    .Where(x => x.Key == organizationKey)
                    .Select(x => (Guid?)x.Id)
                    .FirstOrDefaultAsync(ct);
                if (organizationId is null)
                {
                    return Results.Ok(Array.Empty<RecurringEliminationRule>());
                }
                query = query.Where(x => x.OrganizationId == organizationId.Value);
            }

            if (!string.IsNullOrWhiteSpace(periodKey))
            {
                var reportingPeriodId = await db.ReportingPeriods
                    .AsNoTracking()
                    .Where(x => x.Key == periodKey)
                    .Select(x => (Guid?)x.Id)
                    .FirstOrDefaultAsync(ct);
                if (reportingPeriodId is null)
                {
                    return Results.Ok(Array.Empty<RecurringEliminationRule>());
                }
                query = query.Where(x => x.ReportingPeriodId == reportingPeriodId.Value);
            }

            var rules = await query.OrderByDescending(x => x.CreatedAt).ToListAsync(ct);
            return Results.Ok(rules);
        });

        app.MapPost("/api/mapping/recurring-eliminations", async (
            RecurringEliminationRuleRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                return Results.BadRequest(new { error = "Audit reason is required." });
            }

            var rule = new RecurringEliminationRule
            {
                Id = Guid.NewGuid(),
                OrganizationId = request.OrganizationId,
                ReportingPeriodId = request.ReportingPeriodId,
                GlAccountId = request.GlAccountId,
                Type = request.Type,
                Description = request.Description,
                CriteriaJson = request.CriteriaJson,
                Amount = request.Amount,
                Reason = request.Reason,
                IsActive = request.IsActive
            };
            db.RecurringEliminationRules.Add(rule);
            await EndpointHelpers.AuditAsync(db, http, "elimination-rule.create", "RecurringEliminationRule", rule.Id, null, request.Reason, "{}", JsonSerializer.Serialize(rule), ct);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/mapping/recurring-eliminations/{rule.Id}", rule);
        });

        return app;
    }

    // -------------------------------------------------------------------------
    // Private helpers — local copies of Program.cs static helpers.
    // These will be deduplicated once Program.cs is fully drained.
    // -------------------------------------------------------------------------

    // Local copy of Program.cs helper; will dedupe once Program.cs is fully drained.
    private static async Task EnsureFsLineDefinitionsAsync(AppDbContext db, Guid organizationId, CancellationToken ct)
    {
        if (await db.FsLineDefinitions.AnyAsync(x => x.OrganizationId == organizationId, ct))
        {
            return;
        }

        var candidates = DefaultFsLineDefinitions();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sort = 10;
        foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x.Name)))
        {
            var statementType = NormalizeStatementType(candidate.StatementType);
            var name = candidate.Name.Trim();
            if (!seen.Add($"{statementType}|{name}"))
            {
                continue;
            }

            db.FsLineDefinitions.Add(new FsLineDefinition
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                StatementType = statementType,
                Section = string.IsNullOrWhiteSpace(candidate.Section) ? InferFsLineSection(name, statementType) : candidate.Section.Trim(),
                Name = name,
                NormalBalance = NormalizeNormalBalance(candidate.NormalBalance, statementType),
                AiGuidance = candidate.AiGuidance.Trim(),
                SortOrder = sort,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            sort += 10;
        }

        await db.SaveChangesAsync(ct);
    }

    // Local copy of Program.cs helper; will dedupe once Program.cs is fully drained.
    private static async Task<bool> FsLineDefinitionExistsAsync(AppDbContext db, Guid organizationId, string fsLine, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fsLine))
        {
            return false;
        }

        await EnsureFsLineDefinitionsAsync(db, organizationId, ct);
        var names = await db.FsLineDefinitions
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsActive)
            .Select(x => x.Name)
            .ToListAsync(ct);
        return names.Any(x => string.Equals(x, fsLine.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    // Local copy of Program.cs helper; will dedupe once Program.cs is fully drained.
    private static async Task<int> NextFsLineSortOrderAsync(AppDbContext db, Guid organizationId, string statementType, string? section, CancellationToken ct)
    {
        var rows = await db.FsLineDefinitions
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.StatementType == statementType)
            .Select(x => new { x.Section, x.SortOrder })
            .ToListAsync(ct);
        var sectionRows = string.IsNullOrWhiteSpace(section)
            ? rows
            : rows.Where(x => string.Equals(x.Section, section.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return sectionRows.Count == 0 ? 10 : sectionRows.Max(x => x.SortOrder) + 10;
    }

    // Local copy of Program.cs helper; will dedupe once Program.cs is fully drained.
    private static string NormalizeStatementType(string? statementType)
    {
        if (string.Equals(statementType, "BalanceSheet", StringComparison.OrdinalIgnoreCase)
            || string.Equals(statementType, "Balance Sheet", StringComparison.OrdinalIgnoreCase))
        {
            return "BalanceSheet";
        }

        if (string.Equals(statementType, "TrialBalance", StringComparison.OrdinalIgnoreCase)
            || string.Equals(statementType, "Trial Balance", StringComparison.OrdinalIgnoreCase))
        {
            return "TrialBalance";
        }

        return "IncomeStatement";
    }

    // Local copy of Program.cs helper; will dedupe once Program.cs is fully drained.
    private static string InferFsLineSection(string fsLine, string statementType)
    {
        if (statementType == "BalanceSheet")
        {
            if (fsLine.Contains("liabil", StringComparison.OrdinalIgnoreCase) || fsLine.Contains("payable", StringComparison.OrdinalIgnoreCase) || fsLine.Contains("debt", StringComparison.OrdinalIgnoreCase))
            {
                return "Liabilities";
            }

            if (fsLine.Contains("equity", StringComparison.OrdinalIgnoreCase) || fsLine.Contains("retained", StringComparison.OrdinalIgnoreCase))
            {
                return "Equity";
            }

            return "Assets";
        }

        if (fsLine.Contains("cost", StringComparison.OrdinalIgnoreCase) || fsLine.Contains("cogs", StringComparison.OrdinalIgnoreCase))
        {
            return "Cost of Revenue";
        }

        if (fsLine.Contains("revenue", StringComparison.OrdinalIgnoreCase) || fsLine.Contains("sales", StringComparison.OrdinalIgnoreCase) || fsLine.Contains("income", StringComparison.OrdinalIgnoreCase))
        {
            return "Revenue";
        }

        if (fsLine.Contains("other", StringComparison.OrdinalIgnoreCase))
        {
            return "Other Income / Expense";
        }

        return "Operating Expenses";
    }

    // Local copy of Program.cs helper; will dedupe once Program.cs is fully drained.
    private static string NormalizeNormalBalance(string? normalBalance, string statementType, string? accountType = null)
    {
        if (string.Equals(normalBalance, "Debit", StringComparison.OrdinalIgnoreCase) || string.Equals(normalBalance, "Credit", StringComparison.OrdinalIgnoreCase))
        {
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalBalance!.ToLowerInvariant());
        }

        var text = $"{statementType} {accountType}";
        return text.Contains("expense", StringComparison.OrdinalIgnoreCase)
               || text.Contains("asset", StringComparison.OrdinalIgnoreCase)
               || text.Contains("cost", StringComparison.OrdinalIgnoreCase)
            ? "Debit"
            : "Credit";
    }

    // Local copy of Program.cs helper; will dedupe once Program.cs is fully drained.
    private static IEnumerable<(string StatementType, string Section, string Name, string NormalBalance, string AiGuidance)> DefaultFsLineDefinitions()
        =>
        [
            ("IncomeStatement", "Revenue", "Revenue", "Credit", "Operating revenue earned from customers."),
            ("IncomeStatement", "Cost of Revenue", "Cost of Goods Sold", "Debit", "Direct costs tied to delivered services or products."),
            ("IncomeStatement", "Operating Expenses", "Operating Expense - Payroll", "Debit", "Salaries, wages, benefits, contractors, and payroll-related costs."),
            ("IncomeStatement", "Operating Expenses", "Operating Expense - General & Administrative", "Debit", "General overhead and administrative expenses."),
            ("IncomeStatement", "Other Income / Expense", "Other Income / Expense", "Debit", "Non-operating income, expense, interest, or one-time items."),
            ("BalanceSheet", "Assets", "Cash", "Debit", "Bank and cash equivalent accounts."),
            ("BalanceSheet", "Assets", "Accounts Receivable", "Debit", "Customer receivable balances."),
            ("BalanceSheet", "Liabilities", "Accounts Payable", "Credit", "Vendor payable balances."),
            ("BalanceSheet", "Liabilities", "Debt", "Credit", "Borrowings and financing liabilities."),
            ("BalanceSheet", "Equity", "Equity", "Credit", "Member equity, retained earnings, and owner capital.")
        ];
}
