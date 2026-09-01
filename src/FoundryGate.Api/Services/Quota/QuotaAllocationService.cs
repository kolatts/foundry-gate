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
        var period = BillingPeriod.Current(timeProvider);

        var row = await FindRowAsync(user.UserId, period, cancellationToken);
        if (row is not null)
        {
            return ToResponse(row);
        }

        var resolution = await quotaResolution.ResolveAsync(user.UserId, period, cancellationToken);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
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

        foreach (var resolution in resolutions)
        {
            resolution.Allocation.IsHardStopped = false;
            resolution.Allocation.ResetDate = now;
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
                createdCount = resolutions.Count(r => r.IsNew),
                tierSyncCount = resolutions.Count(r => r.TierSyncRequested),
            },
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Quota reset for {Period}: {UsersResetCount} active users ({CreatedCount} new allocations, {TierSyncCount} tier syncs).",
            period,
            resolutions.Count,
            resolutions.Count(r => r.IsNew),
            resolutions.Count(r => r.TierSyncRequested));

        return new QuotaResetResult(resolutions.Count, period.Year, period.Month, now);
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
