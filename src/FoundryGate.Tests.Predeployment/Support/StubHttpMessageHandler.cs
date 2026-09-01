using System.Net;
using System.Text;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// Scripted <see cref="HttpMessageHandler"/> for testing SDK-backed clients (the Graph directory
/// client) with no network: routes are matched in registration order against the request's
/// <em>URL-decoded</em> absolute URL (so a test can write <c>$select=id,displayName</c> rather than
/// <c>%24select=id%2CdisplayName</c>), and every request's decoded URL is recorded in
/// <see cref="Requests"/> for assertions on query shape and call counts. An unmatched request
/// throws, so a test never silently passes on a call it did not script.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly List<(Func<string, bool> Match, HttpStatusCode Status, string Json)> _routes = [];

    /// <summary>Decoded absolute URLs of every request seen, in order.</summary>
    public List<string> Requests { get; } = [];

    /// <summary>Scripts a JSON response for every request whose decoded URL satisfies <paramref name="urlMatch"/>.</summary>
    public StubHttpMessageHandler OnJson(Func<string, bool> urlMatch, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((urlMatch, status, json));
        return this;
    }

    /// <summary>Scripts a Graph-shaped <c>404 Request_ResourceNotFound</c> error for matching requests.</summary>
    public StubHttpMessageHandler OnNotFound(Func<string, bool> urlMatch) =>
        OnJson(
            urlMatch,
            """{"error":{"code":"Request_ResourceNotFound","message":"Resource does not exist or one of its queried reference-property objects are not present."}}""",
            HttpStatusCode.NotFound);

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var url = Uri.UnescapeDataString(request.RequestUri!.AbsoluteUri);
        Requests.Add(url);

        foreach (var (match, status, json) in _routes)
        {
            if (match(url))
            {
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    RequestMessage = request,
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
            }
        }

        throw new InvalidOperationException($"No stubbed response for {request.Method} {url}");
    }
}
