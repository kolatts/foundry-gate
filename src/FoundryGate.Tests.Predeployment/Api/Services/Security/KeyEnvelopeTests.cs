using FoundryGate.Api.Services.Security;

namespace FoundryGate.Tests.Predeployment.Api.Services.Security;

/// <summary>The stored-envelope grammar both protectors write and read.</summary>
public class KeyEnvelopeTests
{
    private const string VersionedKeyId = "https://kv-fg-dev-abc12.vault.azure.net/keys/fg-apim-key-encryption/0123456789abcdef0123456789abcdef";

    [Fact]
    public void KeyVault_envelope_round_trips_a_key_id_that_itself_contains_colons()
    {
        var envelope = new KeyEnvelope(KeyEnvelope.KeyVaultScheme, VersionedKeyId, "AQID");

        var text = envelope.ToString();

        Assert.Equal($"kv1:{VersionedKeyId}:AQID", text);
        Assert.True(KeyEnvelope.TryParse(text, out var parsed));
        Assert.Equal(envelope, parsed);
    }

    [Fact]
    public void DataProtection_envelope_round_trips()
    {
        var envelope = new KeyEnvelope(KeyEnvelope.DataProtectionScheme, null, "CfDJ8token_base64url-payload");

        var text = envelope.ToString();

        Assert.Equal("dp1:CfDJ8token_base64url-payload", text);
        Assert.True(KeyEnvelope.TryParse(text, out var parsed));
        Assert.Equal(envelope, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("plaintext-apim-key-with-no-scheme")]
    [InlineData(":payload")]
    [InlineData("xx9:payload")]
    [InlineData("kv1:no-separator-after-key-id")]
    [InlineData("kv1:https://vault/keys/k/v:")]
    [InlineData("dp1:")]
    public void TryParse_rejects_anything_that_is_not_an_envelope(string? value)
    {
        Assert.False(KeyEnvelope.TryParse(value, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void ParseFor_rejects_an_envelope_from_the_other_provider_with_a_configuration_hint()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => KeyEnvelope.ParseFor("dp1:abc", KeyEnvelope.KeyVaultScheme));

        Assert.Contains("'dp1'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("'kv1'", exception.Message, StringComparison.Ordinal);
        Assert.Contains("KeyProtection:Provider", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseFor_rejects_a_value_that_is_not_an_envelope_at_all() =>
        Assert.Throws<InvalidOperationException>(() => KeyEnvelope.ParseFor("not-an-envelope", KeyEnvelope.DataProtectionScheme));
}
