namespace FoundryGate.Tests.Predeployment.Data.Seeding;

public class TestDataSeederTests : InMemoryDatabaseTest
{
    [Fact]
    public async Task SeedAsync_creates_developers_with_varied_quota_tiers()
    {
        await SeedTestDataAsync();

        var users = Context.Users.ToList();
        Assert.Equal(8, users.Count);

        // At least one unlimited developer and at least one fixed-quota developer, matching the
        // landing page's demo shape (docs-site/src/pages/index.astro).
        Assert.Contains(users, u => u.IsUnlimited && u.MonthlyTokenQuota is null);
        Assert.Contains(users, u => !u.IsUnlimited && u.MonthlyTokenQuota is not null);

        // A budget is always a tier (D-013): every seeded quota is a shipped tier cap or unlimited.
        Assert.All(users, u => Assert.Contains(u.MonthlyTokenQuota, new long?[] { Support.TestGatewayTiers.StandardCap, Support.TestGatewayTiers.PowerCap, null }));
        Assert.All(Context.Groups.ToList(), g => Assert.Contains(g.MonthlyTokenQuota, new long?[] { Support.TestGatewayTiers.StandardCap, Support.TestGatewayTiers.PowerCap, null }));
        Assert.All(Context.QuotaAllocations.ToList(), a => Assert.False(a.IsGatewayCapped));
        Assert.Equal(8, Context.QuotaAllocations.Count());
        Assert.True(Context.GroupMembers.Any());

        // The demo request must be one POST /requests would actually have accepted (#34): a tier cap
        // asked for by a developer on a smaller finite tier — never an unlimited user (nothing larger
        // to ask for) and never a value that is not a tier.
        var increaseRequest = Assert.Single(Context.QuotaIncreaseRequests);
        Assert.Equal(Support.TestGatewayTiers.PowerCap, increaseRequest.RequestedQuota);
        Assert.Equal(Support.TestGatewayTiers.StandardCap, increaseRequest.CurrentQuota);
        Assert.False(users.Single(u => u.UserId == increaseRequest.UserId).IsUnlimited);
    }

    [Fact]
    public async Task SeedAsync_is_a_noop_when_users_already_exist()
    {
        await SeedTestDataAsync();
        await SeedTestDataAsync();

        Assert.Equal(8, Context.Users.Count());
    }
}
