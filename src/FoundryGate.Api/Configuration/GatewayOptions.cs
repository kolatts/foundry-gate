using System.ComponentModel.DataAnnotations;

namespace FoundryGate.Api.Configuration;

/// <summary>
/// Addressing for the gateway data plane this control plane manages, bound from the
/// <c>Gateway</c> section (issue #108). Infra sets these on the Container App as
/// <c>Gateway__SubscriptionId</c>, <c>Gateway__ResourceGroup</c>, <c>Gateway__FoundryAccountNames__{i}</c>, …
/// (infra/modules/control-plane.bicep is the source of truth for the key names) so nobody types
/// ARM resource ids into <c>SystemConfiguration</c> by hand. Optional as a whole: absent locally,
/// where there is no gateway to manage — features that need it (<c>/foundry/*</c>) fail with a
/// clear message rather than the whole host refusing to start.
/// </summary>
/// <remarks>
/// Only the members the Foundry deployment service (#61) reads are modelled so far; the APIM
/// key service (#36/#37) and reconciliation function (#84) add theirs as they land, in this class.
/// </remarks>
public class GatewayOptions : IValidatableObject
{
    /// <summary>Azure subscription id the gateway resource group lives in.</summary>
    public string? SubscriptionId { get; set; }

    /// <summary>Resource group holding the gateway (<c>rg-foundrygate-{env}</c>).</summary>
    public string? ResourceGroup { get; set; }

    /// <summary>
    /// Foundry (Cognitive Services <c>AIServices</c>) account names in APIM backend-pool order —
    /// index 0 is the primary region, the rest are pool members (infra/main.bicep
    /// <c>foundryRegions</c>). The API manages deployments in exactly these accounts and no others.
    /// </summary>
    public List<string> FoundryAccountNames { get; set; } = [];

    /// <summary>
    /// <see langword="true"/> when Foundry deployment management can address ARM: subscription,
    /// resource group and at least one account are all present.
    /// </summary>
    public bool IsFoundryConfigured =>
        !string.IsNullOrWhiteSpace(SubscriptionId)
        && !string.IsNullOrWhiteSpace(ResourceGroup)
        && FoundryAccountNames.Count > 0;

    /// <summary>
    /// Fail-fast on a <em>partial</em> Foundry configuration: account names without a subscription
    /// or resource group can never be resolved to ARM resources, and a half-set section is a
    /// deployment mistake worth refusing to start over (an entirely absent section is fine).
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (FoundryAccountNames.Count == 0)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(SubscriptionId))
        {
            yield return new ValidationResult(
                $"{nameof(SubscriptionId)} is required when {nameof(FoundryAccountNames)} is set.",
                [nameof(SubscriptionId)]);
        }

        if (string.IsNullOrWhiteSpace(ResourceGroup))
        {
            yield return new ValidationResult(
                $"{nameof(ResourceGroup)} is required when {nameof(FoundryAccountNames)} is set.",
                [nameof(ResourceGroup)]);
        }

        if (FoundryAccountNames.Any(string.IsNullOrWhiteSpace))
        {
            yield return new ValidationResult(
                $"{nameof(FoundryAccountNames)} must not contain blank entries.",
                [nameof(FoundryAccountNames)]);
        }
    }
}
