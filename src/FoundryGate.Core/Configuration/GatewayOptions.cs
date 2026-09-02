using System.ComponentModel.DataAnnotations;
using FoundryGate.Domain.Config;
using FoundryGate.Domain.Constants;

namespace FoundryGate.Core.Configuration;

/// <summary>
/// Everything the control plane knows about the gateway data plane it manages, bound from the
/// <c>Gateway</c> section (issue #108): ARM addressing set by infra on the Container App
/// <em>and</em> the Function App as <c>Gateway__SubscriptionId</c>, <c>Gateway__ResourceGroup</c>,
/// <c>Gateway__ApimName</c>, <c>Gateway__LogAnalyticsWorkspaceId</c>,
/// <c>Gateway__KeyEncryptionKeyUri</c>, <c>Gateway__FoundryAccountNames__{i}</c>, …
/// (infra/modules/control-plane.bicep is the source of truth for the key names) so nobody types
/// ARM resource ids into <c>SystemConfiguration</c> by hand — and the quota <see cref="Tiers"/>
/// (<c>Gateway__Tiers__{i}__*</c>, issue #32 / D-013 / #201), which every allocation is resolved against.
/// </summary>
/// <remarks>
/// <para>
/// Lives in Core, not the Api (#119): both hosts bind this same section — the Api for the APIM key
/// service and Foundry deployments, the Functions host for quota resolution's tier table and the
/// reconciliation workspace — and one section deserves one class. Each host's own
/// <c>Configuration/AppSettings.cs</c> carries a <c>Gateway</c> property of this type.
/// </para>
/// <para>
/// The <em>addressing</em> members are optional as a whole: absent locally, where there is no
/// gateway to manage — features that need them (<c>/foundry/*</c>, <c>/keys/*</c>) fail with a clear
/// <c>503</c> rather than the whole host refusing to start; outside <c>local</c> each feature's
/// registration fails startup when its members are missing. <see cref="SubscriptionId"/> and
/// <see cref="ResourceGroup"/> are the ARM scope shared by APIM (#36/#37) and Foundry (#61); each
/// feature declares which further members it needs (<see cref="IsApimConfigured"/>,
/// <see cref="IsFoundryConfigured"/>) and <see cref="Validate"/> only insists on the shared pair when a
/// feature-specific member is present.
/// </para>
/// <para>
/// <see cref="Tiers"/> is <em>always</em> required — quota resolution has no meaning without it. On a
/// deployed host it comes from infra as <c>Gateway__Tiers__{i}__ProductId</c> / <c>__DisplayName</c> /
/// <c>__MonthlyTokenQuota</c>, projected by <c>infra/modules/control-plane.bicep</c> from the very
/// <c>quotaTiers</c> parameter that creates the APIM products and renders their <c>llm-token-limit</c>
/// policies (#201) — one source, so a fork that overrides <c>quotaTiers</c> at deploy time cannot leave
/// the control plane validating quotas against caps the gateway has never heard of. Neither host ships
/// the table in <c>appsettings.json</c> any more; each carries it in <c>appsettings.local.json</c>, for
/// the <c>local</c> environment where there is no gateway to emit anything
/// (<c>GatewayOptionsTiersTests</c> cross-checks that file against the bicep defaults). The tiers are
/// deliberately <em>not</em> C# defaults either: the configuration binder appends configured list items
/// to a pre-populated list rather than replacing it, so C# defaults plus a fork's override would
/// silently produce duplicate tiers. <c>ValidateRecursively()</c> does not recurse into list items, so
/// <see cref="Validate"/> checks each <see cref="GatewayTier"/> itself.
/// </para>
/// </remarks>
public class GatewayOptions : IValidatableObject
{
    /// <summary>
    /// Path segment of the gateway's Anthropic Messages front door, as
    /// <c>infra/modules/ai-gateway.bicep</c> creates it (<c>anthropicApiPath</c>). A constant rather
    /// than a setting because the bicep hard-codes it: making it configurable here would let the
    /// control plane hand developers a path the data plane does not serve. If it ever becomes a
    /// bicep parameter, it becomes a <c>Gateway__*</c> env var at the same time (#153).
    /// </summary>
    public const string AnthropicBasePath = "/anthropic";

    /// <summary>Path segment of the gateway's OpenAI Responses front door (<c>infra/modules/ai-gateway.bicep</c>'s <c>openaiApiPath</c>). See <see cref="AnthropicBasePath"/>.</summary>
    public const string OpenAiBasePath = "/openai/v1";

    /// <summary>Azure subscription id the gateway resource group lives in.</summary>
    public string? SubscriptionId { get; set; }

    /// <summary>Resource group holding the gateway (<c>rg-foundrygate-{env}</c>).</summary>
    public string? ResourceGroup { get; set; }

    /// <summary>APIM service name — the short name, not the ARM id (<c>Gateway__ApimName</c>). Required for subscription-key management (#36/#37).</summary>
    public string? ApimName { get; set; }

    /// <summary>
    /// The gateway's public origin (<c>https://apim-foundrygate-{env}.azure-api.net</c>, or a fork's
    /// custom domain) — infra already sets it on the control plane as <c>Gateway__ApimGatewayUrl</c>
    /// (<c>infra/modules/control-plane.bicep</c>, from the APIM module's <c>gatewayUrl</c> output). It is
    /// what <c>GET /users/me</c> hands a developer as <c>cliConfig.gatewayBaseUrl</c> (#28), so it is an
    /// address for humans and CLIs, not an ARM id: absent locally, where there is no gateway, in which
    /// case <c>/users/me</c> returns an empty base URL rather than inventing one.
    /// </summary>
    public string? ApimGatewayUrl { get; set; }

    /// <summary>
    /// Versionless Key Vault key URI (<c>https://{vault}.vault.azure.net/keys/fg-apim-key-encryption</c>)
    /// of the RSA key that wraps APIM subscription keys before they are stored (#95;
    /// <c>Gateway__KeyEncryptionKeyUri</c>). Versionless so a Key Vault key rotation needs no redeploy —
    /// the key protector resolves the current version per wrap and each stored envelope records the
    /// version that wrapped it. Required when the Api's <c>KeyProtection:Provider</c> is
    /// <c>KeyVault</c> (checked at startup by the key protector factory).
    /// </summary>
    public string? KeyEncryptionKeyUri { get; set; }

    /// <summary>
    /// The Log Analytics workspace <b>GUID</b> — what the query API calls the "workspace id"
    /// (<c>Gateway__LogAnalyticsWorkspaceId</c>, from the workspace's <c>properties.customerId</c>;
    /// <c>infra/modules/monitoring.bicep</c>). This is the value
    /// <c>LogsQueryClient.QueryWorkspaceAsync</c> takes, and the only gateway setting the usage
    /// reconciliation job (#39/#84) needs: absent, the job logs and no-ops rather than failing the host,
    /// because a fork without the GenAI diagnostic setting has nothing to reconcile against.
    /// </summary>
    public string? LogAnalyticsWorkspaceId { get; set; }

    /// <summary>
    /// ARM resource id of the same workspace (<c>Gateway__LogAnalyticsWorkspaceResourceId</c>) — for
    /// <c>QueryResourceAsync</c> and anything management-plane. Bound so nothing has to reconstruct it;
    /// nothing reads it yet.
    /// </summary>
    public string? LogAnalyticsWorkspaceResourceId { get; set; }

    /// <summary>
    /// Foundry (Cognitive Services <c>AIServices</c>) account names in APIM backend-pool order —
    /// index 0 is the primary region, the rest are pool members (infra/main.bicep
    /// <c>foundryRegions</c>). The API manages deployments in exactly these accounts and no others.
    /// </summary>
    public List<string> FoundryAccountNames { get; set; } = [];

    /// <summary>
    /// The gateway's quota tier products and their monthly token caps
    /// (<c>Gateway__Tiers__{i}__ProductId</c> / <c>__DisplayName</c> / <c>__MonthlyTokenQuota</c> from
    /// infra, <c>Gateway:Tiers</c> in <c>appsettings.local.json</c>). A
    /// developer's monthly budget <em>is</em> one of these tiers (D-013): every numeric quota the control
    /// plane accepts must equal a configured cap or be unlimited, because APIM's <c>token-quota</c> is a
    /// per-product literal (#82) and the tier product a subscription sits on is what the gateway enforces.
    /// Any order — resolution sorts finite tiers by cap itself.
    /// </summary>
    [Required]
    public List<GatewayTier> Tiers { get; set; } = [];

    /// <summary>
    /// The gateway's model alias map, flattened one row per (tier, alias) — the same
    /// <c>productModelAliases</c> object <c>infra/modules/ai-gateway.bicep</c> turns into policy
    /// (<c>infra/policies/model-alias-fragment.xml</c>), emitted to both hosts by
    /// <c>infra/modules/control-plane.bicep</c> as <c>Gateway__ModelAliases__{i}__Tier</c> /
    /// <c>__Alias</c> / <c>__DeploymentName</c> / <c>__Provider</c> (#153). One source, two consumers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why flattened rather than nested by tier.</b> The map genuinely differs per product — the
    /// allowlist <em>is</em> the alias map (#86), so an alias the caller's tier does not list is a
    /// <c>403 model_not_permitted</c> at the gateway. Handing every developer the union would tell a
    /// Standard developer they can use <c>opus</c> and let them find out at the first request, which
    /// is worse than telling them nothing. <c>GET /users/me</c> therefore filters with
    /// <see cref="AliasesForTier"/>.
    /// </para>
    /// <para>
    /// Empty when infra has not emitted it — a fork on an older deploy, or the local shape — in which
    /// case <c>GET /users/me</c> returns an empty alias list exactly as it did before and developers
    /// read model names from the CLI setup docs.
    /// </para>
    /// </remarks>
    public List<GatewayModelAlias> ModelAliases { get; set; } = [];

    /// <summary>
    /// <see langword="true"/> when APIM subscription-key management can address the management plane:
    /// subscription, resource group and APIM service name are all present.
    /// </summary>
    public bool IsApimConfigured =>
        !string.IsNullOrWhiteSpace(SubscriptionId)
        && !string.IsNullOrWhiteSpace(ResourceGroup)
        && !string.IsNullOrWhiteSpace(ApimName);

    /// <summary>
    /// <see langword="true"/> when the usage reconciliation job (#39/#84) has a workspace to query.
    /// Nothing else is needed: the Log Analytics query API is addressed by workspace GUID, and the
    /// Functions identity's Log Analytics Reader assignment is what authorizes it.
    /// </summary>
    public bool IsUsageReconciliationConfigured => !string.IsNullOrWhiteSpace(LogAnalyticsWorkspaceId);

    /// <summary>
    /// The aliases <paramref name="tierProductId"/>'s product permits, ordered by alias — what a
    /// developer on that tier may put in <c>ANTHROPIC_DEFAULT_*_MODEL</c> / Codex's <c>model</c>.
    /// Empty for an unknown tier or an unconfigured map, never null.
    /// </summary>
    /// <param name="tierProductId">A <see cref="GatewayTiers"/> product id.</param>
    public IReadOnlyList<GatewayModelAlias> AliasesForTier(string tierProductId) =>
        string.IsNullOrWhiteSpace(tierProductId)
            ? []
            : [.. ModelAliases
                .Where(alias => string.Equals(alias.Tier, tierProductId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(alias => alias.Alias, StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// <see langword="true"/> when Foundry deployment management can address ARM: subscription,
    /// resource group and at least one account are all present.
    /// </summary>
    public bool IsFoundryConfigured =>
        !string.IsNullOrWhiteSpace(SubscriptionId)
        && !string.IsNullOrWhiteSpace(ResourceGroup)
        && FoundryAccountNames.Count > 0;

    /// <summary>
    /// Fail-fast rules. <b>Addressing:</b> a <em>partial</em> configuration — a feature-specific member
    /// (<see cref="ApimName"/>, <see cref="FoundryAccountNames"/>) without the shared
    /// <see cref="SubscriptionId"/> / <see cref="ResourceGroup"/> — can never be resolved to ARM resources
    /// and is a deployment mistake worth refusing to start over; the shared pair on its own is fine
    /// (another feature may own it), and entirely absent addressing is fine.
    /// <see cref="KeyEncryptionKeyUri"/>, when present, must be an absolute <c>https</c> URI.
    /// <b>Tiers:</b> at least one tier; every product id is one of <see cref="GatewayTiers.All"/> (the ids
    /// the bicep actually creates); no duplicate ids; every cap within
    /// <c>[0, ValidationConstants.MaxMonthlyTokenQuota]</c>; exactly one unlimited tier
    /// (<see cref="GatewayTier.MonthlyTokenQuota"/> = 0); and at least one finite tier for finite quotas
    /// to land on.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var result in ValidateAddressing())
        {
            yield return result;
        }

        foreach (var result in ValidateTiers())
        {
            yield return result;
        }

        foreach (var result in ValidateModelAliases())
        {
            yield return result;
        }
    }

    private IEnumerable<ValidationResult> ValidateAddressing()
    {
        var apimPresent = !string.IsNullOrWhiteSpace(ApimName);
        var foundryPresent = FoundryAccountNames.Count > 0;

        if (apimPresent || foundryPresent)
        {
            var requiredBy = string.Join(" / ", new[] { apimPresent ? nameof(ApimName) : null, foundryPresent ? nameof(FoundryAccountNames) : null }.Where(name => name is not null));

            if (string.IsNullOrWhiteSpace(SubscriptionId))
            {
                yield return new ValidationResult(
                    $"{nameof(SubscriptionId)} is required when {requiredBy} is set.",
                    [nameof(SubscriptionId)]);
            }

            if (string.IsNullOrWhiteSpace(ResourceGroup))
            {
                yield return new ValidationResult(
                    $"{nameof(ResourceGroup)} is required when {requiredBy} is set.",
                    [nameof(ResourceGroup)]);
            }
        }

        if (FoundryAccountNames.Any(string.IsNullOrWhiteSpace))
        {
            yield return new ValidationResult(
                $"{nameof(FoundryAccountNames)} must not contain blank entries.",
                [nameof(FoundryAccountNames)]);
        }

        if (!string.IsNullOrWhiteSpace(KeyEncryptionKeyUri)
            && (!Uri.TryCreate(KeyEncryptionKeyUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ValidationResult(
                $"{nameof(KeyEncryptionKeyUri)} must be an absolute https URI of a Key Vault key.",
                [nameof(KeyEncryptionKeyUri)]);
        }

        // The query API takes the workspace GUID, not the ARM id — the single most likely thing to get
        // wrong when wiring the two by hand, and it would fail 15 minutes later inside a timer trigger
        // rather than at startup. A GUID is cheap to check, so check it.
        if (!string.IsNullOrWhiteSpace(LogAnalyticsWorkspaceId) && !Guid.TryParse(LogAnalyticsWorkspaceId, out _))
        {
            yield return new ValidationResult(
                $"{nameof(LogAnalyticsWorkspaceId)} must be the workspace GUID (its properties.customerId), not the ARM resource id — that one is {nameof(LogAnalyticsWorkspaceResourceId)}.",
                [nameof(LogAnalyticsWorkspaceId)]);
        }

        // A malformed gateway URL is worth refusing to start over: every developer's CLI config would
        // otherwise be built from it and fail at the first request, far from the cause.
        if (!string.IsNullOrWhiteSpace(ApimGatewayUrl)
            && (!Uri.TryCreate(ApimGatewayUrl, UriKind.Absolute, out var gatewayUri) || gatewayUri.Scheme != Uri.UriSchemeHttps))
        {
            yield return new ValidationResult(
                $"{nameof(ApimGatewayUrl)} must be an absolute https URI of the gateway (e.g. https://apim-foundrygate-dev.azure-api.net).",
                [nameof(ApimGatewayUrl)]);
        }
    }

    private IEnumerable<ValidationResult> ValidateTiers()
    {
        if (Tiers.Count == 0)
        {
            yield return new ValidationResult(
                $"{nameof(Tiers)} must contain at least one tier. On a deployed host infra sets them as " +
                "Gateway__Tiers__0__ProductId / Gateway__Tiers__0__DisplayName / Gateway__Tiers__0__MonthlyTokenQuota (…__1__…, …) " +
                "from infra/main.bicep's quotaTiers parameter; locally they come from appsettings.local.json's Gateway:Tiers. " +
                "Neither is present here, and quota resolution has no meaning without a tier table.",
                [nameof(Tiers)]);
            yield break;
        }

        // Item-level checks live here because ValidateRecursively() stops at the list: a tier with a
        // negative cap would otherwise start fine as a finite tier no quota ever matches.
        for (var i = 0; i < Tiers.Count; i++)
        {
            var tier = Tiers[i];
            var member = $"{nameof(Tiers)}[{i}]";

            if (string.IsNullOrWhiteSpace(tier.ProductId))
            {
                yield return new ValidationResult($"{member}.{nameof(GatewayTier.ProductId)} is required.", [member]);
            }

            if (tier.MonthlyTokenQuota < 0 || tier.MonthlyTokenQuota > ValidationConstants.MaxMonthlyTokenQuota)
            {
                yield return new ValidationResult(
                    $"{member}.{nameof(GatewayTier.MonthlyTokenQuota)} = {tier.MonthlyTokenQuota} must be between 0 (unlimited) and {ValidationConstants.MaxMonthlyTokenQuota}.",
                    [member]);
            }
        }

        var unknown = Tiers
            .Select(t => t.ProductId)
            .Where(id => !GatewayTiers.All.Contains(id, StringComparer.Ordinal))
            .ToList();
        if (unknown.Count > 0)
        {
            yield return new ValidationResult(
                $"{nameof(Tiers)} names product ids the gateway does not create: {string.Join(", ", unknown)}. Valid ids (FoundryGate.Domain.Constants.GatewayTiers / infra/main.bicep quotaTiers): {string.Join(", ", GatewayTiers.All)}.",
                [nameof(Tiers)]);
        }

        var duplicates = Tiers
            .GroupBy(t => t.ProductId, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            yield return new ValidationResult(
                $"{nameof(Tiers)} lists a product id more than once: {string.Join(", ", duplicates)}.",
                [nameof(Tiers)]);
        }

        var unlimitedCount = Tiers.Count(t => t.IsUnlimited);
        if (unlimitedCount != 1)
        {
            yield return new ValidationResult(
                $"{nameof(Tiers)} must contain exactly one unlimited tier ({nameof(GatewayTier.MonthlyTokenQuota)} = 0); found {unlimitedCount}.",
                [nameof(Tiers)]);
        }

        if (Tiers.Count - unlimitedCount == 0)
        {
            yield return new ValidationResult(
                $"{nameof(Tiers)} must contain at least one finite tier ({nameof(GatewayTier.MonthlyTokenQuota)} > 0) for finite quotas to map onto.",
                [nameof(Tiers)]);
        }
    }

    /// <summary>
    /// Item-level rules for <see cref="ModelAliases"/>. <c>ValidateRecursively()</c> stops at the list,
    /// so — exactly as for <see cref="Tiers"/> — the checks live here or a blank alias would ship a row
    /// that promises a developer a model name of <c>""</c>.
    /// </summary>
    private IEnumerable<ValidationResult> ValidateModelAliases()
    {
        for (var i = 0; i < ModelAliases.Count; i++)
        {
            var alias = ModelAliases[i];
            var member = $"{nameof(ModelAliases)}[{i}]";

            if (string.IsNullOrWhiteSpace(alias.Tier))
            {
                yield return new ValidationResult($"{member}.{nameof(GatewayModelAlias.Tier)} is required.", [member]);
            }
            else if (!GatewayTiers.All.Contains(alias.Tier, StringComparer.OrdinalIgnoreCase))
            {
                // A typo here is silent otherwise: the row simply never matches a caller's tier, and the
                // developer is told their gateway has no models rather than that it is misconfigured.
                yield return new ValidationResult(
                    $"{member}.{nameof(GatewayModelAlias.Tier)} = '{alias.Tier}' is not a gateway tier product. Valid ids: {string.Join(", ", GatewayTiers.All)}.",
                    [member]);
            }

            if (string.IsNullOrWhiteSpace(alias.Alias))
            {
                yield return new ValidationResult($"{member}.{nameof(GatewayModelAlias.Alias)} is required.", [member]);
            }

            if (string.IsNullOrWhiteSpace(alias.DeploymentName))
            {
                yield return new ValidationResult($"{member}.{nameof(GatewayModelAlias.DeploymentName)} is required.", [member]);
            }
        }

        var duplicates = ModelAliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias.Tier) && !string.IsNullOrWhiteSpace(alias.Alias))
            .GroupBy(alias => $"{alias.Tier.ToLowerInvariant()}/{alias.Alias.ToLowerInvariant()}", StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            // The gateway's own map is an object keyed by alias, so two rows for one tier+alias cannot
            // have come from it — something flattened the map wrong, and which deployment wins would be
            // decided by list order.
            yield return new ValidationResult(
                $"{nameof(ModelAliases)} lists the same tier/alias more than once: {string.Join(", ", duplicates)}.",
                [nameof(ModelAliases)]);
        }
    }
}

/// <summary>One quota tier: an APIM product id, its display name, and the monthly cap its <c>llm-token-limit</c> policy enforces (<c>Gateway:Tiers[i]</c>).</summary>
public class GatewayTier
{
    /// <summary>APIM product id — one of <see cref="GatewayTiers.All"/> (the <c>name</c> of a bicep <c>quotaTiers</c> entry).</summary>
    [Required]
    [StringLength(64)]
    public string ProductId { get; set; } = string.Empty;

    /// <summary>Human-readable name for the UI (bicep's <c>displayName</c>); falls back to <see cref="ProductId"/> when empty.</summary>
    [StringLength(100)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Tokens per calendar month the tier's policy enforces; <c>0</c> means the tier carries no
    /// <c>token-quota</c> at all (unlimited — TPM smoothing only), matching bicep's
    /// <c>monthlyTokenQuota: 0</c> convention.
    /// </summary>
    [Range(0, ValidationConstants.MaxMonthlyTokenQuota)]
    public long MonthlyTokenQuota { get; set; }

    /// <summary>True when this is the unlimited tier (<see cref="MonthlyTokenQuota"/> = 0).</summary>
    public bool IsUnlimited => MonthlyTokenQuota == 0;
}

/// <summary>
/// One row of the gateway's model alias map (<c>Gateway:ModelAliases[i]</c>): the alias
/// <see cref="Alias"/> resolves, for developers on tier product <see cref="Tier"/>, to the Foundry
/// deployment <see cref="DeploymentName"/> behind the <see cref="Provider"/> front door.
/// </summary>
/// <remarks>
/// Flattened from <c>infra/main.bicep</c>'s <c>productModelAliases</c>
/// (<c>{ tier: { alias: { deployment, pool, provider } } }</c>), one row per tier/alias pair —
/// <c>pool</c> is not carried because it is a routing detail of the data plane and means nothing to
/// a developer configuring a CLI. Deployments rotate underneath by editing the bicep, which is why
/// the CLI panel tells developers to pin the <em>alias</em> rather than
/// <see cref="DeploymentName"/> (shown for transparency and debugging only).
/// </remarks>
public class GatewayModelAlias
{
    /// <summary>
    /// The quota tier product this alias is permitted on — one of <see cref="GatewayTiers.All"/>. The
    /// alias map is also the allowlist (#86): an alias a tier does not list is a
    /// <c>403 model_not_permitted</c> at the gateway.
    /// </summary>
    [Required]
    [StringLength(64)]
    public string Tier { get; set; } = string.Empty;

    /// <summary>The virtual model name a developer's CLI pins, e.g. <c>sonnet</c>.</summary>
    [Required]
    [StringLength(ValidationConstants.FoundryDeploymentNameMaxLength)]
    public string Alias { get; set; } = string.Empty;

    /// <summary>The Foundry deployment it currently resolves to, e.g. <c>claude-sonnet-4-5</c>.</summary>
    [Required]
    [StringLength(ValidationConstants.FoundryDeploymentNameMaxLength)]
    public string DeploymentName { get; set; } = string.Empty;

    /// <summary>
    /// Which front door the alias belongs to, so a client is told the right base path and auth header
    /// style. Bound case-insensitively from bicep's lower-case <c>provider</c>
    /// (<c>anthropic</c> / <c>openai</c>); an unrecognized value fails the binder at startup, which is
    /// the right moment to find out.
    /// </summary>
    public ModelProviderType Provider { get; set; }
}
