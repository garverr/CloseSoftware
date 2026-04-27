using System.Text.Json;
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
/// P3.27 — first feature-folder extraction. Owns the marquee Board Package endpoints:
/// the diff engine read API, CFO approval, and unapproval. Establishes the pattern
/// (RouteGroupBuilder per feature, mapping helper called from Program.cs) without
/// touching the rest of the 5,000-line Program.cs in this PR. Cat 29.
/// </summary>
public static class PackageApprovalEndpoints
{
    public static IEndpointRouteBuilder MapPackageApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/packages");

        // GET diff — exposes typed Keep/Modify/Add/Remove decisions for the frontend.
        group.MapGet("/{packageId:guid}/diff-v2", async (
            Guid packageId,
            PackageDiffService diff,
            CancellationToken ct) =>
        {
            var result = await diff.ComputeAsync(packageId, ct);
            return Results.Ok(new
            {
                packageId = result.PackageId,
                priorPackageId = result.PriorPackageId,
                boardDollarThreshold = result.BoardDollarThreshold,
                boardPercentThreshold = result.BoardPercentThreshold,
                decisions = result.Decisions.Select(d => new
                {
                    kind = d.Kind.ToString(),
                    priorSlideId = d.PriorSlideId,
                    currentSlideId = d.CurrentSlideId,
                    currentFluxGroupId = d.CurrentFluxGroupId,
                    subject = d.Subject,
                    currentValue = d.CurrentValue,
                    priorValue = d.PriorValue,
                    varianceAmount = d.VarianceAmount,
                    variancePercent = d.VariancePercent,
                    rationale = d.Rationale
                })
            });
        });

        // P1.16 — CFO approval gate. Stamps ApprovedBy/ApprovedAt, transitions to Final,
        // and creates a tagged immutable PackageVersion snapshot. Cat 26.
        app.MapPost("/api/packages/{packageId:guid}/approve", async (
            Guid packageId,
            HttpContext http,
            AppDbContext db,
            PackageSnapshotBuilder snapshotBuilder,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "CFO"))
            {
                return Results.Forbid();
            }
            var pkg = await db.ReportPackages.FirstOrDefaultAsync(x => x.Id == packageId, ct);
            if (pkg is null)
            {
                return Results.NotFound();
            }
            if (pkg.IsApproved)
            {
                return Results.Conflict(new { error = "Package is already approved.", pkg.ApprovedBy, pkg.ApprovedAt, pkg.ApprovedVersionId });
            }
            var snapshotJson = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct);
            var version = new PackageVersion
            {
                Id = Guid.NewGuid(),
                ReportPackageId = packageId,
                VersionLabel = $"CFO Approved {DateTimeOffset.UtcNow:yyyyMMdd-HHmm}",
                CreatedBy = EndpointHelpers.Actor(http),
                ChangeSummary = "CFO approval — immutable snapshot tagged for board distribution.",
                SnapshotJson = snapshotJson
            };
            db.PackageVersions.Add(version);
            pkg.IsApproved = true;
            pkg.ApprovedBy = EndpointHelpers.Actor(http);
            pkg.ApprovedAt = DateTimeOffset.UtcNow;
            pkg.ApprovedVersionId = version.Id;
            pkg.Status = PackageStatus.Final;
            pkg.UpdatedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AuditAsync(db, http, "package.approve", "ReportPackage", packageId, packageId, "CFO approved package; snapshot locked.", "{}", JsonSerializer.Serialize(new { pkg.ApprovedBy, pkg.ApprovedAt, pkg.ApprovedVersionId }), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { packageId, version.Id, pkg.ApprovedBy, pkg.ApprovedAt, status = pkg.Status.ToString() });
        });

        app.MapPost("/api/packages/{packageId:guid}/unapprove", async (
            Guid packageId,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin"))
            {
                return Results.Forbid();
            }
            var pkg = await db.ReportPackages.FirstOrDefaultAsync(x => x.Id == packageId, ct);
            if (pkg is null)
            {
                return Results.NotFound();
            }
            if (!pkg.IsApproved)
            {
                return Results.NoContent();
            }
            var before = JsonSerializer.Serialize(new { pkg.ApprovedBy, pkg.ApprovedAt, pkg.ApprovedVersionId });
            pkg.IsApproved = false;
            pkg.ApprovedBy = "";
            pkg.ApprovedAt = null;
            pkg.ApprovedVersionId = null;
            pkg.Status = PackageStatus.Review;
            pkg.UpdatedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AuditAsync(db, http, "package.unapprove", "ReportPackage", packageId, packageId, "Approval rescinded.", before, "{}", ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { packageId, status = pkg.Status.ToString() });
        });

        return app;
    }
}
