using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Data.Seeding;

public class ReferenceDataSeederTests : InMemoryDatabaseTest
{
    [Fact]
    public async Task SeedAsync_inserts_all_five_SystemConfiguration_defaults()
    {
        await SeedReferenceDataAsync();

        var keys = Context.SystemConfigurations.Select(c => c.Key).ToList();

        Assert.Equal(5, keys.Count);
        Assert.Contains("DefaultMonthlyTokenQuota", keys);
        Assert.Contains("ApimResourceId", keys);
        Assert.Contains("FoundryResourceId", keys);
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

        Assert.Equal(5, Context.SystemConfigurations.Count());

        var reloaded = await Context.SystemConfigurations.FindAsync("DefaultMonthlyTokenQuota");
        Assert.Equal("5000000", reloaded!.Value);
    }

    [Fact]
    public async Task SeedAsync_deletes_the_retired_keys_from_a_database_seeded_before_they_were_dropped()
    {
        // #164/#123: the rows exist in every fork deployed before this change — including ones an
        // operator edited, which is exactly why deleting them is a data change worth its own review.
        // `db seed-reference` on the next deploy is what removes them.
        foreach (var retired in SystemConfigurationKeys.Retired)
        {
            _ = Context.SystemConfigurations.Add(new SystemConfiguration { Key = retired, Value = "set by an operator" });
        }

        _ = await Context.SaveChangesAsync();

        var result = await SeedReferenceDataAsync();

        Assert.Equal(SystemConfigurationKeys.Retired.Count, result[nameof(SystemConfiguration)].Deleted);
        var keys = await Context.SystemConfigurations.AsNoTracking().Select(c => c.Key).ToListAsync();
        Assert.Equal(SystemConfigurationKeys.All.Order(), keys.Order());
    }

    [Fact]
    public async Task SeedAsync_leaves_a_row_a_fork_operator_added_alone()
    {
        // The delete filter is what makes retiring safe: it only ever removes keys this code names,
        // so a fork's own configuration row survives every re-seed.
        _ = Context.SystemConfigurations.Add(new SystemConfiguration { Key = "ContosoSlackWebhook", Value = "https://hooks.contoso.test/abc" });
        _ = await Context.SaveChangesAsync();

        _ = await SeedReferenceDataAsync();
        _ = await SeedReferenceDataAsync();

        var row = await Context.SystemConfigurations.AsNoTracking().SingleAsync(c => c.Key == "ContosoSlackWebhook");
        Assert.Equal("https://hooks.contoso.test/abc", row.Value);
    }
}
