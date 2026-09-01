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

        Assert.Equal(8, Context.QuotaAllocations.Count());
        Assert.Single(Context.QuotaIncreaseRequests);
        Assert.True(Context.GroupMembers.Any());
    }

    [Fact]
    public async Task SeedAsync_is_a_noop_when_users_already_exist()
    {
        await SeedTestDataAsync();
        await SeedTestDataAsync();

        Assert.Equal(8, Context.Users.Count());
    }
}
