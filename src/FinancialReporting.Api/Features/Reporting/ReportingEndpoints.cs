using System.Text.Json;
using FinancialReporting.Api.Common;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Features.Reporting;

/// <summary>
/// Reporting-context, statements, ledger-summary, packages list, entity periods,
/// and non-financial-metrics endpoints.
/// Extracted from Program.cs (lines 273–393, 398–421, 423–489, 2579–2659). Cat 47.
/// </summary>
public static class ReportingEndpoints
{
    public static IEndpointRouteBuilder MapReportingEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Reporting context ────────────────────────────────────────────────────────────
        app.MapGet("/api/reporting-context", async (string? organizationKey, AppDbContext db, CancellationToken ct) =>
        {
            var mappings = await db.XeroTenantEntityMappings
                .AsNoTracking()
                .Where(x => !x.IsIgnored)
                .ToListAsync(ct);
            var mappedOrganizationIds = mappings
                .Select(x => x.OrganizationId)
                .ToHashSet();

            var organizations = await db.Organizations
                .AsNoTracking()
                .OrderBy(x => x.Name)
                .Select(x => new OrganizationOptionDto(x.Id, x.Key, x.Name, x.Abbreviation, x.IsConsolidated, mappedOrganizationIds.Contains(x.Id)))
                .ToListAsync(ct);

            var minVisiblePeriod = new DateOnly(2025, 1, 1);
            var maxVisiblePeriod = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
            var periods = await db.ReportingPeriods
                .AsNoTracking()
                .Where(x => x.PeriodStart >= minVisiblePeriod && x.PeriodStart <= maxVisiblePeriod)
                .ToListAsync(ct);
            var packageRows = await db.ReportPackages
                .AsNoTracking()
                .Include(x => x.Organization)
                .Include(x => x.ReportingPeriod)
                .ToListAsync(ct);
            var journalRows = await db.XeroJournals
                .AsNoTracking()
                .Select(x => new { x.TenantId, x.JournalDate })
                .ToListAsync(ct);

            var packageCountsByPeriod = packageRows
                .Where(x => x.ReportingPeriod is not null)
                .GroupBy(x => x.ReportingPeriod!.Key)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
            var ledgerCountsByPeriod = journalRows
                .GroupBy(x => $"{x.JournalDate.Year:D4}-{x.JournalDate.Month:D2}")
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

            var scopedPeriodKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(organizationKey))
            {
                var selectedOrg = organizations.FirstOrDefault(x => x.Key == organizationKey);
                if (selectedOrg is not null)
                {
                    var tenantIds = mappings
                        .Where(x => x.OrganizationId == selectedOrg.Id)
                        .Select(x => x.TenantId)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    scopedPeriodKeys = journalRows
                        .Where(x => tenantIds.Contains(x.TenantId))
                        .Select(x => $"{x.JournalDate.Year:D4}-{x.JournalDate.Month:D2}")
                        .Concat(packageRows.Where(x => x.OrganizationId == selectedOrg.Id && x.ReportingPeriod is not null).Select(x => x.ReportingPeriod!.Key))
                        .Concat(await db.FinancialStatementLines.AsNoTracking().Where(x => x.OrganizationId == selectedOrg.Id).Select(x => x.ReportingPeriodId).Join(db.ReportingPeriods.AsNoTracking(), id => id, period => period.Id, (id, period) => period.Key).ToListAsync(ct))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
            }

            var periodOptions = periods
                .Where(x => x.PeriodStart >= minVisiblePeriod && x.PeriodStart <= maxVisiblePeriod)
                .Where(x => string.IsNullOrWhiteSpace(organizationKey) || scopedPeriodKeys.Contains(x.Key))
                .OrderByDescending(x => x.PeriodStart)
                .Select(x => new PeriodOptionDto(
                    x.Id,
                    x.Key,
                    x.Label,
                    x.PeriodStart,
                    x.PeriodEnd,
                    x.IsClosed,
                    packageCountsByPeriod.GetValueOrDefault(x.Key),
                    ledgerCountsByPeriod.GetValueOrDefault(x.Key)))
                .ToList();

            var orgKeysById = organizations.ToDictionary(x => x.Id, x => x.Key);
            var mappedOrgKeysByTenant = mappings
                .Where(x => orgKeysById.ContainsKey(x.OrganizationId))
                .GroupBy(x => x.TenantId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => orgKeysById[x.First().OrganizationId], StringComparer.OrdinalIgnoreCase);
            var ledgerCountsByOrgPeriod = journalRows
                .Where(x => mappedOrgKeysByTenant.ContainsKey(x.TenantId))
                .GroupBy(x => new
                {
                    OrganizationKey = mappedOrgKeysByTenant[x.TenantId],
                    PeriodKey = $"{x.JournalDate.Year:D4}-{x.JournalDate.Month:D2}"
                })
                .ToDictionary(x => $"{x.Key.OrganizationKey}|{x.Key.PeriodKey}", x => x.Count(), StringComparer.OrdinalIgnoreCase);
            var packagesByOrgPeriod = packageRows
                .Where(x => x.Organization is not null && x.ReportingPeriod is not null)
                .GroupBy(x => $"{x.Organization!.Key}|{x.ReportingPeriod!.Key}", StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

            var packageOptions = packageRows
                .Where(x => x.Organization is not null && x.ReportingPeriod is not null)
                .OrderBy(x => x.Organization!.Name)
                .ThenByDescending(x => x.ReportingPeriod!.PeriodStart)
                .Select(x => new PackageOptionDto(
                    x.Id,
                    x.Organization!.Key,
                    x.Organization.Name,
                    x.ReportingPeriod!.Key,
                    x.ReportingPeriod.Label,
                    x.Status.ToString()))
                .ToList();

            var coverage = organizations
                .SelectMany(org => periodOptions.Select(period =>
                {
                    var key = $"{org.Key}|{period.Key}";
                    packagesByOrgPeriod.TryGetValue(key, out var package);
                    return new ReportingCoverageDto(
                        org.Key,
                        period.Key,
                        package?.Id,
                        package?.Status.ToString(),
                        ledgerCountsByOrgPeriod.GetValueOrDefault(key));
                }))
                .ToList();

            return Results.Ok(new ReportingContextDto(organizations, periodOptions, packageOptions, coverage));
        });

        // ── Packages list ────────────────────────────────────────────────────────────────
        app.MapGet("/api/packages", async (string? periodKey, AppDbContext db, CancellationToken ct) =>
        {
            var query = db.ReportPackages
                .AsNoTracking()
                .Include(x => x.Organization)
                .Include(x => x.ReportingPeriod)
                .Include(x => x.Slides.OrderBy(s => s.SortOrder))
                    .ThenInclude(s => s.Blocks.OrderBy(b => b.SortOrder))
                .Include(x => x.Issues)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(periodKey))
            {
                query = query.Where(x => x.ReportingPeriod!.Key == periodKey);
            }

            var packages = await query
                .OrderByDescending(x => x.ReportingPeriod!.PeriodStart)
                .ThenBy(x => x.Organization!.Name)
                .Select(x => PackageDto.From(x))
                .ToListAsync(ct);

            return Results.Ok(packages);
        });

        // ── Entity periods ───────────────────────────────────────────────────────────────
        app.MapGet("/api/entities/{organizationKey}/periods", async (string organizationKey, AppDbContext db, CancellationToken ct) =>
        {
            var org = await db.Organizations.AsNoTracking().FirstOrDefaultAsync(x => x.Key == organizationKey, ct);
            if (org is null)
            {
                return Results.NotFound();
            }

            var contextResult = await BuildEntityPeriodsAsync(db, org.Id, ct);
            return Results.Ok(contextResult);
        });

        // ── Entity period statements ─────────────────────────────────────────────────────
        app.MapGet("/api/entities/{organizationKey}/periods/{periodKey}/statements", async (
            string organizationKey,
            string periodKey,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var org = await db.Organizations.AsNoTracking().FirstOrDefaultAsync(x => x.Key == organizationKey, ct);
            var period = await db.ReportingPeriods.AsNoTracking().FirstOrDefaultAsync(x => x.Key == periodKey, ct);
            if (org is null || period is null)
            {
                return Results.NotFound();
            }

            var lines = await db.FinancialStatementLines
                .AsNoTracking()
                .Where(x => x.OrganizationId == org.Id && x.ReportingPeriodId == period.Id)
                .OrderBy(x => x.StatementType)
                .ThenBy(x => x.SortOrder)
                .Select(x => new StatementLineDto(x.StatementType, x.Section, x.RowPath, x.LineName, x.AccountCode, x.CurrentAmount, x.PriorAmount, x.AmountsJson))
                .ToListAsync(ct);
            return Results.Ok(new EntityPeriodStatementsDto(org.Key, period.Key, lines));
        });

        // ── Entity period ledger summary ─────────────────────────────────────────────────
        app.MapGet("/api/entities/{organizationKey}/periods/{periodKey}/ledger-summary", async (
            string organizationKey,
            string periodKey,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var org = await db.Organizations.AsNoTracking().FirstOrDefaultAsync(x => x.Key == organizationKey, ct);
            var period = await db.ReportingPeriods.AsNoTracking().FirstOrDefaultAsync(x => x.Key == periodKey, ct);
            if (org is null || period is null)
            {
                return Results.NotFound();
            }

            var tenantIds = await db.XeroTenantEntityMappings
                .AsNoTracking()
                .Where(x => x.OrganizationId == org.Id && !x.IsIgnored)
                .Select(x => x.TenantId)
                .ToListAsync(ct);
            var journals = await db.XeroJournals
                .AsNoTracking()
                .Include(x => x.Lines)
                .Where(x => tenantIds.Contains(x.TenantId) && x.JournalDate >= period.PeriodStart && x.JournalDate <= period.PeriodEnd)
                .ToListAsync(ct);
            var rows = journals
                .SelectMany(x => x.Lines.Select(line => new { x.JournalDate, line.AccountCode, line.AccountName, line.NetAmount }))
                .ToList();
            var summary = rows
                .GroupBy(x => new { x.AccountCode, x.AccountName })
                .OrderByDescending(x => Math.Abs(x.Sum(row => row.NetAmount)))
                .Select(x => new LedgerSummaryLineDto(x.Key.AccountCode, x.Key.AccountName, x.Sum(row => row.NetAmount), x.Count()))
                .ToList();
            return Results.Ok(new EntityPeriodLedgerSummaryDto(org.Key, period.Key, rows.Count, summary));
        });

        // Non-financial metrics endpoints owned by Features/Planning/PlanningEndpoints.cs.
        // Removed from this file to avoid double registration.

        return app;
    }

    /// <summary>
    /// Builds the list of visible reporting periods for a given organization, merging
    /// journal-date keys, package links, and statement-line links.
    /// Copied from Program.cs top-level static (line 3650) — keep in sync until Program.cs
    /// call site is redirected here and the original removed.
    /// </summary>
    private static async Task<List<PeriodOptionDto>> BuildEntityPeriodsAsync(AppDbContext db, Guid organizationId, CancellationToken ct)
    {
        var tenantIds = await db.XeroTenantEntityMappings
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && !x.IsIgnored)
            .Select(x => x.TenantId)
            .ToListAsync(ct);
        var periodKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in await db.XeroJournals
                     .AsNoTracking()
                     .Where(x => tenantIds.Contains(x.TenantId))
                     .Where(x => x.JournalDate >= new DateOnly(2025, 1, 1) && x.JournalDate <= DateOnly.FromDateTime(DateTime.UtcNow.Date))
                     .Select(x => $"{x.JournalDate.Year:D4}-{x.JournalDate.Month:D2}")
                     .Distinct()
                     .ToListAsync(ct))
        {
            periodKeys.Add(key);
        }
        foreach (var key in await db.ReportPackages
                     .AsNoTracking()
                     .Where(x => x.OrganizationId == organizationId)
                     .Join(db.ReportingPeriods.AsNoTracking(), package => package.ReportingPeriodId, period => period.Id, (package, period) => period.Key)
                     .Distinct()
                     .ToListAsync(ct))
        {
            periodKeys.Add(key);
        }
        foreach (var key in await db.FinancialStatementLines
                     .AsNoTracking()
                     .Where(x => x.OrganizationId == organizationId)
                     .Join(db.ReportingPeriods.AsNoTracking(), line => line.ReportingPeriodId, period => period.Id, (line, period) => period.Key)
                     .Distinct()
                     .ToListAsync(ct))
        {
            periodKeys.Add(key);
        }

        var packageCounts = await db.ReportPackages
            .AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .GroupBy(x => x.ReportingPeriodId)
            .Select(x => new { PeriodId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.PeriodId, x => x.Count, ct);
        var journalCountSourceRows = await db.XeroJournals
            .AsNoTracking()
            .Where(x => tenantIds.Contains(x.TenantId))
            .ToListAsync(ct);
        var journalCounts = journalCountSourceRows
            .GroupBy(x => $"{x.JournalDate.Year:D4}-{x.JournalDate.Month:D2}")
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);

        var minVisiblePeriodStart = new DateOnly(2025, 1, 1);
        var maxVisiblePeriodStart = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var periods = await db.ReportingPeriods
            .AsNoTracking()
            .Where(x => periodKeys.Contains(x.Key))
            .ToListAsync(ct);

        return periods
            .Where(x => x.PeriodStart >= minVisiblePeriodStart && x.PeriodStart <= maxVisiblePeriodStart)
            .OrderByDescending(x => x.PeriodStart)
            .Select(x => new PeriodOptionDto(
                x.Id,
                x.Key,
                x.Label,
                x.PeriodStart,
                x.PeriodEnd,
                x.IsClosed,
                packageCounts.GetValueOrDefault(x.Id),
                journalCounts.GetValueOrDefault(x.Key)))
            .ToList();
    }
}
