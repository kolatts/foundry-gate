namespace FoundryGate.Domain.Exceptions;

/// <summary>
/// Thrown when a request needs an optional, externally-addressed feature (Foundry deployment
/// management, APIM key management, …) whose configuration is absent or points at a resource
/// that does not exist. Mapped to <c>503 Service Unavailable</c> ("feature not configured") by
/// FoundryGate.Api's <c>GlobalExceptionHandler</c>: it is a <em>server</em> problem, not the
/// caller's — never a 404 (the resource the caller asked about may well exist) and not a bare
/// 500 (the operator can fix it from the message alone). The message is written to go on the wire,
/// so it names configuration keys and resource names, never resource-group names, ids or secrets.
/// </summary>
public sealed class FeatureNotConfiguredException : Exception
{
    public FeatureNotConfiguredException(string message)
        : base(message)
    {
    }

    public FeatureNotConfiguredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
