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
/// Issue resolution endpoints: apply-fix and ignore.
/// Migrated from Program.cs inline registrations. Cat 29.
/// </summary>
public static class IssueEndpoints
{
    public static IEndpointRouteBuilder MapIssueEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /api/packages/{packageId:guid}/issues/{issueId:guid}/apply-fix
        // Validates and applies the AI-recommended fix operations from the issue's RecommendedFixJson.
        app.MapPost("/api/packages/{packageId:guid}/issues/{issueId:guid}/apply-fix", async (
            Guid packageId,
            Guid issueId,
            ApplyFixRequest request,
            HttpContext http,
            AppDbContext db,
            FixOperationValidator validator,
            PackageSnapshotBuilder snapshotBuilder,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }
            if (await EndpointHelpers.RejectIfPackageApprovedAsync(db, packageId, ct) is { } locked)
            {
                return locked;
            }

            var issue = await db.PackageIssues.FirstOrDefaultAsync(x => x.Id == issueId && x.ReportPackageId == packageId, ct);
            if (issue is null)
            {
                return Results.NotFound();
            }

            var operations = ParseOperations(issue.RecommendedFixJson).ToList();
            var errors = validator.Validate(operations);
            if (errors.Count > 0)
            {
                return Results.BadRequest(new { errors });
            }

            var before = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct);
            foreach (var operation in operations)
            {
                await ApplyOperationAsync(db, operation, request.Reason ?? "User approved AI recommendation", ct);
            }

            issue.Status = IssueStatus.Resolved;
            issue.UserComment = request.Comment;
            issue.ResolvedAt = DateTimeOffset.UtcNow;
            db.PackageVersions.Add(new PackageVersion
            {
                Id = Guid.NewGuid(),
                ReportPackageId = packageId,
                VersionLabel = $"Fix {DateTimeOffset.UtcNow:HH:mm}",
                CreatedBy = EndpointHelpers.Actor(http),
                ChangeSummary = $"Applied AI fix for issue: {issue.Title}",
                SnapshotJson = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct)
            });
            await EndpointHelpers.AuditAsync(db, http, "ai.apply-fix", "PackageIssue", issueId, packageId, request.Reason ?? "User approved AI recommendation", before, issue.RecommendedFixJson, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { issue.Id, Status = issue.Status.ToString() });
        });

        // POST /api/packages/{packageId:guid}/issues/{issueId:guid}/ignore
        // Marks an issue as ignored without applying any fix.
        app.MapPost("/api/packages/{packageId:guid}/issues/{issueId:guid}/ignore", async (
            Guid packageId,
            Guid issueId,
            ApplyFixRequest request,
            HttpContext http,
            AppDbContext db,
            PackageSnapshotBuilder snapshotBuilder,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }
            if (await EndpointHelpers.RejectIfPackageApprovedAsync(db, packageId, ct) is { } locked)
            {
                return locked;
            }

            var issue = await db.PackageIssues.FirstOrDefaultAsync(x => x.Id == issueId && x.ReportPackageId == packageId, ct);
            if (issue is null)
            {
                return Results.NotFound();
            }

            var before = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct);
            issue.Status = IssueStatus.Ignored;
            issue.UserComment = request.Comment ?? request.Reason;
            issue.ResolvedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AuditAsync(db, http, "issue.ignore", "PackageIssue", issue.Id, packageId, request.Reason ?? "Ignored from issue workbench", before, JsonSerializer.Serialize(issue), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(IssueDto.From(issue));
        });

        return app;
    }

    // --- Local copies of helpers still in Program.cs; remove once Program.cs is fully drained. ---

    /// <summary>
    /// LOCAL COPY from Program.cs — ParseOperations.
    /// Parses the JSON operations array from an issue's RecommendedFixJson into FixOperation records.
    /// </summary>
    private static IEnumerable<FixOperation> ParseOperations(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch
        {
            yield break;
        }

        var operations = root?["operations"]?.AsArray();
        if (operations is null)
        {
            yield break;
        }

        foreach (var node in operations)
        {
            if (node is null)
            {
                continue;
            }

            var targetId = Guid.TryParse(node["targetId"]?.GetValue<string>(), out var id) ? id : Guid.Empty;
            var valueElement = node["value"] is null
                ? (JsonElement?)null
                : JsonSerializer.SerializeToElement(node["value"]);
            yield return new FixOperation(
                node["op"]?.GetValue<string>() ?? "",
                node["targetType"]?.GetValue<string>() ?? "",
                targetId,
                valueElement,
                node["reason"]?.GetValue<string>());
        }
    }

    /// <summary>
    /// LOCAL COPY from Program.cs — ApplyOperationAsync.
    /// Dispatches a single FixOperation to the appropriate domain entity.
    /// </summary>
    private static async Task ApplyOperationAsync(AppDbContext db, FixOperation operation, string reason, CancellationToken ct)
    {
        if (operation.TargetType.Equals("slide", StringComparison.OrdinalIgnoreCase))
        {
            var slide = await db.PackageSlides.FirstOrDefaultAsync(x => x.Id == operation.TargetId, ct);
            if (slide is null)
            {
                return;
            }

            if (operation.Op.Equals("append_narrative", StringComparison.OrdinalIgnoreCase)
                || operation.Op.Equals("add_callout", StringComparison.OrdinalIgnoreCase))
            {
                var text = ExtractText(operation.Value) ?? "AI recommended update approved.";
                var nextSortOrder = await db.SlideBlocks
                    .Where(x => x.PackageSlideId == slide.Id)
                    .Select(x => (int?)x.SortOrder)
                    .MaxAsync(ct) ?? 0;
                db.SlideBlocks.Add(new SlideBlock
                {
                    Id = Guid.NewGuid(),
                    PackageSlideId = slide.Id,
                    SortOrder = nextSortOrder + 1,
                    Kind = operation.Op.Equals("add_callout", StringComparison.OrdinalIgnoreCase) ? "callout" : "text",
                    ContentJson = JsonSerializer.Serialize(new { text, reason })
                });
            }
            else if (operation.Op.Equals("update_chart", StringComparison.OrdinalIgnoreCase) && operation.Value is { } value)
            {
                slide.ChartConfigJson = value.GetRawText();
            }
        }

        if (operation.TargetType.Equals("block", StringComparison.OrdinalIgnoreCase)
            && operation.Op.Equals("replace_text", StringComparison.OrdinalIgnoreCase))
        {
            var block = await db.SlideBlocks.FirstOrDefaultAsync(x => x.Id == operation.TargetId, ct);
            if (block is not null)
            {
                block.ContentJson = JsonSerializer.Serialize(new { text = ExtractText(operation.Value) ?? "", reason });
            }
        }

        if (operation.TargetType.Equals("account", StringComparison.OrdinalIgnoreCase))
        {
            var account = await db.GlAccounts.FirstOrDefaultAsync(x => x.Id == operation.TargetId, ct);
            if (account is null)
            {
                return;
            }

            if (operation.Op.Equals("map_account", StringComparison.OrdinalIgnoreCase))
            {
                account.FsLine = ExtractFsLine(operation.Value) ?? account.FsLine;
                account.ReviewStatus = MappingReviewStatus.Reviewed;
                db.AccountMappings.Add(new AccountMapping
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = account.OrganizationId,
                    ReportingPeriodId = account.ReportingPeriodId,
                    FsLine = account.FsLine,
                    AccountCodesCsv = account.Code,
                    EntityKeysCsv = account.TenantId,
                    Reason = reason
                });
            }
            else if (operation.Op.Equals("eliminate_account", StringComparison.OrdinalIgnoreCase))
            {
                account.ConsolidationTreatment = ConsolidationTreatment.Eliminate;
                db.EliminationEntries.Add(new EliminationEntry
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = account.OrganizationId,
                    ReportingPeriodId = account.ReportingPeriodId,
                    GlAccountId = account.Id,
                    Type = "EliminateAccount",
                    Description = $"AI-approved elimination for {account.Code}",
                    Amount = await db.GlTransactions.Where(x => x.GlAccountId == account.Id).SumAsync(x => x.Credit - x.Debit, ct),
                    Reason = reason
                });
            }
            else if (operation.Op.Equals("exclude_account", StringComparison.OrdinalIgnoreCase))
            {
                account.ConsolidationTreatment = ConsolidationTreatment.Exclude;
            }
            else if (operation.Op.Equals("create_intercompany_elimination", StringComparison.OrdinalIgnoreCase))
            {
                account.ConsolidationTreatment = ConsolidationTreatment.Intercompany;
                db.EliminationEntries.Add(new EliminationEntry
                {
                    Id = Guid.NewGuid(),
                    OrganizationId = account.OrganizationId,
                    ReportingPeriodId = account.ReportingPeriodId,
                    GlAccountId = account.Id,
                    Type = "Intercompany",
                    Description = ExtractText(operation.Value) ?? $"AI-approved intercompany elimination for {account.Code}",
                    Amount = await db.GlTransactions.Where(x => x.GlAccountId == account.Id).SumAsync(x => x.Credit - x.Debit, ct),
                    Reason = reason
                });
            }

            account.AuditReason = reason;
            account.UpdatedAt = DateTimeOffset.UtcNow;
        }

        if (operation.TargetType.Equals("kpi", StringComparison.OrdinalIgnoreCase)
            && operation.Op.Equals("update_kpi", StringComparison.OrdinalIgnoreCase))
        {
            var kpi = await db.KpiDefinitions.FirstOrDefaultAsync(x => x.Id == operation.TargetId, ct);
            if (kpi is not null && operation.Value is { } value)
            {
                if (value.TryGetProperty("targetValue", out var target) && target.TryGetDecimal(out var targetValue))
                {
                    kpi.TargetValue = targetValue;
                }

                if (value.TryGetProperty("currentValue", out var current) && current.TryGetDecimal(out var currentValue))
                {
                    kpi.CurrentValue = currentValue;
                }

                if (value.TryGetProperty("isPinned", out var pinned) && pinned.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    kpi.IsPinned = pinned.GetBoolean();
                }

                kpi.Status = FinancialMath.KpiStatus(kpi.CurrentValue, kpi.TargetValue);
            }
        }

        if (operation.TargetType.Equals("issue", StringComparison.OrdinalIgnoreCase))
        {
            var issue = await db.PackageIssues.FirstOrDefaultAsync(x => x.Id == operation.TargetId, ct);
            if (issue is not null)
            {
                if (operation.Op.Equals("resolve_issue", StringComparison.OrdinalIgnoreCase))
                {
                    issue.Status = IssueStatus.Resolved;
                    issue.ResolvedAt = DateTimeOffset.UtcNow;
                }
                else if (operation.Op.Equals("ignore_issue", StringComparison.OrdinalIgnoreCase))
                {
                    issue.Status = IssueStatus.Ignored;
                }

                issue.UserComment = reason;
            }
        }
    }

    /// <summary>
    /// LOCAL COPY from Program.cs — ExtractText.
    /// Pulls the text string from a JsonElement value (object with "text" key, or raw string).
    /// </summary>
    private static string? ExtractText(JsonElement? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Value.ValueKind == JsonValueKind.Object && value.Value.TryGetProperty("text", out var text)
            ? text.GetString()
            : value.Value.ToString();
    }

    /// <summary>
    /// LOCAL COPY from Program.cs — ExtractFsLine.
    /// Pulls the fsLine string from a JsonElement value object.
    /// </summary>
    private static string? ExtractFsLine(JsonElement? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.Value.ValueKind == JsonValueKind.Object && value.Value.TryGetProperty("fsLine", out var text)
            ? text.GetString()
            : null;
    }
}
