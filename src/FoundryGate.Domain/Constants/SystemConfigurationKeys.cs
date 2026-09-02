namespace FoundryGate.Domain.Constants;

/// <summary>
/// The <c>SystemConfiguration</c> keys seeded on first deploy (plans/02-data-layer.md
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

    /// <summary>ARM resource ID of the Azure AI Foundry account.</summary>
    public const string FoundryResourceId = nameof(FoundryResourceId);

    /// <summary>"true" | "false" — whether Entra group sync (spec &#167;7.3) is active.</summary>
    public const string EntraGroupSyncEnabled = nameof(EntraGroupSyncEnabled);

    /// <summary>Day of month the monthly reset fires; always "1" for v1 (spec &#167;6).</summary>
    public const string ResetDayOfMonth = nameof(ResetDayOfMonth);

    /// <summary>All seeded keys, for iteration in seeders and completeness tests.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        DefaultMonthlyTokenQuota,
        ApimResourceId,
        FoundryResourceId,
        EntraGroupSyncEnabled,
        ResetDayOfMonth,
    ];

    /// <summary>
    /// Keys that were once seeded and are now <b>retired</b> (#164/#123): nothing reads them, nothing
    /// seeds them, and an existing row is deleted by the next reference-data seed. They stay named
    /// here — not deleted outright — because the seeder's delete filter only touches keys it knows
    /// about, so this list is what tells a deployed fork's database that these rows may go. A fork
    /// operator's own rows are still never deleted.
    /// <list type="bullet">
    /// <item><c>ApimGatewayUrl</c> — the gateway origin shown on <c>/me</c> comes from
    /// <c>Gateway:ApimGatewayUrl</c>, which infra sets on the container from the APIM module's own
    /// output, so it can never drift from the gateway that was deployed (#156).</item>
    /// <item><c>ApimProductId</c> — quota tiers are per-tier APIM products now
    /// (<see cref="GatewayTiers"/>): a subscription is issued against the product for the user's
    /// tier, not one fork-wide id.</item>
    /// <item><c>EntraTenantId</c> — Graph is called as the API's own identity, so the tenant is
    /// implied by the credential; the only tenant setting read is <c>AzureAd:TenantId</c> (#123).</item>
    /// </list>
    /// </summary>
    public static readonly IReadOnlyList<string> Retired =
    [
        "ApimGatewayUrl",
        "ApimProductId",
        "EntraTenantId",
    ];
}
