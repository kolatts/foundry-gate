using FoundryGate.Data.Audit;
using FoundryGate.Data.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FoundryGate.Data;

/// <summary>
/// DI wiring for <see cref="AppDbContext"/> and the data-layer services every host shares. Called
/// by every host that talks to the database (Api now; Functions/Cli in later issues) so the context,
/// its interceptor, the <see cref="TimeProvider"/> it depends on, and the <see cref="IAuditWriter"/>
/// are configured exactly once, in one place.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TimeProvider.System"/>, <see cref="TimestampInterceptor"/>,
    /// <see cref="AppDbContext"/> (SQL Server) against <paramref name="connectionString"/>, and the
    /// scoped <see cref="IAuditWriter"/> (scoped because it adds to the same context the caller saves).
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

        services.AddScoped<IAuditWriter, AuditWriter>();

        return services;
    }
}
