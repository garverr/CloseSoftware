using System.Text.Json;
using FinancialReporting.Api.Common;
using FinancialReporting.Api.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Features.ReportingPeriods;

public static class ReportingPeriodEndpoints
{
    public static IEndpointRouteBuilder MapReportingPeriodEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/reporting-periods", async (
            CreateReportingPeriodRequest request,
            HttpContext http,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!EndpointHelpers.Can(http, "Admin", "Finance Editor"))
            {
                return Results.Forbid();
            }
            if (!EndpointHelpers.TryParsePeriodKey(request.PeriodKey, out var year, out var month))
            {
                return Results.BadRequest(new { error = "PeriodKey must be in YYYY-MM format." });
            }
            var existing = await db.ReportingPeriods.FirstOrDefaultAsync(x => x.Key == request.PeriodKey, ct);
            if (existing is not null)
            {
                return Results.Ok(new PeriodOptionDto(existing.Id, existing.Key, existing.Label, existing.PeriodStart, existing.PeriodEnd, existing.IsClosed, 0, 0));
            }
            var period = EndpointHelpers.BuildReportingPeriod(year, month, request.IsClosed ?? false);
            db.ReportingPeriods.Add(period);
            await EndpointHelpers.AuditAsync(db, http, "period.create", "ReportingPeriod", period.Id, null, "Created reporting period", "{}", JsonSerializer.Serialize(period), ct);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/reporting-periods/{period.Key}", new PeriodOptionDto(period.Id, period.Key, period.Label, period.PeriodStart, period.PeriodEnd, period.IsClosed, 0, 0));
        });

        return app;
    }
}

// CreateReportingPeriodRequest + PeriodOptionDto are still defined in Program.cs (used by
// many other endpoints). The shared types will be moved here once the consumers have all
// migrated to feature folders.
