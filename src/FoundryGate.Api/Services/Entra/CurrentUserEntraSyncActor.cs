using FoundryGate.Api.Services.Identity;
using FoundryGate.Core.Entra;
using FoundryGate.Data.Entities;

namespace FoundryGate.Api.Services.Entra;

/// <summary>
/// The Api's <see cref="IEntraSyncActor"/>: a directory sync run belongs to the admin who called
/// <c>POST /users/sync</c> or <c>POST /groups/sync-entra</c>, so its audit row is attributed to them
/// and a caller with no <c>User</c> row is refused with the same 403 every other Api path uses.
/// </summary>
/// <remarks>
/// Never returns <see langword="null"/> — on this host there is always a caller. The scheduled
/// counterpart is Core's <see cref="SystemEntraSyncActor"/>, which always does (#151).
/// </remarks>
public sealed class CurrentUserEntraSyncActor(ICurrentUserAccessor currentUser) : IEntraSyncActor
{
    /// <inheritdoc />
    public async Task<User?> ResolveActorAsync(CancellationToken cancellationToken) =>
        await currentUser.GetRequiredUserAsync(cancellationToken);
}
