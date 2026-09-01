using FoundryGate.Data.Entities;

namespace FoundryGate.Api.Services.Quota;

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
/// Called <em>before</em> the caller's <c>SaveChangesAsync</c>: if the gateway move fails, the request
/// fails and the database never claims a tier the gateway is not enforcing (the reverse — DB saved,
/// gateway not moved — would be a silent under- or over-enforcement). Implementations must therefore
/// be idempotent: a retry, or a call for a subscription already on the target product, is a no-op.
/// </para>
/// <para>
/// The real implementation (<c>ApimGatewayTierSync</c>, #118) lands with the users/lifecycle wave
/// alongside the APIM key service; until then <see cref="NullGatewayTierSync"/> is registered.
/// </para>
/// </remarks>
public interface IGatewayTierSync
{
    /// <summary>Ensures <paramref name="user"/>'s APIM subscription is scoped to the <paramref name="tierProductId"/> product.</summary>
    /// <param name="user">The user whose subscription moves; <see cref="User.ApimSubscriptionId"/> is non-empty when this is called.</param>
    /// <param name="tierProductId">Target tier product — one of <see cref="Domain.Constants.GatewayTiers.All"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SyncAsync(User user, string tierProductId, CancellationToken cancellationToken);
}
