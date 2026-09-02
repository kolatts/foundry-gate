using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// An <see cref="IAuditWriter"/> that fails loudly if anything writes through it — for tests about DI
/// wiring, where a service has to be <em>constructible</em> without a database and calling it would
/// mean the test had drifted into exercising behaviour. Hand-rolled per CONVENTIONS.md (no mocking
/// library).
/// </summary>
public sealed class NeverCalledAuditWriter : IAuditWriter
{
    /// <inheritdoc />
    public AuditLog Add(User actor, string action, string targetType, string targetId, object? details) => throw Unexpected(action);

    /// <inheritdoc />
    public AuditLog Add(int actorUserId, string action, string targetType, string targetId, object? details) => throw Unexpected(action);

    /// <inheritdoc />
    public AuditLog AddSystem(string action, string targetType, string targetId, object? details) => throw Unexpected(action);

    private static InvalidOperationException Unexpected(string action) =>
        new($"The audit writer was not expected to be called (action '{action}').");
}
