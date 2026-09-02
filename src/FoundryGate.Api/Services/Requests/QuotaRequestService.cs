using System.Globalization;
using System.Linq.Expressions;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Core.Quota;
using FoundryGate.Core.Requests;
using FoundryGate.Data;
using FoundryGate.Data.Concurrency;
using FoundryGate.Data.Entities;
using FoundryGate.Data.Extensions;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Requests;
using FoundryGate.Domain.Requests.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FoundryGate.Api.Services.Requests;

/// <summary>
/// Default <see cref="IQuotaRequestService"/>: projection-to-record reads over
/// <see cref="AppDbContext.QuotaIncreaseRequests"/>, and the three writers (submit, approve, reject)
/// that own their <c>SaveChangesAsync</c> and commit the audit row with the mutation.
/// </summary>
public sealed class QuotaRequestService(
    AppDbContext dbContext,
    IQuotaResolutionService quotaResolution,
    IQuotaRequestExpiry requestExpiry,
    GatewayTierMapper tierMapper,
    ICurrentUserAccessor currentUser,
    IAuditService audit,
    TimeProvider timeProvider) : IQuotaRequestService
{
    /// <summary>
    /// The one query-side projection, so every read path returns an identical shape. The entity
    /// stores "no notes" as an empty string (non-nullable string convention); the response contract
    /// exposes it as <see langword="null"/>, so the translation happens here rather than leaking
    /// <c>""</c> to the UI (same treatment as <c>AuditService</c>'s target/details columns).
    /// </summary>
    private static readonly Expression<Func<QuotaIncreaseRequest, QuotaIncreaseRequestResponse>> Projection = r =>
        new QuotaIncreaseRequestResponse(
            r.QuotaIncreaseRequestId,
            r.QuotaIncreaseRequestUnique,
            r.UserId,
            r.User.UserUnique,
            r.User.DisplayName,
            r.RequestedByUserId,
            r.PeriodYear,
            r.PeriodMonth,
            r.CurrentQuota,
            r.RequestedQuota,
            r.Justification,
            r.StatusType,
            r.ReviewedByUserId,
            r.ReviewedDate,
            r.ReviewNotes == string.Empty ? null : r.ReviewNotes,
            r.CreatedDate);

    /// <inheritdoc />
    public async Task<QuotaIncreaseRequestResponse> SubmitAsync(SubmitQuotaIncreaseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var caller = await currentUser.GetRequiredUserAsync(cancellationToken);

        // Same answer as GET /quota/allocations/me gives a deactivated developer (403, not 404): the
        // caller is known but not entitled — a budget they cannot spend is not worth an admin's review.
        if (!caller.IsActive)
        {
            throw new UnauthorizedAccessException(
                $"User {caller.UserId} is deactivated and cannot request a quota increase. An admin can re-activate the account via POST /users/{caller.UserId}/activate.");
        }

        return await SubmitCoreAsync(caller, caller, request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<QuotaIncreaseRequestResponse> SubmitForUserAsync(int userId, SubmitQuotaIncreaseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Resolve the admin first: an admin with no User row cannot be the audit actor, and refusing
        // before anything is touched keeps the failure a clean 403 (CONVENTIONS.md).
        var actor = await currentUser.GetRequiredUserAsync(cancellationToken);

        var subject = await dbContext.Users.SingleOrDefaultAsync(u => u.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException($"User {userId} was not found.");

        // A third party asking for a deactivated user is a state conflict, not an authorization
        // problem — the admin is perfectly entitled, the target just isn't in a state to hold a budget.
        if (!subject.IsActive)
        {
            throw new ConflictException(
                $"User {userId} is deactivated and cannot hold a quota increase request. Re-activate the account first (POST /users/{userId}/activate).");
        }

        return await SubmitCoreAsync(subject, actor, request, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PagedResult<QuotaIncreaseRequestResponse>> ListAsync(QuotaRequestQuery filter, PagedRequest paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(paging);

        var caller = await currentUser.GetRequiredUserAsync(cancellationToken);

        var query = dbContext.QuotaIncreaseRequests.AsNoTracking();

        if (currentUser.IsAdmin)
        {
            if (filter.UserId is { } adminScopedUserId)
            {
                query = query.Where(r => r.UserId == adminScopedUserId);
            }
        }
        else
        {
            // An explicit ?userId= naming someone else is refused rather than quietly rewritten to
            // "your own": a developer who asked for another person's queue deserves to be told no.
            if (filter.UserId is { } requestedUserId && requestedUserId != caller.UserId)
            {
                throw new UnauthorizedAccessException(
                    $"Only an admin can list another user's quota increase requests. Omit ?userId= (or pass {caller.UserId}) to list your own.");
            }

            query = query.Where(r => r.UserId == caller.UserId);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(r => r.StatusType == status);
        }

        return await query
            .OrderByDescending(r => r.CreatedDate)
            .ThenByDescending(r => r.QuotaIncreaseRequestId)
            .Select(Projection)
            .ToPagedAsync(paging, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<QuotaIncreaseRequestResponse> GetAsync(int quotaIncreaseRequestId, CancellationToken cancellationToken)
    {
        var caller = await currentUser.GetRequiredUserAsync(cancellationToken);

        var response = await FindProjectedAsync(quotaIncreaseRequestId, cancellationToken)
            ?? throw NotFound(quotaIncreaseRequestId);

        // Deliberately the same exception as "no such id": distinguishing them would turn this route
        // into an enumeration oracle for other people's requests.
        return currentUser.IsAdmin || response.UserId == caller.UserId
            ? response
            : throw NotFound(quotaIncreaseRequestId);
    }

    /// <inheritdoc />
    public async Task<QuotaIncreaseRequestResponse> ApproveAsync(int quotaIncreaseRequestId, ReviewQuotaIncreaseRequest review, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(review);

        var (entity, reviewer) = await LoadForReviewAsync(quotaIncreaseRequestId, cancellationToken);

        var period = BillingPeriod.Current(timeProvider);
        if (entity.PeriodYear != period.Year || entity.PeriodMonth != period.Month)
        {
            // The cheapest refusal, and the one that needs no other read. Everything below re-resolves
            // *this* period, so approving a request filed for another one would raise today's budget
            // while the row and the response still reported the month it was filed for — the number in
            // the UI and the month actually affected would disagree (#159). The monthly reset closes
            // these, so an admin normally never sees one; this is the guard for the window between a
            // period ending and the next reset running.
            throw new ConflictException(
                $"Quota increase request {quotaIncreaseRequestId} was filed for {new BillingPeriod(entity.PeriodYear, entity.PeriodMonth)}, which has ended; the current period is {period}. Approving it would raise the budget for a different month than the one the request records. Reject it and ask the developer to submit a new one.");
        }

        var subject = await dbContext.Users.SingleAsync(u => u.UserId == entity.UserId, cancellationToken);
        if (!subject.IsActive)
        {
            throw new ConflictException(
                $"User {subject.UserId} is deactivated; approving would issue a budget to an account that cannot use it. Re-activate them first (POST /users/{subject.UserId}/activate) or reject the request.");
        }

        // Both write-path guards run against the world as it is *now*, not as it was when the request
        // was filed. The tier table is configuration and can change; the subject's own quota is far more
        // volatile than that (PUT /users/{id}/quota, group membership, group quota), and applying a
        // stale RequestedQuota unconditionally would silently *lower* a budget that has since gone up.
        tierMapper.EnsureValidQuota(entity.RequestedQuota, nameof(entity.RequestedQuota));

        var current = await quotaResolution.PreviewAsync(subject.UserId, cancellationToken);
        if (!IsIncrease(current.Quota, entity.RequestedQuota))
        {
            throw new ConflictException(NoLongerAnIncrease(subject.UserId, current.Quota, entity.RequestedQuota));
        }

        var before = new { subject.IsUnlimited, subject.MonthlyTokenQuota };

        // Claim the row before the gateway is touched, inside the transaction, so a concurrent reviewer
        // cannot also decide it (and an ARM failure below rolls the claim back).
        var reviewedDate = timeProvider.GetUtcNow();
        await using var transaction = await BeginTransactionIfNoneAsync(cancellationToken);
        await ClaimAsync(entity, QuotaRequestStatusType.Approved, reviewer, review.ReviewNotes, reviewedDate, cancellationToken);

        if (entity.RequestedQuota is { } approvedQuota)
        {
            subject.IsUnlimited = false;
            subject.MonthlyTokenQuota = approvedQuota;
        }
        else
        {
            subject.IsUnlimited = true;
            subject.MonthlyTokenQuota = null;
        }

        // Immediately live: upserts this period's allocation and (via the resolution service) moves the
        // subscription to the new tier product before anything is committed, so a failed gateway move
        // fails the approval rather than leaving the database claiming a budget nobody enforces.
        var resolution = await quotaResolution.ResolveAsync(subject.UserId, period, cancellationToken);

        // Past the commit point (CONVENTIONS.md "External side effects have a commit point") exactly when
        // resolution actually moved the subscription: from there the audit row and the save must not be
        // abandoned because the client hung up. Every refusal above happened before that call; when
        // nothing reached the gateway the request's own token still applies and the transaction below
        // simply rolls back (#163).
        var completionToken = CommitToken.For(resolution.TierSyncRequested, cancellationToken);

        _ = await audit.LogAsync(
            AuditActions.QuotaIncreaseApproved,
            AuditTargetTypes.QuotaIncreaseRequest,
            entity.QuotaIncreaseRequestId.ToString(CultureInfo.InvariantCulture),
            new
            {
                userId = subject.UserId,
                before,
                after = new { subject.IsUnlimited, subject.MonthlyTokenQuota },
                currentQuotaAtReview = current.Quota,
                resolvedLevelAtReview = current.Level,
                allocatedTokens = resolution.Allocation.AllocatedTokens,
                tierProductId = resolution.Allocation.TierProductId,
                previousTierProductId = resolution.PreviousTierProductId,
                tierSyncRequested = resolution.TierSyncRequested,
            },
            completionToken);

        await dbContext.SaveChangesAsync(completionToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(completionToken);
        }

        // completionToken again: the read-back is what turns the committed decision into the response,
        // and failing it on a cancelled token would report an approval that actually landed as an error.
        return await GetProjectedAsync(entity.QuotaIncreaseRequestId, completionToken);
    }

    /// <inheritdoc />
    public async Task<QuotaIncreaseRequestResponse> RejectAsync(int quotaIncreaseRequestId, ReviewQuotaIncreaseRequest review, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(review);

        var (entity, reviewer) = await LoadForReviewAsync(quotaIncreaseRequestId, cancellationToken);

        // Same row claim as approval — the transaction is what makes the claim and the audit row one
        // unit, so a rejection can never end up decided but untraceable.
        await using var transaction = await BeginTransactionIfNoneAsync(cancellationToken);
        await ClaimAsync(entity, QuotaRequestStatusType.Rejected, reviewer, review.ReviewNotes, timeProvider.GetUtcNow(), cancellationToken);

        _ = await audit.LogAsync(
            AuditActions.QuotaIncreaseRejected,
            AuditTargetTypes.QuotaIncreaseRequest,
            entity.QuotaIncreaseRequestId.ToString(CultureInfo.InvariantCulture),
            new
            {
                userId = entity.UserId,
                currentQuota = entity.CurrentQuota,
                requestedQuota = entity.RequestedQuota,
                reviewNotes = review.ReviewNotes ?? string.Empty,
            },
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return await GetProjectedAsync(entity.QuotaIncreaseRequestId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> CancelPendingForUserAsync(int userId, string note, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(note);

        var pending = await dbContext.QuotaIncreaseRequests
            .Where(r => r.UserId == userId && r.StatusType == QuotaRequestStatusType.Pending)
            .ToListAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        foreach (var request in pending)
        {
            // Rejected, with no ReviewedByUserId: the status enum has no "Cancelled" state and no
            // human decided these — the note says a lifecycle event closed them.
            request.StatusType = QuotaRequestStatusType.Rejected;
            request.ReviewedDate = now;
            request.ReviewNotes = note;
        }

        return pending.Count;
    }

    /// <inheritdoc />
    public async Task<int> ExpireStaleAsync(CancellationToken cancellationToken)
    {
        // The rule is Core's, shared with the monthly reset; the Api's job here is only to give it a
        // period and own the save. Nothing external is touched, so the caller's token applies throughout
        // and a count of 0 leaves the change tracker (and the audit log) untouched.
        var expired = await requestExpiry.ExpireStaleAsync(BillingPeriod.Current(timeProvider), cancellationToken);
        if (expired > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return expired;
    }

    private async Task<QuotaIncreaseRequestResponse> SubmitCoreAsync(
        User subject,
        User actor,
        SubmitQuotaIncreaseRequest request,
        CancellationToken cancellationToken)
    {
        // Cheapest refusal first (no database at all), then the state conflict, then the comparison
        // that needs the subject's resolved quota.
        tierMapper.EnsureValidQuota(request.RequestedQuota, nameof(request.RequestedQuota));

        var period = BillingPeriod.Current(timeProvider);

        // The fast path, and the friendly one: it is what a serial double-submit hits, and it refuses
        // before PreviewAsync's read. It is NOT the guard — two concurrent submissions can both pass it,
        // which is what IX_QuotaIncreaseRequests_PendingPerUserPeriod is for (#147); the insert below
        // translates that index's violation into this same 409.
        if (await dbContext.QuotaIncreaseRequests.AnyAsync(
            r => r.UserId == subject.UserId
                && r.PeriodYear == period.Year
                && r.PeriodMonth == period.Month
                && r.StatusType == QuotaRequestStatusType.Pending,
            cancellationToken))
        {
            throw new ConflictException(AlreadyPending(subject.UserId, period));
        }

        // Live resolution, not the stored allocation row: the row is whatever the last resolution wrote,
        // so a group membership or user override changed since then would make the "before" the reviewing
        // admin sees plain wrong. PreviewAsync writes nothing and calls no gateway — so every refusal
        // below still happens before any side effect (CONVENTIONS.md).
        var currentQuota = (await quotaResolution.PreviewAsync(subject.UserId, cancellationToken)).Quota;

        if (currentQuota is null)
        {
            throw new ArgumentException(
                $"User {subject.UserId} already has an unlimited budget; there is nothing larger to request.",
                nameof(request));
        }

        if (!IsIncrease(currentQuota, request.RequestedQuota))
        {
            throw new ArgumentException(
                $"A quota increase request must ask for more than the current budget of {currentQuota.Value.ToString("N0", CultureInfo.InvariantCulture)} tokens; {request.RequestedQuota!.Value.ToString("N0", CultureInfo.InvariantCulture)} is not an increase. {tierMapper.Describe()}",
                nameof(request.RequestedQuota));
        }

        var entity = new QuotaIncreaseRequest
        {
            UserId = subject.UserId,
            RequestedByUserId = actor.UserId,
            PeriodYear = period.Year,
            PeriodMonth = period.Month,
            CurrentQuota = currentQuota,
            RequestedQuota = request.RequestedQuota,
            Justification = request.Justification,
            StatusType = QuotaRequestStatusType.Pending,
        };
        dbContext.QuotaIncreaseRequests.Add(entity);

        // The audit row's TargetId is the request's identity PK, which does not exist until the insert
        // has run — so this is the one write path here that cannot be a single SaveChangesAsync. The
        // two saves run inside one transaction instead, because CONVENTIONS.md's rule is that a
        // mutation must never exist without its audit row, not that there must be exactly one save.
        // (Keying the row on QuotaIncreaseRequestUnique, which exists before the insert, would collapse
        // this to one save — but approve/reject key on the int PK, so a request's three audit rows would
        // then live in two different id spaces and the audit viewer's targetId filter could not join
        // them. One transaction is the cheaper price.)
        await using var transaction = await BeginTransactionIfNoneAsync(cancellationToken);

        await InsertAsync(subject.UserId, period, cancellationToken);

        _ = await audit.LogAsync(
            AuditActions.QuotaIncreaseSubmitted,
            AuditTargetTypes.QuotaIncreaseRequest,
            entity.QuotaIncreaseRequestId.ToString(CultureInfo.InvariantCulture),
            new
            {
                userId = subject.UserId,
                requestedByUserId = actor.UserId,
                periodYear = period.Year,
                periodMonth = period.Month,
                currentQuota,
                requestedQuota = request.RequestedQuota,
            },
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return await GetProjectedAsync(entity.QuotaIncreaseRequestId, cancellationToken);
    }

    /// <summary>
    /// Identifiers a violation of the pending-per-period index carries, per provider: SQL Server names
    /// the index ("Cannot insert duplicate key row … with unique index
    /// 'IX_QuotaIncreaseRequests_PendingPerUserPeriod'"), SQLite names the columns ("UNIQUE constraint
    /// failed: QuotaIncreaseRequests.UserId, …"). Matching the identifiers rather than re-querying keeps
    /// the 409 honest whichever provider is underneath; the <c>GroupService</c> name/Entra-link indexes
    /// are the precedent. <c>UserId</c> is enough to pick this index out on SQLite — the table's only
    /// other unique index is on <c>QuotaIncreaseRequestUnique</c>.
    /// </summary>
    private static readonly string[] PendingPerUserPeriodIndexMarkers =
        ["IX_QuotaIncreaseRequests_PendingPerUserPeriod", "QuotaIncreaseRequests.UserId"];

    /// <summary>
    /// Inserts the pending request, turning the filtered unique index's violation into the same
    /// <c>409</c> the read-then-write check above produces serially (#147). Two concurrent submissions
    /// from one developer — a double-clicked button, a retrying client — used to leave two pending rows
    /// for one period, showing the same person twice in the reviewer queue and stranding whichever one
    /// nobody approved. Precedent for adopting the database's answer rather than re-querying:
    /// <c>QuotaAllocationService</c>'s "lost the race" path and <c>GroupService.SaveGroupAsync</c>.
    /// </summary>
    private async Task InsertAsync(int userId, BillingPeriod period, CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (Mentions(exception, PendingPerUserPeriodIndexMarkers))
        {
            throw new ConflictException(AlreadyPending(userId, period), exception);
        }
    }

    /// <summary>True when any exception in the chain names one of <paramref name="markers"/>.</summary>
    private static bool Mentions(Exception exception, string[] markers)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (Array.Exists(markers, marker => current.Message.Contains(marker, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    private static string AlreadyPending(int userId, BillingPeriod period) =>
        $"User {userId} already has a pending quota increase request for {period}. It must be approved or rejected before another can be submitted.";

    /// <summary>
    /// A transaction for this unit of work, or <see langword="null"/> when the caller already owns one —
    /// an orchestrator calling in is the stated design here (<see cref="CancelPendingForUserAsync"/>),
    /// and <c>BeginTransactionAsync</c> throws on an ambient transaction. Precedent:
    /// <c>ApimKeyService.ProvisionAsync</c>. Commit only what you began.
    /// </summary>
    private async Task<IDbContextTransaction?> BeginTransactionIfNoneAsync(CancellationToken cancellationToken) =>
        dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

    /// <summary>Loads a request that must still be pending, plus the reviewing admin (the audit actor).</summary>
    /// <remarks>
    /// The status check here is the fast, friendly refusal; it is not the guard. Two reviewers can both
    /// pass it — <see cref="ClaimAsync"/> is what makes exactly one of them win.
    /// </remarks>
    private async Task<(QuotaIncreaseRequest Request, User Reviewer)> LoadForReviewAsync(int quotaIncreaseRequestId, CancellationToken cancellationToken)
    {
        var reviewer = await currentUser.GetRequiredUserAsync(cancellationToken);

        var entity = await dbContext.QuotaIncreaseRequests
            .SingleOrDefaultAsync(r => r.QuotaIncreaseRequestId == quotaIncreaseRequestId, cancellationToken)
            ?? throw NotFound(quotaIncreaseRequestId);

        if (entity.StatusType != QuotaRequestStatusType.Pending)
        {
            throw AlreadyDecided(quotaIncreaseRequestId, entity.StatusType);
        }

        return (entity, reviewer);
    }

    /// <summary>
    /// Moves the row out of <see cref="QuotaRequestStatusType.Pending"/> with a single conditional
    /// <c>UPDATE … WHERE StatusType = Pending</c>: whoever's statement matches the row wins, the other
    /// gets 0 rows and a 409. Read-then-write on the tracked entity would let a simultaneous approve and
    /// reject both proceed, leaving the quota raised, the subscription moved, the row reading
    /// <c>Rejected</c> and two contradictory audit rows. Precedent:
    /// <c>ApimKeyService.ProvisionAsync</c>'s subscription-id claim.
    /// </summary>
    /// <remarks>
    /// <c>ExecuteUpdateAsync</c> runs immediately, outside the change tracker, so the tracked copy is
    /// stale the moment it succeeds: it is detached rather than patched. Mutating it instead would make
    /// the later <c>SaveChangesAsync</c> issue a second, unconditional UPDATE of the same columns —
    /// undoing the whole point of the claim — and leaving a stale copy tracked would let a second review
    /// in the same scope read <c>Pending</c> from memory after the database says otherwise. Reads after
    /// this point go to the database (<see cref="GetProjectedAsync"/> projects fresh).
    /// </remarks>
    private async Task ClaimAsync(
        QuotaIncreaseRequest entity,
        QuotaRequestStatusType status,
        User reviewer,
        string? reviewNotes,
        DateTimeOffset reviewedDate,
        CancellationToken cancellationToken)
    {
        int? reviewerUserId = reviewer.UserId;
        DateTimeOffset? reviewedOn = reviewedDate;
        var notes = reviewNotes ?? string.Empty;

        var claimed = await dbContext.QuotaIncreaseRequests
            .Where(r => r.QuotaIncreaseRequestId == entity.QuotaIncreaseRequestId && r.StatusType == QuotaRequestStatusType.Pending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(r => r.StatusType, status)
                    .SetProperty(r => r.ReviewedByUserId, reviewerUserId)
                    .SetProperty(r => r.ReviewedDate, reviewedOn)
                    .SetProperty(r => r.ReviewNotes, notes),
                cancellationToken);

        if (claimed == 0)
        {
            // Another reviewer got there between LoadForReviewAsync and here. Re-read to say which way
            // it went rather than guessing.
            var decided = await dbContext.QuotaIncreaseRequests.AsNoTracking()
                .Where(r => r.QuotaIncreaseRequestId == entity.QuotaIncreaseRequestId)
                .Select(r => (QuotaRequestStatusType?)r.StatusType)
                .SingleOrDefaultAsync(cancellationToken);

            throw decided is { } decidedStatus
                ? AlreadyDecided(entity.QuotaIncreaseRequestId, decidedStatus)
                : NotFound(entity.QuotaIncreaseRequestId);
        }

        dbContext.Entry(entity).State = EntityState.Detached;
    }

    /// <summary>
    /// Is <paramml name="requested"/> strictly more budget than <paramref name="current"/>? Unlimited
    /// (<see langword="null"/>) beats every finite quota and nothing beats unlimited — so a user who is
    /// already unlimited can never be increased.
    /// </summary>
    private static bool IsIncrease(long? current, long? requested) =>
        current is { } finiteCurrent && (requested is null || requested.Value > finiteCurrent);

    private static string NoLongerAnIncrease(int userId, long? currentQuota, long? requestedQuota)
    {
        var requested = requestedQuota is { } value
            ? $"{value.ToString("N0", CultureInfo.InvariantCulture)} tokens"
            : "unlimited";

        return currentQuota is null
            ? $"User {userId}'s budget is already unlimited, so approving this request for {requested} would lower it. Their quota changed after the request was filed; reject it instead."
            : $"User {userId}'s budget is now {currentQuota.Value.ToString("N0", CultureInfo.InvariantCulture)} tokens, so approving this request for {requested} would not raise it. Their quota changed after the request was filed; reject it instead.";
    }

    private static ConflictException AlreadyDecided(int quotaIncreaseRequestId, QuotaRequestStatusType status) =>
        new($"Quota increase request {quotaIncreaseRequestId} was already {status.ToString().ToLowerInvariant()} and cannot be reviewed again.");

    private Task<QuotaIncreaseRequestResponse?> FindProjectedAsync(int quotaIncreaseRequestId, CancellationToken cancellationToken) =>
        dbContext.QuotaIncreaseRequests.AsNoTracking()
            .Where(r => r.QuotaIncreaseRequestId == quotaIncreaseRequestId)
            .Select(Projection)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<QuotaIncreaseRequestResponse> GetProjectedAsync(int quotaIncreaseRequestId, CancellationToken cancellationToken) =>
        await FindProjectedAsync(quotaIncreaseRequestId, cancellationToken)
            ?? throw new InvalidOperationException($"Quota increase request {quotaIncreaseRequestId} was saved but could not be read back.");

    private static KeyNotFoundException NotFound(int quotaIncreaseRequestId) =>
        new($"Quota increase request {quotaIncreaseRequestId} was not found.");
}
