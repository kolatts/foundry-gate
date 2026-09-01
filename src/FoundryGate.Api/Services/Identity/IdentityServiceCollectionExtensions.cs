namespace FoundryGate.Api.Services.Identity;

/// <summary>DI registration for the <c>Services/Identity</c> area. Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class IdentityServiceCollectionExtensions
{
    /// <summary>Registers <see cref="ICurrentUserAccessor"/> (scoped — it caches the caller's <c>User</c> per request) and the <see cref="IHttpContextAccessor"/> it reads claims from.</summary>
    public static IServiceCollection AddIdentityServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUserAccessor, CurrentUserAccessor>();

        return services;
    }
}
