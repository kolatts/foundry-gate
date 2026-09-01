using System.Text.RegularExpressions;
using FoundryGate.Domain.Constants;

namespace FoundryGate.Tests.Predeployment.Domain;

/// <summary>
/// <see cref="GatewayTiers"/> must match the APIM products <c>infra/main.bicep</c> actually creates
/// — a tier id that exists in code but not in the gateway (or vice versa) breaks subscription
/// provisioning silently. Parses the bicep <c>quotaTiers</c> parameter's default value rather than
/// trusting a comment.
/// </summary>
public class GatewayTiersTests
{
    [Fact]
    public void All_contains_every_tier_in_declaration_order_and_Default_is_one_of_them()
    {
        Assert.Equal([GatewayTiers.Standard, GatewayTiers.Power, GatewayTiers.Unlimited], GatewayTiers.All);
        Assert.Contains(GatewayTiers.Default, GatewayTiers.All);
        Assert.Equal(GatewayTiers.All.Count, GatewayTiers.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Tier_ids_match_the_quotaTiers_names_in_infra_main_bicep_in_the_same_order()
    {
        var bicep = File.ReadAllText(Path.Combine(FindRepoRoot(), "infra", "main.bicep"));

        // The default value of `param quotaTiers array = [ ... ]` runs from that line to the first
        // line that is exactly "]" — each entry's `name: '<id>'` is the APIM product id.
        var block = Regex.Match(bicep, @"param quotaTiers array = \[(?<body>.*?)^\]", RegexOptions.Singleline | RegexOptions.Multiline);
        Assert.True(block.Success, "Could not find `param quotaTiers array = [ ... ]` in infra/main.bicep.");

        var bicepTierNames = Regex.Matches(block.Groups["body"].Value, @"^\s*name:\s*'(?<name>[^']+)'", RegexOptions.Multiline)
            .Select(m => m.Groups["name"].Value)
            .ToList();

        Assert.Equal(bicepTierNames, GatewayTiers.All);
        Assert.Equal(GatewayTiers.Default, bicepTierNames[0]); // bicep's defaultProductId is quotaTiers[0].name
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
