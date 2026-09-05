using System.Text.RegularExpressions;

namespace FoundryGate.Tests.Predeployment.Api.Configuration;

/// <summary>
/// Two invariants that live entirely inside <c>infra/main.bicep</c>'s shipped defaults, both of
/// which dev violated on its first day while every resource reported healthy.
/// <list type="number">
/// <item><b>Every alias names a deployment the template creates (#259).</b> The alias map and the
/// deployment list are independent parameters that nothing compared, so dev's gateway answered
/// <c>404 DeploymentNotFound</c> on every model on every tier while <c>Deploy All</c> was green.</item>
/// <item><b>An OpenAI alias's deployment can serve the TPM of every tier that reaches it (#260).</b>
/// Below that ceiling the developer's own meter is unreachable: every throttle is the shared
/// deployment saturating, with <c>x-fg-remaining-tpm</c> still showing headroom on the refusal.</item>
/// </list>
/// Scope caveat, as in <see cref="GatewayOptionsModelAliasesTests"/>: this pins the parameters'
/// <em>defaults</em>. A fork that overrides them owns its own arithmetic — which is why the deploy
/// also emits <c>modelCapacityWarnings</c> from the values actually used.
/// </summary>
public class InfraModelDeploymentTests
{
    /// <summary>Anthropic capacity is a create-once, money-spending decision (#205), so the shipped Claude defaults are reported by the deploy rather than asserted here.</summary>
    private const string OpenAiFormat = "OpenAI";

    [Fact]
    public void Every_alias_names_a_deployment_the_template_creates()
    {
        var bicep = ReadMainBicep();
        var declared = ParseDeployments(bicep).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var referenced = ParseAliasDeployments(bicep);

        Assert.NotEmpty(declared);
        Assert.NotEmpty(referenced);
        Assert.All(referenced, deployment => Assert.True(
            declared.Contains(deployment),
            $"infra/main.bicep's productModelAliases routes at deployment '{deployment}', which neither " +
            $"pooledModelDeployments nor primaryOnlyModelDeployments declares. The gateway would answer " +
            $"404 DeploymentNotFound for every request naming it (#259). Declared: {string.Join(", ", declared.Order(StringComparer.Ordinal))}."));
    }

    [Fact]
    public void An_openai_deployment_can_serve_the_tpm_of_every_tier_that_reaches_it()
    {
        var bicep = ReadMainBicep();
        var deployments = ParseDeployments(bicep).ToDictionary(d => d.Name, StringComparer.Ordinal);
        var tierTpm = ParseTierTpm(bicep);
        var regionCount = ParseFoundryRegionCount(bicep);

        Assert.NotEmpty(tierTpm);

        foreach (var (tier, alias, deploymentName) in ParseAliasReach(bicep))
        {
            if (!deployments.TryGetValue(deploymentName, out var deployment) || deployment.Format != OpenAiFormat)
            {
                continue;
            }

            var servableTpm = deployment.Capacity * 1000 * (deployment.Pooled ? regionCount : 1);
            var allowedTpm = tierTpm[tier];

            Assert.True(
                servableTpm >= allowedTpm,
                $"Tier '{tier}' allows {allowedTpm} TPM, but alias '{alias}' routes at OpenAI deployment " +
                $"'{deploymentName}', which can serve {servableTpm} TPM ({deployment.Capacity} capacity units" +
                $"{(deployment.Pooled ? $" x {regionCount} regions" : string.Empty)}). Developers on that tier are " +
                $"throttled by the deployment before their own meter is ever reached (#260).");
        }
    }

    private sealed record ModelDeployment(string Name, string Format, int Capacity, bool Pooled);

    private static IReadOnlyList<ModelDeployment> ParseDeployments(string bicep) =>
    [
        .. ParseDeploymentArray(bicep, "pooledModelDeployments", pooled: true),
        .. ParseDeploymentArray(bicep, "primaryOnlyModelDeployments", pooled: false),
    ];

    private static IEnumerable<ModelDeployment> ParseDeploymentArray(string bicep, string parameterName, bool pooled)
    {
        var body = MatchParameterBody(bicep, parameterName, open: '[', close: ']');

        foreach (Match entry in Regex.Matches(body, @"\{(?<entry>[^{}]*)\}", RegexOptions.Singleline))
        {
            var text = entry.Groups["entry"].Value;
            var name = Regex.Match(text, @"name:\s*'(?<v>[^']+)'").Groups["v"].Value;
            var format = Regex.Match(text, @"format:\s*'(?<v>[^']+)'").Groups["v"].Value;
            var capacity = Regex.Match(text, @"^\s*capacity:\s*(?<v>\d+)", RegexOptions.Multiline).Groups["v"].Value;

            Assert.False(string.IsNullOrEmpty(name), $"A {parameterName} entry has no name in infra/main.bicep.");
            Assert.False(string.IsNullOrEmpty(capacity), $"{parameterName} entry '{name}' has no capacity in infra/main.bicep.");

            yield return new ModelDeployment(name, format, int.Parse(capacity), pooled);
        }
    }

    private static IReadOnlyList<string> ParseAliasDeployments(string bicep) =>
        [.. ParseAliasReach(bicep).Select(r => r.Deployment).Distinct(StringComparer.Ordinal)];

    private static IEnumerable<(string Tier, string Alias, string Deployment)> ParseAliasReach(string bicep)
    {
        var body = MatchParameterBody(bicep, "productModelAliases", open: '{', close: '}');
        var tierBlocks = Regex.Matches(
            body,
            @"^  (?<tier>[A-Za-z0-9_]+):\s*\{(?<entries>.*?)^  \}",
            RegexOptions.Singleline | RegexOptions.Multiline);

        foreach (Match tierBlock in tierBlocks)
        {
            var tier = tierBlock.Groups["tier"].Value;
            var aliases = Regex.Matches(
                tierBlock.Groups["entries"].Value,
                @"(?<alias>[A-Za-z0-9_-]+):\s*\{\s*deployment:\s*'(?<deployment>[^']+)'");

            foreach (Match alias in aliases)
            {
                yield return (tier, alias.Groups["alias"].Value, alias.Groups["deployment"].Value);
            }
        }
    }

    private static IReadOnlyDictionary<string, int> ParseTierTpm(string bicep)
    {
        var body = MatchParameterBody(bicep, "quotaTiers", open: '[', close: ']');
        var tiers = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (Match entry in Regex.Matches(body, @"\{(?<entry>[^{}]*)\}", RegexOptions.Singleline))
        {
            var text = entry.Groups["entry"].Value;
            var name = Regex.Match(text, @"name:\s*'(?<v>[^']+)'").Groups["v"].Value;
            var tpm = Regex.Match(text, @"^\s*tpm:\s*(?<v>\d+)", RegexOptions.Multiline).Groups["v"].Value;

            Assert.False(string.IsNullOrEmpty(name), "A quotaTiers entry has no name in infra/main.bicep.");
            Assert.False(string.IsNullOrEmpty(tpm), $"quotaTiers entry '{name}' has no tpm in infra/main.bicep.");

            tiers[name] = int.Parse(tpm);
        }

        return tiers;
    }

    private static int ParseFoundryRegionCount(string bicep)
    {
        var match = Regex.Match(bicep, @"^param foundryRegions array = \[(?<regions>[^\]]*)\]", RegexOptions.Multiline);
        Assert.True(match.Success, "Could not find `param foundryRegions array = [ ... ]` in infra/main.bicep.");

        return Regex.Matches(match.Groups["regions"].Value, @"'[^']+'").Count;
    }

    /// <summary>The body of a top-level <c>param x T = [ … ]</c> / <c>{ … }</c>, matched to its closing bracket at column 0.</summary>
    private static string MatchParameterBody(string bicep, string parameterName, char open, char close)
    {
        var pattern = $@"^param {Regex.Escape(parameterName)} \w+ = \{open}\r?\n(?<body>.*?)^\{close}";
        var match = Regex.Match(bicep, pattern, RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.True(match.Success, $"Could not find `param {parameterName} ... = {open} ... {close}` in infra/main.bicep.");

        return match.Groups["body"].Value;
    }

    private static string ReadMainBicep() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "infra", "main.bicep"));

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
