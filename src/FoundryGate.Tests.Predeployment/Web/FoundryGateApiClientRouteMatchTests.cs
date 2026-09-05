using System.Net;
using FoundryGate.Tests.Predeployment.Api;
using FoundryGate.Web.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// Every route <see cref="FoundryGateApiClient"/> puts on the wire, resolved against the API's own
/// <see cref="EndpointDataSource"/> rather than against a second copy of the docs.
/// </summary>
/// <remarks>
/// <see cref="FoundryGateApiClientRouteTests"/> asserts the verb and path each method sends, which
/// is what caught the #169 drift — but its expectations were still hand-written from
/// <c>api.md</c>'s prose, so a client and a docs table could agree with each other and both be
/// wrong. This suite closes that: the Web project's client and the Api project's routing table are
/// both in this assembly's reference graph, so an unmatched route is a build-time-adjacent failure
/// instead of a 404 in a browser. A method that starts sending a path no controller serves fails
/// here even if nobody remembered to update the docs.
/// <para>
/// Only mutating calls and the ones carrying route parameters are listed: those are where a
/// hand-built path string can go wrong. A wrong verb on a real path is caught too, since the match
/// is verb-aware.
/// </para>
/// </remarks>
public class FoundryGateApiClientRouteMatchTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    public static TheoryData<string, string> ClientRoutes() => new()
    {
        // Users
        { "GET", "/api/v1/users" },
        { "GET", "/api/v1/users/me" },
        { "GET", "/api/v1/users/7" },
        { "PUT", "/api/v1/users/7/quota" },
        { "POST", "/api/v1/users/7/activate" },
        { "POST", "/api/v1/users/7/deactivate" },
        { "POST", "/api/v1/users/sync" },
        { "GET", "/api/v1/users/sync/last" },

        // Groups
        { "GET", "/api/v1/groups" },
        { "POST", "/api/v1/groups" },
        { "GET", "/api/v1/groups/7" },
        { "PUT", "/api/v1/groups/7" },
        { "DELETE", "/api/v1/groups/7" },
        { "GET", "/api/v1/groups/7/members" },
        { "POST", "/api/v1/groups/7/members" },
        { "DELETE", "/api/v1/groups/7/members/1" },
        { "POST", "/api/v1/groups/7/sync-entra" },
        { "POST", "/api/v1/groups/sync-entra" },

        // Quota
        { "GET", "/api/v1/quota/tiers" },
        { "GET", "/api/v1/quota/allocations" },
        { "GET", "/api/v1/quota/allocations/me" },
        { "GET", "/api/v1/quota/allocations/7" },
        { "POST", "/api/v1/quota/reset" },

        // Quota increase requests
        { "GET", "/api/v1/requests" },
        { "POST", "/api/v1/requests" },
        { "GET", "/api/v1/requests/5" },
        { "POST", "/api/v1/requests/5/approve" },
        { "POST", "/api/v1/requests/5/reject" },

        // Keys
        { "GET", "/api/v1/keys/me" },
        { "POST", "/api/v1/keys/me/reveal" },
        { "POST", "/api/v1/keys/me/rotate" },
        { "POST", "/api/v1/keys/7/rotate" },
        { "POST", "/api/v1/keys/7/provision" },
        { "DELETE", "/api/v1/keys/7" },

        // Foundry
        { "GET", "/api/v1/foundry/models" },
        { "GET", "/api/v1/foundry/deployments" },
        { "GET", "/api/v1/foundry/catalog" },
        { "POST", "/api/v1/foundry/deployments" },
        { "DELETE", "/api/v1/foundry/deployments/fg-eastus/gpt-4-1-mini" },
        { "GET", "/api/v1/foundry/deployments/fg-eastus/gpt-4-1-mini" },

        // Gateway model allowlist (#225)
        { "GET", "/api/v1/gateway/tiers" },
        { "GET", "/api/v1/gateway/tiers/standard/models" },
        { "PUT", "/api/v1/gateway/tiers/standard/models" },

        // Admin
        { "GET", "/api/v1/config" },
        { "PUT", "/api/v1/config/DefaultMonthlyTokenQuota" },
        { "GET", "/api/v1/audit" },
        { "GET", "/api/v1/dashboard" },
    };

    [Theory]
    [MemberData(nameof(ClientRoutes))]
    public async Task Every_route_the_client_sends_is_served_by_the_api(string method, string path)
    {
        // The cheapest honest proof that a route exists: ask the real pipeline for it anonymously.
        // A served route answers 401 (the global AuthorizeFilter); a route nothing serves answers
        // 404 or 405 before authorization runs.
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));

        using var response = await client.SendAsync(request);

        Assert.False(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"{method} {path} is not served by the API — the client would get a {(int)response.StatusCode}.");
    }

    [Fact]
    public void The_route_list_covers_every_mutating_client_method()
    {
        // A method added to the client without a row above would otherwise be silently unguarded.
        var mutating = typeof(IFoundryGateApiClient)
            .GetMethods()
            .Select(m => m.Name)
            .Where(name =>
                name.StartsWith("Create", StringComparison.Ordinal) ||
                name.StartsWith("Update", StringComparison.Ordinal) ||
                name.StartsWith("Delete", StringComparison.Ordinal) ||
                name.StartsWith("Revoke", StringComparison.Ordinal) ||
                name.StartsWith("Rotate", StringComparison.Ordinal) ||
                name.StartsWith("Provision", StringComparison.Ordinal) ||
                name.StartsWith("Approve", StringComparison.Ordinal) ||
                name.StartsWith("Reject", StringComparison.Ordinal) ||
                name.StartsWith("Activate", StringComparison.Ordinal) ||
                name.StartsWith("Deactivate", StringComparison.Ordinal) ||
                name.StartsWith("Add", StringComparison.Ordinal) ||
                name.StartsWith("Remove", StringComparison.Ordinal) ||
                name.StartsWith("Submit", StringComparison.Ordinal) ||
                name.StartsWith("Sync", StringComparison.Ordinal) ||
                name.StartsWith("Reset", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // 22 mutating methods across users, groups, quota, requests, keys and foundry. Bump this
        // deliberately, and add the route row in the same commit.
        Assert.Equal(22, mutating.Count);
    }

    [Fact]
    public void The_api_serves_no_route_the_client_reaches_by_a_different_verb()
    {
        // The exact shape of #169: /users/{id}/activate exists, and the client sent PUT to it.
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        var activate = endpoints
            .OfType<RouteEndpoint>()
            .Single(e => string.Equals(e.RoutePattern.RawText, "api/v1/Users/{userId:int}/activate", StringComparison.OrdinalIgnoreCase));

        var verbs = activate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;

        Assert.NotNull(verbs);
        Assert.Equal("POST", Assert.Single(verbs));
    }
}
