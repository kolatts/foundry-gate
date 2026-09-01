namespace FoundryGate.Domain.Common;

/// <summary>
/// Deployment environment (CONVENTIONS.md §Configuration &amp; auth: "Environments: lowercase
/// local / qa / prod, parsed to an enum, DI singleton"). FoundryGate.Api parses
/// <c>IHostEnvironment.EnvironmentName</c> into <see cref="Types"/> at startup and registers
/// it as a singleton so services branch on this instead of ASP.NET Core's own
/// Development/Staging/Production convention, which this project doesn't use
/// (<c>ASPNETCORE_ENVIRONMENT</c> is set to <c>local</c>/<c>qa</c>/<c>prod</c> directly).
/// </summary>
public static class AppEnvironment
{
    /// <summary>Lowercase to match <c>ASPNETCORE_ENVIRONMENT</c> exactly (case-insensitive parse).</summary>
#pragma warning disable CA1707 // lowercase members are the deliberate convention here, not an oversight
    public enum Types
    {
        local,
        qa,
        prod,
    }
#pragma warning restore CA1707
}
