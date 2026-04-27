using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FinancialReporting.Api.Data;
using FinancialReporting.Api.Domain;
using FinancialReporting.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FinancialReporting.Api.Common;

/// <summary>
/// Cross-cutting endpoint helpers extracted from Program.cs so feature-folder endpoint files
/// can call them without duplication. Cat 29.
/// Auth: Can / Actor / Role honor a JWT principal first, then a Development header bypass.
/// Audit: AuditAsync / AddVersionAndAuditAsync stamp every state-changing endpoint with the
/// resolved actor + role + redacted before/after JSON snapshots.
/// </summary>
public static class EndpointHelpers
{
    public static bool Can(HttpContext http, params string[] roles)
    {
        if (http.User?.Identity?.IsAuthenticated == true)
        {
            var claimRoles = http.User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
            return claimRoles.Any(role => roles.Contains(role, StringComparer.OrdinalIgnoreCase));
        }

        if (!AuthBypass.AllowDevAdminBypass)
        {
            return false;
        }

        var roleHeader = http.Request.Headers["X-FR-Role"].FirstOrDefault();
        string[] activeRoles;
        if (string.IsNullOrWhiteSpace(roleHeader))
        {
            activeRoles = ["Admin"];
        }
        else
        {
            activeRoles = roleHeader.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
        return activeRoles.Any(role => roles.Contains(role, StringComparer.OrdinalIgnoreCase));
    }

    public static string Actor(HttpContext http)
    {
        if (http.User?.Identity?.IsAuthenticated == true)
        {
            return http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                   ?? http.User.FindFirst("sub")?.Value
                   ?? http.User.Identity.Name
                   ?? "authenticated";
        }
        if (AuthBypass.AllowDevAdminBypass)
        {
            var supplied = http.Request.Headers["X-FR-User"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(supplied))
            {
                return supplied;
            }
        }
        return AuthBypass.AllowDevAdminBypass ? "dev-admin" : "anonymous";
    }

    public static string Role(HttpContext http)
    {
        if (http.User?.Identity?.IsAuthenticated == true)
        {
            return http.User.FindFirst(ClaimTypes.Role)?.Value ?? "User";
        }
        if (AuthBypass.AllowDevAdminBypass)
        {
            var supplied = http.Request.Headers["X-FR-Role"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(supplied))
            {
                return supplied;
            }
        }
        return AuthBypass.AllowDevAdminBypass ? "Admin" : "Anonymous";
    }

    public static IResult? RejectIfApproved(ReportPackage package)
        => package.IsApproved
            ? Results.Conflict(new
            {
                error = "Package is approved and locked. Unapprove it before making changes.",
                package.ApprovedBy,
                package.ApprovedAt,
                package.ApprovedVersionId
            })
            : null;

    public static async Task<IResult?> RejectIfPackageApprovedAsync(AppDbContext db, Guid packageId, CancellationToken ct)
    {
        var package = await db.ReportPackages
            .AsNoTracking()
            .Where(x => x.Id == packageId)
            .Select(x => new ReportPackage
            {
                Id = x.Id,
                IsApproved = x.IsApproved,
                ApprovedBy = x.ApprovedBy,
                ApprovedAt = x.ApprovedAt,
                ApprovedVersionId = x.ApprovedVersionId
            })
            .FirstOrDefaultAsync(ct);
        return package is null ? Results.NotFound() : RejectIfApproved(package);
    }

    public static async Task<IResult?> RejectIfFluxGroupPackageApprovedAsync(AppDbContext db, Guid groupId, CancellationToken ct)
    {
        var package = await db.FluxReviewGroups
            .AsNoTracking()
            .Where(x => x.Id == groupId)
            .Join(db.ReportPackages.AsNoTracking(), group => group.ReportPackageId, package => package.Id, (_, package) => new ReportPackage
            {
                Id = package.Id,
                IsApproved = package.IsApproved,
                ApprovedBy = package.ApprovedBy,
                ApprovedAt = package.ApprovedAt,
                ApprovedVersionId = package.ApprovedVersionId
            })
            .FirstOrDefaultAsync(ct);
        return package is null ? Results.NotFound() : RejectIfApproved(package);
    }

    public static Task AuditAsync(
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

    public static async Task AddVersionAndAuditAsync(
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

    public static string HashSecret(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool TryParsePeriodKey(string periodKey, out int year, out int month)
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

    public static ReportingPeriod BuildReportingPeriod(int year, int month, bool isClosed)
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

    public static string BuildBaseFrom(ReportingPeriod period)
        => period.PeriodStart.AddMonths(-1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    public static string RedactSensitive(string value)
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
}
