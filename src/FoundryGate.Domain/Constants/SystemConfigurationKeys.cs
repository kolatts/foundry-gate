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

    /// <summary>
    /// ISO-8601 instant of the last successful <c>POST /users/sync</c>, written by the sync itself
    /// (#171). Empty until a fork has run one. System-managed — see <see cref="SystemManaged"/>.
    /// </summary>
    public const string LastUserSyncDate = nameof(LastUserSyncDate);

    /// <summary>
    /// The last successful <c>POST /users/sync</c>'s <c>UserSyncResult</c>, JSON-serialized
    /// (camelCase), written in the same unit of work as the run's audit row (#171). Empty until a
    /// fork has run one. System-managed — see <see cref="SystemManaged"/>.
    /// </summary>
    public const string LastUserSyncResult = nameof(LastUserSyncResult);

    /// <summary>
    /// What a token costs, as one JSON array of
    /// <c>{ "modelPrefix", "inputPerMillion", "outputPerMillion" }</c> objects (#177) — the fork's
    /// own prices, because Claude bills as a single aggregate Marketplace meter that Azure Cost
    /// Management cannot break down per developer. Seeded as <c>[]</c>: until an operator fills it
    /// in, no cost is estimated anywhere (which is the honest answer, not a zero).
    /// </summary>
    /// <remarks>
    /// One key rather than one per model: the whole card is edited as a unit, and a per-model key
    /// would need the key name itself to carry a model name — something a fixed <see cref="All"/>
    /// list cannot express. Validated on <c>PUT /config/{key}</c> so a malformed card is a 400
    /// rather than a wrong invoice.
    /// </remarks>
    public const string RateCard = nameof(RateCard);

    /// <summary>All seeded keys, for iteration in seeders and completeness tests.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        DefaultMonthlyTokenQuota,
        ApimResourceId,
        FoundryResourceId,
        EntraGroupSyncEnabled,
        ResetDayOfMonth,
        LastUserSyncDate,
        LastUserSyncResult,
        RateCard,
    ];

    /// <summary>
    /// Keys the system writes and an admin may only read, mapped to the reason (#171/#172). <b>One
    /// map, two readers</b>: the Api's <c>SystemConfigValidator.EnsureEditable</c> refuses a
    /// <c>PUT</c> with a <c>409</c> carrying the reason, and <c>SystemConfigEntryResponse.IsReadOnly</c>
    /// is set from the same lookup so the Web editor can disable the field instead of offering an edit
    /// that can only fail. Neither side keeps a list of its own — that is exactly the duplication #172
    /// was filed about, and it had already drifted by one entry once.
    /// <para>
    /// Distinct from <see cref="Retired"/>: a retired key's row is <em>deleted</em> (a <c>PUT</c> is a
    /// 404 because there is nothing to write), whereas these rows exist, are seeded, and carry a value
    /// worth reading — they are simply not an admin's to set.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SystemManaged =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [LastUserSyncDate] = "it records when POST /users/sync last ran and is written by the sync itself",
            [LastUserSyncResult] = "it records what POST /users/sync last did and is written by the sync itself",
        };

    /// <summary>The reason <paramref name="key"/> is system-managed, or <see langword="null"/> when an admin may edit it.</summary>
    public static string? SystemManagedReason(string key) =>
        key is not null && SystemManaged.TryGetValue(key, out var reason) ? reason : null;

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
