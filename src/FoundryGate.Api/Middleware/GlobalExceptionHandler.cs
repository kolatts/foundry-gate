using FoundryGate.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FoundryGate.Api.Middleware;

/// <summary>
/// The single global exception handler (CONVENTIONS.md §Configuration &amp; auth: "Exceptions
/// → HTTP via one <c>IExceptionHandler</c> + ProblemDetails (404/400/403/409 mapping), not
/// per-controller try/catch"). Registered via <c>AddExceptionHandler&lt;GlobalExceptionHandler&gt;()</c>
/// + <c>app.UseExceptionHandler()</c>. Mapped exceptions get a specific status and their own
/// <see cref="Exception.Message"/> as the ProblemDetails <c>Detail</c> — those messages are
/// written by application code specifically to be shown to a caller. Unmapped exceptions are
/// logged with full detail but return <c>false</c> (imagile-app's <c>ApiExceptionHandler</c>
/// fall-through pattern), so ASP.NET Core's own <c>AddProblemDetails()</c> writes the generic,
/// message-free <c>500</c> body — an unmapped exception's <see cref="Exception.Message"/> is
/// never guaranteed safe to put on the wire (stack traces, connection strings, internal paths).
/// </summary>
/// <remarks>
/// Mapping: <see cref="KeyNotFoundException"/> → 404, <see cref="ArgumentException"/> → 400,
/// <see cref="ConflictException"/> → 409, <see cref="UnauthorizedAccessException"/> → 403.
/// </remarks>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var correlationId = httpContext.TraceIdentifier;
        httpContext.Response.Headers["X-Correlation-Id"] = correlationId;

        (int StatusCode, string Title)? mapping = exception switch
        {
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden"),
            _ => null,
        };

        if (mapping is null)
        {
            // Unmapped: log with full detail, but never echo exception.Message onto the wire.
            // Falling through (returning false) hands the response to ASP.NET Core's own
            // AddProblemDetails() default, which writes a generic 500 with no Detail.
            logger.LogError(exception, "Unhandled exception. CorrelationId: {CorrelationId}", correlationId);
            return false;
        }

        var (statusCode, title) = mapping.Value;
        logger.LogWarning(
            exception,
            "{Title} ({StatusCode}). CorrelationId: {CorrelationId}",
            title,
            statusCode,
            correlationId);

        httpContext.Response.StatusCode = statusCode;
        var problemDetails = new ProblemDetails
        {
            Title = title,
            Status = statusCode,
            Detail = exception.Message,
            Instance = httpContext.Request.Path,
        };
        problemDetails.Extensions["correlationId"] = correlationId;

        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);
        return true;
    }
}
