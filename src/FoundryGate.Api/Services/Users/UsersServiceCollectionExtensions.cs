namespace FoundryGate.Api.Services.Users;

/// <summary>DI registration for the <c>Services/Users</c> area. Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class UsersServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scoped <see cref="IUserService"/> (it shares the request's <c>AppDbContext</c> with
    /// the lifecycle, quota, key and audit services it composes). The Entra bulk sync behind
    /// <c>POST /users/sync</c> is registered by <c>AddEntraServices</c>, where its Graph client lives.
    /// </summary>
    public static IServiceCollection AddUsersServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IUserService, UserService>();

        return services;
    }
}
