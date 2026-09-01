namespace FoundryGate.Api.Configuration;

/// <summary>Named CORS policies registered in <c>Program.cs</c>.</summary>
public static class CorsPolicyNames
{
    /// <summary>The Blazor WASM UI's cross-origin allowance. Applied globally via
    /// <c>app.UseCors(CorsPolicyNames.Api)</c> in <c>Program.cs</c> — <c>Microsoft.AspNetCore
    /// .Cors.EnableCorsAttribute</c> isn't usable as an <c>MvcOptions.Filters</c> entry the way
    /// <c>AuthorizeFilter</c> is, so this isn't scoped to <c>/api/v1</c> controllers only. In
    /// practice this is equivalent today: <c>/api/v1</c> is the only browser-facing surface,
    /// and CORS is a browser-enforced allow-list, not a server-side authorization boundary, so
    /// <c>/health</c> and the OpenAPI document carrying the same policy doesn't expose
    /// anything. Revisit if a future route needs a genuinely different origin policy.</summary>
    public const string Api = "Api";
}
