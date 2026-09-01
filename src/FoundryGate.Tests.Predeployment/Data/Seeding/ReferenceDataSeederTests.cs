namespace FoundryGate.Tests.Predeployment.Data.Seeding;

public class ReferenceDataSeederTests : InMemoryDatabaseTest
{
    [Fact]
    public async Task SeedAsync_inserts_all_eight_SystemConfiguration_defaults()
    {
        await SeedReferenceDataAsync();

        var keys = Context.SystemConfigurations.Select(c => c.Key).ToList();

        Assert.Equal(8, keys.Count);
        Assert.Contains("DefaultMonthlyTokenQuota", keys);
        Assert.Contains("ApimResourceId", keys);
        Assert.Contains("ApimGatewayUrl", keys);
        Assert.Contains("ApimProductId", keys);
        Assert.Contains("FoundryResourceId", keys);
        Assert.Contains("EntraTenantId", keys);
        Assert.Contains("EntraGroupSyncEnabled", keys);
        Assert.Contains("ResetDayOfMonth", keys);
    }

    [Fact]
    public async Task SeedAsync_is_idempotent_and_never_overwrites_an_edited_value()
    {
        await SeedReferenceDataAsync();

        // Simulate a fork operator editing a default via the admin config page.
        var quota = await Context.SystemConfigurations.FindAsync("DefaultMonthlyTokenQuota");
        Assert.NotNull(quota);
        quota!.Value = "5000000";
        await Context.SaveChangesAsync();

        // Re-run seeding twice, as would happen across repeated deploys.
        await SeedReferenceDataAsync();
        await SeedReferenceDataAsync();

        Assert.Equal(8, Context.SystemConfigurations.Count());

        var reloaded = await Context.SystemConfigurations.FindAsync("DefaultMonthlyTokenQuota");
        Assert.Equal("5000000", reloaded!.Value);
    }
}
