using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Data;
using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Constants;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Api.Services.Config;

/// <summary>
/// Default <see cref="IConfigService"/>. Scoped: it shares the request's <see cref="AppDbContext"/>
/// with <see cref="IAuditService"/>, so the value change and its <c>config.updated</c> row commit in
/// one <c>SaveChangesAsync</c> — a configuration edit without its audit trail is exactly the kind of
/// change an operator later needs to explain.
/// </summary>
/// <remarks>
/// No concurrency token: two admins editing the same key are last-write-wins today. Deliberately
/// deferred to #170, which adds an optional <c>ExpectedUpdatedDate</c> to the request when the admin
/// config page (#55) exists to send it.
/// </remarks>
public sealed class ConfigService(
    AppDbContext dbContext,
    SystemConfigValidator validator,
    ICurrentUserAccessor currentUser,
    IAuditService audit,
    TimeProvider timeProvider) : IConfigService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<SystemConfigEntryResponse>> ListAsync(CancellationToken cancellationToken)
    {
        // Projected to a row shape first: IsReadOnly comes from a Domain dictionary lookup, which no
        // provider can translate, and the alternative — materializing the key list into the query — is
        // a filter this five-row table does not need.
        var rows = await dbContext.SystemConfigurations
            .AsNoTracking()
            .OrderBy(c => c.Key)
            .Select(c => new
            {
                c.Key,
                c.Value,
                c.UpdatedDate,
                c.UpdatedByUserId,
                UpdatedByDisplayName = c.UpdatedByUser != null ? c.UpdatedByUser.DisplayName : null,
            })
            .ToListAsync(cancellationToken);

        return [.. rows.Select(r => new SystemConfigEntryResponse(
            r.Key,
            r.Value,
            r.UpdatedDate,
            r.UpdatedByUserId,
            r.UpdatedByDisplayName,
            SystemConfigurationKeys.SystemManagedReason(r.Key) is not null))];
    }

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

        // Refused before the value is even looked at: a system-managed key has no admin-settable
        // value, so validating one would be answering the wrong question (#171/#172).
        SystemConfigValidator.EnsureEditable(entry.Key);

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
            actor.DisplayName,
            // Always false here — EnsureEditable above refuses every system-managed key — but read
            // from the same map rather than hard-coded, so the two can never fall out of step.
            SystemConfigurationKeys.SystemManagedReason(entry.Key) is not null);
    }
}
