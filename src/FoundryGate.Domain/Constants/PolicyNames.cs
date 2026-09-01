namespace FoundryGate.Domain.Constants;

/// <summary>
/// ASP.NET Core authorization policy names registered in FoundryGate.Api's
/// <c>Program.cs</c> and referenced from <c>[Authorize(Policy = ...)]</c>.
/// </summary>
public static class PolicyNames
{
    /// <summary>Requires <see cref="RoleNames.Admin"/>. Used on every admin-only endpoint.</summary>
    public const string AdminOnly = "AdminOnly";
}
