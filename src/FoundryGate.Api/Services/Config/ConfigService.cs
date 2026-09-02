using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Core.Quota;
using FoundryGate.Data;
using FoundryGate.Data.Concurrency;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Quota;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Api.Services.Config;

/// <summary>
/// Default <see cref="IConfigService"/>. Scoped: it shares the request's <see cref="AppDbContext"/>
/// with <see cref="IAuditService"/> and <see cref="IQuotaResolutionService"/>, so the value change, the
/// allocations it moves and its <c>config.updated</c> row commit in one <c>SaveChangesAsync</c> — a
/// configuration edit without its audit trail is exactly the kind of change an operator later needs to
/// explain.
/// </summary>
/// <remarks>
/// <para>
/// Concurrency is opt-in per request (#170): a caller that echoes back the <c>updatedDate</c> it read
/// gets a <c>409</c> when someone else has written the row since, and one that omits
/// <c>ExpectedUpdatedDate</c> keeps the original last-write-wins behaviour. The check lives in the
/// request rather than in a <c>rowversion</c> column because <c>SystemConfiguration</c> is reference
/// data whose columns are all <c>[DoNotUpdate]</c> — a real EF concurrency token would make the seeder
/// more delicate for a nine-row table, and the contention here is between two humans with a form open.
/// </para>
/// <para>
/// <b>Commit-point discipline</b> (CONVENTIONS.md "external side effects have a commit point"): editing
/// <c>DefaultMonthlyTokenQuota</c> re-resolves every developer who falls through to it (#193), which
/// can reach <see cref="IGatewayTierSync"/> and move APIM subscriptions between tier products. Every
/// refusal — 404, the 409, the per-key 400, the actor's 403 — therefore happens <em>before</em> that
/// call, and when it actually moved the gateway the audit row and the save run on
/// <see cref="CancellationToken.None"/> via <c>CommitToken.For</c>.
/// </para>
/// </remarks>
public sealed class ConfigService(
    AppDbContext dbContext,
    SystemConfigValidator validator,
    IQuotaResolutionService quotaResolution,
    ICurrentUserAccessor currentUser,
    IAuditService audit,
    TimeProvider timeProvider) : IConfigService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<SystemConfigEntryResponse>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.SystemConfigurations
            .AsNoTracking()
            .OrderBy(c => c.Key)
            .Select(c => new SystemConfigEntryResponse(
                c.Key,
                c.Value,
                c.UpdatedDate,
                c.UpdatedByUserId,
                c.UpdatedByUser != null ? c.UpdatedByUser.DisplayName : null))
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<SystemConfigEntryResponse> UpdateAsync(
        string key,
        UpdateSystemConfigRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(request);

        // Materialize the whole table (five rows on a shipped fork) and match in memory rather than
        // translating the comparison: `Key == key` is case-insensitive under SQL Server's default
        // collation but case-sensitive under the SQLite the tests run on, and an endpoint that 404s
        // on one provider and succeeds on the other is a contract nobody can document. Tracked (no
        // AsNoTracking) — the matched row is mutated below.
        var entries = await dbContext.SystemConfigurations.ToListAsync(cancellationToken);
        var entry = entries.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(
                $"There is no system configuration key '{key}'. GET /api/v1/config lists the keys this fork has.");

        // Before the value is even validated: a caller whose view of the row is stale must go and re-read
        // it, whatever they were trying to write. Compared as instants (DateTimeOffset equality ignores
        // the offset), so a UTC-normalizing client still matches a row stored with a local offset.
        if (request.ExpectedUpdatedDate is { } expected && expected != entry.UpdatedDate)
        {
            throw new ConflictException(await ConcurrentEditMessageAsync(entry, expected, cancellationToken));
        }

        var newValue = validator.Normalize(entry.Key, request.Value);

        // Resolve the actor before mutating anything: "no User row for this caller" is a 403, and it
        // should leave the change tracker as clean as it found it.
        var actor = await currentUser.GetRequiredUserAsync(cancellationToken);

        var before = entry.Value;
        entry.Value = newValue;
        entry.UpdatedByUserId = actor.UserId;

        // Set explicitly rather than leaning on TimestampInterceptor alone: re-saving an unchanged
        // value must still record who touched it and when, and an entity EF sees as unmodified is
        // never handed to the interceptor. The interceptor then stamps the same instant (one
        // TimeProvider), so the two never disagree.
        entry.UpdatedDate = timeProvider.GetUtcNow();

        // Level 5 of the precedence chain just moved for everyone who falls through to it (#193).
        // Resolution reads the edited (still unsaved) row through the change tracker, so this sees the
        // new default and the whole thing commits together — allocations, the config row and the audit
        // row. Before this, an edit here changed nobody until something else happened to touch them, and
        // the first thing to notice was usually the Functions host's monthly reset, which has no APIM
        // client and so could only log the divergence it was creating.
        var reresolvedUserIds = IsDefaultQuotaChange(entry.Key, before, newValue)
            ? await SystemDefaultUserIdsAsync(cancellationToken)
            : [];

        var resolutions = reresolvedUserIds.Count == 0
            ? []
            : await quotaResolution.ResolveManyAsync(reresolvedUserIds, BillingPeriod.Current(timeProvider), cancellationToken);

        var gatewayMoved = resolutions.Any(resolution => resolution.TierSyncRequested);
        var commitToken = CommitToken.For(gatewayMoved, cancellationToken);

        await audit.LogAsync(
            AuditActions.ConfigUpdated,
            AuditTargetTypes.SystemConfiguration,
            entry.Key,
            new
            {
                key = entry.Key,
                before,
                after = newValue,
                // An admin who changed one config value and moved 200 developers between APIM products
                // deserves a trail entry that says so.
                reresolvedUserCount = reresolvedUserIds.Count,
                tierChangeCount = resolutions.Count(resolution => resolution.TierSyncRequested),
            },
            commitToken);

        await dbContext.SaveChangesAsync(commitToken);

        return new SystemConfigEntryResponse(
            entry.Key,
            entry.Value,
            entry.UpdatedDate,
            entry.UpdatedByUserId,
            actor.DisplayName);
    }

    /// <summary>
    /// True only for an edit that actually changes <c>DefaultMonthlyTokenQuota</c>. Re-saving the same
    /// value still stamps the editor (that is the point of the explicit <c>UpdatedDate</c>) but must not
    /// walk the user table, let alone call ARM, for a no-op.
    /// </summary>
    private static bool IsDefaultQuotaChange(string key, string before, string after) =>
        string.Equals(key, SystemConfigurationKeys.DefaultMonthlyTokenQuota, StringComparison.Ordinal)
        && !string.Equals(before, after, StringComparison.Ordinal);

    /// <summary>
    /// The users a change to the system default actually affects: every <b>active</b> user whose
    /// resolution falls through to level 5 — no <c>IsUnlimited</c>, no <c>MonthlyTokenQuota</c>, and no
    /// group membership that would win at levels 3-4 — plus any whose <em>current</em> allocation already
    /// says <see cref="QuotaLevelType.SystemDefault"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two halves are not the same set and both are wanted. The first is who the new default applies
    /// to; the second catches a row the chain has since moved off the default, whose stored allocation is
    /// stale and would otherwise keep claiming a budget the gateway does not enforce — re-resolving it
    /// costs one row and ends the disagreement.
    /// </para>
    /// <para>
    /// Deactivated users are excluded, matching the monthly reset and <c>GroupService</c>: their key is
    /// revoked, so there is no subscription to move and no budget to correct.
    /// </para>
    /// <para>
    /// Deliberately a query rather than <c>IQuotaResolutionService.PreviewAsync</c> per user: the chain
    /// is deterministic, so "which level would this user land on" is expressible as a predicate, and a
    /// preview per user would be one round trip each on the one path that may already have hundreds.
    /// </para>
    /// </remarks>
    private Task<List<int>> SystemDefaultUserIdsAsync(CancellationToken cancellationToken)
    {
        var period = BillingPeriod.Current(timeProvider);

        return dbContext.Users.AsNoTracking()
            .Where(user => user.IsActive
                && ((!user.IsUnlimited
                        && user.MonthlyTokenQuota == null
                        && !dbContext.GroupMembers.Any(member =>
                            member.UserId == user.UserId
                            && (member.Group.IsUnlimited || member.Group.MonthlyTokenQuota != null)))
                    || dbContext.QuotaAllocations.Any(allocation =>
                        allocation.UserId == user.UserId
                        && allocation.PeriodYear == period.Year
                        && allocation.PeriodMonth == period.Month
                        && allocation.ResolvedLevelType == QuotaLevelType.SystemDefault)))
            .OrderBy(user => user.UserId)
            .Select(user => user.UserId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// The 409 body for a lost edit: what the row says now, when it changed, and — when the row has an
    /// editor — who changed it, so the admin can decide whether to re-apply without another round trip.
    /// The display-name lookup is a second query on purpose: it runs only on the conflict path, where a
    /// stale write is being refused anyway, rather than joining <c>UpdatedByUser</c> into every update.
    /// </summary>
    private async Task<string> ConcurrentEditMessageAsync(
        SystemConfiguration entry,
        DateTimeOffset expected,
        CancellationToken cancellationToken)
    {
        var editor = entry.UpdatedByUserId is { } editorUserId
            ? await dbContext.Users.AsNoTracking()
                .Where(u => u.UserId == editorUserId)
                .Select(u => u.DisplayName)
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        var by = string.IsNullOrWhiteSpace(editor) ? string.Empty : $" by {editor}";

        return $"System configuration key '{entry.Key}' was changed{by} at {entry.UpdatedDate:O} — you were editing the version from {expected:O}. " +
            $"Its value is now '{entry.Value}'. Re-read GET /api/v1/config and re-apply your change if you still want it.";
    }
}
