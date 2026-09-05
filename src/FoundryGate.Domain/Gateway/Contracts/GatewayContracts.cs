using System.ComponentModel.DataAnnotations;
using FoundryGate.Domain.Config;
using FoundryGate.Domain.Constants;

namespace FoundryGate.Domain.Gateway.Contracts;

/// <summary>
/// One quota-tier product as <c>GET /api/v1/gateway/tiers</c> reports it — the tiers whose model
/// allowlists <c>/models</c> edits (#225). Same tier table the quota endpoints resolve against
/// (<c>Gateway:Tiers</c>, projected from <c>infra/main.bicep</c>'s <c>quotaTiers</c>), plus how many
/// models the tier currently permits.
/// </summary>
/// <param name="Tier">APIM product id — a <see cref="GatewayTiers"/> value.</param>
/// <param name="DisplayName">The tier's human label; falls back to <paramref name="Tier"/> when infra emitted none.</param>
/// <param name="MonthlyTokenQuota">Tokens per calendar month the tier's <c>llm-token-limit</c> policy enforces, or <see langword="null"/> for the unlimited tier.</param>
/// <param name="IsUnlimited">True when the tier carries no monthly cap (TPM smoothing only).</param>
/// <param name="AllowedModelCount">
/// Aliases the tier's <c>fg-model-map-{tier}</c> named value lists right now. <c>0</c> is a real
/// answer, not a missing one: a tier with no map permits no models at all (the allowlist fails loud,
/// by design).
/// </param>
public record GatewayTierResponse(
    string Tier,
    string DisplayName,
    long? MonthlyTokenQuota,
    bool IsUnlimited,
    int AllowedModelCount);

/// <summary>
/// A tier's model allowlist — <c>GET /api/v1/gateway/tiers/{tier}/models</c>, admin-only. The rows
/// are the tier's <c>fg-model-map-{tier}</c> named value as APIM holds it right now, read back
/// through the Management API rather than from configuration, because the named value is the thing
/// the gateway actually enforces.
/// </summary>
/// <param name="Tier">The tier this map belongs to.</param>
/// <param name="DisplayName">The tier's human label.</param>
/// <param name="Aliases">The permitted aliases, ordered by alias.</param>
public record GatewayTierModelsResponse(
    string Tier,
    string DisplayName,
    IReadOnlyList<GatewayModelAliasResponse> Aliases);

/// <summary>
/// One row of a tier's alias map, with what the control plane knows about whether it would actually
/// work. A row whose deployment is missing is still returned — hiding it would make a broken map look
/// like a smaller one — but it is flagged so the UI can say which model is about to 404.
/// </summary>
/// <param name="Alias">The virtual model name a developer pins, e.g. <c>sonnet</c>.</param>
/// <param name="DeploymentName">The real Foundry deployment it resolves to.</param>
/// <param name="Pool">The logical backend pool the request is routed at — <c>anthropic</c> or <c>openai</c> (<see cref="GatewayModelMap"/>).</param>
/// <param name="Provider">The front door the alias belongs to; a right-plan/wrong-door request is refused by the policy naming the correct base path.</param>
/// <param name="DeploymentExists">
/// True when at least one configured Foundry account has a deployment of that name. False means every
/// request for this alias reaches a backend that has never heard of it.
/// </param>
/// <param name="MissingFromAccounts">
/// Configured accounts that do <em>not</em> carry the deployment. Empty for a healthy pooled model.
/// Non-empty on an <c>anthropic</c>-pool alias is the dangerous shape: the pool fails a 429 over to
/// another region, and a region missing the deployment turns a throttle into a 404 (the contract
/// stated in <c>infra/main.bicep</c>).
/// </param>
public record GatewayModelAliasResponse(
    string Alias,
    string DeploymentName,
    string Pool,
    ModelProviderType Provider,
    bool DeploymentExists,
    IReadOnlyList<string> MissingFromAccounts);

/// <summary>
/// <c>PUT /api/v1/gateway/tiers/{tier}/models</c> body: the tier's allowlist in full. A replace, not a
/// patch — the named value is one JSON document, the map is small, and "these are the models this tier
/// may use" is the sentence an admin is actually writing. An empty list is legal and means the tier
/// permits nothing.
/// </summary>
/// <remarks>
/// An init-property record with attributes on the properties, per CONVENTIONS.md — the one placement
/// MVC model binding, <c>Validator</c> and Blazor's <c>DataAnnotationsValidator</c> all agree on.
/// </remarks>
public record ReplaceTierModelsRequest
{
    /// <summary>
    /// The aliases this tier may use. Each alias may appear once; the service refuses duplicates
    /// (case-insensitively) rather than letting list order decide which deployment wins.
    /// </summary>
    [Required]
    public IList<TierModelAliasRequest> Aliases { get; init; } = [];
}

/// <summary>One entry of a <see cref="ReplaceTierModelsRequest"/>.</summary>
public record TierModelAliasRequest
{
    /// <summary>The virtual model name, lower-case and url-safe (<see cref="GatewayModelMap.AliasPattern"/>).</summary>
    [Required]
    [StringLength(ValidationConstants.FoundryDeploymentNameMaxLength, MinimumLength = 1)]
    [RegularExpression(GatewayModelMap.AliasPattern)]
    public string Alias { get; init; } = string.Empty;

    /// <summary>The Foundry deployment it resolves to. Must exist in a configured account — in <em>every</em> configured account for an <c>anthropic</c>-pool alias.</summary>
    [Required]
    [StringLength(ValidationConstants.FoundryDeploymentNameMaxLength, MinimumLength = 1)]
    [RegularExpression(ValidationConstants.FoundryDeploymentNamePattern)]
    public string DeploymentName { get; init; } = string.Empty;

    /// <summary>The logical backend pool (<c>anthropic</c> or <c>openai</c>); the service resolves it to the real APIM backend id exactly as the bicep does.</summary>
    [Required]
    [StringLength(ValidationConstants.FoundryDeploymentNameMaxLength, MinimumLength = 1)]
    public string Pool { get; init; } = GatewayModelMap.AnthropicPool;

    /// <summary>Which front door the alias belongs to.</summary>
    public ModelProviderType Provider { get; init; }
}
