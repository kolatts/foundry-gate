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
    private const string TiersPath = "/api/v1/quota/tiers";

    // -- Auth contract --

    [Theory]
    [InlineData("GET", AllocationsPath)]
    [InlineData("GET", MePath)]
    [InlineData("GET", AllocationsPath + "/1")]
    [InlineData("GET", TiersPath)]
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

    [Fact]
    public async Task Me_for_a_deactivated_user_returns_403_and_creates_nothing()
    {
        var oid = Guid.NewGuid().ToString();
        var me = await factory.SeedUserAsync(entraObjectId: oid, isActive: false);
        using var client = factory.CreateClientAs(oid);

        var response = await client.GetAsync(new Uri(MePath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Contains("deactivated", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        await using var dbContext = factory.CreateDbContext();
        Assert.False(await dbContext.QuotaAllocations.AnyAsync(a => a.UserId == me.UserId));
    }

    // -- GET /quota/tiers --

    [Fact]
    public async Task Tiers_are_readable_by_any_authenticated_user_and_mirror_the_shipped_appsettings()
    {
        var dev = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(dev.EntraObjectId, isAdmin: false);

        var tiers = await client.GetFromJsonAsync<List<QuotaTierResponse>>(new Uri(TiersPath, UriKind.Relative), JsonOptions);

        Assert.NotNull(tiers);
        Assert.Equal(
            [
                new QuotaTierResponse(GatewayTiers.Standard, "Standard", 5_000_000, false),
                new QuotaTierResponse(GatewayTiers.Power, "Power", 20_000_000, false),
                new QuotaTierResponse(GatewayTiers.Unlimited, "Unlimited", null, true),
            ],
            tiers);
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
        await SeedAllocationAsync(dev.UserId, allocated: 5_000_000, tokensUsed: 2_500_000);
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
        Assert.False(mine.IsGatewayCapped);
    }

    // -- GET /quota/allocations filters (#208) --

    [Fact]
    public async Task Admin_list_filtered_by_isHardStopped_returns_only_the_hard_stopped_rows()
    {
        var stopped = await factory.SeedUserAsync(displayName: $"Stopped {Guid.NewGuid():N}");
        var running = await factory.SeedUserAsync(displayName: $"Running {Guid.NewGuid():N}");
        await SeedAllocationAsync(stopped.UserId, allocated: 5_000_000, tokensUsed: 1, isHardStopped: true);
        await SeedAllocationAsync(running.UserId, allocated: 5_000_000, tokensUsed: 1);
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var onlyStopped = await ReadFilteredAsync(client, "isHardStopped=true");
        Assert.Contains(onlyStopped, a => a.UserId == stopped.UserId);
        Assert.DoesNotContain(onlyStopped, a => a.UserId == running.UserId);
        Assert.All(onlyStopped, a => Assert.True(a.IsHardStopped));

        // false is a filter of its own, not "no filter" — the other half has to be reachable too.
        var onlyRunning = await ReadFilteredAsync(client, "isHardStopped=false");
        Assert.Contains(onlyRunning, a => a.UserId == running.UserId);
        Assert.DoesNotContain(onlyRunning, a => a.UserId == stopped.UserId);
    }

    [Fact]
    public async Task Admin_list_filtered_by_isOverBudget_uses_the_same_rule_the_dashboard_counts_with()
    {
        // Exactly at the cap counts as over: the gateway's token-quota policy refuses the request
        // that would cross it, so "reached it" is already "cut off" — DashboardService uses >= for
        // the count, and a card that links to a list applying a different rule would be a lie.
        var atCap = await factory.SeedUserAsync(displayName: $"AtCap {Guid.NewGuid():N}");
        var under = await factory.SeedUserAsync(displayName: $"Under {Guid.NewGuid():N}");
        var unlimited = await factory.SeedUserAsync(displayName: $"Unlimited {Guid.NewGuid():N}");
        await SeedAllocationAsync(atCap.UserId, allocated: 5_000_000, tokensUsed: 5_000_000);
        await SeedAllocationAsync(under.UserId, allocated: 5_000_000, tokensUsed: 4_999_999);
        await SeedAllocationAsync(unlimited.UserId, allocated: null, tokensUsed: 99_000_000);
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var over = await ReadFilteredAsync(client, "isOverBudget=true");
        Assert.Contains(over, a => a.UserId == atCap.UserId);
        Assert.DoesNotContain(over, a => a.UserId == under.UserId);
        Assert.DoesNotContain(over, a => a.UserId == unlimited.UserId);

        // An unlimited allocation is never over budget, so it belongs to the "false" half.
        var withinBudget = await ReadFilteredAsync(client, "isOverBudget=false");
        Assert.Contains(withinBudget, a => a.UserId == under.UserId);
        Assert.Contains(withinBudget, a => a.UserId == unlimited.UserId);
        Assert.DoesNotContain(withinBudget, a => a.UserId == atCap.UserId);
    }

    [Theory]
    [InlineData("power")]
    [InlineData("Power")]
    [InlineData("POWER")]
    public async Task Admin_list_filtered_by_tier_matches_a_configured_tier_whatever_its_casing(string tier)
    {
        var powerUser = await factory.SeedUserAsync(displayName: $"Power {Guid.NewGuid():N}");
        var standardUser = await factory.SeedUserAsync(displayName: $"Standard {Guid.NewGuid():N}");
        await SeedAllocationAsync(powerUser.UserId, allocated: 20_000_000, tokensUsed: 0, tierProductId: GatewayTiers.Power);
        await SeedAllocationAsync(standardUser.UserId, allocated: 5_000_000, tokensUsed: 0, tierProductId: GatewayTiers.Standard);
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var items = await ReadFilteredAsync(client, $"tier={tier}");

        Assert.Contains(items, a => a.UserId == powerUser.UserId);
        Assert.DoesNotContain(items, a => a.UserId == standardUser.UserId);
        Assert.All(items, a => Assert.Equal(GatewayTiers.Power, a.TierProductId));
    }

    [Fact]
    public async Task Admin_list_filtered_by_search_matches_the_owning_users_name_or_email()
    {
        var marker = Guid.NewGuid().ToString("N");
        var byName = await factory.SeedUserAsync(displayName: $"Ada {marker}");
        var byEmail = await factory.SeedUserAsync(displayName: "Someone Else", email: $"{marker}@contoso.test");
        var neither = await factory.SeedUserAsync(displayName: $"Nobody {Guid.NewGuid():N}");
        await SeedAllocationAsync(byName.UserId, allocated: 5_000_000, tokensUsed: 0);
        await SeedAllocationAsync(byEmail.UserId, allocated: 5_000_000, tokensUsed: 0);
        await SeedAllocationAsync(neither.UserId, allocated: 5_000_000, tokensUsed: 0);
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var items = await ReadFilteredAsync(client, $"search={marker}");

        Assert.Contains(items, a => a.UserId == byName.UserId);
        Assert.Contains(items, a => a.UserId == byEmail.UserId);
        Assert.DoesNotContain(items, a => a.UserId == neither.UserId);
    }

    [Fact]
    public async Task Admin_list_filtered_by_isActive_scopes_to_the_population_the_dashboard_counts()
    {
        var active = await factory.SeedUserAsync(displayName: $"Active {Guid.NewGuid():N}");
        var departed = await factory.SeedUserAsync(displayName: $"Departed {Guid.NewGuid():N}", isActive: false);
        await SeedAllocationAsync(active.UserId, allocated: 5_000_000, tokensUsed: 5_000_000, isHardStopped: true);
        await SeedAllocationAsync(departed.UserId, allocated: 5_000_000, tokensUsed: 5_000_000, isHardStopped: true);
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        // The dashboard's hard-stopped and over-budget counts are both scoped to active users, and
        // the deprovision pipeline hard-stops *and* deactivates — so without this filter the card's
        // link would land on everyone who ever left.
        var items = await ReadFilteredAsync(client, "isHardStopped=true&isActive=true");

        Assert.Contains(items, a => a.UserId == active.UserId);
        Assert.DoesNotContain(items, a => a.UserId == departed.UserId);
    }

    [Fact]
    public async Task Admin_list_with_no_filters_still_returns_the_rows_the_filters_would_exclude()
    {
        var stopped = await factory.SeedUserAsync(displayName: $"Unfiltered {Guid.NewGuid():N}", isActive: false);
        await SeedAllocationAsync(stopped.UserId, allocated: 5_000_000, tokensUsed: 5_000_000, isHardStopped: true);
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var items = await ReadAllPagesAsync(client);

        Assert.Contains(items, a => a.UserId == stopped.UserId);
    }

    // -- GET /quota/allocations/me --

    [Fact]
    public async Task Me_auto_creates_the_callers_current_period_allocation_and_is_stable_across_calls()
    {
        var oid = Guid.NewGuid().ToString();
        var me = await factory.SeedUserAsync(entraObjectId: oid, displayName: "Grace Hopper", configure: u => u.MonthlyTokenQuota = 5_000_000);
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
        Assert.Equal(5_000_000, first.AllocatedTokens);
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
        var dev = await factory.SeedUserAsync(configure: u => u.MonthlyTokenQuota = 20_000_000);
        await SeedAllocationAsync(dev.UserId, allocated: 5_000_000, tokensUsed: 321);
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
        Assert.Equal(20_000_000, devRow.AllocatedTokens); // re-resolved from the user override
        Assert.Equal(GatewayTiers.Power, devRow.TierProductId);
        Assert.False(devRow.IsHardStopped);
        Assert.Equal(factory.TimeProvider.GetUtcNow(), devRow.ResetDate);
        Assert.True(await dbContext.QuotaAllocations.AnyAsync(a => a.UserId == admin.UserId && a.PeriodYear == period.Year && a.PeriodMonth == period.Month));
        Assert.True(await dbContext.AuditLogs.CountAsync(a => a.Action == AuditActions.QuotaAllocationReset && a.ActorUserId == admin.UserId) >= 2);
    }

    // -- Helpers --

    private async Task SeedAllocationAsync(
        int userId,
        long? allocated,
        long tokensUsed,
        bool isHardStopped = false,
        string? tierProductId = null)
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
            IsHardStopped = isHardStopped,
            ResolvedLevelType = allocated is null ? QuotaLevelType.UserUnlimited : QuotaLevelType.UserOverride,
            TierProductId = tierProductId ?? (allocated is null ? GatewayTiers.Unlimited : GatewayTiers.Standard),
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>Every allocation the filtered list returned, across every page (the class shares one database).</summary>
    private static async Task<List<QuotaAllocationResponse>> ReadFilteredAsync(HttpClient client, string queryString)
    {
        var items = new List<QuotaAllocationResponse>();
        for (var page = 1; ; page++)
        {
            var result = await client.GetFromJsonAsync<PagedResult<QuotaAllocationResponse>>(
                new Uri($"{AllocationsPath}?{queryString}&page={page}&pageSize={PagedRequest.MaxPageSize}", UriKind.Relative),
                JsonOptions);
            Assert.NotNull(result);
            items.AddRange(result.Items);
            if (page >= result.TotalPages)
            {
                return items;
            }
        }
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
