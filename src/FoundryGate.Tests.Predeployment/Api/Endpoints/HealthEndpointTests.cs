using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// End-to-end proof that the host starts hermetically — no docker, no Azure — using the same
/// <c>local</c> environment appsettings.local.json overlay a developer runs with (verification
/// item on plans/04-api-foundation.md: "dotnet run ... starts locally and GET /health returns
/// 200 WITHOUT docker running").
/// </summary>
/// <remarks>
/// Both readiness branches are pinned here, each by a fixture that <em>owns</em> the answer rather
/// than inheriting it from the machine (#197):
/// <list type="bullet">
/// <item>the plain <see cref="WebApplicationFactory{TEntryPoint}"/> points <c>AppDbContext</c> at a
/// closed local port through <see cref="IWebHostBuilder.UseSetting"/> — the connection string the
/// host actually binds — so <c>/health/ready</c> is 503;</item>
/// <item><see cref="ApiTestFactory"/> runs the same pipeline on its kept-open SQLite in-memory
/// connection, so the very same check is reachable and <c>/health/ready</c> is 200.</item>
/// </list>
/// The override used to go through <c>ConfigureAppConfiguration</c>, whose sources minimal hosting
/// appends only after <c>Program.cs</c> has already run <c>Configuration.Get&lt;AppSettings&gt;()</c>
/// (CONVENTIONS.md, §Integration tests). It therefore never reached the bound options, and the 503
/// the test asserted was produced by whatever <c>appsettings.local.json</c>'s real connection string
/// happened to be — passing only on a machine with no SQL Server listening there (#197).
/// </remarks>
public class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>, IClassFixture<ApiTestFactory>
{
    /// <summary>A local port nothing listens on, so the readiness check fails fast instead of waiting out a TCP timeout.</summary>
    private const string UnreachableConnectionString =
        "Server=127.0.0.1,1;Database=FoundryGateTest;Connect Timeout=1;TrustServerCertificate=True";

    private readonly HttpClient _client;
    private readonly HttpClient _reachableDatabaseClient;

    public HealthEndpointTests(WebApplicationFactory<Program> factory, ApiTestFactory reachableDatabaseFactory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(reachableDatabaseFactory);

        _client = factory.WithWebHostBuilder(builder =>
        {
            _ = builder.UseEnvironment("local");
            _ = builder.UseSetting("ConnectionStrings:FoundryGate", UnreachableConnectionString);
        }).CreateClient();

        _reachableDatabaseClient = reachableDatabaseFactory.CreateClient();
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
        // The connection string above points at a closed local port (127.0.0.1,1) and reaches the
        // host through UseSetting, so the AddDbContextCheck<AppDbContext> check deterministically
        // fails whatever this machine has listening -- pinning 503 (not a 200-or-503 either/or) is
        // the actual contract: an unreachable database must degrade the readiness probe, never
        // crash the process or silently report healthy.
        var response = await _client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("database", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Unhealthy", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HealthReady_returns_200_when_the_database_is_reachable()
    {
        // The other half of the contract, and the half a broken override silently skips: with a
        // database the host can actually open (ApiTestFactory's SQLite in-memory connection), the
        // same check reports Healthy. Without this, "the readiness probe is wired to the database
        // at all" is unproven -- a check that always failed would pass the 503 test too.
        var response = await _reachableDatabaseClient.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("database", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Healthy", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Unhealthy", body, StringComparison.OrdinalIgnoreCase);
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
