namespace FoundryGate.Api.Services.Security;

/// <summary>
/// The stored shape of an encrypted APIM key: <c>{scheme}:{payload}</c>, or for Key Vault
/// <c>{scheme}:{keyId}:{payload}</c> where <c>keyId</c> is the <em>versioned</em> Key Vault key URI
/// that performed the wrap. A versioned scheme tag means a future algorithm change can coexist with
/// existing rows; recording the key version means a Key Vault key rotation is detectable per row and
/// old rows keep unwrapping with the version that wrapped them (Key Vault retains prior versions).
/// </summary>
/// <param name="Scheme">One of <see cref="KeyVaultScheme"/> / <see cref="DataProtectionScheme"/>.</param>
/// <param name="KeyId">The versioned Key Vault key URI for <see cref="KeyVaultScheme"/>; <see langword="null"/> otherwise.</param>
/// <param name="Payload">The ciphertext: base64 for Key Vault, the Data Protection token otherwise. Never contains <c>:</c>.</param>
public sealed record KeyEnvelope(string Scheme, string? KeyId, string Payload)
{
    /// <summary>Key Vault RSA-OAEP-256 wrap, version 1: <c>kv1:{keyId}:{base64}</c>.</summary>
    public const string KeyVaultScheme = "kv1";

    /// <summary>ASP.NET Core Data Protection, version 1: <c>dp1:{token}</c>.</summary>
    public const string DataProtectionScheme = "dp1";

    private const char Separator = ':';

    /// <summary>Renders the envelope as the string stored in <c>User.ApimSubscriptionKey</c>.</summary>
    public override string ToString() =>
        KeyId is null ? $"{Scheme}{Separator}{Payload}" : $"{Scheme}{Separator}{KeyId}{Separator}{Payload}";

    /// <summary>
    /// Parses a stored value. The key id (a URI) itself contains <c>:</c>, so the payload is taken
    /// from the <em>last</em> separator — payloads are base64/base64url and never contain one.
    /// </summary>
    public static bool TryParse(string? value, out KeyEnvelope? envelope)
    {
        envelope = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var firstSeparator = value.IndexOf(Separator, StringComparison.Ordinal);
        if (firstSeparator <= 0)
        {
            return false;
        }

        var scheme = value[..firstSeparator];
        var rest = value[(firstSeparator + 1)..];

        if (string.Equals(scheme, KeyVaultScheme, StringComparison.Ordinal))
        {
            var lastSeparator = rest.LastIndexOf(Separator);
            if (lastSeparator <= 0 || lastSeparator == rest.Length - 1)
            {
                return false;
            }

            envelope = new KeyEnvelope(scheme, rest[..lastSeparator], rest[(lastSeparator + 1)..]);
            return true;
        }

        if (string.Equals(scheme, DataProtectionScheme, StringComparison.Ordinal))
        {
            if (rest.Length == 0)
            {
                return false;
            }

            envelope = new KeyEnvelope(scheme, null, rest);
            return true;
        }

        return false;
    }

    /// <summary>Parses or throws the <see cref="InvalidOperationException"/> <see cref="IKeyProtector.UnprotectAsync"/> documents, checking the scheme matches <paramref name="expectedScheme"/>.</summary>
    public static KeyEnvelope ParseFor(string? value, string expectedScheme)
    {
        if (!TryParse(value, out var envelope) || envelope is null)
        {
            throw new InvalidOperationException(
                "The stored APIM key is not a recognized encryption envelope; it cannot be decrypted.");
        }

        if (!string.Equals(envelope.Scheme, expectedScheme, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The stored APIM key was encrypted with the '{envelope.Scheme}' scheme but the configured key protector only opens '{expectedScheme}'. " +
                "Check KeyProtection:Provider — a value written under one provider cannot be read under the other.");
        }

        return envelope;
    }
}
