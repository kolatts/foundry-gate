using FoundryGate.Domain.Exceptions;

namespace FoundryGate.Core.Entra;

/// <summary>
/// The <see cref="IEntraDirectoryClient"/> registered when <c>Entra:Enabled</c> is <c>false</c>
/// (the default — local dev, the integration-test host, a fork that hasn't granted Graph roles yet).
/// Every method throws <see cref="FeatureNotConfiguredException"/>, which <c>GlobalExceptionHandler</c>
/// maps to <c>503</c> with the message below as the ProblemDetails detail — so an admin who calls
/// <c>POST /users/sync</c> or <c>POST /groups/{id}/sync-entra</c> on such a host reads exactly which
/// setting to flip and which Graph roles to grant, rather than a bare 500.
/// </summary>
/// <remarks>
/// Why 503 and not 400: the request is not malformed and there is nothing the caller can change about
/// it — directory sync is an optional, externally-addressed feature that this host has not been
/// configured for, which is exactly what CONVENTIONS.md's <see cref="FeatureNotConfiguredException"/>
/// rule covers (<c>FoundryDeploymentService</c> is the precedent). It also keeps "this host has no
/// Graph" distinguishable from a real caller error such as "this group has no Entra link", which is a
/// genuine <c>400</c>. This class predates that rule and returned <c>400</c> until #152.
/// </remarks>
public sealed class DisabledEntraDirectoryClient : IEntraDirectoryClient
{
    /// <summary>The detail every caller sees.</summary>
    public const string Message =
        "Entra directory sync is disabled on this host (Entra:Enabled is false). Set Entra:Enabled to true and grant the " +
        "API identity the Microsoft Graph application roles Application.Read.All, User.Read.All and GroupMember.ReadBasic.All " +
        "before calling the sync endpoints.";

    /// <inheritdoc />
    public Task<EntraUser?> GetUserAsync(string objectId, CancellationToken cancellationToken) => throw Disabled();

    /// <inheritdoc />
    public Task<EntraAssignedUsers> ListAssignedUsersAsync(CancellationToken cancellationToken) => throw Disabled();

    /// <inheritdoc />
    public IAsyncEnumerable<string> ListGroupMemberIdsAsync(string groupObjectId, bool transitive, CancellationToken cancellationToken) => throw Disabled();

    private static FeatureNotConfiguredException Disabled() => new(Message);
}
