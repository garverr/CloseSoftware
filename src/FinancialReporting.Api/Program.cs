using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using FinancialReporting.Api.Hubs;
using FinancialReporting.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddSignalR();
builder.Services.AddHttpClient();
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
builder.Services.AddScoped<XeroBackfillService>();
builder.Services.AddScoped<FluxReviewService>();
builder.Services.AddScoped<AiPackageDraftService>();
builder.Services.AddSingleton<CodexCommandBuilder>();
builder.Services.AddSingleton<CodexModelDiscovery>();
builder.Services.AddHostedService<CodexWorker>();
if (builder.Configuration.GetValue("Xero:EnableLedgerSyncWorker", true))
{
    builder.Services.AddHostedService<XeroLedgerSyncWorker>();
}

if (builder.Configuration.GetValue("Xero:EnableBackfillWorker", true))
{
    builder.Services.AddHostedService<XeroBackfillWorker>();
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();

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
    await RealDataCleanupService.PurgeRuntimeMockDataAsync(db, CancellationToken.None);
}

app.MapHub<AiHub>("/hubs/ai");

app.MapGet("/api/health", (IConfiguration config) => Results.Ok(new
{
    status = "ok",
    database = "SQLite",
    aiRunner = config.GetValue("Ai:UseMockRunner", true) ? "mock" : "codex-cli"
}));

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

app.MapPost("/api/reporting-periods", async (
    CreateReportingPeriodRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    if (!TryParsePeriodKey(request.PeriodKey, out var year, out var month))
    {
        return Results.BadRequest(new { error = "PeriodKey must be in YYYY-MM format." });
    }

    var existing = await db.ReportingPeriods.FirstOrDefaultAsync(x => x.Key == request.PeriodKey, ct);
    if (existing is not null)
    {
        return Results.Ok(new PeriodOptionDto(existing.Id, existing.Key, existing.Label, existing.PeriodStart, existing.PeriodEnd, existing.IsClosed, 0, 0));
    }

    var period = BuildReportingPeriod(year, month, request.IsClosed ?? false);
    db.ReportingPeriods.Add(period);
    await AuditAsync(db, http, "period.create", "ReportingPeriod", period.Id, null, "Created reporting period", "{}", JsonSerializer.Serialize(period), ct);
    await db.SaveChangesAsync(ct);

    return Results.Created($"/api/reporting-periods/{period.Key}", new PeriodOptionDto(period.Id, period.Key, period.Label, period.PeriodStart, period.PeriodEnd, period.IsClosed, 0, 0));
});

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

app.MapPost("/api/packages", async (CreatePackageRequest request, AppDbContext db, CancellationToken ct) =>
{
    var org = await db.Organizations.FirstOrDefaultAsync(x => x.Key == request.OrganizationKey, ct);
    var period = await db.ReportingPeriods.FirstOrDefaultAsync(x => x.Key == request.PeriodKey, ct);
    if (org is null || period is null)
    {
        return Results.BadRequest(new { error = "OrganizationKey and PeriodKey must exist." });
    }

    var package = new ReportPackage
    {
        Id = Guid.NewGuid(),
        OrganizationId = org.Id,
        ReportingPeriodId = period.Id,
        Status = PackageStatus.Draft,
        VersionLabel = "v1",
        BaseFrom = request.BaseFrom ?? "",
        ThemeJson = JsonSerializer.Serialize(new { primary = org.PrimaryColor, accent = org.AccentColor, org.CoverStyle })
    };

    db.ReportPackages.Add(package);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/packages/{package.Id}", new { package.Id });
});

app.MapPost("/api/packages/ensure", async (
    CreatePackageRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var org = await db.Organizations.FirstOrDefaultAsync(x => x.Key == request.OrganizationKey, ct);
    var period = await db.ReportingPeriods.FirstOrDefaultAsync(x => x.Key == request.PeriodKey, ct);
    if (org is null || period is null)
    {
        return Results.BadRequest(new { error = "OrganizationKey and PeriodKey must exist." });
    }

    var package = await db.ReportPackages
        .Include(x => x.Organization)
        .Include(x => x.ReportingPeriod)
        .Include(x => x.Slides.OrderBy(s => s.SortOrder))
            .ThenInclude(s => s.Blocks.OrderBy(b => b.SortOrder))
        .Include(x => x.Issues)
        .FirstOrDefaultAsync(x => x.OrganizationId == org.Id && x.ReportingPeriodId == period.Id, ct);

    if (package is null)
    {
        package = new ReportPackage
        {
            Id = Guid.NewGuid(),
            OrganizationId = org.Id,
            Organization = org,
            ReportingPeriodId = period.Id,
            ReportingPeriod = period,
            Status = PackageStatus.Draft,
            VersionLabel = "v1",
            BaseFrom = request.BaseFrom ?? BuildBaseFrom(period),
            ThemeJson = JsonSerializer.Serialize(new { primary = org.PrimaryColor, accent = org.AccentColor, org.CoverStyle })
        };
        db.ReportPackages.Add(package);
        await AuditAsync(db, http, "package.create", "ReportPackage", package.Id, package.Id, "Created empty package shell", "{}", JsonSerializer.Serialize(new { organizationKey = org.Key, periodKey = period.Key }), ct);
        await db.SaveChangesAsync(ct);
    }

    package = await db.ReportPackages
        .AsNoTracking()
        .Include(x => x.Organization)
        .Include(x => x.ReportingPeriod)
        .Include(x => x.Slides.OrderBy(s => s.SortOrder))
            .ThenInclude(s => s.Blocks.OrderBy(b => b.SortOrder))
        .Include(x => x.Issues)
        .FirstAsync(x => x.Id == package.Id, ct);

    return Results.Ok(PackageDto.From(package));
});

app.MapPost("/api/packages/{packageId:guid}/recompile", async (
    Guid packageId,
    HttpContext http,
    AppDbContext db,
    XeroIntegrationService xero,
    PackageSnapshotBuilder snapshotBuilder,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var package = await db.ReportPackages.FirstOrDefaultAsync(x => x.Id == packageId, ct);
    if (package is null)
    {
        return Results.NotFound();
    }

    package.Status = PackageStatus.Syncing;
    await db.SaveChangesAsync(ct);

    var before = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct);
    var run = await xero.SyncPackageAsync(db, packageId, ct);
    db.PackageVersions.Add(new PackageVersion
    {
        Id = Guid.NewGuid(),
        ReportPackageId = package.Id,
        VersionLabel = $"Recompile {DateTimeOffset.UtcNow:HH:mm}",
        CreatedBy = Actor(http),
        ChangeSummary = "Recompiled package from Xero source data",
        SnapshotJson = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct)
    });
    await AuditAsync(db, http, "xero.sync", "ReportPackage", packageId, packageId, "Recompile package from Xero source data", before, JsonSerializer.Serialize(run), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { package.Id, Status = package.Status.ToString(), package.LastXeroSyncAt, syncRun = run });
});

app.MapPost("/api/packages/{packageId:guid}/final-review", async (
    Guid packageId,
    HttpContext http,
    AppDbContext db,
    PackageSnapshotBuilder snapshotBuilder,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    var packageExists = await db.ReportPackages.AnyAsync(x => x.Id == packageId, ct);
    if (!packageExists)
    {
        return Results.NotFound();
    }

    var setting = await db.AiRuntimeSettings.FirstAsync(x => x.Module == "final-review", ct);
    var snapshot = await snapshotBuilder.BuildFinalReviewSnapshotAsync(packageId, ct);
    var run = new AiRun
    {
        Id = Guid.NewGuid(),
        ReportPackageId = packageId,
        Module = "final-review",
        PromptProfile = setting.Profile,
        Model = setting.Model,
        ReasoningEffort = setting.ReasoningEffort,
        InputJson = snapshot
    };
    db.AiRuns.Add(run);
    await AuditAsync(db, http, "ai.final-review.queue", "AiRun", run.Id, packageId, "Queued final AI review", "{}", snapshot, ct);
    await db.SaveChangesAsync(ct);
    return Results.Accepted($"/api/ai/runs/{run.Id}", AiRunDto.From(run));
});

app.MapPost("/api/packages/{packageId:guid}/issues/{issueId:guid}/apply-fix", async (
    Guid packageId,
    Guid issueId,
    ApplyFixRequest request,
    HttpContext http,
    AppDbContext db,
    FixOperationValidator validator,
    PackageSnapshotBuilder snapshotBuilder,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    var issue = await db.PackageIssues.FirstOrDefaultAsync(x => x.Id == issueId && x.ReportPackageId == packageId, ct);
    if (issue is null)
    {
        return Results.NotFound();
    }

    var operations = ParseOperations(issue.RecommendedFixJson).ToList();
    var errors = validator.Validate(operations);
    if (errors.Count > 0)
    {
        return Results.BadRequest(new { errors });
    }

    var before = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct);
    foreach (var operation in operations)
    {
        await ApplyOperationAsync(db, operation, request.Reason ?? "User approved AI recommendation", ct);
    }

    issue.Status = IssueStatus.Resolved;
    issue.UserComment = request.Comment;
    issue.ResolvedAt = DateTimeOffset.UtcNow;
    db.PackageVersions.Add(new PackageVersion
    {
        Id = Guid.NewGuid(),
        ReportPackageId = packageId,
        VersionLabel = $"Fix {DateTimeOffset.UtcNow:HH:mm}",
        CreatedBy = Actor(http),
        ChangeSummary = $"Applied AI fix for issue: {issue.Title}",
        SnapshotJson = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct)
    });
    await AuditAsync(db, http, "ai.apply-fix", "PackageIssue", issueId, packageId, request.Reason ?? "User approved AI recommendation", before, issue.RecommendedFixJson, ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { issue.Id, Status = issue.Status.ToString() });
});

app.MapPost("/api/packages/{packageId:guid}/issues/{issueId:guid}/ignore", async (
    Guid packageId,
    Guid issueId,
    ApplyFixRequest request,
    HttpContext http,
    AppDbContext db,
    PackageSnapshotBuilder snapshotBuilder,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    var issue = await db.PackageIssues.FirstOrDefaultAsync(x => x.Id == issueId && x.ReportPackageId == packageId, ct);
    if (issue is null)
    {
        return Results.NotFound();
    }

    var before = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct);
    issue.Status = IssueStatus.Ignored;
    issue.UserComment = request.Comment ?? request.Reason;
    issue.ResolvedAt = DateTimeOffset.UtcNow;
    await AuditAsync(db, http, "issue.ignore", "PackageIssue", issue.Id, packageId, request.Reason ?? "Ignored from issue workbench", before, JsonSerializer.Serialize(issue), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(IssueDto.From(issue));
});

app.MapGet("/api/packages/{packageId:guid}/versions", async (Guid packageId, AppDbContext db, CancellationToken ct) =>
{
    var versions = await db.PackageVersions
        .AsNoTracking()
        .Where(x => x.ReportPackageId == packageId)
        .Select(x => new PackageVersionDto(x.Id, x.VersionLabel, x.CreatedBy, x.ChangeSummary, x.CreatedAt))
        .ToListAsync(ct);
    return Results.Ok(versions.OrderByDescending(x => x.CreatedAt));
});

app.MapPost("/api/packages/{packageId:guid}/versions/{versionId:guid}/restore", async (
    Guid packageId,
    Guid versionId,
    HttpContext http,
    AppDbContext db,
    PackageSnapshotBuilder snapshotBuilder,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var version = await db.PackageVersions.FirstOrDefaultAsync(x => x.ReportPackageId == packageId && x.Id == versionId, ct);
    if (version is null)
    {
        return Results.NotFound();
    }

    var before = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct);
    var restored = await RestorePackageSnapshotAsync(db, packageId, version.SnapshotJson, ct);
    if (!restored)
    {
        return Results.BadRequest(new { error = "Version snapshot could not be restored." });
    }

    db.PackageVersions.Add(new PackageVersion
    {
        Id = Guid.NewGuid(),
        ReportPackageId = packageId,
        VersionLabel = $"Restore {DateTimeOffset.UtcNow:HH:mm}",
        CreatedBy = Actor(http),
        ChangeSummary = $"Restored {version.VersionLabel}",
        SnapshotJson = before
    });
    await AuditAsync(db, http, "package.version.restore", "PackageVersion", versionId, packageId, "Restore package version", before, version.SnapshotJson, ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { restored = versionId });
});

app.MapPut("/api/slides/{slideId:guid}", async (
    Guid slideId,
    UpdateSlideRequest request,
    HttpContext http,
    AppDbContext db,
    PackageSnapshotBuilder snapshotBuilder,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var slide = await db.PackageSlides.Include(x => x.ReportPackage).FirstOrDefaultAsync(x => x.Id == slideId, ct);
    if (slide is null)
    {
        return Results.NotFound();
    }

    var before = await snapshotBuilder.BuildPackageSnapshotAsync(slide.ReportPackageId, ct);
    slide.Subject = request.Subject ?? slide.Subject;
    slide.KpiLabel = request.KpiLabel ?? slide.KpiLabel;
    slide.AccountCodesCsv = request.AccountCodesCsv ?? slide.AccountCodesCsv;
    slide.MonthlyJson = request.MonthlyJson ?? slide.MonthlyJson;
    slide.PriorMonthlyJson = request.PriorMonthlyJson ?? slide.PriorMonthlyJson;
    slide.ChartConfigJson = request.ChartConfigJson ?? slide.ChartConfigJson;
    if (request.CurrentValue is not null) slide.CurrentValue = request.CurrentValue.Value;
    if (request.PriorValue is not null) slide.PriorValue = request.PriorValue.Value;
    var variance = FinancialMath.Variance(slide.CurrentValue, slide.PriorValue);
    slide.VarianceAmount = variance.Amount;
    slide.VariancePercent = variance.Percent;
    slide.ReportPackage!.UpdatedAt = DateTimeOffset.UtcNow;
    await AddVersionAndAuditAsync(db, http, snapshotBuilder, slide.ReportPackageId, "slide.update", "PackageSlide", slide.Id, "Updated slide", before, ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(SlideDto.From(slide));
});

app.MapPost("/api/slides/{slideId:guid}/blocks", async (
    Guid slideId,
    UpsertBlockRequest request,
    HttpContext http,
    AppDbContext db,
    PackageSnapshotBuilder snapshotBuilder,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var slide = await db.PackageSlides.Include(x => x.Blocks).FirstOrDefaultAsync(x => x.Id == slideId, ct);
    if (slide is null)
    {
        return Results.NotFound();
    }

    var before = await snapshotBuilder.BuildPackageSnapshotAsync(slide.ReportPackageId, ct);
    var nextSortOrder = request.SortOrder ?? ((slide.Blocks.Select(x => (int?)x.SortOrder).Max() ?? 0) + 1);
    var block = new SlideBlock
    {
        Id = Guid.NewGuid(),
        PackageSlideId = slideId,
        SortOrder = nextSortOrder,
        Kind = request.Kind,
        ContentJson = request.ContentJson
    };
    db.SlideBlocks.Add(block);
    await AddVersionAndAuditAsync(db, http, snapshotBuilder, slide.ReportPackageId, "block.create", "SlideBlock", block.Id, "Created slide block", before, ct);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/blocks/{block.Id}", SlideBlockDto.From(block));
});

app.MapPut("/api/blocks/{blockId:guid}", async (
    Guid blockId,
    UpsertBlockRequest request,
    HttpContext http,
    AppDbContext db,
    PackageSnapshotBuilder snapshotBuilder,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var block = await db.SlideBlocks.Include(x => x.PackageSlide).FirstOrDefaultAsync(x => x.Id == blockId, ct);
    if (block is null)
    {
        return Results.NotFound();
    }

    var before = await snapshotBuilder.BuildPackageSnapshotAsync(block.PackageSlide!.ReportPackageId, ct);
    block.Kind = string.IsNullOrWhiteSpace(request.Kind) ? block.Kind : request.Kind;
    block.ContentJson = string.IsNullOrWhiteSpace(request.ContentJson) ? block.ContentJson : request.ContentJson;
    if (request.SortOrder is not null) block.SortOrder = request.SortOrder.Value;
    await AddVersionAndAuditAsync(db, http, snapshotBuilder, block.PackageSlide.ReportPackageId, "block.update", "SlideBlock", block.Id, "Updated slide block", before, ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(SlideBlockDto.From(block));
});

app.MapDelete("/api/blocks/{blockId:guid}", async (
    Guid blockId,
    HttpContext http,
    AppDbContext db,
    PackageSnapshotBuilder snapshotBuilder,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var block = await db.SlideBlocks.Include(x => x.PackageSlide).FirstOrDefaultAsync(x => x.Id == blockId, ct);
    if (block is null)
    {
        return Results.NotFound();
    }

    var packageId = block.PackageSlide!.ReportPackageId;
    var before = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct);
    db.SlideBlocks.Remove(block);
    await AddVersionAndAuditAsync(db, http, snapshotBuilder, packageId, "block.delete", "SlideBlock", block.Id, "Deleted slide block", before, ct);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.MapPost("/api/slides/{slideId:guid}/reorder-blocks", async (
    Guid slideId,
    ReorderBlocksRequest request,
    HttpContext http,
    AppDbContext db,
    PackageSnapshotBuilder snapshotBuilder,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var slide = await db.PackageSlides.Include(x => x.Blocks).FirstOrDefaultAsync(x => x.Id == slideId, ct);
    if (slide is null)
    {
        return Results.NotFound();
    }

    var before = await snapshotBuilder.BuildPackageSnapshotAsync(slide.ReportPackageId, ct);
    var order = request.BlockIds.Select((id, index) => new { id, sort = index + 1 }).ToDictionary(x => x.id, x => x.sort);
    foreach (var block in slide.Blocks)
    {
        if (order.TryGetValue(block.Id, out var sort))
        {
            block.SortOrder = sort;
        }
    }

    await AddVersionAndAuditAsync(db, http, snapshotBuilder, slide.ReportPackageId, "block.reorder", "PackageSlide", slide.Id, "Reordered slide blocks", before, ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(slide.Blocks.OrderBy(x => x.SortOrder).Select(SlideBlockDto.From));
});

app.MapGet("/api/settings/ai-runtime", async (AppDbContext db, CancellationToken ct) =>
{
    var settings = await db.AiRuntimeSettings.AsNoTracking().OrderBy(x => x.Module).ToListAsync(ct);
    return Results.Ok(settings);
});

app.MapPut("/api/settings/ai-runtime", async (List<AiRuntimeSettingRequest> requests, HttpContext http, AppDbContext db, CancellationToken ct) =>
{
    if (!Can(http, "Admin"))
    {
        return Results.Forbid();
    }

    var before = JsonSerializer.Serialize(await db.AiRuntimeSettings.AsNoTracking().OrderBy(x => x.Module).ToListAsync(ct));
    foreach (var request in requests)
    {
        var setting = await db.AiRuntimeSettings.FirstOrDefaultAsync(x => x.Module == request.Module, ct);
        if (setting is null)
        {
            db.AiRuntimeSettings.Add(new AiRuntimeSetting
            {
                Id = Guid.NewGuid(),
                Module = request.Module,
                Model = request.Model,
                ReasoningEffort = request.ReasoningEffort,
                Profile = request.Profile,
                Enabled = request.Enabled,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            setting.Model = request.Model;
            setting.ReasoningEffort = request.ReasoningEffort;
            setting.Profile = request.Profile;
            setting.Enabled = request.Enabled;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    await AuditAsync(db, http, "ai.settings.update", "AiRuntimeSetting", null, null, "Updated AI runtime settings", before, JsonSerializer.Serialize(requests), ct);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.MapGet("/api/ai/models", async (CodexModelDiscovery discovery, CancellationToken ct) =>
    Results.Ok(await discovery.DiscoverAsync(ct)));

app.MapPost("/api/ai/runs", async (CreateAiRunRequest request, HttpContext http, AppDbContext db, CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    var setting = await db.AiRuntimeSettings.FirstOrDefaultAsync(x => x.Module == request.Module, ct);
    var run = new AiRun
    {
        Id = Guid.NewGuid(),
        ReportPackageId = request.ReportPackageId,
        Module = request.Module,
        PromptProfile = request.PromptProfile ?? setting?.Profile ?? request.Module,
        Model = request.Model ?? setting?.Model ?? "gpt-5.5",
        ReasoningEffort = request.ReasoningEffort ?? setting?.ReasoningEffort ?? "high",
        InputJson = request.InputJson
    };
    db.AiRuns.Add(run);
    await AuditAsync(db, http, "ai.run.queue", "AiRun", run.Id, request.ReportPackageId, $"Queued {request.Module}", "{}", request.InputJson, ct);
    await db.SaveChangesAsync(ct);
    return Results.Accepted($"/api/ai/runs/{run.Id}", AiRunDto.From(run));
});

app.MapGet("/api/ai/runs/{runId:guid}", async (Guid runId, AppDbContext db, CancellationToken ct) =>
{
    var run = await db.AiRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == runId, ct);
    return run is null ? Results.NotFound() : Results.Ok(AiRunDto.From(run));
});

app.MapPost("/api/ai/runs/{runId:guid}/cancel", async (Guid runId, HttpContext http, AppDbContext db, CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    var run = await db.AiRuns.FirstOrDefaultAsync(x => x.Id == runId, ct);
    if (run is null)
    {
        return Results.NotFound();
    }

    run.CancellationRequested = true;
    if (run.Status == AiRunStatus.Queued)
    {
        run.Status = AiRunStatus.Cancelled;
        run.CompletedAt = DateTimeOffset.UtcNow;
    }

    await AuditAsync(db, http, "ai.run.cancel", "AiRun", run.Id, run.ReportPackageId, "Cancelled AI run", "{}", JsonSerializer.Serialize(run), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(AiRunDto.From(run));
});

app.MapGet("/api/mapping/accounts", async (
    string? organizationKey,
    string? periodKey,
    string? status,
    bool? firstSeen,
    AppDbContext db,
    CancellationToken ct) =>
{
    var query = db.GlAccounts.AsNoTracking()
        .Include(x => x.Transactions)
        .AsQueryable();

    if (!string.IsNullOrWhiteSpace(organizationKey))
    {
        var organizationId = await db.Organizations
            .AsNoTracking()
            .Where(x => x.Key == organizationKey)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(ct);
        if (organizationId is null)
        {
            return Results.Ok(Array.Empty<AccountDto>());
        }

        query = query.Where(x => x.OrganizationId == organizationId.Value);
    }

    if (!string.IsNullOrWhiteSpace(periodKey))
    {
        var reportingPeriodId = await db.ReportingPeriods
            .AsNoTracking()
            .Where(x => x.Key == periodKey)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(ct);
        if (reportingPeriodId is null)
        {
            return Results.Ok(Array.Empty<AccountDto>());
        }

        query = query.Where(x => x.ReportingPeriodId == reportingPeriodId.Value);
    }

    if (firstSeen is not null)
    {
        query = query.Where(x => x.IsFirstSeen == firstSeen);
    }

    if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<MappingReviewStatus>(status, true, out var reviewStatus))
    {
        query = query.Where(x => x.ReviewStatus == reviewStatus);
    }

    var accounts = await query
        .OrderByDescending(x => x.IsFirstSeen)
        .ThenBy(x => x.Code)
        .Select(x => AccountDto.From(x))
        .ToListAsync(ct);
    return Results.Ok(accounts);
});

app.MapGet("/api/mapping/fs-lines", async (
    string organizationKey,
    bool? includeInactive,
    AppDbContext db,
    CancellationToken ct) =>
{
    var organization = await db.Organizations.AsNoTracking().FirstOrDefaultAsync(x => x.Key == organizationKey, ct);
    if (organization is null)
    {
        return Results.NotFound();
    }

    await EnsureFsLineDefinitionsAsync(db, organization.Id, ct);
    var query = db.FsLineDefinitions
        .AsNoTracking()
        .Where(x => x.OrganizationId == organization.Id);

    if (includeInactive != true)
    {
        query = query.Where(x => x.IsActive);
    }

    var lines = await query
        .OrderBy(x => x.StatementType)
        .ThenBy(x => x.Section)
        .ThenBy(x => x.SortOrder)
        .ThenBy(x => x.Name)
        .Select(x => FsLineDefinitionDto.From(x))
        .ToListAsync(ct);
    return Results.Ok(lines);
});

app.MapPost("/api/mapping/fs-lines", async (
    UpsertFsLineDefinitionRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var organization = await db.Organizations.FirstOrDefaultAsync(x => x.Key == request.OrganizationKey, ct);
    if (organization is null)
    {
        return Results.NotFound();
    }

    if (string.IsNullOrWhiteSpace(request.Name))
    {
        return Results.BadRequest(new { error = "FS line name is required." });
    }

    var statementType = NormalizeStatementType(request.StatementType);
    var name = request.Name.Trim();
    var existing = await db.FsLineDefinitions.FirstOrDefaultAsync(x =>
        x.OrganizationId == organization.Id
        && x.StatementType == statementType
        && x.Name == name,
        ct);
    if (existing is not null)
    {
        if (!existing.IsActive)
        {
            var before = JsonSerializer.Serialize(existing);
            existing.Section = string.IsNullOrWhiteSpace(request.Section) ? InferFsLineSection(name, statementType) : request.Section.Trim();
            existing.NormalBalance = NormalizeNormalBalance(request.NormalBalance, statementType);
            existing.AiGuidance = request.AiGuidance?.Trim() ?? "";
            existing.SortOrder = request.SortOrder ?? existing.SortOrder;
            existing.IsActive = request.IsActive ?? true;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            await AuditAsync(db, http, "mapping.fs-line.reactivate", "FsLineDefinition", existing.Id, null, request.Reason ?? "Reactivated FS line", before, JsonSerializer.Serialize(existing), ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(FsLineDefinitionDto.From(existing));
        }

        return Results.Conflict(new { error = "That FS line already exists for this entity and statement type." });
    }

    var sortOrder = request.SortOrder ?? await NextFsLineSortOrderAsync(db, organization.Id, statementType, request.Section, ct);
    var line = new FsLineDefinition
    {
        Id = Guid.NewGuid(),
        OrganizationId = organization.Id,
        StatementType = statementType,
        Section = string.IsNullOrWhiteSpace(request.Section) ? InferFsLineSection(name, statementType) : request.Section.Trim(),
        Name = name,
        NormalBalance = NormalizeNormalBalance(request.NormalBalance, statementType),
        AiGuidance = request.AiGuidance?.Trim() ?? "",
        SortOrder = sortOrder,
        IsActive = request.IsActive ?? true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
    db.FsLineDefinitions.Add(line);
    await AuditAsync(db, http, "mapping.fs-line.create", "FsLineDefinition", line.Id, null, request.Reason ?? "Created FS line", "{}", JsonSerializer.Serialize(line), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(FsLineDefinitionDto.From(line));
});

app.MapPut("/api/mapping/fs-lines/{lineId:guid}", async (
    Guid lineId,
    UpsertFsLineDefinitionRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var line = await db.FsLineDefinitions.FirstOrDefaultAsync(x => x.Id == lineId, ct);
    if (line is null)
    {
        return Results.NotFound();
    }

    if (string.IsNullOrWhiteSpace(request.Name))
    {
        return Results.BadRequest(new { error = "FS line name is required." });
    }

    var before = JsonSerializer.Serialize(line);
    line.StatementType = NormalizeStatementType(request.StatementType);
    line.Section = string.IsNullOrWhiteSpace(request.Section) ? InferFsLineSection(request.Name, line.StatementType) : request.Section.Trim();
    line.Name = request.Name.Trim();
    line.NormalBalance = NormalizeNormalBalance(request.NormalBalance, line.StatementType);
    line.AiGuidance = request.AiGuidance?.Trim() ?? "";
    line.SortOrder = request.SortOrder ?? line.SortOrder;
    line.IsActive = request.IsActive ?? line.IsActive;
    line.UpdatedAt = DateTimeOffset.UtcNow;

    await AuditAsync(db, http, "mapping.fs-line.update", "FsLineDefinition", line.Id, null, request.Reason ?? "Updated FS line", before, JsonSerializer.Serialize(line), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(FsLineDefinitionDto.From(line));
});

app.MapDelete("/api/mapping/fs-lines/{lineId:guid}", async (
    Guid lineId,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var line = await db.FsLineDefinitions.FirstOrDefaultAsync(x => x.Id == lineId, ct);
    if (line is null)
    {
        return Results.NotFound();
    }

    var before = JsonSerializer.Serialize(line);
    line.IsActive = false;
    line.UpdatedAt = DateTimeOffset.UtcNow;
    await AuditAsync(db, http, "mapping.fs-line.deactivate", "FsLineDefinition", line.Id, null, "Deactivated FS line", before, JsonSerializer.Serialize(line), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(FsLineDefinitionDto.From(line));
});

app.MapGet("/api/mapping/accounts/{accountId:guid}", async (Guid accountId, AppDbContext db, CancellationToken ct) =>
{
    var account = await db.GlAccounts
        .AsNoTracking()
        .Include(x => x.Transactions.OrderByDescending(t => t.TransactionDate))
        .FirstOrDefaultAsync(x => x.Id == accountId, ct);
    return account is null ? Results.NotFound() : Results.Ok(AccountDetailDto.From(account));
});

app.MapPost("/api/mapping/accounts/{accountId:guid}/map", async (
    Guid accountId,
    MapAccountRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    if (string.IsNullOrWhiteSpace(request.Reason))
    {
        return Results.BadRequest(new { error = "Audit reason is required." });
    }

    var account = await db.GlAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
    if (account is null)
    {
        return Results.NotFound();
    }

    var fsLine = request.FsLine.Trim();
    if (!await FsLineDefinitionExistsAsync(db, account.OrganizationId, fsLine, ct))
    {
        return Results.BadRequest(new { error = "Create this FS line in the FS line library before mapping accounts to it." });
    }

    var before = JsonSerializer.Serialize(account);
    account.FsLine = fsLine;
    account.ReviewStatus = MappingReviewStatus.Reviewed;
    account.AuditReason = request.Reason;
    account.UpdatedAt = DateTimeOffset.UtcNow;
    db.AccountMappings.Add(new AccountMapping
    {
        Id = Guid.NewGuid(),
        OrganizationId = account.OrganizationId,
        ReportingPeriodId = account.ReportingPeriodId,
        FsLine = fsLine,
        AccountCodesCsv = account.Code,
        EntityKeysCsv = account.TenantId,
        Reason = request.Reason
    });
    await AuditAsync(db, http, "mapping.map", "GlAccount", account.Id, null, request.Reason, before, JsonSerializer.Serialize(account), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(AccountDto.From(account));
});

app.MapPost("/api/mapping/accounts/{accountId:guid}/eliminate", async (
    Guid accountId,
    EliminateAccountRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    if (string.IsNullOrWhiteSpace(request.Reason))
    {
        return Results.BadRequest(new { error = "Audit reason is required." });
    }

    var account = await db.GlAccounts.Include(x => x.Transactions).FirstOrDefaultAsync(x => x.Id == accountId, ct);
    if (account is null)
    {
        return Results.NotFound();
    }

    var before = JsonSerializer.Serialize(AccountDetailDto.From(account));
    account.ConsolidationTreatment = request.Type.Equals("exclude", StringComparison.OrdinalIgnoreCase)
        ? ConsolidationTreatment.Exclude
        : request.Type.Equals("intercompany", StringComparison.OrdinalIgnoreCase)
            ? ConsolidationTreatment.Intercompany
            : ConsolidationTreatment.Eliminate;
    account.ReviewStatus = MappingReviewStatus.Reviewed;
    account.AuditReason = request.Reason;
    account.UpdatedAt = DateTimeOffset.UtcNow;

    var amount = account.Transactions.Sum(x => x.Credit - x.Debit);
    db.EliminationEntries.Add(new EliminationEntry
    {
        Id = Guid.NewGuid(),
        OrganizationId = account.OrganizationId,
        ReportingPeriodId = account.ReportingPeriodId,
        GlAccountId = account.Id,
        Type = request.Type,
        Description = request.Description,
        Amount = amount,
        Reason = request.Reason,
        IsRecurringRule = request.CreateRecurringRule
    });
    if (request.CreateRecurringRule)
    {
        db.RecurringEliminationRules.Add(new RecurringEliminationRule
        {
            Id = Guid.NewGuid(),
            OrganizationId = account.OrganizationId,
            ReportingPeriodId = account.ReportingPeriodId,
            GlAccountId = account.Id,
            Type = request.Type,
            Description = request.Description,
            CriteriaJson = JsonSerializer.Serialize(new { account.Code, account.TenantId }),
            Reason = request.Reason
        });
    }
    await AuditAsync(db, http, "mapping.eliminate", "GlAccount", account.Id, null, request.Reason, before, JsonSerializer.Serialize(AccountDetailDto.From(account)), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(AccountDetailDto.From(account));
});

app.MapPost("/api/mapping/accounts/{accountId:guid}/split", async (
    Guid accountId,
    SplitMappingRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    if (string.IsNullOrWhiteSpace(request.Reason) || request.Lines.Length == 0 || request.Lines.Sum(x => x.Percent) != 100m)
    {
        return Results.BadRequest(new { error = "Split mappings require a reason and lines totaling 100%." });
    }

    var account = await db.GlAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
    if (account is null)
    {
        return Results.NotFound();
    }

    foreach (var line in request.Lines)
    {
        if (!await FsLineDefinitionExistsAsync(db, account.OrganizationId, line.FsLine.Trim(), ct))
        {
            return Results.BadRequest(new { error = $"Create FS line '{line.FsLine}' in the FS line library before using it in a split." });
        }
    }

    var before = JsonSerializer.Serialize(account);
    account.FsLine = string.Join(" / ", request.Lines.Select(x => $"{x.FsLine.Trim()} {x.Percent:0.#}%"));
    account.ReviewStatus = MappingReviewStatus.Reviewed;
    account.AuditReason = request.Reason;
    account.UpdatedAt = DateTimeOffset.UtcNow;
    db.AccountMappings.Add(new AccountMapping
    {
        Id = Guid.NewGuid(),
        OrganizationId = account.OrganizationId,
        ReportingPeriodId = account.ReportingPeriodId,
        FsLine = "Split mapping",
        AccountCodesCsv = account.Code,
        EntityKeysCsv = account.TenantId,
        Reason = JsonSerializer.Serialize(new { request.Reason, request.Lines })
    });
    await AuditAsync(db, http, "mapping.split", "GlAccount", account.Id, null, request.Reason, before, JsonSerializer.Serialize(account), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(AccountDto.From(account));
});

app.MapPost("/api/mapping/accounts/{accountId:guid}/reject", async (
    Guid accountId,
    MappingReasonRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    if (string.IsNullOrWhiteSpace(request.Reason))
    {
        return Results.BadRequest(new { error = "Audit reason is required." });
    }

    var account = await db.GlAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
    if (account is null)
    {
        return Results.NotFound();
    }

    var before = JsonSerializer.Serialize(account);
    account.ReviewStatus = MappingReviewStatus.Rejected;
    account.AuditReason = request.Reason;
    account.UpdatedAt = DateTimeOffset.UtcNow;
    await AuditAsync(db, http, "mapping.reject", "GlAccount", account.Id, null, request.Reason, before, JsonSerializer.Serialize(account), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(AccountDto.From(account));
});

app.MapPost("/api/mapping/accounts/{accountId:guid}/mark-reviewed", async (
    Guid accountId,
    MappingReasonRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    if (string.IsNullOrWhiteSpace(request.Reason))
    {
        return Results.BadRequest(new { error = "Audit reason is required." });
    }

    var account = await db.GlAccounts.FirstOrDefaultAsync(x => x.Id == accountId, ct);
    if (account is null)
    {
        return Results.NotFound();
    }

    var before = JsonSerializer.Serialize(account);
    account.ReviewStatus = MappingReviewStatus.Reviewed;
    account.AuditReason = request.Reason;
    account.UpdatedAt = DateTimeOffset.UtcNow;
    await AuditAsync(db, http, "mapping.review", "GlAccount", account.Id, null, request.Reason, before, JsonSerializer.Serialize(account), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(AccountDto.From(account));
});

app.MapGet("/api/mapping/recurring-eliminations", async (AppDbContext db, CancellationToken ct) =>
{
    var rules = await db.RecurringEliminationRules.AsNoTracking().ToListAsync(ct);
    return Results.Ok(rules.OrderByDescending(x => x.CreatedAt));
});

app.MapPost("/api/mapping/recurring-eliminations", async (
    RecurringEliminationRuleRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    if (string.IsNullOrWhiteSpace(request.Reason))
    {
        return Results.BadRequest(new { error = "Audit reason is required." });
    }

    var rule = new RecurringEliminationRule
    {
        Id = Guid.NewGuid(),
        OrganizationId = request.OrganizationId,
        ReportingPeriodId = request.ReportingPeriodId,
        GlAccountId = request.GlAccountId,
        Type = request.Type,
        Description = request.Description,
        CriteriaJson = request.CriteriaJson,
        Reason = request.Reason,
        IsActive = request.IsActive
    };
    db.RecurringEliminationRules.Add(rule);
    await AuditAsync(db, http, "elimination-rule.create", "RecurringEliminationRule", rule.Id, null, request.Reason, "{}", JsonSerializer.Serialize(rule), ct);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/mapping/recurring-eliminations/{rule.Id}", rule);
});

app.MapGet("/api/xero/status", async (AppDbContext db, XeroIntegrationService xero, XeroTenantLedgerService ledger, CancellationToken ct) =>
{
    var connections = await db.XeroConnections.AsNoTracking().OrderBy(x => x.TenantName).ToListAsync(ct);
    var tenants = await db.XeroTenantConnections.AsNoTracking().OrderBy(x => x.TenantName).ToListAsync(ct);
    var mappings = await db.XeroTenantEntityMappings.AsNoTracking().ToListAsync(ct);
    var runs = (await db.XeroSyncRuns.AsNoTracking().ToListAsync(ct))
        .OrderByDescending(x => x.StartedAt)
        .Take(50)
        .ToList();
    var ledgerStatus = await ledger.GetSyncStatusAsync(db, ct);
    return Results.Ok(xero.GetStatus(connections, runs, tenants, mappings, ledgerStatus));
});

app.MapGet("/api/xero/connections", async (AppDbContext db, CancellationToken ct) =>
{
    var connections = await db.XeroConnections.AsNoTracking().OrderBy(x => x.TenantName).ToListAsync(ct);
    return Results.Ok(connections.Select(XeroConnectionDto.From));
});

app.MapGet("/api/xero/tenants", async (AppDbContext db, CancellationToken ct) =>
{
    var tenants = await db.XeroTenantConnections.AsNoTracking().OrderBy(x => x.TenantName).ToListAsync(ct);
    var mappings = await db.XeroTenantEntityMappings.AsNoTracking().ToDictionaryAsync(x => x.TenantId, ct);
    return Results.Ok(tenants.Select(t =>
    {
        mappings.TryGetValue(t.TenantId, out var mapping);
        return new
        {
            t.Id,
            t.TenantId,
            t.TenantName,
            t.TenantType,
            t.ConnectionStatus,
            t.RequiresReconnectForLedger,
            t.TokenExpiresAt,
            t.LastConnectedAt,
            t.LastError,
            t.Source,
            mappedOrganizationId = mapping?.OrganizationId,
            isIgnored = mapping?.IsIgnored ?? false
        };
    }));
});

app.MapPut("/api/xero/tenants/{tenantId}/entity-map", async (
    string tenantId,
    TenantEntityMapRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin"))
    {
        return Results.Forbid();
    }

    var tenant = await db.XeroTenantConnections.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
    if (tenant is null)
    {
        return Results.NotFound();
    }

    var org = await db.Organizations.FirstOrDefaultAsync(x => x.Id == request.OrganizationId, ct);
    if (org is null)
    {
        return Results.BadRequest(new { message = "Mapped organization was not found." });
    }

    var mapping = await db.XeroTenantEntityMappings.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct)
                  ?? new XeroTenantEntityMapping { Id = Guid.NewGuid(), TenantId = tenantId, CreatedAt = DateTimeOffset.UtcNow };
    var before = JsonSerializer.Serialize(mapping);
    mapping.OrganizationId = request.OrganizationId;
    mapping.IsIgnored = request.IsIgnored;
    mapping.Reason = request.Reason ?? "Updated tenant/entity mapping";
    mapping.UpdatedAt = DateTimeOffset.UtcNow;
    if (db.Entry(mapping).State == EntityState.Detached)
    {
        db.XeroTenantEntityMappings.Add(mapping);
    }

    await AuditAsync(db, http, "xero.tenant-map", "XeroTenantEntityMapping", mapping.Id, null, mapping.Reason, before, JsonSerializer.Serialize(mapping), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(mapping);
});

app.MapGet("/api/xero/connect", async (
    Guid? organizationId,
    HttpContext http,
    AppDbContext db,
    XeroIntegrationService xero,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin"))
    {
        return Results.Forbid();
    }

    var orgId = organizationId ?? await db.Organizations.OrderBy(x => x.Name).Select(x => x.Id).FirstAsync(ct);
    var response = await xero.BuildConnectUrlAsync(db, orgId, ct);
    return response.Error is null ? Results.Ok(response) : Results.BadRequest(response);
});

app.MapPost("/api/xero/connections/{connectionId:guid}/reconnect", async (
    Guid connectionId,
    HttpContext http,
    AppDbContext db,
    XeroIntegrationService xero,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin"))
    {
        return Results.Forbid();
    }

    var connection = await db.XeroConnections.FirstOrDefaultAsync(x => x.Id == connectionId, ct);
    if (connection is null)
    {
        return Results.NotFound();
    }

    var response = await xero.BuildConnectUrlAsync(db, connection.OrganizationId, ct);
    return response.Error is null ? Results.Ok(response) : Results.BadRequest(response);
});

app.MapPost("/api/xero/tenants/{tenantId}/reconnect", async (
    string tenantId,
    HttpContext http,
    AppDbContext db,
    XeroIntegrationService xero,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin"))
    {
        return Results.Forbid();
    }

    var mapping = await db.XeroTenantEntityMappings.FirstOrDefaultAsync(x => x.TenantId == tenantId, ct);
    var orgId = mapping?.OrganizationId ?? await db.Organizations.OrderBy(x => x.Name).Select(x => x.Id).FirstAsync(ct);
    var response = await xero.BuildConnectUrlAsync(db, orgId, ct);
    return response.Error is null ? Results.Ok(response) : Results.BadRequest(response);
});

app.MapGet("/api/xero/callback", async (
    string? code,
    string? state,
    string? error,
    string? error_description,
    AppDbContext db,
    XeroIntegrationService xero,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (!string.IsNullOrWhiteSpace(error))
    {
        var message = string.IsNullOrWhiteSpace(error_description) ? error : error_description;
        return Results.Content(XeroCallbackHtml(false, "Xero authorization failed", message), "text/html");
    }

    if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
    {
        return Results.Content(XeroCallbackHtml(false, "Xero authorization failed", "Missing Xero code or state. Start the connection again from Xero Settings."), "text/html");
    }

    try
    {
        var connection = await xero.CompleteCallbackAsync(db, code, state, ct);
        return Results.Content(
            XeroCallbackHtml(true, "Xero connected", $"{connection.TenantName} is connected. You can return to Financial Reporting Software."),
            "text/html");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Xero callback failed.");
        return Results.Content(
            XeroCallbackHtml(false, "Xero authorization failed", SafeXeroCallbackMessage(ex)),
            "text/html");
    }
});

app.MapPost("/api/xero/sync", async (
    XeroSyncRequest request,
    HttpContext http,
    AppDbContext db,
    XeroIntegrationService xero,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var packageId = request.ReportPackageId ?? await db.ReportPackages.OrderBy(x => x.Id).Select(x => x.Id).FirstAsync(ct);
    var run = await xero.SyncPackageAsync(db, packageId, ct);
    await AuditAsync(db, http, "xero.sync", "XeroSyncRun", run.Id, packageId, "Manual Xero sync", "{}", JsonSerializer.Serialize(run), ct);
    await db.SaveChangesAsync(ct);
    return Results.Accepted($"/api/xero/sync/{run.Id}", run);
});

app.MapPost("/api/xero/sync-period", async (
    XeroSyncPeriodRequest request,
    HttpContext http,
    AppDbContext db,
    XeroIntegrationService xero,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var options = new XeroPeriodSyncOptions(
        string.IsNullOrWhiteSpace(request.PeriodKey) ? "2026-01" : request.PeriodKey,
        string.IsNullOrWhiteSpace(request.Basis) ? "accrual" : request.Basis,
        request.IncludeAllTenants,
        request.CreateConsolidation);
    var result = await xero.SyncPeriodAsync(db, options, ct);
    await AuditAsync(db, http, "xero.sync-period", "ReportingPeriod", result.ReportingPeriodId, null, $"Synced {result.PeriodKey} Xero financials", "{}", JsonSerializer.Serialize(result), ct);
    await db.SaveChangesAsync(ct);
    return Results.Accepted("/api/xero/sync-period", result);
});

app.MapGet("/api/xero/sync/{syncRunId:guid}", async (Guid syncRunId, AppDbContext db, CancellationToken ct) =>
{
    var run = await db.XeroSyncRuns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == syncRunId, ct);
    return run is null ? Results.NotFound() : Results.Ok(run);
});

app.MapPost("/api/xero/import-v2-tokens/preview", async (
    HttpContext http,
    AppDbContext db,
    XeroTenantLedgerService ledger,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin"))
    {
        return Results.Forbid();
    }

    var result = await ledger.PreviewFinanceAppV2ImportAsync(ct);
    await AuditAsync(db, http, "xero.import-v2-preview", "XeroTenantConnection", null, null, "Previewed Finance App V2 token import", "{}", JsonSerializer.Serialize(result), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(result);
});

app.MapPost("/api/xero/import-v2-tokens", async (
    HttpContext http,
    AppDbContext db,
    XeroTenantLedgerService ledger,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin"))
    {
        return Results.Forbid();
    }

    var result = await ledger.ImportFinanceAppV2TokensAsync(db, ct);
    await AuditAsync(db, http, "xero.import-v2-tokens", "XeroTenantConnection", null, null, "Imported Finance App V2 Xero tenants", "{}", JsonSerializer.Serialize(result), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(result);
});

app.MapGet("/api/xero/ledger-sync-settings", async (AppDbContext db, XeroTenantLedgerService ledger, CancellationToken ct) =>
{
    var settings = await ledger.GetSettingsAsync(db, ct);
    return Results.Ok(settings);
});

app.MapPut("/api/xero/ledger-sync-settings", async (
    XeroLedgerSyncSettingsRequest request,
    HttpContext http,
    AppDbContext db,
    XeroTenantLedgerService ledger,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin"))
    {
        return Results.Forbid();
    }

    var settings = await ledger.UpdateSettingsAsync(db, request, ct);
    await AuditAsync(db, http, "xero.ledger-settings", "XeroLedgerSyncSetting", settings.Id, null, "Updated ledger sync settings", "{}", JsonSerializer.Serialize(settings), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(settings);
});

app.MapPost("/api/xero/ledger-sync/run", async (
    XeroLedgerRunRequest request,
    HttpContext http,
    AppDbContext db,
    XeroTenantLedgerService ledger,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var result = await ledger.RunIncrementalLedgerSyncAsync(db, request.TenantId, request.Force, ct);
    await AuditAsync(db, http, "xero.ledger-sync", "XeroLedgerSyncCursor", null, null, "Manual Xero ledger sync", "{}", JsonSerializer.Serialize(result), ct);
    await db.SaveChangesAsync(ct);
    return Results.Accepted("/api/xero/ledger-sync/status", result);
});

app.MapGet("/api/xero/ledger-sync/status", async (AppDbContext db, XeroTenantLedgerService ledger, CancellationToken ct) =>
{
    var result = await ledger.GetSyncStatusAsync(db, ct);
    return Results.Ok(result);
});

app.MapPost("/api/xero/backfill/preview", async (
    XeroBackfillRequest request,
    HttpContext http,
    AppDbContext db,
    XeroBackfillService backfill,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var result = await backfill.PreviewAsync(db, request, ct);
    await AuditAsync(db, http, "xero.backfill-preview", "XeroBackfillRun", null, null, "Previewed historical Xero backfill", "{}", JsonSerializer.Serialize(result), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(result);
});

app.MapPost("/api/xero/backfill", async (
    XeroBackfillRequest request,
    HttpContext http,
    AppDbContext db,
    XeroBackfillService backfill,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var result = await backfill.QueueAsync(db, request, ct);
    await AuditAsync(db, http, "xero.backfill-queue", "XeroBackfillRun", result.Id, null, "Queued historical Xero backfill", "{}", JsonSerializer.Serialize(result), ct);
    await db.SaveChangesAsync(ct);
    return Results.Accepted($"/api/xero/backfill/{result.Id}", result);
});

app.MapGet("/api/xero/backfill/{runId:guid}", async (Guid runId, AppDbContext db, XeroBackfillService backfill, CancellationToken ct) =>
{
    var result = await backfill.GetRunAsync(db, runId, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/xero/backfill/{runId:guid}/pause", async (Guid runId, HttpContext http, AppDbContext db, XeroBackfillService backfill, CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var result = await backfill.SetStatusAsync(db, runId, "Paused", ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/xero/backfill/{runId:guid}/resume", async (Guid runId, HttpContext http, AppDbContext db, XeroBackfillService backfill, CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var result = await backfill.SetStatusAsync(db, runId, "Queued", ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/xero/backfill/{runId:guid}/cancel", async (Guid runId, HttpContext http, AppDbContext db, XeroBackfillService backfill, CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var result = await backfill.SetStatusAsync(db, runId, "Cancelled", ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapGet("/api/xero/data-coverage", async (string? from, string? to, AppDbContext db, XeroBackfillService backfill, CancellationToken ct) =>
{
    var result = await backfill.BuildCoverageAsync(db, from, to, ct);
    return Results.Ok(result);
});

app.MapGet("/api/xero/tenants/{tenantId}/ledger-reconciliations", async (string tenantId, AppDbContext db, XeroTenantLedgerService ledger, CancellationToken ct) =>
{
    var result = await ledger.GetReconciliationsAsync(db, tenantId, ct);
    return Results.Ok(result);
});

app.MapPost("/api/xero/tenants/{tenantId}/ledger-reconciliations/run", async (
    string tenantId,
    XeroReconciliationRunRequest request,
    HttpContext http,
    AppDbContext db,
    XeroTenantLedgerService ledger,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var date = request.SnapshotDate ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);
    var result = await ledger.RunTrialBalanceReconciliationAsync(db, tenantId, date, ct);
    await AuditAsync(db, http, "xero.ledger-reconcile", "XeroLedgerReconciliationRun", result.Id, null, "Ran Trial Balance reconciliation", "{}", JsonSerializer.Serialize(result), ct);
    await db.SaveChangesAsync(ct);
    return Results.Accepted($"/api/xero/tenants/{tenantId}/ledger-reconciliations", result);
});

app.MapPost("/api/xero/test", async (AppDbContext db, XeroIntegrationService xero, XeroTenantLedgerService ledger, CancellationToken ct) =>
{
    var connections = await db.XeroConnections.AsNoTracking().ToListAsync(ct);
    var tenants = await db.XeroTenantConnections.AsNoTracking().ToListAsync(ct);
    var mappings = await db.XeroTenantEntityMappings.AsNoTracking().ToListAsync(ct);
    var runs = (await db.XeroSyncRuns.AsNoTracking().ToListAsync(ct))
        .OrderByDescending(x => x.StartedAt)
        .Take(50)
        .ToList();
    var ledgerStatus = await ledger.GetSyncStatusAsync(db, ct);
    return Results.Ok(xero.GetStatus(connections, runs, tenants, mappings, ledgerStatus));
});

app.MapGet("/api/packages/{packageId:guid}/flux-review", async (
    Guid packageId,
    FluxReviewService flux,
    CancellationToken ct) =>
{
    var result = await flux.GetOrBuildAsync(packageId, ct);
    return Results.Ok(result);
});

app.MapPost("/api/packages/{packageId:guid}/refresh-flux", async (
    Guid packageId,
    HttpContext http,
    AppDbContext db,
    FluxReviewService flux,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    var result = await flux.RefreshAsync(packageId, ct);
    await AuditAsync(db, http, "flux.refresh", "ReportPackage", packageId, packageId, "Refreshed flux review from current ledger/statement data", "{}", JsonSerializer.Serialize(result), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(result);
});

app.MapPost("/api/packages/{packageId:guid}/pull-ledger-detail", async (
    Guid packageId,
    HttpContext http,
    AppDbContext db,
    FluxReviewService flux,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    var result = await flux.PullLedgerDetailAsync(packageId, ct);
    await AuditAsync(db, http, "flux.ledger-detail.pull", "ReportPackage", packageId, packageId, "Pulled on-demand Xero ledger detail for active entity-period flux review", "{}", JsonSerializer.Serialize(result), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(result);
});

app.MapGet("/api/flux-review/groups/{groupId:guid}/drilldown", async (
    Guid groupId,
    FluxReviewService flux,
    CancellationToken ct) =>
{
    var result = await flux.GetDrilldownAsync(groupId, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapGet("/api/packages/{packageId:guid}/flux-review/export.csv", async (
    Guid packageId,
    FluxReviewService flux,
    CancellationToken ct) =>
{
    var csv = await flux.ExportCsvAsync(packageId, ct);
    return Results.Text(csv, "text/csv");
});

app.MapPut("/api/flux-review/groups/{groupId:guid}/settings", async (
    Guid groupId,
    FluxReviewGroupSettingsRequest request,
    HttpContext http,
    AppDbContext db,
    FluxReviewService flux,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    var before = await db.FluxReviewGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == groupId, ct);
    var result = await flux.UpdateSettingsAsync(groupId, request, ct);
    await AuditAsync(db, http, "flux.settings.update", "FluxReviewGroup", groupId, result.ReportPackageId, request.Reason ?? "Updated flux thresholds and workflow settings", JsonSerializer.Serialize(before), JsonSerializer.Serialize(result), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(FluxReviewGroupDto.From(result));
});

app.MapPost("/api/flux-review/groups/{groupId:guid}/sign-off", async (
    Guid groupId,
    FluxSignOffRequest request,
    HttpContext http,
    AppDbContext db,
    FluxReviewService flux,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    var before = await db.FluxReviewGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == groupId, ct);
    var result = await flux.SignOffAsync(groupId, request.Action ?? "prepare", Actor(http), ct);
    await AuditAsync(db, http, "flux.signoff", "FluxReviewGroup", groupId, result.ReportPackageId, request.Reason ?? "Flux sign-off", JsonSerializer.Serialize(before), JsonSerializer.Serialize(result), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(FluxReviewGroupDto.From(result));
});

app.MapPost("/api/flux-review/groups/{groupId:guid}/roll-forward-explanation", async (
    Guid groupId,
    HttpContext http,
    AppDbContext db,
    FluxReviewService flux,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    var before = await db.FluxReviewGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == groupId, ct);
    var result = await flux.RollForwardExplanationAsync(groupId, Actor(http), ct);
    await AuditAsync(db, http, "flux.roll-forward", "FluxReviewGroup", groupId, result.ReportPackageId, "Rolled forward prior flux explanation", JsonSerializer.Serialize(before), JsonSerializer.Serialize(result), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(FluxReviewGroupDto.From(result));
});

app.MapPut("/api/flux-review/groups/{groupId:guid}/explanation", async (
    Guid groupId,
    FluxExplanationRequest request,
    HttpContext http,
    AppDbContext db,
    FluxReviewService flux,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    if (string.IsNullOrWhiteSpace(request.Explanation))
    {
        return Results.BadRequest(new { message = "Explanation is required." });
    }

    var before = await db.FluxReviewGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == groupId, ct);
    var result = await flux.UpdateExplanationAsync(groupId, request.Explanation, Actor(http), ct);
    await AuditAsync(db, http, "flux.explain", "FluxReviewGroup", groupId, result.ReportPackageId, request.Reason ?? "Updated flux explanation", JsonSerializer.Serialize(before), JsonSerializer.Serialize(result), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(FluxReviewGroupDto.From(result));
});

app.MapPost("/api/flux-review/groups/{groupId:guid}/approve", async (
    Guid groupId,
    HttpContext http,
    AppDbContext db,
    FluxReviewService flux,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    var before = await db.FluxReviewGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == groupId, ct);
    var result = await flux.ApproveAsync(groupId, Actor(http), ct);
    await AuditAsync(db, http, "flux.approve", "FluxReviewGroup", groupId, result.ReportPackageId, "Approved flux review group", JsonSerializer.Serialize(before), JsonSerializer.Serialize(result), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(FluxReviewGroupDto.From(result));
});

app.MapPost("/api/flux-review/groups/{groupId:guid}/ai-explain", async (
    Guid groupId,
    HttpContext http,
    AppDbContext db,
    FluxReviewService flux,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    var group = await db.FluxReviewGroups.FirstOrDefaultAsync(x => x.Id == groupId, ct);
    if (group is null)
    {
        return Results.NotFound();
    }

    var setting = await db.AiRuntimeSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Module == "flux-explain", ct);
    var snapshot = await flux.BuildAiExplanationSnapshotAsync(groupId, ct);
    var run = new AiRun
    {
        Id = Guid.NewGuid(),
        ReportPackageId = group.ReportPackageId,
        Module = "flux-explain",
        PromptProfile = setting?.Profile ?? "variance-review",
        Model = setting?.Model ?? "gpt-5.5",
        ReasoningEffort = setting?.ReasoningEffort ?? "high",
        Status = AiRunStatus.Queued,
        InputJson = snapshot
    };
    db.AiRuns.Add(run);
    await AuditAsync(db, http, "flux.ai-explain", "AiRun", run.Id, group.ReportPackageId, "Queued AI flux explanation", "{}", snapshot, ct);
    await db.SaveChangesAsync(ct);
    return Results.Accepted($"/api/ai/runs/{run.Id}", AiRunDto.From(run));
});

app.MapGet("/api/packages/{packageId:guid}/ai-package-drafts", async (
    Guid packageId,
    AiPackageDraftService drafts,
    CancellationToken ct) =>
{
    var result = await drafts.GetDraftsAsync(packageId, ct);
    return Results.Ok(result);
});

app.MapPost("/api/packages/{packageId:guid}/ai-package-draft", async (
    Guid packageId,
    HttpContext http,
    AppDbContext db,
    AiPackageDraftService drafts,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var result = await drafts.CreateDraftsAsync(packageId, ct);
    await AuditAsync(db, http, "ai-package-draft.create", "ReportPackage", packageId, packageId, "Created staged AI package draft suggestions", "{}", JsonSerializer.Serialize(result), ct);
    await db.SaveChangesAsync(ct);
    return Results.Accepted($"/api/packages/{packageId}/ai-package-drafts", result);
});

app.MapPost("/api/ai-package-drafts/{draftId:guid}/accept", async (
    Guid draftId,
    HttpContext http,
    AppDbContext db,
    AiPackageDraftService drafts,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var before = await db.AiPackageDraftSuggestions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == draftId, ct);
    var result = await drafts.AcceptAsync(draftId, ct);
    await AuditAsync(db, http, "ai-package-draft.accept", "AiPackageDraftSuggestion", draftId, result.ReportPackageId, "Accepted staged AI package suggestion", JsonSerializer.Serialize(before), JsonSerializer.Serialize(result), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(result);
});

app.MapPost("/api/ai-package-drafts/{draftId:guid}/reject", async (
    Guid draftId,
    RejectDraftRequest request,
    HttpContext http,
    AppDbContext db,
    AiPackageDraftService drafts,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    var before = await db.AiPackageDraftSuggestions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == draftId, ct);
    var result = await drafts.RejectAsync(draftId, request.Reason, ct);
    await AuditAsync(db, http, "ai-package-draft.reject", "AiPackageDraftSuggestion", draftId, result.ReportPackageId, request.Reason ?? "Rejected staged AI package suggestion", JsonSerializer.Serialize(before), JsonSerializer.Serialize(result), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(result);
});

app.MapPost("/api/exports/pdf", async (
    ExportRequest request,
    HttpContext http,
    AppDbContext db,
    ExportService exports,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    var artifact = await exports.CreatePdfAsync(request.ReportPackageId, request.IncludeIssues, request.IncludeAppendix, ct);
    db.ExportArtifacts.Add(artifact);
    await AuditAsync(db, http, "export.pdf", "ExportArtifact", artifact.Id, request.ReportPackageId, "Generated PDF export", "{}", JsonSerializer.Serialize(artifact), ct);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/exports/{artifact.Id}", ExportArtifactDto.From(artifact));
});

app.MapPost("/api/exports/excel", async (
    ExportRequest request,
    HttpContext http,
    AppDbContext db,
    ExportService exports,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
    {
        return Results.Forbid();
    }

    var artifact = await exports.CreateExcelAsync(request.ReportPackageId, request.IncludeIssues, request.IncludeAppendix, ct);
    db.ExportArtifacts.Add(artifact);
    await AuditAsync(db, http, "export.excel", "ExportArtifact", artifact.Id, request.ReportPackageId, "Generated Excel export", "{}", JsonSerializer.Serialize(artifact), ct);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/exports/{artifact.Id}", ExportArtifactDto.From(artifact));
});

app.MapGet("/api/exports/{exportId:guid}", async (Guid exportId, AppDbContext db, CancellationToken ct) =>
{
    var artifact = await db.ExportArtifacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == exportId, ct);
    return artifact is null ? Results.NotFound() : Results.Ok(ExportArtifactDto.From(artifact));
});

app.MapGet("/api/exports/{exportId:guid}/download", async (Guid exportId, AppDbContext db, CancellationToken ct) =>
{
    var artifact = await db.ExportArtifacts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == exportId, ct);
    if (artifact is null || !System.IO.File.Exists(artifact.StoragePath))
    {
        return Results.NotFound();
    }

    return Results.File(artifact.StoragePath, artifact.ContentType, artifact.FileName);
});

app.MapPost("/api/exports/{exportId:guid}/qa", async (
    Guid exportId,
    CreateExportQaRequest request,
    AppDbContext db,
    ExportService exports,
    CancellationToken ct) =>
{
    var artifact = await db.ExportArtifacts.FirstOrDefaultAsync(x => x.Id == exportId, ct);
    if (artifact is null)
    {
        return Results.NotFound();
    }

    var qa = await exports.BuildExportQaAsync(request.ReportPackageId ?? artifact.ReportPackageId, exportId, ct);
    artifact.MetadataJson = JsonSerializer.Serialize(new { artifact.MetadataJson, qa });
    await db.SaveChangesAsync(ct);
    return Results.Ok(JsonNode.Parse(qa));
});

app.MapPost("/api/share-links", async (
    CreateShareLinkRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var link = new ShareLink
    {
        Id = Guid.NewGuid(),
        ReportPackageId = request.ReportPackageId,
        Token = Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant(),
        RequirePassword = request.RequirePassword,
        PasswordHash = string.IsNullOrWhiteSpace(request.Password) ? null : HashSecret(request.Password),
        AllowDownload = request.AllowDownload,
        DashboardOnly = request.DashboardOnly,
        ExpiresAt = request.ExpiresAt
    };
    db.ShareLinks.Add(link);
    await AuditAsync(db, http, "share.create", "ShareLink", link.Id, request.ReportPackageId, "Created share link", "{}", JsonSerializer.Serialize(ShareLinkDto.From(link)), ct);
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
    if (!Can(http, "Admin", "Finance Editor"))
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
    if (!string.IsNullOrWhiteSpace(request.Password)) link.PasswordHash = HashSecret(request.Password);
    link.UpdatedAt = DateTimeOffset.UtcNow;
    await AuditAsync(db, http, "share.update", "ShareLink", link.Id, link.ReportPackageId, "Updated share link", before, JsonSerializer.Serialize(link), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(ShareLinkDto.From(link));
});

app.MapDelete("/api/share-links/{shareLinkId:guid}", async (
    Guid shareLinkId,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
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
    await AuditAsync(db, http, "share.delete", "ShareLink", shareLinkId, link.ReportPackageId, "Deleted share link", before, "{}", ct);
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
});

app.MapPost("/api/distribution-schedules", async (
    CreateDistributionScheduleRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
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
    await AuditAsync(db, http, "distribution.create", "DistributionSchedule", schedule.Id, request.ReportPackageId, "Created distribution schedule", "{}", JsonSerializer.Serialize(schedule), ct);
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
    if (!Can(http, "Admin", "Finance Editor"))
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
    await AuditAsync(db, http, "distribution.update", "DistributionSchedule", schedule.Id, schedule.ReportPackageId, "Updated distribution schedule", before, JsonSerializer.Serialize(schedule), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(schedule);
});

app.MapPost("/api/distribution-schedules/{scheduleId:guid}/send-test", async (
    Guid scheduleId,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var schedule = await db.DistributionSchedules.FirstOrDefaultAsync(x => x.Id == scheduleId, ct);
    if (schedule is null)
    {
        return Results.NotFound();
    }

    var before = JsonSerializer.Serialize(schedule);
    schedule.LastTestSentAt = DateTimeOffset.UtcNow;
    schedule.Status = "TestSent";
    schedule.UpdatedAt = DateTimeOffset.UtcNow;
    await AuditAsync(db, http, "distribution.send-test", "DistributionSchedule", schedule.Id, schedule.ReportPackageId, "Manual test package send", before, JsonSerializer.Serialize(schedule), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { schedule.Id, schedule.LastTestSentAt, mode = "configured-email-adapter" });
});

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
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
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
        Author = string.IsNullOrWhiteSpace(request.Author) ? Actor(http) : request.Author.Trim(),
        ResolvedAt = string.Equals(request.Status, "Resolved", StringComparison.OrdinalIgnoreCase) ? DateTimeOffset.UtcNow : null
    };
    db.PackageComments.Add(comment);
    await AuditAsync(db, http, "comment.create", "PackageComment", comment.Id, packageId, "Created package comment", "{}", JsonSerializer.Serialize(comment), ct);
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
    if (!Can(http, "Admin", "Finance Editor", "Reviewer"))
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
    await AuditAsync(db, http, "comment.update", "PackageComment", comment.Id, comment.ReportPackageId, "Updated package comment", before, JsonSerializer.Serialize(comment), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(PackageCommentDto.From(comment));
});

app.MapGet("/api/kpis", async (Guid? organizationId, AppDbContext db, CancellationToken ct) =>
{
    var query = db.KpiDefinitions.AsNoTracking().AsQueryable();
    if (organizationId is not null)
    {
        query = query.Where(x => x.OrganizationId == organizationId);
    }

    var kpis = await query.OrderByDescending(x => x.IsPinned).ThenBy(x => x.Name).ToListAsync(ct);
    return Results.Ok(kpis.Select(KpiDto.From));
});

app.MapPost("/api/kpis", async (
    UpsertKpiRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var kpi = new KpiDefinition
    {
        Id = Guid.NewGuid(),
        OrganizationId = request.OrganizationId,
        Name = request.Name,
        Category = request.Category,
        Formula = request.Formula,
        Unit = request.Unit,
        CurrentValue = request.CurrentValue,
        TargetValue = request.TargetValue,
        IsPinned = request.IsPinned,
        Status = FinancialMath.KpiStatus(request.CurrentValue, request.TargetValue, request.HigherIsBetter)
    };
    db.KpiDefinitions.Add(kpi);
    await AuditAsync(db, http, "kpi.create", "KpiDefinition", kpi.Id, null, "Created KPI", "{}", JsonSerializer.Serialize(kpi), ct);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/kpis/{kpi.Id}", KpiDto.From(kpi));
});

app.MapPut("/api/kpis/{kpiId:guid}", async (
    Guid kpiId,
    UpsertKpiRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var kpi = await db.KpiDefinitions.FirstOrDefaultAsync(x => x.Id == kpiId, ct);
    if (kpi is null)
    {
        return Results.NotFound();
    }

    var before = JsonSerializer.Serialize(kpi);
    kpi.Name = request.Name;
    kpi.Category = request.Category;
    kpi.Formula = request.Formula;
    kpi.Unit = request.Unit;
    kpi.CurrentValue = request.CurrentValue;
    kpi.TargetValue = request.TargetValue;
    kpi.IsPinned = request.IsPinned;
    kpi.Status = FinancialMath.KpiStatus(request.CurrentValue, request.TargetValue, request.HigherIsBetter);
    await AuditAsync(db, http, "kpi.update", "KpiDefinition", kpi.Id, null, "Updated KPI", before, JsonSerializer.Serialize(kpi), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(KpiDto.From(kpi));
});

app.MapPost("/api/formulas/evaluate", async (
    EvaluateFormulaRequest request,
    AppDbContext db,
    CancellationToken ct) =>
{
    try
    {
        var result = await EvaluateFormulaAsync(db, request.OrganizationId, request.ReportingPeriodId, request.Formula, ct);
        return Results.Ok(result);
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/kpi-alerts", async (Guid? organizationId, AppDbContext db, CancellationToken ct) =>
{
    var query = db.KpiAlerts
        .AsNoTracking()
        .Include(x => x.KpiDefinition)
        .AsQueryable();

    if (organizationId is not null)
    {
        query = query.Where(x => x.KpiDefinition != null && x.KpiDefinition.OrganizationId == organizationId);
    }

    var alerts = await query.OrderByDescending(x => x.IsActive).ThenBy(x => x.KpiDefinition!.Name).ToListAsync(ct);
    return Results.Ok(alerts.Select(KpiAlertDto.From));
});

app.MapPost("/api/kpi-alerts", async (
    UpsertKpiAlertRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var kpi = await db.KpiDefinitions.FirstOrDefaultAsync(x => x.Id == request.KpiDefinitionId, ct);
    if (kpi is null)
    {
        return Results.NotFound();
    }

    var alert = new KpiAlert
    {
        Id = Guid.NewGuid(),
        KpiDefinitionId = kpi.Id,
        Direction = NormalizeAlertDirection(request.Direction),
        ThresholdValue = request.ThresholdValue,
        Severity = string.IsNullOrWhiteSpace(request.Severity) ? "Medium" : request.Severity,
        Message = string.IsNullOrWhiteSpace(request.Message) ? $"{kpi.Name} crossed {request.ThresholdValue:n1}{kpi.Unit}." : request.Message,
        IsActive = request.IsActive
    };
    if (IsKpiAlertTriggered(kpi.CurrentValue, alert.Direction, alert.ThresholdValue))
    {
        alert.LastTriggeredAt = DateTimeOffset.UtcNow;
    }

    db.KpiAlerts.Add(alert);
    await AuditAsync(db, http, "kpi-alert.create", "KpiAlert", alert.Id, null, "Created KPI alert", "{}", JsonSerializer.Serialize(SafeKpiAlertAudit(alert)), ct);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/kpi-alerts/{alert.Id}", KpiAlertDto.From(alert, kpi));
});

app.MapPut("/api/kpi-alerts/{alertId:guid}", async (
    Guid alertId,
    UpsertKpiAlertRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var alert = await db.KpiAlerts.Include(x => x.KpiDefinition).FirstOrDefaultAsync(x => x.Id == alertId, ct);
    if (alert is null || alert.KpiDefinition is null)
    {
        return Results.NotFound();
    }

    var before = JsonSerializer.Serialize(SafeKpiAlertAudit(alert));
    alert.Direction = NormalizeAlertDirection(request.Direction);
    alert.ThresholdValue = request.ThresholdValue;
    alert.Severity = string.IsNullOrWhiteSpace(request.Severity) ? alert.Severity : request.Severity;
    alert.Message = string.IsNullOrWhiteSpace(request.Message) ? alert.Message : request.Message;
    alert.IsActive = request.IsActive;
    alert.UpdatedAt = DateTimeOffset.UtcNow;
    if (alert.IsActive && IsKpiAlertTriggered(alert.KpiDefinition.CurrentValue, alert.Direction, alert.ThresholdValue))
    {
        alert.LastTriggeredAt = DateTimeOffset.UtcNow;
    }

    await AuditAsync(db, http, "kpi-alert.update", "KpiAlert", alert.Id, null, "Updated KPI alert", before, JsonSerializer.Serialize(SafeKpiAlertAudit(alert)), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(KpiAlertDto.From(alert));
});

app.MapGet("/api/non-financial-metrics", async (
    Guid organizationId,
    string? periodKey,
    AppDbContext db,
    CancellationToken ct) =>
{
    var query = db.NonFinancialMetrics.AsNoTracking().Where(x => x.OrganizationId == organizationId);
    if (!string.IsNullOrWhiteSpace(periodKey))
    {
        query = query.Join(db.ReportingPeriods.AsNoTracking().Where(x => x.Key == periodKey), metric => metric.ReportingPeriodId, period => period.Id, (metric, _) => metric);
    }

    var metrics = await query.OrderByDescending(x => x.IsPinned).ThenBy(x => x.Category).ThenBy(x => x.Name).ToListAsync(ct);
    return Results.Ok(metrics.Select(NonFinancialMetricDto.From));
});

app.MapPost("/api/non-financial-metrics", async (
    UpsertNonFinancialMetricRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var metric = new NonFinancialMetric
    {
        Id = Guid.NewGuid(),
        OrganizationId = request.OrganizationId,
        ReportingPeriodId = request.ReportingPeriodId,
        Name = request.Name,
        Category = request.Category,
        Unit = request.Unit,
        CurrentValue = request.CurrentValue,
        PriorValue = request.PriorValue,
        TargetValue = request.TargetValue,
        ValuesJson = request.ValuesJson ?? "[]",
        Source = string.IsNullOrWhiteSpace(request.Source) ? "Manual datasheet" : request.Source,
        IsPinned = request.IsPinned
    };
    db.NonFinancialMetrics.Add(metric);
    await AuditAsync(db, http, "non-financial-metric.create", "NonFinancialMetric", metric.Id, null, "Created non-financial metric", "{}", JsonSerializer.Serialize(metric), ct);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/non-financial-metrics/{metric.Id}", NonFinancialMetricDto.From(metric));
});

app.MapPut("/api/non-financial-metrics/{metricId:guid}", async (
    Guid metricId,
    UpsertNonFinancialMetricRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var metric = await db.NonFinancialMetrics.FirstOrDefaultAsync(x => x.Id == metricId, ct);
    if (metric is null)
    {
        return Results.NotFound();
    }

    var before = JsonSerializer.Serialize(metric);
    metric.Name = request.Name;
    metric.Category = request.Category;
    metric.Unit = request.Unit;
    metric.CurrentValue = request.CurrentValue;
    metric.PriorValue = request.PriorValue;
    metric.TargetValue = request.TargetValue;
    metric.ValuesJson = request.ValuesJson ?? metric.ValuesJson;
    metric.Source = string.IsNullOrWhiteSpace(request.Source) ? metric.Source : request.Source;
    metric.IsPinned = request.IsPinned;
    metric.UpdatedAt = DateTimeOffset.UtcNow;
    await AuditAsync(db, http, "non-financial-metric.update", "NonFinancialMetric", metric.Id, null, "Updated non-financial metric", before, JsonSerializer.Serialize(metric), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(NonFinancialMetricDto.From(metric));
});

app.MapGet("/api/fx-rates", async (
    Guid organizationId,
    string? periodKey,
    AppDbContext db,
    CancellationToken ct) =>
{
    var period = string.IsNullOrWhiteSpace(periodKey)
        ? await db.ReportingPeriods.AsNoTracking().OrderByDescending(x => x.PeriodStart).FirstOrDefaultAsync(ct)
        : await db.ReportingPeriods.AsNoTracking().FirstOrDefaultAsync(x => x.Key == periodKey, ct);
    if (period is null)
    {
        return Results.Ok(Array.Empty<FxRateDto>());
    }

    await EnsureFxDefaultsAsync(db, organizationId, period.Id, ct);
    var rates = await db.FxRates
        .AsNoTracking()
        .Where(x => x.OrganizationId == organizationId && x.ReportingPeriodId == period.Id)
        .OrderBy(x => x.CurrencyCode)
        .ToListAsync(ct);
    return Results.Ok(rates.Select(FxRateDto.From));
});

app.MapPost("/api/fx-rates", async (
    UpsertFxRateRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var rate = new FxRate
    {
        Id = Guid.NewGuid(),
        OrganizationId = request.OrganizationId,
        ReportingPeriodId = request.ReportingPeriodId,
        CurrencyCode = NormalizeCurrencyCode(request.CurrencyCode),
        RateToPresentation = request.RateToPresentation <= 0m ? 1m : request.RateToPresentation,
        Source = string.IsNullOrWhiteSpace(request.Source) ? "Manual" : request.Source.Trim()
    };
    db.FxRates.Add(rate);
    await AuditAsync(db, http, "fx-rate.create", "FxRate", rate.Id, null, "Created FX rate", "{}", JsonSerializer.Serialize(rate), ct);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/fx-rates/{rate.Id}", FxRateDto.From(rate));
});

app.MapPut("/api/fx-rates/{rateId:guid}", async (
    Guid rateId,
    UpsertFxRateRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var rate = await db.FxRates.FirstOrDefaultAsync(x => x.Id == rateId, ct);
    if (rate is null)
    {
        return Results.NotFound();
    }

    var before = JsonSerializer.Serialize(rate);
    rate.CurrencyCode = NormalizeCurrencyCode(request.CurrencyCode);
    rate.RateToPresentation = request.RateToPresentation <= 0m ? 1m : request.RateToPresentation;
    rate.Source = string.IsNullOrWhiteSpace(request.Source) ? "Manual" : request.Source.Trim();
    rate.UpdatedAt = DateTimeOffset.UtcNow;
    await AuditAsync(db, http, "fx-rate.update", "FxRate", rate.Id, null, "Updated FX rate", before, JsonSerializer.Serialize(rate), ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(FxRateDto.From(rate));
});

app.MapGet("/api/planning/{packageId:guid}/overview", async (
    Guid packageId,
    AppDbContext db,
    CancellationToken ct) =>
{
    var package = await db.ReportPackages
        .AsNoTracking()
        .Include(x => x.Organization)
        .Include(x => x.ReportingPeriod)
        .FirstOrDefaultAsync(x => x.Id == packageId, ct);
    if (package is null || package.ReportingPeriod is null)
    {
        return Results.NotFound();
    }

    await EnsurePlanningDefaultsAsync(db, package.OrganizationId, package.ReportingPeriodId, ct);
    var scenarios = await db.ForecastScenarios
        .AsNoTracking()
        .Include(x => x.Events.OrderBy(e => e.MonthOffset))
        .Where(x => x.OrganizationId == package.OrganizationId && x.ReportingPeriodId == package.ReportingPeriodId)
        .OrderByDescending(x => x.IsBase)
        .ThenBy(x => x.Name)
        .ToListAsync(ct);
    var actuals = await BuildForecastActualsAsync(db, package.OrganizationId, package.ReportingPeriodId, ct);
    var budgetRows = await BuildBudgetVarianceRowsAsync(db, package.OrganizationId, package.ReportingPeriodId, scenarios.FirstOrDefault(), ct);
    var metrics = await db.NonFinancialMetrics
        .AsNoTracking()
        .Where(x => x.OrganizationId == package.OrganizationId && x.ReportingPeriodId == package.ReportingPeriodId)
        .OrderByDescending(x => x.IsPinned)
        .ThenBy(x => x.Name)
        .ToListAsync(ct);

    var overview = new PlanningOverviewDto(
        package.Id,
        package.OrganizationId,
        package.Organization?.Name ?? "",
        package.ReportingPeriod.Key,
        actuals.MonthlyRevenue,
        actuals.MonthlyOperatingExpense,
        actuals.StartMonth,
        scenarios.Select(x => ForecastScenarioDto.From(x, actuals)).ToList(),
        budgetRows,
        metrics.Select(NonFinancialMetricDto.From).ToList());
    return Results.Ok(overview);
});

app.MapGet("/api/planning/{packageId:guid}/cash-timing", async (
    Guid packageId,
    Guid? scenarioId,
    string? granularity,
    int? months,
    AppDbContext db,
    CancellationToken ct) =>
{
    var package = await db.ReportPackages
        .AsNoTracking()
        .Include(x => x.ReportingPeriod)
        .FirstOrDefaultAsync(x => x.Id == packageId, ct);
    if (package is null)
    {
        return Results.NotFound();
    }

    await EnsurePlanningDefaultsAsync(db, package.OrganizationId, package.ReportingPeriodId, ct);
    var scenarioQuery = db.ForecastScenarios
        .AsNoTracking()
        .Include(x => x.Events.OrderBy(e => e.MonthOffset))
        .Where(x => x.OrganizationId == package.OrganizationId && x.ReportingPeriodId == package.ReportingPeriodId);
    var scenario = scenarioId is not null
        ? await scenarioQuery.FirstOrDefaultAsync(x => x.Id == scenarioId, ct)
        : await scenarioQuery.OrderByDescending(x => x.IsBase).ThenBy(x => x.Name).FirstOrDefaultAsync(ct);
    if (scenario is null)
    {
        return Results.NotFound();
    }

    var actuals = await BuildForecastActualsAsync(db, package.OrganizationId, package.ReportingPeriodId, ct);
    var dto = ForecastScenarioDto.From(scenario, actuals);
    var rows = BuildCashTimingRows(dto, granularity, months);
    return Results.Ok(new CashTimingDto(packageId, dto.Id, NormalizeCashGranularity(granularity), rows));
});

app.MapPost("/api/planning/scenarios", async (
    UpsertForecastScenarioRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var scenario = new ForecastScenario
    {
        Id = Guid.NewGuid(),
        OrganizationId = request.OrganizationId,
        ReportingPeriodId = request.ReportingPeriodId,
        Name = request.Name,
        Description = request.Description ?? "",
        ScenarioType = request.ScenarioType ?? "Custom",
        HorizonMonths = Math.Clamp(request.HorizonMonths, 1, 36),
        RevenueGrowthPercent = request.RevenueGrowthPercent,
        GrossMarginPercent = request.GrossMarginPercent,
        OpexGrowthPercent = request.OpexGrowthPercent,
        CashConversionPercent = request.CashConversionPercent,
        StartingCash = request.StartingCash,
        CashThreshold = request.CashThreshold,
        AssumptionsJson = request.AssumptionsJson ?? "[]",
        IsBase = request.IsBase
    };
    db.ForecastScenarios.Add(scenario);
    await AuditAsync(db, http, "forecast-scenario.create", "ForecastScenario", scenario.Id, null, "Created forecast scenario", "{}", JsonSerializer.Serialize(SafeForecastScenarioAudit(scenario)), ct);
    await db.SaveChangesAsync(ct);

    var actuals = await BuildForecastActualsAsync(db, scenario.OrganizationId, scenario.ReportingPeriodId, ct);
    return Results.Created($"/api/planning/scenarios/{scenario.Id}", ForecastScenarioDto.From(scenario, actuals));
});

app.MapPut("/api/planning/scenarios/{scenarioId:guid}", async (
    Guid scenarioId,
    UpsertForecastScenarioRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var scenario = await db.ForecastScenarios.Include(x => x.Events).FirstOrDefaultAsync(x => x.Id == scenarioId, ct);
    if (scenario is null)
    {
        return Results.NotFound();
    }

    var before = JsonSerializer.Serialize(SafeForecastScenarioAudit(scenario));
    scenario.Name = request.Name;
    scenario.Description = request.Description ?? "";
    scenario.ScenarioType = request.ScenarioType ?? scenario.ScenarioType;
    scenario.HorizonMonths = Math.Clamp(request.HorizonMonths, 1, 36);
    scenario.RevenueGrowthPercent = request.RevenueGrowthPercent;
    scenario.GrossMarginPercent = request.GrossMarginPercent;
    scenario.OpexGrowthPercent = request.OpexGrowthPercent;
    scenario.CashConversionPercent = request.CashConversionPercent;
    scenario.StartingCash = request.StartingCash;
    scenario.CashThreshold = request.CashThreshold;
    scenario.AssumptionsJson = request.AssumptionsJson ?? scenario.AssumptionsJson;
    scenario.IsBase = request.IsBase;
    scenario.UpdatedAt = DateTimeOffset.UtcNow;
    await AuditAsync(db, http, "forecast-scenario.update", "ForecastScenario", scenario.Id, null, "Updated forecast scenario", before, JsonSerializer.Serialize(SafeForecastScenarioAudit(scenario)), ct);
    await db.SaveChangesAsync(ct);

    var actuals = await BuildForecastActualsAsync(db, scenario.OrganizationId, scenario.ReportingPeriodId, ct);
    return Results.Ok(ForecastScenarioDto.From(scenario, actuals));
});

app.MapPost("/api/planning/scenarios/{scenarioId:guid}/events", async (
    Guid scenarioId,
    UpsertForecastEventRequest request,
    HttpContext http,
    AppDbContext db,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var scenario = await db.ForecastScenarios.FirstOrDefaultAsync(x => x.Id == scenarioId, ct);
    if (scenario is null)
    {
        return Results.NotFound();
    }

    var forecastEvent = new ForecastEvent
    {
        Id = Guid.NewGuid(),
        ForecastScenarioId = scenario.Id,
        MonthOffset = Math.Clamp(request.MonthOffset, 1, 36),
        Name = request.Name,
        Category = request.Category ?? "Microforecast",
        RevenueImpact = request.RevenueImpact,
        ExpenseImpact = request.ExpenseImpact,
        CashImpact = request.CashImpact,
        IsRecurring = request.IsRecurring,
        Notes = request.Notes ?? ""
    };
    db.ForecastEvents.Add(forecastEvent);
    scenario.UpdatedAt = DateTimeOffset.UtcNow;
    await AuditAsync(db, http, "forecast-event.create", "ForecastEvent", forecastEvent.Id, null, "Created forecast micro-event", "{}", JsonSerializer.Serialize(SafeForecastEventAudit(forecastEvent)), ct);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/planning/scenarios/{scenario.Id}/events/{forecastEvent.Id}", ForecastEventDto.From(forecastEvent));
});

app.MapGet("/api/benchmarking", async (
    string? periodKey,
    AppDbContext db,
    CancellationToken ct) =>
{
    var selectedPeriod = string.IsNullOrWhiteSpace(periodKey)
        ? await db.ReportingPeriods.AsNoTracking().OrderByDescending(x => x.PeriodStart).FirstOrDefaultAsync(ct)
        : await db.ReportingPeriods.AsNoTracking().FirstOrDefaultAsync(x => x.Key == periodKey, ct);
    if (selectedPeriod is null)
    {
        return Results.Ok(new BenchmarkingDto("", []));
    }

    var organizations = await db.Organizations.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
    var rows = new List<BenchmarkRowDto>();
    foreach (var organization in organizations)
    {
        var package = await db.ReportPackages
            .AsNoTracking()
            .Include(x => x.Issues)
            .FirstOrDefaultAsync(x => x.OrganizationId == organization.Id && x.ReportingPeriodId == selectedPeriod.Id, ct);
        var rollup = await BuildBenchmarkRollupAsync(db, organization.Id, selectedPeriod.Id, ct);
        var kpis = await db.KpiDefinitions
            .AsNoTracking()
            .Where(x => x.OrganizationId == organization.Id)
            .OrderByDescending(x => x.IsPinned)
            .ThenBy(x => x.Name)
            .Take(6)
            .ToListAsync(ct);
        rows.Add(new BenchmarkRowDto(
            organization.Id,
            organization.Name,
            organization.Abbreviation,
            organization.IsConsolidated,
            package?.Id,
            package?.Status.ToString() ?? "No package",
            rollup.Revenue,
            rollup.Expense,
            rollup.Net,
            rollup.GrossMarginPercent,
            package?.Issues.Count(x => x.Status == IssueStatus.Open) ?? 0,
            kpis.Select(KpiDto.From).ToList()));
    }

    var ranked = rows
        .OrderByDescending(x => x.NetIncome)
        .Select((row, index) => row with { Rank = index + 1 })
        .ToList();
    return Results.Ok(new BenchmarkingDto(selectedPeriod.Key, ranked));
});

app.MapGet("/api/report-templates", async (AppDbContext db, CancellationToken ct) =>
{
    await EnsureReportTemplatesAsync(db, ct);
    var templates = await db.ReportTemplates.AsNoTracking().OrderBy(x => x.Category).ThenBy(x => x.Name).ToListAsync(ct);
    return Results.Ok(templates.Select(ReportTemplateDto.From));
});

app.MapPost("/api/packages/{packageId:guid}/apply-template", async (
    Guid packageId,
    ApplyReportTemplateRequest request,
    HttpContext http,
    AppDbContext db,
    PackageSnapshotBuilder snapshotBuilder,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    await EnsureReportTemplatesAsync(db, ct);
    var package = await db.ReportPackages
        .Include(x => x.Slides)
            .ThenInclude(x => x.Blocks)
        .FirstOrDefaultAsync(x => x.Id == packageId, ct);
    var template = await db.ReportTemplates.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.TemplateId, ct);
    if (package is null || template is null)
    {
        return Results.NotFound();
    }

    var before = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct);
    var sections = ReadStringArray(template.SectionsJson);
    var existingSubjects = package.Slides.Select(x => x.Subject).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var nextSortOrder = package.Slides.Count == 0 ? 1 : package.Slides.Max(x => x.SortOrder) + 1;
    foreach (var section in sections.Where(x => !existingSubjects.Contains(x)))
    {
        var slide = new PackageSlide
        {
            Id = Guid.NewGuid(),
            ReportPackageId = package.Id,
            SortOrder = nextSortOrder++,
            Subject = section,
            KpiLabel = section,
            CurrentValue = 0m,
            PriorValue = 0m,
            VarianceAmount = 0m,
            VariancePercent = 0m,
            AccountCodesCsv = "",
            MonthlyJson = "[]",
            PriorMonthlyJson = "[]",
            ChartConfigJson = JsonSerializer.Serialize(new { type = "template", source = template.Name }),
            Blocks =
            [
                new SlideBlock { Id = Guid.NewGuid(), SortOrder = 1, Kind = "text", ContentJson = JsonSerializer.Serialize(new { text = $"{section} generated from {template.Name}." }) },
                new SlideBlock { Id = Guid.NewGuid(), SortOrder = 2, Kind = "table", ContentJson = JsonSerializer.Serialize(new { source = "template" }) }
            ]
        };
        db.PackageSlides.Add(slide);
    }

    package.UpdatedAt = DateTimeOffset.UtcNow;
    await AddVersionAndAuditAsync(db, http, snapshotBuilder, packageId, "report-template.apply", "ReportPackage", package.Id, $"Applied template {template.Name}", before, ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(PackageDto.From(await db.ReportPackages
        .AsNoTracking()
        .Include(x => x.Organization)
        .Include(x => x.ReportingPeriod)
        .Include(x => x.Slides.OrderBy(s => s.SortOrder))
            .ThenInclude(s => s.Blocks.OrderBy(b => b.SortOrder))
        .Include(x => x.Issues)
        .FirstAsync(x => x.Id == packageId, ct)));
});

app.MapGet("/api/competitive-gaps", () => Results.Ok(CompetitiveFeatureGroups()));

app.MapPut("/api/packages/{packageId:guid}/theme", async (
    Guid packageId,
    UpdatePackageThemeRequest request,
    HttpContext http,
    AppDbContext db,
    PackageSnapshotBuilder snapshotBuilder,
    CancellationToken ct) =>
{
    if (!Can(http, "Admin", "Finance Editor"))
    {
        return Results.Forbid();
    }

    var package = await db.ReportPackages.FirstOrDefaultAsync(x => x.Id == packageId, ct);
    if (package is null)
    {
        return Results.NotFound();
    }

    var before = await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct);
    package.ThemeJson = JsonSerializer.Serialize(new
    {
        request.Primary,
        request.Accent,
        request.LogoFileName,
        request.FontFamily,
        request.CoverStyle,
        request.PageOrder,
        request.HeaderText,
        request.FooterText,
        request.ExportSettings
    });
    package.UpdatedAt = DateTimeOffset.UtcNow;
    await AddVersionAndAuditAsync(db, http, snapshotBuilder, packageId, "package.theme", "ReportPackage", package.Id, "Updated branding/layout settings", before, ct);
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { package.Id, package.ThemeJson });
});

app.MapGet("/api/audit", async (Guid? reportPackageId, AppDbContext db, CancellationToken ct) =>
{
    var query = db.AuditRecords.AsNoTracking().AsQueryable();
    if (reportPackageId is not null)
    {
        query = query.Where(x => x.ReportPackageId == reportPackageId);
    }

    var records = await query.ToListAsync(ct);
    return Results.Ok(records.OrderByDescending(x => x.CreatedAt).Take(200));
});

app.Run();

static string XeroCallbackHtml(bool success, string title, string message)
{
    var encodedTitle = System.Net.WebUtility.HtmlEncode(title);
    var encodedMessage = System.Net.WebUtility.HtmlEncode(message);
    var type = success ? "xero-connected" : "xero-error";
    var color = success ? "#0f7a57" : "#b91c1c";
    var messagePayload = JsonSerializer.Serialize(new { type, message });

    return $"""
        <!doctype html>
        <html>
        <head>
            <title>{encodedTitle}</title>
            <meta name="viewport" content="width=device-width, initial-scale=1" />
        </head>
        <body style="margin:0;background:#f7f6f4;color:#1a1a1a;font-family:Inter,-apple-system,BlinkMacSystemFont,'Segoe UI',sans-serif;">
            <main style="max-width:520px;margin:72px auto;padding:28px;border:1px solid #e5e5e8;border-radius:8px;background:white;">
                <div style="width:36px;height:36px;border-radius:8px;background:{color};color:white;display:grid;place-items:center;font-weight:800;">{(success ? "✓" : "!")}</div>
                <h1 style="margin:18px 0 8px;font-size:24px;line-height:1.1;">{encodedTitle}</h1>
                <p style="margin:0;color:#6b6b70;line-height:1.5;">{encodedMessage}</p>
            </main>
            <script>
                window.opener?.postMessage({messagePayload}, '*');
            </script>
        </body>
        </html>
        """;
}

static bool TryParsePeriodKey(string periodKey, out int year, out int month)
{
    year = 0;
    month = 0;
    var parts = periodKey.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return parts.Length == 2
           && int.TryParse(parts[0], out year)
           && int.TryParse(parts[1], out month)
           && year is >= 2000 and <= 2100
           && month is >= 1 and <= 12;
}

static ReportingPeriod BuildReportingPeriod(int year, int month, bool isClosed)
{
    var start = new DateOnly(year, month, 1);
    return new ReportingPeriod
    {
        Id = Guid.NewGuid(),
        Key = $"{year:D4}-{month:D2}",
        Label = new DateTime(year, month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture),
        PeriodStart = start,
        PeriodEnd = start.AddMonths(1).AddDays(-1),
        IsClosed = isClosed
    };
}

static string BuildBaseFrom(ReportingPeriod period)
    => period.PeriodStart.AddMonths(-1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);

static string SafeXeroCallbackMessage(Exception ex)
{
    var message = RedactSensitive(ex.Message);
    if (string.IsNullOrWhiteSpace(message))
    {
        return "The callback could not be completed. Return to Xero Settings and start a fresh reconnect.";
    }

    if (message.Length > 280)
    {
        message = $"{message[..280]}...";
    }

    return $"The callback could not be completed: {message}";
}

static IEnumerable<FixOperation> ParseOperations(string json)
{
    JsonNode? root;
    try
    {
        root = JsonNode.Parse(json);
    }
    catch
    {
        yield break;
    }

    var operations = root?["operations"]?.AsArray();
    if (operations is null)
    {
        yield break;
    }

    foreach (var node in operations)
    {
        if (node is null)
        {
            continue;
        }

        var targetId = Guid.TryParse(node["targetId"]?.GetValue<string>(), out var id) ? id : Guid.Empty;
        var valueElement = node["value"] is null
            ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(node["value"]);
        yield return new FixOperation(
            node["op"]?.GetValue<string>() ?? "",
            node["targetType"]?.GetValue<string>() ?? "",
            targetId,
            valueElement,
            node["reason"]?.GetValue<string>());
    }
}

static async Task ApplyOperationAsync(AppDbContext db, FixOperation operation, string reason, CancellationToken ct)
{
    if (operation.TargetType.Equals("slide", StringComparison.OrdinalIgnoreCase))
    {
        var slide = await db.PackageSlides.FirstOrDefaultAsync(x => x.Id == operation.TargetId, ct);
        if (slide is null)
        {
            return;
        }

        if (operation.Op.Equals("append_narrative", StringComparison.OrdinalIgnoreCase)
            || operation.Op.Equals("add_callout", StringComparison.OrdinalIgnoreCase))
        {
            var text = ExtractText(operation.Value) ?? "AI recommended update approved.";
            var nextSortOrder = await db.SlideBlocks
                .Where(x => x.PackageSlideId == slide.Id)
                .Select(x => (int?)x.SortOrder)
                .MaxAsync(ct) ?? 0;
            db.SlideBlocks.Add(new SlideBlock
            {
                Id = Guid.NewGuid(),
                PackageSlideId = slide.Id,
                SortOrder = nextSortOrder + 1,
                Kind = operation.Op.Equals("add_callout", StringComparison.OrdinalIgnoreCase) ? "callout" : "text",
                ContentJson = JsonSerializer.Serialize(new { text, reason })
            });
        }
        else if (operation.Op.Equals("update_chart", StringComparison.OrdinalIgnoreCase) && operation.Value is { } value)
        {
            slide.ChartConfigJson = value.GetRawText();
        }
    }

    if (operation.TargetType.Equals("block", StringComparison.OrdinalIgnoreCase)
        && operation.Op.Equals("replace_text", StringComparison.OrdinalIgnoreCase))
    {
        var block = await db.SlideBlocks.FirstOrDefaultAsync(x => x.Id == operation.TargetId, ct);
        if (block is not null)
        {
            block.ContentJson = JsonSerializer.Serialize(new { text = ExtractText(operation.Value) ?? "", reason });
        }
    }

    if (operation.TargetType.Equals("account", StringComparison.OrdinalIgnoreCase))
    {
        var account = await db.GlAccounts.FirstOrDefaultAsync(x => x.Id == operation.TargetId, ct);
        if (account is null)
        {
            return;
        }

        if (operation.Op.Equals("map_account", StringComparison.OrdinalIgnoreCase))
        {
            account.FsLine = ExtractFsLine(operation.Value) ?? account.FsLine;
            account.ReviewStatus = MappingReviewStatus.Reviewed;
            db.AccountMappings.Add(new AccountMapping
            {
                Id = Guid.NewGuid(),
                OrganizationId = account.OrganizationId,
                ReportingPeriodId = account.ReportingPeriodId,
                FsLine = account.FsLine,
                AccountCodesCsv = account.Code,
                EntityKeysCsv = account.TenantId,
                Reason = reason
            });
        }
        else if (operation.Op.Equals("eliminate_account", StringComparison.OrdinalIgnoreCase))
        {
            account.ConsolidationTreatment = ConsolidationTreatment.Eliminate;
            db.EliminationEntries.Add(new EliminationEntry
            {
                Id = Guid.NewGuid(),
                OrganizationId = account.OrganizationId,
                ReportingPeriodId = account.ReportingPeriodId,
                GlAccountId = account.Id,
                Type = "EliminateAccount",
                Description = $"AI-approved elimination for {account.Code}",
                Amount = await db.GlTransactions.Where(x => x.GlAccountId == account.Id).SumAsync(x => x.Credit - x.Debit, ct),
                Reason = reason
            });
        }
        else if (operation.Op.Equals("exclude_account", StringComparison.OrdinalIgnoreCase))
        {
            account.ConsolidationTreatment = ConsolidationTreatment.Exclude;
        }
        else if (operation.Op.Equals("create_intercompany_elimination", StringComparison.OrdinalIgnoreCase))
        {
            account.ConsolidationTreatment = ConsolidationTreatment.Intercompany;
            db.EliminationEntries.Add(new EliminationEntry
            {
                Id = Guid.NewGuid(),
                OrganizationId = account.OrganizationId,
                ReportingPeriodId = account.ReportingPeriodId,
                GlAccountId = account.Id,
                Type = "Intercompany",
                Description = ExtractText(operation.Value) ?? $"AI-approved intercompany elimination for {account.Code}",
                Amount = await db.GlTransactions.Where(x => x.GlAccountId == account.Id).SumAsync(x => x.Credit - x.Debit, ct),
                Reason = reason
            });
        }

        account.AuditReason = reason;
        account.UpdatedAt = DateTimeOffset.UtcNow;
    }

    if (operation.TargetType.Equals("kpi", StringComparison.OrdinalIgnoreCase)
        && operation.Op.Equals("update_kpi", StringComparison.OrdinalIgnoreCase))
    {
        var kpi = await db.KpiDefinitions.FirstOrDefaultAsync(x => x.Id == operation.TargetId, ct);
        if (kpi is not null && operation.Value is { } value)
        {
            if (value.TryGetProperty("targetValue", out var target) && target.TryGetDecimal(out var targetValue))
            {
                kpi.TargetValue = targetValue;
            }

            if (value.TryGetProperty("currentValue", out var current) && current.TryGetDecimal(out var currentValue))
            {
                kpi.CurrentValue = currentValue;
            }

            if (value.TryGetProperty("isPinned", out var pinned) && pinned.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                kpi.IsPinned = pinned.GetBoolean();
            }

            kpi.Status = FinancialMath.KpiStatus(kpi.CurrentValue, kpi.TargetValue);
        }
    }

    if (operation.TargetType.Equals("issue", StringComparison.OrdinalIgnoreCase))
    {
        var issue = await db.PackageIssues.FirstOrDefaultAsync(x => x.Id == operation.TargetId, ct);
        if (issue is not null)
        {
            if (operation.Op.Equals("resolve_issue", StringComparison.OrdinalIgnoreCase))
            {
                issue.Status = IssueStatus.Resolved;
                issue.ResolvedAt = DateTimeOffset.UtcNow;
            }
            else if (operation.Op.Equals("ignore_issue", StringComparison.OrdinalIgnoreCase))
            {
                issue.Status = IssueStatus.Ignored;
            }

            issue.UserComment = reason;
        }
    }
}

static string? ExtractText(JsonElement? value)
{
    if (value is null)
    {
        return null;
    }

    return value.Value.ValueKind == JsonValueKind.Object && value.Value.TryGetProperty("text", out var text)
        ? text.GetString()
        : value.Value.ToString();
}

static string? ExtractFsLine(JsonElement? value)
{
    if (value is null)
    {
        return null;
    }

    return value.Value.ValueKind == JsonValueKind.Object && value.Value.TryGetProperty("fsLine", out var text)
        ? text.GetString()
        : null;
}

static bool Can(HttpContext http, params string[] roles)
{
    var roleHeader = http.Request.Headers["X-FR-Role"].FirstOrDefault();
    var activeRoles = string.IsNullOrWhiteSpace(roleHeader)
        ? ["Admin"]
        : roleHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    return activeRoles.Any(role => roles.Contains(role, StringComparer.OrdinalIgnoreCase));
}

static string Actor(HttpContext http)
    => http.Request.Headers["X-FR-User"].FirstOrDefault() ?? "dev-admin";

static string Role(HttpContext http)
    => http.Request.Headers["X-FR-Role"].FirstOrDefault() ?? "Admin";

static Task AuditAsync(
    AppDbContext db,
    HttpContext http,
    string action,
    string entityType,
    Guid? entityId,
    Guid? reportPackageId,
    string reason,
    string beforeJson,
    string afterJson,
    CancellationToken ct)
{
    db.AuditRecords.Add(new AuditRecord
    {
        Id = Guid.NewGuid(),
        Actor = Actor(http),
        Role = Role(http),
        Action = action,
        EntityType = entityType,
        EntityId = entityId,
        ReportPackageId = reportPackageId,
        Reason = reason,
        BeforeJson = RedactSensitive(beforeJson),
        AfterJson = RedactSensitive(afterJson)
    });
    return Task.CompletedTask;
}

static async Task AddVersionAndAuditAsync(
    AppDbContext db,
    HttpContext http,
    PackageSnapshotBuilder snapshotBuilder,
    Guid packageId,
    string action,
    string entityType,
    Guid entityId,
    string summary,
    string before,
    CancellationToken ct)
{
    db.PackageVersions.Add(new PackageVersion
    {
        Id = Guid.NewGuid(),
        ReportPackageId = packageId,
        VersionLabel = $"{summary} {DateTimeOffset.UtcNow:HH:mm}",
        CreatedBy = Actor(http),
        ChangeSummary = summary,
        SnapshotJson = before
    });
    await AuditAsync(db, http, action, entityType, entityId, packageId, summary, before, await snapshotBuilder.BuildPackageSnapshotAsync(packageId, ct), ct);
}

static async Task<bool> RestorePackageSnapshotAsync(AppDbContext db, Guid packageId, string snapshotJson, CancellationToken ct)
{
    JsonNode? root;
    try
    {
        root = JsonNode.Parse(snapshotJson);
    }
    catch
    {
        return false;
    }

    if (root?["package"] is null || root["slides"] is not JsonArray slides)
    {
        return false;
    }

    var package = await db.ReportPackages
        .Include(x => x.Slides)
            .ThenInclude(x => x.Blocks)
        .FirstOrDefaultAsync(x => x.Id == packageId, ct);
    if (package is null)
    {
        return false;
    }

    package.VersionLabel = root["package"]?["versionLabel"]?.GetValue<string>() ?? package.VersionLabel;
    package.BaseFrom = root["package"]?["baseFrom"]?.GetValue<string>() ?? package.BaseFrom;
    package.ThemeJson = root["package"]?["themeJson"]?.GetValue<string>() ?? package.ThemeJson;
    package.UpdatedAt = DateTimeOffset.UtcNow;

    var incomingSlideIds = new HashSet<Guid>();
    foreach (var slideNode in slides)
    {
        if (slideNode is null || !Guid.TryParse(slideNode["id"]?.GetValue<string>(), out var slideId))
        {
            continue;
        }

        incomingSlideIds.Add(slideId);
        var slide = package.Slides.FirstOrDefault(x => x.Id == slideId);
        if (slide is null)
        {
            slide = new PackageSlide { Id = slideId, ReportPackageId = packageId };
            package.Slides.Add(slide);
        }

        slide.SortOrder = slideNode["sortOrder"]?.GetValue<int>() ?? slide.SortOrder;
        slide.Subject = slideNode["subject"]?.GetValue<string>() ?? slide.Subject;
        slide.KpiLabel = slideNode["kpiLabel"]?.GetValue<string>() ?? slide.KpiLabel;
        slide.CurrentValue = slideNode["currentValue"]?.GetValue<decimal>() ?? slide.CurrentValue;
        slide.PriorValue = slideNode["priorValue"]?.GetValue<decimal>() ?? slide.PriorValue;
        slide.VarianceAmount = slideNode["varianceAmount"]?.GetValue<decimal>() ?? slide.VarianceAmount;
        slide.VariancePercent = slideNode["variancePercent"]?.GetValue<decimal>() ?? slide.VariancePercent;
        slide.AccountCodesCsv = slideNode["accountCodesCsv"]?.GetValue<string>() ?? slide.AccountCodesCsv;
        slide.MonthlyJson = slideNode["monthlyJson"]?.GetValue<string>() ?? slide.MonthlyJson;
        slide.PriorMonthlyJson = slideNode["priorMonthlyJson"]?.GetValue<string>() ?? slide.PriorMonthlyJson;
        slide.ChartConfigJson = slideNode["chartConfigJson"]?.GetValue<string>() ?? slide.ChartConfigJson;

        db.SlideBlocks.RemoveRange(slide.Blocks);
        if (slideNode["blocks"] is JsonArray blocks)
        {
            foreach (var blockNode in blocks)
            {
                if (blockNode is null || !Guid.TryParse(blockNode["id"]?.GetValue<string>(), out var blockId))
                {
                    continue;
                }

                slide.Blocks.Add(new SlideBlock
                {
                    Id = blockId,
                    PackageSlideId = slide.Id,
                    SortOrder = blockNode["sortOrder"]?.GetValue<int>() ?? 1,
                    Kind = blockNode["kind"]?.GetValue<string>() ?? "text",
                    ContentJson = blockNode["contentJson"]?.GetValue<string>() ?? "{}"
                });
            }
        }
    }

    foreach (var slide in package.Slides.Where(x => !incomingSlideIds.Contains(x.Id)).ToList())
    {
        db.PackageSlides.Remove(slide);
    }

    return true;
}

static async Task<List<PeriodOptionDto>> BuildEntityPeriodsAsync(AppDbContext db, Guid organizationId, CancellationToken ct)
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

static async Task EnsureFsLineDefinitionsAsync(AppDbContext db, Guid organizationId, CancellationToken ct)
{
    if (await db.FsLineDefinitions.AnyAsync(x => x.OrganizationId == organizationId, ct))
    {
        return;
    }

    var candidates = DefaultFsLineDefinitions();

    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var sort = 10;
    foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x.Name)))
    {
        var statementType = NormalizeStatementType(candidate.StatementType);
        var name = candidate.Name.Trim();
        if (!seen.Add($"{statementType}|{name}"))
        {
            continue;
        }

        db.FsLineDefinitions.Add(new FsLineDefinition
        {
            Id = Guid.NewGuid(),
            OrganizationId = organizationId,
            StatementType = statementType,
            Section = string.IsNullOrWhiteSpace(candidate.Section) ? InferFsLineSection(name, statementType) : candidate.Section.Trim(),
            Name = name,
            NormalBalance = NormalizeNormalBalance(candidate.NormalBalance, statementType),
            AiGuidance = candidate.AiGuidance.Trim(),
            SortOrder = sort,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        sort += 10;
    }

    await db.SaveChangesAsync(ct);
}

static async Task<bool> FsLineDefinitionExistsAsync(AppDbContext db, Guid organizationId, string fsLine, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(fsLine))
    {
        return false;
    }

    await EnsureFsLineDefinitionsAsync(db, organizationId, ct);
    var names = await db.FsLineDefinitions
        .AsNoTracking()
        .Where(x => x.OrganizationId == organizationId && x.IsActive)
        .Select(x => x.Name)
        .ToListAsync(ct);
    return names.Any(x => string.Equals(x, fsLine.Trim(), StringComparison.OrdinalIgnoreCase));
}

static async Task<int> NextFsLineSortOrderAsync(AppDbContext db, Guid organizationId, string statementType, string? section, CancellationToken ct)
{
    var rows = await db.FsLineDefinitions
        .AsNoTracking()
        .Where(x => x.OrganizationId == organizationId && x.StatementType == statementType)
        .Select(x => new { x.Section, x.SortOrder })
        .ToListAsync(ct);
    var sectionRows = string.IsNullOrWhiteSpace(section)
        ? rows
        : rows.Where(x => string.Equals(x.Section, section.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
    return sectionRows.Count == 0 ? 10 : sectionRows.Max(x => x.SortOrder) + 10;
}

static string NormalizeStatementType(string? statementType)
{
    if (string.Equals(statementType, "BalanceSheet", StringComparison.OrdinalIgnoreCase)
        || string.Equals(statementType, "Balance Sheet", StringComparison.OrdinalIgnoreCase))
    {
        return "BalanceSheet";
    }

    if (string.Equals(statementType, "TrialBalance", StringComparison.OrdinalIgnoreCase)
        || string.Equals(statementType, "Trial Balance", StringComparison.OrdinalIgnoreCase))
    {
        return "TrialBalance";
    }

    return "IncomeStatement";
}

static string InferFsLineSection(string fsLine, string statementType)
{
    if (statementType == "BalanceSheet")
    {
        if (fsLine.Contains("liabil", StringComparison.OrdinalIgnoreCase) || fsLine.Contains("payable", StringComparison.OrdinalIgnoreCase) || fsLine.Contains("debt", StringComparison.OrdinalIgnoreCase))
        {
            return "Liabilities";
        }

        if (fsLine.Contains("equity", StringComparison.OrdinalIgnoreCase) || fsLine.Contains("retained", StringComparison.OrdinalIgnoreCase))
        {
            return "Equity";
        }

        return "Assets";
    }

    if (fsLine.Contains("cost", StringComparison.OrdinalIgnoreCase) || fsLine.Contains("cogs", StringComparison.OrdinalIgnoreCase))
    {
        return "Cost of Revenue";
    }

    if (fsLine.Contains("revenue", StringComparison.OrdinalIgnoreCase) || fsLine.Contains("sales", StringComparison.OrdinalIgnoreCase) || fsLine.Contains("income", StringComparison.OrdinalIgnoreCase))
    {
        return "Revenue";
    }

    if (fsLine.Contains("other", StringComparison.OrdinalIgnoreCase))
    {
        return "Other Income / Expense";
    }

    return "Operating Expenses";
}

static string NormalizeNormalBalance(string? normalBalance, string statementType, string? accountType = null)
{
    if (string.Equals(normalBalance, "Debit", StringComparison.OrdinalIgnoreCase) || string.Equals(normalBalance, "Credit", StringComparison.OrdinalIgnoreCase))
    {
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalBalance!.ToLowerInvariant());
    }

    var text = $"{statementType} {accountType}";
    return text.Contains("expense", StringComparison.OrdinalIgnoreCase)
           || text.Contains("asset", StringComparison.OrdinalIgnoreCase)
           || text.Contains("cost", StringComparison.OrdinalIgnoreCase)
        ? "Debit"
        : "Credit";
}

static string NormalizeAlertDirection(string? direction)
    => string.Equals(direction, "Above", StringComparison.OrdinalIgnoreCase) ? "Above" : "Below";

static bool IsKpiAlertTriggered(decimal currentValue, string direction, decimal threshold)
    => string.Equals(direction, "Above", StringComparison.OrdinalIgnoreCase)
        ? currentValue > threshold
        : currentValue < threshold;

static object SafeKpiAlertAudit(KpiAlert alert)
    => new
    {
        alert.Id,
        alert.KpiDefinitionId,
        alert.Direction,
        alert.ThresholdValue,
        alert.Severity,
        alert.Message,
        alert.IsActive,
        alert.LastTriggeredAt,
        alert.CreatedAt,
        alert.UpdatedAt
    };

static async Task<FormulaEvaluationDto> EvaluateFormulaAsync(AppDbContext db, Guid organizationId, Guid reportingPeriodId, string formula, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(formula))
    {
        throw new ArgumentException("Formula is required.");
    }

    var dependencies = new List<string>();
    var fsLines = await db.FinancialStatementLines
        .AsNoTracking()
        .Where(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId)
        .ToListAsync(ct);
    var fsValues = fsLines
        .GroupBy(x => x.LineName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(x => x.Key, x => x.Sum(line => line.CurrentAmount), StringComparer.OrdinalIgnoreCase);
    foreach (var rowPathGroup in fsLines.GroupBy(x => x.RowPath, StringComparer.OrdinalIgnoreCase))
    {
        fsValues.TryAdd(rowPathGroup.Key, rowPathGroup.Sum(line => line.CurrentAmount));
    }

    var kpiValues = await db.KpiDefinitions
        .AsNoTracking()
        .Where(x => x.OrganizationId == organizationId)
        .ToDictionaryAsync(x => x.Name, x => x.CurrentValue, StringComparer.OrdinalIgnoreCase, ct);
    var metricValues = await db.NonFinancialMetrics
        .AsNoTracking()
        .Where(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId)
        .ToDictionaryAsync(x => x.Name, x => x.CurrentValue, StringComparer.OrdinalIgnoreCase, ct);

    var normalized = Regex.Replace(
        formula,
        """(?<fn>fs|kpi|metric)\(\s*["'](?<name>[^"']+)["']\s*\)""",
        match =>
        {
            var name = match.Groups["name"].Value.Trim();
            var fn = match.Groups["fn"].Value.ToLowerInvariant();
            var value = fn switch
            {
                "fs" when fsValues.TryGetValue(name, out var fsValue) => fsValue,
                "kpi" when kpiValues.TryGetValue(name, out var kpiValue) => kpiValue,
                "metric" when metricValues.TryGetValue(name, out var metricValue) => metricValue,
                _ => throw new InvalidOperationException($"Unknown {fn} reference '{name}'.")
            };
            dependencies.Add($"{fn}:{name}");
            return value.ToString(CultureInfo.InvariantCulture);
        },
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    if (Regex.IsMatch(normalized, "[A-Za-z_]", RegexOptions.CultureInvariant))
    {
        throw new InvalidOperationException("Only numbers, arithmetic operators, parentheses, and fs/kpi/metric references are supported.");
    }

    var value = FormulaMath.Evaluate(normalized);
    return new FormulaEvaluationDto(formula, normalized, value, dependencies.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
}

static string NormalizeCurrencyCode(string value)
{
    var normalized = new string(value.Where(char.IsLetter).Take(3).ToArray()).ToUpperInvariant();
    return normalized.Length == 3 ? normalized : "USD";
}

static async Task EnsureFxDefaultsAsync(AppDbContext db, Guid organizationId, Guid reportingPeriodId, CancellationToken ct)
{
    if (await db.FxRates.AnyAsync(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId, ct))
    {
        return;
    }

    db.FxRates.Add(new FxRate
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        ReportingPeriodId = reportingPeriodId,
        CurrencyCode = "USD",
        RateToPresentation = 1m,
        Source = "Presentation currency"
    });
    await db.SaveChangesAsync(ct);
}

static string NormalizeCashGranularity(string? granularity)
    => string.Equals(granularity, "Daily", StringComparison.OrdinalIgnoreCase) ? "Daily" : "Weekly";

static List<CashTimingRowDto> BuildCashTimingRows(ForecastScenarioDto scenario, string? granularity, int? months)
{
    var mode = NormalizeCashGranularity(granularity);
    var monthLimit = Math.Clamp(months ?? 3, 1, Math.Min(12, Math.Max(1, scenario.Rows.Count)));
    var rows = new List<CashTimingRowDto>();
    foreach (var month in scenario.Rows.Take(monthLimit))
    {
        var parts = month.MonthKey.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var year = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var monthNumber = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var start = new DateOnly(year, monthNumber, 1);
        var days = DateTime.DaysInMonth(year, monthNumber);
        var periodCount = mode == "Daily" ? days : (int)Math.Ceiling(days / 7m);
        var startingCash = month.EndingCash - month.NetCashFlow;
        var runningCash = startingCash;
        for (var period = 0; period < periodCount; period++)
        {
            var periodStart = mode == "Daily" ? start.AddDays(period) : start.AddDays(period * 7);
            var periodEnd = mode == "Daily" ? periodStart : MinDate(start.AddDays(days - 1), periodStart.AddDays(6));
            var inflow = decimal.Round(month.CashInflow / periodCount, 2);
            var outflow = decimal.Round(month.CashOutflow / periodCount, 2);
            var net = decimal.Round(inflow - outflow, 2);
            runningCash += net;
            var label = mode == "Daily" ? periodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : $"{month.MonthKey} W{period + 1}";
            rows.Add(new CashTimingRowDto(
                label,
                periodStart,
                periodEnd,
                inflow,
                outflow,
                net,
                decimal.Round(runningCash, 2),
                scenario.CashThreshold > 0m && runningCash < scenario.CashThreshold));
        }
    }

    return rows;
}

static DateOnly MinDate(DateOnly left, DateOnly right)
    => left <= right ? left : right;

static async Task EnsurePlanningDefaultsAsync(AppDbContext db, Guid organizationId, Guid reportingPeriodId, CancellationToken ct)
{
    if (!await db.ForecastScenarios.AnyAsync(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId, ct))
    {
        var actuals = await BuildForecastActualsAsync(db, organizationId, reportingPeriodId, ct);
        db.ForecastScenarios.AddRange(
            BuildDefaultScenario(organizationId, reportingPeriodId, "Base case", "Base", 7m, 39m, 4m, actuals.EstimatedStartingCash, actuals.CashThreshold, true),
            BuildDefaultScenario(organizationId, reportingPeriodId, "Upside case", "Upside", 12m, 41m, 5m, actuals.EstimatedStartingCash, actuals.CashThreshold, false),
            BuildDefaultScenario(organizationId, reportingPeriodId, "Downside case", "Downside", -3m, 35m, 2m, actuals.EstimatedStartingCash, actuals.CashThreshold, false));
        await db.SaveChangesAsync(ct);
    }

    if (!await db.NonFinancialMetrics.AnyAsync(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId, ct))
    {
        db.NonFinancialMetrics.AddRange(
            new NonFinancialMetric
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                ReportingPeriodId = reportingPeriodId,
                Name = "Customer Count",
                Category = "Operations",
                Unit = "customers",
                CurrentValue = 120m,
                PriorValue = 112m,
                TargetValue = 128m,
                ValuesJson = JsonSerializer.Serialize(new[] { 112, 114, 115, 117, 118, 119, 120 }),
                Source = "Manual datasheet",
                IsPinned = true
            },
            new NonFinancialMetric
            {
                Id = Guid.NewGuid(),
                OrganizationId = organizationId,
                ReportingPeriodId = reportingPeriodId,
                Name = "Employee Count",
                Category = "People",
                Unit = "FTE",
                CurrentValue = 34m,
                PriorValue = 31m,
                TargetValue = 36m,
                ValuesJson = JsonSerializer.Serialize(new[] { 31, 31, 32, 33, 33, 34, 34 }),
                Source = "Manual datasheet",
                IsPinned = false
            });
        await db.SaveChangesAsync(ct);
    }
}

static ForecastScenario BuildDefaultScenario(
    Guid organizationId,
    Guid reportingPeriodId,
    string name,
    string scenarioType,
    decimal revenueGrowth,
    decimal grossMargin,
    decimal opexGrowth,
    decimal startingCash,
    decimal cashThreshold,
    bool isBase)
    => new()
    {
        Id = Guid.NewGuid(),
        OrganizationId = organizationId,
        ReportingPeriodId = reportingPeriodId,
        Name = name,
        Description = $"{scenarioType} rolling 36-month scenario generated from the current package trend.",
        ScenarioType = scenarioType,
        HorizonMonths = 36,
        RevenueGrowthPercent = revenueGrowth,
        GrossMarginPercent = grossMargin,
        OpexGrowthPercent = opexGrowth,
        CashConversionPercent = 85m,
        StartingCash = startingCash,
        CashThreshold = cashThreshold,
        AssumptionsJson = JsonSerializer.Serialize(new[] { "Rolling forecast from latest imported actuals", "Working-capital conversion uses a simplified driver", "Edit assumptions as the board plan changes" }),
        IsBase = isBase
    };

static object SafeForecastScenarioAudit(ForecastScenario scenario)
    => new
    {
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
        scenario.CreatedAt,
        scenario.UpdatedAt,
        Events = scenario.Events.Select(SafeForecastEventAudit).ToList()
    };

static object SafeForecastEventAudit(ForecastEvent forecastEvent)
    => new
    {
        forecastEvent.Id,
        forecastEvent.ForecastScenarioId,
        forecastEvent.MonthOffset,
        forecastEvent.Name,
        forecastEvent.Category,
        forecastEvent.RevenueImpact,
        forecastEvent.ExpenseImpact,
        forecastEvent.CashImpact,
        forecastEvent.IsRecurring,
        forecastEvent.Notes,
        forecastEvent.CreatedAt
    };

static async Task<ForecastActuals> BuildForecastActualsAsync(AppDbContext db, Guid organizationId, Guid reportingPeriodId, CancellationToken ct)
{
    var period = await db.ReportingPeriods.AsNoTracking().FirstAsync(x => x.Id == reportingPeriodId, ct);
    var accounts = await db.GlAccounts
        .AsNoTracking()
        .Where(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId)
        .ToListAsync(ct);
    var revenueByMonth = new decimal[12];
    var expenseByMonth = new decimal[12];

    foreach (var account in accounts)
    {
        var balances = FinancialEngine.ReadMonthlyBalances(account.MonthlyBalancesJson);
        for (var i = 0; i < Math.Min(12, balances.Length); i++)
        {
            var amount = Math.Abs(balances[i]);
            if (IsRevenueAccount(account))
            {
                revenueByMonth[i] += amount;
            }
            else if (IsExpenseAccount(account))
            {
                expenseByMonth[i] += amount;
            }
        }
    }

    var latestRevenue = LastNonZero(revenueByMonth);
    var latestExpense = LastNonZero(expenseByMonth);
    if (latestRevenue == 0m)
    {
        latestRevenue = await db.PackageSlides.AsNoTracking()
            .Where(x => x.ReportPackage != null && x.ReportPackage.OrganizationId == organizationId && x.ReportPackage.ReportingPeriodId == reportingPeriodId)
            .Select(x => x.CurrentValue)
            .DefaultIfEmpty()
            .AverageAsync(ct) / 12m;
    }
    if (latestExpense == 0m)
    {
        latestExpense = latestRevenue * 0.58m;
    }

    var startingCash = Math.Max(250_000m, Math.Abs(latestExpense) * 6m);
    var cashRunway = await db.KpiDefinitions
        .AsNoTracking()
        .Where(x => x.OrganizationId == organizationId && x.Name.Contains("Cash Runway"))
        .Select(x => (decimal?)x.CurrentValue)
        .FirstOrDefaultAsync(ct);
    if (cashRunway is not null && latestExpense > 0m)
    {
        startingCash = latestExpense * cashRunway.Value;
    }

    return new ForecastActuals(
        period.PeriodStart.AddMonths(1),
        decimal.Round(Math.Max(latestRevenue, 1m), 2),
        decimal.Round(Math.Max(latestExpense, 1m), 2),
        decimal.Round(startingCash, 2),
        decimal.Round(Math.Max(latestExpense * 3m, startingCash * 0.25m), 2));
}

static bool IsRevenueAccount(GlAccount account)
    => account.Type.Contains("revenue", StringComparison.OrdinalIgnoreCase)
       || account.Type.Contains("income", StringComparison.OrdinalIgnoreCase)
       || account.FsLine.Contains("revenue", StringComparison.OrdinalIgnoreCase)
       || account.FsLine.Contains("income", StringComparison.OrdinalIgnoreCase);

static bool IsExpenseAccount(GlAccount account)
    => account.Type.Contains("expense", StringComparison.OrdinalIgnoreCase)
       || account.Type.Contains("cost", StringComparison.OrdinalIgnoreCase)
       || account.FsLine.Contains("expense", StringComparison.OrdinalIgnoreCase)
       || account.FsLine.Contains("cost", StringComparison.OrdinalIgnoreCase)
       || account.FsLine.Contains("payroll", StringComparison.OrdinalIgnoreCase);

static decimal LastNonZero(decimal[] values)
    => values.Reverse().FirstOrDefault(x => x != 0m);

static async Task<List<BudgetVarianceRowDto>> BuildBudgetVarianceRowsAsync(AppDbContext db, Guid organizationId, Guid reportingPeriodId, ForecastScenario? scenario, CancellationToken ct)
{
    var accounts = await db.GlAccounts
        .AsNoTracking()
        .Where(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId)
        .ToListAsync(ct);
    var growth = scenario?.RevenueGrowthPercent ?? 5m;
    var expenseGrowth = scenario?.OpexGrowthPercent ?? 3m;

    return accounts
        .GroupBy(x => string.IsNullOrWhiteSpace(x.FsLine) ? "Unmapped" : x.FsLine)
        .OrderBy(x => x.Key)
        .Select(group =>
        {
            var actual = group.Sum(x => FinancialEngine.ReadMonthlyBalances(x.MonthlyBalancesJson).Sum());
            var factor = group.Any(IsRevenueAccount) ? 1m + growth / 100m : 1m + expenseGrowth / 100m;
            var budget = actual / factor;
            var variance = actual - budget;
            return new BudgetVarianceRowDto(group.Key, decimal.Round(actual, 2), decimal.Round(budget, 2), decimal.Round(variance, 2), budget == 0m ? 0m : decimal.Round(variance / budget * 100m, 1));
        })
        .ToList();
}

static async Task<BenchmarkRollup> BuildBenchmarkRollupAsync(AppDbContext db, Guid organizationId, Guid reportingPeriodId, CancellationToken ct)
{
    var accounts = await db.GlAccounts
        .AsNoTracking()
        .Where(x => x.OrganizationId == organizationId && x.ReportingPeriodId == reportingPeriodId)
        .ToListAsync(ct);
    var revenue = accounts.Where(IsRevenueAccount).Sum(x => FinancialEngine.ReadMonthlyBalances(x.MonthlyBalancesJson).Sum());
    var expense = accounts.Where(IsExpenseAccount).Sum(x => FinancialEngine.ReadMonthlyBalances(x.MonthlyBalancesJson).Sum());
    var net = revenue - expense;
    var margin = revenue == 0m ? 0m : decimal.Round(net / revenue * 100m, 1);
    return new BenchmarkRollup(decimal.Round(revenue, 2), decimal.Round(expense, 2), decimal.Round(net, 2), margin);
}

static async Task EnsureReportTemplatesAsync(AppDbContext db, CancellationToken ct)
{
    var existing = await db.ReportTemplates.Select(x => x.Name).ToListAsync(ct);
    var existingNames = existing.ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var template in BuiltInReportTemplates().Where(x => !existingNames.Contains(x.Name)))
    {
        db.ReportTemplates.Add(new ReportTemplate
        {
            Id = Guid.NewGuid(),
            Name = template.Name,
            Category = template.Category,
            Description = template.Description,
            SectionsJson = JsonSerializer.Serialize(template.Sections),
            IsBuiltIn = true
        });
    }

    await db.SaveChangesAsync(ct);
}

static IEnumerable<(string Name, string Category, string Description, string[] Sections)> BuiltInReportTemplates()
    =>
    [
        ("Executive board pack", "Management reporting", "Monthly board package with KPI scorecard, statements, flux review, and appendix.", ["Executive Summary", "KPI Scorecard", "P&L Trend", "Balance Sheet Snapshot", "Cash Flow", "Flux Review", "Appendix"]),
        ("3-way forecast pack", "Planning", "Forward-looking package with P&L, balance sheet, cash flow, scenario comparison, and assumptions.", ["Forecast Summary", "P&L Forecast", "Balance Sheet Forecast", "Cash Flow Forecast", "Scenario Comparison", "Assumptions"]),
        ("Consolidation pack", "Consolidation", "Group reporting pack for entity drilldown, side-by-side financials, eliminations, and FX review.", ["Group Summary", "Side-by-side Financials", "Entity Drilldown", "Eliminations", "FX Rates", "Consolidated Forecast"]),
        ("Advisory dashboard", "Client portal", "Interactive dashboard for goals, alerts, non-financial drivers, and AI narrative.", ["Goals", "KPI Alerts", "Non-financial Metrics", "AI Commentary", "Action Plan"])
    ];

static string[] ReadStringArray(string json)
{
    try
    {
        return JsonSerializer.Deserialize<string[]>(json) ?? [];
    }
    catch
    {
        return [];
    }
}

static IReadOnlyList<CompetitiveFeatureGroupDto> CompetitiveFeatureGroups()
    =>
    [
        new("Reporting", "Fathom and Reach emphasize editable reports, templates, live text, scheduled delivery, branded exports, comments, and access control.", [
            new("Report editor", "Implemented", "Slide/block editor with reorder, update, history, and AI review."),
            new("Template library", "Implemented", "Built-in report templates can now be applied to packages."),
            new("Scheduled reports", "Implemented", "Distribution schedules exist with test-send tracking."),
            new("PDF/Excel exports", "Implemented", "Artifacts are generated with QA metadata."),
            new("Comments", "Implemented", "Slide and block comments can be captured, tracked, and resolved.")
        ]),
        new("Analysis", "Competitors cover KPI libraries, custom formulas, alerts, targets, non-financial metrics, benchmarking, and trend analysis.", [
            new("KPI goals and targets", "Implemented", "Editable KPI targets and status scoring."),
            new("KPI alerts", "Implemented", "Persisted threshold alerts with trigger status."),
            new("Non-financial metrics", "Implemented", "Manual datasheet-style metrics sit beside financial KPIs."),
            new("Benchmarking and ranking", "Implemented", "Entity comparison and ranking by period."),
            new("Formula builder", "Implemented", "Finance formulas evaluate arithmetic plus fs(), kpi(), and metric() references.")
        ]),
        new("Planning", "Fathom, Reach, Jirav, Float, and Syft all push scenario planning, driver forecasts, cash runway, 3-way financials, and assumptions.", [
            new("3-way forecast", "Implemented", "P&L, cash flow, and balance sheet projections are generated for up to 36 months."),
            new("Scenario planning", "Implemented", "Base/upside/downside/custom scenarios with editable drivers."),
            new("Microforecasts", "Implemented", "Scenario events model hires, renewals, and one-time cash impacts."),
            new("Budget variance", "Implemented", "Actual-vs-budget rows are generated from scenario drivers."),
            new("Daily cash forecasting", "Implemented", "Forecast cash flows can be spread into weekly or daily timing rows.")
        ]),
        new("Consolidation", "The strongest tools include eliminations, multi-system data, multi-currency, side-by-side financials, group forecasts, and entity drilldown.", [
            new("Eliminations", "Implemented", "Account-level eliminations and recurring rules exist."),
            new("Entity drilldown", "Implemented", "Ledger and mapping drilldowns are available."),
            new("Side-by-side financials", "Implemented", "Benchmarking view compares entities by period."),
            new("Group forecasts", "Implemented", "Consolidated entities can use the same 3-way planning surface."),
            new("Custom FX rates", "Implemented", "User-managed FX rate tables are persisted by entity and period.")
        ])
    ];

static IEnumerable<(string StatementType, string Section, string Name, string NormalBalance, string AiGuidance)> DefaultFsLineDefinitions()
    =>
    [
        ("IncomeStatement", "Revenue", "Revenue", "Credit", "Operating revenue earned from customers."),
        ("IncomeStatement", "Cost of Revenue", "Cost of Goods Sold", "Debit", "Direct costs tied to delivered services or products."),
        ("IncomeStatement", "Operating Expenses", "Operating Expense - Payroll", "Debit", "Salaries, wages, benefits, contractors, and payroll-related costs."),
        ("IncomeStatement", "Operating Expenses", "Operating Expense - General & Administrative", "Debit", "General overhead and administrative expenses."),
        ("IncomeStatement", "Other Income / Expense", "Other Income / Expense", "Debit", "Non-operating income, expense, interest, or one-time items."),
        ("BalanceSheet", "Assets", "Cash", "Debit", "Bank and cash equivalent accounts."),
        ("BalanceSheet", "Assets", "Accounts Receivable", "Debit", "Customer receivable balances."),
        ("BalanceSheet", "Liabilities", "Accounts Payable", "Credit", "Vendor payable balances."),
        ("BalanceSheet", "Liabilities", "Debt", "Credit", "Borrowings and financing liabilities."),
        ("BalanceSheet", "Equity", "Equity", "Credit", "Member equity, retained earnings, and owner capital.")
    ];

static string HashSecret(string value)
{
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
    return Convert.ToHexString(bytes).ToLowerInvariant();
}

static string RedactSensitive(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return value;
    }

    var redacted = value;
    foreach (var marker in new[] { "access_token", "refresh_token", "EncryptedAccessToken", "EncryptedRefreshToken", "PasswordHash", "ConnectionString", ".codex/auth.json" })
    {
        redacted = redacted.Replace(marker, "[redacted]", StringComparison.OrdinalIgnoreCase);
    }

    return redacted;
}

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
public sealed record CreateAiRunRequest(Guid? ReportPackageId, string Module, string? PromptProfile, string? Model, string? ReasoningEffort, string InputJson);
public sealed record MapAccountRequest(string FsLine, string Reason);
public sealed record UpsertFsLineDefinitionRequest(string OrganizationKey, string? StatementType, string? Section, string Name, string? NormalBalance, string? AiGuidance, int? SortOrder, bool? IsActive, string? Reason);
public sealed record EliminateAccountRequest(string Type, string Description, string Reason, bool CreateRecurringRule);
public sealed record ExportRequest(Guid ReportPackageId, bool IncludeIssues, bool IncludeAppendix);
public sealed record CreateShareLinkRequest(Guid ReportPackageId, bool RequirePassword, bool AllowDownload, DateTimeOffset? ExpiresAt, string? Password, bool DashboardOnly);
public sealed record CreateDistributionScheduleRequest(Guid ReportPackageId, string[] Recipients, string Cadence, bool IncludePdf, bool IncludeExcel, DateTimeOffset? NextRunAt);
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
public sealed record RecurringEliminationRuleRequest(Guid OrganizationId, Guid ReportingPeriodId, Guid? GlAccountId, string Type, string Description, string CriteriaJson, string Reason, bool IsActive);
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
            package.IsSourceDataStale,
            package.SourceDataStaleReason,
            package.SourceDataChangedAt,
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
