using System.Text.Json;
using FinancialReporting.Api.Common;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Features.Distribution;

public static class DistributionEndpoints
{
    public static IEndpointRouteBuilder MapDistributionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/distribution-schedules", async (
            CreateDistributionScheduleRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }
            var schedule = new DistributionSchedule
            {
                Id = Guid.NewGuid(),
                ReportPackageId = request.ReportPackageId,
                RecipientsCsv = string.Join(",", request.Recipients),
                Cadence = request.Cadence,
                IncludePdf = request.IncludePdf,
                IncludeExcel = request.IncludeExcel,
                NextRunAt = request.NextRunAt
            };
            db.DistributionSchedules.Add(schedule);
            await EndpointHelpers.AuditAsync(db, http, "distribution.create", "DistributionSchedule", schedule.Id, request.ReportPackageId, "Created distribution schedule", "{}", JsonSerializer.Serialize(schedule), ct);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/distribution-schedules/{schedule.Id}", schedule);
        });

        app.MapPut("/api/distribution-schedules/{scheduleId:guid}", async (
            Guid scheduleId,
            CreateDistributionScheduleRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }
            var schedule = await db.DistributionSchedules.FirstOrDefaultAsync(x => x.Id == scheduleId, ct);
            if (schedule is null)
            {
                return Results.NotFound();
            }
            var before = JsonSerializer.Serialize(schedule);
            schedule.RecipientsCsv = string.Join(",", request.Recipients);
            schedule.Cadence = request.Cadence;
            schedule.IncludePdf = request.IncludePdf;
            schedule.IncludeExcel = request.IncludeExcel;
            schedule.NextRunAt = request.NextRunAt;
            schedule.UpdatedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AuditAsync(db, http, "distribution.update", "DistributionSchedule", schedule.Id, schedule.ReportPackageId, "Updated distribution schedule", before, JsonSerializer.Serialize(schedule), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(schedule);
        });

        app.MapPost("/api/distribution-schedules/{scheduleId:guid}/send-test", async (
            Guid scheduleId,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }
            var schedule = await db.DistributionSchedules.FirstOrDefaultAsync(x => x.Id == scheduleId, ct);
            if (schedule is null)
            {
                return Results.NotFound();
            }
            // P1.16 — block distribution unless the package has been approved by a CFO. Cat 26.
            var pkg = await db.ReportPackages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == schedule.ReportPackageId, ct);
            if (pkg is null || !pkg.IsApproved)
            {
                return Results.BadRequest(new { error = "Package must be approved (POST /api/packages/{id}/approve) before distribution." });
            }
            var before = JsonSerializer.Serialize(schedule);
            schedule.LastTestSentAt = DateTimeOffset.UtcNow;
            schedule.Status = "TestSent";
            schedule.UpdatedAt = DateTimeOffset.UtcNow;
            await EndpointHelpers.AuditAsync(db, http, "distribution.send-test", "DistributionSchedule", schedule.Id, schedule.ReportPackageId, "Manual test package send", before, JsonSerializer.Serialize(schedule), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { schedule.Id, schedule.LastTestSentAt, mode = "configured-email-adapter" });
        });

        return app;
    }
}

public sealed record CreateDistributionScheduleRequest(Guid ReportPackageId, string[] Recipients, string Cadence, bool IncludePdf, bool IncludeExcel, DateTimeOffset? NextRunAt);
