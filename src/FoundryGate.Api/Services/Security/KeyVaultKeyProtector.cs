using System.Collections.Concurrent;
using System.Text;
using Azure.Security.KeyVault.Keys.Cryptography;

namespace FoundryGate.Api.Services.Security;

/// <summary>
/// <see cref="IKeyProtector"/> backed by Azure Key Vault key wrapping (spec &#167;11, #95): the APIM
/// key bytes are wrapped directly with RSA-OAEP-256 under the RSA key
/// <c>GatewayOptions.KeyEncryptionKeyUri</c> (RSA-3072 wraps up to 318 bytes; APIM keys are 32
/// characters, so no data-encryption-key indirection is needed). The private key never leaves the
/// vault — wrap/unwrap run inside Key Vault, authorized by the API identity's Key Vault Crypto User
/// role (<c>infra/modules/control-plane-rbac.bicep</c>).
/// </summary>
/// <remarks>
/// <para>
/// Stored as <c>kv1:{versionedKeyId}:{base64}</c> (<see cref="KeyEnvelope"/>). Wrapping targets the
/// configured, <em>versionless</em> URI so Key Vault picks the current version; Key Vault reports the
/// version it actually used and that is what gets recorded. Unwrapping targets the recorded version,
/// so after a Key Vault key rotation every existing row still opens (old versions stay enabled) while
/// new writes move to the new version — no re-encryption sweep is needed to rotate, and an operator
/// can find rows still on an old version with a <c>LIKE 'kv1:%/{oldVersion}:%'</c> query.
/// </para>
/// <para>
/// One <see cref="CryptographyClient"/> per key version, cached: the clients are thread-safe and this
/// class is a singleton. The factory is injected so tests can substitute a client that never
/// touches Azure.
/// </para>
/// </remarks>
public sealed class KeyVaultKeyProtector(Uri keyEncryptionKeyUri, Func<Uri, CryptographyClient> clientFactory) : IKeyProtector
{
    private readonly ConcurrentDictionary<string, CryptographyClient> _clients = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<string> ProtectAsync(string plaintext, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        var client = GetClient(keyEncryptionKeyUri);
        var result = await client.WrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, Encoding.UTF8.GetBytes(plaintext), cancellationToken);

        return new KeyEnvelope(KeyEnvelope.KeyVaultScheme, result.KeyId, Convert.ToBase64String(result.EncryptedKey)).ToString();
    }

    /// <inheritdoc />
    public async Task<string> UnprotectAsync(string ciphertext, CancellationToken cancellationToken)
    {
        var envelope = KeyEnvelope.ParseFor(ciphertext, KeyEnvelope.KeyVaultScheme);

        if (!Uri.TryCreate(envelope.KeyId, UriKind.Absolute, out var keyId))
        {
            throw new InvalidOperationException("The stored APIM key envelope carries a key id that is not an absolute URI; it cannot be decrypted.");
        }

        var client = GetClient(keyId);
        var result = await client.UnwrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, Convert.FromBase64String(envelope.Payload), cancellationToken);

        return Encoding.UTF8.GetString(result.Key);
    }

    private CryptographyClient GetClient(Uri keyId) =>
        _clients.GetOrAdd(keyId.AbsoluteUri, static (_, state) => state.Factory(state.KeyId), (Factory: clientFactory, KeyId: keyId));
}
