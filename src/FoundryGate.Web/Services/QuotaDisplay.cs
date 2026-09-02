using System.Globalization;
using FoundryGate.Domain.Constants;
using MudBlazor;

namespace FoundryGate.Web.Services;

/// <summary>
/// The presentation rules the quota surfaces share — the gauge's colour thresholds, the tier's
/// human name, and token formatting. Static and side-effect free so the thresholds are unit-tested
/// directly rather than inferred from rendered CSS classes, and so <c>/me</c>, <c>/dashboard</c> and
/// any future quota view can never disagree about what "80%" looks like.
/// </summary>
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
