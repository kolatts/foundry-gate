using FoundryGate.Api.Services.Cost;
using FoundryGate.Data;
using FoundryGate.Domain.Dashboard.Contracts;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FoundryGate.Api.Services.Dashboard;

/// <summary>
/// Default <see cref="IDashboardService"/>: eight set-based, <c>AsNoTracking</c> queries over the
/// current <see cref="BillingPeriod"/>, behind a short shared cache.
/// </summary>
/// <remarks>
/// <b>Why a cache.</b> The dashboard is the admin landing page and refreshes itself every 60 s
/// (plans/18), so N admins with the tab open are N × 8 aggregate queries a minute against tables the
/// enforcement path also uses. Holding the answer for <see cref="CacheDuration"/> collapses that to
/// roughly two query bursts a minute for the whole fork. It is a summary of numbers that are
/// themselves reconciled on the sync job's cadence — 30 seconds of staleness is invisible next to
/// that — and <c>?fresh=true</c> is the escape hatch when it is not.
/// <para>
/// The key carries the period, so the first read after a month boundary can never serve last month's
/// figures no matter how recently the entry was written.
/// </para>
/// </remarks>
public sealed class DashboardService(
    AppDbContext dbContext,
    IMemoryCache cache,
    ICostEstimator costEstimator,
    TimeProvider timeProvider) : IDashboardService
{
    /// <summary>Prefix of the <see cref="IMemoryCache"/> key; the current <see cref="BillingPeriod"/> is appended.</summary>
    public const string CacheKeyPrefix = "FoundryGate.Dashboard.Summary";

    /// <summary>How long a computed summary is served to every admin before it is recomputed.</summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    /// <summary>How many consumers the summary lists (spec &#167;4.6's "top consumers").</summary>
    public const int TopConsumerCount = 10;

    /// <summary>The cache key for <paramref name="period"/>.</summary>
    public static string CacheKey(BillingPeriod period) => $"{CacheKeyPrefix}:{period}";

    /// <inheritdoc />
    public async Task<DashboardSummaryResponse> GetSummaryAsync(bool fresh, CancellationToken cancellationToken)
    {
        var period = BillingPeriod.Current(timeProvider);
        var cacheKey = CacheKey(period);

        if (!fresh && cache.TryGetValue(cacheKey, out DashboardSummaryResponse? cached) && cached is not null)
        {
            return cached;
        }

        var summary = await QueryAsync(period, fresh, cancellationToken);
        _ = cache.Set(cacheKey, summary, CacheDuration);
        return summary;
    }

    /// <summary>
    /// The eight queries, run in sequence — one <see cref="AppDbContext"/> permits one active operation
    /// at a time, so "concurrently" is not on the table; each is a single aggregate the indexes
    /// already cover (<c>QuotaAllocation</c> has a <c>(PeriodYear, PeriodMonth)</c> index).
    /// </summary>
    private async Task<DashboardSummaryResponse> QueryAsync(BillingPeriod period, bool fresh, CancellationToken cancellationToken)
    {
        var totalUserCount = await dbContext.Users.AsNoTracking()
            .CountAsync(cancellationToken);

        var activeUserCount = await dbContext.Users.AsNoTracking()
            .CountAsync(u => u.IsActive, cancellationToken);

        // "Unlimited" is a property of an active user: a deactivated account consumes nothing, so
        // counting it here would inflate the number an admin reads as "how many people are uncapped".
        var unlimitedUserCount = await dbContext.Users.AsNoTracking()
            .CountAsync(u => u.IsActive && u.IsUnlimited, cancellationToken);

        var pendingQuotaIncreaseRequestCount = await dbContext.QuotaIncreaseRequests.AsNoTracking()
            .CountAsync(r => r.StatusType == QuotaRequestStatusType.Pending, cancellationToken);

        // Cast to long? so an empty period sums to null rather than depending on the provider to
        // COALESCE it; every user's allocation counts, including deactivated ones, because the
        // tokens they burned before offboarding are still tokens this month spent.
        var totalTokensUsedThisPeriod = await CurrentPeriod(period)
            .Select(a => (long?)a.TokensUsed)
            .SumAsync(cancellationToken) ?? 0L;

        // Both "who is broken right now" counts are scoped to active users (#190): a deactivated
        // account is already off the gateway, so counting it here would bury the handful of people an
        // admin can actually do something for.
        var hardStoppedUserCount = await CurrentPeriod(period)
            .CountAsync(a => a.User.IsActive && a.IsHardStopped, cancellationToken);

        // Reconciled usage against a finite budget. ">=" not ">": the gateway's token-quota policy
        // refuses the request that would cross the cap, so "reached it" is already "cut off".
        var overBudgetUserCount = await CurrentPeriod(period)
            .CountAsync(
                a => a.User.IsActive && a.AllocatedTokens != null && a.TokensUsed >= a.AllocatedTokens.Value,
                cancellationToken);

        var topConsumers = await CurrentPeriod(period)
            .Where(a => a.User.IsActive)
            .OrderByDescending(a => a.TokensUsed)
            .ThenBy(a => a.UserId)
            .Take(TopConsumerCount)
            .Select(a => new ConsumerRow(
                a.UserId,
                a.User.UserUnique,
                a.User.DisplayName,
                a.TokensUsed,
                a.AllocatedTokens))
            .ToListAsync(cancellationToken);

        // The fork's own prices, read once for the whole summary (#177). Null everywhere when no
        // rate card is configured, which is how a fork ships — a truer answer than a zero.
        // `fresh` carried through: the rate card has a cache of its own, and an admin who corrects a
        // price and hits Refresh would otherwise be served the price they came to replace.
        var rateCard = await costEstimator.GetRateCardAsync(fresh, cancellationToken);

        return new DashboardSummaryResponse(
            totalUserCount,
            activeUserCount,
            unlimitedUserCount,
            pendingQuotaIncreaseRequestCount,
            totalTokensUsedThisPeriod,
            [.. topConsumers.Select(c => new TopConsumerResponse(
                c.UserId,
                c.UserUnique,
                c.DisplayName,
                c.TokensUsed,
                c.AllocatedTokens,
                PercentUsed(c.AllocatedTokens, c.TokensUsed),
                rateCard.Estimate(c.TokensUsed)))],
            hardStoppedUserCount,
            overBudgetUserCount,
            rateCard.Estimate(totalTokensUsedThisPeriod));
    }

    private IQueryable<Data.Entities.QuotaAllocation> CurrentPeriod(BillingPeriod period) =>
        dbContext.QuotaAllocations.AsNoTracking()
            .Where(a => a.PeriodYear == period.Year && a.PeriodMonth == period.Month);

    /// <summary>
    /// Null when unlimited; a zero quota reads as 100% the moment anything is used (never a division
    /// by zero). Same rule as <c>QuotaAllocationService</c>, so the dashboard and the quota pages
    /// never show one user two different percentages.
    /// </summary>
    private static double? PercentUsed(long? allocated, long used) => allocated switch
    {
        null => null,
        <= 0 => used > 0 ? 100d : 0d,
        _ => used * 100d / allocated.Value,
    };

    /// <summary>Query-side shape: <c>PercentUsed</c> has a zero-quota branch better expressed in C# than in translated SQL.</summary>
    private sealed record ConsumerRow(
        int UserId,
        Guid UserUnique,
        string DisplayName,
        long TokensUsed,
        long? AllocatedTokens);
}
