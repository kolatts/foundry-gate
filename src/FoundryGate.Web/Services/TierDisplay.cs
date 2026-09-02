using System.Globalization;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Quota.Contracts;
using MudBlazor;

namespace FoundryGate.Web.Services;

/// <summary>
/// Turns the two numbers a quota is stored as (<c>IsUnlimited</c> + <c>MonthlyTokenQuota</c>)
/// back into the tier an admin picked, so the UI can show a name where a raw token count
/// would otherwise appear.
/// </summary>
/// <remarks>
/// The single owner of quota presentation (#188). Two of these existed while the developer and
/// admin waves were open: the other resolved a tier name from a hard-coded <c>switch</c> over
/// <see cref="GatewayTiers"/>, which is the half that breaks the first time a fork configures a
/// tier product of its own, and it formatted the same token count a second way. This one won and
/// absorbed its gauge helpers: names come from the live <c>GET /quota/tiers</c> catalogue, which
/// is what D-013 says a budget actually is.
/// <para>
/// The two token formats are named rather than left to guess: <see cref="FormatTokensCompact"/>
/// ("5M tokens") for a grid cell or chip, <see cref="FormatTokensExact"/> ("5,000,000") where the
/// number itself is the point, such as a gauge's "x of y".
/// </para>
/// <para>
/// A monthly budget is either unlimited or exactly one configured tier cap
/// (fable-refactor-log.md D-013) — <c>GET /quota/tiers</c> is the whole vocabulary. Values
/// that predate a tier change still exist in the database, and the API resolves those upward
/// rather than failing (<c>IsGatewayCapped</c>), so <see cref="Describe"/> never throws: an
/// unmatched number is rendered as itself with a note, not swallowed.
/// </para>
/// </remarks>
public static class TierDisplay
{
    /// <summary>Shown where a group or user has no quota of its own and inherits one.</summary>
    public const string InheritedLabel = "Inherited";

    /// <summary>
    /// The tier name for a stored quota: the matching tier's <see cref="QuotaTierResponse.DisplayName"/>,
    /// "Unlimited" when <paramref name="isUnlimited"/>, <see cref="InheritedLabel"/> when the
    /// quota is null, or the formatted token count when nothing in
    /// <paramref name="tiers"/> matches (a legacy value — see the remarks).
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
    /// The tier a quota editor should start on: the product id of the tier the stored values
    /// match, or null when the user/group inherits (nothing selected yet).
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

    /// <summary>Colour for a resolved-level chip: where the quota came from, not how big it is.</summary>
    public static Color LevelColor(QuotaLevelType level) => level switch
    {
        QuotaLevelType.UserUnlimited or QuotaLevelType.GroupUnlimited => Color.Info,
        QuotaLevelType.UserOverride => Color.Primary,
        QuotaLevelType.GroupMax => Color.Secondary,
        _ => Color.Default,
    };

    /// <summary>Human wording for <see cref="QuotaLevelType"/> — "where this month's budget came from".</summary>
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
    /// Token counts as an admin reads them at a glance: 5,000,000 -&gt; "5M tokens". For a grid cell,
    /// a chip, or anywhere the magnitude matters more than the digits.
    /// </summary>
    public static string FormatTokensCompact(long tokens) => tokens switch
    {
        >= 1_000_000_000 when tokens % 1_000_000_000 == 0 => $"{tokens / 1_000_000_000:N0}B tokens",
        >= 1_000_000 when tokens % 1_000_000 == 0 => $"{tokens / 1_000_000:N0}M tokens",
        >= 1_000 when tokens % 1_000 == 0 => $"{tokens / 1_000:N0}K tokens",
        _ => $"{tokens:N0} tokens",
    };

    /// <summary>
    /// The digits, with thousands separators: 5,000,000. For a gauge or anywhere an admin needs the
    /// number and not an approximation of it. <see langword="null"/> is an unlimited budget, which
    /// has no number to show.
    /// </summary>
    public static string FormatTokensExact(long? tokens) =>
        tokens?.ToString("N0", CultureInfo.CurrentCulture) ?? "Unlimited";

    /// <summary>Below this percentage of the monthly budget the gauge is green.</summary>
    public const double WarningThresholdPercent = 80;

    /// <summary>Above this percentage the gauge is red; from <see cref="WarningThresholdPercent"/> to here it is amber.</summary>
    public const double CriticalThresholdPercent = 95;

    /// <summary>
    /// Gauge colour for a percentage of monthly budget consumed (#49): green below 80%, amber from
    /// 80% through 95%, red above 95%.
    /// </summary>
    /// <param name="percentUsed">
    /// <c>PercentUsed</c> from a <c>QuotaAllocationResponse</c>. <see langword="null"/> means
    /// unlimited — there is no bar to colour, so callers render the "Unlimited" chip instead; this
    /// answers <see cref="Color.Success"/> for that case rather than throwing.
    /// </param>
    public static Color GaugeColor(double? percentUsed) => percentUsed switch
    {
        > CriticalThresholdPercent => Color.Error,
        >= WarningThresholdPercent => Color.Warning,
        _ => Color.Success,
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

    /// <summary>
    /// The human name for an APIM tier product id (<see cref="GatewayTiers"/>), for the places that
    /// hold a product id rather than a quota — <c>QuotaAllocationResponse.TierProductId</c>, mostly.
    /// Prefers the catalogue's own <see cref="QuotaTierResponse.DisplayName"/> when
    /// <paramref name="tiers"/> is supplied, so a fork that adds a tier product gets its real name;
    /// falls back to title-casing the id rather than showing nothing.
    /// </summary>
    public static string TierDisplayName(string? tierProductId, IReadOnlyList<QuotaTierResponse>? tiers = null)
    {
        if (string.IsNullOrEmpty(tierProductId))
        {
            return "Unknown tier";
        }

        var match = tiers?.FirstOrDefault(t => string.Equals(t.ProductId, tierProductId, StringComparison.OrdinalIgnoreCase));
        return match?.DisplayName ?? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(tierProductId);
    }

    /// <summary>Where a budget came from, as a sentence fragment addressed to the developer who has it.</summary>
    public static string LevelSentence(QuotaLevelType level) => level switch
    {
        QuotaLevelType.UserUnlimited => "your unlimited flag",
        QuotaLevelType.UserOverride => "your personal quota",
        QuotaLevelType.GroupUnlimited => "an unlimited group you belong to",
        QuotaLevelType.GroupMax => "the most generous group you belong to",
        QuotaLevelType.SystemDefault => "the fork-wide default",
        _ => "quota resolution",
    };

    private static QuotaTierResponse? FindUnlimited(IReadOnlyList<QuotaTierResponse>? tiers) =>
        tiers?.FirstOrDefault(t => t.IsUnlimited);
}
