namespace FoundryGate.Domain.Common;

/// <summary>
/// RFC 7807-shaped error envelope. FoundryGate.Api's single <c>IExceptionHandler</c>
/// (CONVENTIONS.md) serializes ASP.NET Core's <c>ProblemDetails</c> onto the wire in
/// this shape; Domain defines its own POCO mirror — rather than referencing
/// <c>Microsoft.AspNetCore.Http.ProblemDetails</c> directly — so FoundryGate.Web (which
/// references Domain only, per the Blazor WASM hard boundary) can deserialize error
/// responses without pulling an ASP.NET Core dependency into the WASM client.
/// </summary>
/// <param name="Type">A URI reference identifying the problem type (RFC 7807 <c>type</c>).</param>
/// <param name="Title">Short, human-readable summary of the problem.</param>
/// <param name="Status">The HTTP status code generated for this occurrence.</param>
/// <param name="Detail">Human-readable explanation specific to this occurrence.</param>
/// <param name="Instance">A URI reference identifying the specific occurrence (typically the request path).</param>
/// <param name="Errors">Per-field validation failures, when <see cref="Status"/> is 400.</param>
public record ApiError(
    string Type,
    string Title,
    int Status,
    string? Detail = null,
    string? Instance = null,
    IReadOnlyDictionary<string, string[]>? Errors = null);
