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
        // caller is known but not entitled — and submitting would mint them an allocation and, once
        // #118 lands, a tier sync onto a product.
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

        // Re-guarded at the write path, not just at submission: the tier table is configuration and
        // may have changed since the request was filed (CONVENTIONS.md "Quota values are tiers").
        tierMapper.EnsureValidQuota(entity.RequestedQuota, nameof(entity.RequestedQuota));

        var before = new { subject.IsUnlimited, subject.MonthlyTokenQuota };

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

        Decide(entity, QuotaRequestStatusType.Approved, reviewer, review.ReviewNotes);

        // Immediately live: upserts this period's allocation and (via the resolution service) moves the
        // subscription to the new tier product before anything is committed, so a failed gateway move
        // fails the approval rather than leaving the database claiming a budget nobody enforces.
        var resolution = await quotaResolution.ResolveAsync(subject.UserId, BillingPeriod.Current(timeProvider), cancellationToken);

        _ = await audit.LogAsync(
            AuditActions.QuotaIncreaseApproved,
            AuditTargetTypes.QuotaIncreaseRequest,
            entity.QuotaIncreaseRequestId.ToString(CultureInfo.InvariantCulture),
            new
            {
                userId = subject.UserId,
                before,
                after = new { subject.IsUnlimited, subject.MonthlyTokenQuota },
                allocatedTokens = resolution.Allocation.AllocatedTokens,
                tierProductId = resolution.Allocation.TierProductId,
                previousTierProductId = resolution.PreviousTierProductId,
                tierSyncRequested = resolution.TierSyncRequested,
            },
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return await GetProjectedAsync(entity.QuotaIncreaseRequestId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<QuotaIncreaseRequestResponse> RejectAsync(int quotaIncreaseRequestId, ReviewQuotaIncreaseRequest review, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(review);

        var (entity, reviewer) = await LoadForReviewAsync(quotaIncreaseRequestId, cancellationToken);

        Decide(entity, QuotaRequestStatusType.Rejected, reviewer, review.ReviewNotes);

        _ = await audit.LogAsync(
            AuditActions.QuotaIncreaseRejected,
            AuditTargetTypes.QuotaIncreaseRequest,
            entity.QuotaIncreaseRequestId.ToString(CultureInfo.InvariantCulture),
            new
            {
                userId = entity.UserId,
                currentQuota = entity.CurrentQuota,
                requestedQuota = entity.RequestedQuota,
                reviewNotes = entity.ReviewNotes,
            },
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

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

        var currentQuota = await GetCurrentQuotaAsync(subject, period, cancellationToken);

        if (currentQuota is null)
        {
            throw new ArgumentException(
                $"User {subject.UserId} already has an unlimited budget for {period}; there is nothing larger to request.",
                nameof(request));
        }

        if (request.RequestedQuota is { } requestedQuota && requestedQuota <= currentQuota.Value)
        {
            throw new ArgumentException(
                $"A quota increase request must ask for more than the current budget of {currentQuota.Value.ToString("N0", CultureInfo.InvariantCulture)} tokens; {requestedQuota.ToString("N0", CultureInfo.InvariantCulture)} is not an increase. {tierMapper.Describe()}",
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
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

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
        await transaction.CommitAsync(cancellationToken);

        return await GetProjectedAsync(entity.QuotaIncreaseRequestId, cancellationToken);
    }

    /// <summary>
    /// The subject's resolved quota for <paramref name="period"/> (<see langword="null"/> = unlimited):
    /// the existing allocation if there is one, otherwise a fresh resolution — the same row a first
    /// <c>GET /quota/allocations/me</c> of the month would create, added to this unit of work and
    /// committed with the request.
    /// </summary>
    private async Task<long?> GetCurrentQuotaAsync(User subject, BillingPeriod period, CancellationToken cancellationToken)
    {
        var existing = await dbContext.QuotaAllocations.AsNoTracking()
            .Where(a => a.UserId == subject.UserId && a.PeriodYear == period.Year && a.PeriodMonth == period.Month)
            .Select(a => new ResolvedQuota(a.AllocatedTokens))
            .SingleOrDefaultAsync(cancellationToken);

        if (existing is not null)
        {
            return existing.AllocatedTokens;
        }

        var resolution = await quotaResolution.ResolveAsync(subject.UserId, period, cancellationToken);
        return resolution.Allocation.AllocatedTokens;
    }

    /// <summary>Loads a request that must still be pending, plus the reviewing admin (the audit actor).</summary>
    private async Task<(QuotaIncreaseRequest Request, User Reviewer)> LoadForReviewAsync(int quotaIncreaseRequestId, CancellationToken cancellationToken)
    {
        var reviewer = await currentUser.GetRequiredUserAsync(cancellationToken);

        var entity = await dbContext.QuotaIncreaseRequests
            .SingleOrDefaultAsync(r => r.QuotaIncreaseRequestId == quotaIncreaseRequestId, cancellationToken)
            ?? throw NotFound(quotaIncreaseRequestId);

        if (entity.StatusType != QuotaRequestStatusType.Pending)
        {
            throw new ConflictException(
                $"Quota increase request {quotaIncreaseRequestId} was already {entity.StatusType.ToString().ToLowerInvariant()} and cannot be reviewed again.");
        }

        return (entity, reviewer);
    }

    private void Decide(QuotaIncreaseRequest entity, QuotaRequestStatusType status, User reviewer, string? reviewNotes)
    {
        entity.StatusType = status;
        entity.ReviewedByUserId = reviewer.UserId;
        entity.ReviewedDate = timeProvider.GetUtcNow();
        entity.ReviewNotes = reviewNotes ?? string.Empty;
    }

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

    /// <summary>Query-side shape for <see cref="GetCurrentQuotaAsync"/> — a nullable quota that must be distinguishable from "no row".</summary>
    private sealed record ResolvedQuota(long? AllocatedTokens);
}
