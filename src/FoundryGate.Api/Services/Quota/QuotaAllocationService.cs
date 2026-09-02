using System.Linq.Expressions;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Data;
using FoundryGate.Data.Entities;
using FoundryGate.Data.Extensions;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Quota.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Api.Services.Quota;

/// <summary>
/// Default <see cref="IQuotaAllocationService"/>: projection-to-record reads over
/// <see cref="AppDbContext.QuotaAllocations"/>, and the two writers (<c>/me</c> auto-create, manual reset)
/// that orchestrate <see cref="IQuotaResolutionService"/> and own the <c>SaveChangesAsync</c>.
/// </summary>
public sealed class QuotaAllocationService(
    AppDbContext dbContext,
    IQuotaResolutionService quotaResolution,
    GatewayTierMapper tierMapper,
    ICurrentUserAccessor currentUser,
    IAuditService audit,
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
    public async Task<PagedResult<QuotaAllocationResponse>> ListCurrentPeriodAsync(PagedRequest paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paging);

        var period = BillingPeriod.Current(timeProvider);

        var page = await ForPeriod(period)
            .OrderBy(a => a.User.DisplayName)
            .ThenBy(a => a.UserId)
            .Select(Projection)
            .ToPagedAsync(paging, cancellationToken);

        return new PagedResult<QuotaAllocationResponse>(
            [.. page.Items.Select(ToResponse)],
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

        var row = await FindRowAsync(user.UserId, period, cancellationToken);
        if (row is not null)
        {
            return ToResponse(row);
        }

        var resolution = await quotaResolution.ResolveAsync(user.UserId, period, cancellationToken);

        // Past the commit point when resolution re-scoped the subscription at the gateway: the row that
        // records the tier must not be abandoned because the client hung up (CONVENTIONS.md; #156 review).
        var completionToken = resolution.TierSyncRequested ? CancellationToken.None : cancellationToken;

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
            row = await FindRowAsync(user.UserId, period, cancellationToken);
            if (row is null)
            {
                throw;
            }

            logger.LogInformation(exception, "Concurrent allocation creation for user {UserId} in {Period}; returning the winning row.", user.UserId, period);
            return ToResponse(row);
        }

        return ToResponse(await FindRowAsync(user.UserId, period, cancellationToken)
            ?? throw new InvalidOperationException($"Allocation for user {user.UserId} in {period} was saved but could not be read back."));
    }

    /// <inheritdoc />
    public async Task<QuotaAllocationResponse?> FindUserAllocationAsync(int userId, CancellationToken cancellationToken)
    {
        var row = await FindRowAsync(userId, BillingPeriod.Current(timeProvider), cancellationToken);
        return row is null ? null : ToResponse(row);
    }

    /// <inheritdoc />
    public async Task<QuotaAllocationResponse> GetUserAllocationAsync(int userId, CancellationToken cancellationToken)
    {
        var period = BillingPeriod.Current(timeProvider);

        var row = await FindRowAsync(userId, period, cancellationToken);
        if (row is not null)
        {
            return ToResponse(row);
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
        var now = timeProvider.GetUtcNow();
        var period = BillingPeriod.FromInstant(now);

        var activeUserIds = await dbContext.Users
            .Where(u => u.IsActive)
            .OrderBy(u => u.UserId)
            .Select(u => u.UserId)
            .ToListAsync(cancellationToken);

        var resolutions = await quotaResolution.ResolveManyAsync(activeUserIds, period, cancellationToken);
        var touched = resolutions.Select(r => r.Allocation).ToList();

        // Past the commit point once any subscription has been re-scoped at the gateway. A mid-loop ARM
        // failure aborts before this line and saves nothing (the tier sync adds its audit row without
        // saving, #156 review), so the reset is all-or-nothing exactly as this method's docs promise.
        var tierSyncCount = resolutions.Count(r => r.TierSyncRequested);
        var completionToken = tierSyncCount > 0 ? CancellationToken.None : cancellationToken;

        foreach (var allocation in touched)
        {
            Stamp(allocation, now);
        }

        // One row per run, no single target (CONVENTIONS.md: empty target when there is none).
        // Added before the save so it commits atomically with every allocation it describes.
        _ = await audit.LogAsync(
            AuditActions.QuotaAllocationReset,
            string.Empty,
            string.Empty,
            new
            {
                usersResetCount = resolutions.Count,
                periodYear = period.Year,
                periodMonth = period.Month,
                tierSyncCount,
            },
            completionToken);

        try
        {
            await dbContext.SaveChangesAsync(completionToken);
        }
        catch (DbUpdateException exception) when (resolutions.Any(r => r.IsNew))
        {
            // A concurrent reset (or a developer's first /me of the month) inserted some of the rows we
            // were about to Add. Adopt the winners — re-apply our resolution to their rows — and save
            // again; a failed SaveChanges leaves every entry (including the audit row) still pending, so
            // the second save is the same atomic unit. Anything other than a lost race is rethrown.
            var adopted = await AdoptConcurrentlyCreatedRowsAsync(touched, period, now, completionToken);
            if (adopted == 0)
            {
                throw;
            }

            logger.LogInformation(exception, "Quota reset for {Period} raced a concurrent writer on {AdoptedCount} allocation(s); adopted the existing rows.", period, adopted);
            await dbContext.SaveChangesAsync(completionToken);
        }

        logger.LogInformation(
            "Quota reset for {Period}: {UsersResetCount} active users, {TierSyncCount} tier syncs.",
            period,
            resolutions.Count,
            tierSyncCount);

        return new QuotaResetResult(resolutions.Count, period.Year, period.Month, now);
    }

    private static void Stamp(QuotaAllocation allocation, DateTimeOffset now)
    {
        allocation.IsHardStopped = false;
        allocation.ResetDate = now;
    }

    /// <summary>
    /// For every allocation in <paramref name="touched"/> that is still <see cref="EntityState.Added"/>
    /// but whose (user, period) row now exists in the database: detaches ours, copies the resolution
    /// outputs onto the winner (which keeps its <c>TokensUsed</c>, exactly as a re-resolve would), stamps
    /// it, and swaps it into <paramref name="touched"/>. Returns how many rows were adopted.
    /// </summary>
    private async Task<int> AdoptConcurrentlyCreatedRowsAsync(List<QuotaAllocation> touched, BillingPeriod period, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pendingIds = touched
            .Where(a => dbContext.Entry(a).State == EntityState.Added)
            .Select(a => a.UserId)
            .ToList();
        if (pendingIds.Count == 0)
        {
            return 0;
        }

        var winners = await dbContext.QuotaAllocations.AsNoTracking()
            .Where(a => pendingIds.Contains(a.UserId) && a.PeriodYear == period.Year && a.PeriodMonth == period.Month)
            .ToDictionaryAsync(a => a.UserId, cancellationToken);

        var adopted = 0;
        for (var i = 0; i < touched.Count; i++)
        {
            var ours = touched[i];
            if (dbContext.Entry(ours).State != EntityState.Added || !winners.TryGetValue(ours.UserId, out var winner))
            {
                continue;
            }

            dbContext.Entry(ours).State = EntityState.Detached;

            winner.AllocatedTokens = ours.AllocatedTokens;
            winner.ResolvedLevelType = ours.ResolvedLevelType;
            winner.TierProductId = ours.TierProductId;
            winner.IsGatewayCapped = ours.IsGatewayCapped;
            Stamp(winner, now);
            dbContext.QuotaAllocations.Update(winner);

            touched[i] = winner;
            adopted++;
        }

        return adopted;
    }

    private IQueryable<QuotaAllocation> ForPeriod(BillingPeriod period) =>
        dbContext.QuotaAllocations.AsNoTracking()
            .Where(a => a.PeriodYear == period.Year && a.PeriodMonth == period.Month);

    private Task<AllocationRow?> FindRowAsync(int userId, BillingPeriod period, CancellationToken cancellationToken) =>
        ForPeriod(period)
            .Where(a => a.UserId == userId)
            .Select(Projection)
            .SingleOrDefaultAsync(cancellationToken);

    private static QuotaAllocationResponse ToResponse(AllocationRow row) =>
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
            row.ResetDate);

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
