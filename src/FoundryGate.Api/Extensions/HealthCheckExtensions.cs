using System.Reflection;
using FoundryGate.Data;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FoundryGate.Api.Extensions;

/// <summary>
/// Health check registration and endpoint mapping (issue #27). Two tiers, matching the
/// imagile-app precedent: <c>/health</c> is a liveness probe with no dependency checks (so it
/// stays hermetic — no docker, no Azure — and always answers fast); <c>/health/ready</c> adds
/// <see cref="AppDbContext"/> connectivity via <c>AddDbContextCheck</c> and may legitimately
/// report degraded/unhealthy when the database isn't reachable.
/// </summary>
public static class HealthCheckExtensions
{
    private static readonly string AssemblyVersion =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>Registers the "self" liveness check and the <see cref="AppDbContext"/>
    /// readiness check, tagged <c>live</c> and <c>ready</c> respectively.</summary>
    public static IServiceCollection AddFoundryGateHealthChecks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHealthChecks()
            .AddCheck(
                "self",
                () => HealthCheckResult.Healthy("The service is up and running."),
                tags: ["live"])
            .AddDbContextCheck<AppDbContext>(
                name: "database",
                failureStatus: HealthStatus.Unhealthy,
                tags: ["ready"]);

        return services;
    }

    /// <summary>Maps <c>GET /health</c> (liveness, anonymous) and <c>GET /health/ready</c>
    /// (readiness, anonymous) with JSON response bodies carrying app version and environment.</summary>
    public static WebApplication MapFoundryGateHealthChecks(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var environmentName = app.Environment.EnvironmentName;

        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("live"),
            ResponseWriter = (context, report) => WriteLivenessResponseAsync(context, report, environmentName),
        }).AllowAnonymous();

        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains("ready"),
            ResponseWriter = (context, report) => WriteReadinessResponseAsync(context, report, environmentName),
        }).AllowAnonymous();

        return app;
    }

    private static Task WriteLivenessResponseAsync(HttpContext context, HealthReport report, string environmentName)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(
            new
            {
                status = report.Status.ToString(),
                version = AssemblyVersion,
                environment = environmentName,
            },
            context.RequestAborted);
    }

    private static Task WriteReadinessResponseAsync(HttpContext context, HealthReport report, string environmentName)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(
            new
            {
                status = report.Status.ToString(),
                version = AssemblyVersion,
                environment = environmentName,
                checks = report.Entries.Select(entry => new
                {
                    name = entry.Key,
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description,
                    durationMs = entry.Value.Duration.TotalMilliseconds,
                }),
            },
            context.RequestAborted);
    }
}
