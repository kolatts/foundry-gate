namespace FoundryGate.Domain.Exceptions;

/// <summary>
/// Thrown when a mutation conflicts with the current state of a resource — a duplicate
/// unique value, a status transition that isn't valid from the resource's current state,
/// an optimistic-concurrency clash. Mapped to <c>409 Conflict</c> by FoundryGate.Api's
/// single <c>GlobalExceptionHandler</c> (CONVENTIONS.md §Configuration &amp; auth: "Exceptions
/// → HTTP via one IExceptionHandler + ProblemDetails, not per-controller try/catch").
/// </summary>
public sealed class ConflictException : Exception
{
    public ConflictException(string message)
        : base(message)
    {
    }

    public ConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
