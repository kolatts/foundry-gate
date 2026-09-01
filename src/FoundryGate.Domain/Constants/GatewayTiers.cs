namespace FoundryGate.Domain.Constants;

/// <summary>
/// APIM product ids for the gateway's quota tiers. Quota tiers are APIM <em>products</em>, not
/// per-user numbers, because APIM's <c>token-quota</c> accepts literals only — policy expressions
/// are rejected (CLAUDE.md ground truth, validated live 2026-09-01) — so a single policy cannot
/// read a per-developer budget. Each tier is a product carrying its own rendered
/// <c>llm-token-limit</c> policy, and the control plane sets a developer's quota by issuing their
/// APIM subscription against the matching tier product (#82). Supersedes the single-product model
/// behind <see cref="SystemConfigurationKeys.ApimProductId"/>.
/// </summary>
/// <remarks>
/// These values MUST match the <c>name</c> of each entry in <c>infra/main.bicep</c>'s
/// <c>quotaTiers</c> parameter (surfaced as the deployment's <c>productIds</c> output). Renaming a
/// tier in one place without the other silently breaks subscription provisioning against the
/// gateway; <c>GatewayTiersTests</c> in FoundryGate.Tests.Predeployment cross-checks the two.
/// </remarks>
public static class GatewayTiers
{
    /// <summary>Everyday agent usage tier (5M tokens/month, 20K TPM as shipped in <c>infra/main.bicep</c>).</summary>
    public const string Standard = "standard";

    /// <summary>Heavy agentic workloads tier (20M tokens/month, 40K TPM as shipped in <c>infra/main.bicep</c>).</summary>
    public const string Power = "power";

    /// <summary>No gateway-enforced monthly budget; burst smoothing only. Monthly oversight is the control plane's job.</summary>
    public const string Unlimited = "unlimited";

    /// <summary>
    /// The tier a newly provisioned developer lands on. Mirrors the bicep <c>defaultProductId</c>
    /// output (<c>quotaTiers[0].name</c>), so the first entry in the bicep array must stay
    /// <see cref="Standard"/>.
    /// </summary>
    public const string Default = Standard;

    /// <summary>Every tier product id, in the same order as <c>infra/main.bicep</c>'s <c>quotaTiers</c>.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Standard,
        Power,
        Unlimited,
    ];
}
