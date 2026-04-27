using System.Text.Json;
using FinancialReporting.Api.Common;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Features.Packages;

/// <summary>
/// Package-context endpoints: comments, share links, audit log query, and version list.
/// All small CRUDs that share the audit-stamping pattern. Cat 29.
/// </summary>
public static class PackageContextEndpoints
{
    public static IEndpointRouteBuilder MapPackageContextEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Comments ────────────────────────────────────────────────────────────────────
        app.MapGet("/api/packages/{packageId:guid}/comments", async (
            Guid packageId,
            Guid? slideId,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var query = db.PackageComments.AsNoTracking().Where(x => x.ReportPackageId == packageId);
            if (slideId is not null)
            {
                query = query.Where(x => x.PackageSlideId == slideId);
            }
            var comments = (await query.ToListAsync(ct))
                .OrderByDescending(x => x.CreatedAt)
                .ToList();
            return Results.Ok(comments.Select(PackageCommentDto.From));
        });

        app.MapPost("/api/packages/{packageId:guid}/comments", async (
            Guid packageId,
            UpsertPackageCommentRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }
            if (!await db.ReportPackages.AnyAsync(x => x.Id == packageId, ct))
            {
                return Results.NotFound();
            }
            var comment = new PackageComment
            {
                Id = Guid.NewGuid(),
                ReportPackageId = packageId,
                PackageSlideId = request.PackageSlideId,
                SlideBlockId = request.SlideBlockId,
                Body = request.Body.Trim(),
                Status = string.Equals(request.Status, "Resolved", StringComparison.OrdinalIgnoreCase) ? "Resolved" : "Open",
                Author = string.IsNullOrWhiteSpace(request.Author) ? EndpointHelpers.Actor(http) : request.Author.Trim(),
                ResolvedAt = string.Equals(request.Status, "Resolved", StringComparison.OrdinalIgnoreCase) ? DateTimeOffset.UtcNow : null
            };
            db.PackageComments.Add(comment);
            await EndpointHelpers.AuditAsync(db, http, "comment.create", "PackageComment", comment.Id, packageId, "Created package comment", "{}", JsonSerializer.Serialize(comment), ct);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/packages/{packageId}/comments/{comment.Id}", PackageCommentDto.From(comment));
        });

        app.MapPut("/api/comments/{commentId:guid}", async (
            Guid commentId,
            UpsertPackageCommentRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor", "Reviewer"))
            {
                return Results.Forbid();
            }
            var comment = await db.PackageComments.FirstOrDefaultAsync(x => x.Id == commentId, ct);
            if (comment is null)
            {
                return Results.NotFound();
            }
            var before = JsonSerializer.Serialize(comment);
            comment.PackageSlideId = request.PackageSlideId;
            comment.SlideBlockId = request.SlideBlockId;
            comment.Body = request.Body.Trim();
            comment.Status = string.Equals(request.Status, "Resolved", StringComparison.OrdinalIgnoreCase) ? "Resolved" : "Open";
            comment.Author = string.IsNullOrWhiteSpace(request.Author) ? comment.Author : request.Author.Trim();
            comment.UpdatedAt = DateTimeOffset.UtcNow;
            comment.ResolvedAt = comment.Status == "Resolved" ? DateTimeOffset.UtcNow : null;
            await EndpointHelpers.AuditAsync(db, http, "comment.update", "PackageComment", comment.Id, comment.ReportPackageId, "Updated package comment", before, JsonSerializer.Serialize(comment), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(PackageCommentDto.From(comment));
        });

        // ── Share links ────────────────────────────────────────────────────────────────
        app.MapPost("/api/share-links", async (
            CreateShareLinkRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }
            var link = new ShareLink
            {
                Id = Guid.NewGuid(),
                ReportPackageId = request.ReportPackageId,
                Token = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant(),
                RequirePassword = request.RequirePassword,
                PasswordHash = string.IsNullOrWhiteSpace(request.Password) ? null : EndpointHelpers.HashSecret(request.Password),
                AllowDownload = request.AllowDownload,
                DashboardOnly = request.DashboardOnly,
                ExpiresAt = request.ExpiresAt
            };
            db.ShareLinks.Add(link);
            await EndpointHelpers.AuditAsync(db, http, "share.create", "ShareLink", link.Id, request.ReportPackageId, "Created share link", "{}", JsonSerializer.Serialize(ShareLinkDto.From(link)), ct);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/share/{link.Token}", ShareLinkDto.From(link));
        });

        app.MapGet("/share/{token}", async (string token, AppDbContext db, CancellationToken ct) =>
        {
            var link = await db.ShareLinks.AsNoTracking().FirstOrDefaultAsync(x => x.Token == token, ct);
            if (link is null || link.ExpiresAt < DateTimeOffset.UtcNow)
            {
                return Results.NotFound();
            }
            var package = await db.ReportPackages
                .AsNoTracking()
                .Include(x => x.Organization)
                .Include(x => x.ReportingPeriod)
                .Include(x => x.Slides.OrderBy(s => s.SortOrder))
                    .ThenInclude(s => s.Blocks.OrderBy(b => b.SortOrder))
                .Include(x => x.Issues)
                .FirstOrDefaultAsync(x => x.Id == link.ReportPackageId, ct);
            return package is null ? Results.NotFound() : Results.Ok(new { link = ShareLinkDto.From(link), package = PackageDto.From(package) });
        });

        app.MapPut("/api/share-links/{shareLinkId:guid}", async (
            Guid shareLinkId,
            UpdateShareLinkRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }
            var link = await db.ShareLinks.FirstOrDefaultAsync(x => x.Id == shareLinkId, ct);
            if (link is null)
            {
                return Results.NotFound();
            }
            var before = JsonSerializer.Serialize(link);
            if (request.RequirePassword is not null) link.RequirePassword = request.RequirePassword.Value;
            if (request.AllowDownload is not null) link.AllowDownload = request.AllowDownload.Value;
            if (request.DashboardOnly is not null) link.DashboardOnly = request.DashboardOnly.Value;
            if (request.ExpiresAtSet) link.ExpiresAt = request.ExpiresAt;
            if (!string.IsNullOrWhiteSpace(request.Password)) link.PasswordHash = EndpointHelpers.HashSecret(request.Password);
            link.UpdatedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AuditAsync(db, http, "share.update", "ShareLink", link.Id, link.ReportPackageId, "Updated share link", before, JsonSerializer.Serialize(link), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ShareLinkDto.From(link));
        });

        app.MapDelete("/api/share-links/{shareLinkId:guid}", async (
            Guid shareLinkId,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }
            var link = await db.ShareLinks.FirstOrDefaultAsync(x => x.Id == shareLinkId, ct);
            if (link is null)
            {
                return Results.NotFound();
            }
            var before = JsonSerializer.Serialize(link);
            db.ShareLinks.Remove(link);
            await EndpointHelpers.AuditAsync(db, http, "share.delete", "ShareLink", shareLinkId, link.ReportPackageId, "Deleted share link", before, "{}", ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ── Audit query ────────────────────────────────────────────────────────────────
        app.MapGet("/api/audit", async (Guid? reportPackageId, AppDbContext db, CancellationToken ct) =>
        {
            var query = db.AuditRecords.AsNoTracking().AsQueryable();
            if (reportPackageId is not null)
            {
                query = query.Where(x => x.ReportPackageId == reportPackageId);
            }
            var records = (await query.ToListAsync(ct))
                .OrderByDescending(x => x.CreatedAt)
                .ToList();
            return Results.Ok(records);
        });

        return app;
    }
}
