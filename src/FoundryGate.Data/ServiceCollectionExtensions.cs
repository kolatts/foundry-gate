using FoundryGate.Data.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FoundryGate.Data;

/// <summary>
/// DI wiring for <see cref="AppDbContext"/>. Called by every host that talks to the database
/// (Api now; Functions/Cli in later issues) so the context, its interceptor, and the
/// <see cref="TimeProvider"/> it depends on are configured exactly once, in one place.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TimeProvider.System"/>, <see cref="TimestampInterceptor"/>, and
    /// <see cref="AppDbContext"/> (SQL Server) against <paramref name="connectionString"/>.
    /// </summary>
    public static IServiceCollection AddFoundryGateData(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TimestampInterceptor>();

        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
            options.UseSqlServer(connectionString)
                .AddInterceptors(serviceProvider.GetRequiredService<TimestampInterceptor>()));

        return services;
    }
}
