using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FinancialReporting.Api;
using FinancialReporting.Api.Common;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using FinancialReporting.Api.Features.Ai;
using FinancialReporting.Api.Features.Auth;
using FinancialReporting.Api.Features.Distribution;
using FinancialReporting.Api.Features.Exports;
using FinancialReporting.Api.Features.Flux;
using FinancialReporting.Api.Features.Health;
using FinancialReporting.Api.Features.Kpis;
using FinancialReporting.Api.Features.Mapping;
using FinancialReporting.Api.Features.Packages;
using FinancialReporting.Api.Features.Planning;
using FinancialReporting.Api.Features.Reporting;
using FinancialReporting.Api.Features.ReportingPeriods;
using FinancialReporting.Api.Features.Xero;
using FinancialReporting.Api.Hubs;
using FinancialReporting.Api.Services;
using static FinancialReporting.Api.Common.EndpointHelpers;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;

// P1.13 — QuestPDF community license. Required before any PDF render call.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

// P3.34 — Serilog: structured JSON logs to stdout for shipping to a log aggregator.
// Cat 46. Use the compact JSON formatter so each line is parseable; enrich with the
// machine name and environment so multi-instance deployments can be split.
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithEnvironmentName()
    .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter())
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// P3.34 — OpenTelemetry traces + metrics. OTLP exporter destination is read from the
// standard OTEL_EXPORTER_OTLP_ENDPOINT env var so deployments can plug in Honeycomb,
// Tempo, Application Insights, etc. without code changes. Cat 46.
const string serviceName = "FinancialReporting.Api";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(serviceName))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());

builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddHttpClient();

// P3.33 — caching scaffolding. AddMemoryCache for in-process key-based caches; AddOutputCache
// to apply the [OutputCache] / WithOutputCache pattern to read-heavy reporting endpoints.
// Cat 38. Default policy is 30s with VaryByQuery so package id / period parameters key.
builder.Services.AddMemoryCache();
builder.Services.AddOutputCache(options =>
{
    options.AddBasePolicy(builder => builder.Expire(TimeSpan.FromSeconds(30)));
    options.AddPolicy("Reporting", builder => builder.Expire(TimeSpan.FromSeconds(60)).SetVaryByQuery("*"));
});

// P3.30 + Auth — Organization context. Backed by the HttpContext when an authenticated
// principal carries an `org` claim; otherwise the EF query filters are a no-op so the
// existing Development bypass still works. Cat 45.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IOrganizationContext, HttpContextOrganizationContext>();

// JWT bearer authentication. Signing key is read from config; in Development we synthesize
// a stable per-machine key so dev tokens survive restarts but are NOT a valid prod secret.
var signingKeyMaterial = builder.Configuration["Auth:SigningKey"]
                         ?? Environment.GetEnvironmentVariable("FINANCEAPP_AUTH_SIGNING_KEY")
                         ?? (builder.Environment.IsDevelopment() ? "dev-only-not-a-prod-secret-financereporting-please-rotate" : "");
if (string.IsNullOrWhiteSpace(signingKeyMaterial))
{
    throw new InvalidOperationException("Auth:SigningKey must be configured in non-Development environments.");
}
var signingKey = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(signingKeyMaterial));
var jwtIssuer = builder.Configuration["Auth:Issuer"] ?? "financereporting-local";
var jwtAudience = builder.Configuration["Auth:Audience"] ?? "financereporting-api";

builder.Services.AddSingleton(new JwtIssuerOptions(signingKey, jwtIssuer, jwtAudience));

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !builder.Environment.IsDevelopment();
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtIssuer,
            ValidateAudience = true,
            ValidAudience = jwtAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        // OIDC bridge: when an external IdP is configured (Auth0, Entra ID, Okta, etc.),
        // also accept its tokens by trusting its metadata document. Set `Auth:OidcAuthority`
        // (e.g. `https://yourtenant.auth0.com/`) and `Auth:OidcAudience` to plug it in.
        // The local symmetric key path remains for service-to-service tokens.
        var oidcAuthority = builder.Configuration["Auth:OidcAuthority"];
        if (!string.IsNullOrWhiteSpace(oidcAuthority))
        {
            options.Authority = oidcAuthority;
            options.Audience = builder.Configuration["Auth:OidcAudience"] ?? jwtAudience;
            options.TokenValidationParameters.ValidateIssuer = false;
            options.TokenValidationParameters.IssuerSigningKey = null!;
            options.TokenValidationParameters.ValidateIssuerSigningKey = false;
        }
    });
builder.Services.AddAuthorization();
// P3.32 — RFC 7807 ProblemDetails so unhandled exceptions surface a structured envelope
// with a traceId clients can correlate, instead of a raw 500 + stack trace. Cat 35, 36.
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
        context.ProblemDetails.Extensions["timestamp"] = DateTimeOffset.UtcNow.ToString("O");
    };
});
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
var dataProtectionAppName = builder.Configuration["DataProtection:ApplicationName"]
                            ?? Environment.GetEnvironmentVariable("FINANCEAPP_DATA_PROTECTION_APP_NAME")
                            ?? "FinanceApp.Api";
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"]
                             ?? Environment.GetEnvironmentVariable("FINANCEAPP_DATA_PROTECTION_KEYS_PATH")
                             ?? Path.Combine(
                                 Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                 "FinanceApp",
                                 "DataProtection-Keys");
Directory.CreateDirectory(dataProtectionKeysPath);
builder.Services.AddDataProtection()
    .SetApplicationName(dataProtectionAppName)
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy
        .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials());
});

builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("SqliteConnection")
                           ?? "Data Source=financial-reporting-dev.db";
    options.UseSqlite(connectionString);
});

builder.Services.AddScoped<MappingService>();
builder.Services.AddScoped<FinancialEngine>();
builder.Services.AddScoped<FixOperationValidator>();
builder.Services.AddScoped<PackageSnapshotBuilder>();
builder.Services.AddScoped<ExportService>();
builder.Services.AddScoped<XeroIntegrationService>();
builder.Services.AddScoped<XeroTenantLedgerService>();
builder.Services.AddSingleton<XeroApiRequestScheduler>();
builder.Services.AddSingleton<XeroTokenRefreshLock>();
builder.Services.AddScoped<XeroBackfillService>();
builder.Services.AddScoped<FluxReviewService>();
builder.Services.AddScoped<PackageDiffService>();
builder.Services.AddScoped<AiPackageDraftService>();
builder.Services.AddScoped<FinancialStatementGroupingService>();
builder.Services.AddSingleton<CodexCommandBuilder>();
builder.Services.AddSingleton<CodexModelDiscovery>();
builder.Services.AddHostedService<CodexWorker>();
if (builder.Configuration.GetValue("UseSqlite", true))
{
    builder.Services.AddHostedService<SqliteBackupService>();
}
if (builder.Configuration.GetValue("Xero:EnableLedgerSyncWorker", true))
{
    builder.Services.AddHostedService<XeroLedgerSyncWorker>();
}

if (builder.Configuration.GetValue("Xero:EnableBackfillWorker", true))
{
    builder.Services.AddHostedService<XeroBackfillWorker>();
}

var app = builder.Build();

// P3.32 — wire the global exception handler. Must be early in the middleware pipeline.
app.UseExceptionHandler();
app.UseStatusCodePages();

// P3.34 — log every HTTP request with method, path, status, elapsed ms.
app.UseSerilogRequestLogging();

// P3.33 — output cache must come after exception/status middleware but before endpoints.
app.UseOutputCache();

// SECURITY: Admin auth bypass is allowed only in Development. In any other environment, missing
// X-FR-Role / X-FR-User headers must produce 401 instead of silently granting Admin rights.
// See Cat 41 in docs/superpowers/specs/2026-04-27-best-in-class-review/.
AuthBypass.AllowDevAdminBypass = app.Environment.IsDevelopment();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (!app.Environment.IsDevelopment()
        && RequiresAuthentication(context.Request.Path)
        && context.User?.Identity?.IsAuthenticated != true)
    {
        await context.ChallengeAsync();
        return;
    }

    await next();
});
app.UseAuthorization();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (app.Configuration.GetValue("Database:UseMigrations", false))
    {
        await db.Database.MigrateAsync();
    }
    else
    {
        await db.Database.EnsureCreatedAsync();
    }

    await SqliteSchemaPatch.EnsureAsync(db);

    // Enable WAL + a busy_timeout so concurrent readers don't block on writers and
    // SQLITE_BUSY events back off instead of throwing. Prerequisite for the nightly
    // backup hosted service. See Cat 33, 47.
    if (db.Database.IsSqlite())
    {
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;");
    }

    await RealDataCleanupService.PurgeRuntimeMockDataAsync(db, CancellationToken.None);
}

var aiHub = app.MapHub<AiHub>("/hubs/ai");
if (!app.Environment.IsDevelopment())
{
    aiHub.RequireAuthorization();
}

// All endpoints live in Features/<Feature>/<Feature>Endpoints.cs. Each feature exposes a
// MapXxxEndpoints extension method on IEndpointRouteBuilder that registers its routes,
// keeping Program.cs as a thin composition root. Cat 29.
app.MapAuthEndpoints(app.Environment);
app.MapHealthEndpoints();
app.MapAiRunEndpoints();
app.MapAiSettingsEndpoints();
app.MapDistributionEndpoints();
app.MapExportEndpoints();
app.MapFluxEndpoints();
app.MapKpiEndpoints();
app.MapMappingEndpoints();
app.MapPackageApprovalEndpoints();
app.MapPackageContextEndpoints();
app.MapPackageLifecycleEndpoints();
app.MapPackageThemeEndpoints();
app.MapIssueEndpoints();
app.MapPlanningEndpoints();
app.MapReportingEndpoints();
app.MapReportingPeriodEndpoints();
app.MapSlideBlockVersionEndpoints();
app.MapXeroEndpoints();

static bool RequiresAuthentication(PathString path)
    => (path.StartsWithSegments("/api")
        && !path.StartsWithSegments("/api/health")
        && !path.StartsWithSegments("/api/xero/callback"))
       || path.StartsWithSegments("/hubs");

app.Run();

// DevTokenRequest moved to Features/Auth/AuthEndpoints.cs.
public sealed record OrganizationOptionDto(Guid Id, string Key, string Name, string Abbreviation, bool IsConsolidated, bool IsXeroMapped);
public sealed record PeriodOptionDto(Guid Id, string Key, string Label, DateOnly PeriodStart, DateOnly PeriodEnd, bool IsClosed, int PackageCount, int LedgerActivityCount);
public sealed record PackageOptionDto(Guid Id, string OrganizationKey, string OrganizationName, string PeriodKey, string PeriodLabel, string Status);
public sealed record ReportingCoverageDto(string OrganizationKey, string PeriodKey, Guid? PackageId, string? PackageStatus, int LedgerActivityCount);
public sealed record ReportingContextDto(List<OrganizationOptionDto> Organizations, List<PeriodOptionDto> Periods, List<PackageOptionDto> Packages, List<ReportingCoverageDto> Coverage);
public sealed record StatementLineDto(string StatementType, string Section, string RowPath, string LineName, string AccountCode, decimal CurrentAmount, decimal PriorAmount, string AmountsJson);
public sealed record EntityPeriodStatementsDto(string OrganizationKey, string PeriodKey, List<StatementLineDto> Lines);
public sealed record LedgerSummaryLineDto(string AccountCode, string AccountName, decimal NetAmount, int TransactionCount);
public sealed record EntityPeriodLedgerSummaryDto(string OrganizationKey, string PeriodKey, int JournalLineCount, List<LedgerSummaryLineDto> Lines);
public sealed record CreateReportingPeriodRequest(string PeriodKey, bool? IsClosed);
public sealed record CreatePackageRequest(string OrganizationKey, string PeriodKey, string? BaseFrom);
public sealed record ApplyFixRequest(string? Reason, string? Comment);
public sealed record AiRuntimeSettingRequest(string Module, string Model, string ReasoningEffort, string Profile, bool Enabled);
// CreateAiRunRequest moved to Features/Ai/AiRunEndpoints.cs.
public sealed record MapAccountRequest(string FsLine, string Reason);
public sealed record GroupFromFinancialsRequest(string? OrganizationKey, bool? IncludeReviewed);
public sealed record UpsertFsLineDefinitionRequest(string OrganizationKey, string? StatementType, string? Section, string Name, string? NormalBalance, string? AiGuidance, int? SortOrder, bool? IsActive, string? Reason);
public sealed record EliminateAccountRequest(string Type, string Description, string Reason, bool CreateRecurringRule);
public sealed record ExportRequest(Guid ReportPackageId, bool IncludeIssues, bool IncludeAppendix);
public sealed record CreateShareLinkRequest(Guid ReportPackageId, bool RequirePassword, bool AllowDownload, DateTimeOffset? ExpiresAt, string? Password, bool DashboardOnly);
// CreateDistributionScheduleRequest moved to Features/Distribution/DistributionEndpoints.cs.
public sealed record UpdateSlideRequest(string? Subject, string? KpiLabel, decimal? CurrentValue, decimal? PriorValue, string? AccountCodesCsv, string? MonthlyJson, string? PriorMonthlyJson, string? ChartConfigJson);
public sealed record UpsertBlockRequest(string Kind, string ContentJson, int? SortOrder);
public sealed record ReorderBlocksRequest(Guid[] BlockIds);
public sealed record PackageVersionDto(Guid Id, string VersionLabel, string CreatedBy, string ChangeSummary, DateTimeOffset CreatedAt);
public sealed record XeroSyncRequest(Guid? ReportPackageId);
public sealed record XeroSyncPeriodRequest(string PeriodKey, string Basis, bool IncludeAllTenants, bool CreateConsolidation);
public sealed record TenantEntityMapRequest(Guid OrganizationId, bool IsIgnored, string? Reason);
public sealed record XeroLedgerRunRequest(string? TenantId, bool Force);
public sealed record XeroReconciliationRunRequest(DateOnly? SnapshotDate);
public sealed record FluxExplanationRequest(string Explanation, string? Reason);
public sealed record FluxReviewGroupSettingsRequest(decimal? DollarThreshold, decimal? PercentThreshold, string? ThresholdLogic, string? Assignee, string? Reviewer, DateOnly? DueDate, string? ExplanationTemplate, string? Tags, string? ApplyScope, string? Reason);
public sealed record FluxSignOffRequest(string? Action, string? Reason);
public sealed record RejectDraftRequest(string? Reason);
public sealed record SplitMappingLineRequest(string FsLine, decimal Percent);
public sealed record SplitMappingRequest(SplitMappingLineRequest[] Lines, string Reason);
public sealed record MappingReasonRequest(string Reason);
public sealed record RecurringEliminationRuleRequest(Guid OrganizationId, Guid ReportingPeriodId, Guid? GlAccountId, string Type, string Description, string CriteriaJson, decimal Amount, string Reason, bool IsActive);
public sealed record CreateExportQaRequest(Guid? ReportPackageId);
public sealed record UpdateShareLinkRequest(bool? RequirePassword, bool? AllowDownload, bool? DashboardOnly, bool ExpiresAtSet, DateTimeOffset? ExpiresAt, string? Password);
public sealed record UpsertKpiRequest(Guid OrganizationId, string Name, string Category, string Formula, string Unit, decimal CurrentValue, decimal TargetValue, bool IsPinned, bool HigherIsBetter);
public sealed record EvaluateFormulaRequest(Guid OrganizationId, Guid ReportingPeriodId, string Formula);
public sealed record UpsertKpiAlertRequest(Guid KpiDefinitionId, string? Direction, decimal ThresholdValue, string? Severity, string? Message, bool IsActive);
public sealed record UpsertNonFinancialMetricRequest(Guid OrganizationId, Guid ReportingPeriodId, string Name, string Category, string Unit, decimal CurrentValue, decimal PriorValue, decimal TargetValue, string? ValuesJson, string? Source, bool IsPinned);
public sealed record UpsertFxRateRequest(Guid OrganizationId, Guid ReportingPeriodId, string CurrencyCode, decimal RateToPresentation, string? Source);
public sealed record UpsertForecastScenarioRequest(Guid OrganizationId, Guid ReportingPeriodId, string Name, string? Description, string? ScenarioType, int HorizonMonths, decimal RevenueGrowthPercent, decimal GrossMarginPercent, decimal OpexGrowthPercent, decimal CashConversionPercent, decimal StartingCash, decimal CashThreshold, string? AssumptionsJson, bool IsBase);
public sealed record UpsertForecastEventRequest(int MonthOffset, string Name, string? Category, decimal RevenueImpact, decimal ExpenseImpact, decimal CashImpact, bool IsRecurring, string? Notes);
public sealed record ApplyReportTemplateRequest(Guid TemplateId);
public sealed record ReportingStudioApplyRequest(string[]? Sections);
public sealed record UpsertPackageCommentRequest(Guid? PackageSlideId, Guid? SlideBlockId, string Body, string? Status, string? Author);
public sealed record UpdatePackageThemeRequest(string Primary, string Accent, string? LogoFileName, string? FontFamily, string? CoverStyle, string[] PageOrder, string? HeaderText, string? FooterText, JsonElement? ExportSettings);
public sealed record ForecastActuals(DateOnly StartMonth, decimal MonthlyRevenue, decimal MonthlyOperatingExpense, decimal EstimatedStartingCash, decimal CashThreshold);
public sealed record BenchmarkRollup(decimal Revenue, decimal Expense, decimal Net, decimal GrossMarginPercent);

public sealed record XeroConnectionDto(Guid Id, Guid OrganizationId, string TenantId, string TenantName, string TenantType, string ConnectionStatus, DateTimeOffset TokenExpiresAt, DateTimeOffset? LastConnectedAt, string? LastError)
{
    public static XeroConnectionDto From(XeroConnection connection)
        => new(connection.Id, connection.OrganizationId, connection.TenantId, connection.TenantName, connection.TenantType, connection.ConnectionStatus, connection.TokenExpiresAt, connection.LastConnectedAt, connection.LastError);
}

public sealed record ExportArtifactDto(Guid Id, Guid ReportPackageId, string Type, string Status, string FileName, string ContentType, string DownloadUrl, string MetadataJson, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt)
{
    public static ExportArtifactDto From(ExportArtifact artifact)
        => new(artifact.Id, artifact.ReportPackageId, artifact.Type, artifact.Status, artifact.FileName, artifact.ContentType, $"/api/exports/{artifact.Id}/download", artifact.MetadataJson, artifact.CreatedAt, artifact.CompletedAt);
}

public sealed record ShareLinkDto(Guid Id, Guid ReportPackageId, string Token, bool RequirePassword, bool AllowDownload, bool DashboardOnly, DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt)
{
    public static ShareLinkDto From(ShareLink link)
        => new(link.Id, link.ReportPackageId, link.Token, link.RequirePassword, link.AllowDownload, link.DashboardOnly, link.ExpiresAt, link.CreatedAt);
}

public sealed record KpiDto(Guid Id, Guid OrganizationId, string Name, string Category, string Formula, string Unit, decimal CurrentValue, decimal TargetValue, bool IsPinned, string Status)
{
    public static KpiDto From(KpiDefinition kpi)
        => new(kpi.Id, kpi.OrganizationId, kpi.Name, kpi.Category, kpi.Formula, kpi.Unit, kpi.CurrentValue, kpi.TargetValue, kpi.IsPinned, kpi.Status);
}

public sealed record KpiAlertDto(Guid Id, Guid KpiDefinitionId, string KpiName, string Direction, decimal ThresholdValue, string Severity, string Message, bool IsActive, bool IsTriggered, DateTimeOffset? LastTriggeredAt)
{
    public static KpiAlertDto From(KpiAlert alert)
        => From(alert, alert.KpiDefinition);

    public static KpiAlertDto From(KpiAlert alert, KpiDefinition? kpi)
    {
        var triggered = kpi is not null && alert.IsActive && (alert.Direction.Equals("Above", StringComparison.OrdinalIgnoreCase)
            ? kpi.CurrentValue > alert.ThresholdValue
            : kpi.CurrentValue < alert.ThresholdValue);
        return new(alert.Id, alert.KpiDefinitionId, kpi?.Name ?? "", alert.Direction, alert.ThresholdValue, alert.Severity, alert.Message, alert.IsActive, triggered, alert.LastTriggeredAt);
    }
}

public sealed record NonFinancialMetricDto(Guid Id, Guid OrganizationId, Guid ReportingPeriodId, string Name, string Category, string Unit, decimal CurrentValue, decimal PriorValue, decimal TargetValue, string ValuesJson, string Source, bool IsPinned)
{
    public static NonFinancialMetricDto From(NonFinancialMetric metric)
        => new(metric.Id, metric.OrganizationId, metric.ReportingPeriodId, metric.Name, metric.Category, metric.Unit, metric.CurrentValue, metric.PriorValue, metric.TargetValue, metric.ValuesJson, metric.Source, metric.IsPinned);
}

public sealed record FormulaEvaluationDto(string Formula, string NormalizedExpression, decimal Value, string[] Dependencies);

public sealed record FxRateDto(Guid Id, Guid OrganizationId, Guid ReportingPeriodId, string CurrencyCode, decimal RateToPresentation, string Source, DateTimeOffset UpdatedAt)
{
    public static FxRateDto From(FxRate rate)
        => new(rate.Id, rate.OrganizationId, rate.ReportingPeriodId, rate.CurrencyCode, rate.RateToPresentation, rate.Source, rate.UpdatedAt);
}

public sealed record PackageCommentDto(Guid Id, Guid ReportPackageId, Guid? PackageSlideId, Guid? SlideBlockId, string Body, string Status, string Author, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ResolvedAt)
{
    public static PackageCommentDto From(PackageComment comment)
        => new(comment.Id, comment.ReportPackageId, comment.PackageSlideId, comment.SlideBlockId, comment.Body, comment.Status, comment.Author, comment.CreatedAt, comment.UpdatedAt, comment.ResolvedAt);
}

public sealed record ForecastEventDto(Guid Id, int MonthOffset, string Name, string Category, decimal RevenueImpact, decimal ExpenseImpact, decimal CashImpact, bool IsRecurring, string Notes)
{
    public static ForecastEventDto From(ForecastEvent forecastEvent)
        => new(forecastEvent.Id, forecastEvent.MonthOffset, forecastEvent.Name, forecastEvent.Category, forecastEvent.RevenueImpact, forecastEvent.ExpenseImpact, forecastEvent.CashImpact, forecastEvent.IsRecurring, forecastEvent.Notes);
}

public sealed record ForecastProjectionRowDto(string MonthKey, decimal Revenue, decimal GrossProfit, decimal OperatingExpense, decimal NetIncome, decimal CashInflow, decimal CashOutflow, decimal NetCashFlow, decimal EndingCash, decimal AccountsReceivable, decimal AccountsPayable, decimal Equity, bool CashThresholdBreached)
{
    public static ForecastProjectionRowDto From(ForecastProjectionRow row)
        => new(row.MonthKey, row.Revenue, row.GrossProfit, row.OperatingExpense, row.NetIncome, row.CashInflow, row.CashOutflow, row.NetCashFlow, row.EndingCash, row.AccountsReceivable, row.AccountsPayable, row.Equity, row.CashThresholdBreached);
}

public sealed record ForecastScenarioDto(Guid Id, Guid OrganizationId, Guid ReportingPeriodId, string Name, string Description, string ScenarioType, int HorizonMonths, decimal RevenueGrowthPercent, decimal GrossMarginPercent, decimal OpexGrowthPercent, decimal CashConversionPercent, decimal StartingCash, decimal CashThreshold, string AssumptionsJson, bool IsBase, List<ForecastEventDto> Events, List<ForecastProjectionRowDto> Rows)
{
    public static ForecastScenarioDto From(ForecastScenario scenario, ForecastActuals actuals)
    {
        var eventInputs = scenario.Events.Select(x => new ForecastEventInput(x.MonthOffset, x.Name, x.RevenueImpact, x.ExpenseImpact, x.CashImpact, x.IsRecurring));
        var rows = ForecastingMath.BuildThreeWayForecast(
                actuals.StartMonth,
                scenario.HorizonMonths,
                actuals.MonthlyRevenue,
                actuals.MonthlyOperatingExpense,
                scenario.RevenueGrowthPercent,
                scenario.GrossMarginPercent,
                scenario.OpexGrowthPercent,
                scenario.CashConversionPercent,
                scenario.StartingCash > 0m ? scenario.StartingCash : actuals.EstimatedStartingCash,
                scenario.CashThreshold > 0m ? scenario.CashThreshold : actuals.CashThreshold,
                eventInputs)
            .Select(ForecastProjectionRowDto.From)
            .ToList();

        return new(
            scenario.Id,
            scenario.OrganizationId,
            scenario.ReportingPeriodId,
            scenario.Name,
            scenario.Description,
            scenario.ScenarioType,
            scenario.HorizonMonths,
            scenario.RevenueGrowthPercent,
            scenario.GrossMarginPercent,
            scenario.OpexGrowthPercent,
            scenario.CashConversionPercent,
            scenario.StartingCash,
            scenario.CashThreshold,
            scenario.AssumptionsJson,
            scenario.IsBase,
            scenario.Events.OrderBy(x => x.MonthOffset).Select(ForecastEventDto.From).ToList(),
            rows);
    }
}

public sealed record BudgetVarianceRowDto(string FsLine, decimal ActualAmount, decimal BudgetAmount, decimal VarianceAmount, decimal VariancePercent);
public sealed record PlanningOverviewDto(Guid ReportPackageId, Guid OrganizationId, string OrganizationName, string PeriodKey, decimal MonthlyRevenueActual, decimal MonthlyOperatingExpenseActual, DateOnly ForecastStartMonth, List<ForecastScenarioDto> Scenarios, List<BudgetVarianceRowDto> BudgetRows, List<NonFinancialMetricDto> NonFinancialMetrics);
public sealed record CashTimingRowDto(string Label, DateOnly PeriodStart, DateOnly PeriodEnd, decimal CashInflow, decimal CashOutflow, decimal NetCashFlow, decimal EndingCash, bool CashThresholdBreached);
public sealed record CashTimingDto(Guid ReportPackageId, Guid ScenarioId, string Granularity, List<CashTimingRowDto> Rows);
public sealed record BenchmarkRowDto(Guid OrganizationId, string OrganizationName, string Abbreviation, bool IsConsolidated, Guid? ReportPackageId, string PackageStatus, decimal Revenue, decimal Expense, decimal NetIncome, decimal GrossMarginPercent, int OpenIssueCount, List<KpiDto> Kpis, int Rank = 0);
public sealed record BenchmarkingDto(string PeriodKey, List<BenchmarkRowDto> Rows);

public sealed record ReportTemplateDto(Guid Id, string Name, string Category, string Description, string[] Sections, bool IsBuiltIn)
{
    public static ReportTemplateDto From(ReportTemplate template)
    {
        string[] sections;
        try
        {
            sections = JsonSerializer.Deserialize<string[]>(template.SectionsJson) ?? [];
        }
        catch
        {
            sections = [];
        }

        return new(template.Id, template.Name, template.Category, template.Description, sections, template.IsBuiltIn);
    }
}

public sealed record CompetitiveFeatureDto(string Name, string Status, string OurImplementation);
public sealed record CompetitiveFeatureGroupDto(string Category, string CompetitorPattern, CompetitiveFeatureDto[] Features);
public sealed record ReportingStudioDto(
    Guid ReportPackageId,
    string OrganizationName,
    string PeriodKey,
    ReportingStudioSettingsDto Settings,
    IReadOnlyList<ReportingStudioContentGroupDto> ContentLibrary,
    List<ReportingStudioStatementSectionDto> FsLineSections,
    List<ReportingStudioStatementSectionDto> StatementSections,
    List<ReportingStudioQualityCheckDto> QualityChecks,
    int QualityScore,
    IReadOnlyList<CompetitiveFeatureGroupDto> MarketCapabilities);

public sealed record ReportingStudioSettingsDto(
    string ReportStyle,
    string NumberFormat,
    string Rounding,
    string StatementLayout,
    string CommentaryTone,
    string[] ReportSections,
    bool ShowPriorMonth,
    bool ShowPriorYear,
    bool ShowBudget,
    bool ShowForecast,
    bool ShowYtd,
    bool ShowRollingTwelve,
    bool ShowVarianceDollar,
    bool ShowVariancePercent,
    bool ShowZeroRows,
    bool LandscapeForWideTables,
    bool IncludeFluxNarratives,
    bool IncludeLedgerEvidence,
    bool IncludeFinalReview,
    bool IncludeActionPlan);

public sealed record ReportingStudioContentGroupDto(string Name, string Description, ReportingStudioContentItemDto[] Items);
public sealed record ReportingStudioContentItemDto(string Name, string Kind, string Description);
public sealed record ReportingStudioStatementSectionDto(string StatementType, string Section, int LineCount);
public sealed record ReportingStudioQualityCheckDto(string Name, string Status, string Detail, string Recommendation);

public sealed record PackageDto(
    Guid Id,
    Guid OrganizationId,
    Guid ReportingPeriodId,
    string OrganizationKey,
    string OrganizationName,
    string OrganizationAbbreviation,
    string PeriodKey,
    string Period,
    string Status,
    string VersionLabel,
    string BaseFrom,
    DateTimeOffset? LastXeroSyncAt,
    bool IsSourceDataStale,
    string? SourceDataStaleReason,
    DateTimeOffset? SourceDataChangedAt,
    string? BlockReason,
    string ThemeJson,
    List<SlideDto> Slides,
    List<IssueDto> Issues)
{
    public static PackageDto From(ReportPackage package)
        => new(
            package.Id,
            package.OrganizationId,
            package.ReportingPeriodId,
            package.Organization?.Key ?? "",
            package.Organization?.Name ?? "",
            package.Organization?.Abbreviation ?? "",
            package.ReportingPeriod?.Key ?? "",
            package.ReportingPeriod?.Label ?? "",
            package.Status.ToString(),
            package.VersionLabel,
            package.BaseFrom,
            package.LastXeroSyncAt,
            package.IsSourceDataStale || !string.IsNullOrWhiteSpace(package.BlockReason),
            package.SourceDataStaleReason ?? package.BlockReason,
            package.SourceDataChangedAt,
            package.BlockReason,
            package.ThemeJson,
            package.Slides.OrderBy(x => x.SortOrder).Select(SlideDto.From).ToList(),
            package.Issues.OrderByDescending(x => x.CreatedAt).Select(IssueDto.From).ToList());
}

public sealed record SlideDto(
    Guid Id,
    int SortOrder,
    string Subject,
    string KpiLabel,
    decimal CurrentValue,
    decimal PriorValue,
    decimal VarianceAmount,
    decimal VariancePercent,
    string AccountCodesCsv,
    string MonthlyJson,
    string PriorMonthlyJson,
    string ChartConfigJson,
    List<SlideBlockDto> Blocks)
{
    public static SlideDto From(PackageSlide slide)
        => new(slide.Id, slide.SortOrder, slide.Subject, slide.KpiLabel, slide.CurrentValue, slide.PriorValue, slide.VarianceAmount, slide.VariancePercent, slide.AccountCodesCsv, slide.MonthlyJson, slide.PriorMonthlyJson, slide.ChartConfigJson, slide.Blocks.OrderBy(x => x.SortOrder).Select(SlideBlockDto.From).ToList());
}

public sealed record SlideBlockDto(Guid Id, int SortOrder, string Kind, string ContentJson)
{
    public static SlideBlockDto From(SlideBlock block) => new(block.Id, block.SortOrder, block.Kind, block.ContentJson);
}

public sealed record IssueDto(Guid Id, Guid? PackageSlideId, string Severity, string Status, string Category, string Title, string Description, string EvidenceJson, string RecommendedFixJson, decimal Confidence)
{
    public static IssueDto From(PackageIssue issue)
        => new(issue.Id, issue.PackageSlideId, issue.Severity.ToString(), issue.Status.ToString(), issue.Category, issue.Title, issue.Description, issue.EvidenceJson, issue.RecommendedFixJson, issue.Confidence);
}

public sealed record AccountDto(Guid Id, string TenantId, string Code, string Name, string Type, string FsLine, string AiSuggestedFsLine, decimal MappingConfidence, bool IsFirstSeen, string ReviewStatus, string ConsolidationTreatment, string MonthlyBalancesJson, int TransactionCount)
{
    public static AccountDto From(GlAccount account)
        => new(account.Id, account.TenantId, account.Code, account.Name, account.Type, account.FsLine, account.AiSuggestedFsLine, account.MappingConfidence, account.IsFirstSeen, account.ReviewStatus.ToString(), account.ConsolidationTreatment.ToString(), account.MonthlyBalancesJson, account.Transactions.Count);
}

public sealed record FsLineDefinitionDto(Guid Id, Guid OrganizationId, string StatementType, string Section, string Name, string NormalBalance, string AiGuidance, int SortOrder, bool IsActive)
{
    public static FsLineDefinitionDto From(FsLineDefinition line)
        => new(line.Id, line.OrganizationId, line.StatementType, line.Section, line.Name, line.NormalBalance, line.AiGuidance, line.SortOrder, line.IsActive);
}

public sealed record AccountDetailDto(AccountDto Account, List<TransactionDto> Transactions)
{
    public static AccountDetailDto From(GlAccount account)
        => new(AccountDto.From(account), account.Transactions.OrderByDescending(x => x.TransactionDate).Select(TransactionDto.From).ToList());
}

public sealed record TransactionDto(Guid Id, DateOnly TransactionDate, string Description, decimal Debit, decimal Credit, string Source)
{
    public static TransactionDto From(GlTransaction tx) => new(tx.Id, tx.TransactionDate, tx.Description, tx.Debit, tx.Credit, tx.Source);
}
