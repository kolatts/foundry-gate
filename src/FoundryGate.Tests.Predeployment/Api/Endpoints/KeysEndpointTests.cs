using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Keys;
using FoundryGate.Domain.Keys.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// <c>/api/v1/keys</c> through the real pipeline (auth filter, exception handler, controller, DI) with
/// the in-memory APIM and the local key protector: the auth matrix (401/403/200/204/404/409/400) and
/// the user-visible key semantics — plaintext once, masked afterwards, rotation replaces, revocation
/// is key-only.
/// </summary>
public class KeysEndpointTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string KeysPath = "/api/v1/keys";

    [Fact]
    public async Task Anonymous_request_returns_401()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri($"{KeysPath}/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_caller_without_a_User_row_returns_403_on_me_routes()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(new Uri($"{KeysPath}/me", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(new Uri($"{KeysPath}/me/reveal", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(new Uri($"{KeysPath}/me/rotate", UriKind.Relative), null)).StatusCode);
    }

    [Fact]
    public async Task Non_admin_returns_403_on_admin_routes()
    {
        var developer = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(developer.EntraObjectId, isAdmin: false);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(new Uri($"{KeysPath}/{developer.UserId}/provision", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(new Uri($"{KeysPath}/{developer.UserId}/rotate", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync(new Uri($"{KeysPath}/{developer.UserId}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Admin_without_a_User_row_returns_403_on_provision_because_the_audit_row_needs_an_actor()
    {
        var developer = await factory.SeedUserAsync();
        using var admin = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var response = await admin.PostAsync(new Uri($"{KeysPath}/{developer.UserId}/provision", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(factory.Apim.Contains(ApimSubscriptionNames.ForUser(developer.UserId)));
    }

    [Fact]
    public async Task Unprovisioned_developer_sees_not_provisioned_and_404_on_reveal_and_rotate()
    {
        var developer = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(developer.EntraObjectId);

        var response = await client.GetAsync(new Uri($"{KeysPath}/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var key = await response.Content.ReadFromJsonAsync<ApiKeyResponse>(JsonOptions);
        Assert.NotNull(key);
        Assert.False(key.IsProvisioned);
        Assert.Null(key.MaskedKey);
        Assert.Null(key.ApimSubscriptionId);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(new Uri($"{KeysPath}/me/reveal", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync(new Uri($"{KeysPath}/me/rotate", UriKind.Relative), null)).StatusCode);
    }

    [Fact]
    public async Task Admin_provisions_a_key_default_tier_returns_plaintext_once_and_the_developer_then_sees_it_masked()
    {
        var (adminUser, admin) = await CreateAdminAsync();
        var developer = await factory.SeedUserAsync(email: "dev.two@contoso.test");
        using var developerClient = factory.CreateClientAs(developer.EntraObjectId);
        var name = ApimSubscriptionNames.ForUser(developer.UserId);

        var response = await admin.PostAsync(new Uri($"{KeysPath}/{developer.UserId}/provision", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var reveal = await response.Content.ReadFromJsonAsync<ApiKeyRevealResponse>(JsonOptions);
        Assert.NotNull(reveal);
        Assert.Equal(factory.Apim.KeysOf(name).PrimaryKey, reveal.PlaintextKey);
        Assert.Equal(GatewayTiers.Default, factory.Apim.ProductOf(name));
        Assert.Equal(factory.TimeProvider.GetUtcNow(), reveal.IssuedDate);

        // The developer's own view is masked and never returns the plaintext.
        var mine = await developerClient.GetFromJsonAsync<ApiKeyResponse>(new Uri($"{KeysPath}/me", UriKind.Relative), JsonOptions);
        Assert.NotNull(mine);
        Assert.True(mine.IsProvisioned);
        Assert.Equal(reveal.MaskedKey, mine.MaskedKey);
        Assert.Equal(reveal.ApimSubscriptionId, mine.ApimSubscriptionId);
        Assert.Equal("••••••••" + reveal.PlaintextKey[^4..], mine.MaskedKey);

        // Stored encrypted; audited to the admin.
        await using var dbContext = factory.CreateDbContext();
        var saved = await dbContext.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId);
        Assert.StartsWith("dp1:", saved.ApimSubscriptionKey, StringComparison.Ordinal);
        Assert.DoesNotContain(reveal.PlaintextKey, saved.ApimSubscriptionKey, StringComparison.OrdinalIgnoreCase);
        var audit = await dbContext.AuditLogs.AsNoTracking().SingleAsync(a =>
            a.Action == AuditActions.KeyProvisioned && a.TargetId == developer.UserId.ToString());
        Assert.Equal(adminUser.UserId, audit.ActorUserId);
    }

    [Fact]
    public async Task Provision_mints_under_the_users_resolved_tier_not_a_caller_supplied_one()
    {
        // #156 review: ?tier= is gone. A budget IS a tier, so the product comes from the user's resolved
        // quota — an admin who wants a different one sets the quota, which moves the gateway too.
        var (_, admin) = await CreateAdminAsync();
        var developer = await factory.SeedUserAsync(configure: user => user.MonthlyTokenQuota = PowerCap);

        // A stale ?tier= from an old client is ignored, not honoured and not a 400.
        var response = await admin.PostAsync(new Uri($"{KeysPath}/{developer.UserId}/provision?tier=standard", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(GatewayTiers.Power, factory.Apim.ProductOf(ApimSubscriptionNames.ForUser(developer.UserId)));

        await using var dbContext = factory.CreateDbContext();
        var allocation = await dbContext.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == developer.UserId);
        Assert.Equal(GatewayTiers.Power, allocation.TierProductId);
    }

    /// <summary>The Power tier's cap as shipped in <c>appsettings.json</c> (mirrored by <c>infra/main.bicep</c>).</summary>
    private const long PowerCap = 20_000_000;

    [Fact]
    public async Task Second_provision_returns_409_and_unknown_or_inactive_users_return_404_or_409()
    {
        var (_, admin) = await CreateAdminAsync();
        var developer = await factory.SeedUserAsync();
        var inactive = await factory.SeedUserAsync(isActive: false);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync(new Uri($"{KeysPath}/{developer.UserId}/provision", UriKind.Relative), null)).StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync(new Uri($"{KeysPath}/{developer.UserId}/provision", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync(new Uri($"{KeysPath}/{inactive.UserId}/provision", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync(new Uri($"{KeysPath}/999999/provision", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync(new Uri($"{KeysPath}/999999/rotate", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync(new Uri($"{KeysPath}/999999", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Developer_reveals_the_same_key_and_rotation_replaces_it()
    {
        var (_, admin) = await CreateAdminAsync();
        var developer = await factory.SeedUserAsync();
        using var developerClient = factory.CreateClientAs(developer.EntraObjectId);
        var name = ApimSubscriptionNames.ForUser(developer.UserId);
        var provisioned = await PostAsync<ApiKeyRevealResponse>(admin, $"{KeysPath}/{developer.UserId}/provision");
        var secondaryBefore = factory.Apim.KeysOf(name).SecondaryKey;

        var revealed = await PostAsync<ApiKeyRevealResponse>(developerClient, $"{KeysPath}/me/reveal");
        var rotated = await PostAsync<ApiKeyRevealResponse>(developerClient, $"{KeysPath}/me/rotate");

        Assert.Equal(provisioned.PlaintextKey, revealed.PlaintextKey);
        Assert.NotEqual(provisioned.PlaintextKey, rotated.PlaintextKey);
        Assert.Equal(factory.Apim.KeysOf(name).PrimaryKey, rotated.PlaintextKey);
        Assert.NotEqual(secondaryBefore, factory.Apim.KeysOf(name).SecondaryKey); // #117: both keys rotate

        var afterRotate = await PostAsync<ApiKeyRevealResponse>(developerClient, $"{KeysPath}/me/reveal");
        Assert.Equal(rotated.PlaintextKey, afterRotate.PlaintextKey);

        await using var dbContext = factory.CreateDbContext();
        var targetId = developer.UserId.ToString(CultureInfo.InvariantCulture);
        var actions = await dbContext.AuditLogs.AsNoTracking()
            .Where(a => a.TargetType == AuditTargetTypes.ApiKey && a.TargetId == targetId)
            .OrderBy(a => a.AuditLogId)
            .Select(a => new { a.Action, a.ActorUserId })
            .ToListAsync();
        Assert.Equal([AuditActions.KeyProvisioned, AuditActions.KeyRevealed, AuditActions.KeyRotated, AuditActions.KeyRevealed], actions.Select(a => a.Action));
        Assert.All(actions.Skip(1), a => Assert.Equal(developer.UserId, a.ActorUserId));
    }

    [Fact]
    public async Task Admin_rotates_any_users_key()
    {
        var (_, admin) = await CreateAdminAsync();
        var developer = await factory.SeedUserAsync();
        var provisioned = await PostAsync<ApiKeyRevealResponse>(admin, $"{KeysPath}/{developer.UserId}/provision");

        var rotated = await PostAsync<ApiKeyRevealResponse>(admin, $"{KeysPath}/{developer.UserId}/rotate");

        Assert.NotEqual(provisioned.PlaintextKey, rotated.PlaintextKey);
        Assert.Equal(provisioned.ApimSubscriptionId, rotated.ApimSubscriptionId);
    }

    [Fact]
    public async Task Delete_revokes_the_key_only_the_user_stays_active_and_a_repeat_is_still_204()
    {
        var (_, admin) = await CreateAdminAsync();
        var developer = await factory.SeedUserAsync();
        using var developerClient = factory.CreateClientAs(developer.EntraObjectId);
        var name = ApimSubscriptionNames.ForUser(developer.UserId);
        _ = await PostAsync<ApiKeyRevealResponse>(admin, $"{KeysPath}/{developer.UserId}/provision");

        var response = await admin.DeleteAsync(new Uri($"{KeysPath}/{developer.UserId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(factory.Apim.Contains(name));
        await using var dbContext = factory.CreateDbContext();
        var saved = await dbContext.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId);
        Assert.True(saved.IsActive);
        Assert.Equal(string.Empty, saved.ApimSubscriptionId);
        Assert.Equal(string.Empty, saved.ApimSubscriptionKey);
        Assert.Equal(string.Empty, saved.ApimSubscriptionKeyHint);
        Assert.Null(saved.ApimKeyIssuedDate);
        Assert.Single(await dbContext.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.KeyRevoked && a.TargetId == developer.UserId.ToString()).ToListAsync());

        var mine = await developerClient.GetFromJsonAsync<ApiKeyResponse>(new Uri($"{KeysPath}/me", UriKind.Relative), JsonOptions);
        Assert.NotNull(mine);
        Assert.False(mine.IsProvisioned);

        // Idempotent: no key → still 204, no second audit row.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(new Uri($"{KeysPath}/{developer.UserId}", UriKind.Relative))).StatusCode);
        Assert.Single(await dbContext.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.KeyRevoked && a.TargetId == developer.UserId.ToString()).ToListAsync());

        // ...and the user can be provisioned again.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync(new Uri($"{KeysPath}/{developer.UserId}/provision", UriKind.Relative), null)).StatusCode);
    }

    private async Task<(FoundryGate.Data.Entities.User User, HttpClient Client)> CreateAdminAsync()
    {
        var adminUser = await factory.SeedUserAsync(displayName: "Ada Admin");
        return (adminUser, factory.CreateClientAs(adminUser.EntraObjectId, isAdmin: true));
    }

    private static async Task<T> PostAsync<T>(HttpClient client, string path)
    {
        var response = await client.PostAsync(new Uri(path, UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<T>(JsonOptions);
        Assert.NotNull(body);
        return body;
    }
}
