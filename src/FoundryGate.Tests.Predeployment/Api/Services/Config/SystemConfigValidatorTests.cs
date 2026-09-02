using FoundryGate.Api.Services.Config;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Tests.Predeployment.Support;

namespace FoundryGate.Tests.Predeployment.Api.Services.Config;

/// <summary>
/// The per-key validation table (#161). Every rule is pinned here so <c>ConfigService</c>'s tests can
/// stay about persistence, auditing and HTTP mapping.
/// </summary>
public class SystemConfigValidatorTests
{
    private readonly SystemConfigValidator _validator = new(TestGatewayTiers.Mapper());

    [Theory]
    [InlineData(SystemConfigurationKeys.ApimProductId, "Gateway:Tiers")]
    [InlineData(SystemConfigurationKeys.EntraTenantId, "Entra:Enabled")]
    public void EnsureEditable_refuses_a_retired_key_and_names_its_replacement(string key, string replacementHint)
    {
        var exception = Assert.Throws<ConflictException>(() => SystemConfigValidator.EnsureEditable(key));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
        Assert.Contains("read-only", exception.Message, StringComparison.Ordinal);
        Assert.Contains(replacementHint, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SystemConfigurationKeys.DefaultMonthlyTokenQuota)]
    [InlineData(SystemConfigurationKeys.ApimResourceId)]
    [InlineData(SystemConfigurationKeys.ApimGatewayUrl)]
    [InlineData(SystemConfigurationKeys.FoundryResourceId)]
    [InlineData(SystemConfigurationKeys.EntraGroupSyncEnabled)]
    [InlineData(SystemConfigurationKeys.ResetDayOfMonth)]
    public void EnsureEditable_allows_every_other_seeded_key(string key) =>
        SystemConfigValidator.EnsureEditable(key);

    [Theory]
    [InlineData("5000000", "5000000")]
    [InlineData("  20000000  ", "20000000")]
    public void DefaultMonthlyTokenQuota_accepts_a_configured_tier_cap(string value, string expected) =>
        Assert.Equal(expected, _validator.Normalize(SystemConfigurationKeys.DefaultMonthlyTokenQuota, value));

    [Theory]
    [InlineData("5000001")] // between Standard and Power — a real number, but not a tier
    [InlineData("0")]       // the unlimited tier's sentinel cap, not a finite budget
    public void DefaultMonthlyTokenQuota_rejects_a_value_that_is_not_a_tier_cap(string value)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => _validator.Normalize(SystemConfigurationKeys.DefaultMonthlyTokenQuota, value));

        Assert.Contains("tier", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5,000,000", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unlimited")]
    [InlineData("5_000_000")]
    [InlineData("-5000000")]
    [InlineData("5000000.0")]
    public void DefaultMonthlyTokenQuota_rejects_anything_that_is_not_a_non_negative_whole_number(string value) =>
        Assert.Throws<ArgumentException>(
            () => _validator.Normalize(SystemConfigurationKeys.DefaultMonthlyTokenQuota, value));

    [Theory]
    [InlineData("1", "1")]
    [InlineData(" 28 ", "28")]
    [InlineData("07", "7")]
    public void ResetDayOfMonth_accepts_1_through_28(string value, string expected) =>
        Assert.Equal(expected, _validator.Normalize(SystemConfigurationKeys.ResetDayOfMonth, value));

    [Theory]
    [InlineData("0")]
    [InlineData("29")]
    [InlineData("31")]
    [InlineData("-1")]
    [InlineData("first")]
    [InlineData("")]
    public void ResetDayOfMonth_rejects_a_day_no_month_is_guaranteed_to_have(string value)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => _validator.Normalize(SystemConfigurationKeys.ResetDayOfMonth, value));

        Assert.Contains("1 to 28", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData("True", "true")]
    [InlineData(" FALSE ", "false")]
    public void EntraGroupSyncEnabled_normalizes_a_boolean_to_lower_case(string value, string expected) =>
        Assert.Equal(expected, _validator.Normalize(SystemConfigurationKeys.EntraGroupSyncEnabled, value));

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    public void EntraGroupSyncEnabled_rejects_a_non_boolean(string value) =>
        Assert.Throws<ArgumentException>(
            () => _validator.Normalize(SystemConfigurationKeys.EntraGroupSyncEnabled, value));

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("https://ai.contoso.com", "https://ai.contoso.com")]
    [InlineData("https://ai.contoso.com/", "https://ai.contoso.com")]
    public void ApimGatewayUrl_accepts_an_absolute_https_url_or_empty(string value, string expected) =>
        Assert.Equal(expected, _validator.Normalize(SystemConfigurationKeys.ApimGatewayUrl, value));

    [Theory]
    [InlineData("http://ai.contoso.com")]
    [InlineData("ai.contoso.com")]
    [InlineData("/anthropic")]
    public void ApimGatewayUrl_rejects_anything_that_is_not_absolute_https(string value)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => _validator.Normalize(SystemConfigurationKeys.ApimGatewayUrl, value));

        Assert.Contains("absolute https URL", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SystemConfigurationKeys.ApimResourceId)]
    [InlineData(SystemConfigurationKeys.FoundryResourceId)]
    public void Resource_id_keys_accept_an_arm_resource_id_or_empty(string key)
    {
        const string ResourceId =
            "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-foundrygate/providers/Microsoft.ApiManagement/service/apim-foundrygate";

        Assert.Equal(string.Empty, _validator.Normalize(key, "  "));
        Assert.Equal(ResourceId, _validator.Normalize(key, ResourceId));
        Assert.Equal(ResourceId, _validator.Normalize(key, ResourceId + "/"));
    }

    [Theory]
    [InlineData(SystemConfigurationKeys.ApimResourceId, "apim-foundrygate")]
    [InlineData(SystemConfigurationKeys.FoundryResourceId, "/subscriptions/abc/resourceGroups/rg")]
    public void Resource_id_keys_reject_anything_that_is_not_shaped_like_one(string key, string value)
    {
        var exception = Assert.Throws<ArgumentException>(() => _validator.Normalize(key, value));

        Assert.Contains("ARM resource id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_operator_added_key_has_no_rule_and_is_stored_trimmed()
    {
        // The reference-data seeder's deleteFilter deliberately preserves rows a fork operator added
        // themselves; the editor must not refuse to edit what the seeder refuses to delete.
        Assert.Equal("anything at all", _validator.Normalize("Contoso:SupportEmail", "  anything at all  "));
    }
}
