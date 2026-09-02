using System.Globalization;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using MudBlazor;

namespace FoundryGate.Web.Services;

/// <summary>
/// The presentation rules the developer-facing quota surfaces share — the gauge's colour
/// thresholds, the tier's human name, where a budget was resolved from, and token formatting.
/// Static and side-effect free so the thresholds are unit-tested directly rather than inferred from
/// rendered CSS classes, and so <c>/me</c> and <c>/dashboard</c> can never disagree about what "80%"
/// looks like.
/// </summary>
/// <remarks>
/// <see cref="TierDisplayName"/> resolves from the shipped tier ids rather than from
/// <c>GET /quota/tiers</c>, because the pages that call it (<c>/me</c> makes one profile call and no
/// tier call) hold a product id and nothing else. The admin management wave's
/// <c>Shared/TierDisplay</c> formats from the live tier catalogue and is the better answer wherever
/// that catalogue is already loaded; issue #188 tracks collapsing the two into it.
/// </remarks>
public static class QuotaDisplay
{
    /// <summary>Below this percentage the gauge is green.</summary>
    public const double WarningThresholdPercent = 80;

    /// <summary>Above this percentage the gauge is red; at or below it (and at/above <see cref="WarningThresholdPercent"/>) it is amber.</summary>
    public const double CriticalThresholdPercent = 95;

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
    /// The human name for an APIM tier product id (<see cref="GatewayTiers"/>). Falls back to the
    /// raw id with its first letter capitalized, so a fork that adds a tier product still renders
    /// something readable without a Web change.
    /// </summary>
    public static string TierDisplayName(string? tierProductId) => tierProductId switch
    {
        null or "" => "Unknown tier",
        GatewayTiers.Standard => "Standard",
        GatewayTiers.Power => "Power",
        GatewayTiers.Unlimited => "Unlimited",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(tierProductId),
    };

    /// <summary>
    /// Full sentence for <see cref="QuotaLevelType"/>, for the developer-facing gauge: "resolved
    /// from <em>your personal quota</em>". Second person on purpose — this one is read by the
    /// developer whose budget it is, not by an admin looking at someone else's.
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
}
