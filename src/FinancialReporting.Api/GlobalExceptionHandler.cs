using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace FinancialReporting.Api;

/// <summary>
/// RFC 7807 ProblemDetails handler. Replaces ad-hoc raw 500 + stack-trace responses with
/// a structured envelope that clients can parse and that carries a traceId for log
/// correlation. Specific exception types map to specific status codes / titles.
/// Cat 35, 36.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = MapException(exception);
        logger.LogError(exception,
            "Unhandled {Type} on {Method} {Path}: {Message}",
            exception.GetType().Name, httpContext.Request.Method, httpContext.Request.Path, exception.Message);

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = exception.Message,
            Type = $"https://httpstatuses.io/{status}",
            Instance = httpContext.Request.Path
        };
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        problem.Extensions["timestamp"] = DateTimeOffset.UtcNow.ToString("O");

        httpContext.Response.StatusCode = status;
        httpContext.Response.ContentType = "application/problem+json";
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }

    private static (int Status, string Title) MapException(Exception ex) => ex switch
    {
        UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "Unauthorized"),
        KeyNotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
        ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),
        InvalidOperationException when ex.Message.Contains("token", StringComparison.OrdinalIgnoreCase)
            => (StatusCodes.Status401Unauthorized, "Xero token failure"),
        InvalidOperationException => (StatusCodes.Status409Conflict, "Operation conflict"),
        OperationCanceledException => (StatusCodes.Status499ClientClosedRequest, "Request cancelled"),
        _ => (StatusCodes.Status500InternalServerError, "Internal server error")
    };
}
