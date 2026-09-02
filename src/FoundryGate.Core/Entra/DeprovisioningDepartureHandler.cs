using System.Globalization;
using Azure;
using FoundryGate.Core.Gateway;
using FoundryGate.Core.Requests;
using FoundryGate.Data;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Concurrency;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Keys;
using FoundryGate.Domain.Quota;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FoundryGate.Core.Entra;

/// <summary>
/// The <see cref="IDepartureHandler"/> for a host that has no <c>IUserLifecycleService</c> to delegate
/// to — the Functions worker running the nightly <c>EntraSyncFunction</c> (#151). Runs plan 21's
/// deprovision Trigger B over the pieces Core owns: the APIM management client (#194),
/// <see cref="IAuditWriter"/> and the shared <see cref="AppDbContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Order, and why.</b> The APIM <c>DELETE</c> is first and outside any transaction of ours, for
/// the same reason <c>UserLifecycleService.DeprovisionAsync</c> does it that way: a delete has no
/// undo, so rolling the database back after it would leave a dead key the database still describes as
/// live. Once the gateway has accepted it, every write runs on <see cref="CancellationToken.None"/>
/// through <see cref="CommitToken"/> (CONVENTIONS.md §External side effects have a commit point) and a
/// failure is logged at Error with the identity needed to reconcile, then rethrown.
/// </para>
/// <para>
/// <b>This is a second implementation of the deprovision pipeline, and that is a known debt.</b> The
/// Api's orchestrator composes <c>IApimKeyService</c> and <c>IQuotaRequestService</c>, neither of
/// which Core can reach, so scheduling the sync meant either duplicating the sequence here or leaving
/// a nightly run that flags departures without stopping their keys — which would be worse than
/// useless, because the admin UI would read "inactive" while the gateway still honoured the key.
/// The pieces that could be shared already are: the pending-request rule is Core's
/// <see cref="IQuotaRequestExpiry.CancelPendingForUserAsync"/>, and every string the trail is made of
/// is <see cref="DepartureAudit"/> — both of which the Api's side also uses. #214 tracks lifting the
/// rest of the pipeline into Core so there is one definition again.
/// </para>
/// <para>
/// <b>The full list of ways this still differs from the Api's version</b>, so the divergence is on the
/// page rather than in somebody's head until #214 closes it. All three are equivalences, not
/// behaviour changes, and <c>DepartureHandlerParityTests</c> pins the observable half of them:
/// </para>
/// <list type="number">
/// <item>
/// <b>No explicit transaction.</b> The Api's orchestrator opens one (or joins the sync's) around steps
/// 2–5; this method does not. Equivalent because those steps end in a single
/// <c>SaveChangesAsync</c>, which is already atomic, and — unlike the Api's path — nothing here shares
/// the unit of work with a caller that might have staged other writes. A step that needed a second save
/// would break that and would have to open one.
/// </item>
/// <item>
/// <b>No <c>.Local</c>-first allocation lookup.</b> The Api checks the change tracker before the
/// database because a provision in the same unit of work can have added this period's allocation
/// unsaved. Nothing on a departure path does, so the query is enough — but it is only enough for that
/// reason.
/// </item>
/// <item>
/// <b>The sequence itself is restated</b> rather than delegated, which is the debt above.
/// </item>
/// </list>
/// </remarks>
public sealed class DeprovisioningDepartureHandler(
    AppDbContext dbContext,
    IApimManagementClient apim,
    IQuotaRequestExpiry quotaRequests,
    IAuditWriter audit,
    TimeProvider timeProvider,
    ILogger<DeprovisioningDepartureHandler> logger) : IDepartureHandler
{
    /// <inheritdoc />
    public async Task HandleAsync(User user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!user.IsActive)
        {
            // Idempotent: someone was deactivated between two runs, which is not a failure.
            logger.LogDebug("Skipping departure deprovision of user {UserId}: already deactivated.", user.UserId);
            return;
        }

        var keyRevoked = await RevokeKeyAsync(user, cancellationToken);

        // Past the commit point once the subscription is gone. A user who never held a key reached
        // nothing external, so their run stays abandonable.
        var completionToken = CommitToken.For(keyRevoked, cancellationToken);

        try
        {
            var now = timeProvider.GetUtcNow();
            var period = BillingPeriod.Current(timeProvider);

            user.IsActive = false;

            // The reconciliation view and the admin UI must agree with the gateway, which now rejects
            // the deleted key outright. Nothing to stop if there is no allocation this month.
            var allocation = await dbContext.QuotaAllocations.SingleOrDefaultAsync(
                a => a.UserId == user.UserId && a.PeriodYear == period.Year && a.PeriodMonth == period.Month,
                completionToken);
            if (allocation is not null)
            {
                allocation.IsHardStopped = true;
            }

            // A Pending request from someone who no longer has access can never be approved into
            // anything useful, and leaving it Pending keeps them on the admin review queue forever.
            var cancelledRequestCount = await quotaRequests.CancelPendingForUserAsync(user.UserId, DepartureAudit.ReviewNote, completionToken);

            _ = audit.AddSystem(
                AuditActions.UserDeactivated,
                AuditTargetTypes.User,
                TargetId(user),
                new
                {
                    trigger = DepartureAudit.Trigger,
                    keyRevoked,
                    allocationHardStopped = allocation is not null,
                    cancelledRequestCount,
                    period = period.ToString(),
                    deactivatedDate = now,
                });

            _ = await dbContext.SaveChangesAsync(completionToken);

            logger.LogInformation(
                "Deprovisioned departed user {UserId}: key revoked {KeyRevoked}, allocation hard-stopped {HardStopped}, {CancelledCount} pending request(s) rejected.",
                user.UserId,
                keyRevoked,
                allocation is not null,
                cancelledRequestCount);
        }
        catch (Exception exception) when (keyRevoked)
        {
            // Plan 21's documented residue: the subscription is gone and the key fields are cleared,
            // but the user is still marked active. Self-healing — the next nightly run repeats the
            // deprovision, and deleting a subscription APIM no longer has is not an error.
            logger.LogError(
                exception,
                "Departed user {UserId}'s APIM subscription was deleted but the deactivation could not be recorded; they are still marked active with no key. The next Entra sync retries — the deprovision is idempotent.",
                user.UserId);
            throw;
        }
    }

    /// <summary>
    /// Deletes the departed user's APIM subscription, clears the key fields and writes the
    /// system-attributed <c>key.revoked</c> row, committing them the moment APIM has accepted the
    /// delete. <see langword="false"/> — and nothing done — when the user held no key.
    /// </summary>
    private async Task<bool> RevokeKeyAsync(User user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(user.ApimSubscriptionId))
        {
            return false;
        }

        var subscriptionName = ApimSubscriptionNames.ForUser(user.UserId);
        var apimSubscriptionId = user.ApimSubscriptionId;

        bool existedInApim;
        try
        {
            existedInApim = await apim.DeleteSubscriptionAsync(subscriptionName, cancellationToken);
        }
        catch (Exception exception) when (IsUpstreamFailure(exception))
        {
            // Same translation the Api's lifecycle service makes, so a caller sees one failure shape
            // whichever host found the departure: nothing was changed for this user, and retrying is
            // the whole remedy.
            throw new UpstreamDependencyException(
                $"The API Management gateway did not accept the deletion of user {user.UserId}'s subscription, so they have not been deprovisioned and nothing was saved. The next run retries.",
                exception);
        }

        // Past the commit point, and this one cannot be undone: the key is dead whatever happens next,
        // so clearing the row and writing the trail run on CancellationToken.None.
        try
        {
            user.ApimSubscriptionId = string.Empty;
            user.ApimSubscriptionKey = string.Empty;
            user.ApimSubscriptionKeyHint = string.Empty;
            user.ApimKeyIssuedDate = null;

            _ = audit.AddSystem(
                AuditActions.KeyRevoked,
                AuditTargetTypes.ApiKey,
                TargetId(user),
                new { apimSubscriptionId, subscriptionName, existedInApim, reason = DepartureAudit.KeyRevocationReason });

            _ = await dbContext.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "APIM deleted subscription {SubscriptionName} for departed user {UserId} but the row still carries the dead key; the next run repeats the revocation (it is idempotent on a missing subscription) to clear it.",
                subscriptionName,
                user.UserId);
            throw;
        }

        return true;
    }

    /// <summary>
    /// The exception types that mean "the gateway failed", as an <b>allowlist</b> — the same one
    /// <c>UserLifecycleService</c> settled on in the #156 review, so our own
    /// <see cref="InvalidOperationException"/>s are never reported as APIM's fault.
    /// </summary>
    private static bool IsUpstreamFailure(Exception exception) =>
        exception is RequestFailedException
            or ApimSubscriptionNotFoundException
            or HttpRequestException
            or TimeoutException;

    private static string TargetId(User user) => user.UserId.ToString(CultureInfo.InvariantCulture);
}
