using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Security;
using FoundryGate.Domain.Common;
using FoundryGate.Tests.Predeployment.Support;
using Imagile.Framework.Configuration.Exceptions;
using Microsoft.AspNetCore.DataProtection;

namespace FoundryGate.Tests.Predeployment.Api.Services.Security;

/// <summary>The fail-fast selection rules the host applies at startup (through <c>AddResolveOnStartup</c>).</summary>
public class KeyProtectorFactoryTests
{
    private const string KeyUri = "https://kv-fg-dev-abc12.vault.azure.net/keys/fg-apim-key-encryption";

    [Fact]
    public void KeyVault_provider_without_a_key_uri_is_a_startup_configuration_error()
    {
        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Create(KeyProtectionProviderType.KeyVault, keyUri: null, AppEnvironment.Types.prod));

        Assert.Contains("Gateway:KeyEncryptionKeyUri", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AppEnvironment.Types.local)]
    [InlineData(AppEnvironment.Types.qa)]
    [InlineData(AppEnvironment.Types.prod)]
    public void KeyVault_provider_with_a_key_uri_is_allowed_everywhere(AppEnvironment.Types environment) =>
        Assert.IsType<KeyVaultKeyProtector>(Create(KeyProtectionProviderType.KeyVault, KeyUri, environment));

    [Fact]
    public void DataProtection_provider_is_allowed_locally() =>
        Assert.IsType<DataProtectionKeyProtector>(Create(KeyProtectionProviderType.DataProtection, keyUri: null, AppEnvironment.Types.local));

    [Theory]
    [InlineData(AppEnvironment.Types.qa)]
    [InlineData(AppEnvironment.Types.prod)]
    public void DataProtection_provider_is_refused_outside_local(AppEnvironment.Types environment)
    {
        var exception = Assert.Throws<ConfigurationValidationException>(() =>
            Create(KeyProtectionProviderType.DataProtection, KeyUri, environment));

        Assert.Contains(environment.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains("KeyVault", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Default_options_select_KeyVault_so_a_cloud_host_that_forgets_the_section_fails_closed() =>
        Assert.Equal(KeyProtectionProviderType.KeyVault, new KeyProtectionOptions().Provider);

    private static IKeyProtector Create(KeyProtectionProviderType provider, string? keyUri, AppEnvironment.Types environment) =>
        KeyProtectorFactory.Create(
            new KeyProtectionOptions { Provider = provider },
            new GatewayOptions { KeyEncryptionKeyUri = keyUri },
            environment,
            new StaticTokenCredential(),
            new EphemeralDataProtectionProvider());
}
