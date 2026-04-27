using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;

namespace FinancialReporting.Api.Features.Health;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", (IConfiguration config) => Results.Ok(new
        {
            status = "ok",
            database = "SQLite",
            aiRunner = config.GetValue("Ai:UseMockRunner", true) ? "mock" : "codex-cli"
        }));

        return app;
    }
}
