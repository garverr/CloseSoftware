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
/// Slide, block, and version endpoints extracted from Program.cs.
/// Covers version history, version restore, slide mutation, block CRUD, and block reorder.
/// </summary>
public static class SlideBlockVersionEndpoints
{
    public static IEndpointRouteBuilder MapSlideBlockVersionEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Versions ─────────────────────────────────────────────────────────────────────

        app.MapGet("/api/packages/{packageId:guid}/versions", async (
            Guid packageId,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var versions = await db.PackageVersions
                .AsNoTracking()
                .Where(x => x.ReportPackageId == packageId)
                .Select(x => new PackageVersionDto(x.Id, x.VersionLabel, x.CreatedBy, x.ChangeSummary, x.CreatedAt))
                .ToListAsync(ct);
            return Results.Ok(versions.OrderByDescending(x => x.CreatedAt));
        });

        app.MapPost("/api/packages/{packageId:guid}/versions/{versionId:guid}/restore", async (
            Guid packageId,
            Guid versionId,
            HttpContext http,
            AppDbContext db,
            PackageSnapshotBuilder snapshotBuilder,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var version = await db.PackageVersions.FirstOrDefaultAsync(x => x.ReportPackageId == packageId && x.Id == versionId, ct);
            if (version is null)
            {
                return Results.NotFound();
            }

            var before = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct);
            var restored = await RestorePackageSnapshotAsync(db, packageId, version.SnapshotJson, ct);
            if (!restored)
            {
                return Results.BadRequest(new { error = "Version snapshot could not be restored." });
            }

            db.PackageVersions.Add(new PackageVersion
            {
                Id = Guid.NewGuid(),
                ReportPackageId = packageId,
                VersionLabel = $"Restore {DateTimeOffset.UtcNow:HH:mm}",
                CreatedBy = EndpointHelpers.Actor(http),
                ChangeSummary = $"Restored {version.VersionLabel}",
                SnapshotJson = before
            });
            await EndpointHelpers.AuditAsync(db, http, "package.version.restore", "PackageVersion", versionId, packageId, "Restore package version", before, version.SnapshotJson, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { restored = versionId });
        });

        // ── Slides ────────────────────────────────────────────────────────────────────────

        app.MapPut("/api/slides/{slideId:guid}", async (
            Guid slideId,
            UpdateSlideRequest request,
            HttpContext http,
            AppDbContext db,
            PackageSnapshotBuilder snapshotBuilder,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var slide = await db.PackageSlides.Include(x => x.ReportPackage).FirstOrDefaultAsync(x => x.Id == slideId, ct);
            if (slide is null)
            {
                return Results.NotFound();
            }

            var before = await snapshotBuilder.BuildPackageSnapshotAsync(slide.ReportPackageId, ct);
            slide.Subject = request.Subject ?? slide.Subject;
            slide.KpiLabel = request.KpiLabel ?? slide.KpiLabel;
            slide.AccountCodesCsv = request.AccountCodesCsv ?? slide.AccountCodesCsv;
            slide.MonthlyJson = request.MonthlyJson ?? slide.MonthlyJson;
            slide.PriorMonthlyJson = request.PriorMonthlyJson ?? slide.PriorMonthlyJson;
            slide.ChartConfigJson = request.ChartConfigJson ?? slide.ChartConfigJson;
            if (request.CurrentValue is not null) slide.CurrentValue = request.CurrentValue.Value;
            if (request.PriorValue is not null) slide.PriorValue = request.PriorValue.Value;
            var variance = FinancialMath.Variance(slide.CurrentValue, slide.PriorValue);
            slide.VarianceAmount = variance.Amount;
            slide.VariancePercent = variance.Percent;
            slide.ReportPackage!.UpdatedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AddVersionAndAuditAsync(db, http, snapshotBuilder, slide.ReportPackageId, "slide.update", "PackageSlide", slide.Id, "Updated slide", before, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(SlideDto.From(slide));
        });

        // ── Blocks ────────────────────────────────────────────────────────────────────────

        app.MapPost("/api/slides/{slideId:guid}/blocks", async (
            Guid slideId,
            UpsertBlockRequest request,
            HttpContext http,
            AppDbContext db,
            PackageSnapshotBuilder snapshotBuilder,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var slide = await db.PackageSlides.Include(x => x.Blocks).FirstOrDefaultAsync(x => x.Id == slideId, ct);
            if (slide is null)
            {
                return Results.NotFound();
            }

            var before = await snapshotBuilder.BuildPackageSnapshotAsync(slide.ReportPackageId, ct);
            var nextSortOrder = request.SortOrder ?? ((slide.Blocks.Select(x => (int?)x.SortOrder).Max() ?? 0) + 1);
            var block = new SlideBlock
            {
                Id = Guid.NewGuid(),
                PackageSlideId = slideId,
                SortOrder = nextSortOrder,
                Kind = request.Kind,
                ContentJson = request.ContentJson
            };
            db.SlideBlocks.Add(block);
            await EndpointHelpers.AddVersionAndAuditAsync(db, http, snapshotBuilder, slide.ReportPackageId, "block.create", "SlideBlock", block.Id, "Created slide block", before, ct);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/blocks/{block.Id}", SlideBlockDto.From(block));
        });

        app.MapPut("/api/blocks/{blockId:guid}", async (
            Guid blockId,
            UpsertBlockRequest request,
            HttpContext http,
            AppDbContext db,
            PackageSnapshotBuilder snapshotBuilder,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var block = await db.SlideBlocks.Include(x => x.PackageSlide).FirstOrDefaultAsync(x => x.Id == blockId, ct);
            if (block is null)
            {
                return Results.NotFound();
            }

            var before = await snapshotBuilder.BuildPackageSnapshotAsync(block.PackageSlide!.ReportPackageId, ct);
            block.Kind = string.IsNullOrWhiteSpace(request.Kind) ? block.Kind : request.Kind;
            block.ContentJson = string.IsNullOrWhiteSpace(request.ContentJson) ? block.ContentJson : request.ContentJson;
            if (request.SortOrder is not null) block.SortOrder = request.SortOrder.Value;
            await EndpointHelpers.AddVersionAndAuditAsync(db, http, snapshotBuilder, block.PackageSlide.ReportPackageId, "block.update", "SlideBlock", block.Id, "Updated slide block", before, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(SlideBlockDto.From(block));
        });

        app.MapDelete("/api/blocks/{blockId:guid}", async (
            Guid blockId,
            HttpContext http,
            AppDbContext db,
            PackageSnapshotBuilder snapshotBuilder,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var block = await db.SlideBlocks.Include(x => x.PackageSlide).FirstOrDefaultAsync(x => x.Id == blockId, ct);
            if (block is null)
            {
                return Results.NotFound();
            }

            var packageId = block.PackageSlide!.ReportPackageId;
            var before = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct);
            db.SlideBlocks.Remove(block);
            await EndpointHelpers.AddVersionAndAuditAsync(db, http, snapshotBuilder, packageId, "block.delete", "SlideBlock", block.Id, "Deleted slide block", before, ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        app.MapPost("/api/slides/{slideId:guid}/reorder-blocks", async (
            Guid slideId,
            ReorderBlocksRequest request,
            HttpContext http,
            AppDbContext db,
            PackageSnapshotBuilder snapshotBuilder,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }

            var slide = await db.PackageSlides.Include(x => x.Blocks).FirstOrDefaultAsync(x => x.Id == slideId, ct);
            if (slide is null)
            {
                return Results.NotFound();
            }

            var before = await snapshotBuilder.BuildPackageSnapshotAsync(slide.ReportPackageId, ct);
            var order = request.BlockIds.Select((id, index) => new { id, sort = index + 1 }).ToDictionary(x => x.id, x => x.sort);
            foreach (var block in slide.Blocks)
            {
                if (order.TryGetValue(block.Id, out var sort))
                {
                    block.SortOrder = sort;
                }
            }

            await EndpointHelpers.AddVersionAndAuditAsync(db, http, snapshotBuilder, slide.ReportPackageId, "block.reorder", "PackageSlide", slide.Id, "Reordered slide blocks", before, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(slide.Blocks.OrderBy(x => x.SortOrder).Select(SlideBlockDto.From));
        });

        return app;
    }

    // Local copy of Program.cs helper; will dedupe once Program.cs is fully drained.
    private static async Task<bool> RestorePackageSnapshotAsync(AppDbContext db, Guid packageId, string snapshotJson, CancellationToken ct)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(snapshotJson);
        }
        catch
        {
            return false;
        }

        if (root?["package"] is null || root["slides"] is not JsonArray slides)
        {
            return false;
        }

        var package = await db.ReportPackages
            .Include(x => x.Slides)
                .ThenInclude(x => x.Blocks)
            .FirstOrDefaultAsync(x => x.Id == packageId, ct);
        if (package is null)
        {
            return false;
        }

        package.VersionLabel = root["package"]?["versionLabel"]?.GetValue<string>() ?? package.VersionLabel;
        package.BaseFrom = root["package"]?["baseFrom"]?.GetValue<string>() ?? package.BaseFrom;
        package.ThemeJson = root["package"]?["themeJson"]?.GetValue<string>() ?? package.ThemeJson;
        package.UpdatedAt = DateTimeOffset.UtcNow;

        var incomingSlideIds = new HashSet<Guid>();
        foreach (var slideNode in slides)
        {
            if (slideNode is null || !Guid.TryParse(slideNode["id"]?.GetValue<string>(), out var slideId))
            {
                continue;
            }

            incomingSlideIds.Add(slideId);
            var slide = package.Slides.FirstOrDefault(x => x.Id == slideId);
            if (slide is null)
            {
                slide = new PackageSlide { Id = slideId, ReportPackageId = packageId };
                package.Slides.Add(slide);
            }

            slide.SortOrder = slideNode["sortOrder"]?.GetValue<int>() ?? slide.SortOrder;
            slide.Subject = slideNode["subject"]?.GetValue<string>() ?? slide.Subject;
            slide.KpiLabel = slideNode["kpiLabel"]?.GetValue<string>() ?? slide.KpiLabel;
            slide.CurrentValue = slideNode["currentValue"]?.GetValue<decimal>() ?? slide.CurrentValue;
            slide.PriorValue = slideNode["priorValue"]?.GetValue<decimal>() ?? slide.PriorValue;
            slide.VarianceAmount = slideNode["varianceAmount"]?.GetValue<decimal>() ?? slide.VarianceAmount;
            slide.VariancePercent = slideNode["variancePercent"]?.GetValue<decimal>() ?? slide.VariancePercent;
            slide.AccountCodesCsv = slideNode["accountCodesCsv"]?.GetValue<string>() ?? slide.AccountCodesCsv;
            slide.MonthlyJson = slideNode["monthlyJson"]?.GetValue<string>() ?? slide.MonthlyJson;
            slide.PriorMonthlyJson = slideNode["priorMonthlyJson"]?.GetValue<string>() ?? slide.PriorMonthlyJson;
            slide.ChartConfigJson = slideNode["chartConfigJson"]?.GetValue<string>() ?? slide.ChartConfigJson;

            db.SlideBlocks.RemoveRange(slide.Blocks);
            if (slideNode["blocks"] is JsonArray blocks)
            {
                foreach (var blockNode in blocks)
                {
                    if (blockNode is null || !Guid.TryParse(blockNode["id"]?.GetValue<string>(), out var blockId))
                    {
                        continue;
                    }

                    slide.Blocks.Add(new SlideBlock
                    {
                        Id = blockId,
                        PackageSlideId = slide.Id,
                        SortOrder = blockNode["sortOrder"]?.GetValue<int>() ?? 1,
                        Kind = blockNode["kind"]?.GetValue<string>() ?? "text",
                        ContentJson = blockNode["contentJson"]?.GetValue<string>() ?? "{}"
                    });
                }
            }
        }

        foreach (var slide in package.Slides.Where(x => !incomingSlideIds.Contains(x.Id)).ToList())
        {
            db.PackageSlides.Remove(slide);
        }

        return true;
    }
}
