using System.Globalization;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Api.Services.Security;
using FoundryGate.Data;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Keys;
using FoundryGate.Domain.Keys.Contracts;

namespace FoundryGate.Api.Services.Keys;

/// <summary>
/// Default <see cref="IApimKeyService"/>. Scoped (shares the request's <see cref="AppDbContext"/> with
/// <see cref="IAuditService"/> so each mutation and its audit row save together); the APIM client and
/// key protector it composes are singletons.
/// </summary>
/// <remarks>
/// Failure ordering, per plan 21's compensation table: APIM is called <em>before</em> the row is
/// touched, so an APIM failure leaves the database untouched (the caller sees the ARM error), and a
/// database failure after APIM succeeded leaves an orphan subscription that the next
/// <see cref="ProvisionAsync"/> finds by name and reuses (regenerating its keys). Nothing here is
/// retried; ARM's own retry policy covers transient faults.
/// </remarks>
public sealed class ApimKeyService(
    AppDbContext dbContext,
    IApimManagementClient apim,
    IKeyProtector keyProtector,
    IAuditService audit,
    ICurrentUserAccessor currentUser,
    TimeProvider timeProvider,
    ILogger<ApimKeyService> logger) : IApimKeyService
{
    /// <summary>Eight bullets then the hint — the shape <c>ApiKeyResponse.MaskedKey</c> documents.</summary>
    private const string MaskPrefix = "••••••••";

    /// <summary>APIM caps a subscription's display name at 100 characters.</summary>
    private const int ApimDisplayNameMaxLength = 100;

    /// <inheritdoc />
    public async Task<ApiKeyRevealResponse> ProvisionAsync(User user, string tierProductId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var productId = NormalizeTier(tierProductId);

        if (user.UserId <= 0)
        {
            throw new InvalidOperationException("The user must be saved (have a UserId) before an APIM subscription can be named after it.");
        }

        if (HasKey(user))
        {
            throw new ConflictException(
                $"User {user.UserId} already has an APIM key. Rotate it (POST /keys/{user.UserId}/rotate) or revoke it (DELETE /keys/{user.UserId}) before provisioning a new one.");
        }

        await EnsureActorAsync(cancellationToken);
        var subscriptionName = ApimSubscriptionNames.ForUser(user.UserId);

        // Orphan detection (plan 21 / #66): a previous provision may have created the subscription and
        // then failed to save. Reuse it rather than erroring — but never trust its existing keys.
        var existing = await apim.GetSubscriptionAsync(subscriptionName, cancellationToken);
        ApimSubscription subscription;
        ApimSubscriptionKeys keys;
        var reusedOrphan = existing is not null;

        if (existing is not null)
        {
            if (!string.Equals(existing.ProductId, productId, StringComparison.OrdinalIgnoreCase))
            {
                await apim.UpdateScopeAsync(subscriptionName, productId, cancellationToken);
            }

            await apim.RegeneratePrimaryKeyAsync(subscriptionName, cancellationToken);
            await apim.RegenerateSecondaryKeyAsync(subscriptionName, cancellationToken);
            keys = await apim.ListSecretsAsync(subscriptionName, cancellationToken);
            subscription = existing;
        }
        else
        {
            var created = await apim.CreateOrUpdateSubscriptionAsync(subscriptionName, DisplayNameFor(user), productId, cancellationToken);
            subscription = created.Subscription;
            keys = created.Keys;
        }

        var issuedDate = timeProvider.GetUtcNow();
        await StoreKeyAsync(user, subscription.ResourceId, keys.PrimaryKey, issuedDate, cancellationToken);

        await audit.LogAsync(
            AuditActions.KeyProvisioned,
            AuditTargetTypes.ApiKey,
            TargetId(user),
            new { apimSubscriptionId = subscription.ResourceId, subscriptionName, productId, reusedOrphan },
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Provisioned APIM subscription {SubscriptionName} for user {UserId} under product {ProductId} (orphan reused: {ReusedOrphan}).",
            subscriptionName,
            user.UserId,
            productId,
            reusedOrphan);

        return Reveal(user, keys.PrimaryKey, issuedDate);
    }

    /// <inheritdoc />
    public async Task<ApiKeyRevealResponse> RotateAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        RequireKey(user);
        await EnsureActorAsync(cancellationToken);

        var subscriptionName = ApimSubscriptionNames.ForUser(user.UserId);
        ApimSubscriptionKeys keys;

        try
        {
            // Both keys (#117): the secondary is never issued, but leaving it unrotated would leave a
            // live credential whose lifetime exceeds every primary the developer has ever held.
            await apim.RegeneratePrimaryKeyAsync(subscriptionName, cancellationToken);
            await apim.RegenerateSecondaryKeyAsync(subscriptionName, cancellationToken);
            keys = await apim.ListSecretsAsync(subscriptionName, cancellationToken);
        }
        catch (ApimSubscriptionNotFoundException exception)
        {
            throw SubscriptionMissing(user, exception);
        }

        var issuedDate = timeProvider.GetUtcNow();
        await StoreKeyAsync(user, user.ApimSubscriptionId, keys.PrimaryKey, issuedDate, cancellationToken);

        await audit.LogAsync(
            AuditActions.KeyRotated,
            AuditTargetTypes.ApiKey,
            TargetId(user),
            new { apimSubscriptionId = user.ApimSubscriptionId, subscriptionName, keysRegenerated = new[] { "primary", "secondary" } },
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Rotated APIM subscription {SubscriptionName} for user {UserId} (primary and secondary regenerated).", subscriptionName, user.UserId);

        return Reveal(user, keys.PrimaryKey, issuedDate);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!HasKey(user))
        {
            return false;
        }

        await EnsureActorAsync(cancellationToken);
        var subscriptionName = ApimSubscriptionNames.ForUser(user.UserId);
        var apimSubscriptionId = user.ApimSubscriptionId;

        var existedInApim = await apim.DeleteSubscriptionAsync(subscriptionName, cancellationToken);

        user.ApimSubscriptionId = string.Empty;
        user.ApimSubscriptionKey = string.Empty;
        user.ApimSubscriptionKeyHint = string.Empty;
        user.ApimKeyIssuedDate = null;

        await audit.LogAsync(
            AuditActions.KeyRevoked,
            AuditTargetTypes.ApiKey,
            TargetId(user),
            new { apimSubscriptionId, subscriptionName, existedInApim },
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Revoked APIM subscription {SubscriptionName} for user {UserId} (existed in APIM: {ExistedInApim}); user remains {IsActive}.", subscriptionName, user.UserId, existedInApim, user.IsActive ? "active" : "inactive");

        return true;
    }

    /// <inheritdoc />
    public async Task MoveToProductAsync(User user, string tierProductId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var productId = NormalizeTier(tierProductId);
        RequireKey(user);
        await EnsureActorAsync(cancellationToken);

        var subscriptionName = ApimSubscriptionNames.ForUser(user.UserId);
        var current = await apim.GetSubscriptionAsync(subscriptionName, cancellationToken)
            ?? throw SubscriptionMissing(user, new ApimSubscriptionNotFoundException(subscriptionName));

        if (string.Equals(current.ProductId, productId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await apim.UpdateScopeAsync(subscriptionName, productId, cancellationToken);
        }
        catch (ApimSubscriptionNotFoundException exception)
        {
            throw SubscriptionMissing(user, exception);
        }

        await audit.LogAsync(
            AuditActions.KeyTierChanged,
            AuditTargetTypes.ApiKey,
            TargetId(user),
            new { apimSubscriptionId = user.ApimSubscriptionId, subscriptionName, before = current.ProductId, after = productId },
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Moved APIM subscription {SubscriptionName} for user {UserId} from product {Before} to {After}.", subscriptionName, user.UserId, current.ProductId, productId);
    }

    /// <inheritdoc />
    public ApiKeyResponse GetMasked(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        return HasKey(user)
            ? new ApiKeyResponse(true, Mask(user.ApimSubscriptionKeyHint), user.ApimSubscriptionId)
            : new ApiKeyResponse(false, null, null);
    }

    /// <inheritdoc />
    public async Task<ApiKeyRevealResponse> RevealAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        RequireKey(user);
        await EnsureActorAsync(cancellationToken);

        var plaintext = await keyProtector.UnprotectAsync(user.ApimSubscriptionKey, cancellationToken);

        await audit.LogAsync(
            AuditActions.KeyRevealed,
            AuditTargetTypes.ApiKey,
            TargetId(user),
            new { apimSubscriptionId = user.ApimSubscriptionId },
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Revealed APIM key for user {UserId}.", user.UserId);

        return Reveal(user, plaintext, user.ApimKeyIssuedDate ?? timeProvider.GetUtcNow());
    }

    /// <inheritdoc />
    public async Task<ApiKeyResponse> GetMineAsync(CancellationToken cancellationToken) =>
        GetMasked(await currentUser.GetRequiredUserAsync(cancellationToken));

    /// <inheritdoc />
    public async Task<ApiKeyRevealResponse> RevealMineAsync(CancellationToken cancellationToken) =>
        await RevealAsync(await GetActiveCallerAsync(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public async Task<ApiKeyRevealResponse> RotateMineAsync(CancellationToken cancellationToken) =>
        await RotateAsync(await GetActiveCallerAsync(cancellationToken), cancellationToken);

    /// <inheritdoc />
    public async Task<ApiKeyRevealResponse> ProvisionForUserAsync(int userId, string tierProductId, CancellationToken cancellationToken)
    {
        var user = await FindUserAsync(userId, cancellationToken);

        if (!user.IsActive)
        {
            throw new ConflictException($"User {userId} is deactivated. Re-activate them (POST /users/{userId}/activate) to issue a key; provisioning alone would leave an inactive user holding a working key.");
        }

        return await ProvisionAsync(user, tierProductId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ApiKeyRevealResponse> RotateForUserAsync(int userId, CancellationToken cancellationToken) =>
        await RotateAsync(await FindUserAsync(userId, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public async Task<bool> RevokeForUserAsync(int userId, CancellationToken cancellationToken) =>
        await RevokeAsync(await FindUserAsync(userId, cancellationToken), cancellationToken);

    /// <summary>
    /// Resolves the audit actor <em>before</em> any APIM side effect. <c>IAuditService.LogAsync</c> would
    /// throw the same 403 later, but by then the subscription would already exist — an orphan created
    /// by a request that was refused. Cheap: the accessor caches the row for the rest of the request.
    /// </summary>
    private async Task EnsureActorAsync(CancellationToken cancellationToken) =>
        _ = await currentUser.GetRequiredUserAsync(cancellationToken);

    private async Task<User> GetActiveCallerAsync(CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredUserAsync(cancellationToken);

        if (!user.IsActive)
        {
            throw new UnauthorizedAccessException("Your FoundryGate account is deactivated; key operations are not available. Contact an administrator.");
        }

        return user;
    }

    private async Task<User> FindUserAsync(int userId, CancellationToken cancellationToken) =>
        await dbContext.Users.FindAsync([userId], cancellationToken)
        ?? throw new KeyNotFoundException($"User {userId} was not found.");

    private async Task StoreKeyAsync(User user, string apimSubscriptionId, string plaintextKey, DateTimeOffset issuedDate, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(plaintextKey))
        {
            throw new InvalidOperationException("APIM returned an empty primary key; refusing to store it.");
        }

        user.ApimSubscriptionKey = await keyProtector.ProtectAsync(plaintextKey, cancellationToken);
        user.ApimSubscriptionId = apimSubscriptionId;
        user.ApimSubscriptionKeyHint = Hint(plaintextKey);
        user.ApimKeyIssuedDate = issuedDate;
    }

    private static ApiKeyRevealResponse Reveal(User user, string plaintextKey, DateTimeOffset issuedDate) =>
        new(plaintextKey, Mask(Hint(plaintextKey)), user.ApimSubscriptionId, issuedDate);

    private static bool HasKey(User user) => !string.IsNullOrEmpty(user.ApimSubscriptionId);

    private static void RequireKey(User user)
    {
        if (!HasKey(user))
        {
            throw new KeyNotFoundException($"User {user.UserId} has no APIM key. An administrator can provision one with POST /keys/{user.UserId}/provision.");
        }
    }

    private static ConflictException SubscriptionMissing(User user, ApimSubscriptionNotFoundException inner) =>
        new(
            $"The APIM subscription behind user {user.UserId}'s key no longer exists on the gateway (deleted outside FoundryGate?). " +
            $"Revoke the key (DELETE /keys/{user.UserId}) and provision a new one.",
            inner);

    /// <summary>Validates against <see cref="GatewayTiers.All"/>; tier ids are lower-case product ids, so the comparison is case-insensitive and the result normalized.</summary>
    private static string NormalizeTier(string tierProductId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tierProductId);

        var match = GatewayTiers.All.FirstOrDefault(tier => string.Equals(tier, tierProductId.Trim(), StringComparison.OrdinalIgnoreCase));
        return match
            ?? throw new ArgumentException($"'{tierProductId}' is not a gateway tier. Valid tiers: {string.Join(", ", GatewayTiers.All)}.", nameof(tierProductId));
    }

    private static string TargetId(User user) => user.UserId.ToString(CultureInfo.InvariantCulture);

    private static string Hint(string plaintextKey) => plaintextKey.Length <= 4 ? plaintextKey : plaintextKey[^4..];

    private static string Mask(string hint) => MaskPrefix + hint;

    private static string DisplayNameFor(User user)
    {
        var displayName = $"FoundryGate {user.Email}";
        return displayName.Length <= ApimDisplayNameMaxLength ? displayName : displayName[..ApimDisplayNameMaxLength];
    }
}
