namespace FoundryGate.Api.Services.Config;

/// <summary>DI registration for the <c>Services/Config</c> area. Invoked from <see cref="ApiServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class ConfigServiceCollectionExtensions
{
    /// <summary>
    /// Registers the per-key validation table (<see cref="SystemConfigValidator"/> — singleton, pure
    /// over the tier table <c>AddQuotaServices</c> registers) and <see cref="IConfigService"/>
    /// (scoped — it shares the request's <c>AppDbContext</c> so the value change and its audit row
    /// commit together).
    /// </summary>
    public static IServiceCollection AddConfigServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<SystemConfigValidator>();
        services.AddScoped<IConfigService, ConfigService>();

        return services;
    }
}
