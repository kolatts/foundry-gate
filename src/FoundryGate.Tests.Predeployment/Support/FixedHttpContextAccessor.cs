using Microsoft.AspNetCore.Http;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// <see cref="IHttpContextAccessor"/> that holds exactly the context it was given. Not the
/// framework's <see cref="HttpContextAccessor"/>: that one keeps its context in a <em>static</em>
/// <c>AsyncLocal</c>, so several instances created in one test all observe whichever context was
/// set last — which makes "admin vs developer vs other role" side-by-side assertions silently test
/// the same principal three times.
/// </summary>
public sealed class FixedHttpContextAccessor(HttpContext? httpContext) : IHttpContextAccessor
{
    /// <inheritdoc />
    public HttpContext? HttpContext { get; set; } = httpContext;
}
