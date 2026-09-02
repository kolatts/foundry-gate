using System.Globalization;
using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Entra;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Api.Services.Keys;
using FoundryGate.Api.Services.Quota;
using FoundryGate.Data;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Requests;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Api.Services.Lifecycle;

/// <summary>
/// Default <see cref="IUserLifecycleService"/> — plan 21's two pipelines, in one place. Scoped: it
/// shares the request's <see cref="AppDbContext"/> with <see cref="IApimKeyService"/>,
/// <see cref="IQuotaResolutionService"/> and the audit path, which is what lets a whole pipeline commit
/// as one unit of work.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transaction shape.</b> Every public method opens a database transaction unless the caller already
/// has one open, in which case it joins (the sync job wraps a whole run). <see cref="IApimKeyService"/>
/// does the same check, so its provisioning claim and its <c>SaveChangesAsync</c> land inside this
/// transaction instead of committing on their own — verified by
/// <c>UserLifecycleServiceTests.First_login_that_cannot_reach_APIM_leaves_no_User_row</c>, which asserts
/// the row is gone after an APIM failure.
/// </para>
/// <para>
/// <b>Ordering.</b> Refusals (unknown user, wrong state, caller with no <c>User</c> row) happen before
/// anything external is touched. The APIM call is the last thing that can fail with side effects; a
/// failure after it (the commit) leaves an orphan subscription, which the next provision adopts by name.
/// </para>
/// </remarks>
public sealed class UserLifecycleService(
    AppDbContext dbContext,
    IQuotaResolutionService quotaResolution,
    IApimKeyService keys,
    IAuditService audit,
    IAuditWriter auditWriter,
    ICurrentUserAccessor currentUser,
    IEntraDirectoryClient directory,
    AppSettings settings,
    TimeProvider timeProvider,
    ILogger<UserLifecycleService> logger) : IUserLifecycleService
{
    /// <summary><c>User.DisplayName</c> is <c>[StringLength(200)]</c>; Entra allows 256 (same clamp as <see cref="EntraUserSyncService"/>).</summary>
    private const int DisplayNameMaxLength = 200;

    /// <summary>The <c>ReviewNotes</c> stamped on a Pending request that deactivation rejects (plan 21 deprovision step 4).</summary>
    internal const string DeactivationReviewNote = "User deactivated";

    /// <summary>The reason recorded on the system-attributed <c>key.revoked</c> row when the Entra sync deprovisions a departed user.</summary>
    internal const string EntraDepartureReason = "entra-departure";

    /// <inheritdoc />
    public async Task<User> ProvisionAsync(ProvisionTrigger trigger, ProvisionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var period = BillingPeriod.Current(timeProvider);

        // Joined rather than nested when the caller owns one (EF has no nested transactions anyway).
        await using var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        User user;
        var directoryEnriched = false;
        switch (trigger)
        {
            case ProvisionTrigger.FirstLogin:
                (user, directoryEnriched) = await CreateFromFirstLoginAsync(cancellationToken);
                break;
            case ProvisionTrigger.AdminProvision:
                user = await LoadForAdminProvisionAsync(context, cancellationToken);
                break;
            case ProvisionTrigger.Reactivate:
                user = await ReactivateAsync(context, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(trigger), trigger, "Unknown provision trigger.");
        }

        // Step 3: upsert this period's allocation. Nothing is saved here; the tier it resolves to is
        // the product the subscription is minted under, so the gateway enforces the same budget the
        // database records from the very first request.
        var resolution = await quotaResolution.ResolveAsync(user.UserId, period, cancellationToken);
        var tierProductId = resolution.Allocation.TierProductId;

        // Step 4: mint the key. Skipped when the user already holds one (an admin re-activating a user
        // whose subscription was never deleted) — ProvisionAsync would 409, and re-minting a working
        // key would break the CLI the developer already has configured.
        var keyProvisioned = false;
        if (string.IsNullOrEmpty(user.ApimSubscriptionId))
        {
            await ProvisionKeyAsync(user, tierProductId, cancellationToken);
            keyProvisioned = true;
        }

        // Step 6: audit, then one save for everything this pipeline still has pending.
        var action = trigger == ProvisionTrigger.Reactivate ? AuditActions.UserActivated : AuditActions.UserProvisioned;
        _ = await audit.LogAsync(
            action,
            AuditTargetTypes.User,
            TargetId(user.UserId),
            new
            {
                trigger = trigger.ToString(),
                tierProductId,
                allocatedTokens = resolution.Allocation.AllocatedTokens,
                period = period.ToString(),
                keyProvisioned,
                directoryEnriched,
            },
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Provisioned user {UserId} ({Trigger}) on tier {TierProductId} for {Period}; key provisioned: {KeyProvisioned}.",
            user.UserId,
            trigger,
            tierProductId,
            period,
            keyProvisioned);

        return user;
    }

    /// <inheritdoc />
    public async Task DeprovisionAsync(DeprovisionTrigger trigger, int userId, CancellationToken cancellationToken)
    {
        var user = await FindUserAsync(userId, cancellationToken);

        if (!user.IsActive)
        {
            if (trigger == DeprovisionTrigger.AdminDeactivation)
            {
                throw new ConflictException(
                    $"User {userId} is already deactivated. To revoke only their key while they stay active, use DELETE /keys/{userId}.");
            }

            // A sync run must not fail because someone was deactivated between two runs.
            logger.LogDebug("Skipping {Trigger} deprovision of user {UserId}: already deactivated.", trigger, userId);
            return;
        }

        var now = timeProvider.GetUtcNow();
        var period = BillingPeriod.Current(timeProvider);

        await using var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        // Step 1: delete the APIM subscription and clear the key fields. A no-op returning false when
        // the user never had one, so it is called unconditionally.
        var keyRevoked = await RevokeKeyAsync(trigger, user, cancellationToken);

        // Step 2.
        user.IsActive = false;

        // Step 3: hard-stop this period's allocation, so the reconciliation view and the admin UI agree
        // with the gateway (which now rejects the deleted key outright). Nothing to stop if the user
        // never had an allocation this month.
        var allocation = await FindAllocationAsync(userId, period, cancellationToken);
        if (allocation is not null)
        {
            allocation.IsHardStopped = true;
        }

        // Step 4: a Pending request from someone who no longer has access can never be approved into
        // anything useful, and leaving it Pending would keep them on the admin review queue forever.
        // Rejected by the *system*, not by whoever happened to deactivate them — nobody reviewed it.
        // #34's IQuotaRequestService.CancelPendingForUserAsync can replace this block verbatim.
        var pending = await dbContext.QuotaIncreaseRequests
            .Where(r => r.UserId == userId && r.StatusType == QuotaRequestStatusType.Pending)
            .ToListAsync(cancellationToken);
        foreach (var request in pending)
        {
            request.StatusType = QuotaRequestStatusType.Rejected;
            request.ReviewNotes = DeactivationReviewNote;
            request.ReviewedByUserId = null;
            request.ReviewedDate = now;
        }

        // Step 5.
        var details = new
        {
            trigger = trigger.ToString(),
            keyRevoked,
            allocationHardStopped = allocation is not null,
            cancelledRequestCount = pending.Count,
            period = period.ToString(),
        };

        if (trigger == DeprovisionTrigger.EntraDeparture)
        {
            _ = auditWriter.AddSystem(AuditActions.UserDeactivated, AuditTargetTypes.User, TargetId(userId), details);
        }
        else
        {
            _ = await audit.LogAsync(AuditActions.UserDeactivated, AuditTargetTypes.User, TargetId(userId), details, cancellationToken);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        logger.LogInformation(
            "Deprovisioned user {UserId} ({Trigger}): key revoked {KeyRevoked}, allocation hard-stopped {HardStopped}, {CancelledCount} pending request(s) rejected.",
            userId,
            trigger,
            keyRevoked,
            allocation is not null,
            pending.Count);
    }

    /// <summary>
    /// Builds the <c>User</c> for a first login: token claims, enriched from the directory when
    /// <c>Entra:Enabled</c>. Saved immediately — the APIM subscription is named
    /// <c>foundrygate-{UserId}</c>, so the identity value has to exist before step 4 can run. It is
    /// inside the pipeline's transaction, so an APIM failure still leaves no row behind.
    /// </summary>
    private async Task<(User User, bool DirectoryEnriched)> CreateFromFirstLoginAsync(CancellationToken cancellationToken)
    {
        var entraObjectId = currentUser.EntraObjectId;

        if (await currentUser.TryGetUserAsync(cancellationToken) is { } existing)
        {
            throw new ConflictException(
                $"A FoundryGate user already exists for oid {entraObjectId} (UserId {existing.UserId}); first-login provisioning is not repeatable.");
        }

        var user = new User
        {
            EntraObjectId = entraObjectId,
            DisplayName = Clamp(currentUser.DisplayName ?? currentUser.Email ?? entraObjectId),
            Email = currentUser.Email ?? string.Empty,
            IsActive = true,
            LastSyncedDate = timeProvider.GetUtcNow(),
        };

        var directoryEnriched = false;
        if (settings.Entra.Enabled)
        {
            // Only when the feature is on: DisabledEntraDirectoryClient throws (→ 400), and a first
            // login must not fail because a fork chose not to wire Graph up.
            var directoryUser = await LookUpInDirectoryAsync(entraObjectId, cancellationToken);
            if (directoryUser is not null)
            {
                user.DisplayName = Clamp(directoryUser.DisplayName);
                user.Email = directoryUser.Email;
                user.EmployeeId = directoryUser.EmployeeId;
                directoryEnriched = true;
            }
            else
            {
                // Not an error: the account can be freshly created, or hidden from the app's Graph
                // permissions. The token already told us who they are; POST /users/sync fills the rest in.
                logger.LogWarning("Entra directory has no user {EntraObjectId}; provisioning from token claims only.", entraObjectId);
            }
        }

        if (string.IsNullOrWhiteSpace(user.Email))
        {
            logger.LogWarning(
                "Provisioning user {EntraObjectId} with no email: the token carries no preferred_username/upn/email claim{DirectorySuffix}.",
                entraObjectId,
                settings.Entra.Enabled ? " and the directory had none either" : " and Entra:Enabled is false");
        }

        dbContext.Users.Add(user);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Two tabs, one first login: EntraObjectId is unique, so the loser lands here. A retry
            // finds the winner's row and returns it, which is why this is a 409 and not a 500.
            dbContext.Entry(user).State = EntityState.Detached;
            throw new ConflictException(
                $"Another request provisioned oid {entraObjectId} at the same time. Retry GET /users/me.",
                exception);
        }

        return (user, directoryEnriched);
    }

    private async Task<EntraUser?> LookUpInDirectoryAsync(string entraObjectId, CancellationToken cancellationToken)
    {
        try
        {
            return await directory.GetUserAsync(entraObjectId, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Plan 21 step 1's failure branch. 502 rather than the plan's 503: the feature is configured,
            // Graph simply did not answer — the caller can retry, the operator has nothing to fix.
            throw new UpstreamDependencyException(
                "Microsoft Graph could not be reached to look up your directory profile, so your FoundryGate account was not created. Nothing was changed; please retry.",
                exception);
        }
    }

    private async Task<User> LoadForAdminProvisionAsync(ProvisionContext context, CancellationToken cancellationToken)
    {
        var user = await FindUserAsync(RequireUserId(context, ProvisionTrigger.AdminProvision), cancellationToken);

        if (!user.IsActive)
        {
            throw new ConflictException(
                $"User {user.UserId} is deactivated; provisioning a key alone would leave an inactive user holding a working one. Re-activate them (POST /users/{user.UserId}/activate) instead.");
        }

        if (!string.IsNullOrEmpty(user.ApimSubscriptionId))
        {
            throw new ConflictException(
                $"User {user.UserId} already has an APIM key. Rotate it (POST /keys/{user.UserId}/rotate) or revoke it (DELETE /keys/{user.UserId}) first.");
        }

        return user;
    }

    private async Task<User> ReactivateAsync(ProvisionContext context, CancellationToken cancellationToken)
    {
        var user = await FindUserAsync(RequireUserId(context, ProvisionTrigger.Reactivate), cancellationToken);

        if (user.IsActive)
        {
            throw new ConflictException($"User {user.UserId} is already active.");
        }

        user.IsActive = true;
        return user;
    }

    /// <summary>
    /// Step 4, with plan 21's failure taxonomy: "APIM is not wired up here" stays a 503 the operator can
    /// act on, a caller-caused refusal keeps its own status, and everything else becomes a 502 — all
    /// three roll the transaction back, so a failed first login leaves no <c>User</c> row.
    /// </summary>
    private async Task ProvisionKeyAsync(User user, string tierProductId, CancellationToken cancellationToken)
    {
        try
        {
            _ = await keys.ProvisionAsync(user, tierProductId, cancellationToken);
        }
        catch (Exception exception) when (IsUpstreamFailure(exception))
        {
            throw new UpstreamDependencyException(
                $"The API Management gateway did not accept the subscription for user {user.UserId}, so no key was issued and nothing was saved. Please retry.",
                exception);
        }
    }

    private async Task<bool> RevokeKeyAsync(DeprovisionTrigger trigger, User user, CancellationToken cancellationToken)
    {
        try
        {
            return trigger == DeprovisionTrigger.EntraDeparture
                ? await keys.RevokeAsSystemAsync(user, EntraDepartureReason, cancellationToken)
                : await keys.RevokeAsync(user, cancellationToken);
        }
        catch (Exception exception) when (IsUpstreamFailure(exception))
        {
            throw new UpstreamDependencyException(
                $"The API Management gateway did not accept the deletion of user {user.UserId}'s subscription, so they have not been deactivated and nothing was saved. Please retry.",
                exception);
        }
    }

    /// <summary>
    /// True for a failure that is neither the caller's fault nor ours: the exception types the services
    /// throw deliberately (mapped to 400/403/404/409/503) and database failures (a genuine 500) pass
    /// through untouched; anything else came from the gateway.
    /// </summary>
    private static bool IsUpstreamFailure(Exception exception) =>
        exception is not OperationCanceledException
        and not FeatureNotConfiguredException
        and not UpstreamDependencyException
        and not ConflictException
        and not ArgumentException
        and not KeyNotFoundException
        and not UnauthorizedAccessException
        and not DbUpdateException;

    private async Task<User> FindUserAsync(int userId, CancellationToken cancellationToken) =>
        await dbContext.Users.FindAsync([userId], cancellationToken)
        ?? throw new KeyNotFoundException($"User {userId} was not found.");

    private async Task<QuotaAllocation?> FindAllocationAsync(int userId, BillingPeriod period, CancellationToken cancellationToken) =>
        dbContext.QuotaAllocations.Local.FirstOrDefault(a => a.UserId == userId && a.PeriodYear == period.Year && a.PeriodMonth == period.Month)
        ?? await dbContext.QuotaAllocations.SingleOrDefaultAsync(
            a => a.UserId == userId && a.PeriodYear == period.Year && a.PeriodMonth == period.Month,
            cancellationToken);

    private static int RequireUserId(ProvisionContext context, ProvisionTrigger trigger) =>
        context.UserId
        ?? throw new ArgumentException($"A user id is required for the {trigger} provision trigger.", nameof(context));

    private static string Clamp(string value) => value.Length > DisplayNameMaxLength ? value[..DisplayNameMaxLength] : value;

    private static string TargetId(int userId) => userId.ToString(CultureInfo.InvariantCulture);
}
