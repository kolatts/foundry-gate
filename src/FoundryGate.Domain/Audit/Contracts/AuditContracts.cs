namespace FoundryGate.Domain.Audit.Contracts;

/// <summary>One audit log entry (spec &#167;3.1 <c>AuditLog</c>). GET /audit (paged, filterable).</summary>
/// <param name="AuditLogId">Surrogate int PK.</param>
/// <param name="ActorUserId">The admin (or system job) that performed the action. Null for system-initiated actions with no actor, e.g. an unattended background job.</param>
/// <param name="ActorDisplayName">Denormalized for display; null when <paramref name="ActorUserId"/> is null.</param>
/// <param name="Action">One of <see cref="Constants.AuditActions"/>, or a future action not yet constant-ized — the column is free text by design.</param>
/// <param name="TargetType">e.g. "User" | "Group" | "Request" (spec &#167;3.1). Null when the action has no single target.</param>
/// <param name="TargetId">Identifier of the affected record, as a string (spec &#167;3.1 keeps this loosely typed since <paramref name="TargetType"/> varies). Null when the action has no single target.</param>
/// <param name="Details">Opaque JSON blob (spec &#167;3.1); the API does not interpret it, only the UI's audit viewer renders it.</param>
/// <param name="OccurredDate">When the action happened.</param>
public record AuditLogEntryResponse(
    int AuditLogId,
    int? ActorUserId,
    string? ActorDisplayName,
    string Action,
    string? TargetType,
    string? TargetId,
    string? Details,
    DateTimeOffset OccurredDate);

/// <summary>Filter parameters for GET /audit. Bind alongside <see cref="Common.PagedRequest"/> via a separate <c>[FromQuery]</c> parameter.</summary>
public record AuditLogQuery(
    int? ActorUserId,
    string? Action,
    string? TargetType,
    string? TargetId,
    DateTimeOffset? FromDate,
    DateTimeOffset? ToDate);
