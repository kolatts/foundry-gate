namespace FoundryGate.Domain.Constants;

/// <summary>
/// Entra ID App Role names used in <c>[Authorize(Roles = ...)]</c> attributes
/// (spec &#167;4: "The admin role is an Entra App Role defined in the app registration").
/// </summary>
public static class RoleNames
{
    /// <summary>Grants access to every admin-only endpoint across the API surface.</summary>
    public const string Admin = "FoundryGate.Admin";
}
