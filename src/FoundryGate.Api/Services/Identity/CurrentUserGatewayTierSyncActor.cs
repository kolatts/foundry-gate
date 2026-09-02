using FoundryGate.Core.Quota;
using FoundryGate.Data.Entities;

namespace FoundryGate.Api.Services.Identity;

/// <summary>
/// The Api's <see cref="IGatewayTierSyncActor"/>: a tier move made while serving a request belongs to
/// the caller who made it — the admin who changed a quota, approved a request or activated a user —
/// so <c>key.tier-changed</c> is attributed to them rather than to "the system".
/// </summary>
/// <remarks>
/// <see cref="ICurrentUserAccessor.GetRequiredUserAsync"/>, not <c>TryGetUserAsync</c>: an
/// authenticated caller with no <c>User</c> row is a 403 (CONVENTIONS.md), and resolving it here —
/// before <see cref="ApimGatewayTierSync"/> touches ARM — is what keeps that refusal from arriving
/// after the subscription has already moved. It is cheap: the accessor caches the row for the rest of
/// the request.
/// </remarks>
public sealed class CurrentUserGatewayTierSyncActor(ICurrentUserAccessor currentUser) : IGatewayTierSyncActor
{
    /// <inheritdoc />
    public async Task<User?> ResolveActorAsync(CancellationToken cancellationToken) =>
        await currentUser.GetRequiredUserAsync(cancellationToken);
}
