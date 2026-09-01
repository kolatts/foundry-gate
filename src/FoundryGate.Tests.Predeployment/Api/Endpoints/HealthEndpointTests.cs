using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// End-to-end proof that the host starts hermetically — no docker, no Azure — using the same
/// <c>local</c> environment appsettings.local.json overlay a developer runs with (verification
/// item on plans/04-api-foundation.md: "dotnet run ... starts locally and GET /health returns
/// 200 WITHOUT docker running"). The database connection string is overridden to fail fast
/// (a closed local port) rather than the real docker SQL address, so /health/ready's
/// (expected) degraded result doesn't slow the suite down waiting on a TCP timeout.
/// </summary>
public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _client = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("local");
            builder.ConfigureAppConfiguration((_, configurationBuilder) =>
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:FoundryGate"] =
                        "Server=127.0.0.1,1;Database=FoundryGateTest;Connect Timeout=1;TrustServerCertificate=True",
                }));
        }).CreateClient();
    }

    [Fact]
    public async Task Health_returns_200_without_authentication_and_without_a_database()
    {
        var response = await _client.GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Healthy", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HealthReady_returns_503_rather_than_throwing_when_the_database_is_unreachable()
    {
        // The connection string above points at a closed local port (127.0.0.1,1), so the
        // AddDbContextCheck<AppDbContext> check deterministically fails -- pinning 503 (not a
        // 200-or-503 either/or) is the actual contract: an unreachable database must degrade
        // the readiness probe, never crash the process or silently report healthy.
        var response = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("database", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unhealthy", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OpenApiDocument_is_served_in_the_local_environment()
    {
        var response = await _client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"openapi\"", body);
    }
}
