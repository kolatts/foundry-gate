using System.Globalization;
using FoundryGate.Core.Configuration;

namespace FoundryGate.Core.Quota;

/// <summary>
/// The rule that a developer's monthly budget <em>is</em> a gateway tier (D-013): every numeric quota
/// the control plane accepts must equal a configured tier cap or be unlimited, because APIM's
/// <c>token-quota</c> is a per-product literal and the tier product is the only thing the gateway
/// can enforce. Pure and singleton — the tier table comes from <see cref="GatewayOptions"/> once at
/// startup.
/// </summary>
/// <remarks>
/// Two faces, one table:
/// <list type="bullet">
/// <item><b>Write paths</b> (<c>PUT /users/{id}/quota</c>, group create/update, request approval) call
/// <see cref="EnsureValidQuota"/> before persisting — a value matching no tier is a 400 with the allowed
/// values listed, never a row the gateway cannot honour.</item>
/// <item><b>Read/resolution path</b> calls <see cref="Map"/>, which never throws: a legacy or hand-edited
/// value that matches no cap is enforced at the next tier <em>up</em> (or the largest finite tier) and
/// flagged <see cref="GatewayTierAssignment.IsGatewayCapped"/>, so existing data never crashes a read
/// and admins can see which rows need fixing.</item>
/// </list>
/// </remarks>
public sealed class GatewayTierMapper
{
    private readonly IReadOnlyList<GatewayTier> _finiteTiersAscending;
    private readonly GatewayTier _unlimitedTier;

    /// <summary>Creates a mapper over <paramref name="options"/>, which must already have passed validation.</summary>
    public GatewayTierMapper(GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _finiteTiersAscending = [.. options.Tiers.Where(t => !t.IsUnlimited).OrderBy(t => t.MonthlyTokenQuota)];
        _unlimitedTier = options.Tiers.SingleOrDefault(t => t.IsUnlimited)
            ?? throw new ArgumentException("Gateway:Tiers must contain exactly one unlimited tier (MonthlyTokenQuota = 0).", nameof(options));

        if (_finiteTiersAscending.Count == 0)
        {
            throw new ArgumentException("Gateway:Tiers must contain at least one finite tier (MonthlyTokenQuota > 0).", nameof(options));
        }

        Tiers = [.. _finiteTiersAscending, _unlimitedTier];
    }

    /// <summary>Every configured tier: finite tiers by ascending cap, then the unlimited tier — the order the UI should offer them in.</summary>
    public IReadOnlyList<GatewayTier> Tiers { get; }

    /// <summary>
    /// The tier product for <paramref name="resolvedQuota"/> (<see langword="null"/> = unlimited). Exact
    /// match on a finite cap → that tier. No match → the next tier up (or the largest finite tier when
    /// the quota exceeds every cap), flagged capped. Never throws for a non-negative value.
    /// </summary>
    public GatewayTierAssignment Map(long? resolvedQuota)
    {
        if (resolvedQuota is null)
        {
            return new GatewayTierAssignment(_unlimitedTier.ProductId, IsGatewayCapped: false);
        }

        ArgumentOutOfRangeException.ThrowIfNegative(resolvedQuota.Value);

        foreach (var tier in _finiteTiersAscending)
        {
            if (tier.MonthlyTokenQuota == resolvedQuota.Value)
            {
                return new GatewayTierAssignment(tier.ProductId, IsGatewayCapped: false);
            }

            if (tier.MonthlyTokenQuota > resolvedQuota.Value)
            {
                return new GatewayTierAssignment(tier.ProductId, IsGatewayCapped: true);
            }
        }

        return new GatewayTierAssignment(_finiteTiersAscending[^1].ProductId, IsGatewayCapped: true);
    }

    /// <summary><see langword="true"/> when <paramref name="quota"/> is unlimited (<see langword="null"/>) or exactly one of the configured finite tier caps.</summary>
    public bool IsValidQuota(long? quota) =>
        quota is null || _finiteTiersAscending.Any(t => t.MonthlyTokenQuota == quota.Value);

    /// <summary>
    /// The guard every write path that accepts a monthly token quota calls before persisting it.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="quota"/> is finite and matches no configured tier cap (→ 400 via
    /// <c>GlobalExceptionHandler</c>); the message lists the allowed values (<see cref="Describe"/>).
    /// </exception>
    public void EnsureValidQuota(long? quota, string paramName)
    {
        if (!IsValidQuota(quota))
        {
            throw new ArgumentException(
                $"{quota!.Value.ToString("N0", CultureInfo.InvariantCulture)} tokens is not a configured budget tier. {Describe()}",
                paramName);
        }
    }

    /// <summary>Human-readable statement of the allowed quota values, for error messages and UI hints.</summary>
    public string Describe()
    {
        var caps = string.Join(
            ", ",
            _finiteTiersAscending.Select(t => $"{t.MonthlyTokenQuota.ToString("N0", CultureInfo.InvariantCulture)} ({t.ProductId})"));

        return $"A monthly token quota must be unlimited or exactly one of the configured tier caps: {caps}.";
    }
}

/// <summary>Output of <see cref="GatewayTierMapper.Map"/>.</summary>
/// <param name="TierProductId">The APIM product id (one of <see cref="Domain.Constants.GatewayTiers.All"/>).</param>
/// <param name="IsGatewayCapped">True when the numeric quota matched no configured tier cap and is enforced at <paramref name="TierProductId"/>'s cap (the next tier up, or the largest finite tier) instead.</param>
public readonly record struct GatewayTierAssignment(string TierProductId, bool IsGatewayCapped);
