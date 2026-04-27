using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Data;

public static class SqliteSchemaPatch
{
    public static async Task EnsureAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsSqlite())
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "AuditRecords" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AuditRecords" PRIMARY KEY,
                "Actor" TEXT NOT NULL,
                "Role" TEXT NOT NULL,
                "Action" TEXT NOT NULL,
                "EntityType" TEXT NOT NULL,
                "EntityId" TEXT NULL,
                "ReportPackageId" TEXT NULL,
                "Reason" TEXT NOT NULL,
                "BeforeJson" TEXT NOT NULL,
                "AfterJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "XeroOAuthSessions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_XeroOAuthSessions" PRIMARY KEY,
                "OrganizationId" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "ProtectedCodeVerifier" TEXT NOT NULL,
                "FlowType" TEXT NOT NULL,
                "ExpiresAt" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "CodeConsumedAt" TEXT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "RecurringEliminationRules" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_RecurringEliminationRules" PRIMARY KEY,
                "OrganizationId" TEXT NOT NULL,
                "ReportingPeriodId" TEXT NOT NULL,
                "GlAccountId" TEXT NULL,
                "Type" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "CriteriaJson" TEXT NOT NULL,
                "Reason" TEXT NOT NULL,
                "IsActive" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "XeroTenantConnections" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_XeroTenantConnections" PRIMARY KEY,
                "TenantId" TEXT NOT NULL,
                "TenantName" TEXT NOT NULL,
                "TenantType" TEXT NOT NULL,
                "EncryptedAccessToken" TEXT NOT NULL,
                "EncryptedRefreshToken" TEXT NOT NULL,
                "TokenExpiresAt" TEXT NOT NULL,
                "Scopes" TEXT NOT NULL,
                "ConnectionStatus" TEXT NOT NULL,
                "RequiresReconnectForLedger" INTEGER NOT NULL,
                "LastConnectedAt" TEXT NULL,
                "LastError" TEXT NULL,
                "Source" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "XeroTenantEntityMappings" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_XeroTenantEntityMappings" PRIMARY KEY,
                "TenantId" TEXT NOT NULL,
                "OrganizationId" TEXT NOT NULL,
                "IsIgnored" INTEGER NOT NULL,
                "Reason" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "XeroLedgerSyncSettings" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_XeroLedgerSyncSettings" PRIMARY KEY,
                "Enabled" INTEGER NOT NULL,
                "SyncEveryMinutes" INTEGER NOT NULL,
                "DailyTrialBalanceHourUtc" INTEGER NOT NULL,
                "RetentionYears" INTEGER NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "XeroLedgerSyncCursors" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_XeroLedgerSyncCursors" PRIMARY KEY,
                "TenantId" TEXT NOT NULL,
                "LastJournalNumber" INTEGER NULL,
                "LastSyncedAt" TEXT NULL,
                "LastSuccessfulSyncAt" TEXT NULL,
                "Status" TEXT NOT NULL,
                "LastError" TEXT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "XeroJournals" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_XeroJournals" PRIMARY KEY,
                "TenantId" TEXT NOT NULL,
                "XeroJournalId" TEXT NOT NULL,
                "JournalNumber" INTEGER NOT NULL,
                "JournalDate" TEXT NOT NULL,
                "CreatedDateUtc" TEXT NULL,
                "SourceType" TEXT NOT NULL,
                "Reference" TEXT NOT NULL,
                "PayloadJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "XeroJournalLines" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_XeroJournalLines" PRIMARY KEY,
                "XeroJournalId" TEXT NOT NULL,
                "TenantId" TEXT NOT NULL,
                "SourceLineId" TEXT NOT NULL,
                "AccountCode" TEXT NOT NULL,
                "AccountName" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "NetAmount" TEXT NOT NULL,
                "GrossAmount" TEXT NOT NULL,
                "TaxAmount" TEXT NOT NULL,
                "TrackingJson" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "XeroLedgerMonthlySummaries" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_XeroLedgerMonthlySummaries" PRIMARY KEY,
                "TenantId" TEXT NOT NULL,
                "OrganizationId" TEXT NOT NULL,
                "MonthKey" TEXT NOT NULL,
                "AccountCode" TEXT NOT NULL,
                "AccountName" TEXT NOT NULL,
                "NetAmount" TEXT NOT NULL,
                "LastRolledUpAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "XeroRawReportSnapshots" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_XeroRawReportSnapshots" PRIMARY KEY,
                "OrganizationId" TEXT NOT NULL,
                "ReportingPeriodId" TEXT NOT NULL,
                "XeroConnectionId" TEXT NULL,
                "TenantId" TEXT NOT NULL,
                "ReportType" TEXT NOT NULL,
                "Basis" TEXT NOT NULL,
                "RequestUrl" TEXT NOT NULL,
                "PayloadJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "XeroTrialBalanceSnapshots" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_XeroTrialBalanceSnapshots" PRIMARY KEY,
                "TenantId" TEXT NOT NULL,
                "OrganizationId" TEXT NOT NULL,
                "ReportingPeriodId" TEXT NULL,
                "SnapshotDate" TEXT NOT NULL,
                "Basis" TEXT NOT NULL,
                "PayloadJson" TEXT NOT NULL,
                "AccountBalancesJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "XeroLedgerReconciliationRuns" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_XeroLedgerReconciliationRuns" PRIMARY KEY,
                "TenantId" TEXT NOT NULL,
                "OrganizationId" TEXT NOT NULL,
                "ReportingPeriodId" TEXT NULL,
                "SnapshotDate" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "DifferenceAmount" TEXT NOT NULL,
                "MissingAccountsJson" TEXT NOT NULL,
                "SummaryJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "XeroBackfillRuns" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_XeroBackfillRuns" PRIMARY KEY,
                "FromPeriodKey" TEXT NOT NULL,
                "ToPeriodKey" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "EstimatedCalls" INTEGER NOT NULL,
                "ActualCalls" INTEGER NOT NULL,
                "SummaryJson" TEXT NOT NULL,
                "RateLimitJson" TEXT NOT NULL,
                "Error" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "StartedAt" TEXT NULL,
                "CompletedAt" TEXT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "XeroBackfillTenantTasks" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_XeroBackfillTenantTasks" PRIMARY KEY,
                "XeroBackfillRunId" TEXT NOT NULL,
                "TenantId" TEXT NOT NULL,
                "TenantName" TEXT NOT NULL,
                "OrganizationId" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "EstimatedCalls" INTEGER NOT NULL,
                "ActualCalls" INTEGER NOT NULL,
                "JournalsImported" INTEGER NOT NULL,
                "JournalLinesImported" INTEGER NOT NULL,
                "StatementsImported" INTEGER NOT NULL,
                "CoverageJson" TEXT NOT NULL,
                "RateLimitJson" TEXT NOT NULL,
                "Error" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "FinancialStatementLines" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_FinancialStatementLines" PRIMARY KEY,
                "OrganizationId" TEXT NOT NULL,
                "ReportingPeriodId" TEXT NOT NULL,
                "ReportPackageId" TEXT NULL,
                "XeroRawReportSnapshotId" TEXT NULL,
                "TenantId" TEXT NOT NULL,
                "StatementType" TEXT NOT NULL,
                "Section" TEXT NOT NULL,
                "RowPath" TEXT NOT NULL,
                "LineName" TEXT NOT NULL,
                "AccountCode" TEXT NOT NULL,
                "CurrentAmount" TEXT NOT NULL,
                "PriorAmount" TEXT NOT NULL,
                "AmountsJson" TEXT NOT NULL,
                "SortOrder" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "StatementRuns" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_StatementRuns" PRIMARY KEY,
                "OrganizationId" TEXT NOT NULL,
                "ReportingPeriodId" TEXT NOT NULL,
                "ReportPackageId" TEXT NULL,
                "TenantId" TEXT NOT NULL,
                "Basis" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "SummaryJson" TEXT NOT NULL,
                "StartedAt" TEXT NOT NULL,
                "CompletedAt" TEXT NULL,
                "Error" TEXT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "StatementQaResults" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_StatementQaResults" PRIMARY KEY,
                "ReportPackageId" TEXT NOT NULL,
                "StatementRunId" TEXT NULL,
                "Status" TEXT NOT NULL,
                "SummaryJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "FluxReviewGroups" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_FluxReviewGroups" PRIMARY KEY,
                "ReportPackageId" TEXT NOT NULL,
                "OrganizationId" TEXT NOT NULL,
                "ReportingPeriodId" TEXT NOT NULL,
                "FluxType" TEXT NOT NULL DEFAULT 'YearOverYear',
                "StatementType" TEXT NOT NULL,
                "GroupKey" TEXT NOT NULL,
                "GroupName" TEXT NOT NULL,
                "CurrentPeriodKey" TEXT NOT NULL DEFAULT '',
                "PriorPeriodKey" TEXT NOT NULL DEFAULT '',
                "CurrentAmount" TEXT NOT NULL,
                "PriorAmount" TEXT NOT NULL,
                "RunningThreeMonthAmount" TEXT NOT NULL DEFAULT '0.0',
                "VarianceAmount" TEXT NOT NULL,
                "VariancePercent" TEXT NOT NULL,
                "DollarThreshold" TEXT NOT NULL,
                "PercentThreshold" TEXT NOT NULL,
                "ThresholdLogic" TEXT NOT NULL DEFAULT 'OR',
                "RequiresExplanation" INTEGER NOT NULL,
                "RequiresLedgerDetail" INTEGER NOT NULL DEFAULT 0,
                "LedgerDetailStatus" TEXT NOT NULL DEFAULT 'Not required',
                "LedgerDetailPulledAt" TEXT NULL,
                "Status" TEXT NOT NULL,
                "Assignee" TEXT NOT NULL DEFAULT '',
                "Reviewer" TEXT NOT NULL DEFAULT '',
                "DueDate" TEXT NULL,
                "ExplanationTemplate" TEXT NOT NULL DEFAULT '',
                "PriorExplanation" TEXT NOT NULL DEFAULT '',
                "Tags" TEXT NOT NULL DEFAULT '',
                "TrendJson" TEXT NOT NULL DEFAULT '[]',
                "DriverSummaryJson" TEXT NOT NULL DEFAULT '[]',
                "RiskFlagsJson" TEXT NOT NULL DEFAULT '[]',
                "AutoSignedOff" INTEGER NOT NULL DEFAULT 0,
                "Explanation" TEXT NOT NULL,
                "ExplanationBy" TEXT NOT NULL,
                "ExplainedAt" TEXT NULL,
                "PreparedBy" TEXT NOT NULL DEFAULT '',
                "PreparedAt" TEXT NULL,
                "ReviewedBy" TEXT NOT NULL DEFAULT '',
                "ReviewedAt" TEXT NULL,
                "EvidenceJson" TEXT NOT NULL,
                "SourceDataHash" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "AiPackageDraftSuggestions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AiPackageDraftSuggestions" PRIMARY KEY,
                "ReportPackageId" TEXT NOT NULL,
                "AiRunId" TEXT NULL,
                "Status" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "Title" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "PayloadJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "DecidedAt" TEXT NULL,
                "DecisionReason" TEXT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "FsLineDefinitions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_FsLineDefinitions" PRIMARY KEY,
                "OrganizationId" TEXT NOT NULL,
                "StatementType" TEXT NOT NULL,
                "Section" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "NormalBalance" TEXT NOT NULL,
                "AiGuidance" TEXT NOT NULL,
                "SortOrder" INTEGER NOT NULL,
                "IsActive" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "KpiAlerts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_KpiAlerts" PRIMARY KEY,
                "KpiDefinitionId" TEXT NOT NULL,
                "Direction" TEXT NOT NULL,
                "ThresholdValue" TEXT NOT NULL,
                "Severity" TEXT NOT NULL,
                "Message" TEXT NOT NULL,
                "IsActive" INTEGER NOT NULL,
                "LastTriggeredAt" TEXT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "NonFinancialMetrics" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_NonFinancialMetrics" PRIMARY KEY,
                "OrganizationId" TEXT NOT NULL,
                "ReportingPeriodId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Category" TEXT NOT NULL,
                "Unit" TEXT NOT NULL,
                "CurrentValue" TEXT NOT NULL,
                "PriorValue" TEXT NOT NULL,
                "TargetValue" TEXT NOT NULL,
                "ValuesJson" TEXT NOT NULL,
                "Source" TEXT NOT NULL,
                "IsPinned" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ForecastScenarios" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ForecastScenarios" PRIMARY KEY,
                "OrganizationId" TEXT NOT NULL,
                "ReportingPeriodId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "ScenarioType" TEXT NOT NULL,
                "HorizonMonths" INTEGER NOT NULL,
                "RevenueGrowthPercent" TEXT NOT NULL,
                "GrossMarginPercent" TEXT NOT NULL,
                "OpexGrowthPercent" TEXT NOT NULL,
                "CashConversionPercent" TEXT NOT NULL,
                "StartingCash" TEXT NOT NULL,
                "CashThreshold" TEXT NOT NULL,
                "AssumptionsJson" TEXT NOT NULL,
                "IsBase" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ForecastEvents" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ForecastEvents" PRIMARY KEY,
                "ForecastScenarioId" TEXT NOT NULL,
                "MonthOffset" INTEGER NOT NULL,
                "Name" TEXT NOT NULL,
                "Category" TEXT NOT NULL,
                "RevenueImpact" TEXT NOT NULL,
                "ExpenseImpact" TEXT NOT NULL,
                "CashImpact" TEXT NOT NULL,
                "IsRecurring" INTEGER NOT NULL,
                "Notes" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "ReportTemplates" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ReportTemplates" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Category" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "SectionsJson" TEXT NOT NULL,
                "IsBuiltIn" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "PackageComments" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_PackageComments" PRIMARY KEY,
                "ReportPackageId" TEXT NOT NULL,
                "PackageSlideId" TEXT NULL,
                "SlideBlockId" TEXT NULL,
                "Body" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "Author" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "ResolvedAt" TEXT NULL
            );
            """, cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "FxRates" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_FxRates" PRIMARY KEY,
                "OrganizationId" TEXT NOT NULL,
                "ReportingPeriodId" TEXT NOT NULL,
                "CurrencyCode" TEXT NOT NULL,
                "RateToPresentation" TEXT NOT NULL,
                "Source" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """, cancellationToken);

        await AddColumnIfMissingAsync(db, "ReportPackages", "IsSourceDataStale", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "ReportPackages", "SourceDataStaleReason", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, "ReportPackages", "SourceDataChangedAt", "TEXT NULL", cancellationToken);
        // P1.1 — Baseline diff engine. Cat 19, 20.
        await AddColumnIfMissingAsync(db, "ReportPackages", "PriorPackageId", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, "ReportPackages", "BoardDollarThreshold", "TEXT NOT NULL DEFAULT '25000.00'", cancellationToken);
        await AddColumnIfMissingAsync(db, "ReportPackages", "BoardPercentThreshold", "TEXT NOT NULL DEFAULT '15.00'", cancellationToken);
        // P2.20 — Materiality matrix. Cat 10.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "OrgFluxThresholdConfigs" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_OrgFluxThresholdConfigs" PRIMARY KEY,
                "OrganizationId" TEXT NOT NULL,
                "StatementType" TEXT NOT NULL,
                "AccountClass" TEXT NOT NULL,
                "DollarThreshold" TEXT NOT NULL DEFAULT '5000.00',
                "PercentThreshold" TEXT NOT NULL DEFAULT '10.00',
                "ThresholdLogic" TEXT NOT NULL DEFAULT 'AND',
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            """, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_OrgFluxThresholdConfigs_OrganizationId_StatementType_AccountClass" ON "OrgFluxThresholdConfigs" ("OrganizationId", "StatementType", "AccountClass");""", cancellationToken);

        // Vendor cache for ContactName resolution. Cat 14.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "XeroContacts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_XeroContacts" PRIMARY KEY,
                "TenantId" TEXT NOT NULL,
                "ContactId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "IsCustomer" INTEGER NOT NULL DEFAULT 0,
                "IsSupplier" INTEGER NOT NULL DEFAULT 0,
                "Status" TEXT NOT NULL DEFAULT '',
                "SyncedAt" TEXT NOT NULL
            );
            """, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_XeroContacts_TenantId_ContactId" ON "XeroContacts" ("TenantId", "ContactId");""", cancellationToken);

        // P0.6 — Authoritative Chart of Accounts cache. Cat 3.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "XeroChartOfAccounts" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_XeroChartOfAccounts" PRIMARY KEY,
                "TenantId" TEXT NOT NULL,
                "Code" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Type" TEXT NOT NULL,
                "Class" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "ReportingCode" TEXT NOT NULL DEFAULT '',
                "ParentCode" TEXT NOT NULL DEFAULT '',
                "IsArchived" INTEGER NOT NULL DEFAULT 0,
                "SyncedAt" TEXT NOT NULL
            );
            """, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_XeroChartOfAccounts_TenantId_Code" ON "XeroChartOfAccounts" ("TenantId", "Code");""", cancellationToken);

        // P0.5 — journal-line metadata. Cat 3, 4, 14, 17.
        await AddColumnIfMissingAsync(db, "XeroJournals", "SourceId", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync(db, "XeroJournals", "ContactId", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync(db, "XeroJournals", "ContactName", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync(db, "XeroJournals", "IsVoided", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "XeroJournals", "CurrencyCode", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync(db, "XeroJournals", "CurrencyRate", "TEXT NOT NULL DEFAULT '0.00'", cancellationToken);

        // P1.16 — CFO approval gate. Cat 26.
        await AddColumnIfMissingAsync(db, "ReportPackages", "IsApproved", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "ReportPackages", "ApprovedBy", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync(db, "ReportPackages", "ApprovedAt", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, "ReportPackages", "ApprovedVersionId", "TEXT NULL", cancellationToken);

        // P1.17 — AI provenance on SlideBlock. Cat 25.
        await AddColumnIfMissingAsync(db, "SlideBlocks", "IsAiAuthored", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "SlideBlocks", "OriginatingAiRunId", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, "SlideBlocks", "AiAuthoredAt", "TEXT NULL", cancellationToken);

        // P2.26 — AuditRecord linkage to AiRun + correlation, AiRun token counts + InputHash.
        await AddColumnIfMissingAsync(db, "AuditRecords", "AiRunId", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, "AuditRecords", "CorrelationId", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, "AiRuns", "PromptTokens", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "AiRuns", "CompletionTokens", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "AiRuns", "InputHash", "TEXT NOT NULL DEFAULT ''", cancellationToken);

        // P3.31 — composite indexes for hot paths. Cat 32.
        await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_GlTransactions_GlAccountId_TransactionDate" ON "GlTransactions" ("GlAccountId", "TransactionDate");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_XeroJournals_TenantId_JournalDate" ON "XeroJournals" ("TenantId", "JournalDate");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_AiRuns_Status" ON "AiRuns" ("Status");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_AuditRecords_AiRunId" ON "AuditRecords" ("AiRunId");""", cancellationToken);
        await AddColumnIfMissingAsync(db, "XeroConnections", "CreatedAt", "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00+00:00'", cancellationToken);
        await AddColumnIfMissingAsync(db, "XeroConnections", "UpdatedAt", "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00+00:00'", cancellationToken);
        await AddColumnIfMissingAsync(db, "XeroSyncRuns", "Error", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, "ExportArtifacts", "ContentType", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync(db, "ExportArtifacts", "StoragePath", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync(db, "ExportArtifacts", "CompletedAt", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, "ShareLinks", "PasswordHash", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, "ShareLinks", "DashboardOnly", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "ShareLinks", "UpdatedAt", "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00+00:00'", cancellationToken);
        await AddColumnIfMissingAsync(db, "DistributionSchedules", "LastTestSentAt", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, "DistributionSchedules", "Status", "TEXT NOT NULL DEFAULT 'Active'", cancellationToken);
        await AddColumnIfMissingAsync(db, "DistributionSchedules", "UpdatedAt", "TEXT NOT NULL DEFAULT '0001-01-01T00:00:00+00:00'", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "FluxType", "TEXT NOT NULL DEFAULT 'YearOverYear'", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "CurrentPeriodKey", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "PriorPeriodKey", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "RunningThreeMonthAmount", "TEXT NOT NULL DEFAULT '0.0'", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "ThresholdLogic", "TEXT NOT NULL DEFAULT 'OR'", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "RequiresLedgerDetail", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "LedgerDetailStatus", "TEXT NOT NULL DEFAULT 'Not required'", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "LedgerDetailPulledAt", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "Assignee", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "Reviewer", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "DueDate", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "ExplanationTemplate", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "PriorExplanation", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "Tags", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "TrendJson", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "DriverSummaryJson", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "RiskFlagsJson", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "AutoSignedOff", "INTEGER NOT NULL DEFAULT 0", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "PreparedBy", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "PreparedAt", "TEXT NULL", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "ReviewedBy", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await AddColumnIfMissingAsync(db, "FluxReviewGroups", "ReviewedAt", "TEXT NULL", cancellationToken);

        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_XeroTenantConnections_TenantId" ON "XeroTenantConnections" ("TenantId");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_XeroTenantEntityMappings_TenantId" ON "XeroTenantEntityMappings" ("TenantId");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_XeroLedgerSyncCursors_TenantId" ON "XeroLedgerSyncCursors" ("TenantId");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_XeroJournals_TenantId_XeroJournalId" ON "XeroJournals" ("TenantId", "XeroJournalId");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_XeroJournalLines_TenantId_AccountCode" ON "XeroJournalLines" ("TenantId", "AccountCode");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""DROP INDEX IF EXISTS "IX_FluxReviewGroups_ReportPackageId_StatementType_GroupKey";""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_FluxReviewGroups_ReportPackageId_FluxType_StatementType_GroupKey" ON "FluxReviewGroups" ("ReportPackageId", "FluxType", "StatementType", "GroupKey");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_XeroBackfillRuns_Status" ON "XeroBackfillRuns" ("Status");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_XeroBackfillTenantTasks_XeroBackfillRunId_TenantId" ON "XeroBackfillTenantTasks" ("XeroBackfillRunId", "TenantId");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_FsLineDefinitions_OrganizationId_StatementType_Name" ON "FsLineDefinitions" ("OrganizationId", "StatementType", "Name");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_KpiAlerts_KpiDefinitionId_IsActive" ON "KpiAlerts" ("KpiDefinitionId", "IsActive");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_NonFinancialMetrics_OrganizationId_ReportingPeriodId_Name" ON "NonFinancialMetrics" ("OrganizationId", "ReportingPeriodId", "Name");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_ForecastScenarios_OrganizationId_ReportingPeriodId_Name" ON "ForecastScenarios" ("OrganizationId", "ReportingPeriodId", "Name");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_ForecastEvents_ForecastScenarioId_MonthOffset" ON "ForecastEvents" ("ForecastScenarioId", "MonthOffset");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReportTemplates_Name" ON "ReportTemplates" ("Name");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE INDEX IF NOT EXISTS "IX_PackageComments_ReportPackageId_PackageSlideId_Status" ON "PackageComments" ("ReportPackageId", "PackageSlideId", "Status");""", cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_FxRates_OrganizationId_ReportingPeriodId_CurrencyCode" ON "FxRates" ("OrganizationId", "ReportingPeriodId", "CurrencyCode");""", cancellationToken);
    }

    private static async Task AddColumnIfMissingAsync(AppDbContext db, string table, string column, string definition, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        var shouldClose = connection.State == System.Data.ConnectionState.Closed;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
            if (!exists)
            {
#pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync($"""ALTER TABLE "{table}" ADD COLUMN "{column}" {definition};""", cancellationToken);
#pragma warning restore EF1002
            }
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }
}
