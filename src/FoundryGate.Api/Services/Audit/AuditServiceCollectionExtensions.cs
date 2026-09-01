namespace FoundryGate.Api.Services.Audit;

/// <summary>DI registration for the <c>Services/Audit</c> area. Invoked from <see cref="ServiceCollectionExtensions.AddFoundryGateApiServices"/>.</summary>
public static class AuditServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IAuditService"/> (scoped — it shares the request's <c>AppDbContext</c> so audit rows save atomically with the mutation).</summary>
    public static IServiceCollection AddAuditServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IAuditService, AuditService>();

        return services;
    }
}
