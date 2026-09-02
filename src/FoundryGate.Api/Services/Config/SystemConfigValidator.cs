using System.Globalization;
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
/// Three outcomes:
/// <list type="bullet">
/// <item><b>Read-only key</b> → <see cref="ConflictException"/> (409). Two seeded keys are no longer
/// wired to anything (<see cref="SystemConfigurationKeys.ApimProductId"/>, superseded by the per-tier
/// APIM products; <see cref="SystemConfigurationKeys.EntraTenantId"/>, unused since #123). They stay in
/// the table because the seed data references them, but editing one would be a silent no-op — so the
/// refusal names what to change instead. #164 removes the rows themselves, which is a data change
/// rather than an API one.</item>
/// <item><b>Known key, bad value</b> → <see cref="ArgumentException"/> (400) whose message states the
/// rule.</item>
/// <item><b>Known key, good value</b> → the <em>normalized</em> string to store (trimmed; booleans
/// lower-cased) so two admins typing <c>True</c> and <c>true</c> leave the same row behind.</item>
/// </list>
/// A key that is present in the table but not in <see cref="SystemConfigurationKeys.All"/> — a fork
/// operator's own row, which the reference-data seeder deliberately preserves — has no rule and is
/// accepted as free text (length is <c>UpdateSystemConfigRequest</c>'s <c>[StringLength]</c> job).
/// </remarks>
public sealed class SystemConfigValidator(GatewayTierMapper tierMapper)
{
    /// <summary>Highest day-of-month a monthly reset may be scheduled on: 28 is the last day every month has.</summary>
    public const int MaxResetDayOfMonth = 28;

    /// <summary>
    /// Throws when <paramref name="key"/> is one of the retired keys, with a message naming its
    /// replacement. Called before any value validation so a read-only key answers <c>409</c>
    /// regardless of what was posted.
    /// </summary>
    /// <exception cref="ConflictException">The key is retired and cannot be edited.</exception>
    public static void EnsureEditable(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var replacement = key switch
        {
            SystemConfigurationKeys.ApimProductId =>
                "quota tiers are APIM products now: a developer's subscription is issued against the product for "
                + "their tier (`Gateway:Tiers` in the API configuration, `quotaTiers` in infra/main.bicep; "
                + "`GET /quota/tiers` lists them), so this single product id is no longer read",
            SystemConfigurationKeys.EntraTenantId =>
                "Microsoft Graph access is configured by the `Entra` options section (`Entra:Enabled`, "
                + "`Entra:GraphBaseUrl`, `Entra:ServicePrincipalObjectId`) over the host's `AzureAd:TenantId`, "
                + "so this key is no longer read",
            _ => null,
        };

        if (replacement is not null)
        {
            throw new ConflictException(
                $"System configuration key '{key}' is read-only: {replacement}. The row is kept so re-seeding stays idempotent.");
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
            SystemConfigurationKeys.ApimGatewayUrl => NormalizeHttpsUrl(key, trimmed),
            SystemConfigurationKeys.ApimResourceId or SystemConfigurationKeys.FoundryResourceId =>
                NormalizeArmResourceId(key, trimmed),
            _ => trimmed,
        };
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

    /// <summary>Empty (feature not addressed yet) or an absolute https origin — the gateway is never plain http.</summary>
    private static string NormalizeHttpsUrl(string key, string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                $"System configuration key '{key}' must be an absolute https URL (e.g. https://ai.contoso.com) or empty, not '{value}'.",
                nameof(value));
        }

        return uri.ToString().TrimEnd('/');
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
