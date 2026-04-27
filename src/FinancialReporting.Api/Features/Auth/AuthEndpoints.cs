using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FinancialReporting.Api.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace FinancialReporting.Api.Features.Auth;

/// <summary>
/// /api/auth/* — issue dev tokens and report the active principal. Cat 29, 41.
/// In Development the dev-token endpoint is open; in non-Development it requires an Admin
/// caller (so a deployed environment can still mint tokens for an out-of-band identity flow
/// without exposing the issuer to anonymous callers).
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app, IHostEnvironment env)
    {
        app.MapPost("/api/auth/dev-token", (DevTokenRequest request, HttpContext http, JwtIssuerOptions opts, IConfiguration config) =>
        {
            if (!env.IsDevelopment() && !EndpointHelpers.Can(http, "Admin"))
            {
                return Results.Forbid();
            }
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, request.Sub ?? "dev-user"),
                new(ClaimTypes.Name, request.Sub ?? "dev-user"),
            };
            foreach (var role in (request.Roles ?? ["Admin"]))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }
            if (request.OrganizationId is { } orgId && orgId != Guid.Empty)
            {
                claims.Add(new Claim("org", orgId.ToString()));
            }
            if (!string.IsNullOrWhiteSpace(request.XeroTenants))
            {
                claims.Add(new Claim("xero_tenants", request.XeroTenants));
            }
            var creds = new SigningCredentials(opts.SigningKey, SecurityAlgorithms.HmacSha256);
            var ttl = TimeSpan.FromMinutes(config.GetValue("Auth:TokenLifetimeMinutes", 60));
            var token = new JwtSecurityToken(opts.Issuer, opts.Audience, claims, expires: DateTime.UtcNow.Add(ttl), signingCredentials: creds);
            var encoded = new JwtSecurityTokenHandler().WriteToken(token);
            return Results.Ok(new { token = encoded, expiresIn = (int)ttl.TotalSeconds, tokenType = "Bearer" });
        });

        app.MapGet("/api/auth/whoami", (HttpContext http) =>
        {
            var actor = EndpointHelpers.Actor(http);
            var role = EndpointHelpers.Role(http);
            var orgClaim = http.User?.FindFirst("org")?.Value;
            return Results.Ok(new { actor, role, organizationId = orgClaim, authenticated = http.User?.Identity?.IsAuthenticated == true });
        });

        return app;
    }
}

public sealed record DevTokenRequest(string? Sub, string[]? Roles, Guid? OrganizationId, string? XeroTenants);
