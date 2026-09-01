using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Quota.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// <c>/api/v1/quota</c> through the real pipeline (#33): the auth contract per route (anonymous 401,
/// non-admin 403 on admin routes, 403 for an unprovisioned caller on <c>/me</c>), the paged envelope,
/// <c>/me</c> auto-creation, admin 404s, and the reset's idempotency across two HTTP calls. One
/// database per class — assertions use rows seeded by the test itself, never absolute counts.
/// </summary>
public class QuotaEndpointTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string AllocationsPath = "/api/v1/quota/allocations";
    private const string MePath = "/api/v1/quota/allocations/me";
    private const string ResetPath = "/api/v1/quota/reset";

    // -- Auth contract --

    [Theory]
    [InlineData("GET", AllocationsPath)]
    [InlineData("GET", MePath)]
    [InlineData("GET", AllocationsPath + "/1")]
    [InlineData("POST", ResetPath)]
    public async Task Anonymous_request_returns_401(string method, string path)
    {
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", AllocationsPath)]
    [InlineData("GET", AllocationsPath + "/1")]
    [InlineData("POST", ResetPath)]
    public async Task Authenticated_non_admin_returns_403_on_admin_routes(string method, string path)
    {
        var dev = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(dev.EntraObjectId, isAdmin: false);

        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Me_for_an_authenticated_caller_with_no_User_row_returns_403_pointing_at_users_me()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString());

        var response = await client.GetAsync(new Uri(MePath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Contains("GET /users/me", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    // -- GET /quota/allocations --

    [Fact]
    public async Task Admin_list_returns_200_with_the_paged_envelope_and_default_paging()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var response = await client.GetAsync(new Uri(AllocationsPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<QuotaAllocationResponse>>(JsonOptions);
        Assert.NotNull(page);
        Assert.Equal(1, page.Page);
        Assert.Equal(PagedRequest.DefaultPageSize, page.PageSize);
    }

    [Fact]
    public async Task Admin_list_honours_paging_and_includes_the_seeded_users_display_name_and_email()
    {
        var marker = Guid.NewGuid().ToString("N");
        var dev = await factory.SeedUserAsync(displayName: $"Zz {marker}", email: $"{marker}@contoso.test");
        await SeedAllocationAsync(dev.UserId, allocated: 1_000, tokensUsed: 500);
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var response = await client.GetAsync(new Uri(AllocationsPath + "?page=1&pageSize=2", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<QuotaAllocationResponse>>(JsonOptions);
        Assert.NotNull(page);
        Assert.Equal(2, page.PageSize);
        Assert.True(page.Items.Count <= 2);
        Assert.True(page.TotalCount >= 1);

        // Find our row wherever it paged to (shared database; other tests seed too).
        var all = await ReadAllPagesAsync(client);
        var mine = Assert.Single(all, i => i.UserId == dev.UserId);
        Assert.Equal($"Zz {marker}", mine.UserDisplayName);
        Assert.Equal($"{marker}@contoso.test", mine.UserEmail);
        Assert.Equal(50d, mine.PercentUsed);
        Assert.Equal(GatewayTiers.Standard, mine.TierProductId);
    }

    // -- GET /quota/allocations/me --

    [Fact]
    public async Task Me_auto_creates_the_callers_current_period_allocation_and_is_stable_across_calls()
    {
        var oid = Guid.NewGuid().ToString();
        var me = await factory.SeedUserAsync(entraObjectId: oid, displayName: "Grace Hopper", configure: u => u.MonthlyTokenQuota = 3_000_000);
        using var client = factory.CreateClientAs(oid);
        var period = BillingPeriod.Current(factory.TimeProvider);

        var first = await client.GetFromJsonAsync<QuotaAllocationResponse>(new Uri(MePath, UriKind.Relative), JsonOptions);
        var second = await client.GetFromJsonAsync<QuotaAllocationResponse>(new Uri(MePath, UriKind.Relative), JsonOptions);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.QuotaAllocationId, second.QuotaAllocationId);
        Assert.Equal(me.UserId, first.UserId);
        Assert.Equal("Grace Hopper", first.UserDisplayName);
        Assert.Equal((period.Year, period.Month), (first.PeriodYear, first.PeriodMonth));
        Assert.Equal(3_000_000, first.AllocatedTokens);
        Assert.Equal(QuotaLevelType.UserOverride, first.ResolvedLevelType);
        Assert.Equal(GatewayTiers.Standard, first.TierProductId);
        Assert.False(first.IsGatewayCapped);
        Assert.Null(first.ResetDate);

        await using var dbContext = factory.CreateDbContext();
        Assert.Equal(1, await dbContext.QuotaAllocations.CountAsync(a => a.UserId == me.UserId));
    }

    [Fact]
    public async Task Me_reflects_group_level_resolution_and_the_gateway_capped_flag()
    {
        var oid = Guid.NewGuid().ToString();
        var me = await factory.SeedUserAsync(entraObjectId: oid);
        await using (var dbContext = factory.CreateDbContext())
        {
            var group = new Group { Name = $"g-{Guid.NewGuid():N}", MonthlyTokenQuota = 50_000_000 }; // above the 20M Power cap
            dbContext.Groups.Add(group);
            await dbContext.SaveChangesAsync();
            dbContext.GroupMembers.Add(new GroupMember { GroupId = group.GroupId, UserId = me.UserId });
            await dbContext.SaveChangesAsync();
        }

        using var client = factory.CreateClientAs(oid);
        var result = await client.GetFromJsonAsync<QuotaAllocationResponse>(new Uri(MePath, UriKind.Relative), JsonOptions);

        Assert.NotNull(result);
        Assert.Equal(QuotaLevelType.GroupMax, result.ResolvedLevelType);
        Assert.Equal(50_000_000, result.AllocatedTokens);
        Assert.Equal(GatewayTiers.Power, result.TierProductId);
        Assert.True(result.IsGatewayCapped);
    }

    // -- GET /quota/allocations/{userId} --

    [Fact]
    public async Task Admin_get_for_unknown_user_returns_404()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var response = await client.GetAsync(new Uri($"{AllocationsPath}/{int.MaxValue}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Admin_get_for_a_user_without_an_allocation_this_period_returns_404_and_creates_nothing()
    {
        var dev = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var response = await client.GetAsync(new Uri($"{AllocationsPath}/{dev.UserId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Contains("POST /quota/reset", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        await using var dbContext = factory.CreateDbContext();
        Assert.False(await dbContext.QuotaAllocations.AnyAsync(a => a.UserId == dev.UserId));
    }

    [Fact]
    public async Task Admin_get_for_a_user_with_an_allocation_returns_200()
    {
        var dev = await factory.SeedUserAsync(displayName: "Linus");
        await SeedAllocationAsync(dev.UserId, allocated: null, tokensUsed: 42);
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var result = await client.GetFromJsonAsync<QuotaAllocationResponse>(new Uri($"{AllocationsPath}/{dev.UserId}", UriKind.Relative), JsonOptions);

        Assert.NotNull(result);
        Assert.Equal(dev.UserId, result.UserId);
        Assert.Equal("Linus", result.UserDisplayName);
        Assert.True(result.IsUnlimited);
        Assert.Null(result.PercentUsed);
        Assert.Equal(42, result.TokensUsed);
    }

    // -- POST /quota/reset --

    [Fact]
    public async Task Admin_reset_by_an_unprovisioned_admin_returns_403_the_audit_row_needs_an_actor()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var response = await client.PostAsync(new Uri(ResetPath, UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_reset_is_idempotent_over_two_calls_and_preserves_TokensUsed()
    {
        var adminOid = Guid.NewGuid().ToString();
        var admin = await factory.SeedUserAsync(entraObjectId: adminOid);
        var dev = await factory.SeedUserAsync(configure: u => u.MonthlyTokenQuota = 1_000);
        await SeedAllocationAsync(dev.UserId, allocated: 500, tokensUsed: 321);
        using var client = factory.CreateClientAs(adminOid, isAdmin: true);
        var period = BillingPeriod.Current(factory.TimeProvider);

        var first = await PostResetAsync(client);
        var rowsAfterFirst = await CountPeriodRowsAsync(period);
        var second = await PostResetAsync(client);
        var rowsAfterSecond = await CountPeriodRowsAsync(period);

        Assert.Equal((period.Year, period.Month), (first.PeriodYear, first.PeriodMonth));
        Assert.Equal(factory.TimeProvider.GetUtcNow(), first.ResetDate);
        Assert.True(first.UsersResetCount >= 2); // at least admin + dev; other tests' active users too
        Assert.Equal(first.UsersResetCount, second.UsersResetCount);
        Assert.Equal(rowsAfterFirst, rowsAfterSecond);

        await using var dbContext = factory.CreateDbContext();
        var devRow = await dbContext.QuotaAllocations.SingleAsync(a => a.UserId == dev.UserId && a.PeriodYear == period.Year && a.PeriodMonth == period.Month);
        Assert.Equal(321, devRow.TokensUsed);
        Assert.Equal(1_000, devRow.AllocatedTokens); // re-resolved from the user override
        Assert.False(devRow.IsHardStopped);
        Assert.Equal(factory.TimeProvider.GetUtcNow(), devRow.ResetDate);
        Assert.True(await dbContext.QuotaAllocations.AnyAsync(a => a.UserId == admin.UserId && a.PeriodYear == period.Year && a.PeriodMonth == period.Month));
        Assert.True(await dbContext.AuditLogs.CountAsync(a => a.Action == AuditActions.QuotaAllocationReset && a.ActorUserId == admin.UserId) >= 2);
    }

    // -- Helpers --

    private async Task SeedAllocationAsync(int userId, long? allocated, long tokensUsed)
    {
        var period = BillingPeriod.Current(factory.TimeProvider);
        await using var dbContext = factory.CreateDbContext();
        dbContext.QuotaAllocations.Add(new QuotaAllocation
        {
            UserId = userId,
            PeriodYear = period.Year,
            PeriodMonth = period.Month,
            AllocatedTokens = allocated,
            TokensUsed = tokensUsed,
            ResolvedLevelType = allocated is null ? QuotaLevelType.UserUnlimited : QuotaLevelType.UserOverride,
            TierProductId = allocated is null ? GatewayTiers.Unlimited : GatewayTiers.Standard,
        });
        await dbContext.SaveChangesAsync();
    }

    private async Task<int> CountPeriodRowsAsync(BillingPeriod period)
    {
        await using var dbContext = factory.CreateDbContext();
        return await dbContext.QuotaAllocations.CountAsync(a => a.PeriodYear == period.Year && a.PeriodMonth == period.Month);
    }

    private static async Task<QuotaResetResult> PostResetAsync(HttpClient client)
    {
        var response = await client.PostAsync(new Uri(ResetPath, UriKind.Relative), content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<QuotaResetResult>(JsonOptions);
        Assert.NotNull(result);
        return result;
    }

    private static async Task<List<QuotaAllocationResponse>> ReadAllPagesAsync(HttpClient client)
    {
        var items = new List<QuotaAllocationResponse>();
        for (var page = 1; ; page++)
        {
            var result = await client.GetFromJsonAsync<PagedResult<QuotaAllocationResponse>>(
                new Uri($"{AllocationsPath}?page={page}&pageSize={PagedRequest.MaxPageSize}", UriKind.Relative), JsonOptions);
            Assert.NotNull(result);
            items.AddRange(result.Items);
            if (page >= result.TotalPages)
            {
                return items;
            }
        }
    }
}
