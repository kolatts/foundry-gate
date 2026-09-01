using System.Collections.Concurrent;
using System.Text;
using Azure.Security.KeyVault.Keys;
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
/// Stored as <c>kv1:{versionedKeyId}:{base64}</c> (<see cref="KeyEnvelope"/>). <b>Wrapping resolves
/// the key's current version explicitly</b> — <see cref="KeyClient.GetKeyAsync"/> on the key name,
/// cached for <see cref="CurrentVersionTtl"/> — and wraps with a <see cref="CryptographyClient"/>
/// bound to that <em>versioned</em> id. This matters because a <see cref="CryptographyClient"/> given a
/// versionless URI fetches the key once and caches it for its lifetime, so a long-lived singleton
/// would keep wrapping under a version that Key Vault has since rotated away from (and that an
/// operator may then disable). With per-version clients, new writes follow a Key Vault rotation
/// within the TTL; unwrapping always targets the version recorded in the envelope, so every
/// existing row keeps opening (old versions stay enabled) and no re-encryption sweep is needed.
/// Rows still on an old version are findable with <c>LIKE 'kv1:%/{oldVersion}:%'</c>.
/// </para>
/// <para>
/// Unwrap only honours a key id on the <em>configured</em> vault host and key name. A database row is
/// not a trusted pointer: a tampered envelope must not be able to steer the API identity at another
/// vault or key it happens to have access to.
/// </para>
/// <para>
/// One <see cref="CryptographyClient"/> per key version, cached: versions are immutable, the clients
/// are thread-safe, and this class is a singleton. The clients are injected as factories so tests can
/// substitute ones that never touch Azure.
/// </para>
/// </remarks>
public sealed class KeyVaultKeyProtector : IKeyProtector
{
    /// <summary>How long a resolved "current version" is trusted before <see cref="KeyClient"/> is asked again — the maximum lag between a Key Vault rotation and new writes using the new version.</summary>
    public static readonly TimeSpan CurrentVersionTtl = TimeSpan.FromMinutes(5);

    private readonly Uri _keyEncryptionKeyUri;
    private readonly string _keyName;
    private readonly KeyClient _keyClient;
    private readonly Func<Uri, CryptographyClient> _clientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, CryptographyClient> _clients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _resolveLock = new(1, 1);
    private (Uri KeyId, DateTimeOffset ExpiresAt)? _currentVersion;

    /// <param name="keyEncryptionKeyUri">The (normally versionless) Key Vault key URI, <c>https://{vault}/keys/{name}</c>.</param>
    /// <param name="keyClient">Client for the vault that owns the key; used only to resolve the current version.</param>
    /// <param name="clientFactory">Creates a <see cref="CryptographyClient"/> for a <em>versioned</em> key id.</param>
    /// <param name="timeProvider">Clock for the version cache.</param>
    public KeyVaultKeyProtector(Uri keyEncryptionKeyUri, KeyClient keyClient, Func<Uri, CryptographyClient> clientFactory, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(keyEncryptionKeyUri);
        ArgumentNullException.ThrowIfNull(keyClient);
        ArgumentNullException.ThrowIfNull(clientFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _keyEncryptionKeyUri = keyEncryptionKeyUri;
        _keyName = KeyNameOf(keyEncryptionKeyUri)
            ?? throw new ArgumentException($"'{keyEncryptionKeyUri}' is not a Key Vault key URI (expected https://{{vault}}/keys/{{name}}).", nameof(keyEncryptionKeyUri));
        _keyClient = keyClient;
        _clientFactory = clientFactory;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async Task<string> ProtectAsync(string plaintext, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);

        var keyId = await GetCurrentVersionAsync(cancellationToken);
        var result = await GetClient(keyId).WrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, Encoding.UTF8.GetBytes(plaintext), cancellationToken);

        return new KeyEnvelope(KeyEnvelope.KeyVaultScheme, result.KeyId, Convert.ToBase64String(result.EncryptedKey)).ToString();
    }

    /// <inheritdoc />
    public async Task<string> UnprotectAsync(string ciphertext, CancellationToken cancellationToken)
    {
        var envelope = KeyEnvelope.ParseFor(ciphertext, KeyEnvelope.KeyVaultScheme);
        var keyId = ValidateEnvelopeKeyId(envelope.KeyId);

        var result = await GetClient(keyId).UnwrapKeyAsync(KeyWrapAlgorithm.RsaOaep256, Convert.FromBase64String(envelope.Payload), cancellationToken);

        return Encoding.UTF8.GetString(result.Key);
    }

    /// <summary>The envelope's key id must be a versioned id of the configured key on the configured vault.</summary>
    private Uri ValidateEnvelopeKeyId(string? keyIdText)
    {
        if (!Uri.TryCreate(keyIdText, UriKind.Absolute, out var keyId))
        {
            throw new InvalidOperationException("The stored APIM key envelope carries a key id that is not an absolute URI; it cannot be decrypted.");
        }

        var sameVault = string.Equals(keyId.Host, _keyEncryptionKeyUri.Host, StringComparison.OrdinalIgnoreCase)
            && keyId.Scheme == Uri.UriSchemeHttps;
        var sameKey = string.Equals(KeyNameOf(keyId), _keyName, StringComparison.OrdinalIgnoreCase);

        if (!sameVault || !sameKey || VersionOf(keyId) is null)
        {
            throw new InvalidOperationException(
                $"The stored APIM key envelope points at '{keyId}', which is not a version of the configured key '{_keyEncryptionKeyUri}'. " +
                "Refusing to decrypt with a key the configuration does not name.");
        }

        return keyId;
    }

    private async Task<Uri> GetCurrentVersionAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (_currentVersion is { } cached && cached.ExpiresAt > now)
        {
            return cached.KeyId;
        }

        await _resolveLock.WaitAsync(cancellationToken);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_currentVersion is { } refreshed && refreshed.ExpiresAt > now)
            {
                return refreshed.KeyId;
            }

            var key = (await _keyClient.GetKeyAsync(_keyName, cancellationToken: cancellationToken)).Value;
            if (key.Properties.Enabled == false)
            {
                throw new InvalidOperationException(
                    $"The current version of Key Vault key '{_keyName}' ({key.Id}) is disabled; APIM keys cannot be encrypted until it is enabled or a new version is created.");
            }

            _currentVersion = (key.Id, now + CurrentVersionTtl);
            return key.Id;
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    private CryptographyClient GetClient(Uri keyId) =>
        _clients.GetOrAdd(keyId.AbsoluteUri, static (_, state) => state.Factory(state.KeyId), (Factory: _clientFactory, KeyId: keyId));

    /// <summary><c>/keys/{name}[/{version}]</c> → <c>name</c>; <see langword="null"/> when the path is not a key path.</summary>
    internal static string? KeyNameOf(Uri keyUri)
    {
        var segments = keyUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length is 2 or 3 && string.Equals(segments[0], "keys", StringComparison.OrdinalIgnoreCase)
            ? segments[1]
            : null;
    }

    /// <summary><c>/keys/{name}/{version}</c> → <c>version</c>; <see langword="null"/> when versionless.</summary>
    internal static string? VersionOf(Uri keyUri)
    {
        var segments = keyUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 3 ? segments[2] : null;
    }
}
