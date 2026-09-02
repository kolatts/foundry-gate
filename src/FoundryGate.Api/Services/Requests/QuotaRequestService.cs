using System.Globalization;
using System.Linq.Expressions;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Api.Services.Quota;
using FoundryGate.Data;
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
        var resolution = await quotaResolution.ResolveAsync(subject.UserId, BillingPeriod.Current(timeProvider), cancellationToken);

        // Past the commit point (CONVENTIONS.md "External side effects have a commit point"): the gateway
        // may already have moved the subscription, so the audit row and the save must not be abandoned
        // because the client hung up. Every refusal above happened before that call.
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
            CancellationToken.None);

        await dbContext.SaveChangesAsync(CancellationToken.None);
        if (transaction is not null)
        {
            await transaction.CommitAsync(CancellationToken.None);
        }

        return await GetProjectedAsync(entity.QuotaIncreaseRequestId, cancellationToken);
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

        if (await dbContext.QuotaIncreaseRequests.AnyAsync(
            r => r.UserId == subject.UserId
                && r.PeriodYear == period.Year
                && r.PeriodMonth == period.Month
                && r.StatusType == QuotaRequestStatusType.Pending,
            cancellationToken))
        {
            throw new ConflictException(
                $"User {subject.UserId} already has a pending quota increase request for {period}. It must be approved or rejected before another can be submitted.");
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

        await dbContext.SaveChangesAsync(cancellationToken);

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
