using System.Globalization;
using Azure;
using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Entra;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Api.Services.Keys;
using FoundryGate.Api.Services.Requests;
using FoundryGate.Core.Gateway;
using FoundryGate.Core.Quota;
using FoundryGate.Data;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Concurrency;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Keys.Contracts;
using FoundryGate.Domain.Quota;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Api.Services.Lifecycle;

/// <summary>
/// Default <see cref="IUserLifecycleService"/> — plan 21's two pipelines, in one place. Scoped: it
/// shares the request's <see cref="AppDbContext"/> with <see cref="IApimKeyService"/>,
/// <see cref="IQuotaResolutionService"/>, <see cref="IQuotaRequestService"/> and the audit path, which
/// is what lets a whole pipeline commit as one unit of work.
/// </summary>
/// <remarks>
/// <para>
/// <b>Provision and deprovision are deliberately shaped differently</b>, because their irreversible
/// step points the other way (#156 review). Provision holds one transaction across the APIM create:
/// its residue — subscription minted, commit failed — is a harmless orphan the next provision adopts
/// by name, so rolling the database back is the right answer and "a failed first login leaves no
/// <c>User</c> row" is true. Deprovision cannot do that: an APIM <c>DELETE</c> has no undo, so
/// rolling back after it would leave a deleted key the database knows nothing about. It therefore does
/// the deletion <em>first and outside</em> any transaction of ours, then takes the database steps in a
/// transaction of their own.
/// </para>
/// <para>
/// <b>Everything after an external side effect runs on <see cref="CancellationToken.None"/></b>
/// (CONVENTIONS.md "External side effects have a commit point"; precedent
/// <c>FoundryDeploymentService.AuditAfterCommitAsync</c>): once APIM has accepted a change, a client
/// that hangs up must not turn it into an unaudited one. Every refusal — unknown user, wrong state,
/// caller with no <c>User</c> row — happens before the first external call, and a save that fails
/// anyway is logged at Error with the identity needed to reconcile it, then rethrown.
/// </para>
/// </remarks>
public sealed class UserLifecycleService(
    AppDbContext dbContext,
    IQuotaResolutionService quotaResolution,
    IQuotaRequestService quotaRequests,
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
    public async Task<User> ProvisionAsync(ProvisionTrigger trigger, ProvisionContext context, CancellationToken cancellationToken) =>
        (await ProvisionCoreAsync(trigger, context, cancellationToken)).User;

    /// <inheritdoc />
    public async Task<ApiKeyRevealResponse> ProvisionKeyForUserAsync(int userId, CancellationToken cancellationToken)
    {
        var (user, key) = await ProvisionCoreAsync(ProvisionTrigger.AdminProvision, ProvisionContext.ForUser(userId), cancellationToken);

        // AdminProvision refuses a user who already holds a key (409), so the mint always happened.
        return key ?? throw new InvalidOperationException($"Provisioning user {user.UserId} returned no key; the AdminProvision trigger must always mint one.");
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

        // Step 1 — the irreversible one, first and outside any transaction of ours. Deleting the
        // subscription is the *point* of deprovisioning: whatever happens to the database afterwards,
        // that key must stop working. Doing it inside a transaction we might roll back would mean a
        // commit failure silently un-recorded a deletion that had already happened. The key service
        // clears the key fields and writes `key.revoked` in its own unit of work (on
        // CancellationToken.None, since APIM has already accepted the delete); a no-op returning false
        // when the user never had a key, so it is called unconditionally.
        var keyRevoked = await RevokeKeyAsync(trigger, user, cancellationToken);

        // Past the commit point once the subscription is gone.
        var completionToken = CommitToken.For(keyRevoked, cancellationToken);

        try
        {
            var now = timeProvider.GetUtcNow();
            var period = BillingPeriod.Current(timeProvider);

            await using var transaction = dbContext.Database.CurrentTransaction is null
                ? await dbContext.Database.BeginTransactionAsync(completionToken)
                : null;

            // Step 2.
            user.IsActive = false;

            // Step 3: hard-stop this period's allocation, so the reconciliation view and the admin UI
            // agree with the gateway (which now rejects the deleted key outright). Nothing to stop if
            // the user never had an allocation this month.
            var allocation = await FindAllocationAsync(userId, period, completionToken);
            if (allocation is not null)
            {
                allocation.IsHardStopped = true;
            }

            // Step 4: a Pending request from someone who no longer has access can never be approved into
            // anything useful, and leaving it Pending would keep them on the admin review queue forever.
            // IQuotaRequestService.CancelPendingForUserAsync (#34/#148) neither saves nor audits and
            // joins this transaction, so the cancellations commit inside this unit of work and are
            // described by this pipeline's audit row rather than by a review decision nobody made.
            var cancelledRequestCount = await quotaRequests.CancelPendingForUserAsync(userId, DeactivationReviewNote, completionToken);

            // Step 5.
            var details = new
            {
                trigger = trigger.ToString(),
                keyRevoked,
                allocationHardStopped = allocation is not null,
                cancelledRequestCount,
                period = period.ToString(),
                deactivatedDate = now,
            };

            if (trigger == DeprovisionTrigger.EntraDeparture)
            {
                _ = auditWriter.AddSystem(AuditActions.UserDeactivated, AuditTargetTypes.User, TargetId(userId), details);
            }
            else
            {
                _ = await audit.LogAsync(AuditActions.UserDeactivated, AuditTargetTypes.User, TargetId(userId), details, completionToken);
            }

            await dbContext.SaveChangesAsync(completionToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(completionToken);
            }

            logger.LogInformation(
                "Deprovisioned user {UserId} ({Trigger}): key revoked {KeyRevoked}, allocation hard-stopped {HardStopped}, {CancelledCount} pending request(s) rejected.",
                userId,
                trigger,
                keyRevoked,
                allocation is not null,
                cancelledRequestCount);
        }
        catch (Exception exception) when (keyRevoked)
        {
            // The documented residue (plan 21's compensation table): the subscription is gone and the
            // key fields are cleared, but the user is still marked active. Self-healing — re-running the
            // deactivation is idempotent, because RevokeAsync tolerates a subscription that is already
            // missing — and the `key.revoked` row already committed, so the trail is not lost.
            logger.LogError(
                exception,
                "User {UserId}'s APIM subscription was deleted but the deactivation could not be recorded; they are still marked active with no key. Re-run POST /users/{UserId}/deactivate — it is idempotent.",
                userId,
                userId);
            throw;
        }
    }

    /// <summary>The shared provision sequence; the key is returned so the admin endpoint can hand the plaintext over once.</summary>
    private async Task<(User User, ApiKeyRevealResponse? Key)> ProvisionCoreAsync(
        ProvisionTrigger trigger,
        ProvisionContext context,
        CancellationToken cancellationToken)
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
        // database records from the very first request. Resolution may itself re-scope an existing
        // subscription (IGatewayTierSync) — an external side effect this method has to account for.
        var resolution = await quotaResolution.ResolveAsync(user.UserId, period, cancellationToken);
        var tierProductId = resolution.Allocation.TierProductId;

        // Re-activation lifts the hard stop its own deactivation set. Nothing else clears it before the
        // next fork-wide POST /quota/reset, so without this a deactivate-by-mistake would leave the
        // developer showing hard-stopped for the rest of the month (#156 review).
        if (trigger == ProvisionTrigger.Reactivate)
        {
            resolution.Allocation.IsHardStopped = false;
        }

        // Step 4: mint the key. Skipped when the user already holds one (an admin re-activating a user
        // whose subscription was never deleted) — ProvisionAsync would 409, and re-minting a working
        // key would break the CLI the developer already has configured.
        ApiKeyRevealResponse? key = null;
        if (string.IsNullOrEmpty(user.ApimSubscriptionId))
        {
            key = await ProvisionKeyAsync(user, tierProductId, cancellationToken);
        }

        // Past the commit point if anything reached the gateway: the mint, or a tier re-scope that
        // resolution triggered for a user who already had a subscription.
        var externalSideEffect = key is not null || resolution.TierSyncRequested;
        var completionToken = CommitToken.For(externalSideEffect, cancellationToken);

        try
        {
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
                    keyProvisioned = key is not null,
                    tierSyncRequested = resolution.TierSyncRequested,
                    directoryEnriched,
                },
                completionToken);

            await dbContext.SaveChangesAsync(completionToken);

            if (transaction is not null)
            {
                await transaction.CommitAsync(completionToken);
            }
        }
        catch (Exception exception) when (externalSideEffect)
        {
            logger.LogError(
                exception,
                "The gateway accepted user {UserId}'s provisioning ({Trigger}, tier {TierProductId}) but the change could not be saved; the subscription is left for the next provision to adopt by name.",
                user.UserId,
                trigger,
                tierProductId);
            throw;
        }

        logger.LogInformation(
            "Provisioned user {UserId} ({Trigger}) on tier {TierProductId} for {Period}; key provisioned: {KeyProvisioned}.",
            user.UserId,
            trigger,
            tierProductId,
            period,
            key is not null);

        return (user, key);
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

            // First login IS a login (#167): stamping it here means a brand-new row's LastLoginDate
            // equals its CreatedDate, and the profile read that provisioned it does not immediately
            // rewrite the row it just created.
            LastLoginDate = timeProvider.GetUtcNow(),
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
            // Two tabs, one first login: EntraObjectId is unique, so the loser lands here. Detach, then
            // ask the database whose row won — provider-agnostic (no SQL Server/SQLite error-number
            // sniffing) and precise: only a save that lost *this* race becomes a conflict, so any other
            // DbUpdateException (a value too long for its column, say) still surfaces as itself (#154).
            dbContext.Entry(user).State = EntityState.Detached;

            if (!await dbContext.Users.AsNoTracking().AnyAsync(u => u.EntraObjectId == entraObjectId, cancellationToken))
            {
                throw;
            }

            // The inner DbUpdateException is load-bearing: it is how UserService.ProvisionFirstLoginAsync
            // tells this race apart from the "provisioning is not repeatable" conflict above, which it
            // must NOT absorb. Still a ConflictException so a caller that reaches this path some other
            // way (the Cli, a future orchestrator) keeps its 409.
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
        catch (Exception exception) when (IsUpstreamFailure(exception))
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
    /// act on, a caller-caused refusal keeps its own status, and an ARM/transport failure becomes a 502.
    /// All three roll the transaction back, so a failed first login leaves no <c>User</c> row.
    /// </summary>
    private async Task<ApiKeyRevealResponse> ProvisionKeyAsync(User user, string tierProductId, CancellationToken cancellationToken)
    {
        try
        {
            return await keys.ProvisionAsync(user, tierProductId, cancellationToken);
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
    /// The exception types that mean "the dependency behind a configured feature failed" — an
    /// <b>allowlist</b>, not a denylist (#156 review): a denylist reported our own
    /// <see cref="InvalidOperationException"/>s ("the user must be saved…", "APIM returned an empty
    /// primary key") to the caller as the gateway's fault. Anything not listed keeps its own mapping,
    /// which for a genuine bug is the 500 it deserves.
    /// </summary>
    private static bool IsUpstreamFailure(Exception exception) =>
        exception is RequestFailedException           // every ARM/Graph SDK failure
            or ApimSubscriptionNotFoundException      // the APIM client's own not-found translation
            or HttpRequestException                   // transport: DNS, TLS, connection reset
            or TimeoutException;

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
