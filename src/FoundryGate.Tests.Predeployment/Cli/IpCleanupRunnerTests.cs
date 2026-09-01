using FoundryGate.Cli.Commands.Ip;
using FoundryGate.Cli.Commands.Ip.Cleanup;
using FoundryGate.Cli.Helpers;
using FoundryGate.Tests.Predeployment.Support;

namespace FoundryGate.Tests.Predeployment.Cli;

public class IpCleanupRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 14, 0, 0, TimeSpan.Zero);
    private const string Server = "sql-foundrygate-dev-e7k2";

    private static SqlFirewallRuleInfo Rule(string name, string ip = "203.0.113.10") => new(name, ip, ip);

    private readonly FakeAzureSqlServerClient _client = new FakeAzureSqlServerClient().AddServer("rg-foundrygate-dev", Server,
        Rule("AllowAllWindowsAzureIps", "0.0.0.0"),
        Rule("fg-dev-LAPTOP-kolat"),
        Rule("hand-made-rule"),
        Rule("gha-100-202609011000"),   // 4 h old   → stale
        Rule("gha-200-202609011330"),   // 30 min old → fresh
        Rule("gha-300-202609011359"),   // 1 min old  → fresh, but belongs to the current run (300)
        Rule("gha-legacy"));            // no timestamp → stale

    private readonly StringWriter _output = new();

    private IpCleanupRunner CreateRunner(RunnerContext runner) => new(_client, runner, new MutableTimeProvider(Now), _output);

    private static IpCleanupRequest Request(double olderThanHours = 2, bool dryRun = false, string env = "dev") =>
        new(env, null, null, TimeSpan.FromHours(olderThanHours), dryRun);

    [Fact]
    public async Task Removes_stale_and_own_CI_rules_and_keeps_everything_else()
    {
        var result = await CreateRunner(new RunnerContext(true, "300", "runner", "host")).RunAsync(Request(), CancellationToken.None);

        Assert.Equal(["gha-100-202609011000", "gha-300-202609011359", "gha-legacy"], result.Removed);
        Assert.Equal(["AllowAllWindowsAzureIps", "fg-dev-LAPTOP-kolat", "gha-200-202609011330", "hand-made-rule"], result.Kept);
        Assert.Equal(
            [$"delete:{Server}/gha-100-202609011000", $"delete:{Server}/gha-300-202609011359", $"delete:{Server}/gha-legacy"],
            _client.Writes);
        Assert.DoesNotContain(_client.RulesOn(Server), r => r.Name == "gha-100-202609011000");
        Assert.Contains(_client.RulesOn(Server), r => r.Name == "fg-dev-LAPTOP-kolat");

        var output = _output.ToString();
        Assert.Contains("removed gha-100-202609011000 (203.0.113.10) — created 4 h ago", output);
        Assert.Contains("removed gha-300-202609011359 (203.0.113.10) — created by this workflow run", output);
        Assert.Contains("removed gha-legacy (203.0.113.10) — CI rule without a creation timestamp", output);
        Assert.Contains("Removed 3 CI firewall rule(s); 4 other rule(s) untouched.", output);
    }

    [Fact]
    public async Task Outside_CI_only_age_decides()
    {
        var result = await CreateRunner(new RunnerContext(false, null, "kolat", "LAPTOP")).RunAsync(Request(), CancellationToken.None);

        Assert.Equal(["gha-100-202609011000", "gha-legacy"], result.Removed);
        Assert.Contains("gha-300-202609011359", result.Kept);
    }

    [Fact]
    public async Task Older_than_zero_removes_every_timestamped_CI_rule()
    {
        var result = await CreateRunner(new RunnerContext(false, null, "kolat", "LAPTOP")).RunAsync(Request(olderThanHours: 0), CancellationToken.None);

        Assert.Equal(["gha-100-202609011000", "gha-200-202609011330", "gha-300-202609011359", "gha-legacy"], result.Removed);
        Assert.Equal(["AllowAllWindowsAzureIps", "fg-dev-LAPTOP-kolat", "hand-made-rule"], result.Kept);
    }

    [Fact]
    public async Task Dry_run_reports_without_deleting()
    {
        var result = await CreateRunner(new RunnerContext(true, "300", "runner", "host")).RunAsync(Request(dryRun: true), CancellationToken.None);

        Assert.Equal(["gha-100-202609011000", "gha-300-202609011359", "gha-legacy"], result.Removed);
        Assert.Empty(_client.Writes);
        Assert.Equal(7, _client.RulesOn(Server).Count);
        Assert.Contains("[dry run]", _output.ToString());
        Assert.Contains("would remove gha-100-202609011000", _output.ToString());
        Assert.Contains("Would remove 3 CI firewall rule(s)", _output.ToString());
    }

    [Fact]
    public async Task Reports_when_nothing_is_stale()
    {
        _ = _client.AddServer("rg-foundrygate-dev", Server, Rule("AllowAllWindowsAzureIps", "0.0.0.0"), Rule("gha-200-202609011330"));

        var result = await CreateRunner(new RunnerContext(false, null, "kolat", "LAPTOP")).RunAsync(Request(), CancellationToken.None);

        Assert.Empty(result.Removed);
        Assert.Empty(_client.Writes);
        Assert.Contains("No stale CI firewall rules found.", _output.ToString());
    }

    [Fact]
    public async Task Rejects_a_negative_threshold()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRunner(new RunnerContext(false, null, "kolat", "LAPTOP")).RunAsync(Request(olderThanHours: -1), CancellationToken.None));

        Assert.Contains("--older-than", ex.Message);
        Assert.Empty(_client.Writes);
    }

    [Fact]
    public async Task Honours_an_explicit_server_and_resource_group()
    {
        _ = _client.AddServer("rg-custom", "sql-elsewhere", Rule("gha-1-202609010000"), Rule("fg-dev-x-y"));

        var result = await CreateRunner(new RunnerContext(false, null, "kolat", "LAPTOP"))
            .RunAsync(new IpCleanupRequest("dev", "sql-elsewhere", "rg-custom", TimeSpan.FromHours(2), DryRun: false), CancellationToken.None);

        Assert.Equal(["gha-1-202609010000"], result.Removed);
        Assert.Equal(["delete:sql-elsewhere/gha-1-202609010000"], _client.Writes);
        Assert.Equal(7, _client.RulesOn(Server).Count);
    }
}
