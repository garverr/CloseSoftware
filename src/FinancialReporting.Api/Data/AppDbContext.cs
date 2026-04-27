using FinancialReporting.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<ReportingPeriod> ReportingPeriods => Set<ReportingPeriod>();
    public DbSet<ReportPackage> ReportPackages => Set<ReportPackage>();
    public DbSet<PackageSlide> PackageSlides => Set<PackageSlide>();
    public DbSet<SlideBlock> SlideBlocks => Set<SlideBlock>();
    public DbSet<KpiDefinition> KpiDefinitions => Set<KpiDefinition>();
    public DbSet<KpiAlert> KpiAlerts => Set<KpiAlert>();
    public DbSet<NonFinancialMetric> NonFinancialMetrics => Set<NonFinancialMetric>();
    public DbSet<ForecastScenario> ForecastScenarios => Set<ForecastScenario>();
    public DbSet<ForecastEvent> ForecastEvents => Set<ForecastEvent>();
    public DbSet<ReportTemplate> ReportTemplates => Set<ReportTemplate>();
    public DbSet<PackageComment> PackageComments => Set<PackageComment>();
    public DbSet<FxRate> FxRates => Set<FxRate>();
    public DbSet<PackageIssue> PackageIssues => Set<PackageIssue>();
    public DbSet<PackageVersion> PackageVersions => Set<PackageVersion>();
    public DbSet<XeroConnection> XeroConnections => Set<XeroConnection>();
    public DbSet<XeroTenantConnection> XeroTenantConnections => Set<XeroTenantConnection>();
    public DbSet<XeroTenantEntityMapping> XeroTenantEntityMappings => Set<XeroTenantEntityMapping>();
    public DbSet<XeroLedgerSyncSetting> XeroLedgerSyncSettings => Set<XeroLedgerSyncSetting>();
    public DbSet<XeroLedgerSyncCursor> XeroLedgerSyncCursors => Set<XeroLedgerSyncCursor>();
    public DbSet<XeroSyncRun> XeroSyncRuns => Set<XeroSyncRun>();
    public DbSet<XeroJournal> XeroJournals => Set<XeroJournal>();
    public DbSet<XeroJournalLine> XeroJournalLines => Set<XeroJournalLine>();
    public DbSet<XeroLedgerMonthlySummary> XeroLedgerMonthlySummaries => Set<XeroLedgerMonthlySummary>();
    public DbSet<XeroRawReportSnapshot> XeroRawReportSnapshots => Set<XeroRawReportSnapshot>();
    public DbSet<XeroTrialBalanceSnapshot> XeroTrialBalanceSnapshots => Set<XeroTrialBalanceSnapshot>();
    public DbSet<XeroLedgerReconciliationRun> XeroLedgerReconciliationRuns => Set<XeroLedgerReconciliationRun>();
    public DbSet<XeroBackfillRun> XeroBackfillRuns => Set<XeroBackfillRun>();
    public DbSet<XeroBackfillTenantTask> XeroBackfillTenantTasks => Set<XeroBackfillTenantTask>();
    public DbSet<FinancialStatementLine> FinancialStatementLines => Set<FinancialStatementLine>();
    public DbSet<StatementRun> StatementRuns => Set<StatementRun>();
    public DbSet<StatementQaResult> StatementQaResults => Set<StatementQaResult>();
    public DbSet<XeroOAuthSession> XeroOAuthSessions => Set<XeroOAuthSession>();
    public DbSet<GlAccount> GlAccounts => Set<GlAccount>();
    public DbSet<GlTransaction> GlTransactions => Set<GlTransaction>();
    public DbSet<FluxReviewGroup> FluxReviewGroups => Set<FluxReviewGroup>();
    public DbSet<AccountMapping> AccountMappings => Set<AccountMapping>();
    public DbSet<FsLineDefinition> FsLineDefinitions => Set<FsLineDefinition>();
    public DbSet<EliminationEntry> EliminationEntries => Set<EliminationEntry>();
    public DbSet<RecurringEliminationRule> RecurringEliminationRules => Set<RecurringEliminationRule>();
    public DbSet<AiRuntimeSetting> AiRuntimeSettings => Set<AiRuntimeSetting>();
    public DbSet<AiRun> AiRuns => Set<AiRun>();
    public DbSet<AiPackageDraftSuggestion> AiPackageDraftSuggestions => Set<AiPackageDraftSuggestion>();
    public DbSet<ExportArtifact> ExportArtifacts => Set<ExportArtifact>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();
    public DbSet<DistributionSchedule> DistributionSchedules => Set<DistributionSchedule>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Organization>().HasIndex(x => x.Key).IsUnique();
        modelBuilder.Entity<ReportingPeriod>().HasIndex(x => x.Key).IsUnique();
        modelBuilder.Entity<ReportPackage>().HasIndex(x => new { x.OrganizationId, x.ReportingPeriodId }).IsUnique();
        modelBuilder.Entity<AiRuntimeSetting>().HasIndex(x => x.Module).IsUnique();
        modelBuilder.Entity<KpiAlert>().HasIndex(x => new { x.KpiDefinitionId, x.IsActive });
        modelBuilder.Entity<NonFinancialMetric>().HasIndex(x => new { x.OrganizationId, x.ReportingPeriodId, x.Name }).IsUnique();
        modelBuilder.Entity<ForecastScenario>().HasIndex(x => new { x.OrganizationId, x.ReportingPeriodId, x.Name }).IsUnique();
        modelBuilder.Entity<ForecastEvent>().HasIndex(x => new { x.ForecastScenarioId, x.MonthOffset });
        modelBuilder.Entity<ReportTemplate>().HasIndex(x => x.Name).IsUnique();
        modelBuilder.Entity<PackageComment>().HasIndex(x => new { x.ReportPackageId, x.PackageSlideId, x.Status });
        modelBuilder.Entity<FxRate>().HasIndex(x => new { x.OrganizationId, x.ReportingPeriodId, x.CurrencyCode }).IsUnique();
        modelBuilder.Entity<XeroConnection>().HasIndex(x => new { x.OrganizationId, x.TenantId }).IsUnique();
        modelBuilder.Entity<XeroTenantConnection>().HasIndex(x => x.TenantId).IsUnique();
        modelBuilder.Entity<XeroTenantEntityMapping>().HasIndex(x => x.TenantId).IsUnique();
        modelBuilder.Entity<XeroLedgerSyncCursor>().HasIndex(x => x.TenantId).IsUnique();
        modelBuilder.Entity<XeroJournal>().HasIndex(x => new { x.TenantId, x.XeroJournalId }).IsUnique();
        modelBuilder.Entity<XeroJournal>().HasIndex(x => new { x.TenantId, x.JournalNumber });
        modelBuilder.Entity<XeroJournalLine>().HasIndex(x => new { x.TenantId, x.AccountCode });
        modelBuilder.Entity<XeroLedgerMonthlySummary>().HasIndex(x => new { x.TenantId, x.MonthKey, x.AccountCode }).IsUnique();
        modelBuilder.Entity<XeroOAuthSession>().HasIndex(x => x.State).IsUnique();
        modelBuilder.Entity<XeroRawReportSnapshot>().HasIndex(x => new { x.OrganizationId, x.ReportingPeriodId, x.TenantId, x.ReportType });
        modelBuilder.Entity<XeroTrialBalanceSnapshot>().HasIndex(x => new { x.TenantId, x.SnapshotDate });
        modelBuilder.Entity<XeroLedgerReconciliationRun>().HasIndex(x => new { x.TenantId, x.SnapshotDate });
        modelBuilder.Entity<XeroBackfillRun>().HasIndex(x => x.Status);
        modelBuilder.Entity<XeroBackfillTenantTask>().HasIndex(x => new { x.XeroBackfillRunId, x.TenantId }).IsUnique();
        modelBuilder.Entity<XeroBackfillTenantTask>().HasIndex(x => x.Status);
        modelBuilder.Entity<FinancialStatementLine>().HasIndex(x => new { x.OrganizationId, x.ReportingPeriodId, x.StatementType, x.SortOrder });
        modelBuilder.Entity<StatementRun>().HasIndex(x => new { x.OrganizationId, x.ReportingPeriodId, x.TenantId });
        modelBuilder.Entity<StatementQaResult>().HasIndex(x => x.ReportPackageId);
        modelBuilder.Entity<GlAccount>().HasIndex(x => new { x.OrganizationId, x.ReportingPeriodId, x.TenantId, x.Code }).IsUnique();
        modelBuilder.Entity<FluxReviewGroup>().HasIndex(x => new { x.ReportPackageId, x.FluxType, x.StatementType, x.GroupKey }).IsUnique();
        modelBuilder.Entity<AccountMapping>().HasIndex(x => new { x.OrganizationId, x.ReportingPeriodId, x.FsLine });
        modelBuilder.Entity<FsLineDefinition>().HasIndex(x => new { x.OrganizationId, x.StatementType, x.Name }).IsUnique();
        modelBuilder.Entity<AiPackageDraftSuggestion>().HasIndex(x => new { x.ReportPackageId, x.Status });
        modelBuilder.Entity<AuditRecord>().HasIndex(x => new { x.EntityType, x.EntityId });
        modelBuilder.Entity<AuditRecord>().HasIndex(x => x.ReportPackageId);
        modelBuilder.Entity<RecurringEliminationRule>().HasIndex(x => new { x.OrganizationId, x.ReportingPeriodId, x.IsActive });

        modelBuilder.Entity<ReportPackage>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<PackageIssue>().Property(x => x.Severity).HasConversion<string>();
        modelBuilder.Entity<PackageIssue>().Property(x => x.Status).HasConversion<string>();
        modelBuilder.Entity<GlAccount>().Property(x => x.ReviewStatus).HasConversion<string>();
        modelBuilder.Entity<GlAccount>().Property(x => x.ConsolidationTreatment).HasConversion<string>();
        modelBuilder.Entity<AiRun>().Property(x => x.Status).HasConversion<string>();

        foreach (var property in modelBuilder.Model.GetEntityTypes()
                     .SelectMany(t => t.GetProperties())
                     .Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
        {
            property.SetPrecision(18);
            property.SetScale(2);
        }
    }
}
