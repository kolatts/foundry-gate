using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FoundryGate.Domain.Keys.Contracts;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// The half of #181 that configuration alone makes testable: the window is a setting, and it really
/// turns over. Its own host, configured with a one-second reveal window and a permit count of one, so
/// the whole cycle — spend the budget, be refused, be allowed again — fits in a couple of seconds of
/// real time.
/// </summary>
/// <remarks>
/// This is the only test in the suite that waits on the wall clock, and it has to:
/// <c>System.Threading.RateLimiting</c>'s limiters own their replenishment timer and accept no
/// <c>TimeProvider</c>, so <c>ApiTestFactory.TimeProvider</c> cannot move them. It polls with a
/// generous ceiling rather than sleeping for exactly one window, so a slow CI agent makes it slower
/// rather than red.
/// </remarks>
public class KeysRateLimitWindowTests(KeysRateLimitWindowTests.ShortWindowFactory factory)
    : IClassFixture<KeysRateLimitWindowTests.ShortWindowFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>How long to keep retrying before calling a window that should have turned over broken.</summary>
    private static readonly TimeSpan ReplenishmentCeiling = TimeSpan.FromSeconds(20);

    private const string KeysPath = "/api/v1/keys";

    /// <summary>A host whose reveal policy is one request per second — the shape a fork retuning `Security:RateLimits` would produce.</summary>
    public sealed class ShortWindowFactory : ApiTestFactory
    {
        /// <inheritdoc />
        protected override void ConfigureSettings(IDictionary<string, string> settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            settings["Security:RateLimits:Reveal:PermitLimit"] = "1";
            settings["Security:RateLimits:Reveal:WindowSeconds"] = "1";
        }
    }

    [Fact]
    public async Task The_configured_permit_count_and_window_are_what_the_host_enforces()
    {
        Assert.Equal(1, factory.RateLimits.Reveal.PermitLimit);
        Assert.Equal(TimeSpan.FromSeconds(1), factory.RateLimits.Reveal.Window);

        // Rotate was not configured, so it keeps the shipped default — a fork retunes one policy
        // without disturbing the other.
        Assert.Equal(3, factory.RateLimits.Rotate.PermitLimit);
        Assert.Equal(TimeSpan.FromSeconds(60), factory.RateLimits.Rotate.Window);

        using var developer = await ProvisionedDeveloperAsync();

        using var first = await developer.PostAsync(new Uri($"{KeysPath}/me/reveal", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var refused = await developer.PostAsync(new Uri($"{KeysPath}/me/reveal", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        // Retry-After never promises longer than the configured window.
        var retryAfter = int.Parse(Assert.Single(refused.Headers.GetValues("Retry-After")), System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(retryAfter, 0, 1);

        Assert.True(await EventuallyAllowedAsync(developer), $"The one-second window never replenished within {ReplenishmentCeiling.TotalSeconds}s.");
    }

    /// <summary>Polls the reveal route until it is allowed again, or the ceiling passes.</summary>
    private static async Task<bool> EventuallyAllowedAsync(HttpClient client)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < ReplenishmentCeiling)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            using var response = await client.PostAsync(new Uri($"{KeysPath}/me/reveal", UriKind.Relative), null);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A developer with a provisioned key and a client authenticated as them.</summary>
    private async Task<HttpClient> ProvisionedDeveloperAsync()
    {
        var adminUser = await factory.SeedUserAsync(displayName: "Ada Admin");
        using var admin = factory.CreateClientAs(adminUser.EntraObjectId, isAdmin: true);
        var developer = await factory.SeedUserAsync();

        using var response = await admin.PostAsync(new Uri($"{KeysPath}/{developer.UserId}/provision", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(await response.Content.ReadFromJsonAsync<ApiKeyRevealResponse>(JsonOptions));

        return factory.CreateClientAs(developer.EntraObjectId);
    }
}
