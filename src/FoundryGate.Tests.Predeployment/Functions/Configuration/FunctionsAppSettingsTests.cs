using FoundryGate.Core.Configuration;
using FoundryGate.Tests.Predeployment.Support;
using Imagile.Framework.Configuration.Exceptions;
using Imagile.Framework.Configuration.Extensions;
using Microsoft.Extensions.Configuration;
using FunctionsAppSettings = FoundryGate.Functions.Configuration.AppSettings;
using FunctionsStorageOptions = FoundryGate.Functions.Configuration.StorageOptions;

namespace FoundryGate.Tests.Predeployment.Functions.Configuration;

/// <summary>
/// The Functions host's fail-fast configuration (#38/#39): the same <c>ValidateRecursively()</c> call
/// <c>Program.cs</c> makes, plus the parity check that keeps its <c>local</c> quota tiers identical to
/// the Api's — two hosts resolving quota against different tier tables would be a silent divergence in
/// the one number the whole product is about. (Deployed, both read the table infra emits, #201.)
/// </summary>
public class FunctionsAppSettingsTests
{
    [Fact]
    public void A_default_instance_fails_startup_on_the_connection_string_and_the_tier_table()
    {
        var settings = new FunctionsAppSettings();

        var exception = Assert.Throws<ConfigurationValidationException>(settings.ValidateRecursively);

        Assert.Contains("ConnectionStrings.FoundryGate", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Gateway.Tiers", exception.Message, StringComparison.Ordinal); // reached via CoreOptionsValidation
    }

    [Fact]
    public void A_deployed_shape_validates()
    {
        Assert.Null(Record.Exception(Valid().ValidateRecursively));
    }

    [Fact]
    public void An_ARM_workspace_id_where_the_query_API_wants_a_GUID_fails_startup()
    {
        var settings = Valid();
        settings.Gateway.LogAnalyticsWorkspaceId =
            "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/law";

        var exception = Assert.Throws<ConfigurationValidationException>(settings.ValidateRecursively);

        Assert.Contains("Gateway.LogAnalyticsWorkspaceId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_workspace_GUID_is_accepted()
    {
        var settings = Valid();
        settings.Gateway.LogAnalyticsWorkspaceId = Guid.NewGuid().ToString();

        Assert.Null(Record.Exception(settings.ValidateRecursively));
        Assert.True(settings.Gateway.IsUsageReconciliationConfigured);
    }

    [Fact]
    public void A_cloud_storage_connection_string_is_refused_because_the_account_takes_no_shared_key()
    {
        var settings = Valid();
        settings.Storage.ConnectionString = "DefaultEndpointsProtocol=https;AccountName=stfg;AccountKey=abc==";

        var exception = Assert.Throws<ConfigurationValidationException>(settings.ValidateRecursively);

        Assert.Contains("Storage.ConnectionString", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Azurite_is_the_one_connection_string_that_is_allowed()
    {
        var settings = Valid();
        settings.Storage.ConnectionString = "UseDevelopmentStorage=true";

        Assert.Null(Record.Exception(settings.ValidateRecursively));
    }

    [Fact]
    public void Telemetry_turned_on_without_a_connection_string_fails_rather_than_dropping_it_silently()
    {
        var settings = Valid();
        settings.OpenTelemetry.Enabled = true;

        var exception = Assert.Throws<ConfigurationValidationException>(settings.ValidateRecursively);

        Assert.Contains("OpenTelemetry.ConnectionString", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_local_Functions_tier_table_is_identical_to_the_Apis()
    {
        // Both hosts get the deployed table from the same infra variable since #201, so the only copies
        // left to disagree are the two appsettings.local.json files — and they must not, because a
        // developer running the Api and `func start` side by side against one docker database would
        // otherwise have the reset resolve budgets the Api refuses to accept.
        var repoRoot = FindRepoRoot();

        var functionsTiers = TiersFrom(Path.Combine(repoRoot, "src", "FoundryGate.Functions", "appsettings.local.json"));
        var apiTiers = TiersFrom(Path.Combine(repoRoot, "src", "FoundryGate.Api", "appsettings.local.json"));

        Assert.NotEmpty(apiTiers);
        Assert.Equal(apiTiers, functionsTiers);
    }

    [Fact]
    public void Neither_host_ships_a_tier_table_in_appsettings_json()
    {
        // The premise of #201's second half. Infra sets Gateway__Tiers__{i}__* on both hosts; a shipped
        // copy would be a second source for one table, and on a fork whose quotaTiers has fewer entries
        // than the shipped three, the leftover shipped index would survive as a phantom tier nothing
        // created a product for.
        var repoRoot = FindRepoRoot();

        Assert.Empty(TiersFrom(Path.Combine(repoRoot, "src", "FoundryGate.Api", "appsettings.json")));
        Assert.Empty(TiersFrom(Path.Combine(repoRoot, "src", "FoundryGate.Functions", "appsettings.json")));
    }

    /// <summary>
    /// The tier table one appsettings file carries, or an empty list when it carries no
    /// <c>Gateway</c> section at all — which is what "ships no tier table" looks like since #201, and
    /// is why this does not assert the bound options are non-null.
    /// </summary>
    private static List<(string ProductId, string DisplayName, long MonthlyTokenQuota)> TiersFrom(string appSettingsPath)
    {
        var options = new ConfigurationBuilder()
            .AddJsonFile(appSettingsPath, optional: false)
            .Build()
            .GetSection("Gateway")
            .Get<GatewayOptions>();

        return [.. (options?.Tiers ?? []).Select(t => (t.ProductId, t.DisplayName, t.MonthlyTokenQuota))];
    }

    private static FunctionsAppSettings Valid() => new()
    {
        ConnectionStrings = new FoundryGate.Functions.Configuration.ConnectionStringOptions
        {
            FoundryGate = "Server=127.0.0.1,3433;Database=FoundryGate;User Id=sa;Password=x;TrustServerCertificate=True",
        },
        Gateway = TestGatewayTiers.Options(),
        Storage = new FunctionsStorageOptions(),
    };

    /// <summary>Walks up from the test binaries to the repo root (the directory holding FoundryGate.sln).</summary>
    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FoundryGate.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
