using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using FoundryGate.Core.Configuration;
using FoundryGate.Domain.Config;
using FoundryGate.Domain.Constants;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.Extensions.Configuration;

namespace FoundryGate.Tests.Predeployment.Api.Configuration;

/// <summary>
/// The alias half of <see cref="GatewayOptions"/> (#153): the per-tier map infra flattens onto both
/// hosts as <c>Gateway__ModelAliases__{i}__*</c>, its validation, and the parity check that
/// <c>infra/main.bicep</c>'s <c>productModelAliases</c> only names tiers and providers the control
/// plane can actually bind. A tier id that drifts between the two is silent otherwise: the row never
/// matches a caller and every developer on that tier is told the gateway has no models.
/// </summary>
public class GatewayOptionsModelAliasesTests
{
    [Fact]
    public void An_empty_map_is_valid_because_a_fork_on_an_older_deploy_has_no_aliases_yet()
    {
        var options = TestGatewayTiers.Options();

        Assert.Empty(Validate(options));
        Assert.Empty(options.AliasesForTier(GatewayTiers.Standard));
    }

    [Fact]
    public void AliasesForTier_returns_only_that_tiers_aliases_ordered_by_alias()
    {
        var options = OptionsWithAliases();

        var standard = options.AliasesForTier(GatewayTiers.Standard);

        Assert.Equal(["gpt", "sonnet"], standard.Select(a => a.Alias));
        Assert.Equal("claude-sonnet-4-5", standard.Single(a => a.Alias == "sonnet").DeploymentName);
        Assert.Equal(ModelProviderType.OpenAi, standard.Single(a => a.Alias == "gpt").Provider);

        // The whole point of carrying the tier: Unlimited lists opus, Standard must not (#86 — the
        // alias map is the allowlist, so promising it would earn a 403 at the first request).
        Assert.Contains(options.AliasesForTier(GatewayTiers.Unlimited), a => a.Alias == "opus");
        Assert.DoesNotContain(standard, a => a.Alias == "opus");
    }

    [Theory]
    [InlineData("STANDARD")]
    [InlineData("standard")]
    public void AliasesForTier_matches_the_tier_case_insensitively(string tier) =>
        Assert.NotEmpty(OptionsWithAliases().AliasesForTier(tier));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("platinum")]
    public void AliasesForTier_is_empty_rather_than_throwing_for_a_tier_nobody_configured(string tier) =>
        Assert.Empty(OptionsWithAliases().AliasesForTier(tier));

    [Fact]
    public void A_tier_the_gateway_does_not_create_is_rejected_and_the_message_lists_the_valid_ids()
    {
        var options = OptionsWithAliases();
        options.ModelAliases[0].Tier = "platinum";

        var error = Assert.Single(Validate(options), r => r.ErrorMessage!.Contains("platinum", StringComparison.Ordinal));
        Assert.All(GatewayTiers.All, id => Assert.Contains(id, error.ErrorMessage!, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Tier")]
    [InlineData("Alias")]
    [InlineData("DeploymentName")]
    public void A_blank_member_is_rejected_at_the_item_level(string member)
    {
        // ValidateRecursively() does not walk list items, so GatewayModelAlias's own [Required] never
        // runs at startup — the same gap Tiers has, checked the same way.
        var options = OptionsWithAliases();
        var alias = options.ModelAliases[1];
        switch (member)
        {
            case "Tier":
                alias.Tier = "  ";
                break;
            case "Alias":
                alias.Alias = "  ";
                break;
            default:
                alias.DeploymentName = "  ";
                break;
        }

        Assert.Contains(Validate(options), r => r.ErrorMessage!.Contains($"ModelAliases[1].{member} is required", StringComparison.Ordinal));
    }

    [Fact]
    public void The_same_tier_and_alias_twice_is_rejected_because_list_order_would_decide_the_winner()
    {
        var options = OptionsWithAliases();
        options.ModelAliases.Add(new GatewayModelAlias
        {
            Tier = GatewayTiers.Standard,
            Alias = "SONNET",
            DeploymentName = "claude-sonnet-4-1",
            Provider = ModelProviderType.Anthropic,
        });

        Assert.Contains(Validate(options), r => r.ErrorMessage!.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void The_env_var_shape_infra_emits_binds_to_the_options()
    {
        // Exactly the keys infra/modules/control-plane.bicep writes, with bicep's lower-case provider.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:ModelAliases:0:Tier"] = "standard",
                ["Gateway:ModelAliases:0:Alias"] = "sonnet",
                ["Gateway:ModelAliases:0:DeploymentName"] = "claude-sonnet-4-5",
                ["Gateway:ModelAliases:0:Provider"] = "anthropic",
                ["Gateway:ModelAliases:1:Tier"] = "standard",
                ["Gateway:ModelAliases:1:Alias"] = "gpt",
                ["Gateway:ModelAliases:1:DeploymentName"] = "gpt-4-1-mini",
                ["Gateway:ModelAliases:1:Provider"] = "openai",
            })
            .Build();

        var options = configuration.GetSection("Gateway").Get<GatewayOptions>();

        Assert.NotNull(options);
        Assert.Equal(2, options.ModelAliases.Count);
        Assert.Equal(ModelProviderType.Anthropic, options.ModelAliases[0].Provider);
        Assert.Equal(ModelProviderType.OpenAi, options.ModelAliases[1].Provider);
    }

    /// <summary>
    /// Same scope caveat as <c>GatewayOptionsTiersTests</c>: this pins the bicep parameter's
    /// <em>default</em> value. A fork that overrides <c>productModelAliases</c> at deploy time owns
    /// keeping its tiers and providers valid — but the shipped default must never be the thing that
    /// makes a deployed host's alias list silently empty.
    /// </summary>
    [Fact]
    public void Infra_productModelAliases_names_only_tiers_and_providers_the_control_plane_can_bind()
    {
        var bicep = File.ReadAllText(Path.Combine(FindRepoRoot(), "infra", "main.bicep"));
        var block = Regex.Match(bicep, @"^param productModelAliases object = \{\r?\n(?<body>.*?)^\}", RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.True(block.Success, "Could not find `param productModelAliases object = { ... }` in infra/main.bicep.");

        var tiers = Regex.Matches(block.Groups["body"].Value, @"^  (?<tier>[A-Za-z0-9_]+):\s*\{", RegexOptions.Multiline)
            .Select(m => m.Groups["tier"].Value)
            .ToList();
        Assert.NotEmpty(tiers);
        Assert.All(tiers, tier => Assert.Contains(tier, GatewayTiers.All, StringComparer.Ordinal));

        var providers = Regex.Matches(block.Groups["body"].Value, @"provider:\s*'(?<provider>[^']+)'")
            .Select(m => m.Groups["provider"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(providers);
        Assert.All(providers, provider => Assert.True(
            Enum.TryParse<ModelProviderType>(provider, ignoreCase: true, out _),
            $"infra/main.bicep's productModelAliases uses provider '{provider}', which does not bind to {nameof(ModelProviderType)}."));
    }

    private static GatewayOptions OptionsWithAliases()
    {
        var options = TestGatewayTiers.Options();
        options.ModelAliases =
        [
            new GatewayModelAlias { Tier = GatewayTiers.Standard, Alias = "sonnet", DeploymentName = "claude-sonnet-4-5", Provider = ModelProviderType.Anthropic },
            new GatewayModelAlias { Tier = GatewayTiers.Standard, Alias = "gpt", DeploymentName = "gpt-4-1-mini", Provider = ModelProviderType.OpenAi },
            new GatewayModelAlias { Tier = GatewayTiers.Unlimited, Alias = "opus", DeploymentName = "claude-opus-4-5", Provider = ModelProviderType.Anthropic },
        ];
        return options;
    }

    private static IList<ValidationResult> Validate(GatewayOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        return results;
    }

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
