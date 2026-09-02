using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// An <see cref="IAuditWriter"/> that forwards to the real one except for the actions
/// <see cref="FailOn"/> selects, which throw instead — the <see cref="FailingAuditService"/> of the
/// Data-layer writer, for services that hold the writer directly rather than the Api's wrapper (Core's
/// two Entra sync services since #151, and every scheduled job).
/// </summary>
/// <remarks>
/// Same single class of assertion as <see cref="FailingAuditService"/>: <b>a mutation whose audit row
/// cannot be written must not be committed</b>, and — where the mutation reached an external system
/// first — that the commit-point recovery path behaves. Deliberately selective, so the setup calls a
/// test makes before the behaviour under test still succeed. Hand-rolled per CONVENTIONS.md (no
/// mocking library).
/// </remarks>
public sealed class FailingAuditWriter(IAuditWriter inner) : IAuditWriter
{
    /// <summary>Actions to fail; everything else is forwarded. Defaults to failing nothing.</summary>
    public Func<string, bool> FailOn { get; set; } = _ => false;

    /// <summary>The exception thrown for a matching action.</summary>
    public Exception Failure { get; set; } = new InvalidOperationException("The audit row could not be written.");

    /// <inheritdoc />
    public AuditLog Add(User actor, string action, string targetType, string targetId, object? details) =>
        FailOn(action) ? throw Failure : inner.Add(actor, action, targetType, targetId, details);

    /// <inheritdoc />
    public AuditLog Add(int actorUserId, string action, string targetType, string targetId, object? details) =>
        FailOn(action) ? throw Failure : inner.Add(actorUserId, action, targetType, targetId, details);

    /// <inheritdoc />
    public AuditLog AddSystem(string action, string targetType, string targetId, object? details) =>
        FailOn(action) ? throw Failure : inner.AddSystem(action, targetType, targetId, details);
}
