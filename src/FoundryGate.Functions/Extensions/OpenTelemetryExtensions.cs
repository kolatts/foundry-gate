using System.Reflection;
using Azure.Monitor.OpenTelemetry.Exporter;
using FoundryGate.Functions.Configuration;
using Microsoft.Azure.Functions.Worker.OpenTelemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace FoundryGate.Functions.Extensions;

/// <summary>
/// OpenTelemetry → Azure Monitor for the isolated worker (CONVENTIONS.md §Configuration &amp; auth:
/// "OpenTelemetry → Azure Monitor … gated by an <c>Enabled</c> option (off locally)"; no Serilog).
/// </summary>
/// <remarks>
/// The Api's <c>Azure.Monitor.OpenTelemetry.AspNetCore</c> one-liner does not apply here — there is no
/// ASP.NET Core pipeline in an isolated worker. The worker's own
/// <c>UseFunctionsWorkerDefaults()</c> wires the invocation activity source and the resource
/// attributes the Functions host expects, and the plain Azure Monitor exporters ship the result.
/// </remarks>
public static class OpenTelemetryExtensions
{
    /// <summary>
    /// No-ops when <paramref name="options"/>.Enabled is <see langword="false"/> — the local default,
    /// so `func start` never needs an Application Insights connection string.
    /// </summary>
    public static IServiceCollection AddFoundryGateOpenTelemetry(
        this IServiceCollection services,
        ILoggingBuilder logging,
        OpenTelemetryOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logging);
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return services;
        }

        var connectionString = options.ConnectionString!; // Validate() guarantees this when Enabled.

        var assembly = Assembly.GetExecutingAssembly();
        var serviceName = assembly.GetName().Name ?? "FoundryGate.Functions";
        var serviceVersion = assembly.GetName().Version?.ToString() ?? "0.0.1";

        _ = services.AddOpenTelemetry()
            .UseFunctionsWorkerDefaults()
            .ConfigureResource(resource => resource.AddService(serviceName: serviceName, serviceVersion: serviceVersion))
            .WithTracing(tracing => tracing
                .AddAzureMonitorTraceExporter(exporter => exporter.ConnectionString = connectionString))
            .WithMetrics(metrics => metrics
                .AddAzureMonitorMetricExporter(exporter => exporter.ConnectionString = connectionString));

        // Logs are a separate provider in the worker: the host forwards ILogger output over gRPC, but a
        // direct exporter is what puts structured job telemetry (the drift warnings) in the same
        // Application Insights resource as the traces above.
        _ = logging.AddOpenTelemetry(telemetry =>
            telemetry.AddAzureMonitorLogExporter(exporter => exporter.ConnectionString = connectionString));

        return services;
    }
}
