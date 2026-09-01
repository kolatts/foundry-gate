namespace FoundryGate.Api.Services;

/// <summary>
/// Fail-fast for singletons whose construction validates configuration (CONVENTIONS.md
/// "Options pattern, fail-fast"): a singleton registered through a factory is otherwise built on
/// its first use, turning a startup misconfiguration into a 500 on the first request. Registering
/// <see cref="AddResolveOnStartup{TService}"/> next to such a factory makes the host resolve it in
/// <see cref="IHostedService.StartAsync"/> — before the server accepts traffic — so the factory's
/// exception aborts startup instead. The same mechanism <c>OptionsBuilder.ValidateOnStart()</c>
/// uses, without requiring the options pattern's <c>IOptions&lt;T&gt;</c> plumbing.
/// </summary>
public static class StartupResolutionExtensions
{
    /// <summary>Resolves <typeparamref name="TService"/> once when the host starts, propagating any construction exception as a startup failure.</summary>
    public static IServiceCollection AddResolveOnStartup<TService>(this IServiceCollection services)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHostedService<ResolveOnStartupHostedService<TService>>();
        return services;
    }

    private sealed class ResolveOnStartupHostedService<TService>(IServiceProvider serviceProvider) : IHostedService
        where TService : class
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _ = serviceProvider.GetRequiredService<TService>();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
