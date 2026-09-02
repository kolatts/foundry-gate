using FoundryGate.Data.Entities;

namespace FoundryGate.Core.Quota;

/// <summary>
/// The seam between quota resolution and the gateway: puts a developer's APIM subscription on the
/// tier product their quota resolved to. Quota tiers are APIM products because <c>token-quota</c>
/// accepts literals only (#82), so "set this user's quota" at the gateway means "move their
/// subscription to another product" — and that is the one thing resolution needs the APIM Management
/// plane for. <see cref="QuotaResolutionService"/> calls this whenever the resolved tier for a user
/// with a non-empty <see cref="User.ApimSubscriptionId"/> differs from the tier on their most recent
/// allocation (or no earlier allocation is known).
/// </summary>
/// <remarks>
/// <para>
/// Called <em>before</em> the caller's <c>SaveChangesAsync</c>: if the gateway move fails, the caller
/// discards the allocation it staged and the database never claims a tier the gateway is not enforcing
/// (the reverse — DB saved, gateway not moved — would be a silent under- or over-enforcement).
/// Implementations must therefore be idempotent: a retry, or a call for a subscription already on the
/// target product, changes nothing at the gateway.
/// </para>
/// <para>
/// <b>Idempotent is not the same as silent.</b> "Already on the target product" has two causes, and
/// <c>previousTierProductId</c> is what separates them: the database agreeing with the
/// gateway (nothing to record), versus the database still recording an older tier — an earlier move
/// that APIM accepted and whose save did not land. The second must still write its audit row, or the
/// allocation is quietly updated past a gateway change nothing recorded (#211 review).
/// </para>
/// <para>
/// The seam and both implementations live in Core because quota resolution does: since #194 the real
/// one (<see cref="ApimGatewayTierSync"/>, #118) composes <see cref="Gateway.IApimManagementClient"/>
/// directly rather than the Api's key service, so <em>both</em> hosts register it whenever
/// <c>GatewayOptions.IsApimConfigured</c> — the Api for a request-time quota change, the Functions
/// host for a tier the monthly reset discovers. A host with no gateway to address registers
/// <see cref="NullGatewayTierSync"/>.
/// </para>
/// </remarks>
public interface IGatewayTierSync
{
    /// <summary>Ensures <paramref name="user"/>'s APIM subscription is scoped to the <paramref name="tierProductId"/> product.</summary>
    /// <param name="user">The user whose subscription moves; <see cref="User.ApimSubscriptionId"/> is non-empty when this is called.</param>
    /// <param name="tierProductId">Target tier product — one of <see cref="Domain.Constants.GatewayTiers.All"/>.</param>
    /// <param name="previousTierProductId">
    /// The tier the <em>database</em> last recorded for this user (the caller's
    /// <see cref="QuotaResolution.PreviousTierProductId"/>), or <see langword="null"/> when none is
    /// known. It is what lets an implementation tell the two ways a subscription can already be sitting
    /// on the target product apart: the database agreeing with the gateway (a true no-op, stay silent),
    /// versus the database still recording something else — an earlier move that reached APIM whose save
    /// did not land. The second is the residual orphan CONVENTIONS.md's commit-point rule describes, and
    /// it must still be audited, or the move is recorded nowhere and the row is quietly updated past it.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SyncAsync(User user, string tierProductId, string? previousTierProductId, CancellationToken cancellationToken);
}
