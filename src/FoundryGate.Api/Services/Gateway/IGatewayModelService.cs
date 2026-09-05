using FoundryGate.Domain.Gateway.Contracts;

namespace FoundryGate.Api.Services.Gateway;

/// <summary>
/// The gateway's per-tier model allowlist (#86, plans/25; #225 made it editable). Each quota-tier
/// product carries an APIM named value <c>fg-model-map-{tier}</c> holding
/// <c>{ alias: { deployment, backend, provider } }</c>; the tier's product policy hands it to the
/// <c>fg-model-alias</c> fragment, which rewrites the alias to the real deployment, routes at the
/// mapped backend, and refuses anything the map does not list with <c>403 model_not_permitted</c>.
/// <b>The map is the allowlist</b> — there is no second list — so editing it is how an admin grants or
/// removes a model, and it takes effect without redeploying a policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the named value and not configuration.</b> <c>GatewayOptions.ModelAliases</c> carries the
/// same map, flattened, for the developer-facing <c>GET /users/me</c> panel — but it is a snapshot of
/// what the last <em>deploy</em> wrote. What the gateway enforces is the named value, so every read
/// here goes to APIM. (That the two can now diverge until the next deploy is tracked separately.)
/// </para>
/// <para>
/// <b>Validation refuses maps that would 404 rather than 403.</b> An alias must be lower-case and
/// url-safe, unique within the tier, and point at a deployment the configured Foundry accounts
/// actually have — and for an <c>anthropic</c>-pool alias, at one that <em>every</em> account has:
/// the pool fails a 429 over to another region, so a region missing the deployment turns a throttle
/// into a 404 (the contract stated in <c>infra/main.bicep</c>). That check needs the Foundry accounts,
/// so both reads and the write require Foundry addressing as well as APIM addressing; without it the
/// service would be answering "is this map safe?" with a guess.
/// </para>
/// <para>
/// <b>Commit point.</b> <see cref="ReplaceTierModelsAsync"/> resolves the caller and does every
/// refusal before touching APIM; once APIM has accepted the new named value the audit row and save
/// run on <see cref="CancellationToken.None"/>, per CONVENTIONS.md.
/// </para>
/// </remarks>
public interface IGatewayModelService
{
    /// <summary>
    /// The gateway's quota tiers with the size of each one's current allowlist — what the
    /// <c>/models</c> page lists down its left-hand side. Tier identity and caps come from the same
    /// <c>Gateway:Tiers</c> table quota resolution uses; the counts come from APIM.
    /// </summary>
    /// <exception cref="Domain.Exceptions.FeatureNotConfiguredException">APIM is not configured (503).</exception>
    Task<IReadOnlyList<GatewayTierResponse>> ListTiersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One tier's allowlist as APIM holds it, with each row flagged for whether its deployment
    /// actually exists in the configured Foundry accounts.
    /// </summary>
    /// <param name="tier">A quota-tier product id (<c>Domain.Constants.GatewayTiers</c>).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">No such tier (404).</exception>
    /// <exception cref="Domain.Exceptions.FeatureNotConfiguredException">APIM or Foundry is not configured (503).</exception>
    Task<GatewayTierModelsResponse> GetTierModelsAsync(string tier, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a tier's allowlist in full and audits <c>gateway.models.updated</c> with the whole map
    /// before and after. A request that resolves to the map already stored is a no-op: no APIM call, no
    /// audit row (an audit trail claiming a change that did not happen is worse than silence).
    /// </summary>
    /// <param name="tier">A quota-tier product id.</param>
    /// <param name="request">The tier's complete allowlist. An empty list is legal and permits nothing.</param>
    /// <param name="cancellationToken">Cancellation token — not observed past the point APIM accepts.</param>
    /// <exception cref="KeyNotFoundException">No such tier (404).</exception>
    /// <exception cref="ArgumentException">A malformed alias, a duplicate alias, an unknown pool, or a deployment the accounts do not have (400).</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">APIM refused the write (409).</exception>
    /// <exception cref="UnauthorizedAccessException">The caller has no <c>User</c> row (403 — call <c>GET /users/me</c> first).</exception>
    /// <exception cref="Domain.Exceptions.FeatureNotConfiguredException">APIM or Foundry is not configured (503).</exception>
    Task<GatewayTierModelsResponse> ReplaceTierModelsAsync(string tier, ReplaceTierModelsRequest request, CancellationToken cancellationToken);
}
