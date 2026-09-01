using System.Globalization;
using FoundryGate.Data;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using Imagile.Framework.Configuration.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Api.Services.Quota;

/// <summary>
/// Default <see cref="IQuotaResolutionService"/>. Scoped: shares the request's <see cref="AppDbContext"/>
/// so the upserted rows commit with the caller's audit row, and caches the parsed system default for
/// the life of the scope so a bulk reset reads <c>SystemConfiguration</c> once.
/// </summary>
public sealed class QuotaResolutionService(
    AppDbContext dbContext,
    GatewayTierMapper tierMapper,
    IGatewayTierSync tierSync,
    ILogger<QuotaResolutionService> logger) : IQuotaResolutionService
{
    private long? _systemDefault;
    private bool _systemDefaultLoaded;

    /// <inheritdoc />
    public async Task<QuotaResolution> ResolveAsync(int userId, BillingPeriod period, CancellationToken cancellationToken)
    {
        // Tracked (not AsNoTracking): the row is handed to IGatewayTierSync and may already be tracked
        // by the request (ICurrentUserAccessor) — EF's identity resolution keeps it one instance.
        var user = await dbContext.Users.SingleOrDefaultAsync(u => u.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException($"User {userId} was not found.");

        var groupPolicies = await dbContext.GroupMembers.AsNoTracking()
            .Where(gm => gm.UserId == userId)
            .Select(gm => new GroupPolicy(gm.Group.IsUnlimited, gm.Group.MonthlyTokenQuota))
            .ToListAsync(cancellationToken);

        var existing = await FindExistingAsync(userId, period, cancellationToken);

        var previousTier = existing?.TierProductId ?? await dbContext.QuotaAllocations.AsNoTracking()
            .Where(a => a.UserId == userId && (a.PeriodYear < period.Year || (a.PeriodYear == period.Year && a.PeriodMonth < period.Month)))
            .OrderByDescending(a => a.PeriodYear)
            .ThenByDescending(a => a.PeriodMonth)
            .Select(a => a.TierProductId)
            .FirstOrDefaultAsync(cancellationToken);

        return await ResolveCoreAsync(user, period, groupPolicies, existing, previousTier, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<QuotaResolution>> ResolveManyAsync(IReadOnlyCollection<int> userIds, BillingPeriod period, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var users = await dbContext.Users
            .Where(u => ids.Contains(u.UserId))
            .ToDictionaryAsync(u => u.UserId, cancellationToken);

        var policiesByUser = (await dbContext.GroupMembers.AsNoTracking()
                .Where(gm => ids.Contains(gm.UserId))
                .Select(gm => new { gm.UserId, gm.Group.IsUnlimited, gm.Group.MonthlyTokenQuota })
                .ToListAsync(cancellationToken))
            .ToLookup(p => p.UserId, p => new GroupPolicy(p.IsUnlimited, p.MonthlyTokenQuota));

        var existingByUser = new Dictionary<int, QuotaAllocation>();
        foreach (var tracked in dbContext.QuotaAllocations.Local.Where(a => a.PeriodYear == period.Year && a.PeriodMonth == period.Month && ids.Contains(a.UserId)))
        {
            existingByUser[tracked.UserId] = tracked;
        }

        foreach (var row in await dbContext.QuotaAllocations
            .Where(a => ids.Contains(a.UserId) && a.PeriodYear == period.Year && a.PeriodMonth == period.Month)
            .ToListAsync(cancellationToken))
        {
            existingByUser.TryAdd(row.UserId, row);
        }

        // "Previous tier" for users with no row this period = the tier on their most recent earlier
        // allocation. Picked in memory: one projected query beats a per-user round-trip, and a
        // GroupBy-with-ordered-First is not something every provider translates.
        var needPrior = ids.Where(id => !existingByUser.ContainsKey(id)).ToList();
        var priorTierByUser = needPrior.Count == 0
            ? new Dictionary<int, string>()
            : (await dbContext.QuotaAllocations.AsNoTracking()
                .Where(a => needPrior.Contains(a.UserId) && (a.PeriodYear < period.Year || (a.PeriodYear == period.Year && a.PeriodMonth < period.Month)))
                .Select(a => new { a.UserId, a.PeriodYear, a.PeriodMonth, a.TierProductId })
                .ToListAsync(cancellationToken))
                .GroupBy(a => a.UserId)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderByDescending(a => a.PeriodYear).ThenByDescending(a => a.PeriodMonth).First().TierProductId);

        var results = new List<QuotaResolution>(ids.Count);
        foreach (var id in ids)
        {
            if (!users.TryGetValue(id, out var user))
            {
                logger.LogWarning("Skipping quota resolution for user {UserId}: no such user (deleted since enumeration?).", id);
                continue;
            }

            existingByUser.TryGetValue(id, out var existing);
            var previousTier = existing?.TierProductId ?? priorTierByUser.GetValueOrDefault(id);

            results.Add(await ResolveCoreAsync(user, period, [.. policiesByUser[id]], existing, previousTier, cancellationToken));
        }

        return results;
    }

    private async Task<QuotaAllocation?> FindExistingAsync(int userId, BillingPeriod period, CancellationToken cancellationToken)
    {
        // Change tracker first: a row Add()ed earlier in this unit of work is not in the database yet,
        // and querying past it would add a second row for the same (user, period) — a unique-index
        // failure at save time. Same precedent as CurrentUserAccessor.TryGetUserAsync.
        var local = dbContext.QuotaAllocations.Local.FirstOrDefault(a =>
            a.UserId == userId && a.PeriodYear == period.Year && a.PeriodMonth == period.Month);
        if (local is not null)
        {
            return local;
        }

        return await dbContext.QuotaAllocations.SingleOrDefaultAsync(
            a => a.UserId == userId && a.PeriodYear == period.Year && a.PeriodMonth == period.Month,
            cancellationToken);
    }

    private async Task<QuotaResolution> ResolveCoreAsync(
        User user,
        BillingPeriod period,
        IReadOnlyList<GroupPolicy> groupPolicies,
        QuotaAllocation? existing,
        string? previousTier,
        CancellationToken cancellationToken)
    {
        var (level, quota) = await ResolveLevelAsync(user, groupPolicies, cancellationToken);
        var tier = tierMapper.Map(quota);

        var isNew = existing is null;
        var allocation = existing ?? new QuotaAllocation
        {
            UserId = user.UserId,
            PeriodYear = period.Year,
            PeriodMonth = period.Month,
            TokensUsed = 0,
            IsHardStopped = false,
        };

        // On an existing row only the resolution outputs change. TokensUsed belongs to reconciliation
        // (#39) and IsHardStopped to offboarding (#7 direction update) — neither is ours to touch.
        allocation.AllocatedTokens = quota;
        allocation.ResolvedLevelType = level;
        allocation.TierProductId = tier.TierProductId;
        allocation.IsGatewayCapped = tier.IsGatewayCapped;

        if (isNew)
        {
            dbContext.QuotaAllocations.Add(allocation);
        }

        var syncRequested = false;
        if (!string.IsNullOrEmpty(user.ApimSubscriptionId)
            && !string.Equals(previousTier, tier.TierProductId, StringComparison.Ordinal))
        {
            await tierSync.SyncAsync(user, tier.TierProductId, cancellationToken);
            syncRequested = true;
        }

        if (tier.IsGatewayCapped)
        {
            logger.LogWarning(
                "User {UserId} resolved to {AllocatedTokens} tokens ({Level}) for {Period}, above every finite tier cap; the gateway will enforce tier {TierProductId}'s cap instead.",
                user.UserId,
                quota,
                level,
                period,
                tier.TierProductId);
        }

        return new QuotaResolution(allocation, isNew, previousTier, syncRequested);
    }

    private async Task<(QuotaLevelType Level, long? Quota)> ResolveLevelAsync(
        User user,
        IReadOnlyList<GroupPolicy> groupPolicies,
        CancellationToken cancellationToken)
    {
        // Levels 1-2: user-level settings win outright, even over a group that would grant unlimited —
        // an admin who pinned a number on a user meant that number.
        if (user.IsUnlimited)
        {
            return (QuotaLevelType.UserUnlimited, null);
        }

        if (user.MonthlyTokenQuota is { } userQuota)
        {
            return (QuotaLevelType.UserOverride, userQuota);
        }

        // Levels 3-4: group-level. Any unlimited group beats every finite group quota.
        if (groupPolicies.Any(p => p.IsUnlimited))
        {
            return (QuotaLevelType.GroupUnlimited, null);
        }

        long? groupMax = null;
        foreach (var policy in groupPolicies)
        {
            if (policy.MonthlyTokenQuota is { } groupQuota && (groupMax is null || groupQuota > groupMax))
            {
                groupMax = groupQuota;
            }
        }

        if (groupMax is { } max)
        {
            return (QuotaLevelType.GroupMax, max);
        }

        // Level 5.
        return (QuotaLevelType.SystemDefault, await GetSystemDefaultAsync(cancellationToken));
    }

    private async Task<long> GetSystemDefaultAsync(CancellationToken cancellationToken)
    {
        if (_systemDefaultLoaded)
        {
            return _systemDefault!.Value;
        }

        var raw = await dbContext.SystemConfigurations.AsNoTracking()
            .Where(c => c.Key == SystemConfigurationKeys.DefaultMonthlyTokenQuota)
            .Select(c => c.Value)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new ConfigurationValidationException(
                $"SystemConfiguration row '{SystemConfigurationKeys.DefaultMonthlyTokenQuota}' is missing; quota resolution cannot fall through to the system default. Run the reference-data seed (`foundrygate db seed-reference`).");

        // Defensive parse: the column is free text edited on the admin /config page. A bad value must
        // surface as a configuration fault, never as a silent 0-token default.
        if (!long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
        {
            throw new ConfigurationValidationException(
                $"SystemConfiguration['{SystemConfigurationKeys.DefaultMonthlyTokenQuota}'] = '{raw}' is not a non-negative integer token count. Fix the value on the admin /config page.");
        }

        _systemDefault = parsed;
        _systemDefaultLoaded = true;
        return parsed;
    }

    /// <summary>The two quota-relevant columns of a group the user belongs to.</summary>
    private sealed record GroupPolicy(bool IsUnlimited, long? MonthlyTokenQuota);
}
