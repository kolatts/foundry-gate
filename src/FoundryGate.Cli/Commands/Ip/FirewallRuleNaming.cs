using System.Globalization;
using System.Text.RegularExpressions;

namespace FoundryGate.Cli.Commands.Ip;

/// <summary>Who is running the CLI — the inputs firewall rule names are derived from.</summary>
/// <param name="IsGitHubActions"><c>GITHUB_ACTIONS=true</c>.</param>
/// <param name="GitHubRunId"><c>GITHUB_RUN_ID</c> (stable across re-run attempts of the same run).</param>
/// <param name="UserName">Local account name (domain/UPN prefixes are stripped by <see cref="FirewallRuleNaming"/>).</param>
/// <param name="MachineName">Local host name.</param>
public sealed record RunnerContext(bool IsGitHubActions, string? GitHubRunId, string UserName, string MachineName)
{
    /// <summary>Reads the current process environment.</summary>
    public static RunnerContext FromEnvironment() => new(
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase),
        Environment.GetEnvironmentVariable("GITHUB_RUN_ID"),
        Environment.UserName,
        Environment.MachineName);
}

/// <summary>
/// Firewall rule naming for the two kinds of rule <c>ip setup</c> creates. Both are recognisable at a
/// glance in the portal and, for CI, prunable by <c>ip cleanup</c>:
/// <list type="bullet">
/// <item><c>gha-{run id}-{yyyyMMddHHmm}</c> for a GitHub Actions runner. ARM keeps no creation timestamp on
/// a firewall rule, so the UTC minute it was created is baked into the name — that is what
/// <c>ip cleanup --older-than</c> compares against.</item>
/// <item><c>fg-dev-{machine}-{user}</c> for a developer machine. Long-lived by design; never pruned.</item>
/// </list>
/// Names are sanitised to <c>[A-Za-z0-9_.-]</c> and capped at Azure's 128-character limit.
/// </summary>
public static partial class FirewallRuleNaming
{
    /// <summary>Prefix of every CI rule.</summary>
    public const string CiRulePrefix = "gha-";

    /// <summary>Prefix of every developer rule.</summary>
    public const string DevRulePrefix = "fg-dev-";

    /// <summary>UTC timestamp format embedded in CI rule names.</summary>
    public const string CiTimestampFormat = "yyyyMMddHHmm";

    /// <summary>Azure's maximum length for a firewall rule name.</summary>
    public const int MaxLength = 128;

    /// <summary>The name <c>ip setup</c> creates for this runner at <paramref name="utcNow"/>.</summary>
    public static string ForSetup(RunnerContext runner, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(runner);

        if (runner.IsGitHubActions)
        {
            var runId = Sanitize(string.IsNullOrWhiteSpace(runner.GitHubRunId) ? "unknown" : runner.GitHubRunId);
            var stamp = utcNow.ToUniversalTime().ToString(CiTimestampFormat, CultureInfo.InvariantCulture);
            return Truncate($"{CiRulePrefix}{runId}-{stamp}");
        }

        var machine = Sanitize(runner.MachineName);
        var user = Sanitize(StripAccountDomain(runner.UserName));
        return Truncate($"{DevRulePrefix}{machine}-{user}");
    }

    /// <summary>
    /// <c>gha-{run id}-</c> for the current GitHub Actions run, or <see langword="null"/> outside CI. Every
    /// rule carrying this prefix belongs to this run (any attempt) and is removed by <c>ip cleanup</c>
    /// regardless of age.
    /// </summary>
    public static string? OwnCiRulePrefix(RunnerContext runner)
    {
        ArgumentNullException.ThrowIfNull(runner);

        if (!runner.IsGitHubActions || string.IsNullOrWhiteSpace(runner.GitHubRunId))
        {
            return null;
        }

        return $"{CiRulePrefix}{Sanitize(runner.GitHubRunId)}-";
    }

    /// <summary>Whether a rule name follows the CI convention and is therefore eligible for pruning.</summary>
    public static bool IsCiRule(string ruleName) =>
        !string.IsNullOrEmpty(ruleName) && ruleName.StartsWith(CiRulePrefix, StringComparison.Ordinal);

    /// <summary>Extracts the UTC creation minute from a CI rule name, or <see langword="null"/> when the name carries none.</summary>
    public static DateTimeOffset? ParseCiTimestamp(string ruleName)
    {
        if (!IsCiRule(ruleName))
        {
            return null;
        }

        var lastDash = ruleName.LastIndexOf('-');
        if (lastDash < 0 || lastDash == ruleName.Length - 1)
        {
            return null;
        }

        var stamp = ruleName[(lastDash + 1)..];
        return DateTimeOffset.TryParseExact(stamp, CiTimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Reduces free text to <c>[A-Za-z0-9_.-]</c>, collapsing runs of separators; never empty.</summary>
    public static string Sanitize(string value)
    {
        var cleaned = InvalidCharacters().Replace(value ?? string.Empty, "-");
        cleaned = RepeatedDashes().Replace(cleaned, "-").Trim('-', '.');
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }

    /// <summary><c>DOMAIN\user</c> → <c>user</c>; <c>user@tenant</c> → <c>user</c>.</summary>
    private static string StripAccountDomain(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return string.Empty;
        }

        var backslash = userName.LastIndexOf('\\');
        if (backslash >= 0)
        {
            return userName[(backslash + 1)..];
        }

        var at = userName.IndexOf('@');
        return at > 0 ? userName[..at] : userName;
    }

    private static string Truncate(string name) => name.Length <= MaxLength ? name : name[..MaxLength].TrimEnd('-', '.');

    [GeneratedRegex("[^A-Za-z0-9_.-]")]
    private static partial Regex InvalidCharacters();

    [GeneratedRegex("-{2,}")]
    private static partial Regex RepeatedDashes();
}
