using FoundryGate.Core.Quota;

namespace FoundryGate.Api.Services.Identity;

/// <summary>DI registration for the <c>Services/Identity</c> area. Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ICurrentUserAccessor"/> (scoped — it caches the caller's <c>User</c> per
    /// request), the <see cref="IHttpContextAccessor"/> it reads claims from, and the
    /// <see cref="IGatewayTierSyncActor"/> that attributes a Core-driven gateway tier move to that same
    /// caller (#194 — the Functions host registers the system one instead).
    /// </summary>
    public static IServiceCollection AddIdentityServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();
        services.AddScoped<IGatewayTierSyncActor, CurrentUserGatewayTierSyncActor>();

        return services;
    }
}
