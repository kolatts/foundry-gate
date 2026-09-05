namespace FoundryGate.Domain.Constants;

/// <summary>
/// The naming contract between <c>infra/modules/ai-gateway.bicep</c> and the control plane for the
/// per-tier model alias map (#86, plans/25). The map is the allowlist: one APIM named value per
/// quota-tier product, holding <c>{ alias: { deployment, backend, provider } }</c>, which the tier's
/// product policy hands to <c>infra/policies/model-alias-fragment.xml</c>. The control plane edits
/// those named values through the APIM Management API, which is only safe if it spells the names and
/// the backend ids exactly as the bicep does — hence these constants rather than string literals in
/// a service.
/// </summary>
/// <remarks>
/// <c>pool</c> and <c>backend</c> are two views of the same choice: the bicep parameter takes a
/// logical <c>pool</c> (<c>anthropic</c> / <c>openai</c>) and renders it into the real APIM backend
/// id before writing the named value, because the policy's <c>set-backend-service</c> needs the id.
/// <see cref="BackendForPool"/> and <see cref="PoolForBackend"/> are that same mapping, in both
/// directions, so a value written by the API and a value written by a deploy are indistinguishable.
/// </remarks>
public static class GatewayModelMap
{
    /// <summary>Prefix of the per-tier named value; the tier's APIM product id completes it.</summary>
    public const string NamedValuePrefix = "fg-model-map-";

    /// <summary>The multi-region Anthropic backend pool's APIM backend id (<c>ai-gateway.bicep</c>'s <c>anthropicPoolName</c>).</summary>
    public const string AnthropicPoolBackend = "foundry-anthropic-pool";

    /// <summary>Prefix of the single-account OpenAI backend id (<c>ai-gateway.bicep</c>'s <c>openaiBackendName</c>); the primary Foundry account name completes it.</summary>
    public const string OpenAiBackendPrefix = "foundry-openai-";

    /// <summary>Logical pool name for the multi-region Anthropic pool — a request routed here may land in any pool member.</summary>
    public const string AnthropicPool = "anthropic";

    /// <summary>Logical pool name for the primary account's OpenAI backend — single region, no failover.</summary>
    public const string OpenAiPool = "openai";

    /// <summary>
    /// The alias names the gateway accepts: lower-case, starting with a letter or digit, and made of
    /// characters that need no escaping in a request body or a URL. Aliases travel in a JSON
    /// <c>model</c> field and are compared by the policy without normalization, so mixed case would
    /// mean <c>Sonnet</c> silently missing an allowlist that lists <c>sonnet</c>.
    /// </summary>
    public const string AliasPattern = "^[a-z0-9][a-z0-9._-]*$";

    /// <summary>Both logical pools, in the order the UI offers them.</summary>
    public static readonly IReadOnlyList<string> Pools = [AnthropicPool, OpenAiPool];

    /// <summary>The named value holding <paramref name="tierProductId"/>'s alias map.</summary>
    public static string NamedValueName(string tierProductId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tierProductId);

        return NamedValuePrefix + tierProductId.ToLowerInvariant();
    }

    /// <summary>
    /// The APIM backend id a logical pool routes at, resolved exactly as the bicep resolves it:
    /// <c>openai</c> → the primary account's OpenAI backend, anything else → the Anthropic pool
    /// (the bicep's own <c>pool == 'openai' ? … : …</c>).
    /// </summary>
    /// <param name="pool">A logical pool name (<see cref="Pools"/>).</param>
    /// <param name="primaryFoundryAccountName">The first entry of <c>Gateway:FoundryAccountNames</c> — the account the OpenAI backend points at.</param>
    public static string BackendForPool(string pool, string primaryFoundryAccountName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryFoundryAccountName);

        return string.Equals(pool, OpenAiPool, StringComparison.OrdinalIgnoreCase)
            ? OpenAiBackendPrefix + primaryFoundryAccountName
            : AnthropicPoolBackend;
    }

    /// <summary>
    /// The logical pool a stored backend id came from — the inverse of <see cref="BackendForPool"/>,
    /// so a map written by a deploy reads back into the same vocabulary the UI edits in. An
    /// unrecognized backend id reads as <see cref="AnthropicPool"/>, matching the bicep's own
    /// "anything that is not openai is the pool" default.
    /// </summary>
    public static string PoolForBackend(string? backend) =>
        backend is not null && backend.StartsWith(OpenAiBackendPrefix, StringComparison.OrdinalIgnoreCase)
            ? OpenAiPool
            : AnthropicPool;
}
