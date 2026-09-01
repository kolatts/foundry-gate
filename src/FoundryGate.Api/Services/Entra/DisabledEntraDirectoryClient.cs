namespace FoundryGate.Api.Services.Entra;

/// <summary>
/// The <see cref="IEntraDirectoryClient"/> registered when <c>Entra:Enabled</c> is <c>false</c>
/// (the default — local dev, the integration-test host, a fork that hasn't granted Graph roles yet).
/// Every method throws <see cref="ArgumentException"/>, which <c>GlobalExceptionHandler</c> maps to
/// <c>400</c> with the message below as the ProblemDetails detail — so an admin who calls
/// <c>POST /users/sync</c> on such a host reads exactly which setting to flip and which Graph roles
/// to grant, rather than a bare 500.
/// </summary>
/// <remarks>
/// Why 400 and not 503/501: the exception handler maps exactly four exception types by design
/// (CONVENTIONS.md), and this wave adds none. A dedicated "feature disabled" mapping can come
/// later if more optional features need it; for now the request <em>is</em> invalid on this host,
/// and the message tells the caller why.
/// </remarks>
public sealed class DisabledEntraDirectoryClient : IEntraDirectoryClient
{
    /// <summary>The detail every caller sees.</summary>
    public const string Message =
        "Entra directory sync is disabled on this host (Entra:Enabled is false). Set Entra:Enabled to true and grant the " +
        "API identity the Microsoft Graph application roles User.Read.All, GroupMember.Read.All and Application.Read.All " +
        "(or set Entra:ServicePrincipalObjectId) before calling the sync endpoints.";

    /// <inheritdoc />
    public Task<EntraUser?> GetUserAsync(string objectId, CancellationToken cancellationToken) => throw Disabled();

    /// <inheritdoc />
    public IAsyncEnumerable<EntraUser> ListAssignedUsersAsync(CancellationToken cancellationToken) => throw Disabled();

    /// <inheritdoc />
    public IAsyncEnumerable<string> ListGroupMemberIdsAsync(string groupObjectId, bool transitive, CancellationToken cancellationToken) => throw Disabled();

    private static ArgumentException Disabled() => new(Message);
}
