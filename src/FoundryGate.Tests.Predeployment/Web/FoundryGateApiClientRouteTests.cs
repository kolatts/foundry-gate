using System.Net;
using System.Net.Http.Json;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Requests;
using FoundryGate.Domain.Requests.Contracts;
using FoundryGate.Domain.Users.Contracts;
using FoundryGate.Web.Services;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// The verb and path each <see cref="FoundryGateApiClient"/> method actually puts on the wire,
/// checked against <c>docs-site/src/content/docs/reference/api.md</c>.
/// </summary>
/// <remarks>
/// The shell client (#48) was written from the spec before the controllers existed, and four
/// methods drifted: activate, deactivate, approve and reject all sent <c>PUT</c> where the API
/// serves <c>POST</c>, and <c>GET /users/{id}</c> was deserialized as <c>UserResponse</c> where
/// it returns <c>UserDetailResponse</c> (#169). Every one of those failed at runtime — a 405 or
/// a half-empty object — and nothing in the build noticed. These tests are the tripwire: the
/// next drift fails CI rather than the browser.
/// </remarks>
public class FoundryGateApiClientRouteTests
{
    [Fact]
    public async Task ActivateUserAsync_posts_to_the_activate_route()
    {
        var (client, handler) = CreateClient(Json(WebTestData.User()));

        _ = await client.ActivateUserAsync(userId: 7);

        AssertSent(handler, HttpMethod.Post, "users/7/activate");
    }

    [Fact]
    public async Task DeactivateUserAsync_posts_to_the_deactivate_route()
    {
        var (client, handler) = CreateClient(Json(WebTestData.User()));

        _ = await client.DeactivateUserAsync(userId: 7);

        AssertSent(handler, HttpMethod.Post, "users/7/deactivate");
    }

    [Fact]
    public async Task ApproveRequestAsync_posts_to_the_approve_route()
    {
        var (client, handler) = CreateClient(new HttpResponseMessage(HttpStatusCode.OK));

        _ = await client.ApproveRequestAsync(requestId: 5, new ReviewQuotaIncreaseRequest());

        AssertSent(handler, HttpMethod.Post, "requests/5/approve");
    }

    [Fact]
    public async Task RejectRequestAsync_posts_to_the_reject_route()
    {
        var (client, handler) = CreateClient(new HttpResponseMessage(HttpStatusCode.OK));

        _ = await client.RejectRequestAsync(requestId: 5, new ReviewQuotaIncreaseRequest());

        AssertSent(handler, HttpMethod.Post, "requests/5/reject");
    }

    [Fact]
    public async Task UpdateUserQuotaAsync_puts_to_the_quota_route_and_reads_back_the_user()
    {
        var (client, handler) = CreateClient(Json(WebTestData.User(displayName: "Dev Eloper")));

        var result = await client.UpdateUserQuotaAsync(7, new UpdateUserQuotaRequest { IsUnlimited = true });

        AssertSent(handler, HttpMethod.Put, "users/7/quota");
        Assert.True(result.IsSuccess);
        Assert.Equal("Dev Eloper", result.Value?.DisplayName);
    }

    [Fact]
    public async Task GetUserAsync_deserializes_the_detail_shape_the_api_returns()
    {
        // The regression #169 names: as UserResponse this deserialized to an object whose every
        // field was default, and the page rendered a blank user rather than failing.
        var detail = WebTestData.UserDetail(
            WebTestData.User(userId: 7, displayName: "Dev Eloper"),
            groups: [WebTestData.Membership(groupId: 3, name: "Platform")]);
        var (client, handler) = CreateClient(Json(detail));

        var result = await client.GetUserAsync(7);

        AssertSent(handler, HttpMethod.Get, "users/7");
        Assert.True(result.IsSuccess);
        Assert.Equal("Dev Eloper", result.Value?.User.DisplayName);
        Assert.Equal("Platform", Assert.Single(result.Value!.Groups).Name);
        Assert.NotNull(result.Value.CurrentAllocation);
        Assert.NotNull(result.Value.ApiKey);
    }

    [Fact]
    public async Task GetUsersAsync_puts_search_and_isActive_on_the_query_string()
    {
        var (client, handler) = CreateClient(Json(new PagedResult<UserResponse>([], 0, 1, 25)));

        _ = await client.GetUsersAsync(new UserListQuery("hopper", IsActive: false), new PagedRequest(2, 50));

        var uri = Assert.Single(handler.Requests).RequestUri!.ToString();
        Assert.Contains("page=2", uri, StringComparison.Ordinal);
        Assert.Contains("pageSize=50", uri, StringComparison.Ordinal);
        Assert.Contains("search=hopper", uri, StringComparison.Ordinal);
        Assert.Contains("isActive=false", uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetUsersAsync_omits_the_filters_that_were_not_set()
    {
        var (client, handler) = CreateClient(Json(new PagedResult<UserResponse>([], 0, 1, 25)));

        _ = await client.GetUsersAsync(new UserListQuery(null, null), new PagedRequest(1, 25));

        var uri = Assert.Single(handler.Requests).RequestUri!.ToString();
        Assert.DoesNotContain("search=", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("isActive=", uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRequestsAsync_sends_the_status_as_its_numeric_value()
    {
        // No string enum converter is configured on either side, so api.md's "0 Pending,
        // 1 Approved, 2 Rejected" is literal.
        var (client, handler) = CreateClient(Json(new PagedResult<QuotaIncreaseRequestResponse>([], 0, 1, 25)));

        _ = await client.GetRequestsAsync(new QuotaRequestQuery(QuotaRequestStatusType.Rejected, UserId: 7), new PagedRequest(1, 25));

        var uri = Assert.Single(handler.Requests).RequestUri!.ToString();
        Assert.Contains("status=2", uri, StringComparison.Ordinal);
        Assert.Contains("userId=7", uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteGroupAsync_only_forces_when_asked_to()
    {
        var (forcing, forcingHandler) = CreateClient(new HttpResponseMessage(HttpStatusCode.NoContent));
        _ = await forcing.DeleteGroupAsync(7, force: true);
        Assert.Contains("force=true", Assert.Single(forcingHandler.Requests).RequestUri!.ToString(), StringComparison.Ordinal);

        var (plain, plainHandler) = CreateClient(new HttpResponseMessage(HttpStatusCode.NoContent));
        _ = await plain.DeleteGroupAsync(7, force: false);
        Assert.DoesNotContain("force", Assert.Single(plainHandler.Requests).RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteFoundryDeploymentAsync_addresses_the_deployment_by_account_and_name()
    {
        var (client, handler) = CreateClient(new HttpResponseMessage(HttpStatusCode.NoContent));

        _ = await client.DeleteFoundryDeploymentAsync("fg-eastus", "gpt-5-codex");

        AssertSent(handler, HttpMethod.Delete, "foundry/deployments/fg-eastus/gpt-5-codex");
    }

    [Fact]
    public async Task GetLastUserSyncAsync_reads_the_sync_status_route()
    {
        var (client, handler) = CreateClient(Json(new UserSyncStatusResponse(
            new DateTimeOffset(2026, 9, 1, 7, 0, 0, TimeSpan.Zero),
            new UserSyncResult(1, 2, 3, 0, 0))));

        var result = await client.GetLastUserSyncAsync();

        AssertSent(handler, HttpMethod.Get, "users/sync/last");
        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value?.LastResult?.DeactivatedCount);
    }

    [Fact]
    public async Task ProvisionUserKeyAsync_sends_no_tier_query()
    {
        // ?tier= was removed with #156; the provision call takes the user's resolved tier.
        var (client, handler) = CreateClient(Json(WebTestData.Reveal()));

        _ = await client.ProvisionUserKeyAsync(7);

        AssertSent(handler, HttpMethod.Post, "keys/7/provision");
        Assert.Equal(string.Empty, Assert.Single(handler.Requests).RequestUri!.Query);
    }

    private static void AssertSent(RecordingHandler handler, HttpMethod method, string relativePath)
    {
        var request = Assert.Single(handler.Requests);

        Assert.Equal(method, request.Method);
        Assert.Equal($"/api/v1/{relativePath}", request.RequestUri!.AbsolutePath);
    }

    private static HttpResponseMessage Json<T>(T value) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private static (FoundryGateApiClient Client, RecordingHandler Handler) CreateClient(HttpResponseMessage response)
    {
        var handler = new RecordingHandler(response);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://foundrygate.test/api/v1/") };
        return (new FoundryGateApiClient(httpClient), handler);
    }

    /// <summary>Records what was sent and answers with one canned response.</summary>
    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(response);
        }
    }
}
