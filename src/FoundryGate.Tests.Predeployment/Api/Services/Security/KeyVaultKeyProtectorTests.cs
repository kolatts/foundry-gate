using System.Security.Cryptography;
using Azure.Security.KeyVault.Keys.Cryptography;
using FoundryGate.Api.Services.Security;
using FoundryGate.Tests.Predeployment.Support;

namespace FoundryGate.Tests.Predeployment.Api.Services.Security;

/// <summary>
/// The Key Vault protector against a real-RSA <see cref="FakeCryptographyClient"/>: envelope shape,
/// version pinning across a Key Vault key rotation, client caching, and the column length budget.
/// </summary>
public sealed class KeyVaultKeyProtectorTests : IDisposable
{
    private const string ApimKey = "3f9a1c2e4b6d8f0a1b2c3d4e5f607182";
    private const string VersionlessKeyUri = "https://kv-fg-dev-abc12.vault.azure.net/keys/fg-apim-key-encryption";
    private const string Version1 = VersionlessKeyUri + "/0123456789abcdef0123456789abcdef";
    private const string Version2 = VersionlessKeyUri + "/fedcba9876543210fedcba9876543210";

    private readonly RSA _rsaV1 = RSA.Create(3072);
    private readonly RSA _rsaV2 = RSA.Create(3072);
    private readonly Dictionary<string, FakeCryptographyClient> _clients = new(StringComparer.Ordinal);
    private readonly List<string> _factoryRequests = [];
    private string _currentVersion = Version1;

    [Fact]
    public async Task Protect_then_Unprotect_returns_the_original_key()
    {
        var protector = CreateProtector();

        var ciphertext = await protector.ProtectAsync(ApimKey, CancellationToken.None);

        Assert.Equal(ApimKey, await protector.UnprotectAsync(ciphertext, CancellationToken.None));
    }

    [Fact]
    public async Task Envelope_records_the_versioned_key_id_Key_Vault_reports_not_the_versionless_one_configured()
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
    public async Task Wrapping_targets_the_configured_versionless_uri_and_unwrapping_targets_the_recorded_version()
    {
        var protector = CreateProtector();

        var ciphertext = await protector.ProtectAsync(ApimKey, CancellationToken.None);
        _ = await protector.UnprotectAsync(ciphertext, CancellationToken.None);

        Assert.Equal([VersionlessKeyUri, Version1], _factoryRequests);
    }

    [Fact]
    public async Task After_a_Key_Vault_key_rotation_old_rows_still_open_with_their_version_and_new_rows_use_the_new_one()
    {
        var protector = CreateProtector();
        var before = await protector.ProtectAsync(ApimKey, CancellationToken.None);

        _currentVersion = Version2; // the vault rotated: the versionless URI now resolves to v2
        _clients.Remove(VersionlessKeyUri); // a fresh client would see v2 — but the protector caches per key id (see below)
        var freshProtector = CreateProtector();
        var after = await freshProtector.ProtectAsync(ApimKey, CancellationToken.None);

        Assert.Equal(Version1, KeyEnvelope.ParseFor(before, KeyEnvelope.KeyVaultScheme).KeyId);
        Assert.Equal(Version2, KeyEnvelope.ParseFor(after, KeyEnvelope.KeyVaultScheme).KeyId);
        Assert.Equal(ApimKey, await freshProtector.UnprotectAsync(before, CancellationToken.None));
        Assert.Equal(ApimKey, await freshProtector.UnprotectAsync(after, CancellationToken.None));
    }

    [Fact]
    public async Task One_client_per_key_id_is_created_and_reused()
    {
        var protector = CreateProtector();

        var first = await protector.ProtectAsync(ApimKey, CancellationToken.None);
        var second = await protector.ProtectAsync(ApimKey, CancellationToken.None);
        _ = await protector.UnprotectAsync(first, CancellationToken.None);
        _ = await protector.UnprotectAsync(second, CancellationToken.None);

        Assert.Equal(2, _factoryRequests.Count);
        Assert.Equal(2, _clients[VersionlessKeyUri].WrapCalls);
        Assert.Equal(2, _clients[Version1].UnwrapCalls);
    }

    [Fact]
    public async Task Envelope_fits_the_1000_character_column_even_for_an_RSA_4096_key_with_a_long_vault_name()
    {
        using var rsa4096 = RSA.Create(4096);
        const string LongVersionedKeyId = "https://kv-foundrygate-production-westeurope.vault.azure.net/keys/fg-apim-key-encryption/0123456789abcdef0123456789abcdef";
        var protector = new KeyVaultKeyProtector(new Uri(VersionlessKeyUri), _ => new FakeCryptographyClient(LongVersionedKeyId, rsa4096));

        var ciphertext = await protector.ProtectAsync(ApimKey, CancellationToken.None);

        Assert.InRange(ciphertext.Length, 1, 1000);
        Assert.Equal(ApimKey, await protector.UnprotectAsync(ciphertext, CancellationToken.None));
    }

    [Fact]
    public async Task Unprotect_refuses_a_DataProtection_envelope() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProtector().UnprotectAsync("dp1:abc", CancellationToken.None));

    [Fact]
    public async Task Unprotect_refuses_an_envelope_whose_key_id_is_not_a_uri() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProtector().UnprotectAsync("kv1:not a uri:AQID", CancellationToken.None));

    public void Dispose()
    {
        _rsaV1.Dispose();
        _rsaV2.Dispose();
    }

    private KeyVaultKeyProtector CreateProtector() => new(new Uri(VersionlessKeyUri), CreateClient);

    /// <summary>Simulates Key Vault: the versionless URI resolves to the <em>current</em> version; a versioned URI resolves to exactly that version.</summary>
    private CryptographyClient CreateClient(Uri keyId)
    {
        _factoryRequests.Add(keyId.AbsoluteUri);

        var version = keyId.AbsoluteUri == VersionlessKeyUri ? _currentVersion : keyId.AbsoluteUri;
        var rsa = version == Version1 ? _rsaV1 : _rsaV2;

        var client = new FakeCryptographyClient(version, rsa);
        _clients[keyId.AbsoluteUri] = client;
        return client;
    }
}
