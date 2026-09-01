using FoundryGate.Api.Middleware;
using FoundryGate.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoundryGate.Tests.Predeployment.Api.Middleware;

/// <summary>
/// The exception→status map is FoundryGate.Api's only error-handling surface
/// (CONVENTIONS.md: "one IExceptionHandler + ProblemDetails ... not per-controller
/// try/catch"), so every mapped exception type — and the unmapped fall-through path — is
/// worth pinning down here.
/// </summary>
public class GlobalExceptionHandlerTests
{
    [Theory]
    [InlineData(typeof(KeyNotFoundException), StatusCodes.Status404NotFound)]
    [InlineData(typeof(ArgumentException), StatusCodes.Status400BadRequest)]
    [InlineData(typeof(ConflictException), StatusCodes.Status409Conflict)]
    [InlineData(typeof(UnauthorizedAccessException), StatusCodes.Status403Forbidden)]
    [InlineData(typeof(FeatureNotConfiguredException), StatusCodes.Status503ServiceUnavailable)]
    public async Task TryHandleAsync_maps_known_exception_type_to_expected_status_code(Type exceptionType, int expectedStatusCode)
    {
        var handler = CreateHandler();
        var context = CreateHttpContext();
        var exception = (Exception)Activator.CreateInstance(exceptionType, "test message")!;

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(expectedStatusCode, context.Response.StatusCode);
    }

    [Fact]
    public async Task TryHandleAsync_returns_false_for_unmapped_exceptions_so_the_default_handler_finishes()
    {
        // Mirrors imagile-app's ApiExceptionHandler fall-through pattern: an unmapped
        // exception isn't ours to shape a body for -- returning false hands the response to
        // ASP.NET Core's own AddProblemDetails() default (generic 500, no Detail).
        var handler = CreateHandler();
        var context = CreateHttpContext();

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException("boom"), CancellationToken.None);

        Assert.False(handled);
    }

    [Fact]
    public async Task TryHandleAsync_never_writes_an_unmapped_exceptions_message_onto_the_wire()
    {
        // The Major review finding this test exists for: an unmapped exception's Message can
        // carry connection strings, stack details, or other internals that must never reach a
        // caller. Returning false without touching the response body is what makes that true --
        // asserted two ways: the body the handler itself wrote is empty, and (redundantly, as
        // the concrete regression guard) it doesn't contain the sensitive text.
        var handler = CreateHandler();
        var context = CreateHttpContext();

        var handled = await handler.TryHandleAsync(
            context,
            new InvalidOperationException("secret detail"),
            CancellationToken.None);

        Assert.False(handled);
        Assert.Equal(0, context.Response.Body.Length);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();

        Assert.DoesNotContain("secret detail", body);
    }

    [Fact]
    public async Task TryHandleAsync_echoes_TraceIdentifier_as_the_correlation_id_header_even_when_unmapped()
    {
        var handler = CreateHandler();
        var context = CreateHttpContext();
        context.TraceIdentifier = "trace-123";

        await handler.TryHandleAsync(context, new InvalidOperationException("boom"), CancellationToken.None);

        Assert.Equal("trace-123", context.Response.Headers["X-Correlation-Id"].ToString());
    }

    [Fact]
    public async Task TryHandleAsync_writes_ProblemDetails_body_with_the_mapped_exceptions_message()
    {
        var handler = CreateHandler();
        var context = CreateHttpContext();

        await handler.TryHandleAsync(context, new KeyNotFoundException("user 42 not found"), CancellationToken.None);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();

        Assert.Contains("user 42 not found", body);
        Assert.Contains("404", body);
    }

    private static GlobalExceptionHandler CreateHandler() =>
        new(NullLogger<GlobalExceptionHandler>.Instance);

    private static DefaultHttpContext CreateHttpContext() =>
        new() { Response = { Body = new MemoryStream() } };
}
