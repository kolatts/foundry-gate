using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FoundryGate.Tests.Postdeployment;

/// <summary>
/// The 401/403 contract of <c>/api/v1</c>, asserted against a real deployment with real Entra
/// tokens (#102). <c>WebApplicationFactory</c> cannot mint an Entra token, so the predeployment
/// suite stubs the handler and proves only that the policies are attached; what it cannot prove is
/// that the deployed configuration — audience, issuer, tenant — actually accepts a token Entra
/// issued. It did not: <c>AzureAd:Audience</c> was <c>api://{clientId}</c> while the registration
/// mints v2 tokens whose audience is the bare client id, so every real token was rejected as
/// <c>The audience '(null)' is invalid</c> until #102's fix. That is the whole reason these
/// assertions belong here and not upstream.
/// </summary>
public class ApiAuthenticationTests
{
    /// <summary>
    /// One admin-only GET per controller that has an addressable admin route with no path
    /// parameters. Not exhaustive — Groups, Requests and the admin half of Keys are all keyed by a
    /// user or request id and need fixtures this suite does not build yet (#139). A route listed
    /// here that lost its policy shows up as a 200 for the non-admin token.
    /// </summary>
    public static TheoryData<string> AdminOnlyRoutes =>
    [
        "api/v1/users",
        "api/v1/audit",
        "api/v1/config",
        "api/v1/dashboard",
        "api/v1/quota/allocations",
        "api/v1/foundry/deployments",
        "api/v1/gateway/tiers/standard/models",
    ];

    /// <summary>
    /// Routes any authenticated developer may call. <c>quota/allocations/me</c> answers 403 for a
    /// principal with no <c>User</c> row yet, so a token for a developer who has never called
    /// <c>GET /users/me</c> will fail here rather than 200 — provision first, or point
    /// <c>FG_NONADMIN_TOKEN</c> at an established principal.
    /// </summary>
    public static TheoryData<string> DeveloperRoutes =>
    [
        "api/v1/users/me",
        "api/v1/quota/allocations/me",
    ];

    [DeployedApiTheory]
    [MemberData(nameof(AdminOnlyRoutes))]
    public async Task An_admin_route_without_a_token_is_401(string route)
    {
        using var client = DeployedEnvironment.CreateClient();

        using var response = await client.GetAsync(new Uri(route, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [DeployedApiTheory]
    [MemberData(nameof(DeveloperRoutes))]
    public async Task A_developer_route_without_a_token_is_401(string route)
    {
        using var client = DeployedEnvironment.CreateClient();

        using var response = await client.GetAsync(new Uri(route, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [DeployedApiFact]
    public async Task A_token_that_is_not_a_token_is_401_rather_than_a_500()
    {
        using var client = DeployedEnvironment.CreateClient("not.a.token");

        using var response = await client.GetAsync(new Uri("api/v1/users/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [NonAdminTokenTheory]
    [MemberData(nameof(AdminOnlyRoutes))]
    public async Task An_admin_route_with_a_valid_non_admin_token_is_403(string route)
    {
        using var client = DeployedEnvironment.CreateClient(DeployedEnvironment.NonAdminToken);

        using var response = await client.GetAsync(new Uri(route, UriKind.Relative));

        // 403, not 401: the token is valid and was accepted — the app role is what is missing. A
        // 401 here would mean the deployment cannot validate real tokens at all, which is exactly
        // the failure mode #102 found.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [NonAdminTokenTheory]
    [MemberData(nameof(DeveloperRoutes))]
    public async Task A_developer_route_with_a_valid_non_admin_token_is_200(string route)
    {
        using var client = DeployedEnvironment.CreateClient(DeployedEnvironment.NonAdminToken);

        using var response = await client.GetAsync(new Uri(route, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [AdminTokenTheory]
    [MemberData(nameof(AdminOnlyRoutes))]
    public async Task An_admin_route_with_the_admin_role_is_200(string route)
    {
        using var client = DeployedEnvironment.CreateClient(DeployedEnvironment.AdminToken);

        using var response = await client.GetAsync(new Uri(route, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// <c>GET /users/me</c> is the self-provisioning entry point: the first call for a principal
    /// creates the <c>User</c> row, the current period's <c>QuotaAllocation</c> and the APIM
    /// subscription. Every later call has to be idempotent and return the same identity.
    /// </summary>
    [AdminTokenFact]
    public async Task Users_me_self_provisions_and_is_idempotent()
    {
        using var client = DeployedEnvironment.CreateClient(DeployedEnvironment.AdminToken);

        var first = await GetUsersMeAsync(client);
        var second = await GetUsersMeAsync(client);

        Assert.True(first.TryGetProperty("userId", out var userId));
        Assert.True(userId.GetInt32() > 0);
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("email").GetString()));
        Assert.True(first.GetProperty("isActive").GetBoolean());

        // The allocation exists for the caller, on this period, tied to the same user.
        var quota = first.GetProperty("quota");
        Assert.Equal(userId.GetInt32(), quota.GetProperty("userId").GetInt32());
        Assert.True(quota.GetProperty("periodYear").GetInt32() >= 2026);
        Assert.InRange(quota.GetProperty("periodMonth").GetInt32(), 1, 12);

        // Idempotent: the second call is the same user, not a second row.
        Assert.Equal(userId.GetInt32(), second.GetProperty("userId").GetInt32());
        Assert.Equal(
            first.GetProperty("userUnique").GetString(),
            second.GetProperty("userUnique").GetString());
    }

    /// <summary>
    /// A provisioned key is only ever reported masked on this route — the plaintext lives behind
    /// <c>POST /keys/me/reveal</c>. A full key appearing here would leak it into every UI load.
    /// </summary>
    /// <remarks>This and the self-provisioning fact above write to the deployed environment; #262 makes that explicit rather than incidental.</remarks>
    [AdminTokenFact]
    public async Task Users_me_reports_the_api_key_masked_only()
    {
        using var client = DeployedEnvironment.CreateClient(DeployedEnvironment.AdminToken);

        var me = await GetUsersMeAsync(client);
        var apiKey = me.GetProperty("apiKey");

        if (!apiKey.GetProperty("isProvisioned").GetBoolean())
        {
            return;
        }

        var masked = apiKey.GetProperty("maskedKey").GetString();
        Assert.False(string.IsNullOrWhiteSpace(masked));
        Assert.Contains('•', masked!);
        Assert.True(masked!.Length < 32, $"maskedKey looks unmasked ({masked!.Length} characters).");
    }

    private static async Task<JsonElement> GetUsersMeAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("api/v1/users/me", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
