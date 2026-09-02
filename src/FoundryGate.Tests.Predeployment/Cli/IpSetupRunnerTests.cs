using System.Net;
using FoundryGate.Cli.Commands.Ip;
using FoundryGate.Cli.Commands.Ip.Setup;
using FoundryGate.Cli.Helpers;
using FoundryGate.Tests.Predeployment.Support;

namespace FoundryGate.Tests.Predeployment.Cli;

public class IpSetupRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 14, 37, 0, TimeSpan.Zero);
    private static readonly RunnerContext CiRunner = new(true, "555", "runner", "fv-az1");
    private static readonly RunnerContext DevRunner = new(false, null, "kolat", "LAPTOP");

    private readonly FakeAzureSqlServerClient _client = new FakeAzureSqlServerClient()
        .AddServer("rg-foundrygate-dev", "sql-foundrygate-dev-e7k2", new SqlFirewallRuleInfo("AllowAllWindowsAzureIps", "0.0.0.0", "0.0.0.0"));

    private readonly StringWriter _output = new();
    private readonly List<string> _prompts = [];
    private bool _confirmAnswer = true;

    private IpSetupRunner CreateRunner(RunnerContext runner, string detectedIp = "203.0.113.10") =>
        new(_client, new FixedPublicIpProvider(IPAddress.Parse(detectedIp)), runner, new MutableTimeProvider(Now), _output, q =>
        {
            _prompts.Add(q);
            return _confirmAnswer;
        });

    private static IpSetupRequest Request(string env = "dev", string? server = null, string? rg = null, string? name = null, string? ip = null, bool yes = true) =>
        new(env, server, rg, name, ip, yes);

    [Fact]
    public async Task Creates_a_CI_rule_for_the_detected_IP_on_the_server_resolved_by_prefix()
    {
        var exit = await CreateRunner(CiRunner).RunAsync(Request(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(["upsert:sql-foundrygate-dev-e7k2/gha-555-202609011437=203.0.113.10"], _client.Writes);
        Assert.Contains(_client.RulesOn("sql-foundrygate-dev-e7k2"), r => r.Name == "gha-555-202609011437" && r.StartIpAddress == "203.0.113.10" && r.EndIpAddress == "203.0.113.10");
        Assert.Contains("Created firewall rule 'gha-555-202609011437' (203.0.113.10-203.0.113.10) on sql-foundrygate-dev-e7k2.", _output.ToString());
        Assert.Empty(_prompts);
    }

    [Fact]
    public async Task Creates_a_developer_rule_outside_CI()
    {
        _ = await CreateRunner(DevRunner).RunAsync(Request(), CancellationToken.None);

        Assert.Equal(["upsert:sql-foundrygate-dev-e7k2/fg-dev-LAPTOP-kolat=203.0.113.10"], _client.Writes);
    }

    [Fact]
    public async Task Is_idempotent_when_the_rule_already_allows_the_same_IP()
    {
        _ = _client.AddServer("rg-foundrygate-dev", "sql-foundrygate-dev-e7k2",
            new SqlFirewallRuleInfo("AllowAllWindowsAzureIps", "0.0.0.0", "0.0.0.0"),
            new SqlFirewallRuleInfo("fg-dev-LAPTOP-kolat", "203.0.113.10", "203.0.113.10"));

        var exit = await CreateRunner(DevRunner).RunAsync(Request(yes: false), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Empty(_client.Writes);
        Assert.Empty(_prompts);
        Assert.Contains("already allows 203.0.113.10 — nothing to do", _output.ToString());
    }

    [Fact]
    public async Task Updates_the_rule_when_the_IP_changed()
    {
        _ = _client.AddServer("rg-foundrygate-dev", "sql-foundrygate-dev-e7k2",
            new SqlFirewallRuleInfo("fg-dev-LAPTOP-kolat", "198.51.100.1", "198.51.100.1"));

        var exit = await CreateRunner(DevRunner).RunAsync(Request(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(["upsert:sql-foundrygate-dev-e7k2/fg-dev-LAPTOP-kolat=203.0.113.10"], _client.Writes);
        Assert.Contains("Updated firewall rule 'fg-dev-LAPTOP-kolat'", _output.ToString());
    }

    [Fact]
    public async Task Prompts_before_writing_unless_yes_and_honours_a_refusal()
    {
        _confirmAnswer = false;

        var exit = await CreateRunner(DevRunner).RunAsync(Request(yes: false), CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Single(_prompts);
        Assert.Contains("create firewall rule 'fg-dev-LAPTOP-kolat' on sql-foundrygate-dev-e7k2", _prompts[0]);
        Assert.Empty(_client.Writes);
        Assert.Contains("Cancelled.", _output.ToString());
    }

    [Fact]
    public async Task Honours_explicit_ip_and_name_overrides()
    {
        _ = await CreateRunner(DevRunner).RunAsync(Request(name: "my.rule_1", ip: " 192.0.2.7 "), CancellationToken.None);

        Assert.Equal(["upsert:sql-foundrygate-dev-e7k2/my.rule_1=192.0.2.7"], _client.Writes);
    }

    [Theory]
    [InlineData("2001:db8::1")]
    [InlineData("not-an-ip")]
    [InlineData("256.1.1.1")]
    public async Task Rejects_an_ip_override_that_is_not_IPv4(string ip)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunner(DevRunner).RunAsync(Request(ip: ip), CancellationToken.None));

        Assert.Contains("IPv4", ex.Message);
        Assert.Empty(_client.Writes);
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("rule/with/slash")]
    public async Task Rejects_an_invalid_rule_name_override(string name)
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunner(DevRunner).RunAsync(Request(name: name), CancellationToken.None));

        Assert.Contains("invalid", ex.Message);
        Assert.Empty(_client.Writes);
    }

    [Fact]
    public async Task Resolves_production_GitHub_Environment_to_the_prod_resource_group()
    {
        _ = _client.AddServer("rg-foundrygate-prod", "sql-foundrygate-prod-e7k2");

        _ = await CreateRunner(CiRunner).RunAsync(Request(env: "production"), CancellationToken.None);

        Assert.Equal(["upsert:sql-foundrygate-prod-e7k2/gha-555-202609011437=203.0.113.10"], _client.Writes);
        Assert.Contains("Environment:    prod", _output.ToString());
    }

    [Fact]
    public async Task Uses_an_explicit_server_and_resource_group_when_given()
    {
        _ = _client.AddServer("rg-custom", "sql-somewhere-else");

        _ = await CreateRunner(CiRunner).RunAsync(Request(server: "sql-somewhere-else", rg: "rg-custom"), CancellationToken.None);

        Assert.Equal(["upsert:sql-somewhere-else/gha-555-202609011437=203.0.113.10"], _client.Writes);
    }

    [Fact]
    public async Task Fails_clearly_when_the_environment_has_no_server()
    {
        _ = _client.AddServer("rg-foundrygate-test", "apim-not-a-sql-server");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunner(CiRunner).RunAsync(Request(env: "test"), CancellationToken.None));

        Assert.Contains("No SQL server named 'sql-foundrygate-test-*'", ex.Message);
        Assert.Contains("--server", ex.Message);
    }

    [Fact]
    public async Task Fails_clearly_when_the_prefix_is_ambiguous()
    {
        _ = _client.AddServer("rg-foundrygate-dev", "sql-foundrygate-dev-zzzz");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunner(CiRunner).RunAsync(Request(), CancellationToken.None));

        Assert.Contains("Found 2 SQL servers", ex.Message);
        Assert.Contains("sql-foundrygate-dev-e7k2, sql-foundrygate-dev-zzzz", ex.Message);
    }

    [Fact]
    public async Task Fails_clearly_when_an_explicit_server_does_not_exist()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunner(CiRunner).RunAsync(Request(server: "sql-nope"), CancellationToken.None));

        Assert.Contains("'sql-nope' was not found in resource group 'rg-foundrygate-dev'", ex.Message);
    }

    private sealed class FixedPublicIpProvider(IPAddress address) : IPublicIpProvider
    {
        public Task<IPAddress> GetPublicIpAsync(CancellationToken cancellationToken) => Task.FromResult(address);
    }
}
