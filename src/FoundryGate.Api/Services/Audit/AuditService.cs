using FoundryGate.Api.Services.Identity;
using FoundryGate.Data;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Data.Extensions;
using FoundryGate.Domain.Audit.Contracts;
using FoundryGate.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Api.Services.Audit;

/// <summary>
/// Default <see cref="IAuditService"/>: resolves the caller through <see cref="ICurrentUserAccessor"/>
/// and delegates the row to <see cref="IAuditWriter"/>; owns the admin read query.
/// </summary>
public sealed class AuditService(AppDbContext dbContext, IAuditWriter auditWriter, ICurrentUserAccessor currentUser)
    : IAuditService
{
    /// <inheritdoc />
    public async Task<AuditLog> LogAsync(string action, string targetType, string targetId, object? details, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentNullException.ThrowIfNull(targetId);

        // GetRequiredUserAsync throws the same UnauthorizedAccessException (403, "call GET /users/me
        // first") for a missing row, so the two "no User row" paths in the API agree.
        var actor = await currentUser.GetRequiredUserAsync(cancellationToken);

        return auditWriter.Add(actor, action, targetType, targetId, details);
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
}
