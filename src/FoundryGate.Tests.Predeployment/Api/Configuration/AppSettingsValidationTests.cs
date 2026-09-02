using FoundryGate.Api.Configuration;
using FoundryGate.Core.Configuration;
using FoundryGate.Tests.Predeployment.Support;
using Imagile.Framework.Configuration.Exceptions;
using Imagile.Framework.Configuration.Extensions;

namespace FoundryGate.Tests.Predeployment.Api.Configuration;

/// <summary>
/// Fail-fast options validation (CONVENTIONS.md: "Options pattern, fail-fast ...
/// ValidateRecursively() at startup"). <see cref="AppSettings"/> is the same class
/// <c>Program.cs</c> binds and validates before the host starts — an invalid configuration
/// must throw here exactly as it would at real startup. Covers the merged
/// <see cref="GatewayOptions"/> from both sides: the Foundry/APIM addressing rules (#131/#133) and
/// the quota tier rules (#127); the tier rules in depth are in <see cref="GatewayOptionsTiersTests"/>.
/// </summary>
public class AppSettingsValidationTests
{
    [Fact]
    public void ValidateRecursively_default_instance_throws_ConfigurationValidationException()
    {
        var appSettings = new AppSettings();

        var exception = Assert.Throws<ConfigurationValidationException>(appSettings.ValidateRecursively);

        Assert.Contains("AzureAd.TenantId", exception.Message);
        Assert.Contains("ConnectionStrings.FoundryGate", exception.Message);
        Assert.Contains("Gateway.Tiers", exception.Message); // tiers are always required
    }

    [Fact]
    public void ValidateRecursively_missing_AzureAd_TenantId_throws_with_nested_member_name()
    {
        var appSettings = ValidAppSettings();
        appSettings.AzureAd.TenantId = string.Empty;

        var exception = Assert.Throws<ConfigurationValidationException>(appSettings.ValidateRecursively);

        Assert.Contains("AzureAd.TenantId", exception.Message);
    }

    [Fact]
    public void ValidateRecursively_fully_populated_settings_does_not_throw()
    {
        var appSettings = ValidAppSettings();

        var exception = Record.Exception(appSettings.ValidateRecursively);

        Assert.Null(exception);
    }

    // -- Gateway addressing (Foundry #131 / APIM #133) --

    [Fact]
    public void ValidateRecursively_absent_Gateway_addressing_is_valid_and_reports_Foundry_unconfigured()
    {
        // Local dev has no gateway to manage: the addressing members may all be missing without
        // failing startup. (Tiers, by contrast, always ship in appsettings.json — see below.)
        var appSettings = ValidAppSettings();

        Assert.Null(Record.Exception(appSettings.ValidateRecursively));
        Assert.False(appSettings.Gateway.IsFoundryConfigured);
        Assert.False(appSettings.Gateway.IsApimConfigured);
    }

    [Fact]
    public void ValidateRecursively_Gateway_account_names_without_subscription_or_resource_group_throws()
    {
        // A half-set section is a deployment mistake: account names alone can't be resolved to ARM ids.
        var appSettings = ValidAppSettings();
        appSettings.Gateway.FoundryAccountNames = ["fgtest-eus2"];

        var exception = Assert.Throws<ConfigurationValidationException>(appSettings.ValidateRecursively);

        Assert.Contains("Gateway.SubscriptionId", exception.Message);
        Assert.Contains("Gateway.ResourceGroup", exception.Message);
    }

    [Fact]
    public void ValidateRecursively_fully_configured_Gateway_section_is_valid_and_reports_Foundry_configured()
    {
        var appSettings = ValidAppSettings();
        appSettings.Gateway.SubscriptionId = "00000000-0000-0000-0000-000000000001";
        appSettings.Gateway.ResourceGroup = "rg-foundrygate-test";
        appSettings.Gateway.FoundryAccountNames = ["fgtest-eus2", "fgtest-swc"];

        Assert.Null(Record.Exception(appSettings.ValidateRecursively));
        Assert.True(appSettings.Gateway.IsFoundryConfigured);
    }

    [Fact]
    public void ValidateRecursively_ApimName_without_subscription_or_resource_group_throws()
    {
        var appSettings = ValidAppSettings();
        appSettings.Gateway.ApimName = "apim-foundrygate-dev";

        var exception = Assert.Throws<ConfigurationValidationException>(appSettings.ValidateRecursively);

        Assert.Contains("Gateway.SubscriptionId", exception.Message);
        Assert.Contains("Gateway.ResourceGroup", exception.Message);
        Assert.Contains("ApimName", exception.Message);
    }

    [Fact]
    public void ValidateRecursively_shared_scope_without_any_feature_member_is_valid_neither_feature_is_configured()
    {
        // #131's shape: subscription + resource group set for Foundry, no ApimName — must not be
        // rejected as a "partial APIM" configuration (the pair is shared scope, owned by no feature).
        var appSettings = ValidAppSettings();
        appSettings.Gateway.SubscriptionId = "00000000-0000-0000-0000-000000000000";
        appSettings.Gateway.ResourceGroup = "rg-foundrygate-dev";

        Assert.Null(Record.Exception(appSettings.ValidateRecursively));
        Assert.False(appSettings.Gateway.IsApimConfigured);
        Assert.False(appSettings.Gateway.IsFoundryConfigured);
    }

    [Fact]
    public void ValidateRecursively_fully_addressed_APIM_is_valid_and_reports_Apim_configured()
    {
        var appSettings = ValidAppSettings();
        appSettings.Gateway.SubscriptionId = "00000000-0000-0000-0000-000000000000";
        appSettings.Gateway.ResourceGroup = "rg-foundrygate-dev";
        appSettings.Gateway.ApimName = "apim-foundrygate-dev";

        Assert.Null(Record.Exception(appSettings.ValidateRecursively));
        Assert.True(appSettings.Gateway.IsApimConfigured);
        Assert.False(appSettings.Gateway.IsFoundryConfigured);
    }

    [Theory]
    [InlineData("http://kv.vault.azure.net/keys/fg-apim-key-encryption")]
    [InlineData("kv.vault.azure.net/keys/fg-apim-key-encryption")]
    public void ValidateRecursively_non_https_key_encryption_uri_throws(string uri)
    {
        var appSettings = ValidAppSettings();
        appSettings.Gateway.KeyEncryptionKeyUri = uri;

        var exception = Assert.Throws<ConfigurationValidationException>(appSettings.ValidateRecursively);

        Assert.Contains("KeyEncryptionKeyUri", exception.Message);
    }

    [Fact]
    public void KeyProtection_defaults_to_KeyVault()
    {
        Assert.Equal(KeyProtectionProviderType.KeyVault, ValidAppSettings().KeyProtection.Provider);
    }

    // -- Gateway quota tiers (#127) --

    [Fact]
    public void ValidateRecursively_missing_Gateway_Tiers_throws_even_with_addressing_absent()
    {
        // Unlike the addressing members, tiers are not a feature toggle: quota resolution cannot run
        // without them, so an empty list is a startup failure with the fix in the message.
        var appSettings = ValidAppSettings();
        appSettings.Gateway.Tiers.Clear();

        var exception = Assert.Throws<ConfigurationValidationException>(appSettings.ValidateRecursively);

        Assert.Contains("Gateway.Tiers", exception.Message);

        // Both places an operator could have to look, named in the message: the environment variables
        // infra sets on a deployed host (#201) and the local file. "Set Gateway:Tiers" on its own sends
        // whoever reads it hunting through an appsettings.json that no longer carries the table.
        Assert.Contains("Gateway__Tiers__0__ProductId", exception.Message, StringComparison.Ordinal);
        Assert.Contains("appsettings.local.json", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateRecursively_negative_tier_cap_throws_the_startup_path_reaches_list_items()
    {
        var appSettings = ValidAppSettings();
        appSettings.Gateway.Tiers[0].MonthlyTokenQuota = -5_000_000;

        var exception = Assert.Throws<ConfigurationValidationException>(appSettings.ValidateRecursively);

        Assert.Contains("Tiers[0].MonthlyTokenQuota", exception.Message);
    }

    [Fact]
    public void ValidateRecursively_addressing_and_tier_errors_are_reported_together()
    {
        var appSettings = ValidAppSettings();
        appSettings.Gateway.ApimName = "apim-foundrygate-dev"; // partial addressing
        appSettings.Gateway.Tiers[1].ProductId = "platinum"; // unknown tier

        var exception = Assert.Throws<ConfigurationValidationException>(appSettings.ValidateRecursively);

        Assert.Contains("Gateway.SubscriptionId", exception.Message);
        Assert.Contains("platinum", exception.Message);
    }

    /// <summary>
    /// A minimal valid settings object — addressing absent (local shape), tiers present (they always
    /// are; <c>appsettings.json</c> ships them). Shared with <see cref="GatewayOptionsTiersTests"/>,
    /// which breaks one tier rule at a time.
    /// </summary>
    internal static AppSettings ValidAppSettings() =>
        new()
        {
            AzureAd = new AzureAdOptions
            {
                TenantId = "00000000-0000-0000-0000-000000000000",
                ClientId = "00000000-0000-0000-0000-000000000000",
                Audience = "api://00000000-0000-0000-0000-000000000000",
            },
            ConnectionStrings = new ConnectionStringOptions
            {
                FoundryGate = "Server=localhost;Database=FoundryGate;",
            },
            Gateway = TestGatewayTiers.Options(),
        };
}
