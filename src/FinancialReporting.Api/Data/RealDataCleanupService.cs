using FinancialReporting.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Data;

public static class RealDataCleanupService
{
    public static async Task PurgeRuntimeMockDataAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var mappedOrganizationIds = await db.XeroTenantEntityMappings
            .AsNoTracking()
            .Where(x => !x.IsIgnored)
            .Select(x => x.OrganizationId)
            .ToListAsync(cancellationToken);
        if (mappedOrganizationIds.Count == 0)
        {
            return;
        }

        var mapped = mappedOrganizationIds.ToHashSet();
        var demoPackageIds = await db.ReportPackages
            .AsNoTracking()
            .Where(x => !mapped.Contains(x.OrganizationId))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        if (demoPackageIds.Count > 0)
        {
            await db.SlideBlocks.Where(x => demoPackageIds.Contains(x.PackageSlide!.ReportPackageId)).ExecuteDeleteAsync(cancellationToken);
            await db.PackageSlides.Where(x => demoPackageIds.Contains(x.ReportPackageId)).ExecuteDeleteAsync(cancellationToken);
            await db.PackageIssues.Where(x => demoPackageIds.Contains(x.ReportPackageId)).ExecuteDeleteAsync(cancellationToken);
            await db.PackageVersions.Where(x => demoPackageIds.Contains(x.ReportPackageId)).ExecuteDeleteAsync(cancellationToken);
            await db.FluxReviewGroups.Where(x => demoPackageIds.Contains(x.ReportPackageId)).ExecuteDeleteAsync(cancellationToken);
            await db.AiPackageDraftSuggestions.Where(x => demoPackageIds.Contains(x.ReportPackageId)).ExecuteDeleteAsync(cancellationToken);
            await db.AiRuns.Where(x => x.ReportPackageId != null && demoPackageIds.Contains(x.ReportPackageId.Value)).ExecuteDeleteAsync(cancellationToken);
            await db.ExportArtifacts.Where(x => demoPackageIds.Contains(x.ReportPackageId)).ExecuteDeleteAsync(cancellationToken);
            await db.ShareLinks.Where(x => demoPackageIds.Contains(x.ReportPackageId)).ExecuteDeleteAsync(cancellationToken);
            await db.DistributionSchedules.Where(x => demoPackageIds.Contains(x.ReportPackageId)).ExecuteDeleteAsync(cancellationToken);
            await db.ReportPackages.Where(x => demoPackageIds.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        var demoOrgIds = await db.Organizations
            .AsNoTracking()
            .Where(x => !mapped.Contains(x.Id))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        if (demoOrgIds.Count > 0)
        {
            await db.GlTransactions.Where(x => demoOrgIds.Contains(x.GlAccount!.OrganizationId)).ExecuteDeleteAsync(cancellationToken);
            await db.GlAccounts.Where(x => demoOrgIds.Contains(x.OrganizationId)).ExecuteDeleteAsync(cancellationToken);
            await db.AccountMappings.Where(x => demoOrgIds.Contains(x.OrganizationId)).ExecuteDeleteAsync(cancellationToken);
            await db.FsLineDefinitions.Where(x => demoOrgIds.Contains(x.OrganizationId)).ExecuteDeleteAsync(cancellationToken);
            await db.EliminationEntries.Where(x => demoOrgIds.Contains(x.OrganizationId)).ExecuteDeleteAsync(cancellationToken);
            await db.RecurringEliminationRules.Where(x => demoOrgIds.Contains(x.OrganizationId)).ExecuteDeleteAsync(cancellationToken);
            await db.KpiDefinitions.Where(x => demoOrgIds.Contains(x.OrganizationId)).ExecuteDeleteAsync(cancellationToken);
            await db.XeroConnections.Where(x => demoOrgIds.Contains(x.OrganizationId)).ExecuteDeleteAsync(cancellationToken);
            await db.Organizations.Where(x => demoOrgIds.Contains(x.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        var validPeriodIds = await BuildDataBackedPeriodIdsAsync(db, cancellationToken);
        await db.ReportingPeriods
            .Where(x => !validPeriodIds.Contains(x.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task<HashSet<Guid>> BuildDataBackedPeriodIdsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid>();
        foreach (var id in await db.ReportPackages.AsNoTracking().Select(x => x.ReportingPeriodId).ToListAsync(cancellationToken))
        {
            ids.Add(id);
        }
        foreach (var id in await db.FinancialStatementLines.AsNoTracking().Select(x => x.ReportingPeriodId).ToListAsync(cancellationToken))
        {
            ids.Add(id);
        }
        foreach (var id in await db.XeroRawReportSnapshots.AsNoTracking().Select(x => x.ReportingPeriodId).ToListAsync(cancellationToken))
        {
            ids.Add(id);
        }
        foreach (var id in await db.XeroTrialBalanceSnapshots.AsNoTracking().Where(x => x.ReportingPeriodId != null).Select(x => x.ReportingPeriodId!.Value).ToListAsync(cancellationToken))
        {
            ids.Add(id);
        }
        foreach (var id in await db.XeroLedgerReconciliationRuns.AsNoTracking().Where(x => x.ReportingPeriodId != null).Select(x => x.ReportingPeriodId!.Value).ToListAsync(cancellationToken))
        {
            ids.Add(id);
        }

        var periodIdsByKey = await db.ReportingPeriods.AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Id, cancellationToken);
        var journalPeriodKeys = await db.XeroJournals
            .AsNoTracking()
            .Select(x => $"{x.JournalDate.Year:D4}-{x.JournalDate.Month:D2}")
            .Distinct()
            .ToListAsync(cancellationToken);
        foreach (var key in journalPeriodKeys)
        {
            if (periodIdsByKey.TryGetValue(key, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
