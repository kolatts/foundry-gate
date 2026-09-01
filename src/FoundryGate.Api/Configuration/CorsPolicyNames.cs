namespace FoundryGate.Api.Configuration;

/// <summary>Named CORS policies registered in <c>Program.cs</c>.</summary>
public static class CorsPolicyNames
{
    /// <summary>Applied to every controller (see <c>AddControllers</c> in <c>Program.cs</c>) —
    /// scopes the Blazor WASM UI's cross-origin allowance to <c>/api/v1</c> without opening
    /// up <c>/health</c> or the OpenAPI document.</summary>
    public const string Api = "Api";
}
