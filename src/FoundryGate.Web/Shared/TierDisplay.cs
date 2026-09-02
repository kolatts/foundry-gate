using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Quota.Contracts;
using MudBlazor;

namespace FoundryGate.Web.Shared;

/// <summary>
/// Turns the two numbers a quota is stored as (<c>IsUnlimited</c> + <c>MonthlyTokenQuota</c>)
/// back into the tier an admin picked, so the UI can show a name where a raw token count
/// would otherwise appear.
/// </summary>
/// <remarks>
/// A monthly budget is either unlimited or exactly one configured tier cap
/// (fable-refactor-log.md D-013) — <c>GET /quota/tiers</c> is the whole vocabulary. Values
/// that predate a tier change still exist in the database, and the API resolves those upward
/// rather than failing (<c>IsGatewayCapped</c>), so <see cref="Describe"/> never throws: an
/// unmatched number is rendered as itself with a note, not swallowed.
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
        return match?.DisplayName ?? FormatTokens(quota);
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

    /// <summary>Token counts as an admin reads them: 5,000,000 -&gt; "5M tokens".</summary>
    public static string FormatTokens(long tokens) => tokens switch
    {
        >= 1_000_000_000 when tokens % 1_000_000_000 == 0 => $"{tokens / 1_000_000_000:N0}B tokens",
        >= 1_000_000 when tokens % 1_000_000 == 0 => $"{tokens / 1_000_000:N0}M tokens",
        >= 1_000 when tokens % 1_000 == 0 => $"{tokens / 1_000:N0}K tokens",
        _ => $"{tokens:N0} tokens",
    };

    private static QuotaTierResponse? FindUnlimited(IReadOnlyList<QuotaTierResponse>? tiers) =>
        tiers?.FirstOrDefault(t => t.IsUnlimited);
}
