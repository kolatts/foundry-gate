using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using FoundryGate.Api.Configuration;
using FoundryGate.Domain.Constants;
using FoundryGate.Tests.Predeployment.Support;
using Imagile.Framework.Configuration.Extensions;
using Microsoft.Extensions.Configuration;

namespace FoundryGate.Tests.Predeployment.Api.Configuration;

/// <summary>
/// <see cref="GatewayTierOptions"/> validation, plus the parity check that the tier caps shipped in
/// <c>appsettings.json</c> match the products <c>infra/main.bicep</c>'s <c>quotaTiers</c> actually
/// creates — mirroring <c>GatewayTiersTests</c> for the ids. A cap that drifts between the two means
/// resolution maps a quota onto a product whose policy enforces a different number.
/// </summary>
public class GatewayTierOptionsTests
{
    [Fact]
    public void Shipped_defaults_are_valid()
    {
        Assert.Empty(Validate(TestGatewayTiers.Options()));
    }

    [Fact]
    public void Empty_tier_list_is_rejected()
    {
        var options = new GatewayTierOptions();

        var error = Assert.Single(Validate(options));
        Assert.Contains("at least one tier", error.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_product_id_is_rejected_and_the_message_lists_the_valid_ids()
    {
        var options = TestGatewayTiers.Options();
        options.Tiers[0].ProductId = "platinum";

        var error = Assert.Single(Validate(options));
        Assert.Contains("platinum", error.ErrorMessage, StringComparison.Ordinal);
        Assert.All(GatewayTiers.All, id => Assert.Contains(id, error.ErrorMessage, StringComparison.Ordinal));
    }

    [Fact]
    public void Duplicate_product_id_is_rejected()
    {
        var options = TestGatewayTiers.Options();
        options.Tiers[1].ProductId = GatewayTiers.Standard;

        Assert.Contains(Validate(options), r => r.ErrorMessage!.Contains("more than once", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_unlimited_tiers_are_rejected()
    {
        var options = TestGatewayTiers.Options();
        options.Tiers[1].MonthlyTokenQuota = 0;

        Assert.Contains(Validate(options), r => r.ErrorMessage!.Contains("exactly one unlimited tier", StringComparison.Ordinal));
    }

    [Fact]
    public void No_unlimited_tier_is_rejected()
    {
        var options = TestGatewayTiers.Options();
        options.Tiers[2].MonthlyTokenQuota = 1;

        Assert.Contains(Validate(options), r => r.ErrorMessage!.Contains("exactly one unlimited tier", StringComparison.Ordinal));
    }

    [Fact]
    public void Only_an_unlimited_tier_is_rejected()
    {
        var options = new GatewayTierOptions
        {
            Tiers = [new GatewayTier { ProductId = GatewayTiers.Unlimited, MonthlyTokenQuota = 0 }],
        };

        Assert.Contains(Validate(options), r => r.ErrorMessage!.Contains("at least one finite tier", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateRecursively_on_AppSettings_surfaces_Gateway_Tiers_errors()
    {
        // The same call Program.cs makes: an invalid tier table must fail startup, with the path.
        var appSettings = AppSettingsValidationTests.ValidAppSettings();
        appSettings.Gateway.Tiers.Clear();

        var exception = Assert.Throws<Imagile.Framework.Configuration.Exceptions.ConfigurationValidationException>(appSettings.ValidateRecursively);

        Assert.Contains("Tiers", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Same scope caveat as <c>GatewayTiersTests</c>: this pins the bicep parameter's <em>default</em>
    /// value against the Api's shipped <c>appsettings.json</c>. A fork that overrides <c>quotaTiers</c>
    /// at deploy time must override <c>Gateway:Tiers</c> to match (#108 will have infra emit it).
    /// </summary>
    [Fact]
    public void Shipped_appsettings_tiers_match_infra_main_bicep_quotaTiers_ids_and_caps_in_order()
    {
        var repoRoot = FindRepoRoot();

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(repoRoot, "src", "FoundryGate.Api", "appsettings.json"), optional: false)
            .Build();
        var options = configuration.GetSection("Gateway").Get<GatewayTierOptions>();
        Assert.NotNull(options);
        Assert.Empty(Validate(options));

        var bicep = File.ReadAllText(Path.Combine(repoRoot, "infra", "main.bicep"));
        var block = Regex.Match(bicep, @"^param quotaTiers array = \[\r?\n(?<body>.*?)^\]", RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.True(block.Success, "Could not find `param quotaTiers array = [ ... ]` in infra/main.bicep.");

        var bicepTiers = Regex.Matches(block.Groups["body"].Value, @"^  \{\r?\n(?<item>.*?)^  \}", RegexOptions.Singleline | RegexOptions.Multiline)
            .Select(item => item.Groups["item"].Value)
            .Select(item => (
                Name: Regex.Match(item, @"^ {4}name:\s*'(?<name>[^']+)'\s*$", RegexOptions.Multiline).Groups["name"].Value,
                Quota: long.Parse(Regex.Match(item, @"^ {4}monthlyTokenQuota:\s*(?<q>\d+)\s*$", RegexOptions.Multiline).Groups["q"].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();

        Assert.NotEmpty(bicepTiers);
        Assert.Equal(bicepTiers, options.Tiers.Select(t => (t.ProductId, t.MonthlyTokenQuota)));
    }

    private static IList<ValidationResult> Validate(GatewayTierOptions options)
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
