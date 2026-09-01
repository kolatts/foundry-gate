using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;

namespace FoundryGate.Api.Services;

/// <summary>
/// The single DI entry point for FoundryGate.Api's application services, called exactly once from
/// <c>Program.cs</c>. Each <c>Services/&lt;Area&gt;</c> folder owns its own
/// <c>&lt;Area&gt;ServiceCollectionExtensions.Add&lt;Area&gt;Services()</c> and is invoked from here —
/// so adding a new area is one new file plus one new line below, and <c>Program.cs</c> never becomes
/// a merge hotspot across parallel endpoint work.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registers every <c>Services/&lt;Area&gt;</c> group. Order is not significant.</summary>
    public static IServiceCollection AddFoundryGateApiServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddIdentityServices();
        services.AddAuditServices();

        return services;
    }
}
