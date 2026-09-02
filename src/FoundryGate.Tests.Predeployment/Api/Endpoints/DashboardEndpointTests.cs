using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Dashboard.Contracts;
using FoundryGate.Domain.Quota;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// End-to-end coverage of <c>GET /api/v1/dashboard</c> (#162): the auth matrix, the wire shape, and
/// the cache contract (<c>?fresh=true</c>). The exact arithmetic behind each number is pinned in
/// <c>Api/Services/Dashboard/DashboardServiceTests</c>, against a database only that class touches —
/// this factory's database is shared by the tests below, so they assert on <em>deltas</em> and on
/// rows they seeded themselves.
/// </summary>
public class DashboardEndpointTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string DashboardPath = "/api/v1/dashboard";

    [Fact]
    public async Task Anonymous_request_returns_401()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(DashboardPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_non_admin_returns_403()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: false);

        var response = await client.GetAsync(new Uri(DashboardPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_returns_200_and_the_summary_shape()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var response = await client.GetAsync(new Uri(DashboardPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var summary = await response.Content.ReadFromJsonAsync<DashboardSummaryResponse>(JsonOptions);
        Assert.NotNull(summary);
        Assert.NotNull(summary.TopConsumers);
        Assert.True(summary.TopConsumers.Count <= 10);
    }

    [Fact]
    public async Task Admin_with_no_user_row_can_still_read_the_dashboard()
    {
        // Nothing is attributed and nothing is written, so — unlike every mutation — the caller does
        // not have to have loaded the app once. An admin's first stop is this page.
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var response = await client.GetAsync(new Uri(DashboardPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Seeded_usage_shows_up_in_the_counts_and_at_the_top_of_the_consumer_list()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);
        var before = await GetAsync(client, fresh: true);

        var user = await factory.SeedUserAsync(displayName: "Katherine Johnson", configure: u => u.IsUnlimited = true);
        // Far more than any other row this class seeds, so "top of the list" is not a coincidence.
        await SeedAllocationAsync(user, tokensUsed: 987_654_321, allocatedTokens: null);

        var after = await GetAsync(client, fresh: true);

        Assert.Equal(before.TotalUserCount + 1, after.TotalUserCount);
        Assert.Equal(before.ActiveUserCount + 1, after.ActiveUserCount);
        Assert.Equal(before.UnlimitedUserCount + 1, after.UnlimitedUserCount);
        Assert.Equal(before.TotalTokensUsedThisPeriod + 987_654_321, after.TotalTokensUsedThisPeriod);

        var top = after.TopConsumers[0];
        Assert.Equal(user.UserId, top.UserId);
        Assert.Equal(user.UserUnique, top.UserUnique);
        Assert.Equal("Katherine Johnson", top.DisplayName);
        Assert.Equal(987_654_321, top.TokensUsed);
        Assert.Null(top.AllocatedTokens);
        Assert.Null(top.PercentUsed);
    }

    [Fact]
    public async Task Hard_stopped_and_over_budget_users_are_on_the_wire()
    {
        // #190: the two figures the dashboard's fourth card and its usage caption read. Deltas, not
        // absolutes — this factory's database is shared by the class.
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);
        var before = await GetAsync(client, fresh: true);

        var stopped = await factory.SeedUserAsync(displayName: "Revoked Rita");
        await SeedAllocationAsync(stopped, tokensUsed: 1_000, allocatedTokens: 5_000_000, isHardStopped: true);

        var exhausted = await factory.SeedUserAsync(displayName: "Exhausted Eli");
        await SeedAllocationAsync(exhausted, tokensUsed: 5_000_000, allocatedTokens: 5_000_000);

        var after = await GetAsync(client, fresh: true);

        Assert.Equal(before.HardStoppedUserCount + 1, after.HardStoppedUserCount);
        // The hard-stopped row is under its cap, so exactly one of the two is over budget.
        Assert.Equal(before.OverBudgetUserCount + 1, after.OverBudgetUserCount);
    }

    [Fact]
    public async Task A_plain_read_is_served_from_cache_and_fresh_true_bypasses_it()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);
        var primed = await GetAsync(client, fresh: true);

        _ = await factory.SeedUserAsync(displayName: "Arrived after the cache was written");

        var cached = await GetAsync(client, fresh: false);
        var fresh = await GetAsync(client, fresh: true);

        Assert.Equal(primed.TotalUserCount, cached.TotalUserCount);
        Assert.Equal(primed.TotalUserCount + 1, fresh.TotalUserCount);
    }

    private static async Task<DashboardSummaryResponse> GetAsync(HttpClient client, bool fresh)
    {
        var path = fresh ? $"{DashboardPath}?fresh=true" : DashboardPath;

        var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = await response.Content.ReadFromJsonAsync<DashboardSummaryResponse>(JsonOptions);
        Assert.NotNull(summary);
        return summary;
    }

    private async Task SeedAllocationAsync(User user, long tokensUsed, long? allocatedTokens, bool isHardStopped = false)
    {
        var period = BillingPeriod.Current(factory.TimeProvider);

        await using var dbContext = factory.CreateDbContext();
        dbContext.QuotaAllocations.Add(new QuotaAllocation
        {
            UserId = user.UserId,
            PeriodYear = period.Year,
            PeriodMonth = period.Month,
            AllocatedTokens = allocatedTokens,
            TokensUsed = tokensUsed,
            IsHardStopped = isHardStopped,
            ResolvedLevelType = allocatedTokens is null ? QuotaLevelType.UserUnlimited : QuotaLevelType.SystemDefault,
            TierProductId = allocatedTokens is null ? GatewayTiers.Unlimited : GatewayTiers.Standard,
        });
        await dbContext.SaveChangesAsync();
    }
}
