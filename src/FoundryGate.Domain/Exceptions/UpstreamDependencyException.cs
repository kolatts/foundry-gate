namespace FoundryGate.Domain.Exceptions;

/// <summary>
/// Thrown when a <em>configured</em> external dependency FoundryGate proxies a change to — the APIM
/// management plane, Microsoft Graph, ARM — refused or failed the call. Mapped to
/// <c>502 Bad Gateway</c> by FoundryGate.Api's <c>GlobalExceptionHandler</c>: the request itself was
/// valid and FoundryGate is configured correctly, but the system behind it did not do its part, so
/// the caller can retry and an operator can look upstream.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="FeatureNotConfiguredException"/> (503): that one means the feature was
/// never wired up on this host — the operator fixes configuration and nothing will ever succeed until
/// they do. This one means the wiring is right and the call still failed — usually transient.
/// </para>
/// <para>
/// The message goes on the wire, so it names the dependency and what FoundryGate was trying to do
/// (and, where relevant, that nothing was persisted), never a resource id, connection string or
/// secret. The original failure is always the <see cref="Exception.InnerException"/> so the log keeps
/// the full detail the caller must not see.
/// </para>
/// </remarks>
public sealed class UpstreamDependencyException : Exception
{
    public UpstreamDependencyException(string message)
        : base(message)
    {
    }

    public UpstreamDependencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
