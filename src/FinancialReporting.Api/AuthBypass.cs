namespace FinancialReporting.Api;

/// SECURITY: gate for the Admin-without-headers fallback used by Can()/Actor()/Role().
/// Set once at startup from app.Environment.IsDevelopment(). True only in Development.
/// In any other environment, missing X-FR-Role / X-FR-User headers must produce 401, not Admin.
/// See Cat 41 in docs/superpowers/specs/2026-04-27-best-in-class-review/.
internal static class AuthBypass
{
    internal static bool AllowDevAdminBypass { get; set; }
}
