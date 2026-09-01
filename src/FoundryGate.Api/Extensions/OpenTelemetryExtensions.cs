using System.Reflection;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using FoundryGate.Api.Configuration;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace FoundryGate.Api.Extensions;

/// <summary>
/// OpenTelemetry → Azure Monitor wiring (CONVENTIONS.md §Configuration &amp; auth: "OpenTelemetry
/// → Azure Monitor (ASP.NET Core + HttpClient + EF Core instrumentation, RecordException),
/// gated by an Enabled option (off locally)"). Adapted from imagile-app's
/// <c>ServiceCollectionExtensions.AddOpenTelemetryInstrumentation</c>.
/// </summary>
public static class OpenTelemetryExtensions
{
    /// <summary>No-ops when <paramref name="options"/>.Enabled is <c>false</c> (the
    /// appsettings.local.json default) so local dev never needs an Application Insights
    /// connection string.</summary>
    public static IServiceCollection AddFoundryGateOpenTelemetry(
        this IServiceCollection services,
        OpenTelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return services;
        }

        var assembly = Assembly.GetExecutingAssembly();
        var serviceName = assembly.GetName().Name ?? "FoundryGate.Api";
        var serviceVersion = assembly.GetName().Version?.ToString() ?? "0.0.1";

        var openTelemetryBuilder = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName: serviceName, serviceVersion: serviceVersion));

        openTelemetryBuilder.WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation(instrumentation => instrumentation.RecordException = true)
            .AddHttpClientInstrumentation(instrumentation => instrumentation.RecordException = true)
            .AddEntityFrameworkCoreInstrumentation());

        openTelemetryBuilder.WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation());

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            openTelemetryBuilder.UseAzureMonitor(monitorOptions => monitorOptions.ConnectionString = options.ConnectionString);
        }
        else
        {
            openTelemetryBuilder.UseAzureMonitor();
        }

        return services;
    }
}
