using FoundryGate.Core.Quota;
using FoundryGate.Data.Entities;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// Hand-rolled <see cref="IGatewayTierSyncActor"/> that always answers with the same user (or
/// <see langword="null"/> for the system), so a tier-sync test can pick the attribution it is
/// asserting on without standing up the Api's <c>ICurrentUserAccessor</c> and a claims principal.
/// CONVENTIONS.md: no mocking library.
/// </summary>
public sealed class FixedGatewayTierSyncActor(User? actor) : IGatewayTierSyncActor
{
    /// <summary>When set, resolution throws it instead of answering — the Api's "caller has no User row → 403" shape.</summary>
    public Exception? Throws { get; set; }

    /// <inheritdoc />
    public Task<User?> ResolveActorAsync(CancellationToken cancellationToken) =>
        Throws is { } exception ? throw exception : Task.FromResult(actor);
}
