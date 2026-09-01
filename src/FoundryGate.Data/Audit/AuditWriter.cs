using System.Text.Json;
using System.Text.Json.Serialization;
using FoundryGate.Data.Entities;

namespace FoundryGate.Data.Audit;

/// <summary>
/// Default <see cref="IAuditWriter"/>. Adds rows to the shared <see cref="AppDbContext"/> (never
/// saves — see the interface remarks); <see cref="AuditLog.OccurredDate"/> comes from the injected
/// <see cref="TimeProvider"/>, never <c>DateTimeOffset.UtcNow</c> (CONVENTIONS.md).
/// </summary>
public sealed class AuditWriter(AppDbContext dbContext, TimeProvider timeProvider) : IAuditWriter
{
    /// <summary>
    /// Web defaults (camelCase property names, so <c>Details</c> reads like the rest of the API's
    /// JSON) plus <see cref="ReferenceHandler.IgnoreCycles"/>: a caller who passes a tracked entity
    /// whose navigations point back at it must get a row with <c>null</c> at the cycle, not a
    /// serializer exception that turns their mutation into an unmapped 500.
    /// </summary>
    private static readonly JsonSerializerOptions DetailsSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    /// <inheritdoc />
    public AuditLog Add(User actor, string action, string targetType, string targetId, object? details)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var entry = Create(action, targetType, targetId, details);

        // Navigation, not FK: an actor added in this same unit of work has UserId == 0 until
        // SaveChanges; EF fixes up ActorUserId from the navigation at save time either way.
        entry.ActorUser = actor;

        dbContext.AuditLogs.Add(entry);
        return entry;
    }

    /// <inheritdoc />
    public AuditLog Add(int actorUserId, string action, string targetType, string targetId, object? details)
    {
        var entry = Create(action, targetType, targetId, details);
        entry.ActorUserId = actorUserId;

        dbContext.AuditLogs.Add(entry);
        return entry;
    }

    /// <inheritdoc />
    public AuditLog AddSystem(string action, string targetType, string targetId, object? details)
    {
        var entry = Create(action, targetType, targetId, details);

        dbContext.AuditLogs.Add(entry);
        return entry;
    }

    private AuditLog Create(string action, string targetType, string targetId, object? details)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentNullException.ThrowIfNull(targetId);

        return new AuditLog
        {
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Details = details is null ? string.Empty : JsonSerializer.Serialize(details, DetailsSerializerOptions),
            OccurredDate = timeProvider.GetUtcNow(),
        };
    }
}
