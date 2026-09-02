using FoundryGate.Domain.Constants;

namespace FoundryGate.Tests.Predeployment.Domain;

public class SystemConfigurationKeysTests
{
    [Fact]
    public void All_contains_exactly_the_eight_seeded_keys_with_no_duplicates()
    {
        // Five originals, the two LastUserSync* rows #171 added, and #177's RateCard.
        Assert.Equal(8, SystemConfigurationKeys.All.Count);
        Assert.Equal(SystemConfigurationKeys.All.Count, SystemConfigurationKeys.All.Distinct().Count());
    }

    [Theory]
    [InlineData(nameof(SystemConfigurationKeys.DefaultMonthlyTokenQuota))]
    [InlineData(nameof(SystemConfigurationKeys.ApimResourceId))]
    [InlineData(nameof(SystemConfigurationKeys.FoundryResourceId))]
    [InlineData(nameof(SystemConfigurationKeys.EntraGroupSyncEnabled))]
    [InlineData(nameof(SystemConfigurationKeys.ResetDayOfMonth))]
    [InlineData(nameof(SystemConfigurationKeys.LastUserSyncDate))]
    [InlineData(nameof(SystemConfigurationKeys.LastUserSyncResult))]
    [InlineData(nameof(SystemConfigurationKeys.RateCard))]
    public void All_contains_the_named_key(string expectedKey)
    {
        Assert.Contains(expectedKey, SystemConfigurationKeys.All);
    }

    [Fact]
    public void System_managed_keys_are_seeded_keys_that_an_admin_may_not_edit()
    {
        // #171/#172: one map, two readers — the Api validator's 409 and the response's IsReadOnly.
        // A system-managed key must exist to be read, so it has to be seeded; and it must not also be
        // retired, or the seeder would delete the row the sync writes.
        Assert.NotEmpty(SystemConfigurationKeys.SystemManaged);

        foreach (var (key, reason) in SystemConfigurationKeys.SystemManaged)
        {
            Assert.Contains(key, SystemConfigurationKeys.All);
            Assert.DoesNotContain(key, SystemConfigurationKeys.Retired);
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{key} must say why it is read-only.");
        }
    }

    [Fact]
    public void System_managed_reason_is_case_insensitive_and_null_for_an_editable_key()
    {
        // Keys are matched case-insensitively everywhere else (SQL Server's default collation), so
        // the read-only decision must not be the one place that is stricter.
        Assert.NotNull(SystemConfigurationKeys.SystemManagedReason("lastusersyncdate"));
        Assert.Null(SystemConfigurationKeys.SystemManagedReason(SystemConfigurationKeys.DefaultMonthlyTokenQuota));
        Assert.Null(SystemConfigurationKeys.SystemManagedReason("SomeForkOwnedKey"));
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
