using System.Security.Cryptography;
using Azure.Security.KeyVault.Keys.Cryptography;
using FoundryGate.Api.Services.Security;
using FoundryGate.Tests.Predeployment.Support;

namespace FoundryGate.Tests.Predeployment.Api.Services.Security;

/// <summary>
/// The Key Vault protector against a real-RSA <see cref="FakeCryptographyClient"/> and a
/// <see cref="FakeKeyClient"/>: envelope shape, explicit current-version resolution (so a long-lived
/// singleton follows a Key Vault key rotation), version pinning on unwrap, vault/key pinning against
/// tampered rows, client caching, and the column length budget.
/// </summary>
public sealed class KeyVaultKeyProtectorTests : IDisposable
{
    private const string ApimKey = "3f9a1c2e4b6d8f0a1b2c3d4e5f607182";
    private const string VaultUri = "https://kv-fg-dev-abc12.vault.azure.net";
    private const string VersionlessKeyUri = VaultUri + "/keys/fg-apim-key-encryption";
    private const string Version1 = VersionlessKeyUri + "/0123456789abcdef0123456789abcdef";
    private const string Version2 = VersionlessKeyUri + "/fedcba9876543210fedcba9876543210";
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly RSA _rsaV1 = RSA.Create(3072);
    private readonly RSA _rsaV2 = RSA.Create(3072);
    private readonly Dictionary<string, FakeCryptographyClient> _clients = new(StringComparer.Ordinal);
    private readonly List<string> _factoryRequests = [];
    private readonly MutableTimeProvider _timeProvider = new(Now);
    private readonly FakeKeyClient _keyClient;

    public KeyVaultKeyProtectorTests()
    {
        _keyClient = new FakeKeyClient(new Uri(VaultUri), "fg-apim-key-encryption", _rsaV1) { CurrentVersionId = new Uri(Version1) };
    }

    [Fact]
    public async Task Protect_then_Unprotect_returns_the_original_key()
    {
        var protector = CreateProtector();

        var ciphertext = await protector.ProtectAsync(ApimKey, CancellationToken.None);

        Assert.Equal(ApimKey, await protector.UnprotectAsync(ciphertext, CancellationToken.None));
    }

    [Fact]
    public async Task Envelope_records_the_versioned_key_id_not_the_versionless_one_configured()
    {
        var protector = CreateProtector();

        var ciphertext = await protector.ProtectAsync(ApimKey, CancellationToken.None);

        Assert.True(KeyEnvelope.TryParse(ciphertext, out var envelope));
        Assert.Equal(KeyEnvelope.KeyVaultScheme, envelope!.Scheme);
        Assert.Equal(Version1, envelope.KeyId);
        Assert.Equal(384, Convert.FromBase64String(envelope.Payload).Length); // RSA-3072 block
        Assert.DoesNotContain(ApimKey, ciphertext, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrapping_never_uses_a_client_bound_to_the_versionless_uri()
    {
        // A CryptographyClient built on the versionless URI caches whatever version it first sees for
        // its whole lifetime — the bug this design exists to avoid. The factory below throws on it.
        var protector = CreateProtector();

        _ = await protector.ProtectAsync(ApimKey, CancellationToken.None);
        _ = await protector.ProtectAsync(ApimKey, CancellationToken.None);

        Assert.All(_factoryRequests, request => Assert.NotEqual(VersionlessKeyUri, request));
        Assert.Equal([Version1], _factoryRequests);
    }

    [Fact]
    public async Task Current_version_is_resolved_through_KeyClient_once_per_TTL_window()
    {
        var protector = CreateProtector();

        _ = await protector.ProtectAsync(ApimKey, CancellationToken.None);
        _ = await protector.ProtectAsync(ApimKey, CancellationToken.None);
        Assert.Equal(1, _keyClient.Calls);

        _timeProvider.Advance(KeyVaultKeyProtector.CurrentVersionTtl + TimeSpan.FromSeconds(1));
        _ = await protector.ProtectAsync(ApimKey, CancellationToken.None);

        Assert.Equal(2, _keyClient.Calls);
    }

    [Fact]
    public async Task After_a_Key_Vault_key_rotation_new_wraps_use_the_new_version_and_old_envelopes_still_unwrap()
    {
        var protector = CreateProtector();
        var before = await protector.ProtectAsync(ApimKey, CancellationToken.None);

        _keyClient.CurrentVersionId = new Uri(Version2); // the vault rotated
        var withinTtl = await protector.ProtectAsync(ApimKey, CancellationToken.None);
        _timeProvider.Advance(KeyVaultKeyProtector.CurrentVersionTtl + TimeSpan.FromSeconds(1));
        var afterTtl = await protector.ProtectAsync(ApimKey, CancellationToken.None);

        Assert.Equal(Version1, KeyEnvelope.ParseFor(before, KeyEnvelope.KeyVaultScheme).KeyId);
        Assert.Equal(Version1, KeyEnvelope.ParseFor(withinTtl, KeyEnvelope.KeyVaultScheme).KeyId); // documented ≤ TTL lag
        Assert.Equal(Version2, KeyEnvelope.ParseFor(afterTtl, KeyEnvelope.KeyVaultScheme).KeyId);

        var keyClientCalls = _keyClient.Calls;
        Assert.Equal(ApimKey, await protector.UnprotectAsync(before, CancellationToken.None));
        Assert.Equal(ApimKey, await protector.UnprotectAsync(withinTtl, CancellationToken.None));
        Assert.Equal(ApimKey, await protector.UnprotectAsync(afterTtl, CancellationToken.None));
        Assert.Equal(keyClientCalls, _keyClient.Calls); // unwrap is pinned to the envelope's version; no lookup
        Assert.Equal(2, _clients[Version1].UnwrapCalls); // `before` and `withinTtl` were both wrapped under v1
        Assert.Equal(1, _clients[Version2].UnwrapCalls);
    }

    [Fact]
    public async Task One_client_per_key_version_is_created_and_reused()
    {
        var protector = CreateProtector();

        var first = await protector.ProtectAsync(ApimKey, CancellationToken.None);
        var second = await protector.ProtectAsync(ApimKey, CancellationToken.None);
        _ = await protector.UnprotectAsync(first, CancellationToken.None);
        _ = await protector.UnprotectAsync(second, CancellationToken.None);

        Assert.Equal([Version1], _factoryRequests);
        Assert.Equal(2, _clients[Version1].WrapCalls);
        Assert.Equal(2, _clients[Version1].UnwrapCalls);
    }

    [Fact]
    public async Task Envelope_fits_the_1000_character_column_even_for_an_RSA_4096_key_with_a_long_vault_name()
    {
        using var rsa4096 = RSA.Create(4096);
        const string LongVault = "https://kv-foundrygate-production-westeurope.vault.azure.net";
        const string LongVersionedKeyId = LongVault + "/keys/fg-apim-key-encryption/0123456789abcdef0123456789abcdef";
        var keyClient = new FakeKeyClient(new Uri(LongVault), "fg-apim-key-encryption", rsa4096) { CurrentVersionId = new Uri(LongVersionedKeyId) };
        var protector = new KeyVaultKeyProtector(new Uri(LongVault + "/keys/fg-apim-key-encryption"), keyClient, id => new FakeCryptographyClient(id.AbsoluteUri, rsa4096), _timeProvider);

        var ciphertext = await protector.ProtectAsync(ApimKey, CancellationToken.None);

        Assert.InRange(ciphertext.Length, 1, 1000);
        Assert.Equal(ApimKey, await protector.UnprotectAsync(ciphertext, CancellationToken.None));
    }

    [Fact]
    public async Task Protect_refuses_when_the_current_key_version_is_disabled()
    {
        _keyClient.CurrentEnabled = false;
        var protector = CreateProtector();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => protector.ProtectAsync(ApimKey, CancellationToken.None));

        Assert.Contains("disabled", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dp1:abc")]
    [InlineData("kv1:not a uri:AQID")]
    [InlineData("kv1:https://kv-someone-else.vault.azure.net/keys/fg-apim-key-encryption/0123456789abcdef0123456789abcdef:AQID")] // other vault
    [InlineData("kv1:https://kv-fg-dev-abc12.vault.azure.net/keys/another-key/0123456789abcdef0123456789abcdef:AQID")] // other key
    [InlineData("kv1:https://kv-fg-dev-abc12.vault.azure.net/keys/fg-apim-key-encryption:AQID")] // versionless
    [InlineData("kv1:http://kv-fg-dev-abc12.vault.azure.net/keys/fg-apim-key-encryption/0123456789abcdef0123456789abcdef:AQID")] // not https
    public async Task Unprotect_refuses_envelopes_that_do_not_name_a_version_of_the_configured_key(string ciphertext)
    {
        var protector = CreateProtector();

        await Assert.ThrowsAsync<InvalidOperationException>(() => protector.UnprotectAsync(ciphertext, CancellationToken.None));

        Assert.Empty(_factoryRequests); // never even built a client for the foreign id
    }

    [Fact]
    public async Task Unprotect_accepts_a_versioned_id_of_the_configured_key_with_different_host_casing()
    {
        var protector = CreateProtector();
        var ciphertext = await protector.ProtectAsync(ApimKey, CancellationToken.None);
        var upperHost = ciphertext.Replace("kv-fg-dev-abc12.vault.azure.net", "KV-FG-DEV-ABC12.VAULT.AZURE.NET", StringComparison.Ordinal);

        Assert.Equal(ApimKey, await protector.UnprotectAsync(upperHost, CancellationToken.None));
    }

    [Theory]
    [InlineData("https://kv-fg-dev-abc12.vault.azure.net/secrets/not-a-key")]
    [InlineData("https://kv-fg-dev-abc12.vault.azure.net/")]
    public void Constructor_rejects_a_uri_that_is_not_a_key_uri(string uri) =>
        Assert.Throws<ArgumentException>(() => new KeyVaultKeyProtector(new Uri(uri), _keyClient, CreateClient, _timeProvider));

    public void Dispose()
    {
        _rsaV1.Dispose();
        _rsaV2.Dispose();
    }

    private KeyVaultKeyProtector CreateProtector() => new(new Uri(VersionlessKeyUri), _keyClient, CreateClient, _timeProvider);

    /// <summary>
    /// Models the SDK faithfully: a client is bound to exactly the version in its URI. A versionless
    /// URI is refused outright — the protector must resolve the version first (see the class remarks).
    /// </summary>
    private CryptographyClient CreateClient(Uri keyId)
    {
        _factoryRequests.Add(keyId.AbsoluteUri);

        var version = keyId.AbsoluteUri;
        if (version == VersionlessKeyUri)
        {
            throw new InvalidOperationException("The protector asked for a client on the versionless URI; that client would pin the first version it sees forever.");
        }

        var rsa = version == Version1 ? _rsaV1 : _rsaV2;
        var client = new FakeCryptographyClient(version, rsa);
        _clients[keyId.AbsoluteUri] = client;
        return client;
    }
}
