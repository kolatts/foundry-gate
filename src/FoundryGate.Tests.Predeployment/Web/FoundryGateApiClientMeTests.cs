using System.Net;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Requests;
using FoundryGate.Domain.Requests.Contracts;
using FoundryGate.Web.Services;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// The routes and query strings the pages' new client methods actually put on the wire
/// (<c>FoundryGateApiClient.Me.cs</c>). A wrong verb or a mis-spelled query parameter fails
/// silently in the browser — a 405 or an unfiltered page — so it is asserted here against the
/// paths in <c>docs-site/src/content/docs/reference/api.md</c>.
/// </summary>
public class FoundryGateApiClientMeTests
{
    [Fact]
    public async Task RevealMyKeyAsync_posts_to_keys_me_reveal()
    {
        var (client, handler) = CreateClient();

        _ = await client.RevealMyKeyAsync();

        Assert.Equal(HttpMethod.Post, handler.LastRequest?.Method);
        Assert.Equal("https://foundrygate.test/api/v1/keys/me/reveal", handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task GetQuotaTiersAsync_gets_quota_tiers()
    {
        var (client, handler) = CreateClient();

        _ = await client.GetQuotaTiersAsync();

        Assert.Equal(HttpMethod.Get, handler.LastRequest?.Method);
        Assert.Equal("https://foundrygate.test/api/v1/quota/tiers", handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task GetFoundryModelsAsync_gets_the_developer_model_view()
    {
        var (client, handler) = CreateClient();

        _ = await client.GetFoundryModelsAsync();

        Assert.Equal("https://foundrygate.test/api/v1/foundry/models", handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task Filtered_requests_send_the_status_as_the_enums_int_value()
    {
        var (client, handler) = CreateClient();

        _ = await client.GetRequestsAsync(
            new QuotaRequestQuery(QuotaRequestStatusType.Pending, UserId: null),
            new PagedRequest(1, 1));

        // api.md: "?status= (0 Pending, 1 Approved, 2 Rejected)".
        Assert.Contains("status=0", handler.LastRequest?.RequestUri?.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("userId", handler.LastRequest?.RequestUri?.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filtered_requests_include_a_named_user_when_one_is_given()
    {
        var (client, handler) = CreateClient();

        _ = await client.GetRequestsAsync(
            new QuotaRequestQuery(QuotaRequestStatusType.Approved, UserId: 42),
            new PagedRequest(2, 50));

        var query = handler.LastRequest?.RequestUri?.Query;
        Assert.Contains("status=1", query, StringComparison.Ordinal);
        Assert.Contains("userId=42", query, StringComparison.Ordinal);
        Assert.Contains("page=2", query, StringComparison.Ordinal);
        Assert.Contains("pageSize=50", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_out_of_range_page_is_clamped_rather_than_sent_as_is()
    {
        var (client, handler) = CreateClient();

        _ = await client.GetRequestsAsync(
            new QuotaRequestQuery(Status: null, UserId: null),
            new PagedRequest(0, 5_000));

        var query = handler.LastRequest?.RequestUri?.Query;
        Assert.Contains("page=1", query, StringComparison.Ordinal);
        Assert.Contains($"pageSize={PagedRequest.MaxPageSize}", query, StringComparison.Ordinal);
    }

    private static (FoundryGateApiClient Client, RecordingHandler Handler) CreateClient()
    {
        var handler = new RecordingHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://foundrygate.test/api/v1/") };
        return (new FoundryGateApiClient(httpClient), handler);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
