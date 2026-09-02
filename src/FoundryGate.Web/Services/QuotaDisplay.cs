using System.Globalization;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Quota.Contracts;
using MudBlazor;

namespace FoundryGate.Web.Services;

/// <summary>
/// Every presentation rule the quota surfaces share — the gauge's colour thresholds, the name of
/// the tier a stored budget is, where that budget was resolved from, and token formatting. Static
/// and side-effect free so the thresholds are unit-tested directly rather than inferred from
/// rendered CSS classes, and so <c>/me</c>, <c>/dashboard</c> and every admin quota view can never
/// disagree about what "80%" or "Power" looks like.
/// </summary>
/// <remarks>
/// This is deliberately the <em>only</em> home for these rules. A monthly budget is either
/// unlimited or exactly one configured tier cap (fable-refactor-log.md D-013), and
/// <c>GET /quota/tiers</c> is the whole vocabulary — so wherever a caller already holds that list,
/// pass it and the fork's own <see cref="QuotaTierResponse.DisplayName"/> wins. Where a caller
/// holds only a product id (<c>/me</c> makes one profile call and no tier call),
/// <see cref="TierDisplayName"/> still renders something readable. Legacy values that match no tier
/// are resolved upward by the API rather than rejected (<c>IsGatewayCapped</c>), so nothing here
/// throws on one: it renders as itself.
/// </remarks>
public static class QuotaDisplay
{
    /// <summary>Below this percentage the gauge is green.</summary>
    public const double WarningThresholdPercent = 80;

    /// <summary>Above this percentage the gauge is red; at or below it (and at/above <see cref="WarningThresholdPercent"/>) it is amber.</summary>
    public const double CriticalThresholdPercent = 95;

    /// <summary>Shown where a user or group has no quota of its own and inherits one.</summary>
    public const string InheritedLabel = "Inherited";

    /// <summary>
    /// Gauge colour for a percentage of monthly budget consumed (issue #49): green below 80%,
    /// amber from 80% through 95%, red above 95%.
    /// </summary>
    /// <param name="percentUsed">
    /// <c>PercentUsed</c> from a <c>QuotaAllocationResponse</c>. <see langword="null"/> means
    /// unlimited — there is no bar to colour, so callers render the "Unlimited" chip instead; this
    /// returns <see cref="Color.Success"/> for that case rather than throwing.
    /// </param>
    public static Color GaugeColor(double? percentUsed) => percentUsed switch
    {
        > CriticalThresholdPercent => Color.Error,
        >= WarningThresholdPercent => Color.Warning,
        _ => Color.Success,
    };

    /// <summary>
    /// The human name for an APIM tier product id (<see cref="GatewayTiers"/>). When
    /// <paramref name="tiers"/> is supplied the fork's own configured
    /// <see cref="QuotaTierResponse.DisplayName"/> wins; otherwise the shipped tier names are used,
    /// falling back to the raw id title-cased so a fork that adds a tier product still renders
    /// something readable without a Web change.
    /// </summary>
    public static string TierDisplayName(string? tierProductId, IReadOnlyList<QuotaTierResponse>? tiers = null)
    {
        if (string.IsNullOrEmpty(tierProductId))
        {
            return "Unknown tier";
        }

        var configured = tiers?.FirstOrDefault(t => string.Equals(t.ProductId, tierProductId, StringComparison.OrdinalIgnoreCase));
        if (configured is not null)
        {
            return configured.DisplayName;
        }

        return tierProductId switch
        {
            GatewayTiers.Standard => "Standard",
            GatewayTiers.Power => "Power",
            GatewayTiers.Unlimited => "Unlimited",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(tierProductId),
        };
    }

    /// <summary>
    /// The tier name for a stored quota as a user or group holds it: the matching tier's
    /// <see cref="QuotaTierResponse.DisplayName"/>, the unlimited tier's name when
    /// <paramref name="isUnlimited"/>, <see cref="InheritedLabel"/> when the quota is null (nothing
    /// set at this level, so it inherits), or the compact token count when nothing matches — a
    /// legacy value the gateway is enforcing at the next tier up.
    /// </summary>
    public static string Describe(bool isUnlimited, long? monthlyTokenQuota, IReadOnlyList<QuotaTierResponse>? tiers)
    {
        if (isUnlimited)
        {
            return FindUnlimited(tiers)?.DisplayName ?? "Unlimited";
        }

        if (monthlyTokenQuota is not { } quota)
        {
            return InheritedLabel;
        }

        var match = tiers?.FirstOrDefault(t => !t.IsUnlimited && t.MonthlyTokenQuota == quota);
        return match?.DisplayName ?? FormatTokensCompact(quota);
    }

    /// <summary>
    /// The tier a quota editor should start on: the product id of the tier the stored values match,
    /// or <see langword="null"/> when the user or group inherits (nothing selected yet).
    /// </summary>
    public static string? MatchProductId(bool isUnlimited, long? monthlyTokenQuota, IReadOnlyList<QuotaTierResponse>? tiers)
    {
        if (isUnlimited)
        {
            return FindUnlimited(tiers)?.ProductId ?? GatewayTiers.Unlimited;
        }

        return monthlyTokenQuota is { } quota
            ? tiers?.FirstOrDefault(t => !t.IsUnlimited && t.MonthlyTokenQuota == quota)?.ProductId
            : null;
    }

    /// <summary>Colour for a resolved-level chip: where the budget came from, not how big it is.</summary>
    public static Color LevelColor(QuotaLevelType level) => level switch
    {
        QuotaLevelType.UserUnlimited or QuotaLevelType.GroupUnlimited => Color.Info,
        QuotaLevelType.UserOverride => Color.Primary,
        QuotaLevelType.GroupMax => Color.Secondary,
        _ => Color.Default,
    };

    /// <summary>Short label for <see cref="QuotaLevelType"/>, for a chip beside a number.</summary>
    public static string LevelLabel(QuotaLevelType level) => level switch
    {
        QuotaLevelType.UserUnlimited => "Unlimited (user)",
        QuotaLevelType.UserOverride => "User override",
        QuotaLevelType.GroupUnlimited => "Unlimited (group)",
        QuotaLevelType.GroupMax => "Highest group",
        QuotaLevelType.SystemDefault => "System default",
        _ => level.ToString(),
    };

    /// <summary>
    /// Full sentence for <see cref="QuotaLevelType"/>, for the developer-facing gauge: "resolved
    /// from <em>your personal quota</em>". <see cref="LevelLabel"/> is the chip-sized version.
    /// </summary>
    public static string LevelSentence(QuotaLevelType level) => level switch
    {
        QuotaLevelType.UserUnlimited => "your unlimited flag",
        QuotaLevelType.UserOverride => "your personal quota",
        QuotaLevelType.GroupUnlimited => "an unlimited group you belong to",
        QuotaLevelType.GroupMax => "the most generous group you belong to",
        QuotaLevelType.SystemDefault => "the fork-wide default",
        _ => "quota resolution",
    };

    /// <summary>A token count with thousands separators; <c>"Unlimited"</c> for <see langword="null"/>.</summary>
    public static string FormatTokens(long? tokens) =>
        tokens?.ToString("N0", CultureInfo.CurrentCulture) ?? "Unlimited";

    /// <summary>Token counts as an admin skims them in a table: 5,000,000 -&gt; "5M tokens".</summary>
    public static string FormatTokensCompact(long tokens) => tokens switch
    {
        >= 1_000_000_000 when tokens % 1_000_000_000 == 0 => $"{tokens / 1_000_000_000:N0}B tokens",
        >= 1_000_000 when tokens % 1_000_000 == 0 => $"{tokens / 1_000_000:N0}M tokens",
        >= 1_000 when tokens % 1_000 == 0 => $"{tokens / 1_000:N0}K tokens",
        _ => $"{tokens:N0} tokens",
    };

    /// <summary>
    /// The value <c>MudProgressLinear</c> should show for a percentage that may be missing or past
    /// 100 (a legacy over-cap allocation): clamped into <c>[0, 100]</c>, zero when unlimited.
    /// </summary>
    public static double GaugeValue(double? percentUsed) => percentUsed switch
    {
        null => 0,
        < 0 => 0,
        > 100 => 100,
        _ => percentUsed.Value,
    };

    private static QuotaTierResponse? FindUnlimited(IReadOnlyList<QuotaTierResponse>? tiers) =>
        tiers?.FirstOrDefault(t => t.IsUnlimited);
}
