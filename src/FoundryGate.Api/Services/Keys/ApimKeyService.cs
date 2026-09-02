using System.Globalization;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Api.Services.Security;
using FoundryGate.Data;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Keys;
using FoundryGate.Domain.Keys.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Api.Services.Keys;

/// <summary>
/// Default <see cref="IApimKeyService"/>. Scoped (shares the request's <see cref="AppDbContext"/> with
/// <see cref="IAuditService"/> so each mutation and its audit row save together); the APIM client and
/// key protector it composes are singletons.
/// </summary>
/// <remarks>
/// Failure ordering, per plan 21's compensation table: the row is claimed (provision) or the actor
/// resolved first, APIM is called next, and the row is written last — so an APIM failure leaves the
/// database as it was (the provision claim rolls back with its transaction), and a database failure
/// after APIM succeeded leaves an orphan subscription that the next <see cref="ProvisionAsync"/>
/// finds by name and adopts (regenerating its keys). Nothing here is retried; ARM's own retry policy
/// covers transient faults.
/// </remarks>
public sealed class ApimKeyService(
    AppDbContext dbContext,
    IApimManagementClient apim,
    IKeyProtector keyProtector,
    IAuditService audit,
    IAuditWriter auditWriter,
    ICurrentUserAccessor currentUser,
    TimeProvider timeProvider,
    ILogger<ApimKeyService> logger) : IApimKeyService
{
    /// <summary>Eight bullets then the hint — the shape <c>ApiKeyResponse.MaskedKey</c> documents.</summary>
    private const string MaskPrefix = "••••••••";

    /// <summary>APIM caps a subscription's display name at 100 characters.</summary>
    private const int ApimDisplayNameMaxLength = 100;

    /// <summary>The only APIM subscription state a developer key is usable in.</summary>
    private const string ActiveState = "active";

    private const string RotationFailureRemedy = "Rotate again (POST /keys/{userId}/rotate) or revoke and re-provision (DELETE /keys/{userId}, then POST /keys/{userId}/provision).";

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
            throw AlreadyHasKey(user);
        }

        await EnsureActorAsync(cancellationToken);
        var subscriptionName = ApimSubscriptionNames.ForUser(user.UserId);
        var resourceId = apim.GetSubscriptionResourceId(subscriptionName);

        // Claim the row before touching APIM. Inside a transaction we own — unless the caller (plan 21's
        // orchestrator) already opened one, in which case the claim and the save simply join it — so an
        // APIM failure below rolls the claim back and never leaves a half-provisioned row. On SQL Server
        // the claimed row stays locked until commit, so a concurrent provisioner blocks, then sees 0 rows.
        await using var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        var claimed = await dbContext.Users
            .Where(u => u.UserId == user.UserId && u.ApimSubscriptionId == string.Empty)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.ApimSubscriptionId, resourceId), cancellationToken);
        if (claimed == 0)
        {
            throw AlreadyHasKey(user);
        }

        // Orphan detection (plan 21 / #66): a previous provision may have created the subscription and
        // then failed to save. Reuse it rather than erroring — but never trust its existing keys, and
        // never adopt one that is not active (its regenerated key would still 401 at the gateway).
        var existing = await apim.GetSubscriptionAsync(subscriptionName, cancellationToken);
        if (existing is not null && !string.Equals(existing.State, ActiveState, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning(
                "Orphan APIM subscription {SubscriptionName} for user {UserId} is in state '{State}', not active; deleting it and creating a fresh one.",
                subscriptionName,
                user.UserId,
                existing.State);
            _ = await apim.DeleteSubscriptionAsync(subscriptionName, cancellationToken);
            existing = null;
        }

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

        // Past the commit point (CONVENTIONS.md "External side effects have a commit point"): APIM has
        // minted the key, so nothing below may be abandoned because the client hung up — everything
        // from here runs on CancellationToken.None. Every refusal happened above.
        var issuedDate = timeProvider.GetUtcNow();
        try
        {
            await StoreKeyAsync(user, subscription.ResourceId, keys.PrimaryKey, issuedDate, CancellationToken.None);

            await audit.LogAsync(
                AuditActions.KeyProvisioned,
                AuditTargetTypes.ApiKey,
                TargetId(user),
                new { apimSubscriptionId = subscription.ResourceId, subscriptionName, productId, reusedOrphan },
                CancellationToken.None);
            await dbContext.SaveChangesAsync(CancellationToken.None);

            if (transaction is not null)
            {
                await transaction.CommitAsync(CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "APIM minted subscription {SubscriptionName} for user {UserId} under product {ProductId} but the row could not be saved; the subscription is now an orphan that the next provision will adopt by name.",
                subscriptionName,
                user.UserId,
                productId);
            throw;
        }

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

        try
        {
            // Both keys (#117): the secondary is never issued, but leaving it unrotated would leave a
            // live credential whose lifetime exceeds every primary the developer has ever held.
            await apim.RegeneratePrimaryKeyAsync(subscriptionName, cancellationToken);
            await apim.RegenerateSecondaryKeyAsync(subscriptionName, cancellationToken);
        }
        catch (ApimSubscriptionNotFoundException exception)
        {
            throw SubscriptionMissing(user, exception);
        }

        // From here on APIM holds new keys; anything that stops us storing the new primary leaves the
        // row's ciphertext stale. Keep the previous values so the failure path can restore them and
        // leave a trail instead of a silently unrevealable key.
        var previous = (user.ApimSubscriptionKey, user.ApimSubscriptionKeyHint, user.ApimKeyIssuedDate);
        AuditLog? rotatedRow = null;
        DateTimeOffset issuedDate;
        ApimSubscriptionKeys keys;

        try
        {
            keys = await apim.ListSecretsAsync(subscriptionName, cancellationToken);
            issuedDate = timeProvider.GetUtcNow();
            await StoreKeyAsync(user, user.ApimSubscriptionId, keys.PrimaryKey, issuedDate, cancellationToken);

            rotatedRow = await audit.LogAsync(
                AuditActions.KeyRotated,
                AuditTargetTypes.ApiKey,
                TargetId(user),
                new { apimSubscriptionId = user.ApimSubscriptionId, subscriptionName, keysRegenerated = new[] { "primary", "secondary" } },
                cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RecordRotationFailureAsync(user, subscriptionName, previous, rotatedRow, exception, cancellationToken);
            throw;
        }

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

        return await RevokeCoreAsync(
            user,
            // CancellationToken.None: this row is written after APIM has already deleted the subscription.
            details => audit.LogAsync(AuditActions.KeyRevoked, AuditTargetTypes.ApiKey, TargetId(user), new { details.apimSubscriptionId, details.subscriptionName, details.existedInApim }, CancellationToken.None),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsSystemAsync(User user, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!HasKey(user))
        {
            return false;
        }

        return await RevokeCoreAsync(
            user,
            details => Task.FromResult(auditWriter.AddSystem(AuditActions.KeyRevoked, AuditTargetTypes.ApiKey, TargetId(user), new { details.apimSubscriptionId, details.subscriptionName, details.existedInApim, reason })),
            cancellationToken);
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

        // Added, not saved (#156 review): this method is called from inside quota resolution, which runs
        // in the middle of its caller's unit of work. Saving here would commit that caller's
        // half-finished mutation — a quota written to the database with no audit row describing it — so
        // the row joins the caller's change tracker and commits with everything else. CancellationToken.None
        // because APIM has already been re-scoped; the caller's save must run on None for the same reason.
        await audit.LogAsync(
            AuditActions.KeyTierChanged,
            AuditTargetTypes.ApiKey,
            TargetId(user),
            new { apimSubscriptionId = user.ApimSubscriptionId, subscriptionName, before = current.ProductId, after = productId },
            CancellationToken.None);

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

        // HasKey ⇒ ApimKeyIssuedDate set is an invariant this service writes; a violation is corrupt
        // data to surface, not a date to make up.
        var issuedDate = user.ApimKeyIssuedDate
            ?? throw new InvalidOperationException($"User {user.UserId} has an APIM key but no ApimKeyIssuedDate; the row is inconsistent.");

        var plaintext = await keyProtector.UnprotectAsync(user.ApimSubscriptionKey, cancellationToken);

        await audit.LogAsync(
            AuditActions.KeyRevealed,
            AuditTargetTypes.ApiKey,
            TargetId(user),
            new { apimSubscriptionId = user.ApimSubscriptionId },
            cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Revealed APIM key for user {UserId}.", user.UserId);

        return Reveal(user, plaintext, issuedDate);
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
    public async Task<ApiKeyRevealResponse> RotateForUserAsync(int userId, CancellationToken cancellationToken) =>
        await RotateAsync(await FindUserAsync(userId, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public async Task<bool> RevokeForUserAsync(int userId, CancellationToken cancellationToken) =>
        await RevokeAsync(await FindUserAsync(userId, cancellationToken), cancellationToken);

    /// <summary>The shared revoke body; <paramref name="addAudit"/> decides who the row is attributed to.</summary>
    private async Task<bool> RevokeCoreAsync(
        User user,
        Func<(string apimSubscriptionId, string subscriptionName, bool existedInApim), Task<AuditLog>> addAudit,
        CancellationToken cancellationToken)
    {
        var subscriptionName = ApimSubscriptionNames.ForUser(user.UserId);
        var apimSubscriptionId = user.ApimSubscriptionId;

        var existedInApim = await apim.DeleteSubscriptionAsync(subscriptionName, cancellationToken);

        // Past the commit point, and this one cannot be undone: the subscription is gone from the
        // gateway and the developer's key is dead whatever happens next. Clearing the row and writing
        // the audit trail therefore runs on CancellationToken.None — a client that hangs up must not
        // leave a live-looking key ciphertext behind an already-deleted subscription, unaudited.
        try
        {
            user.ApimSubscriptionId = string.Empty;
            user.ApimSubscriptionKey = string.Empty;
            user.ApimSubscriptionKeyHint = string.Empty;
            user.ApimKeyIssuedDate = null;

            _ = await addAudit((apimSubscriptionId, subscriptionName, existedInApim));
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "APIM deleted subscription {SubscriptionName} for user {UserId} but the row still carries the dead key; re-run the revocation (it is idempotent on a missing subscription) to clear it.",
                subscriptionName,
                user.UserId);
            throw;
        }

        logger.LogInformation(
            "Revoked APIM subscription {SubscriptionName} for user {UserId} (existed in APIM: {ExistedInApim}); user remains {ActiveState}.",
            subscriptionName,
            user.UserId,
            existedInApim,
            user.IsActive ? "active" : "inactive");

        return true;
    }

    /// <summary>
    /// APIM regenerated the keys but the new primary was not stored. Restore the previous (stale)
    /// values so the row is at least self-consistent, drop the unsaved <c>key.rotated</c> row, log at
    /// Error with the remedy, and try to leave a <c>key.rotation-failed</c> audit row (best effort — the
    /// database itself may be what failed).
    /// </summary>
    private async Task RecordRotationFailureAsync(
        User user,
        string subscriptionName,
        (string Key, string Hint, DateTimeOffset? IssuedDate) previous,
        AuditLog? rotatedRow,
        Exception exception,
        CancellationToken cancellationToken)
    {
        user.ApimSubscriptionKey = previous.Key;
        user.ApimSubscriptionKeyHint = previous.Hint;
        user.ApimKeyIssuedDate = previous.IssuedDate;

        if (rotatedRow is not null)
        {
            dbContext.Entry(rotatedRow).State = EntityState.Detached;
        }

        logger.LogError(
            exception,
            "APIM regenerated the keys of subscription {SubscriptionName} but the new key could not be stored; the ciphertext on user {UserId} is now STALE and reveal will return a dead key. {Remedy}",
            subscriptionName,
            user.UserId,
            RotationFailureRemedy);

        try
        {
            await audit.LogAsync(
                AuditActions.KeyRotationFailed,
                AuditTargetTypes.ApiKey,
                TargetId(user),
                new { apimSubscriptionId = user.ApimSubscriptionId, subscriptionName, error = exception.GetType().Name, remedy = RotationFailureRemedy },
                cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception auditException) when (auditException is not OperationCanceledException)
        {
            logger.LogError(auditException, "Could not record the key.rotation-failed audit row for user {UserId}; the log line above is the only trail.", user.UserId);
        }
    }

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

    private static ConflictException AlreadyHasKey(User user) =>
        new($"User {user.UserId} already has an APIM key (or one is being provisioned right now). Rotate it (POST /keys/{user.UserId}/rotate) or revoke it (DELETE /keys/{user.UserId}) before provisioning a new one.");

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
