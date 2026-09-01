using System.Security.Cryptography;
using FoundryGate.Api.Services.Security;
using Microsoft.AspNetCore.DataProtection;

namespace FoundryGate.Tests.Predeployment.Api.Services.Security;

/// <summary>The local-only protector: round trip, envelope shape, and refusal of foreign or tampered values.</summary>
public class DataProtectionKeyProtectorTests
{
    private const string ApimKey = "3f9a1c2e4b6d8f0a1b2c3d4e5f607182";

    private readonly DataProtectionKeyProtector _protector = new(new EphemeralDataProtectionProvider());

    [Fact]
    public async Task Protect_then_Unprotect_returns_the_original_key()
    {
        var ciphertext = await _protector.ProtectAsync(ApimKey, CancellationToken.None);

        Assert.Equal(ApimKey, await _protector.UnprotectAsync(ciphertext, CancellationToken.None));
    }

    [Fact]
    public async Task Ciphertext_is_a_dp1_envelope_that_does_not_contain_the_plaintext()
    {
        var ciphertext = await _protector.ProtectAsync(ApimKey, CancellationToken.None);

        Assert.StartsWith("dp1:", ciphertext, StringComparison.Ordinal);
        Assert.DoesNotContain(ApimKey, ciphertext, StringComparison.OrdinalIgnoreCase);
        Assert.True(KeyEnvelope.TryParse(ciphertext, out var envelope));
        Assert.Equal(KeyEnvelope.DataProtectionScheme, envelope!.Scheme);
        Assert.Null(envelope.KeyId);
    }

    [Fact]
    public async Task Unprotect_refuses_a_Key_Vault_envelope()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _protector.UnprotectAsync("kv1:https://kv.vault.azure.net/keys/k/v1:AQID", CancellationToken.None));

        Assert.Contains("'kv1'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unprotect_refuses_a_tampered_payload()
    {
        var ciphertext = await _protector.ProtectAsync(ApimKey, CancellationToken.None);
        var tampered = ciphertext[..^2] + (ciphertext[^2] == 'A' ? "BB" : "AA");

        await Assert.ThrowsAsync<CryptographicException>(() => _protector.UnprotectAsync(tampered, CancellationToken.None));
    }

    [Fact]
    public async Task A_value_protected_under_another_key_ring_cannot_be_opened()
    {
        var other = new DataProtectionKeyProtector(new EphemeralDataProtectionProvider());
        var ciphertext = await other.ProtectAsync(ApimKey, CancellationToken.None);

        await Assert.ThrowsAsync<CryptographicException>(() => _protector.UnprotectAsync(ciphertext, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Protect_rejects_an_empty_key(string? plaintext) =>
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _protector.ProtectAsync(plaintext!, CancellationToken.None));
}
