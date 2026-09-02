using System.Linq.Expressions;
using FoundryGate.Api.Services.Cost;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Core.Quota;
using FoundryGate.Data;
using FoundryGate.Data.Concurrency;
using FoundryGate.Data.Entities;
using FoundryGate.Data.Extensions;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Quota.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Api.Services.Quota;

/// <summary>
/// Default <see cref="IQuotaAllocationService"/>: projection-to-record reads over
/// <see cref="AppDbContext.QuotaAllocations"/>, the <c>/me</c> auto-create that orchestrates
/// <see cref="IQuotaResolutionService"/> and owns its <c>SaveChangesAsync</c>, and the manual reset,
/// which resolves the acting admin and hands the work to Core's <see cref="IQuotaResetService"/> —
/// the same implementation the scheduled Function runs (#38/#119).
/// </summary>
public sealed class QuotaAllocationService(
    AppDbContext dbContext,
    IQuotaResolutionService quotaResolution,
    IQuotaResetService quotaReset,
    GatewayTierMapper tierMapper,
    ICostEstimator costEstimator,
    ICurrentUserAccessor currentUser,
    TimeProvider timeProvider,
    ILogger<QuotaAllocationService> logger) : IQuotaAllocationService
{
    /// <summary>
    /// The one query-side projection, so every read path returns identical shapes. Projects to an
    /// intermediate record rather than straight to <see cref="QuotaAllocationResponse"/> because
    /// <c>PercentUsed</c> has a zero-quota branch better expressed in C# than in translated SQL.
    /// </summary>
    private static readonly Expression<Func<QuotaAllocation, AllocationRow>> Projection = a => new AllocationRow(
        a.QuotaAllocationId,
        a.UserId,
        a.User.UserUnique,
        a.User.DisplayName,
        a.User.Email,
        a.PeriodYear,
        a.PeriodMonth,
        a.AllocatedTokens,
        a.TokensUsed,
        a.IsHardStopped,
        a.ResolvedLevelType,
        a.TierProductId,
        a.IsGatewayCapped,
        a.ResetDate);

    /// <inheritdoc />
    public IReadOnlyList<QuotaTierResponse> ListTiers() =>
        [.. tierMapper.Tiers.Select(t => new QuotaTierResponse(
            t.ProductId,
            string.IsNullOrWhiteSpace(t.DisplayName) ? t.ProductId : t.DisplayName,
            t.IsUnlimited ? null : t.MonthlyTokenQuota,
            t.IsUnlimited))];

    /// <inheritdoc />
    public async Task<PagedResult<QuotaAllocationResponse>> ListCurrentPeriodAsync(
        QuotaAllocationQuery filter,
        PagedRequest paging,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(paging);

        var period = BillingPeriod.Current(timeProvider);

        var page = await Filtered(ForPeriod(period), filter)
            .OrderBy(a => a.User.DisplayName)
            .ThenBy(a => a.UserId)
            .Select(Projection)
            .ToPagedAsync(paging, cancellationToken);

        // One rate card for the whole page — it is a cached configuration read, not a per-row one.
        var rateCard = await costEstimator.GetRateCardAsync(fresh: false, cancellationToken);

        return new PagedResult<QuotaAllocationResponse>(
            [.. page.Items.Select(row => ToResponse(row, rateCard))],
            page.TotalCount,
            page.Page,
            page.PageSize);
    }

    /// <inheritdoc />
    public async Task<QuotaAllocationResponse> GetMyAllocationAsync(CancellationToken cancellationToken)
    {
        var user = await currentUser.GetRequiredUserAsync(cancellationToken);

        // Same class of answer as "no User row" (403, not 404): the caller is known but not entitled.
        // A deactivated developer with a still-valid token must not mint an allocation — nor, once
        // #118 lands, a tier sync onto a product.
        if (!user.IsActive)
        {
            throw new UnauthorizedAccessException(
                $"User {user.UserId} is deactivated and has no quota allocation. An admin can re-activate the account via POST /users/{user.UserId}/activate.");
        }

        var period = BillingPeriod.Current(timeProvider);
        var rateCard = await costEstimator.GetRateCardAsync(fresh: false, cancellationToken);

        var row = await FindRowAsync(user.UserId, period, cancellationToken);
        if (row is not null)
        {
            return ToResponse(row, rateCard);
        }

        var resolution = await quotaResolution.ResolveAsync(user.UserId, period, cancellationToken);

        // Past the commit point when resolution re-scoped the subscription at the gateway: the row that
        // records the tier must not be abandoned because the client hung up (CONVENTIONS.md; #156 review).
        var completionToken = CommitToken.For(resolution.TierSyncRequested, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(completionToken);
        }
        catch (DbUpdateException exception) when (resolution.IsNew)
        {
            // Two first-of-the-month requests from the same developer raced on the unique
            // (UserId, PeriodYear, PeriodMonth) index. The other one won; drop our Added entity and
            // read theirs. Anything else (still no row) is a genuine failure and is rethrown.
            dbContext.Entry(resolution.Allocation).State = EntityState.Detached;
            row = await FindRowAsync(user.UserId, period, completionToken);
            if (row is null)
            {
                throw;
            }

            logger.LogInformation(exception, "Concurrent allocation creation for user {UserId} in {Period}; returning the winning row.", user.UserId, period);
            return ToResponse(row, rateCard);
        }

        // completionToken, not the request's: the read-back is what turns the committed row into the
        // response, and failing it on a cancelled token would report a save that actually succeeded as
        // an error (#163).
        return ToResponse(
            await FindRowAsync(user.UserId, period, completionToken)
            ?? throw new InvalidOperationException($"Allocation for user {user.UserId} in {period} was saved but could not be read back."),
            rateCard);
    }

    /// <inheritdoc />
    public async Task<QuotaAllocationResponse?> FindUserAllocationAsync(int userId, CancellationToken cancellationToken)
    {
        var row = await FindRowAsync(userId, BillingPeriod.Current(timeProvider), cancellationToken);
        return row is null ? null : ToResponse(row, await costEstimator.GetRateCardAsync(fresh: false, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<QuotaAllocationResponse> GetUserAllocationAsync(int userId, CancellationToken cancellationToken)
    {
        var period = BillingPeriod.Current(timeProvider);

        var row = await FindRowAsync(userId, period, cancellationToken);
        if (row is not null)
        {
            return ToResponse(row, await costEstimator.GetRateCardAsync(fresh: false, cancellationToken));
        }

        if (!await dbContext.Users.AnyAsync(u => u.UserId == userId, cancellationToken))
        {
            throw new KeyNotFoundException($"User {userId} was not found.");
        }

        throw new KeyNotFoundException(
            $"User {userId} has no quota allocation for {period} yet. It is created on their first GET /quota/allocations/me of the month, or for every active user by POST /quota/reset.");
    }

    /// <inheritdoc />
    public async Task<QuotaResetResult> ResetAsync(CancellationToken cancellationToken)
    {
        // Resolve the actor before the first gateway call, so a caller without a User row is a 403 that
        // never moved an APIM subscription (CONVENTIONS.md: every refusal before the external call). The
        // reset itself lives in Core, shared with the scheduled Function (#38/#119) — the only difference
        // between a button and a timer is who the audit row names.
        var actor = await currentUser.GetRequiredUserAsync(cancellationToken);

        var outcome = await quotaReset.ResetAsync(QuotaResetTrigger.Admin(actor.UserId), cancellationToken);

        return new QuotaResetResult(
            outcome.UsersResetCount,
            outcome.Period.Year,
            outcome.Period.Month,
            outcome.ResetDate,
            outcome.ExpiredRequestCount);
    }

    /// <summary>
    /// Applies <paramref name="filter"/> to a period's allocations (#208). Each clause is skipped
    /// when its value is null, so an empty query string is the unfiltered list.
    /// </summary>
    /// <remarks>
    /// The over-budget predicate is written the same way in both directions and matches
    /// <c>DashboardService</c>'s count exactly, including the <c>&gt;=</c>: the gateway's
    /// <c>token-quota</c> policy refuses the request that would cross the cap, so "reached it" is
    /// already "cut off", and a card that counts one rule while the list it links to applies another
    /// is worse than no link at all.
    /// </remarks>
    private IQueryable<QuotaAllocation> Filtered(IQueryable<QuotaAllocation> allocations, QuotaAllocationQuery filter)
    {
        if (filter.IsHardStopped is { } hardStopped)
        {
            allocations = allocations.Where(a => a.IsHardStopped == hardStopped);
        }

        if (filter.IsOverBudget is { } overBudget)
        {
            allocations = overBudget
                ? allocations.Where(a => a.AllocatedTokens != null && a.TokensUsed >= a.AllocatedTokens.Value)
                : allocations.Where(a => a.AllocatedTokens == null || a.TokensUsed < a.AllocatedTokens.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.Tier))
        {
            // Canonicalized against the configured tiers so ?tier=Standard finds the rows stored as
            // "standard" on every provider, rather than depending on the database's collation for a
            // value that is an identifier rather than prose. An id no tier claims is matched as
            // typed: a legacy product id is exactly what someone would be looking for.
            var tier = filter.Tier.Trim();
            var canonical = tierMapper.Tiers
                .FirstOrDefault(t => string.Equals(t.ProductId, tier, StringComparison.OrdinalIgnoreCase))?.ProductId
                ?? tier;

            allocations = allocations.Where(a => a.TierProductId == canonical);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // Translates to LIKE '%term%'; case sensitivity follows the database collation (SQL
            // Server's default is case-insensitive), which is what an admin typing a name expects.
            // Same rule as GET /users?search=, so one search box behaves one way across the portal.
            var search = filter.Search.Trim();
            allocations = allocations.Where(a => a.User.DisplayName.Contains(search) || a.User.Email.Contains(search));
        }

        if (filter.IsActive is { } isActive)
        {
            allocations = allocations.Where(a => a.User.IsActive == isActive);
        }

        return allocations;
    }

    private IQueryable<QuotaAllocation> ForPeriod(BillingPeriod period) =>
        dbContext.QuotaAllocations.AsNoTracking()
            .Where(a => a.PeriodYear == period.Year && a.PeriodMonth == period.Month);

    private Task<AllocationRow?> FindRowAsync(int userId, BillingPeriod period, CancellationToken cancellationToken) =>
        ForPeriod(period)
            .Where(a => a.UserId == userId)
            .Select(Projection)
            .SingleOrDefaultAsync(cancellationToken);

    private static QuotaAllocationResponse ToResponse(AllocationRow row, RateCard rateCard) =>
        new(
            row.QuotaAllocationId,
            row.UserId,
            row.UserUnique,
            row.UserDisplayName,
            row.UserEmail,
            row.PeriodYear,
            row.PeriodMonth,
            IsUnlimited: row.AllocatedTokens is null,
            row.AllocatedTokens,
            row.TokensUsed,
            PercentUsed: PercentUsed(row.AllocatedTokens, row.TokensUsed),
            row.IsHardStopped,
            row.ResolvedLevelType,
            row.TierProductId,
            row.IsGatewayCapped,
            row.ResetDate,
            EstimatedCost: rateCard.Estimate(row.TokensUsed));

    /// <summary>Null when unlimited; a zero quota reads as 100% the moment anything is used (never a division by zero).</summary>
    private static double? PercentUsed(long? allocated, long used) => allocated switch
    {
        null => null,
        <= 0 => used > 0 ? 100d : 0d,
        _ => used * 100d / allocated.Value,
    };

    /// <summary>Query-side shape; see <see cref="Projection"/>.</summary>
    private sealed record AllocationRow(
        int QuotaAllocationId,
        int UserId,
        Guid UserUnique,
        string UserDisplayName,
        string UserEmail,
        int PeriodYear,
        int PeriodMonth,
        long? AllocatedTokens,
        long TokensUsed,
        bool IsHardStopped,
        QuotaLevelType ResolvedLevelType,
        string TierProductId,
        bool IsGatewayCapped,
        DateTimeOffset? ResetDate);
}
