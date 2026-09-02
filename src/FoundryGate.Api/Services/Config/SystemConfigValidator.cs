using System.Globalization;
using FoundryGate.Api.Services.Cost;
using FoundryGate.Core.Quota;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;

namespace FoundryGate.Api.Services.Config;

/// <summary>
/// The one place that says what a <c>SystemConfiguration</c> value is allowed to be (#161). Every
/// key the seeder ships has a rule; <c>PUT /config/{key}</c> runs it before persisting, so a value
/// that would break quota resolution, the monthly reset, or the gateway addressing is a <c>400</c>
/// at the edit rather than a runtime failure days later. Pure and singleton — the only state is the
/// tier table behind <see cref="GatewayTierMapper"/>.
/// </summary>
/// <remarks>
/// Two outcomes:
/// <list type="bullet">
/// <item><b>Known key, bad value</b> → <see cref="ArgumentException"/> (400) whose message states the
/// rule.</item>
/// <item><b>Known key, good value</b> → the <em>normalized</em> string to store (trimmed; booleans
/// lower-cased) so two admins typing <c>True</c> and <c>true</c> leave the same row behind.</item>
/// </list>
/// A key that is present in the table but not in <see cref="SystemConfigurationKeys.All"/> — a fork
/// operator's own row, which the reference-data seeder deliberately preserves — has no rule and is
/// accepted as free text (length is <c>UpdateSystemConfigRequest</c>'s <c>[StringLength]</c> job).
/// <para>
/// <b>Read-only keys.</b> <see cref="EnsureEditable"/> refuses the keys the system writes
/// (<see cref="SystemConfigurationKeys.SystemManaged"/>) with a <c>409</c> naming the reason. It reads
/// that one Domain map, and so does <c>SystemConfigEntryResponse.IsReadOnly</c>, so the API and the
/// admin editor can never disagree about which keys are editable (#172 — they already had, once).
/// Keys that are <em>retired</em> rather than system-managed (<c>ApimProductId</c>,
/// <c>EntraTenantId</c>, <c>ApimGatewayUrl</c>) are a different story: unseeded and deleted from
/// deployed databases by the reference-data sync (#164/#123), so editing one is an unknown key, which
/// <c>PUT /config/{key}</c> already answers with <c>404</c>.
/// </para>
/// </remarks>
public sealed class SystemConfigValidator(GatewayTierMapper tierMapper)
{
    /// <summary>Highest day-of-month a monthly reset may be scheduled on: 28 is the last day every month has.</summary>
    public const int MaxResetDayOfMonth = 28;

    /// <summary>
    /// Refuses an edit to a key the system writes for itself
    /// (<see cref="SystemConfigurationKeys.SystemManaged"/>), naming the reason so the admin reads why
    /// rather than "no". A <c>409</c>, not a <c>403</c>: the caller's permissions are fine — the
    /// resource is not theirs to set.
    /// </summary>
    /// <param name="key">The configuration key, as stored (the caller has already matched the row).</param>
    /// <exception cref="ConflictException">The key is system-managed (→ 409).</exception>
    public static void EnsureEditable(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (SystemConfigurationKeys.SystemManagedReason(key) is { } reason)
        {
            throw new ConflictException(
                $"System configuration key '{key}' is read-only: {reason}. It can be read from GET /api/v1/config, " +
                "but nothing an admin sets here would survive the next run.");
        }
    }

    /// <summary>
    /// Validates <paramref name="value"/> against <paramref name="key"/>'s rule and returns the
    /// normalized form to store.
    /// </summary>
    /// <exception cref="ArgumentException">The value breaks the key's rule (→ 400).</exception>
    public string Normalize(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        var trimmed = value.Trim();

        return key switch
        {
            SystemConfigurationKeys.DefaultMonthlyTokenQuota => NormalizeDefaultQuota(trimmed),
            SystemConfigurationKeys.ResetDayOfMonth => NormalizeResetDayOfMonth(trimmed),
            SystemConfigurationKeys.EntraGroupSyncEnabled => NormalizeBoolean(key, trimmed),
            SystemConfigurationKeys.ApimResourceId or SystemConfigurationKeys.FoundryResourceId =>
                NormalizeArmResourceId(key, trimmed),
            SystemConfigurationKeys.RateCard => NormalizeRateCard(trimmed),
            _ => trimmed,
        };
    }

    /// <summary>
    /// The cost rate card (#177). Parsed, checked for blank prefixes, negative prices and repeated
    /// prefixes, and stored re-serialized so two admins pasting the same rates with different
    /// whitespace leave the same row behind. A malformed card has to be a <c>400</c> at the edit:
    /// the number it produces ends up next to a developer's name.
    /// </summary>
    private static string NormalizeRateCard(string value)
    {
        // An empty box means "no rate card", which is how a fork ships — not a parse failure.
        if (value.Length == 0)
        {
            return "[]";
        }

        RateCard card;
        try
        {
            card = RateCard.Parse(value);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException($"{exception.Message} {RateCard.Describe()}", nameof(value), exception);
        }

        var stored = card.ToStoredValue();
        if (stored.Length > ValidationConstants.ConfigValueMaxLength)
        {
            throw new ArgumentException(
                $"The rate card is {stored.Length} characters once normalized, over the {ValidationConstants.ConfigValueMaxLength}-character "
                + "limit for a configuration value. Use model prefixes rather than one entry per deployment.",
                nameof(value));
        }

        return stored;
    }

    /// <summary>
    /// The system-wide fallback budget (quota resolution's last precedence level). Must be a
    /// non-negative integer — <c>QuotaResolutionService</c> parses it as one — and, per D-013, must
    /// equal a configured tier cap: the gateway can only enforce a tier, so a default that is not one
    /// would be silently rounded up for every user who falls through to it.
    /// </summary>
    private string NormalizeDefaultQuota(string value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var quota))
        {
            throw new ArgumentException(
                $"'{value}' is not a non-negative whole number of tokens. {tierMapper.Describe()}",
                nameof(value));
        }

        // Unlimited is not expressible here: the system default is the fallback for users with no
        // override at all, and quota resolution reads this row as a number. An unlimited default is
        // set per user (IsUnlimited) or per group, never fork-wide by accident.
        tierMapper.EnsureValidQuota(quota, nameof(value));

        return quota.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 1–28: the reset must land on a day every calendar month actually has (spec §6 ships "1"). The
    /// rule is enforced ahead of a reader — nothing consumes this key yet (#165).
    /// </summary>
    private static string NormalizeResetDayOfMonth(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var day)
            || day < 1
            || day > MaxResetDayOfMonth)
        {
            throw new ArgumentException(
                $"'{value}' is not a valid day of month for the monthly reset. Use a whole number from 1 to {MaxResetDayOfMonth} "
                + "(28 is the last day every month has).",
                nameof(value));
        }

        return day.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Stored lower-cased so <c>bool.Parse</c> on the read side never sees a casing it dislikes.</summary>
    private static string NormalizeBoolean(string key, string value)
    {
        if (!bool.TryParse(value, out var parsed))
        {
            throw new ArgumentException(
                $"System configuration key '{key}' is a boolean: use 'true' or 'false', not '{value}'.",
                nameof(value));
        }

        return parsed ? "true" : "false";
    }

    /// <summary>
    /// Empty or an ARM resource id. Shape only (<c>/subscriptions/{id}/.../providers/{namespace}/...</c>)
    /// — whether the resource exists is Azure's answer, not a config editor's, and the endpoints that
    /// use it report a missing resource as <c>503 feature not configured</c>.
    /// </summary>
    private static string NormalizeArmResourceId(string key, string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        if (!value.StartsWith("/subscriptions/", StringComparison.OrdinalIgnoreCase)
            || !value.Contains("/providers/", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"System configuration key '{key}' must be an ARM resource id "
                + "(/subscriptions/{subscriptionId}/resourceGroups/{group}/providers/{namespace}/{type}/{name}) or empty, "
                + $"not '{value}'.",
                nameof(value));
        }

        return value.TrimEnd('/');
    }
}
