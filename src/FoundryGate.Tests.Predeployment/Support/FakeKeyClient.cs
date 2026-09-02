using System.Security.Cryptography;
using Azure;
using Azure.Security.KeyVault.Keys;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// A <see cref="KeyClient"/> whose <see cref="GetKeyAsync"/> answers "the current version of the key
/// is <see cref="CurrentVersionId"/>" without touching Azure — the one call the Key Vault protector
/// makes to follow a key rotation. Flip <see cref="CurrentVersionId"/> to simulate a rotation and
/// <see cref="CurrentEnabled"/> to simulate a disabled version. Uses the SDK's protected mocking
/// constructor and <see cref="KeyModelFactory"/>.
/// </summary>
public sealed class FakeKeyClient(Uri vaultUri, string keyName, RSA publicKeySource) : KeyClient
{
    /// <summary>The versioned key id the next <see cref="GetKeyAsync"/> reports.</summary>
    public required Uri CurrentVersionId { get; set; }

    public bool CurrentEnabled { get; set; } = true;

    public int Calls { get; private set; }

    /// <inheritdoc />
    public override Task<Response<KeyVaultKey>> GetKeyAsync(string name, string? version = null, CancellationToken cancellationToken = default)
    {
        Calls++;

        if (!string.Equals(name, keyName, StringComparison.Ordinal))
        {
            throw new RequestFailedException(404, $"Key '{name}' not found in fake vault.");
        }

        var id = CurrentVersionId;
        var properties = KeyModelFactory.KeyProperties(
            id,
            vaultUri,
            keyName,
            id.AbsolutePath.Trim('/').Split('/')[^1],
            managed: false,
            createdOn: null,
            updatedOn: null,
            recoveryLevel: null);
        properties.Enabled = CurrentEnabled;

        var key = KeyModelFactory.KeyVaultKey(properties, new JsonWebKey(publicKeySource, includePrivateParameters: false));
        return Task.FromResult<Response<KeyVaultKey>>(new ValueResponse<KeyVaultKey>(key));
    }

    private sealed class ValueResponse<T>(T value) : Response<T>
    {
        public override T Value => value;

        public override Response GetRawResponse() => throw new NotSupportedException("The fake carries no HTTP response.");
    }
}
