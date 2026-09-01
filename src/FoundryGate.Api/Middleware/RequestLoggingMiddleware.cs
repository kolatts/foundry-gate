using System.Diagnostics;

namespace FoundryGate.Api.Middleware;

/// <summary>
/// Structured request logging (issue #26): logs method, path, status code, and elapsed time
/// for every request via <see cref="ILogger"/> — CONVENTIONS.md rules out Serilog for this
/// project, so OpenTelemetry's ASP.NET Core instrumentation (when enabled) carries tracing,
/// and this middleware carries plain log-based visibility even when OpenTelemetry is off
/// (e.g. local dev).
/// </summary>
public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    /// <summary>Times the downstream pipeline and logs the outcome once it completes.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            logger.LogInformation(
                "{Method} {Path} responded {StatusCode} in {ElapsedMilliseconds}ms",
                context.Request.Method,
                context.Request.Path,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds);
        }
    }
}
