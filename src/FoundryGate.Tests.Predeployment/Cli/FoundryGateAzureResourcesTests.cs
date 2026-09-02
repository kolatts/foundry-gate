using FoundryGate.Cli.Helpers;

namespace FoundryGate.Tests.Predeployment.Cli;

/// <summary>
/// The CLI's copy of the infra naming convention must match <c>infra/main.bicep</c> /
/// <c>infra/modules/control-plane.bicep</c> and the table in <c>reference/infrastructure.md</c> character
/// for character — a drift here means the deploy pipeline addresses a resource that does not exist.
/// </summary>
public class FoundryGateAzureResourcesTests
{
    [Theory]
    [InlineData("dev", "dev")]
    [InlineData("prod", "prod")]
    [InlineData("test", "test")]
    [InlineData("DEV", "dev")]
    [InlineData(" dev ", "dev")]
    [InlineData("production", "prod")]
    [InlineData("Production", "prod")]
    [InlineData("development", "dev")]
    public void NormalizeEnvironment_lowercases_and_maps_GitHub_Environment_aliases(string input, string expected)
    {
        Assert.Equal(expected, FoundryGateAzureResources.NormalizeEnvironment(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeEnvironment_rejects_a_missing_value(string input)
    {
        Assert.ThrowsAny<ArgumentException>(() => FoundryGateAzureResources.NormalizeEnvironment(input));
    }

    [Theory]
    [InlineData("pre-prod", "pre-prod")]
    [InlineData("Pre-Prod", "pre-prod")]
    [InlineData("integration-eu", "integration-eu")]
    public void A_hyphenated_GitHub_Environment_name_normalizes_and_derives_names(string input, string expected)
    {
        // A fork whose GitHub Environment is 'pre-prod' passes that straight through --env; nothing about
        // it is invalid, and both `ip` commands are handed an explicit --server/--resource-group anyway.
        Assert.Equal(expected, FoundryGateAzureResources.NormalizeEnvironment(input));
        Assert.Equal($"rg-foundrygate-{expected}", FoundryGateAzureResources.ResourceGroupName(input));
        Assert.Equal($"sqldb-foundrygate-{expected}", FoundryGateAzureResources.SqlDatabaseName(input));
        Assert.Equal($"id-foundrygate-api-{expected}", FoundryGateAzureResources.ApiIdentityName(input));
    }

    [Theory]
    [InlineData("-dev")]
    [InlineData("dev-")]
    [InlineData("dev_2")]
    [InlineData("dev env")]
    [InlineData("averyveryverylongenvironmentname")]
    public void Name_derivation_rejects_values_that_cannot_be_part_of_a_resource_name(string input)
    {
        // The check lives on the name builders, not on NormalizeEnvironment: a value nothing derives a
        // name from is nobody's problem (the reviewer's point on #143).
        _ = FoundryGateAzureResources.NormalizeEnvironment(input);
        Assert.ThrowsAny<ArgumentException>(() => FoundryGateAzureResources.ResourceGroupName(input));
        Assert.ThrowsAny<ArgumentException>(() => FoundryGateAzureResources.SqlServerNamePrefix(input));
        Assert.ThrowsAny<ArgumentException>(() => FoundryGateAzureResources.ApiIdentityName(input));
    }

    [Fact]
    public void Names_follow_the_infra_convention_for_dev()
    {
        Assert.Equal("rg-foundrygate-dev", FoundryGateAzureResources.ResourceGroupName("dev"));
        Assert.Equal("sql-foundrygate-dev-", FoundryGateAzureResources.SqlServerNamePrefix("dev"));
        Assert.Equal("sqldb-foundrygate-dev", FoundryGateAzureResources.SqlDatabaseName("dev"));
        Assert.Equal("id-foundrygate-api-dev", FoundryGateAzureResources.ApiIdentityName("dev"));
        Assert.Equal("id-foundrygate-func-dev", FoundryGateAzureResources.FunctionsIdentityName("dev"));
    }

    [Fact]
    public void Names_use_the_short_Bicep_environment_for_the_production_GitHub_Environment()
    {
        Assert.Equal("rg-foundrygate-prod", FoundryGateAzureResources.ResourceGroupName("production"));
        Assert.Equal("sql-foundrygate-prod-", FoundryGateAzureResources.SqlServerNamePrefix("production"));
        Assert.Equal("sqldb-foundrygate-prod", FoundryGateAzureResources.SqlDatabaseName("production"));
        Assert.Equal("id-foundrygate-api-prod", FoundryGateAzureResources.ApiIdentityName("production"));
        Assert.Equal("id-foundrygate-func-prod", FoundryGateAzureResources.FunctionsIdentityName("production"));
    }

    [Fact]
    public void The_dev_server_name_from_PR_111_matches_the_prefix()
    {
        // sql-foundrygate-dev-e7k2 is the live dev server named in #96's infra-contract comment.
        Assert.StartsWith(FoundryGateAzureResources.SqlServerNamePrefix("dev"), "sql-foundrygate-dev-e7k2", StringComparison.Ordinal);
    }

    [Fact]
    public void EntraConnectionString_matches_the_sql_bicep_output_shape()
    {
        var connectionString = FoundryGateAzureResources.EntraConnectionString("sql-foundrygate-dev-e7k2.database.windows.net", "sqldb-foundrygate-dev");

        Assert.Equal(
            "Server=tcp:sql-foundrygate-dev-e7k2.database.windows.net,1433;Database=sqldb-foundrygate-dev;Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;",
            connectionString);
    }
}
