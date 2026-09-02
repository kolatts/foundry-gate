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
/// <c>Program.cs</c> makes, plus the parity check that keeps its shipped quota tiers identical to the
/// Api's — two hosts resolving quota against different tier tables would be a silent divergence in the
/// one number the whole product is about.
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
    public void The_shipped_Functions_tier_table_is_identical_to_the_Apis()
    {
        var repoRoot = FindRepoRoot();

        var functionsTiers = TiersFrom(Path.Combine(repoRoot, "src", "FoundryGate.Functions", "appsettings.json"));
        var apiTiers = TiersFrom(Path.Combine(repoRoot, "src", "FoundryGate.Api", "appsettings.json"));

        Assert.NotEmpty(apiTiers);
        Assert.Equal(apiTiers, functionsTiers);
    }

    private static List<(string ProductId, string DisplayName, long MonthlyTokenQuota)> TiersFrom(string appSettingsPath)
    {
        var options = new ConfigurationBuilder()
            .AddJsonFile(appSettingsPath, optional: false)
            .Build()
            .GetSection("Gateway")
            .Get<GatewayOptions>();

        Assert.NotNull(options);
        return [.. options.Tiers.Select(t => (t.ProductId, t.DisplayName, t.MonthlyTokenQuota))];
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
