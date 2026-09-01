using System.ComponentModel.DataAnnotations;

namespace FoundryGate.Api.Configuration;

/// <summary>
/// Addressing for the gateway data plane this control plane manages, bound from the
/// <c>Gateway</c> section (issue #108). Infra sets these on the Container App as
/// <c>Gateway__SubscriptionId</c>, <c>Gateway__ResourceGroup</c>, <c>Gateway__ApimName</c>,
/// <c>Gateway__KeyEncryptionKeyUri</c>, <c>Gateway__FoundryAccountNames__{i}</c>, …
/// (infra/modules/control-plane.bicep is the source of truth for the key names) so nobody types
/// ARM resource ids into <c>SystemConfiguration</c> by hand. Optional as a whole: absent locally,
/// where there is no gateway to manage — features that need it (<c>/foundry/*</c>, <c>/keys/*</c>)
/// fail with a clear <c>503</c> rather than the whole host refusing to start; outside <c>local</c>
/// each feature's registration fails startup when its members are missing.
/// </summary>
/// <remarks>
/// One class for every gateway-addressed feature. <see cref="SubscriptionId"/> and
/// <see cref="ResourceGroup"/> are the ARM scope shared by APIM (#36/#37) and Foundry (#61); each
/// feature declares which further members it needs (<see cref="IsApimConfigured"/>,
/// <see cref="IsFoundryConfigured"/>) and <see cref="Validate"/> only insists on the shared pair when a
/// feature-specific member is present. The quota tiers (#127, <c>Tiers</c>) and the reconciliation
/// workspace (#84, <c>LogAnalyticsWorkspaceId</c>) land here too.
/// </remarks>
public class GatewayOptions : IValidatableObject
{
    /// <summary>Azure subscription id the gateway resource group lives in.</summary>
    public string? SubscriptionId { get; set; }

    /// <summary>Resource group holding the gateway (<c>rg-foundrygate-{env}</c>).</summary>
    public string? ResourceGroup { get; set; }

    /// <summary>APIM service name — the short name, not the ARM id (<c>Gateway__ApimName</c>). Required for subscription-key management (#36/#37).</summary>
    public string? ApimName { get; set; }

    /// <summary>
    /// Versionless Key Vault key URI (<c>https://{vault}.vault.azure.net/keys/fg-apim-key-encryption</c>)
    /// of the RSA key that wraps APIM subscription keys before they are stored (#95;
    /// <c>Gateway__KeyEncryptionKeyUri</c>). Versionless so a Key Vault key rotation needs no redeploy —
    /// the key protector resolves the current version per wrap and each stored envelope records the
    /// version that wrapped it. Required when <see cref="KeyProtectionOptions.Provider"/> is
    /// <see cref="KeyProtectionProviderType.KeyVault"/> (checked at startup by the key protector factory).
    /// </summary>
    public string? KeyEncryptionKeyUri { get; set; }

    /// <summary>
    /// Foundry (Cognitive Services <c>AIServices</c>) account names in APIM backend-pool order —
    /// index 0 is the primary region, the rest are pool members (infra/main.bicep
    /// <c>foundryRegions</c>). The API manages deployments in exactly these accounts and no others.
    /// </summary>
    public List<string> FoundryAccountNames { get; set; } = [];

    /// <summary>
    /// <see langword="true"/> when APIM subscription-key management can address the management plane:
    /// subscription, resource group and APIM service name are all present.
    /// </summary>
    public bool IsApimConfigured =>
        !string.IsNullOrWhiteSpace(SubscriptionId)
        && !string.IsNullOrWhiteSpace(ResourceGroup)
        && !string.IsNullOrWhiteSpace(ApimName);

    /// <summary>
    /// <see langword="true"/> when Foundry deployment management can address ARM: subscription,
    /// resource group and at least one account are all present.
    /// </summary>
    public bool IsFoundryConfigured =>
        !string.IsNullOrWhiteSpace(SubscriptionId)
        && !string.IsNullOrWhiteSpace(ResourceGroup)
        && FoundryAccountNames.Count > 0;

    /// <summary>
    /// Fail-fast on a <em>partial</em> configuration: a feature-specific member (<see cref="ApimName"/>,
    /// <see cref="FoundryAccountNames"/>) without the shared <see cref="SubscriptionId"/> /
    /// <see cref="ResourceGroup"/> can never be resolved to ARM resources and is a deployment mistake
    /// worth refusing to start over. The shared pair on its own is fine (another feature may own it),
    /// and an entirely absent section is fine. <see cref="KeyEncryptionKeyUri"/>, when present, must be
    /// an absolute <c>https</c> URI.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
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
    }
}
