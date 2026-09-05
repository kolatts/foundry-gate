namespace FoundryGate.Tests.Postdeployment;

/// <summary>
/// The deployed environment these tests run against, read from the environment variables
/// <c>_postdeployment-tests.yml</c> already exports. Nothing here reaches Azure management APIs —
/// a postdeployment test only ever talks to the deployed HTTP surface, with credentials the
/// workflow (or a developer) supplies.
/// <list type="bullet">
/// <item><c>FG_API_BASE_URL</c> — the Container App's FQDN, exported by the reusable workflow.</item>
/// <item><c>FG_ADMIN_TOKEN</c> — a bearer token for a principal holding <c>FoundryGate.Admin</c>.</item>
/// <item><c>FG_NONADMIN_TOKEN</c> — a bearer token for a principal that does <em>not</em>.</item>
/// </list>
/// A missing variable skips the tests that need it rather than failing them, so the suite stays
/// runnable from a developer machine with only the pieces at hand.
/// </summary>
public static class DeployedEnvironment
{
    /// <summary>Base URL of the deployed API, without a trailing slash, or <see langword="null"/> when unset.</summary>
    public static string? ApiBaseUrl => Normalize(Environment.GetEnvironmentVariable("FG_API_BASE_URL"));

    /// <summary>Bearer token for a principal holding the <c>FoundryGate.Admin</c> app role.</summary>
    public static string? AdminToken => Normalize(Environment.GetEnvironmentVariable("FG_ADMIN_TOKEN"));

    /// <summary>Bearer token for an authenticated principal without the <c>FoundryGate.Admin</c> app role.</summary>
    public static string? NonAdminToken => Normalize(Environment.GetEnvironmentVariable("FG_NONADMIN_TOKEN"));

    /// <summary>Why the API tests cannot run, or <see langword="null"/> when they can.</summary>
    public static string? ApiSkipReason =>
        ApiBaseUrl is null ? "FG_API_BASE_URL is not set — no deployed API to test against." : null;

    /// <summary>Why the admin-token tests cannot run, or <see langword="null"/> when they can.</summary>
    public static string? AdminSkipReason =>
        ApiSkipReason ?? (AdminToken is null ? "FG_ADMIN_TOKEN is not set — no Entra token for an admin principal." : null);

    /// <summary>Why the non-admin-token tests cannot run, or <see langword="null"/> when they can.</summary>
    public static string? NonAdminSkipReason =>
        ApiSkipReason ?? (NonAdminToken is null ? "FG_NONADMIN_TOKEN is not set — no Entra token for a non-admin principal." : null);

    /// <summary>An <see cref="HttpClient"/> pointed at the deployed API, optionally carrying a bearer token.</summary>
    public static HttpClient CreateClient(string? bearerToken = null)
    {
        var baseUrl = ApiBaseUrl ?? throw new InvalidOperationException("FG_API_BASE_URL is not set.");
        var client = new HttpClient { BaseAddress = new Uri(baseUrl + "/"), Timeout = TimeSpan.FromSeconds(60) };

        if (bearerToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new("Bearer", bearerToken);
        }

        return client;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/');
}
