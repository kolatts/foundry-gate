using System.Net;
using System.Net.Sockets;

namespace FoundryGate.Cli.Commands.Ip;

/// <summary>Answers "what public IPv4 address does this machine egress from?" — behind an interface so command logic is testable offline.</summary>
public interface IPublicIpProvider
{
    /// <summary>The caller's public IPv4 address; throws <see cref="InvalidOperationException"/> with a printable message when it cannot be determined.</summary>
    Task<IPAddress> GetPublicIpAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Asks a public "what is my IP" service, trying each endpoint in turn (ipify first, as imagile-app
/// does; ifconfig.me as the fallback). Azure SQL firewall rules are IPv4-only, so an IPv6 answer is
/// rejected rather than turned into a rule ARM would refuse.
/// </summary>
public sealed class HttpPublicIpProvider(HttpClient httpClient) : IPublicIpProvider
{
    /// <summary>Endpoints that return the caller's address as a bare text body.</summary>
    public static readonly IReadOnlyList<Uri> Endpoints =
    [
        new("https://api.ipify.org"),
        new("https://ifconfig.me/ip")
    ];

    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    /// <inheritdoc />
    public async Task<IPAddress> GetPublicIpAsync(CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        foreach (var endpoint in Endpoints)
        {
            try
            {
                var body = (await _httpClient.GetStringAsync(endpoint, cancellationToken)).Trim();
                if (TryParseIpv4(body, out var address))
                {
                    return address;
                }

                failures.Add($"{endpoint}: '{body}' is not an IPv4 address");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                failures.Add($"{endpoint}: {ex.Message}");
            }
        }

        throw new InvalidOperationException(
            "Unable to detect this machine's public IPv4 address (" + string.Join("; ", failures) + "). " +
            "Check internet connectivity or pass --ip <address> explicitly.");
    }

    /// <summary>
    /// Parses a dotted-quad IPv4 literal only — IPv6 and hostnames are rejected, and so is
    /// <c>0.0.0.0</c>: a rule with start == end == <c>0.0.0.0</c> is Azure's allow-all-Azure-services
    /// sentinel (<c>infra/modules/sql.bicep</c>'s "magic 0.0.0.0 rule"), not a host address, so a
    /// typo'd <c>--ip</c> or a captive-portal response body must never become one under a <c>gha-*</c> name.
    /// </summary>
    public static bool TryParseIpv4(string? value, out IPAddress address)
    {
        if (IPAddress.TryParse(value, out var parsed)
            && parsed.AddressFamily == AddressFamily.InterNetwork
            && !parsed.Equals(IPAddress.Any))
        {
            address = parsed;
            return true;
        }

        address = IPAddress.None;
        return false;
    }
}
