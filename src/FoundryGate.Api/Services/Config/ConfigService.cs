using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Data;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Api.Services.Config;

/// <summary>
/// Default <see cref="IConfigService"/>. Scoped: it shares the request's <see cref="AppDbContext"/>
/// with <see cref="IAuditService"/>, so the value change and its <c>config.updated</c> row commit in
/// one <c>SaveChangesAsync</c> — a configuration edit without its audit trail is exactly the kind of
/// change an operator later needs to explain.
/// </summary>
/// <remarks>
/// Concurrency is opt-in per request (#170): a caller that echoes back the <c>updatedDate</c> it read
/// gets a <c>409</c> when someone else has written the row since, and one that omits
/// <c>ExpectedUpdatedDate</c> keeps the original last-write-wins behaviour. The check lives in the
/// request rather than in a <c>rowversion</c> column because <c>SystemConfiguration</c> is reference
/// data whose columns are all <c>[DoNotUpdate]</c> — a real EF concurrency token would make the seeder
/// more delicate for a nine-row table, and the contention here is between two humans with a form open.
/// </remarks>
public sealed class ConfigService(
    AppDbContext dbContext,
    SystemConfigValidator validator,
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

        await audit.LogAsync(
            AuditActions.ConfigUpdated,
            AuditTargetTypes.SystemConfiguration,
            entry.Key,
            new { key = entry.Key, before, after = newValue },
            cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new SystemConfigEntryResponse(
            entry.Key,
            entry.Value,
            entry.UpdatedDate,
            entry.UpdatedByUserId,
            actor.DisplayName);
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
