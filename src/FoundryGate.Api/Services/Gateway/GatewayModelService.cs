using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Azure;
using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Foundry;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Core.Configuration;
using FoundryGate.Core.Gateway;
using FoundryGate.Data;
using FoundryGate.Domain.Config;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Gateway.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace FoundryGate.Api.Services.Gateway;

/// <summary>
/// Default <see cref="IGatewayModelService"/>. Scoped: it shares the request's
/// <see cref="AppDbContext"/> with <see cref="IAuditService"/> so the audit row commits in this
/// service's own <c>SaveChangesAsync</c>. See the interface remarks for why reads go to APIM rather
/// than configuration, what the validation refuses, and where the commit point is.
/// </summary>
public sealed partial class GatewayModelService(
    IApimManagementClient apim,
    IFoundryDeploymentService deployments,
    AppSettings appSettings,
    IAuditService auditService,
    ICurrentUserAccessor currentUser,
    AppDbContext dbContext,
    IMemoryCache cache,
    ILogger<GatewayModelService> logger)
    : IGatewayModelService
{
    /// <summary>
    /// How long a tier's map and the Foundry deployment placement are reused. Rendering <c>/models</c>
    /// asks for every tier's map twice — once for the counts on <c>GET /gateway/tiers</c>, once per
    /// tier for the rows — and each of those reads would otherwise re-enumerate every Foundry account
    /// to answer "does this deployment exist". Short enough that an edit made in the Azure portal shows
    /// up while the admin is still looking, and a write through this service replaces its own entry
    /// rather than waiting for it to expire.
    /// </summary>
    public static readonly TimeSpan ReadCacheDuration = TimeSpan.FromSeconds(15);

    /// <summary><see cref="IMemoryCache"/> key for the deployment-name → accounts placement map.</summary>
    public const string PlacementCacheKey = "FoundryGate.Gateway.DeploymentPlacement";

    /// <summary><see cref="IMemoryCache"/> key for one tier's parsed alias map.</summary>
    public static string MapCacheKey(string tierProductId) => $"FoundryGate.Gateway.ModelMap.{tierProductId}";

    /// <summary>
    /// How the named value is written: camelCase, no indentation. The bicep writes it with ARM's
    /// <c>string()</c>, which is compact camelCase too — a value written here and a value written by a
    /// deploy have to be the same document, because the policy substitutes it verbatim into a
    /// <c>set-variable</c> attribute.
    /// </summary>
    private static readonly JsonSerializerOptions MapJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    /// <summary>The alias grammar, compiled once — see <see cref="GatewayModelMap.AliasPattern"/> for why it is lower-case only.</summary>
    [GeneratedRegex(GatewayModelMap.AliasPattern)]
    private static partial Regex AliasRegex();

    /// <inheritdoc />
    public async Task<IReadOnlyList<GatewayTierResponse>> ListTiersAsync(CancellationToken cancellationToken)
    {
        RequireApim();

        var tiers = appSettings.Gateway.Tiers;

        // One named-value read per tier, concurrently: three small reads against independent ARM
        // resources, and the page cannot render its left-hand side without all of them anyway.
        var maps = await Task.WhenAll(tiers.Select(tier => ReadMapAsync(tier.ProductId, cancellationToken)));

        return [.. tiers.Select((tier, index) => new GatewayTierResponse(
            tier.ProductId,
            DisplayNameOf(tier),
            tier.IsUnlimited ? null : tier.MonthlyTokenQuota,
            tier.IsUnlimited,
            maps[index].Count))];
    }

    /// <inheritdoc />
    public async Task<GatewayTierModelsResponse> GetTierModelsAsync(string tier, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tier);

        var configured = RequireTier(tier);
        RequireApim();

        var map = await ReadMapAsync(configured.ProductId, cancellationToken);
        var placement = await ReadDeploymentPlacementAsync(cancellationToken);

        return new GatewayTierModelsResponse(configured.ProductId, DisplayNameOf(configured), Project(map, placement));
    }

    /// <inheritdoc />
    public async Task<GatewayTierModelsResponse> ReplaceTierModelsAsync(string tier, ReplaceTierModelsRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tier);
        ArgumentNullException.ThrowIfNull(request);

        var configured = RequireTier(tier);
        RequireApim();

        // 403 for an unprovisioned caller, and every refusal, before APIM is touched.
        _ = await currentUser.GetRequiredUserAsync(cancellationToken);

        var placement = await ReadDeploymentPlacementAsync(cancellationToken);
        var desired = Validate(request, placement);
        var current = await ReadMapAsync(configured.ProductId, cancellationToken);

        if (AreEquivalent(current, desired))
        {
            // Writing the value it already holds changes nothing at the gateway, so an audit row
            // claiming an allowlist change would be a lie. Same rule as a no-op capacity PATCH.
            logger.LogDebug("Gateway tier {Tier} model map is already what was requested; nothing to change", configured.ProductId);
            return new GatewayTierModelsResponse(configured.ProductId, DisplayNameOf(configured), Project(current, placement));
        }

        var namedValueName = GatewayModelMap.NamedValueName(configured.ProductId);

        try
        {
            await apim.SetNamedValueAsync(namedValueName, JsonSerializer.Serialize(desired, MapJson), cancellationToken);
        }
        catch (RequestFailedException exception)
        {
            // APIM refusing the write is a state problem the admin can act on (a named value held by
            // Key Vault, a value APIM will not accept, a concurrent edit) — not an unmapped 500.
            throw new ConflictException(
                $"API Management refused the update to named value '{namedValueName}' (HTTP {exception.Status}). " +
                "The tier's model allowlist was not changed. " +
                $"API Management said: {exception.Message}",
                exception);
        }

        // ---- commit point: APIM has accepted the new map. Nothing below observes cancellationToken. ----
        _ = cache.Set(MapCacheKey(configured.ProductId), desired, ReadCacheDuration);

        logger.LogInformation(
            "Gateway tier {Tier} model allowlist replaced: {Before} -> {After} aliases ({Aliases})",
            configured.ProductId,
            current.Count,
            desired.Count,
            string.Join(", ", desired.Keys));

        try
        {
            _ = await auditService.LogAsync(
                AuditActions.GatewayModelsUpdated,
                AuditTargetTypes.GatewayTier,
                configured.ProductId,
                new { before = current, after = desired },
                CancellationToken.None);
            _ = await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Gateway tier {Tier}: API Management accepted the new model allowlist but the audit row could not be saved — reconcile manually",
                configured.ProductId);
            throw;
        }

        return new GatewayTierModelsResponse(configured.ProductId, DisplayNameOf(configured), Project(desired, placement));
    }

    /// <summary>
    /// Turns the request into the exact document the named value will hold, refusing anything the
    /// gateway would answer with a 404 instead of an honest 403.
    /// </summary>
    private SortedDictionary<string, ModelMapEntry> Validate(ReplaceTierModelsRequest request, IReadOnlyDictionary<string, List<string>> placement)
    {
        var accounts = appSettings.Gateway.FoundryAccountNames;
        var primaryAccount = accounts[0];
        var map = new SortedDictionary<string, ModelMapEntry>(StringComparer.Ordinal);

        foreach (var entry in request.Aliases)
        {
            var alias = (entry.Alias ?? string.Empty).Trim();
            var deployment = (entry.DeploymentName ?? string.Empty).Trim();
            var pool = (entry.Pool ?? string.Empty).Trim();

            if (!AliasRegex().IsMatch(alias))
            {
                throw new ArgumentException(
                    $"'{alias}' is not a usable model alias. An alias is what a developer types into their CLI's model field, so it must be " +
                    "lower-case and url-safe: start with a letter or digit, then letters, digits, and . _ - only.",
                    nameof(request));
            }

            if (map.ContainsKey(alias))
            {
                throw new ArgumentException(
                    $"Alias '{alias}' is listed more than once. The gateway's map is keyed by alias, so a duplicate would let list order decide " +
                    "which deployment a developer actually reaches.",
                    nameof(request));
            }

            if (!GatewayModelMap.Pools.Contains(pool, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"Alias '{alias}' names pool '{pool}', which is not a backend this gateway has. Valid pools: {string.Join(", ", GatewayModelMap.Pools)}.",
                    nameof(request));
            }

            if (!placement.TryGetValue(deployment, out var deployedIn))
            {
                throw new ArgumentException(
                    $"Alias '{alias}' points at deployment '{deployment}', which does not exist in any of this gateway's Foundry accounts " +
                    $"({string.Join(", ", accounts)}). Create the deployment first — every request for this alias would otherwise reach a backend " +
                    "that has never heard of it.",
                    nameof(request));
            }

            var isPooled = !string.Equals(pool, GatewayModelMap.OpenAiPool, StringComparison.OrdinalIgnoreCase);
            if (isPooled)
            {
                // infra/main.bicep's contract: the Anthropic pool fails a 429 over to another region,
                // so a region missing the deployment turns a throttle into a 404.
                var missing = accounts.Where(account => !deployedIn.Contains(account, StringComparer.OrdinalIgnoreCase)).ToList();
                if (missing.Count > 0)
                {
                    throw new ArgumentException(
                        $"Alias '{alias}' routes at the '{GatewayModelMap.AnthropicPool}' pool, so deployment '{deployment}' must exist in every " +
                        $"Foundry account — it is missing from {string.Join(", ", missing)}. The pool fails a throttled request over to another " +
                        "region, and a region without the deployment turns that throttle into a 404. Deploy it there, or route this alias at the " +
                        $"'{GatewayModelMap.OpenAiPool}' backend if it is a single-region model.",
                        nameof(request));
                }
            }

            map[alias] = new ModelMapEntry(
                deployment,
                GatewayModelMap.BackendForPool(pool, primaryAccount),
                ProviderToken(entry.Provider));
        }

        return map;
    }

    /// <summary>Reads and parses one tier's named value; an absent or unparseable map is an empty allowlist, which is what the gateway itself makes of it.</summary>
    private async Task<SortedDictionary<string, ModelMapEntry>> ReadMapAsync(string tierProductId, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(MapCacheKey(tierProductId), out SortedDictionary<string, ModelMapEntry>? cached) && cached is not null)
        {
            return cached;
        }

        var namedValueName = GatewayModelMap.NamedValueName(tierProductId);
        var raw = await apim.GetNamedValueAsync(namedValueName, cancellationToken);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return Cache(tierProductId, []);
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, ModelMapEntry>>(raw, MapJson) ?? [];

            // plans/25: an entry missing any of its three fields is blocked by the policy exactly as an
            // absent one is, so a half-written entry is dropped here rather than rendered as a row —
            // and the projection below never has to look a null deployment name up.
            return Cache(
                tierProductId,
                [.. parsed.Where(entry => !string.IsNullOrWhiteSpace(entry.Value?.Deployment))]);
        }
        catch (JsonException exception)
        {
            // The policy fragment treats a map it cannot read as "permits nothing" (fail loud, #86), so
            // reporting an empty allowlist here matches what developers are actually experiencing.
            logger.LogError(
                exception,
                "APIM named value {NamedValue} does not hold a readable model alias map; reporting the tier as permitting no models",
                namedValueName);
            return Cache(tierProductId, []);
        }
    }

    /// <summary>Stores a tier's parsed map under <see cref="MapCacheKey"/> and hands it back.</summary>
    private SortedDictionary<string, ModelMapEntry> Cache(string tierProductId, IEnumerable<KeyValuePair<string, ModelMapEntry>> entries)
    {
        var map = new SortedDictionary<string, ModelMapEntry>(StringComparer.Ordinal);
        foreach (var (alias, entry) in entries)
        {
            map[alias] = entry;
        }

        return cache.Set(MapCacheKey(tierProductId), map, ReadCacheDuration);
    }

    /// <summary>Deployment name → the configured accounts that carry it, case-insensitively on both.</summary>
    private async Task<IReadOnlyDictionary<string, List<string>>> ReadDeploymentPlacementAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(PlacementCacheKey, out IReadOnlyDictionary<string, List<string>>? cached) && cached is not null)
        {
            return cached;
        }

        // ListDeploymentsAsync raises FeatureNotConfiguredException (503) when Foundry addressing is
        // absent — the honest answer, since "does this deployment exist?" cannot be answered without it.
        var all = await deployments.ListDeploymentsAsync(cancellationToken);
        var placement = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var deployment in all)
        {
            if (!placement.TryGetValue(deployment.DeploymentName, out var accounts))
            {
                accounts = [];
                placement[deployment.DeploymentName] = accounts;
            }

            accounts.Add(deployment.AccountName);
        }

        return cache.Set<IReadOnlyDictionary<string, List<string>>>(PlacementCacheKey, placement, ReadCacheDuration);
    }

    private IReadOnlyList<GatewayModelAliasResponse> Project(
        SortedDictionary<string, ModelMapEntry> map,
        IReadOnlyDictionary<string, List<string>> placement)
    {
        var accounts = appSettings.Gateway.FoundryAccountNames;

        return
        [
            .. map.Select(pair =>
            {
                var deployedIn = placement.TryGetValue(pair.Value.Deployment, out var found) ? found : [];

                return new GatewayModelAliasResponse(
                    pair.Key,
                    pair.Value.Deployment,
                    GatewayModelMap.PoolForBackend(pair.Value.Backend),
                    ProviderFromToken(pair.Value.Provider),
                    deployedIn.Count > 0,
                    [.. accounts.Where(account => !deployedIn.Contains(account, StringComparer.OrdinalIgnoreCase))]);
            })
        ];
    }

    private static bool AreEquivalent(SortedDictionary<string, ModelMapEntry> left, SortedDictionary<string, ModelMapEntry> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var other) && pair.Value == other);

    private static string DisplayNameOf(GatewayTier tier) =>
        string.IsNullOrWhiteSpace(tier.DisplayName) ? tier.ProductId : tier.DisplayName;

    private GatewayTier RequireTier(string tier) =>
        appSettings.Gateway.Tiers.FirstOrDefault(t => string.Equals(t.ProductId, tier, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException(
            $"'{tier}' is not one of this gateway's quota tiers ({string.Join(", ", appSettings.Gateway.Tiers.Select(t => t.ProductId))}).");

    private void RequireApim()
    {
        if (!appSettings.Gateway.IsApimConfigured)
        {
            throw new FeatureNotConfiguredException(
                "The gateway's model allowlist is not configured: set Gateway:SubscriptionId, Gateway:ResourceGroup and Gateway:ApimName " +
                "(infra sets these on the Container App as Gateway__*; see issue #108).");
        }
    }

    /// <summary>Bicep writes the provider lower-case; so does this, so a map is the same document whoever wrote it.</summary>
    private static string ProviderToken(ModelProviderType provider) =>
        provider == ModelProviderType.OpenAi ? GatewayModelMap.OpenAiPool : GatewayModelMap.AnthropicPool;

    private static ModelProviderType ProviderFromToken(string? provider) =>
        string.Equals(provider, GatewayModelMap.OpenAiPool, StringComparison.OrdinalIgnoreCase)
            ? ModelProviderType.OpenAi
            : ModelProviderType.Anthropic;

    /// <summary>
    /// One entry of the named value, in the wire shape <c>infra/modules/ai-gateway.bicep</c> writes and
    /// <c>infra/policies/model-alias-fragment.xml</c> reads: the real deployment name, the APIM backend
    /// id to route at, and the front door the alias belongs to. A record, so two maps compare by value.
    /// </summary>
    private sealed record ModelMapEntry(string Deployment, string Backend, string Provider);
}
