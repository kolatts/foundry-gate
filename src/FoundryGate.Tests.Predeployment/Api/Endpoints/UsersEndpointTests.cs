using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Config;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Keys;
using FoundryGate.Domain.Users.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// <c>/api/v1/users</c> through the real pipeline (auth filter, exception handler, controller, DI) with
/// the in-memory APIM and the local key protector: the auth matrix, first-login auto-provisioning
/// (#28), and the admin list / detail / quota / activate / deactivate surface (#29). One database for
/// the class, so every fixture uses unique markers rather than absolute counts.
/// </summary>
public class UsersEndpointTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string UsersPath = "/api/v1/users";

    // -- Auth matrix --------------------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_requests_are_401()
    {
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri($"{UsersPath}/me", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri(UsersPath, UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Non_admins_are_403_on_every_admin_route_but_can_read_their_own_profile()
    {
        var developer = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(developer.EntraObjectId);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(new Uri(UsersPath, UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(new Uri($"{UsersPath}/{developer.UserId}", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(new Uri($"{UsersPath}/{developer.UserId}/activate", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync(new Uri($"{UsersPath}/{developer.UserId}/deactivate", UriKind.Relative), null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PutAsJsonAsync(new Uri($"{UsersPath}/{developer.UserId}/quota", UriKind.Relative), new UpdateUserQuotaRequest())).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri($"{UsersPath}/me", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task A_deactivated_caller_gets_403_with_an_explanation_rather_than_a_profile()
    {
        var developer = await factory.SeedUserAsync(isActive: false);
        using var client = factory.CreateClientAs(developer.EntraObjectId);

        var response = await client.GetAsync(new Uri($"{UsersPath}/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsBody>(JsonOptions);
        Assert.NotNull(problem);
        Assert.Contains("deactivated", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // -- GET /users/me: first-login auto-provisioning (#28) -----------------------------------------

    [Fact]
    public async Task First_call_provisions_the_user_their_allocation_and_their_gateway_key()
    {
        var oid = Guid.NewGuid().ToString();
        using var client = factory.CreateClientAs(oid, name: "Nina Newcomer", email: "nina.newcomer@contoso.test");

        var profile = await client.GetFromJsonAsync<UserProfileResponse>(new Uri($"{UsersPath}/me", UriKind.Relative), JsonOptions);

        Assert.NotNull(profile);
        Assert.Equal("Nina Newcomer", profile.DisplayName);
        Assert.Equal("nina.newcomer@contoso.test", profile.Email);
        Assert.True(profile.IsActive);

        // Quota gauge: resolved for this month, nothing used yet.
        Assert.Equal(factory.TimeProvider.GetUtcNow().Year, profile.Quota.PeriodYear);
        Assert.Equal(0, profile.Quota.TokensUsed);
        Assert.Contains(profile.Quota.TierProductId, GatewayTiers.All, StringComparer.Ordinal);

        // Key: provisioned and masked, never plaintext.
        var subscriptionName = ApimSubscriptionNames.ForUser(profile.UserId);
        Assert.True(profile.ApiKey.IsProvisioned);
        Assert.NotNull(profile.ApiKey.MaskedKey);
        Assert.StartsWith("••••••••", profile.ApiKey.MaskedKey, StringComparison.Ordinal);
        Assert.True(factory.Apim.Contains(subscriptionName));
        Assert.DoesNotContain(factory.Apim.KeysOf(subscriptionName).PrimaryKey, profile.ApiKey.MaskedKey, StringComparison.Ordinal);

        // CLI config: the configured gateway origin and the paths the bicep actually serves.
        Assert.Equal(ApiTestFactory.GatewayUrl, profile.CliConfig.GatewayBaseUrl);
        Assert.Equal("/anthropic", profile.CliConfig.AnthropicBasePath);
        Assert.Equal("/openai/v1", profile.CliConfig.OpenAiBasePath);

        // Model aliases: the ones this developer's tier product actually permits, and only those
        // (#153). `opus` is configured for Unlimited only, and a new developer resolves to Standard —
        // listing it would promise a model the gateway answers with 403 model_not_permitted.
        Assert.Equal(GatewayTiers.Standard, profile.Quota.TierProductId);
        Assert.Equal(["gpt", "sonnet"], profile.CliConfig.ModelAliases.Select(alias => alias.Alias));
        Assert.Equal("claude-sonnet-4-5", profile.CliConfig.ModelAliases.Single(a => a.Alias == "sonnet").DeploymentName);
        Assert.Equal(ModelProviderType.Anthropic, profile.CliConfig.ModelAliases.Single(a => a.Alias == "sonnet").Provider);
        Assert.Equal(ModelProviderType.OpenAi, profile.CliConfig.ModelAliases.Single(a => a.Alias == "gpt").Provider);

        await using var dbContext = factory.CreateDbContext();
        var audit = await dbContext.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.UserProvisioned && a.TargetId == profile.UserId.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(profile.UserId, audit.ActorUserId);
    }

    [Fact]
    public async Task A_second_call_is_idempotent_and_refreshes_the_display_fields_from_the_token()
    {
        var oid = Guid.NewGuid().ToString();
        using var first = factory.CreateClientAs(oid, name: "Original Name", email: "original@contoso.test");
        var before = await first.GetFromJsonAsync<UserProfileResponse>(new Uri($"{UsersPath}/me", UriKind.Relative), JsonOptions);
        Assert.NotNull(before);

        using var renamed = factory.CreateClientAs(oid, name: "Married Name", email: "married@contoso.test");
        var after = await renamed.GetFromJsonAsync<UserProfileResponse>(new Uri($"{UsersPath}/me", UriKind.Relative), JsonOptions);

        Assert.NotNull(after);
        Assert.Equal(before.UserId, after.UserId);
        Assert.Equal("Married Name", after.DisplayName);
        Assert.Equal("married@contoso.test", after.Email);
        Assert.Equal(before.ApiKey.MaskedKey, after.ApiKey.MaskedKey);
        Assert.Equal(before.Quota.QuotaAllocationId, after.Quota.QuotaAllocationId);

        await using var dbContext = factory.CreateDbContext();
        Assert.Equal(1, await dbContext.Users.AsNoTracking().CountAsync(u => u.EntraObjectId == oid));
        Assert.Equal(1, await dbContext.QuotaAllocations.AsNoTracking().CountAsync(a => a.UserId == before.UserId));
        Assert.Equal(
            1,
            await dbContext.AuditLogs.AsNoTracking()
                .CountAsync(a => a.Action == AuditActions.UserProvisioned && a.TargetId == before.UserId.ToString()));
    }

    // -- Admin list and detail (#29) ----------------------------------------------------------------

    [Fact]
    public async Task List_pages_and_filters_by_search_and_active_state()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var alpha = await factory.SeedUserAsync(displayName: $"Zz{marker} Alpha", email: $"alpha.{marker}@contoso.test");
        var beta = await factory.SeedUserAsync(displayName: $"Zz{marker} Beta", email: $"beta.{marker}@contoso.test");
        var gone = await factory.SeedUserAsync(displayName: $"Zz{marker} Gone", email: $"gone.{marker}@contoso.test", isActive: false);
        using var admin = await CreateAdminAsync();

        var all = await admin.GetFromJsonAsync<PagedResult<UserResponse>>(new Uri($"{UsersPath}?search=Zz{marker}", UriKind.Relative), JsonOptions);
        Assert.NotNull(all);
        Assert.Equal(3, all.TotalCount);
        Assert.Equal([alpha.UserId, beta.UserId, gone.UserId], all.Items.Select(u => u.UserId));

        // Search also matches the email, and isActive narrows it.
        var active = await admin.GetFromJsonAsync<PagedResult<UserResponse>>(
            new Uri($"{UsersPath}?search=Zz{marker}&isActive=true", UriKind.Relative),
            JsonOptions);
        Assert.NotNull(active);
        Assert.Equal(2, active.TotalCount);
        Assert.DoesNotContain(gone.UserId, active.Items.Select(u => u.UserId));

        var byEmail = await admin.GetFromJsonAsync<PagedResult<UserResponse>>(
            new Uri($"{UsersPath}?search=beta.{marker}", UriKind.Relative),
            JsonOptions);
        Assert.NotNull(byEmail);
        Assert.Equal(beta.UserId, Assert.Single(byEmail.Items).UserId);

        // Paging: page 2 of a page size of 1 is the second row of the same ordering.
        var page2 = await admin.GetFromJsonAsync<PagedResult<UserResponse>>(
            new Uri($"{UsersPath}?search=Zz{marker}&page=2&pageSize=1", UriKind.Relative),
            JsonOptions);
        Assert.NotNull(page2);
        Assert.Equal(3, page2.TotalCount);
        Assert.Equal(beta.UserId, Assert.Single(page2.Items).UserId);
    }

    [Fact]
    public async Task Detail_carries_the_allocation_the_masked_key_and_404s_for_an_unknown_user()
    {
        var oid = Guid.NewGuid().ToString();
        using var developerClient = factory.CreateClientAs(oid, name: "Detail Subject", email: "detail@contoso.test");
        var profile = await developerClient.GetFromJsonAsync<UserProfileResponse>(new Uri($"{UsersPath}/me", UriKind.Relative), JsonOptions);
        Assert.NotNull(profile);
        using var admin = await CreateAdminAsync();

        var detail = await admin.GetFromJsonAsync<UserDetailResponse>(new Uri($"{UsersPath}/{profile.UserId}", UriKind.Relative), JsonOptions);

        Assert.NotNull(detail);
        Assert.Equal(profile.UserId, detail.User.UserId);
        Assert.True(detail.User.IsApiKeyProvisioned);
        Assert.NotNull(detail.CurrentAllocation);
        Assert.Equal(profile.Quota.QuotaAllocationId, detail.CurrentAllocation.QuotaAllocationId);
        Assert.Equal(profile.ApiKey.MaskedKey, detail.ApiKey.MaskedKey);
        Assert.Empty(detail.Groups);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(new Uri($"{UsersPath}/987654", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Detail_of_a_user_who_has_never_logged_in_reports_no_allocation_and_no_key()
    {
        var developer = await factory.SeedUserAsync(displayName: "Imported Only");
        using var admin = await CreateAdminAsync();

        var detail = await admin.GetFromJsonAsync<UserDetailResponse>(new Uri($"{UsersPath}/{developer.UserId}", UriKind.Relative), JsonOptions);

        Assert.NotNull(detail);
        Assert.Null(detail.CurrentAllocation);
        Assert.False(detail.ApiKey.IsProvisioned);
        Assert.Null(detail.ApiKey.MaskedKey);
    }

    // -- PUT /users/{id}/quota ----------------------------------------------------------------------

    [Fact]
    public async Task Setting_a_tier_quota_moves_the_APIM_subscription_and_audits_before_and_after()
    {
        var oid = Guid.NewGuid().ToString();
        using var developerClient = factory.CreateClientAs(oid, name: "Tier Mover", email: "tier@contoso.test");
        var profile = await developerClient.GetFromJsonAsync<UserProfileResponse>(new Uri($"{UsersPath}/me", UriKind.Relative), JsonOptions);
        Assert.NotNull(profile);
        var subscriptionName = ApimSubscriptionNames.ForUser(profile.UserId);
        var keysBefore = factory.Apim.KeysOf(subscriptionName);
        using var admin = await CreateAdminAsync();

        var response = await admin.PutAsJsonAsync(
            new Uri($"{UsersPath}/{profile.UserId}/quota", UriKind.Relative),
            new UpdateUserQuotaRequest { MonthlyTokenQuota = PowerCap });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<UserResponse>(JsonOptions);
        Assert.NotNull(updated);
        Assert.Equal(PowerCap, updated.MonthlyTokenQuota);
        Assert.False(updated.IsUnlimited);

        // #118: the gateway is where a budget is enforced, so the subscription really moved — and the
        // developer's key survived the move.
        Assert.Equal(GatewayTiers.Power, factory.Apim.ProductOf(subscriptionName));
        Assert.Equal(keysBefore.PrimaryKey, factory.Apim.KeysOf(subscriptionName).PrimaryKey);

        await using var dbContext = factory.CreateDbContext();
        var audit = await dbContext.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.UserQuotaChanged && a.TargetId == profile.UserId.ToString(CultureInfo.InvariantCulture));
        Assert.Contains("\"before\":", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"after\":", audit.Details, StringComparison.Ordinal);
        Assert.Contains($"\"tierProductId\":\"{GatewayTiers.Power}\"", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setting_unlimited_clears_the_numeric_override()
    {
        var developer = await factory.SeedUserAsync(displayName: "Unlimited Subject");
        using var admin = await CreateAdminAsync();

        var response = await admin.PutAsJsonAsync(
            new Uri($"{UsersPath}/{developer.UserId}/quota", UriKind.Relative),
            new UpdateUserQuotaRequest { IsUnlimited = true, MonthlyTokenQuota = PowerCap });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<UserResponse>(JsonOptions);
        Assert.NotNull(updated);
        Assert.True(updated.IsUnlimited);
        Assert.Null(updated.MonthlyTokenQuota);
    }

    [Fact]
    public async Task A_quota_that_is_not_a_configured_tier_is_400_listing_the_allowed_values()
    {
        var developer = await factory.SeedUserAsync(displayName: "Bad Quota Subject");
        using var admin = await CreateAdminAsync();

        var response = await admin.PutAsJsonAsync(
            new Uri($"{UsersPath}/{developer.UserId}/quota", UriKind.Relative),
            new UpdateUserQuotaRequest { MonthlyTokenQuota = 1234 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsBody>(JsonOptions);
        Assert.NotNull(problem);
        Assert.Contains("not a configured budget tier", problem.Detail, StringComparison.Ordinal);
        Assert.Contains(GatewayTiers.Standard, problem.Detail, StringComparison.Ordinal);
        Assert.Contains(GatewayTiers.Power, problem.Detail, StringComparison.Ordinal);

        await using var dbContext = factory.CreateDbContext();
        var unchanged = await dbContext.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId);
        Assert.Null(unchanged.MonthlyTokenQuota);
    }

    [Fact]
    public async Task Setting_a_quota_for_an_unknown_user_is_404()
    {
        using var admin = await CreateAdminAsync();

        var response = await admin.PutAsJsonAsync(
            new Uri($"{UsersPath}/987654/quota", UriKind.Relative),
            new UpdateUserQuotaRequest { IsUnlimited = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -- activate / deactivate ----------------------------------------------------------------------

    [Fact]
    public async Task Deactivate_deletes_the_subscription_hard_stops_the_allocation_and_then_activate_restores_a_key()
    {
        var oid = Guid.NewGuid().ToString();
        using var developerClient = factory.CreateClientAs(oid, name: "Round Trip", email: "roundtrip@contoso.test");
        var profile = await developerClient.GetFromJsonAsync<UserProfileResponse>(new Uri($"{UsersPath}/me", UriKind.Relative), JsonOptions);
        Assert.NotNull(profile);
        var subscriptionName = ApimSubscriptionNames.ForUser(profile.UserId);
        using var admin = await CreateAdminAsync();

        var deactivated = await admin.PostAsync(new Uri($"{UsersPath}/{profile.UserId}/deactivate", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        var afterDeactivate = await deactivated.Content.ReadFromJsonAsync<UserResponse>(JsonOptions);
        Assert.NotNull(afterDeactivate);
        Assert.False(afterDeactivate.IsActive);
        Assert.False(afterDeactivate.IsApiKeyProvisioned);
        Assert.False(factory.Apim.Contains(subscriptionName));

        await using (var dbContext = factory.CreateDbContext())
        {
            var allocation = await dbContext.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == profile.UserId);
            Assert.True(allocation.IsHardStopped);
            _ = await dbContext.AuditLogs.AsNoTracking()
                .SingleAsync(a => a.Action == AuditActions.UserDeactivated && a.TargetId == profile.UserId.ToString(CultureInfo.InvariantCulture));
        }

        // The developer is now locked out of their own profile.
        Assert.Equal(HttpStatusCode.Forbidden, (await developerClient.GetAsync(new Uri($"{UsersPath}/me", UriKind.Relative))).StatusCode);

        var activated = await admin.PostAsync(new Uri($"{UsersPath}/{profile.UserId}/activate", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.OK, activated.StatusCode);
        var afterActivate = await activated.Content.ReadFromJsonAsync<UserResponse>(JsonOptions);
        Assert.NotNull(afterActivate);
        Assert.True(afterActivate.IsActive);
        Assert.True(afterActivate.IsApiKeyProvisioned);
        Assert.True(factory.Apim.Contains(subscriptionName));

        // And the key is never handed to the admin — the developer reveals their own.
        Assert.DoesNotContain(
            factory.Apim.KeysOf(subscriptionName).PrimaryKey,
            await activated.Content.ReadAsStringAsync(),
            StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, (await developerClient.GetAsync(new Uri($"{UsersPath}/me", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Activating_an_active_user_and_deactivating_an_inactive_one_are_both_409()
    {
        var active = await factory.SeedUserAsync(displayName: "Already Active");
        var inactive = await factory.SeedUserAsync(displayName: "Already Inactive", isActive: false);
        using var admin = await CreateAdminAsync();

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await admin.PostAsync(new Uri($"{UsersPath}/{active.UserId}/activate", UriKind.Relative), null)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await admin.PostAsync(new Uri($"{UsersPath}/{inactive.UserId}/deactivate", UriKind.Relative), null)).StatusCode);
    }

    [Fact]
    public async Task Activate_and_deactivate_of_an_unknown_user_are_404()
    {
        using var admin = await CreateAdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync(new Uri($"{UsersPath}/987654/activate", UriKind.Relative), null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync(new Uri($"{UsersPath}/987654/deactivate", UriKind.Relative), null)).StatusCode);
    }

    /// <summary>The Power tier's cap as shipped in <c>appsettings.json</c> (mirrored by <c>infra/main.bicep</c>).</summary>
    private const long PowerCap = 20_000_000;

    /// <summary>An admin client whose <c>User</c> row exists — every audited admin action needs an actor.</summary>
    private async Task<HttpClient> CreateAdminAsync()
    {
        var admin = await factory.SeedUserAsync(displayName: "Ada Admin");
        return factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
    }

    /// <summary>Just the fields these tests assert on from a ProblemDetails body.</summary>
    private sealed record ProblemDetailsBody(string Title, int Status, string Detail);
}
