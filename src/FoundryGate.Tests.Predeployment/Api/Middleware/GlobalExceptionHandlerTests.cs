using FoundryGate.Api.Middleware;
using FoundryGate.Domain.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoundryGate.Tests.Predeployment.Api.Middleware;

/// <summary>
/// The exception→status map is FoundryGate.Api's only error-handling surface
/// (CONVENTIONS.md: "one IExceptionHandler + ProblemDetails ... not per-controller
/// try/catch"), so every mapped exception type is worth pinning down here.
/// </summary>
public class GlobalExceptionHandlerTests
{
    [Theory]
    [InlineData(typeof(KeyNotFoundException), StatusCodes.Status404NotFound)]
    [InlineData(typeof(ArgumentException), StatusCodes.Status400BadRequest)]
    [InlineData(typeof(ConflictException), StatusCodes.Status409Conflict)]
    [InlineData(typeof(UnauthorizedAccessException), StatusCodes.Status403Forbidden)]
    [InlineData(typeof(InvalidOperationException), StatusCodes.Status500InternalServerError)]
    public async Task TryHandleAsync_maps_exception_type_to_expected_status_code(Type exceptionType, int expectedStatusCode)
    {
        var handler = CreateHandler();
        var context = CreateHttpContext();
        var exception = (Exception)Activator.CreateInstance(exceptionType, "test message")!;

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(expectedStatusCode, context.Response.StatusCode);
    }

    [Fact]
    public async Task TryHandleAsync_always_returns_true_even_for_unmapped_exceptions()
    {
        var handler = CreateHandler();
        var context = CreateHttpContext();

        var handled = await handler.TryHandleAsync(context, new InvalidOperationException("boom"), CancellationToken.None);

        Assert.True(handled);
    }

    [Fact]
    public async Task TryHandleAsync_echoes_TraceIdentifier_as_the_correlation_id_header()
    {
        var handler = CreateHandler();
        var context = CreateHttpContext();
        context.TraceIdentifier = "trace-123";

        await handler.TryHandleAsync(context, new InvalidOperationException("boom"), CancellationToken.None);

        Assert.Equal("trace-123", context.Response.Headers["X-Correlation-Id"].ToString());
    }

    [Fact]
    public async Task TryHandleAsync_writes_ProblemDetails_body_with_the_exception_message()
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
