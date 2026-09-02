using System.Globalization;
using FoundryGate.Data;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using Imagile.Framework.Configuration.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FoundryGate.Core.Quota;

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

        var groupPolicies = (await LoadGroupPoliciesAsync([userId], cancellationToken)).GetValueOrDefault(userId) ?? [];

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

        var policiesByUser = await LoadGroupPoliciesAsync(ids, cancellationToken);

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
        // allocation — one windowed query (ROW_NUMBER per user) returning at most one row per user,
        // never the whole allocation history. Inferring from history at all goes away once #118 records
        // the product a subscription is actually on.
        var needPrior = ids.Where(id => !existingByUser.ContainsKey(id)).ToList();
        var priorTierByUser = needPrior.Count == 0
            ? new Dictionary<int, string>()
            : await dbContext.QuotaAllocations.AsNoTracking()
                .Where(a => needPrior.Contains(a.UserId) && (a.PeriodYear < period.Year || (a.PeriodYear == period.Year && a.PeriodMonth < period.Month)))
                .GroupBy(a => a.UserId)
                .Select(g => g
                    .OrderByDescending(a => a.PeriodYear)
                    .ThenByDescending(a => a.PeriodMonth)
                    .Select(a => new { a.UserId, a.TierProductId })
                    .First())
                .ToDictionaryAsync(x => x.UserId, x => x.TierProductId, cancellationToken);

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

            results.Add(await ResolveCoreAsync(user, period, policiesByUser.GetValueOrDefault(id) ?? [], existing, previousTier, cancellationToken));
        }

        return results;
    }

    /// <summary>
    /// The group-level inputs to levels 3-4 for <paramref name="userIds"/>, read <b>through the change
    /// tracker</b>: the persisted <c>(UserId, GroupId)</c> memberships, overlaid with the ones this unit
    /// of work has <c>Add</c>ed or <c>Remove</c>d but not yet saved, and joined to <see cref="Group"/>
    /// rows loaded <em>tracked</em> so a group whose quota the caller just edited resolves to its new
    /// value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Same rule as <see cref="FindExistingAsync"/> and <c>CurrentUserAccessor.TryGetUserAsync</c>:
    /// pending state first, database second. Without it, "change the group's quota / add a member /
    /// delete the group, then re-resolve, then save once" — the whole of <c>GroupService</c> (#30/#31)
    /// and <c>EntraGroupSyncService</c> (#41) — would resolve against the pre-mutation database and
    /// write allocations for the state the admin just replaced. A projection query cannot see pending
    /// changes at all, which is why the join to <c>Groups</c> is a tracked entity load rather than
    /// <c>Select(gm =&gt; new { gm.Group.IsUnlimited, ... })</c>: EF's identity resolution hands back the
    /// instance the caller mutated.
    /// </para>
    /// <para>
    /// Not representable, and not needed by any caller: a membership added to a <see cref="Group"/> that
    /// is itself unsaved (both ids are still 0). Every write path saves the group before it can have
    /// members.
    /// </para>
    /// </remarks>
    private async Task<Dictionary<int, List<GroupPolicy>>> LoadGroupPoliciesAsync(IReadOnlyCollection<int> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds as List<int> ?? [.. userIds];

        var pairs = new HashSet<(int UserId, int GroupId)>(
            (await dbContext.GroupMembers.AsNoTracking()
                .Where(gm => ids.Contains(gm.UserId))
                .Select(gm => new { gm.UserId, gm.GroupId })
                .ToListAsync(cancellationToken))
            .Select(pair => (pair.UserId, pair.GroupId)));

        var wanted = ids.ToHashSet();
        foreach (var entry in dbContext.ChangeTracker.Entries<GroupMember>())
        {
            var membership = entry.Entity;
            if (!wanted.Contains(membership.UserId))
            {
                continue;
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    _ = pairs.Add((membership.UserId, membership.GroupId));
                    break;
                case EntityState.Deleted:
                    _ = pairs.Remove((membership.UserId, membership.GroupId));
                    break;
                default:
                    break;
            }
        }

        // A group being deleted takes its memberships with it, whether or not the caller removed the
        // GroupMember rows explicitly (the relationship cascades).
        var deletedGroupIds = dbContext.ChangeTracker.Entries<Group>()
            .Where(entry => entry.State == EntityState.Deleted)
            .Select(entry => entry.Entity.GroupId)
            .ToHashSet();
        _ = pairs.RemoveWhere(pair => deletedGroupIds.Contains(pair.GroupId));

        var groupIds = pairs.Select(pair => pair.GroupId).Distinct().ToList();
        var policies = groupIds.Count == 0
            ? []
            : (await dbContext.Groups
                .Where(g => groupIds.Contains(g.GroupId))
                .ToListAsync(cancellationToken))
                .ToDictionary(g => g.GroupId, g => new GroupPolicy(g.IsUnlimited, g.MonthlyTokenQuota));

        var byUser = new Dictionary<int, List<GroupPolicy>>();
        foreach (var (userId, groupId) in pairs)
        {
            if (!policies.TryGetValue(groupId, out var policy))
            {
                continue;
            }

            if (!byUser.TryGetValue(userId, out var list))
            {
                list = [];
                byUser[userId] = list;
            }

            list.Add(policy);
        }

        return byUser;
    }

    /// <inheritdoc />
    public async Task<QuotaPreview> PreviewAsync(int userId, CancellationToken cancellationToken)
    {
        // AsNoTracking throughout, and deliberately none of ResolveCoreAsync's write half: this is the
        // chain's answer only — no allocation row, no tier mapping, no gateway sync.
        var user = await dbContext.Users.AsNoTracking().SingleOrDefaultAsync(u => u.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException($"User {userId} was not found.");

        // Straight projection, not LoadGroupPoliciesAsync: preview is a read with nothing pending in the
        // unit of work, and staying AsNoTracking is the point of it. A caller that ever previews *after*
        // editing a group in the same request would need the tracker-aware loader instead (#163).
        var groupPolicies = await dbContext.GroupMembers.AsNoTracking()
            .Where(gm => gm.UserId == userId)
            .Select(gm => new GroupPolicy(gm.Group.IsUnlimited, gm.Group.MonthlyTokenQuota))
            .ToListAsync(cancellationToken);

        var (level, quota) = await ResolveLevelAsync(user, groupPolicies, cancellationToken);
        return new QuotaPreview(level, quota);
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
                "User {UserId} resolved to {AllocatedTokens} tokens ({Level}) for {Period}, which matches no configured tier cap; the gateway will enforce tier {TierProductId}'s cap instead. Correct the value to a tier.",
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

        // Change tracker first, database second — the same rule as FindExistingAsync and
        // LoadGroupPoliciesAsync, and the reason a caller can edit state and re-resolve against it in one
        // unit of work. A projection query cannot see a pending change at all, so a caller that mutates
        // this row through the change tracker and then re-resolves would otherwise have the OLD default
        // written back onto every default-tier developer while still saving the new one (#193).
        // `PUT /config` no longer relies on it — it claims the row with a conditional UPDATE, so the new
        // value is already in the database inside its transaction by the time this runs — but the read
        // stays, because resolution reading this key differently from how it reads allocations and group
        // policies is what made that bug possible in the first place.
        //
        // OrdinalIgnoreCase, matching ConfigService's own key lookup: SQL Server's default collation is
        // case-insensitive, so a tracked row could carry a casing an Ordinal compare would miss — and
        // missing the tracker hit is the quiet failure, not a loud one.
        var raw = dbContext.SystemConfigurations.Local
                .FirstOrDefault(c => string.Equals(c.Key, SystemConfigurationKeys.DefaultMonthlyTokenQuota, StringComparison.OrdinalIgnoreCase))?.Value
            ?? await dbContext.SystemConfigurations.AsNoTracking()
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
