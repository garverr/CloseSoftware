using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;

namespace FinancialReporting.Api;

/// <summary>
/// Tenant / organization scope accessor used by EF global query filters. When an authenticated
/// principal carries an `org` claim, the EF filters scope every query to that organization.
/// In Development with no auth (header bypass), CurrentOrganizationId returns null and the
/// filters are a no-op. Cat 45.
/// </summary>
public interface IOrganizationContext
{
    /// <summary>Active organization id, or null when filtering should be a no-op.</summary>
    Guid? CurrentOrganizationId { get; }

    /// <summary>Active tenant ids the caller can see, or null when filtering should be a no-op.</summary>
    IReadOnlyCollection<string>? AllowedTenantIds { get; }
}

/// <summary>
/// No-op organization context used when no auth principal is present (e.g. Development
/// header bypass). EF query filters resolve to "no filter" against this context.
/// </summary>
public sealed class NullOrganizationContext : IOrganizationContext
{
    public Guid? CurrentOrganizationId => null;
    public IReadOnlyCollection<string>? AllowedTenantIds => null;
}

/// <summary>
/// Reads the active organization from the authenticated JWT principal's `org` claim and
/// allowed tenant ids from `xero_tenants` (space-separated). Development header bypasses
/// still return null so local data is unfiltered. Authenticated callers without org scope
/// fail closed by returning Guid.Empty unless they carry an explicit PlatformAdmin role.
/// </summary>
public sealed class HttpContextOrganizationContext(IHttpContextAccessor accessor) : IOrganizationContext
{
    public Guid? CurrentOrganizationId
    {
        get
        {
            var user = accessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                return null;
            }
            if (IsPlatformAdmin(user))
            {
                return null;
            }
            var raw = user.FindFirst("org")?.Value;
            return Guid.TryParse(raw, out var id) ? id : Guid.Empty;
        }
    }

    public IReadOnlyCollection<string>? AllowedTenantIds
    {
        get
        {
            var user = accessor.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true)
            {
                return null;
            }
            if (IsPlatformAdmin(user))
            {
                return null;
            }
            var raw = user.FindFirst("xero_tenants")?.Value;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [];
            }
            return raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }

    private static bool IsPlatformAdmin(ClaimsPrincipal user)
        => user.FindAll(ClaimTypes.Role).Any(x => string.Equals(x.Value, "PlatformAdmin", StringComparison.OrdinalIgnoreCase))
           || user.FindAll("role").Any(x => string.Equals(x.Value, "PlatformAdmin", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Captured at startup so endpoints/services can issue dev tokens.</summary>
public sealed record JwtIssuerOptions(SymmetricSecurityKey SigningKey, string Issuer, string Audience);
