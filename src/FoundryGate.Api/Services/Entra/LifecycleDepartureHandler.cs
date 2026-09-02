using FoundryGate.Api.Services.Lifecycle;
using FoundryGate.Core.Entra;
using FoundryGate.Data.Entities;

namespace FoundryGate.Api.Services.Entra;

/// <summary>
/// The Api's <see cref="IDepartureHandler"/>: a departure the sync found goes through plan 21's one
/// orchestrator, exactly as it did before the sync moved to Core (#151). No behaviour of its own —
/// the whole point of the seam is that this host keeps the single pipeline
/// <see cref="IUserLifecycleService"/> owns, rather than growing a second copy of it.
/// </summary>
public sealed class LifecycleDepartureHandler(IUserLifecycleService lifecycle) : IDepartureHandler
{
    /// <inheritdoc />
    public Task HandleAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        return lifecycle.DeprovisionAsync(DeprovisionTrigger.EntraDeparture, user.UserId, cancellationToken);
    }
}
