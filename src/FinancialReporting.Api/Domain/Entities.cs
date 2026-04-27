namespace FinancialReporting.Api.Domain;

public enum PackageStatus
{
    Draft,
    Review,
    Syncing,
    Blocked,
    Final
}

public enum IssueStatus
{
    Open,
    Resolved,
    Ignored,
    Rejected
}

public enum IssueSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public enum AiRunStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

public enum MappingReviewStatus
{
    New,
    Suggested,
    Reviewed,
    Rejected
}

public enum ConsolidationTreatment
{
    Include,
    Eliminate,
    Intercompany,
    Exclude
}

public sealed class Organization
{
    public Guid Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Abbreviation { get; set; } = "";
    public bool IsConsolidated { get; set; }
    public string PrimaryColor { get; set; } = "#0F2A4A";
    public string AccentColor { get; set; } = "#6B4FA8";
    public string CoverStyle { get; set; } = "modern";
    public string Tagline { get; set; } = "Confidential financial reporting";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ReportPackage> Packages { get; set; } = [];
}

public sealed class ReportingPeriod
{
    public Guid Id { get; set; }
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public bool IsClosed { get; set; }
}

public sealed class ReportPackage
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public Guid ReportingPeriodId { get; set; }
    public ReportingPeriod? ReportingPeriod { get; set; }
    public PackageStatus Status { get; set; } = PackageStatus.Draft;
    public string VersionLabel { get; set; } = "v1";
    public string BaseFrom { get; set; } = "";
    // FK to the prior month's package this one was built from. Drives the AI baseline diff
    // engine: slides from the prior package are evaluated for keep/modify/add/remove against
    // the current month's flux. Cat 19. Nullable because a brand-new org has no prior.
    public Guid? PriorPackageId { get; set; }
    // Tiered materiality: ops-level thresholds live on FluxReviewGroup; these are the
    // CFO/Board cutoffs that decide whether a slide reaches the board package versus an
    // appendix. Cat 20. Defaults are conservative starting points; tunable per package.
    public decimal BoardDollarThreshold { get; set; } = 25000m;
    public decimal BoardPercentThreshold { get; set; } = 15m;
    // P1.16 — CFO approval gate. Once approved the package is immutable (UpdatedAt and
    // mutating endpoints reject); DistributionSchedule send is blocked unless IsApproved.
    // Cat 26.
    public bool IsApproved { get; set; }
    public string ApprovedBy { get; set; } = "";
    public DateTimeOffset? ApprovedAt { get; set; }
    public Guid? ApprovedVersionId { get; set; }   // PackageVersion snapshot tagged at approval
    public DateTimeOffset? LastXeroSyncAt { get; set; }
    public bool IsSourceDataStale { get; set; }
    public string? SourceDataStaleReason { get; set; }
    public DateTimeOffset? SourceDataChangedAt { get; set; }
    public string? BlockReason { get; set; }
    public string ThemeJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<PackageSlide> Slides { get; set; } = [];
    public List<PackageIssue> Issues { get; set; } = [];
    public List<PackageVersion> Versions { get; set; } = [];
}

public sealed class PackageSlide
{
    public Guid Id { get; set; }
    public Guid ReportPackageId { get; set; }
    public ReportPackage? ReportPackage { get; set; }
    public int SortOrder { get; set; }
    public string Subject { get; set; } = "";
    public string KpiLabel { get; set; } = "";
    public decimal CurrentValue { get; set; }
    public decimal PriorValue { get; set; }
    public decimal VarianceAmount { get; set; }
    public decimal VariancePercent { get; set; }
    public string AccountCodesCsv { get; set; } = "";
    public string MonthlyJson { get; set; } = "[]";
    public string PriorMonthlyJson { get; set; } = "[]";
    public string ChartConfigJson { get; set; } = "{}";
    public List<SlideBlock> Blocks { get; set; } = [];
}

public sealed class SlideBlock
{
    public Guid Id { get; set; }
    public Guid PackageSlideId { get; set; }
    public PackageSlide? PackageSlide { get; set; }
    public int SortOrder { get; set; }
    public string Kind { get; set; } = "text";
    public string ContentJson { get; set; } = "{}";
    // P1.17 — AI provenance on accepted suggestions. Without these fields a CFO cannot
    // distinguish AI-authored prose from human-authored prose after the fact, breaking
    // both auditability and the "review what AI wrote" workflow. Cat 25.
    public bool IsAiAuthored { get; set; }
    public Guid? OriginatingAiRunId { get; set; }
    public DateTimeOffset? AiAuthoredAt { get; set; }
}

public sealed class KpiDefinition
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Formula { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal CurrentValue { get; set; }
    public decimal TargetValue { get; set; }
    public bool IsPinned { get; set; }
    public string Status { get; set; } = "good";
    public List<KpiAlert> Alerts { get; set; } = [];
}

public sealed class KpiAlert
{
    public Guid Id { get; set; }
    public Guid KpiDefinitionId { get; set; }
    public KpiDefinition? KpiDefinition { get; set; }
    public string Direction { get; set; } = "Below";
    public decimal ThresholdValue { get; set; }
    public string Severity { get; set; } = "Medium";
    public string Message { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastTriggeredAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class NonFinancialMetric
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ReportingPeriodId { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Unit { get; set; } = "";
    public decimal CurrentValue { get; set; }
    public decimal PriorValue { get; set; }
    public decimal TargetValue { get; set; }
    public string ValuesJson { get; set; } = "[]";
    public string Source { get; set; } = "Manual";
    public bool IsPinned { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ForecastScenario
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ReportingPeriodId { get; set; }
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string ScenarioType { get; set; } = "Base";
    public int HorizonMonths { get; set; } = 36;
    public decimal RevenueGrowthPercent { get; set; }
    public decimal GrossMarginPercent { get; set; } = 55m;
    public decimal OpexGrowthPercent { get; set; }
    public decimal CashConversionPercent { get; set; } = 85m;
    public decimal StartingCash { get; set; }
    public decimal CashThreshold { get; set; }
    public string AssumptionsJson { get; set; } = "[]";
    public bool IsBase { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<ForecastEvent> Events { get; set; } = [];
}

public sealed class ForecastEvent
{
    public Guid Id { get; set; }
    public Guid ForecastScenarioId { get; set; }
    public ForecastScenario? ForecastScenario { get; set; }
    public int MonthOffset { get; set; } = 1;
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Microforecast";
    public decimal RevenueImpact { get; set; }
    public decimal ExpenseImpact { get; set; }
    public decimal CashImpact { get; set; }
    public bool IsRecurring { get; set; }
    public string Notes { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ReportTemplate
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public string SectionsJson { get; set; } = "[]";
    public bool IsBuiltIn { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PackageComment
{
    public Guid Id { get; set; }
    public Guid ReportPackageId { get; set; }
    public ReportPackage? ReportPackage { get; set; }
    public Guid? PackageSlideId { get; set; }
    public PackageSlide? PackageSlide { get; set; }
    public Guid? SlideBlockId { get; set; }
    public SlideBlock? SlideBlock { get; set; }
    public string Body { get; set; } = "";
    public string Status { get; set; } = "Open";
    public string Author { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class FxRate
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ReportingPeriodId { get; set; }
    public string CurrencyCode { get; set; } = "";
    public decimal RateToPresentation { get; set; } = 1m;
    public string Source { get; set; } = "Manual";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PackageIssue
{
    public Guid Id { get; set; }
    public Guid ReportPackageId { get; set; }
    public ReportPackage? ReportPackage { get; set; }
    public Guid? PackageSlideId { get; set; }
    public IssueSeverity Severity { get; set; }
    public IssueStatus Status { get; set; } = IssueStatus.Open;
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string EvidenceJson { get; set; } = "{}";
    public string RecommendedFixJson { get; set; } = "{}";
    public decimal Confidence { get; set; }
    public string? UserComment { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAt { get; set; }
}

public sealed class PackageVersion
{
    public Guid Id { get; set; }
    public Guid ReportPackageId { get; set; }
    public string VersionLabel { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public string ChangeSummary { get; set; } = "";
    public string SnapshotJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class XeroConnection
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string TenantId { get; set; } = "";
    public string TenantName { get; set; } = "";
    public string TenantType { get; set; } = "";
    public string EncryptedAccessToken { get; set; } = "";
    public string EncryptedRefreshToken { get; set; } = "";
    public DateTimeOffset TokenExpiresAt { get; set; }
    public string Scopes { get; set; } = "";
    public string ConnectionStatus { get; set; } = "NeedsReconnect";
    public DateTimeOffset? LastConnectedAt { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class XeroTenantConnection
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string TenantName { get; set; } = "";
    public string TenantType { get; set; } = "ORGANISATION";
    public string EncryptedAccessToken { get; set; } = "";
    public string EncryptedRefreshToken { get; set; } = "";
    public DateTimeOffset TokenExpiresAt { get; set; }
    public string Scopes { get; set; } = "";
    public string ConnectionStatus { get; set; } = "NeedsReconnect";
    public bool RequiresReconnectForLedger { get; set; }
    public DateTimeOffset? LastConnectedAt { get; set; }
    public string? LastError { get; set; }
    public string Source { get; set; } = "Xero";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class XeroTenantEntityMapping
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public Guid OrganizationId { get; set; }
    public Organization? Organization { get; set; }
    public bool IsIgnored { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class XeroLedgerSyncSetting
{
    public Guid Id { get; set; }
    public bool Enabled { get; set; } = true;
    public int SyncEveryMinutes { get; set; } = 15;
    public int DailyTrialBalanceHourUtc { get; set; } = 11;
    public int RetentionYears { get; set; } = 3;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class XeroLedgerSyncCursor
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public int? LastJournalNumber { get; set; }
    public DateTimeOffset? LastSyncedAt { get; set; }
    public DateTimeOffset? LastSuccessfulSyncAt { get; set; }
    public string Status { get; set; } = "Pending";
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class XeroSyncRun
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ReportingPeriodId { get; set; }
    public string Status { get; set; } = "Queued";
    public string SummaryJson { get; set; } = "{}";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class XeroJournal
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string XeroJournalId { get; set; } = "";
    public int JournalNumber { get; set; }
    public DateOnly JournalDate { get; set; }
    public DateTimeOffset? CreatedDateUtc { get; set; }
    public string SourceType { get; set; } = "";
    public string Reference { get; set; } = "";
    // Metadata captured from the Xero /Journals payload to enable vendor-pattern detection,
    // void-aware reconciliation, and machine-parseable AI citations. Cat 3, 4, 14, 17.
    public string SourceId { get; set; } = "";       // Xero SourceID (originating doc GUID)
    public string ContactId { get; set; } = "";     // Counterparty / payee GUID when present
    public string ContactName { get; set; } = "";   // Counterparty / payee display name
    public bool IsVoided { get; set; }              // True when payload Status flags void/deleted
    public string CurrencyCode { get; set; } = "";  // Org base currency or per-source currency
    public decimal CurrencyRate { get; set; }       // FX rate to base; 0 = unknown / base currency
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<XeroJournalLine> Lines { get; set; } = [];
}

public sealed class XeroJournalLine
{
    public Guid Id { get; set; }
    public Guid XeroJournalId { get; set; }
    public XeroJournal? XeroJournal { get; set; }
    public string TenantId { get; set; } = "";
    public string SourceLineId { get; set; } = "";
    public string AccountCode { get; set; } = "";
    public string AccountName { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal NetAmount { get; set; }
    public decimal GrossAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public string TrackingJson { get; set; } = "[]";
}

public sealed class XeroLedgerMonthlySummary
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public Guid OrganizationId { get; set; }
    public string MonthKey { get; set; } = "";
    public string AccountCode { get; set; } = "";
    public string AccountName { get; set; } = "";
    public decimal NetAmount { get; set; }
    public DateTimeOffset LastRolledUpAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class XeroRawReportSnapshot
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ReportingPeriodId { get; set; }
    public Guid? XeroConnectionId { get; set; }
    public string TenantId { get; set; } = "";
    public string ReportType { get; set; } = "";
    public string Basis { get; set; } = "accrual";
    public string RequestUrl { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class XeroTrialBalanceSnapshot
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public Guid OrganizationId { get; set; }
    public Guid? ReportingPeriodId { get; set; }
    public DateOnly SnapshotDate { get; set; }
    public string Basis { get; set; } = "accrual";
    public string PayloadJson { get; set; } = "{}";
    public string AccountBalancesJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class XeroLedgerReconciliationRun
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public Guid OrganizationId { get; set; }
    public Guid? ReportingPeriodId { get; set; }
    public DateOnly SnapshotDate { get; set; }
    public string Status { get; set; } = "Pending";
    public decimal DifferenceAmount { get; set; }
    public string MissingAccountsJson { get; set; } = "[]";
    public string SummaryJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class XeroBackfillRun
{
    public Guid Id { get; set; }
    public string FromPeriodKey { get; set; } = "";
    public string ToPeriodKey { get; set; } = "";
    public string Status { get; set; } = "Queued";
    public int EstimatedCalls { get; set; }
    public int ActualCalls { get; set; }
    public string SummaryJson { get; set; } = "{}";
    public string RateLimitJson { get; set; } = "{}";
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class XeroBackfillTenantTask
{
    public Guid Id { get; set; }
    public Guid XeroBackfillRunId { get; set; }
    public string TenantId { get; set; } = "";
    public string TenantName { get; set; } = "";
    public Guid OrganizationId { get; set; }
    public string Status { get; set; } = "Queued";
    public int EstimatedCalls { get; set; }
    public int ActualCalls { get; set; }
    public int JournalsImported { get; set; }
    public int JournalLinesImported { get; set; }
    public int StatementsImported { get; set; }
    public string CoverageJson { get; set; } = "{}";
    public string RateLimitJson { get; set; } = "{}";
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FinancialStatementLine
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ReportingPeriodId { get; set; }
    public Guid? ReportPackageId { get; set; }
    public Guid? XeroRawReportSnapshotId { get; set; }
    public string TenantId { get; set; } = "";
    public string StatementType { get; set; } = "";
    public string Section { get; set; } = "";
    public string RowPath { get; set; } = "";
    public string LineName { get; set; } = "";
    public string AccountCode { get; set; } = "";
    public decimal CurrentAmount { get; set; }
    public decimal PriorAmount { get; set; }
    public string AmountsJson { get; set; } = "[]";
    public int SortOrder { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StatementRun
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ReportingPeriodId { get; set; }
    public Guid? ReportPackageId { get; set; }
    public string TenantId { get; set; } = "";
    public string Basis { get; set; } = "accrual";
    public string Status { get; set; } = "Queued";
    public string SummaryJson { get; set; } = "{}";
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class StatementQaResult
{
    public Guid Id { get; set; }
    public Guid ReportPackageId { get; set; }
    public Guid? StatementRunId { get; set; }
    public string Status { get; set; } = "Pending";
    public string SummaryJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class XeroOAuthSession
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string State { get; set; } = "";
    public string ProtectedCodeVerifier { get; set; } = "";
    public string FlowType { get; set; } = "single-org";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CodeConsumedAt { get; set; }
}

/// <summary>
/// P2.20 — Materiality matrix. Per (OrganizationId, StatementType, AccountClass) tunable
/// thresholds; new FluxReviewGroup rows are seeded from the matching row instead of the
/// hardcoded 0 / 10% defaults that effectively disabled the dollar leg of the dual-threshold
/// gate. Cat 10.
/// </summary>
public sealed class OrgFluxThresholdConfig
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string StatementType { get; set; } = "";    // ProfitAndLoss / BalanceSheet / "*"
    public string AccountClass { get; set; } = "";     // Revenue / Operating Expense / Asset / "*"
    public decimal DollarThreshold { get; set; } = 5000m;
    public decimal PercentThreshold { get; set; } = 10m;
    public string ThresholdLogic { get; set; } = "AND"; // AND = both must trip; OR = either
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Cache of Xero contacts (vendors / customers). Lets the flux pipeline resolve
/// ContactName for journals where the ContactID was captured but the inline Contact node
/// wasn't present (ACCREC/ACCPAY journals derived from Invoices/Bills). Cat 14.
/// </summary>
public sealed class XeroContact
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string ContactId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool IsCustomer { get; set; }
    public bool IsSupplier { get; set; }
    public string Status { get; set; } = "";       // ACTIVE / ARCHIVED
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Authoritative chart-of-accounts cache fetched from Xero /Accounts. Replaces the prior
/// GuessTypeFromAmount sign heuristic in ProjectGlForPeriodAsync. Cat 3.
/// </summary>
public sealed class XeroChartOfAccount
{
    public Guid Id { get; set; }
    public string TenantId { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";          // Xero AccountType (e.g. REVENUE, EXPENSE)
    public string Class { get; set; } = "";         // Xero AccountClass (e.g. ASSET, LIABILITY)
    public string Status { get; set; } = "";        // ACTIVE / ARCHIVED
    public string ReportingCode { get; set; } = ""; // Reporting hierarchy code
    public string ParentCode { get; set; } = "";    // For sub-accounts
    public bool IsArchived { get; set; }
    public DateTimeOffset SyncedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class GlAccount
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ReportingPeriodId { get; set; }
    public string TenantId { get; set; } = "";
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string Class { get; set; } = "";
    public string FsLine { get; set; } = "";
    public string AiSuggestedFsLine { get; set; } = "";
    public decimal MappingConfidence { get; set; }
    public bool IsFirstSeen { get; set; }
    public MappingReviewStatus ReviewStatus { get; set; } = MappingReviewStatus.New;
    public ConsolidationTreatment ConsolidationTreatment { get; set; } = ConsolidationTreatment.Include;
    public string MonthlyBalancesJson { get; set; } = "[]";
    public string PriorPeriodHistoryJson { get; set; } = "[]";
    public string AuditReason { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<GlTransaction> Transactions { get; set; } = [];
}

public sealed class GlTransaction
{
    public Guid Id { get; set; }
    public Guid GlAccountId { get; set; }
    public GlAccount? GlAccount { get; set; }
    public DateOnly TransactionDate { get; set; }
    public string Description { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string Source { get; set; } = "Xero";
}

public sealed class FluxReviewGroup
{
    public Guid Id { get; set; }
    public Guid ReportPackageId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ReportingPeriodId { get; set; }
    public string FluxType { get; set; } = "YearOverYear";
    public string StatementType { get; set; } = "";
    public string GroupKey { get; set; } = "";
    public string GroupName { get; set; } = "";
    public string CurrentPeriodKey { get; set; } = "";
    public string PriorPeriodKey { get; set; } = "";
    public decimal CurrentAmount { get; set; }
    public decimal PriorAmount { get; set; }
    public decimal RunningThreeMonthAmount { get; set; }
    public decimal VarianceAmount { get; set; }
    public decimal VariancePercent { get; set; }
    public decimal DollarThreshold { get; set; }
    public decimal PercentThreshold { get; set; } = 10m;
    public string ThresholdLogic { get; set; } = "OR";
    public bool RequiresExplanation { get; set; }
    public bool RequiresLedgerDetail { get; set; }
    public string LedgerDetailStatus { get; set; } = "Not required";
    public DateTimeOffset? LedgerDetailPulledAt { get; set; }
    public string Status { get; set; } = "Open";
    public string Assignee { get; set; } = "";
    public string Reviewer { get; set; } = "";
    public DateOnly? DueDate { get; set; }
    public string ExplanationTemplate { get; set; } = "";
    public string PriorExplanation { get; set; } = "";
    public string Tags { get; set; } = "";
    public string TrendJson { get; set; } = "[]";
    public string DriverSummaryJson { get; set; } = "[]";
    public string RiskFlagsJson { get; set; } = "[]";
    public bool AutoSignedOff { get; set; }
    public string Explanation { get; set; } = "";
    public string ExplanationBy { get; set; } = "";
    public DateTimeOffset? ExplainedAt { get; set; }
    public string PreparedBy { get; set; } = "";
    public DateTimeOffset? PreparedAt { get; set; }
    public string ReviewedBy { get; set; } = "";
    public DateTimeOffset? ReviewedAt { get; set; }
    public string EvidenceJson { get; set; } = "{}";
    public string SourceDataHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AccountMapping
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ReportingPeriodId { get; set; }
    public string FsLine { get; set; } = "";
    public string AccountCodesCsv { get; set; } = "";
    public string EntityKeysCsv { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FsLineDefinition
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public string StatementType { get; set; } = "IncomeStatement";
    public string Section { get; set; } = "";
    public string Name { get; set; } = "";
    public string NormalBalance { get; set; } = "Credit";
    public string AiGuidance { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class EliminationEntry
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ReportingPeriodId { get; set; }
    public Guid GlAccountId { get; set; }
    public string Type { get; set; } = "EliminateAccount";
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public string Status { get; set; } = "Balanced";
    public string Reason { get; set; } = "";
    public bool IsRecurringRule { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class RecurringEliminationRule
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid ReportingPeriodId { get; set; }
    public Guid? GlAccountId { get; set; }
    public string Type { get; set; } = "EliminateAccount";
    public string Description { get; set; } = "";
    public string CriteriaJson { get; set; } = "{}";
    public string Reason { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AiRuntimeSetting
{
    public Guid Id { get; set; }
    public string Module { get; set; } = "";
    public string Model { get; set; } = "";
    public string ReasoningEffort { get; set; } = "high";
    public string Profile { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AiRun
{
    public Guid Id { get; set; }
    public Guid? ReportPackageId { get; set; }
    public string Module { get; set; } = "";
    public string PromptProfile { get; set; } = "";
    public string Model { get; set; } = "";
    public string ReasoningEffort { get; set; } = "";
    public AiRunStatus Status { get; set; } = AiRunStatus.Queued;
    public int Progress { get; set; }
    public string InputJson { get; set; } = "{}";
    public string OutputJson { get; set; } = "{}";
    public string Logs { get; set; } = "";
    public bool CancellationRequested { get; set; }
    // P2.26 — token accounting + cost reproduction. Cat 18.
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    // Hash of InputJson at run time so a re-execution can prove identical inputs.
    public string InputHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class AiPackageDraftSuggestion
{
    public Guid Id { get; set; }
    public Guid ReportPackageId { get; set; }
    public Guid? AiRunId { get; set; }
    public string Status { get; set; } = "Staged";
    public string Kind { get; set; } = "Context";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionReason { get; set; }
}

public sealed class ExportArtifact
{
    public Guid Id { get; set; }
    public Guid ReportPackageId { get; set; }
    public string Type { get; set; } = "PDF";
    public string Status { get; set; } = "Queued";
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public string StoragePath { get; set; } = "";
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class ShareLink
{
    public Guid Id { get; set; }
    public Guid ReportPackageId { get; set; }
    public string Token { get; set; } = "";
    public bool RequirePassword { get; set; }
    public string? PasswordHash { get; set; }
    public bool AllowDownload { get; set; }
    public bool DashboardOnly { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DistributionSchedule
{
    public Guid Id { get; set; }
    public Guid ReportPackageId { get; set; }
    public string RecipientsCsv { get; set; } = "";
    public string Cadence { get; set; } = "Monthly";
    public bool IncludePdf { get; set; } = true;
    public bool IncludeExcel { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastTestSentAt { get; set; }
    public string Status { get; set; } = "Active";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuditRecord
{
    public Guid Id { get; set; }
    public string Actor { get; set; } = "";
    public string Role { get; set; } = "";
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public Guid? EntityId { get; set; }
    public Guid? ReportPackageId { get; set; }
    public string Reason { get; set; } = "";
    public string BeforeJson { get; set; } = "{}";
    public string AfterJson { get; set; } = "{}";
    // P2.26 — link audit rows to the AI run that produced the change so a CFO/auditor can
    // traverse audit → AI run → input prompt → output → journal-line citations. Cat 18.
    public Guid? AiRunId { get; set; }
    public string? CorrelationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
