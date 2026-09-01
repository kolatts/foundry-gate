using FoundryGate.Domain.Constants;

namespace FoundryGate.Tests.Predeployment.Domain;

public class SystemConfigurationKeysTests
{
    [Fact]
    public void All_contains_exactly_the_eight_seeded_keys_with_no_duplicates()
    {
        Assert.Equal(8, SystemConfigurationKeys.All.Count);
        Assert.Equal(SystemConfigurationKeys.All.Count, SystemConfigurationKeys.All.Distinct().Count());
    }

    [Theory]
    [InlineData(nameof(SystemConfigurationKeys.DefaultMonthlyTokenQuota))]
    [InlineData(nameof(SystemConfigurationKeys.ApimResourceId))]
    [InlineData(nameof(SystemConfigurationKeys.ApimGatewayUrl))]
    [InlineData(nameof(SystemConfigurationKeys.ApimProductId))]
    [InlineData(nameof(SystemConfigurationKeys.FoundryResourceId))]
    [InlineData(nameof(SystemConfigurationKeys.EntraTenantId))]
    [InlineData(nameof(SystemConfigurationKeys.EntraGroupSyncEnabled))]
    [InlineData(nameof(SystemConfigurationKeys.ResetDayOfMonth))]
    public void All_contains_the_named_key(string expectedKey)
    {
        Assert.Contains(expectedKey, SystemConfigurationKeys.All);
    }
}
