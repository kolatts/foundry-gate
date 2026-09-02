using FoundryGate.Api.Services.Keys;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Keys.Contracts;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// An <see cref="IApimKeyService"/> that exists only to be injected. Every member throws, so a test
/// that is about a <em>registration</em> (which implementation the container picks, what lifetime it
/// has) cannot accidentally start asserting on key behaviour — that belongs to
/// <c>ApimKeyServiceTests</c>. Hand-rolled per CONVENTIONS.md (no mocking library).
/// </summary>
public sealed class NeverCalledApimKeyService : IApimKeyService
{
    /// <inheritdoc />
    public Task<ApiKeyRevealResponse> ProvisionAsync(User user, string tierProductId, CancellationToken cancellationToken) => throw Unexpected();

    /// <inheritdoc />
    public Task<ApiKeyRevealResponse> RotateAsync(User user, CancellationToken cancellationToken) => throw Unexpected();

    /// <inheritdoc />
    public Task<bool> RevokeAsync(User user, CancellationToken cancellationToken) => throw Unexpected();

    /// <inheritdoc />
    public Task<bool> RevokeAsSystemAsync(User user, string reason, CancellationToken cancellationToken) => throw Unexpected();

    /// <inheritdoc />
    public Task MoveToProductAsync(User user, string tierProductId, CancellationToken cancellationToken) => throw Unexpected();

    /// <inheritdoc />
    public ApiKeyResponse GetMasked(User user) => throw Unexpected();

    /// <inheritdoc />
    public Task<ApiKeyRevealResponse> RevealAsync(User user, CancellationToken cancellationToken) => throw Unexpected();

    /// <inheritdoc />
    public Task<ApiKeyResponse> GetMineAsync(CancellationToken cancellationToken) => throw Unexpected();

    /// <inheritdoc />
    public Task<ApiKeyRevealResponse> RevealMineAsync(CancellationToken cancellationToken) => throw Unexpected();

    /// <inheritdoc />
    public Task<ApiKeyRevealResponse> RotateMineAsync(CancellationToken cancellationToken) => throw Unexpected();

    /// <inheritdoc />
    public Task<ApiKeyRevealResponse> RotateForUserAsync(int userId, CancellationToken cancellationToken) => throw Unexpected();

    /// <inheritdoc />
    public Task<bool> RevokeForUserAsync(int userId, CancellationToken cancellationToken) => throw Unexpected();

    private static InvalidOperationException Unexpected() =>
        new("This test injects IApimKeyService only to let the container build; no member of it should be called.");
}
