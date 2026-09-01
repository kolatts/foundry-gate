using Azure.Core;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using FoundryGate.Api.Configuration;
using FoundryGate.Domain.Common;
using Imagile.Framework.Configuration.Exceptions;
using Microsoft.AspNetCore.DataProtection;

namespace FoundryGate.Api.Services.Security;

/// <summary>
/// Chooses and fail-fast-validates the <see cref="IKeyProtector"/> for the current configuration and
/// environment. Invoked from DI at startup (<see cref="SecurityServiceCollectionExtensions"/> resolves
/// the singleton eagerly) so a misconfiguration is a refused start, never a 500 on the first key
/// operation. Kept as a pure static so the rules are unit-testable without a host.
/// </summary>
public static class KeyProtectorFactory
{
    /// <summary>
    /// Rules: <see cref="KeyProtectionProviderType.KeyVault"/> needs
    /// <see cref="GatewayOptions.KeyEncryptionKeyUri"/> (an https Key Vault key URI);
    /// <see cref="KeyProtectionProviderType.DataProtection"/> is permitted in
    /// <see cref="AppEnvironment.Types.local"/> only.
    /// </summary>
    /// <exception cref="ConfigurationValidationException">A rule above is violated.</exception>
    public static IKeyProtector Create(
        KeyProtectionOptions keyProtection,
        GatewayOptions gateway,
        AppEnvironment.Types environment,
        TokenCredential credential,
        IDataProtectionProvider dataProtectionProvider,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(keyProtection);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        ArgumentNullException.ThrowIfNull(timeProvider);

        switch (keyProtection.Provider)
        {
            case KeyProtectionProviderType.KeyVault:
                if (!Uri.TryCreate(gateway.KeyEncryptionKeyUri, UriKind.Absolute, out var keyUri)
                    || keyUri.Scheme != Uri.UriSchemeHttps
                    || KeyVaultKeyProtector.KeyNameOf(keyUri) is null)
                {
                    throw new ConfigurationValidationException(
                        "KeyProtection:Provider is 'KeyVault' but Gateway:KeyEncryptionKeyUri is not a Key Vault key URI. " +
                        "Set it to the versionless URI (https://{vault}/keys/{name}) of the Key Vault key that wraps APIM subscription keys " +
                        "(infra output 'keyEncryptionKeyUri' / env var Gateway__KeyEncryptionKeyUri), or use " +
                        "KeyProtection:Provider = 'DataProtection' in the local environment only.");
                }

                var vaultUri = new Uri(keyUri.GetLeftPart(UriPartial.Authority));
                return new KeyVaultKeyProtector(
                    keyUri,
                    new KeyClient(vaultUri, credential),
                    keyId => new CryptographyClient(keyId, credential),
                    timeProvider);

            case KeyProtectionProviderType.DataProtection:
                if (environment != AppEnvironment.Types.local)
                {
                    throw new ConfigurationValidationException(
                        $"KeyProtection:Provider 'DataProtection' is only permitted in the local environment, not '{environment}'. " +
                        "Cloud environments must use 'KeyVault' so APIM keys are wrapped by a Key Vault key (spec §11, #95).");
                }

                return new DataProtectionKeyProtector(dataProtectionProvider);

            default:
                throw new ConfigurationValidationException($"KeyProtection:Provider '{keyProtection.Provider}' is not a supported provider.");
        }
    }
}
