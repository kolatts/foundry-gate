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
    public void ValidateRecursively_partially_addressed_APIM_throws_naming_the_three_Gateway_members()
    {
        var appSettings = ValidAppSettings();
        appSettings.Gateway.ApimName = "apim-foundrygate-dev";

        var exception = Assert.Throws<ConfigurationValidationException>(appSettings.ValidateRecursively);

        Assert.Contains("SubscriptionId", exception.Message);
        Assert.Contains("ResourceGroup", exception.Message);
        Assert.Contains("ApimName", exception.Message);
    }

    [Fact]
    public void ValidateRecursively_fully_addressed_APIM_does_not_throw()
    {
        var appSettings = ValidAppSettings();
        appSettings.Gateway.SubscriptionId = "00000000-0000-0000-0000-000000000000";
        appSettings.Gateway.ResourceGroup = "rg-foundrygate-dev";
        appSettings.Gateway.ApimName = "apim-foundrygate-dev";

        Assert.Null(Record.Exception(appSettings.ValidateRecursively));
        Assert.True(appSettings.Gateway.IsApimConfigured);
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
    public void ValidateRecursively_empty_Gateway_section_is_valid_local_dev_has_no_APIM()
    {
        var appSettings = ValidAppSettings();

        Assert.Null(Record.Exception(appSettings.ValidateRecursively));
        Assert.False(appSettings.Gateway.IsApimConfigured);
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
