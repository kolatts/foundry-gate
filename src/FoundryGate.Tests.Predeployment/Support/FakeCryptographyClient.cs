using System.Security.Cryptography;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// A <see cref="CryptographyClient"/> that performs real RSA-OAEP-256 wrap/unwrap with a local
/// <see cref="RSA"/> key instead of calling Key Vault, reporting <paramref name="versionedKeyId"/> as
/// the key that did the work — exactly what Key Vault reports when asked to wrap under a versionless
/// key URI. Real RSA (not a byte-reversing stub) so ciphertext sizes, and therefore the
/// <c>User.ApimSubscriptionKey</c> length budget, are measured for real. Uses the SDK's protected
/// mocking constructor and <see cref="CryptographyModelFactory"/>.
/// </summary>
public sealed class FakeCryptographyClient(string versionedKeyId, RSA rsa) : CryptographyClient
{
    /// <summary>The versioned key id this fake reports on every result.</summary>
    public string VersionedKeyId { get; } = versionedKeyId;

    public int WrapCalls { get; private set; }

    public int UnwrapCalls { get; private set; }

    /// <inheritdoc />
    public override Task<WrapResult> WrapKeyAsync(KeyWrapAlgorithm algorithm, byte[] key, CancellationToken cancellationToken = default)
    {
        RequireRsaOaep256(algorithm);
        WrapCalls++;

        return Task.FromResult(CryptographyModelFactory.WrapResult(VersionedKeyId, rsa.Encrypt(key, RSAEncryptionPadding.OaepSHA256), algorithm));
    }

    /// <inheritdoc />
    public override Task<UnwrapResult> UnwrapKeyAsync(KeyWrapAlgorithm algorithm, byte[] encryptedKey, CancellationToken cancellationToken = default)
    {
        RequireRsaOaep256(algorithm);
        UnwrapCalls++;

        return Task.FromResult(CryptographyModelFactory.UnwrapResult(VersionedKeyId, rsa.Decrypt(encryptedKey, RSAEncryptionPadding.OaepSHA256), algorithm));
    }

    private static void RequireRsaOaep256(KeyWrapAlgorithm algorithm)
    {
        if (algorithm != KeyWrapAlgorithm.RsaOaep256)
        {
            throw new ArgumentException($"Expected RSA-OAEP-256 but the protector asked for {algorithm}.", nameof(algorithm));
        }
    }
}
