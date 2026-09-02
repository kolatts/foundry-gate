namespace FoundryGate.Api.Services.Security;

/// <summary>
/// Encrypts an APIM subscription key for storage in <c>User.ApimSubscriptionKey</c> and decrypts it
/// back for the one-time reveal (#95; spec &#167;11 "APIM keys encrypted: stored encrypted in SQL
/// using Azure Key Vault key wrapping"). Two implementations, selected by
/// <c>KeyProtection:Provider</c>: <see cref="KeyVaultKeyProtector"/> (cloud) and
/// <see cref="DataProtectionKeyProtector"/> (local only). Both produce a self-describing
/// <see cref="KeyEnvelope"/> string so a stored value says which scheme — and, for Key Vault, which
/// key version — can open it.
/// </summary>
/// <remarks>
/// Implementations must never log, throw, or otherwise emit the plaintext; the caller treats the
/// returned plaintext as a one-shot value (returned once in an <c>ApiKeyRevealResponse</c>, never
/// cached). Singleton-safe.
/// </remarks>
public interface IKeyProtector
{
    /// <summary>Encrypts <paramref name="plaintext"/> into an envelope string fit for <c>User.ApimSubscriptionKey</c>.</summary>
    Task<string> ProtectAsync(string plaintext, CancellationToken cancellationToken);

    /// <summary>Decrypts an envelope produced by <see cref="ProtectAsync"/>.</summary>
    /// <exception cref="InvalidOperationException">The envelope was produced by a different provider than the one configured (e.g. a <c>kv1</c> value under the Data Protection provider), or is not a recognized envelope at all.</exception>
    Task<string> UnprotectAsync(string ciphertext, CancellationToken cancellationToken);
}
