using FoundryGate.Cli.Commands.Ip;

namespace FoundryGate.Tests.Predeployment.Cli;

public class FirewallRuleNamingTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 14, 37, 59, TimeSpan.Zero);

    [Fact]
    public void GitHub_Actions_rule_is_gha_runid_and_UTC_minute()
    {
        var runner = new RunnerContext(IsGitHubActions: true, GitHubRunId: "1234567890", UserName: "runner", MachineName: "fv-az123-456");

        Assert.Equal("gha-1234567890-202609011437", FirewallRuleNaming.ForSetup(runner, Now));
    }

    [Fact]
    public void GitHub_Actions_rule_timestamp_is_converted_to_UTC()
    {
        var runner = new RunnerContext(true, "42", "runner", "host");
        var local = new DateTimeOffset(2026, 9, 1, 10, 5, 0, TimeSpan.FromHours(-4));

        Assert.Equal("gha-42-202609011405", FirewallRuleNaming.ForSetup(runner, local));
    }

    [Fact]
    public void GitHub_Actions_without_a_run_id_still_produces_a_prunable_name()
    {
        var runner = new RunnerContext(true, null, "runner", "host");

        var name = FirewallRuleNaming.ForSetup(runner, Now);

        Assert.Equal("gha-unknown-202609011437", name);
        Assert.True(FirewallRuleNaming.IsCiRule(name));
        Assert.Null(FirewallRuleNaming.OwnCiRulePrefix(runner));
    }

    [Theory]
    [InlineData("kolat", "LAPTOP-01", "fg-dev-LAPTOP-01-kolat")]
    [InlineData("CONTOSO\\sunny.k", "dev box", "fg-dev-dev-box-sunny.k")]
    [InlineData("sunny@contoso.com", "mac", "fg-dev-mac-sunny")]
    [InlineData("a b/c", "x*y", "fg-dev-x-y-a-b-c")]
    [InlineData("", "", "fg-dev-unknown-unknown")]
    public void Developer_rule_is_fg_dev_machine_user_sanitised(string user, string machine, string expected)
    {
        var runner = new RunnerContext(false, null, user, machine);

        Assert.Equal(expected, FirewallRuleNaming.ForSetup(runner, Now));
    }

    [Fact]
    public void Developer_rule_is_never_a_CI_rule_and_never_pruned()
    {
        var name = FirewallRuleNaming.ForSetup(new RunnerContext(false, null, "kolat", "LAPTOP"), Now);

        Assert.False(FirewallRuleNaming.IsCiRule(name));
        Assert.Null(FirewallRuleNaming.ParseCiTimestamp(name));
    }

    [Fact]
    public void Names_are_capped_at_Azure_limit()
    {
        var runner = new RunnerContext(false, null, new string('u', 100), new string('m', 100));

        var name = FirewallRuleNaming.ForSetup(runner, Now);

        Assert.True(name.Length <= FirewallRuleNaming.MaxLength, $"length {name.Length}");
        Assert.StartsWith("fg-dev-", name, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnCiRulePrefix_matches_every_rule_of_the_same_run_regardless_of_minute()
    {
        var runner = new RunnerContext(true, "777", "runner", "host");

        var prefix = FirewallRuleNaming.OwnCiRulePrefix(runner);
        var attempt1 = FirewallRuleNaming.ForSetup(runner, Now);
        var attempt2 = FirewallRuleNaming.ForSetup(runner, Now.AddMinutes(20));

        Assert.Equal("gha-777-", prefix);
        Assert.StartsWith(prefix, attempt1, StringComparison.Ordinal);
        Assert.StartsWith(prefix, attempt2, StringComparison.Ordinal);
        Assert.Null(FirewallRuleNaming.OwnCiRulePrefix(new RunnerContext(false, "777", "u", "m")));
    }

    [Theory]
    [InlineData("gha-1234567890-202609011437", 2026, 9, 1, 14, 37)]
    [InlineData("gha-unknown-202601010000", 2026, 1, 1, 0, 0)]
    public void ParseCiTimestamp_reads_the_UTC_minute_back(string name, int y, int m, int d, int h, int min)
    {
        Assert.Equal(new DateTimeOffset(y, m, d, h, min, 0, TimeSpan.Zero), FirewallRuleNaming.ParseCiTimestamp(name));
    }

    [Theory]
    [InlineData("gha-1234567890")]
    [InlineData("gha-1234567890-")]
    [InlineData("gha-1234567890-notadate")]
    [InlineData("gha-1234567890-20260901")]
    [InlineData("fg-dev-host-user")]
    [InlineData("AllowAllWindowsAzureIps")]
    [InlineData("")]
    public void ParseCiTimestamp_returns_null_for_names_without_a_valid_stamp(string name)
    {
        Assert.Null(FirewallRuleNaming.ParseCiTimestamp(name));
    }

    [Theory]
    [InlineData("gha-1", true)]
    [InlineData("gha-", true)]
    [InlineData("GHA-1", false)]
    [InlineData("fg-dev-x", false)]
    [InlineData("AllowAllWindowsAzureIps", false)]
    public void IsCiRule_is_an_exact_case_sensitive_prefix_check(string name, bool expected)
    {
        Assert.Equal(expected, FirewallRuleNaming.IsCiRule(name));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has space", "has-space")]
    [InlineData("a//b\\\\c", "a-b-c")]
    [InlineData("--lead-and-trail--", "lead-and-trail")]
    [InlineData("dots.are.fine", "dots.are.fine")]
    [InlineData("under_score", "under_score")]
    [InlineData("!!!", "unknown")]
    public void Sanitize_keeps_only_letters_digits_underscore_dot_dash(string input, string expected)
    {
        Assert.Equal(expected, FirewallRuleNaming.Sanitize(input));
    }
}
