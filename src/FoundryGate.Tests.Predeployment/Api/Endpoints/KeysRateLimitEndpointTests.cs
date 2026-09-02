using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FoundryGate.Domain.Keys.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// The per-user rate limits on the two routes that hand a developer their own gateway credential
/// (#136), through the real pipeline. Its own factory (its own host) so the limiter's partitions start
/// empty and the counts in these tests cannot be spent by another class's requests; every test uses a
/// freshly-seeded oid, which is what the policies partition on, so they are hermetic from each other.
/// </summary>
/// <remarks>
/// <b>These read the wall clock, not <c>factory.TimeProvider</c></b> (#184 review nit).
/// <c>System.Threading.RateLimiting</c>'s built-in limiters own their replenishment timer and expose no
/// seam to drive it, and the middleware does not offer one either — so a test cannot move the window.
/// Since #181 it can <em>choose</em> one: the permit counts and window are <c>Security:RateLimits</c>,
/// so these tests read the values the host is enforcing rather than restating them, and
/// <see cref="KeysRateLimitWindowTests"/> configures a one-second window to prove it really turns over.
/// </remarks>
public class KeysRateLimitEndpointTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string KeysPath = "/api/v1/keys";

    /// <summary>What the host is actually enforcing (<c>Security:RateLimits:Reveal:PermitLimit</c>), not a copy of it.</summary>
    private int RevealsPerWindow => factory.RateLimits.Reveal.PermitLimit;

    /// <summary>What the host is actually enforcing (<c>Security:RateLimits:Rotate:PermitLimit</c>).</summary>
    private int RotationsPerWindow => factory.RateLimits.Rotate.PermitLimit;

    [Fact]
    public async Task Reveal_is_capped_per_user_and_the_refusal_is_a_ProblemDetails_429_with_Retry_After()
    {
        using var developerClient = await ProvisionedDeveloperAsync();

        for (var i = 0; i < RevealsPerWindow; i++)
        {
            using var allowed = await developerClient.PostAsync(new Uri($"{KeysPath}/me/reveal", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var refused = await developerClient.PostAsync(new Uri($"{KeysPath}/me/reveal", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        // Retry-After so a client can back off without parsing prose.
        var retryAfter = Assert.Single(refused.Headers.GetValues("Retry-After"));
        Assert.True(int.Parse(retryAfter, CultureInfo.InvariantCulture) > 0);

        // Same ProblemDetails shape as every other error the API produces (CONVENTIONS.md).
        var problem = await refused.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions);
        Assert.NotNull(problem);
        Assert.Equal((int)HttpStatusCode.TooManyRequests, problem.Status);
        Assert.Contains("rate-limited per user", problem.Detail, StringComparison.Ordinal);
        Assert.True(problem.Extensions.ContainsKey("correlationId"));
    }

    [Fact]
    public async Task Rotate_is_capped_per_user_more_tightly_than_reveal()
    {
        Assert.True(RotationsPerWindow < RevealsPerWindow, "Rotation is a write the gateway feels, so its cap must stay the tighter of the two.");

        using var developerClient = await ProvisionedDeveloperAsync();

        for (var i = 0; i < RotationsPerWindow; i++)
        {
            using var allowed = await developerClient.PostAsync(new Uri($"{KeysPath}/me/rotate", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }

        using var refused = await developerClient.PostAsync(new Uri($"{KeysPath}/me/rotate", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        // Independent buckets: burning the rotate budget must not lock a developer out of reading the
        // key they already hold.
        using var reveal = await developerClient.PostAsync(new Uri($"{KeysPath}/me/reveal", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, reveal.StatusCode);
    }

    [Fact]
    public async Task One_developer_burning_their_budget_does_not_throttle_anybody_else()
    {
        // The reason the partition is the caller's oid and not their address (#136): the UI sits behind
        // a shared egress, so an IP partition would throttle a whole office at once.
        using var noisyClient = await ProvisionedDeveloperAsync();
        using var quietClient = await ProvisionedDeveloperAsync();

        for (var i = 0; i <= RevealsPerWindow; i++)
        {
            using var burned = await noisyClient.PostAsync(new Uri($"{KeysPath}/me/reveal", UriKind.Relative), null);
        }

        using var refused = await noisyClient.PostAsync(new Uri($"{KeysPath}/me/reveal", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        using var allowed = await quietClient.PostAsync(new Uri($"{KeysPath}/me/reveal", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task The_admin_rotate_route_is_deliberately_not_limited()
    {
        // An admin rotating a compromised team's keys is exactly the traffic a limiter would get in the
        // way of, and the route never discloses the admin's own credential to a token thief (#136).
        var adminUser = await factory.SeedUserAsync(displayName: "Ada Admin");
        using var admin = factory.CreateClientAs(adminUser.EntraObjectId, isAdmin: true);
        var developer = await factory.SeedUserAsync();
        using var provision = await admin.PostAsync(new Uri($"{KeysPath}/{developer.UserId}/provision", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, provision.StatusCode);

        for (var i = 0; i <= RotationsPerWindow + 2; i++)
        {
            using var response = await admin.PostAsync(new Uri($"{KeysPath}/{developer.UserId}/rotate", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task Anonymous_callers_do_not_share_one_bucket_and_keep_getting_their_401()
    {
        // The reviewer's probe (#184). The global authorization is an MVC AuthorizeFilter, not endpoint
        // metadata, so UseRateLimiter runs before anything has rejected an anonymous request: with a
        // single shared "anonymous" partition the sixth unauthenticated call turned 401 into 429, and one
        // scanner could deny every other anonymous caller the honest answer.
        using var anonymous = factory.CreateClient();

        for (var i = 0; i < RevealsPerWindow * 3; i++)
        {
            using var response = await anonymous.PostAsync(new Uri($"{KeysPath}/me/reveal", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // And an authenticated caller's own budget is untouched by all of that.
        using var developerClient = await ProvisionedDeveloperAsync();
        using var allowed = await developerClient.PostAsync(new Uri($"{KeysPath}/me/reveal", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
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
