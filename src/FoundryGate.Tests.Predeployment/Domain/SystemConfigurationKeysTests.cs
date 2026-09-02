using FoundryGate.Domain.Constants;

namespace FoundryGate.Tests.Predeployment.Domain;

public class SystemConfigurationKeysTests
{
    [Fact]
    public void All_contains_exactly_the_five_seeded_keys_with_no_duplicates()
    {
        Assert.Equal(5, SystemConfigurationKeys.All.Count);
        Assert.Equal(SystemConfigurationKeys.All.Count, SystemConfigurationKeys.All.Distinct().Count());
    }

    [Theory]
    [InlineData(nameof(SystemConfigurationKeys.DefaultMonthlyTokenQuota))]
    [InlineData(nameof(SystemConfigurationKeys.ApimResourceId))]
    [InlineData(nameof(SystemConfigurationKeys.FoundryResourceId))]
    [InlineData(nameof(SystemConfigurationKeys.EntraGroupSyncEnabled))]
    [InlineData(nameof(SystemConfigurationKeys.ResetDayOfMonth))]
    public void All_contains_the_named_key(string expectedKey)
    {
        Assert.Contains(expectedKey, SystemConfigurationKeys.All);
    }

    [Theory]
    [InlineData("ApimGatewayUrl")]
    [InlineData("ApimProductId")]
    [InlineData("EntraTenantId")]
    public void Retired_names_a_key_that_All_no_longer_seeds(string retiredKey)
    {
        // #164/#123: retired keys must be named (so the seeder's delete filter still covers them and
        // deployed rows are cleaned up) but never seeded — a key in both lists would be inserted and
        // deleted on alternating passes.
        Assert.Contains(retiredKey, SystemConfigurationKeys.Retired);
        Assert.DoesNotContain(retiredKey, SystemConfigurationKeys.All);
    }

    [Fact]
    public void Retired_and_All_never_overlap()
    {
        Assert.Empty(SystemConfigurationKeys.Retired.Intersect(SystemConfigurationKeys.All, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(SystemConfigurationKeys.Retired.Count, SystemConfigurationKeys.Retired.Distinct().Count());
    }
}
