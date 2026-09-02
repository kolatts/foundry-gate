using System.Net;
using FoundryGate.Cli.Commands.Ip;

namespace FoundryGate.Tests.Predeployment.Cli;

public class HttpPublicIpProviderTests
{
    [Fact]
    public async Task Returns_the_first_endpoints_IPv4_answer()
    {
        var handler = new ScriptedHandler { ["https://api.ipify.org/"] = _ => Text("203.0.113.10\n") };

        var address = await new HttpPublicIpProvider(new HttpClient(handler)).GetPublicIpAsync(CancellationToken.None);

        Assert.Equal(IPAddress.Parse("203.0.113.10"), address);
        Assert.Equal(["https://api.ipify.org/"], handler.Requested);
    }

    [Fact]
    public async Task Falls_back_to_the_next_endpoint_when_the_first_fails_or_answers_IPv6()
    {
        var handler = new ScriptedHandler
        {
            ["https://api.ipify.org/"] = _ => Text("2001:db8::1"),
            ["https://ifconfig.me/ip"] = _ => Text("198.51.100.7")
        };

        var address = await new HttpPublicIpProvider(new HttpClient(handler)).GetPublicIpAsync(CancellationToken.None);

        Assert.Equal(IPAddress.Parse("198.51.100.7"), address);
        Assert.Equal(["https://api.ipify.org/", "https://ifconfig.me/ip"], handler.Requested);
    }

    [Fact]
    public async Task Never_returns_the_allow_all_Azure_sentinel_from_a_bad_endpoint_body()
    {
        // A captive portal or a broken endpoint answering 0.0.0.0 would otherwise become a gha-* rule
        // that allows every Azure service — the same thing sql.bicep's "magic 0.0.0.0 rule" declares.
        var handler = new ScriptedHandler
        {
            ["https://api.ipify.org/"] = _ => Text("0.0.0.0"),
            ["https://ifconfig.me/ip"] = _ => Text("198.51.100.7")
        };

        var address = await new HttpPublicIpProvider(new HttpClient(handler)).GetPublicIpAsync(CancellationToken.None);

        Assert.Equal(IPAddress.Parse("198.51.100.7"), address);
    }

    [Fact]
    public async Task Reports_every_endpoint_failure_and_points_at_the_ip_override()
    {
        var handler = new ScriptedHandler
        {
            ["https://api.ipify.org/"] = _ => throw new HttpRequestException("boom"),
            ["https://ifconfig.me/ip"] = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new HttpPublicIpProvider(new HttpClient(handler)).GetPublicIpAsync(CancellationToken.None));

        Assert.Contains("api.ipify.org", ex.Message);
        Assert.Contains("ifconfig.me", ex.Message);
        Assert.Contains("--ip", ex.Message);
    }

    [Theory]
    [InlineData("203.0.113.10", true)]
    // 0.0.0.0 is Azure's allow-all-Azure-services sentinel, never a host address — see TryParseIpv4.
    [InlineData("0.0.0.0", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:203.0.113.10", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParseIpv4_accepts_dotted_quads_only(string? value, bool expected)
    {
        Assert.Equal(expected, HttpPublicIpProvider.TryParseIpv4(value, out _));
    }

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _responses = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Requested { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> this[string url]
        {
            set => _responses[url] = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            return Task.FromResult(_responses.TryGetValue(url, out var respond)
                ? respond(request)
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
