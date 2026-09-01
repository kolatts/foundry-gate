using FoundryGate.Api.Configuration;
using Imagile.Framework.Configuration.Exceptions;
using Imagile.Framework.Configuration.Extensions;

namespace FoundryGate.Tests.Predeployment.Api.Configuration;

/// <summary>
/// Fail-fast options validation (CONVENTIONS.md: "Options pattern, fail-fast ...
/// ValidateRecursively() at startup"). <see cref="AppSettings"/> is the same class
/// <c>Program.cs</c> binds and validates before the host starts — an invalid configuration
/// must throw here exactly as it would at real startup.
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

    [Fact]
    public void ValidateRecursively_absent_Gateway_section_is_valid_and_reports_Foundry_unconfigured()
    {
        // Local dev has no gateway to manage: the whole section may be missing without failing startup.
        var appSettings = ValidAppSettings();

        Assert.Null(Record.Exception(appSettings.ValidateRecursively));
        Assert.False(appSettings.Gateway.IsFoundryConfigured);
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
        appSettings.Gateway = new GatewayOptions
        {
            SubscriptionId = "00000000-0000-0000-0000-000000000001",
            ResourceGroup = "rg-foundrygate-test",
            FoundryAccountNames = ["fgtest-eus2", "fgtest-swc"],
        };

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

    private static AppSettings ValidAppSettings() =>
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
        };
}
