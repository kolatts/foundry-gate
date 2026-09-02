using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Entra;
using FoundryGate.Api.Services.Foundry;
using FoundryGate.Api.Services.Groups;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Api.Services.Keys;
using FoundryGate.Api.Services.Lifecycle;
using FoundryGate.Api.Services.Quota;
using FoundryGate.Api.Services.Requests;
using FoundryGate.Api.Services.Security;
using FoundryGate.Api.Services.Users;

namespace FoundryGate.Api.Services;

/// <summary>
/// The single DI entry point for FoundryGate.Api's application services, called exactly once from
/// <c>Program.cs</c>. Each <c>Services/&lt;Area&gt;</c> folder owns its own
/// <c>&lt;Area&gt;ServiceCollectionExtensions.Add&lt;Area&gt;Services()</c> and is invoked from here —
/// so adding a new area is one new file plus one new line below. Parallel waves will still collide
/// on adjacent lines in this method, but that conflict is one trivially-resolved line, versus the
/// registrations-plus-usings churn <c>Program.cs</c> would otherwise take from every wave.
/// </summary>
public static class ApiServiceCollectionExtensions
{
    /// <summary>Registers every <c>Services/&lt;Area&gt;</c> group. Order is not significant.</summary>
    public static IServiceCollection AddFoundryGateApiServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddIdentityServices();
        services.AddAuditServices();
        services.AddQuotaServices();
        services.AddGroupsServices();
        services.AddRequestsServices();
        services.AddFoundryServices();
        services.AddEntraServices();
        services.AddSecurityServices();
        services.AddKeysServices();
        services.AddLifecycleServices();
        services.AddUsersServices();

        return services;
    }
}
