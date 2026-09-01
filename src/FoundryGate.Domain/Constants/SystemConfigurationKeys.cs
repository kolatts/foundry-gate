namespace FoundryGate.Domain.Constants;

/// <summary>
/// The eight <c>SystemConfiguration</c> keys seeded on first deploy (plans/02-data-layer.md
/// #23). Shared between the Data-layer seeder, the Api's config endpoints, and any
/// service that reads a configuration value by key, so the key string is never
/// duplicated as a magic literal.
/// </summary>
public static class SystemConfigurationKeys
{
    /// <summary>Per-user fallback monthly token budget (quota resolution precedence, spec &#167;3.2 step 5).</summary>
    public const string DefaultMonthlyTokenQuota = nameof(DefaultMonthlyTokenQuota);

    /// <summary>ARM resource ID of the APIM instance.</summary>
    public const string ApimResourceId = nameof(ApimResourceId);

    /// <summary>APIM gateway base URL shown to developers on <c>/me</c> (see <see cref="Config.Contracts.GatewayConnectionInfo"/>).</summary>
    public const string ApimGatewayUrl = nameof(ApimGatewayUrl);

    /// <summary>
    /// <b>Legacy (single-product model).</b> APIM product name covering the Foundry routes (spec
    /// &#167;5.1), from before quota tiers became per-tier APIM products. Superseded by
    /// <see cref="GatewayTiers"/>: a developer's subscription is issued against the product for
    /// their tier (<see cref="GatewayTiers.Default"/> for new users), not against this one value.
    /// Kept because the seeded reference data references it; do not remove without a migration.
    /// </summary>
    public const string ApimProductId = nameof(ApimProductId);

    /// <summary>ARM resource ID of the Azure AI Foundry account.</summary>
    public const string FoundryResourceId = nameof(FoundryResourceId);

    /// <summary>Azure AD tenant ID used for Microsoft Graph sync (spec &#167;7).</summary>
    public const string EntraTenantId = nameof(EntraTenantId);

    /// <summary>"true" | "false" — whether Entra group sync (spec &#167;7.3) is active.</summary>
    public const string EntraGroupSyncEnabled = nameof(EntraGroupSyncEnabled);

    /// <summary>Day of month the monthly reset fires; always "1" for v1 (spec &#167;6).</summary>
    public const string ResetDayOfMonth = nameof(ResetDayOfMonth);

    /// <summary>All seeded keys, for iteration in seeders and completeness tests.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        DefaultMonthlyTokenQuota,
        ApimResourceId,
        ApimGatewayUrl,
        ApimProductId,
        FoundryResourceId,
        EntraTenantId,
        EntraGroupSyncEnabled,
        ResetDayOfMonth,
    ];
}
