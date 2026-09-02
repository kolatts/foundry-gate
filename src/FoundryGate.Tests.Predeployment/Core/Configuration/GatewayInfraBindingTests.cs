using System.Reflection;
using System.Text.RegularExpressions;
using FoundryGate.Core.Configuration;
using Microsoft.Extensions.Configuration;

namespace FoundryGate.Tests.Predeployment.Core.Configuration;

/// <summary>
/// Drift alarm between <see cref="GatewayOptions"/> and the <c>Gateway__*</c> environment variables
/// <c>infra/modules/control-plane.bicep</c> actually sets — the question #108 asks and that nothing
/// answered automatically: infra is the source of truth for the names, so a rename on either side, a
/// variable infra sets that nothing binds, or a member the control plane needs that infra never
/// supplies, should all fail here rather than in a deployed environment.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately reads the bicep as <em>text</em>, the way <c>SchemaParityTests</c> reads the .sql
/// files: no Bicep/ARM tooling is available in every environment these tests run in, and the repo's
/// own style (<c>{ name: 'Key', value: … }</c>, one per line) parses cleanly. A hand-authored line that
/// strays from that style shows up as a missing variable, not as a silent pass.
/// </para>
/// <para>
/// The <c>__</c> → <c>:</c> step below is what
/// <c>AddEnvironmentVariables()</c> does at runtime; it is applied here rather than by setting real
/// process environment variables, which would be shared mutable state across a parallel test run. What
/// this test guards is the <em>names</em>, not that separator.
/// </para>
/// </remarks>
public class GatewayInfraBindingTests
{
    private const string Section = "Gateway";

    /// <summary>
    /// <c>{ name: 'Gateway__Something', … }</c>, including the interpolated array form
    /// <c>'Gateway__FoundryAccountNames__${i}'</c>.
    /// </summary>
    private static readonly Regex GatewaySettingRegex = new(
        @"name:\s*'(?<name>Gateway__[A-Za-z0-9_]+(?:__\$\{i\})?)'",
        RegexOptions.Compiled);

    /// <summary>
    /// Members of <see cref="GatewayOptions"/> that infra deliberately does <b>not</b> set, and why.
    /// Anything not listed here must appear in the bicep — that is the whole point of the section.
    /// </summary>
    private static readonly Dictionary<string, string> NotSetByInfra = new(StringComparer.Ordinal)
    {
        [nameof(GatewayOptions.Tiers)] =
            "the tier table ships in both hosts' appsettings.json, mirroring infra/main.bicep's quotaTiers " +
            "parameter (GatewayOptionsTiersTests and FunctionsAppSettingsTests keep the three in step). A fork " +
            "that overrides quotaTiers at deploy time must still edit Gateway:Tiers by hand — see issue #201.",
    };

    [Fact]
    public void Every_Gateway_variable_infra_sets_binds_to_a_GatewayOptions_member()
    {
        var names = GatewayVariableNames();
        Assert.NotEmpty(names);

        // One distinct sentinel per variable, so a member bound from the wrong key is a value mismatch
        // rather than an accidental pass. The array form gets two entries, which also proves pool order
        // survives binding.
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (name.EndsWith("__${i}", StringComparison.Ordinal))
            {
                var prefix = ConfigurationKey(name[..^"__${i}".Length]);
                values[$"{prefix}:0"] = "primary-account";
                values[$"{prefix}:1"] = "secondary-account";
            }
            else
            {
                values[ConfigurationKey(name)] = SentinelFor(name);
            }
        }

        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build()
            .GetSection(Section)
            .Get<GatewayOptions>();

        Assert.NotNull(options);
        Assert.Equal("Gateway__SubscriptionId", options.SubscriptionId);
        Assert.Equal("Gateway__ResourceGroup", options.ResourceGroup);
        Assert.Equal("Gateway__ApimName", options.ApimName);
        Assert.Equal("Gateway__ApimGatewayUrl", options.ApimGatewayUrl);
        Assert.Equal("Gateway__LogAnalyticsWorkspaceId", options.LogAnalyticsWorkspaceId);
        Assert.Equal("Gateway__LogAnalyticsWorkspaceResourceId", options.LogAnalyticsWorkspaceResourceId);
        Assert.Equal("Gateway__KeyEncryptionKeyUri", options.KeyEncryptionKeyUri);
        Assert.Equal(["primary-account", "secondary-account"], options.FoundryAccountNames);
    }

    [Fact]
    public void Every_GatewayOptions_member_is_either_set_by_infra_or_listed_as_deliberately_absent()
    {
        var fromInfra = GatewayVariableNames()
            .Select(name => name.EndsWith("__${i}", StringComparison.Ordinal) ? name[..^"__${i}".Length] : name)
            .Select(name => name["Gateway__".Length..])
            .ToHashSet(StringComparer.Ordinal);

        var unaccounted = typeof(GatewayOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanWrite)
            .Select(property => property.Name)
            .Where(name => !fromInfra.Contains(name) && !NotSetByInfra.ContainsKey(name))
            .ToList();

        Assert.True(
            unaccounted.Count == 0,
            $"GatewayOptions member(s) {string.Join(", ", unaccounted)} are neither set by " +
            "infra/modules/control-plane.bicep nor listed in NotSetByInfra. Every member of the Gateway " +
            "section has to come from somewhere on a deployed host: either add a `Gateway__{name}` line to " +
            "the bicep's sharedAppConfig (and to reference/configuration.mdx), or add it to NotSetByInfra " +
            "with the reason it does not need one. Silently binding nothing is how a control plane ends up " +
            "addressing a gateway it cannot reach (#108).");
    }

    [Fact]
    public void Infra_does_not_emit_the_tier_table()
    {
        // Pinned so the note in reference/configuration.mdx stays honest: if infra ever
        // starts emitting Gateway__Tiers__*, the appsettings-is-the-source guidance (#201) has to change with
        // it, and the binder appends configured list items to a pre-populated list rather than replacing
        // it — so having both sources at once would silently duplicate every tier.
        Assert.DoesNotContain(
            GatewayVariableNames(),
            name => name.StartsWith("Gateway__Tiers", StringComparison.Ordinal));
    }

    [Fact]
    public void Both_hosts_get_the_same_gateway_configuration()
    {
        // The Api reads the addressing, the Functions host reads the workspace GUID, and both bind the
        // whole section — so a Gateway variable added for one host must reach the other or the two
        // disagree about the same gateway.
        foreach (var module in new[] { "containerApp", "functionApp" })
        {
            var declaration = ModuleDeclaration(module);
            Assert.Contains("sharedAppConfig", declaration, StringComparison.Ordinal);
            Assert.Contains("foundryAccountConfig", declaration, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The text of one <c>module &lt;name&gt; '…' { … }</c> declaration: from its <c>module</c> keyword to
    /// the next top-level <c>module</c>/<c>output</c>, which is all this needs and avoids matching braces
    /// with a regex.
    /// </summary>
    private static string ModuleDeclaration(string moduleName)
    {
        var bicep = ControlPlaneBicep().Replace("\r\n", "\n", StringComparison.Ordinal);

        var start = bicep.IndexOf($"\nmodule {moduleName} '", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find `module {moduleName} '…'` in infra/modules/control-plane.bicep.");

        var next = new[] { bicep.IndexOf("\nmodule ", start + 1, StringComparison.Ordinal), bicep.IndexOf("\noutput ", start + 1, StringComparison.Ordinal) }
            .Where(index => index >= 0)
            .DefaultIfEmpty(bicep.Length)
            .Min();

        return bicep[start..next];
    }

    private static string ConfigurationKey(string environmentVariableName) =>
        environmentVariableName.Replace("__", ":", StringComparison.Ordinal);

    private static string SentinelFor(string environmentVariableName) => environmentVariableName;

    private static List<string> GatewayVariableNames() =>
        [.. GatewaySettingRegex.Matches(ControlPlaneBicep())
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    private static string ControlPlaneBicep() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "infra", "modules", "control-plane.bicep"));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FoundryGate.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (FoundryGate.sln) from the test output directory.");
    }
}
