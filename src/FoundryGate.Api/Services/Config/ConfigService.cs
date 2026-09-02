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
/// It is a real guard, not a read-then-compare: the write is one conditional
/// <c>UPDATE … WHERE UpdatedDate = @expected</c> (<see cref="ClaimAsync"/>), so two admins who genuinely
/// race cannot both win.
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
        // on one provider and succeeds on the other is a contract nobody can document. AsNoTracking:
        // the row is written by the conditional UPDATE in ClaimAsync, never through the change tracker.
        var entries = await dbContext.SystemConfigurations.AsNoTracking().ToListAsync(cancellationToken);
        var entry = entries.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(
                $"There is no system configuration key '{key}'. GET /api/v1/config lists the keys this fork has.");

        // The friendly refusal, and the one that runs before the value is even validated: a caller whose
        // view of the row is stale must go and re-read it, whatever they were trying to write. It is not
        // the guard — two admins who genuinely race both pass it — which is what ClaimAsync's conditional
        // UPDATE is for; the same relationship as the pending-request pre-check and its unique index (#147).
        if (request.ExpectedUpdatedDate is { } expected && expected != entry.UpdatedDate)
        {
            throw new ConflictException(await ConcurrentEditMessageAsync(entry, expected, cancellationToken));
        }

        var newValue = validator.Normalize(entry.Key, request.Value);

        // Resolve the actor before anything is written: "no User row for this caller" is a 403, and it
        // should leave the database as it found it.
        var actor = await currentUser.GetRequiredUserAsync(cancellationToken);

        var before = entry.Value;

        // Stamped explicitly rather than by TimestampInterceptor: the write is an ExecuteUpdate, which
        // never goes through the change tracker the interceptor hooks. Re-saving an unchanged value must
        // still record who touched it and when — the same reason this was explicit before.
        var updatedDate = timeProvider.GetUtcNow();

        // Claim the row before the gateway can be touched, inside the transaction, so a concurrent admin
        // cannot also write it — and so a failed tier move below rolls the claim back with everything
        // else. Precedent: QuotaRequestService.ApproveAsync.
        await using var transaction = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;

        await ClaimAsync(entry.Key, newValue, actor.UserId, updatedDate, request.ExpectedUpdatedDate, cancellationToken);

        // Level 5 of the precedence chain just moved for everyone who falls through to it (#193). The
        // claim above has already written the new value inside this transaction, so resolution reads it
        // and the whole thing commits together — allocations, the config row and the audit row. Before
        // this, an edit here changed nobody until something else happened to touch them, and the first
        // thing to notice was usually the Functions host's monthly reset — which, before #194, had no APIM
        // client and could only log the divergence it was creating.
        var reresolvedUserIds = IsDefaultQuotaChange(entry.Key, before, newValue)
            ? await SystemDefaultUserIdsAsync(cancellationToken)
            : [];

        var resolutions = reresolvedUserIds.Count == 0
            ? []
            : await quotaResolution.ResolveManyAsync(reresolvedUserIds, BillingPeriod.Current(timeProvider), GatewayTierSyncMode.Immediate, cancellationToken);

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
        if (transaction is not null)
        {
            await transaction.CommitAsync(commitToken);
        }

        // Built from what the claim actually wrote rather than re-read: those are the values the
        // conditional UPDATE committed, and a read-back on a cancelled token would report a change that
        // landed as an error.
        return new SystemConfigEntryResponse(entry.Key, newValue, updatedDate, actor.UserId, actor.DisplayName);
    }

    /// <summary>
    /// Writes the row with a single conditional <c>UPDATE … WHERE [Key] = @key</c> — plus
    /// <c>AND [UpdatedDate] = @expected</c> when the caller supplied one (#170). Whoever's statement
    /// matches the row wins; the other gets 0 rows and a <c>409</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read-then-write on a tracked entity would let two admins who genuinely race both pass the check in
    /// <see cref="UpdateAsync"/> and the second still overwrite the first — the very thing
    /// <c>ExpectedUpdatedDate</c> promises will not happen. Same reasoning and same shape as
    /// <c>QuotaRequestService.ClaimAsync</c>. <c>ExecuteUpdateAsync</c> also keeps this off the change
    /// tracker, so the later <c>SaveChangesAsync</c> — which commits the audit row and any re-resolved
    /// allocations — cannot issue a second, unconditional UPDATE of the same columns.
    /// </para>
    /// <para>
    /// The date is compared as an <em>instant</em> on both providers — SQL Server's
    /// <c>datetimeoffset</c> comparison normalises to UTC, and the SQLite harness stores UTC ticks — so a
    /// client that normalises to UTC still matches a row written with another offset.
    /// </para>
    /// </remarks>
    private async Task ClaimAsync(
        string key,
        string newValue,
        int actorUserId,
        DateTimeOffset updatedDate,
        DateTimeOffset? expectedUpdatedDate,
        CancellationToken cancellationToken)
    {
        var query = dbContext.SystemConfigurations.Where(c => c.Key == key);
        if (expectedUpdatedDate is { } expected)
        {
            query = query.Where(c => c.UpdatedDate == expected);
        }

        int? updatedByUserId = actorUserId;
        var claimed = await query.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(c => c.Value, newValue)
                .SetProperty(c => c.UpdatedByUserId, updatedByUserId)
                .SetProperty(c => c.UpdatedDate, updatedDate),
            cancellationToken);

        if (claimed > 0)
        {
            // ExecuteUpdate runs outside the change tracker, so any instance of this row the request had
            // already loaded is stale the moment it succeeds. Detach it, exactly as
            // QuotaRequestService.ClaimAsync does — and here it is load-bearing, not tidiness: quota
            // resolution reads the system default from the tracker first (pending state before database),
            // and the reference-data seeder leaves these very rows tracked, so a stale copy left here
            // would have the re-resolution below write the OLD default back onto every default-tier
            // developer while still committing the new one.
            foreach (var stale in dbContext.ChangeTracker.Entries<SystemConfiguration>()
                .Where(tracked => string.Equals(tracked.Entity.Key, key, StringComparison.OrdinalIgnoreCase))
                .ToList())
            {
                stale.State = EntityState.Detached;
            }

            return;
        }

        // Nothing matched: another admin wrote the row between our read and here, or — with no expected
        // date, where the filter is the key alone — it was deleted. Re-read to say which.
        var current = await dbContext.SystemConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Key == key, cancellationToken)
            ?? throw new KeyNotFoundException(
                $"System configuration key '{key}' was deleted while this request was in flight, so there was nothing to update.");

        throw new ConflictException(await ConcurrentEditMessageAsync(current, expectedUpdatedDate, cancellationToken));
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
        DateTimeOffset? expected,
        CancellationToken cancellationToken)
    {
        var editor = entry.UpdatedByUserId is { } editorUserId
            ? await dbContext.Users.AsNoTracking()
                .Where(u => u.UserId == editorUserId)
                .Select(u => u.DisplayName)
                .SingleOrDefaultAsync(cancellationToken)
            : null;

        var by = string.IsNullOrWhiteSpace(editor) ? string.Empty : $" by {editor}";

        // The "you were editing" half only makes sense when the caller told us which version they had.
        var wasEditing = expected is { } version ? $" — you were editing the version from {version:O}" : string.Empty;

        return $"System configuration key '{entry.Key}' was changed{by} at {entry.UpdatedDate:O}{wasEditing}. " +
            $"Its value is now '{entry.Value}'. Re-read GET /api/v1/config and re-apply your change if you still want it.";
    }
}
