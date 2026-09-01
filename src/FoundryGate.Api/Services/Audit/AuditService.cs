using System.Text.Json;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Data;
using FoundryGate.Data.Entities;
using FoundryGate.Data.Extensions;
using FoundryGate.Domain.Audit.Contracts;
using FoundryGate.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Api.Services.Audit;

/// <summary>
/// Default <see cref="IAuditService"/>. Writes go into the request-scoped <see cref="AppDbContext"/>
/// and are persisted by the caller's <c>SaveChangesAsync</c> (see the interface remarks for why);
/// <see cref="AuditLog.OccurredDate"/> comes from the injected <see cref="TimeProvider"/>, never
/// <c>DateTimeOffset.UtcNow</c> (CONVENTIONS.md).
/// </summary>
public sealed class AuditService(AppDbContext dbContext, ICurrentUserAccessor currentUser, TimeProvider timeProvider)
    : IAuditService
{
    /// <summary>Web defaults (camelCase, relaxed escaping) so <c>Details</c> reads like the rest of the API's JSON.</summary>
    private static readonly JsonSerializerOptions DetailsSerializerOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<AuditLog> LogAsync(string action, string targetType, string targetId, object? details, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentNullException.ThrowIfNull(targetId);

        var actor = await currentUser.TryGetUserAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException(
                $"Cannot attribute '{action}' to the caller: no FoundryGate user exists for oid {currentUser.EntraObjectId}. " +
                "Callers are provisioned by GET /users/me before they can perform audited actions.");

        return Add(actor.UserId, action, targetType, targetId, details);
    }

    /// <inheritdoc />
    public Task<AuditLog> LogAsync(int? actorUserId, string action, string targetType, string targetId, object? details, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentNullException.ThrowIfNull(targetId);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Add(actorUserId, action, targetType, targetId, details));
    }

    /// <inheritdoc />
    public Task<PagedResult<AuditLogEntryResponse>> QueryAsync(AuditLogQuery filter, PagedRequest paging, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(paging);

        IQueryable<AuditLog> query = dbContext.AuditLogs.AsNoTracking();

        if (filter.ActorUserId is { } actorUserId)
        {
            query = query.Where(a => a.ActorUserId == actorUserId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Action))
        {
            query = query.Where(a => a.Action == filter.Action);
        }

        if (!string.IsNullOrWhiteSpace(filter.TargetType))
        {
            query = query.Where(a => a.TargetType == filter.TargetType);
        }

        if (!string.IsNullOrWhiteSpace(filter.TargetId))
        {
            query = query.Where(a => a.TargetId == filter.TargetId);
        }

        if (filter.FromDate is { } fromDate)
        {
            query = query.Where(a => a.OccurredDate >= fromDate);
        }

        if (filter.ToDate is { } toDate)
        {
            query = query.Where(a => a.OccurredDate <= toDate);
        }

        // Projection-to-record inside the query (CONVENTIONS.md). The entity stores "no target"/"no
        // details" as empty strings (non-nullable string convention); the response contract exposes
        // them as null, so the translation happens here rather than leaking "" to the UI.
        return query
            .OrderByDescending(a => a.OccurredDate)
            .ThenByDescending(a => a.AuditLogId)
            .Select(a => new AuditLogEntryResponse(
                a.AuditLogId,
                a.ActorUserId,
                a.ActorUser != null ? a.ActorUser.DisplayName : null,
                a.Action,
                a.TargetType == string.Empty ? null : a.TargetType,
                a.TargetId == string.Empty ? null : a.TargetId,
                a.Details == string.Empty ? null : a.Details,
                a.OccurredDate))
            .ToPagedAsync(paging, cancellationToken);
    }

    private AuditLog Add(int? actorUserId, string action, string targetType, string targetId, object? details)
    {
        var entry = new AuditLog
        {
            ActorUserId = actorUserId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Details = details is null ? string.Empty : JsonSerializer.Serialize(details, DetailsSerializerOptions),
            OccurredDate = timeProvider.GetUtcNow(),
        };

        dbContext.AuditLogs.Add(entry);
        return entry;
    }
}
