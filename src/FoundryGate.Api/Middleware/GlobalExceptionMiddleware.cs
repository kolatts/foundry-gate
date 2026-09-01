using FoundryGate.Domain.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FoundryGate.Api.Middleware;

/// <summary>
/// The single global exception handler (CONVENTIONS.md §Configuration &amp; auth: "Exceptions
/// → HTTP via one <c>IExceptionHandler</c> + ProblemDetails (404/400/403/409 mapping), not
/// per-controller try/catch"). Registered via <c>AddExceptionHandler&lt;GlobalExceptionHandler&gt;()</c>
/// + <c>app.UseExceptionHandler()</c>; unlike a handler that falls through for unmapped
/// exceptions, this one always handles (returns <c>true</c>) so every unhandled exception —
/// mapped or not — gets the same ProblemDetails shape and correlation ID header. Unmapped
/// exceptions fall back to <c>500</c>.
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

        var (statusCode, title) = exception switch
        {
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden"),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred"),
        };

        var correlationId = httpContext.TraceIdentifier;
        httpContext.Response.Headers["X-Correlation-Id"] = correlationId;

        if (statusCode == StatusCodes.Status500InternalServerError)
        {
            logger.LogError(exception, "Unhandled exception. CorrelationId: {CorrelationId}", correlationId);
        }
        else
        {
            logger.LogWarning(
                exception,
                "{Title} ({StatusCode}). CorrelationId: {CorrelationId}",
                title,
                statusCode,
                correlationId);
        }

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
