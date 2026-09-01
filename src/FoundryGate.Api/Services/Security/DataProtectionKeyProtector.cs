using Microsoft.AspNetCore.DataProtection;

namespace FoundryGate.Api.Services.Security;

/// <summary>
/// <see cref="IKeyProtector"/> for the <c>local</c> environment only: ASP.NET Core Data Protection
/// with the machine-local key ring, so local dev and the integration tests need no Azure at all.
/// Stored as <c>dp1:{token}</c> (<see cref="KeyEnvelope"/>). <see cref="KeyProtectorFactory"/>
/// refuses to construct this outside <c>local</c> — a machine-local key ring is not an
/// at-rest-encryption story for a shared database (the keys live next to the app, not in an HSM,
/// and a redeployed container loses them).
/// </summary>
public sealed class DataProtectionKeyProtector : IKeyProtector
{
    /// <summary>Purpose string; changing it invalidates every locally stored key (fine — local rows are disposable).</summary>
    public const string Purpose = "FoundryGate.ApimSubscriptionKey.v1";

    private readonly IDataProtector _protector;

    public DataProtectionKeyProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        _protector = provider.CreateProtector(Purpose);
    }

    /// <inheritdoc />
    public Task<string> ProtectAsync(string plaintext, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new KeyEnvelope(KeyEnvelope.DataProtectionScheme, null, _protector.Protect(plaintext)).ToString());
    }

    /// <inheritdoc />
    public Task<string> UnprotectAsync(string ciphertext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var envelope = KeyEnvelope.ParseFor(ciphertext, KeyEnvelope.DataProtectionScheme);
        return Task.FromResult(_protector.Unprotect(envelope.Payload));
    }
}
