using FoundryGate.Api.Services.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Audit.Contracts;
using FoundryGate.Domain.Common;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// An <see cref="IAuditService"/> that forwards to the real one except for the actions
/// <see cref="FailOn"/> selects, which throw instead. Hand-rolled per CONVENTIONS.md (no mocking
/// library), and deliberately selective: a service that failed on <em>every</em> action would break the
/// setup calls a test makes before it gets to the behaviour under test.
/// </summary>
/// <remarks>
/// Exists for one class of assertion: <b>a mutation whose audit row cannot be written must not be
/// committed</b> (CONVENTIONS.md: "a mutation without its audit row is worse than a failed request").
/// The only way to prove that is to make the audit write fail at exactly the moment the mutation is
/// pending and check the database afterwards.
/// </remarks>
public sealed class FailingAuditService(IAuditService inner) : IAuditService
{
    /// <summary>Actions to fail; everything else is forwarded. Defaults to failing nothing.</summary>
    public Func<string, bool> FailOn { get; set; } = _ => false;

    /// <summary>The exception thrown for a matching action.</summary>
    public Exception Failure { get; set; } = new InvalidOperationException("The audit row could not be written.");

    /// <inheritdoc />
    public Task<AuditLog> LogAsync(string action, string targetType, string targetId, object? details, CancellationToken cancellationToken) =>
        FailOn(action)
            ? throw Failure
            : inner.LogAsync(action, targetType, targetId, details, cancellationToken);

    /// <inheritdoc />
    public Task<PagedResult<AuditLogEntryResponse>> QueryAsync(AuditLogQuery filter, PagedRequest paging, CancellationToken cancellationToken) =>
        inner.QueryAsync(filter, paging, cancellationToken);
}
