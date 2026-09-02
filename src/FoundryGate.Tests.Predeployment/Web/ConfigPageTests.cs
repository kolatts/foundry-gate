using Bunit;
using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Constants;
using FoundryGate.Web.Pages;
using FoundryGate.Web.Services;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// <c>/config</c> (#55): dirty tracking, the diff dialog that stands between an edit and a write,
/// per-row outcomes, and the read-only keys that are disabled rather than offered.
/// </summary>
public class ConfigPageTests : WebTestContext
{
    public ConfigPageTests() => SignInAsAdmin();

    [Fact]
    public void Renders_a_row_per_key_with_when_and_by_whom_it_last_changed()
    {
        Api.ConfigResult = Ok<IReadOnlyList<SystemConfigEntryResponse>>(
        [
            WebTestData.ConfigEntry(SystemConfigurationKeys.DefaultMonthlyTokenQuota, "5000000", updatedByUserId: 3, updatedByDisplayName: "Ada Admin"),
            WebTestData.ConfigEntry(SystemConfigurationKeys.ResetDayOfMonth, "1"),
        ]);

        var page = RenderPage<Config>();

        var table = page.Find("[data-testid='config-table']").TextContent;
        Assert.Contains(SystemConfigurationKeys.DefaultMonthlyTokenQuota, table, StringComparison.Ordinal);
        Assert.Contains("by Ada Admin", table, StringComparison.Ordinal);
        Assert.Contains("seeded — never edited", table, StringComparison.Ordinal);
    }

    [Fact]
    public void Falls_back_to_the_user_id_when_a_row_carries_no_editor_name()
    {
        Api.ConfigResult = Ok<IReadOnlyList<SystemConfigEntryResponse>>(
            [WebTestData.ConfigEntry(SystemConfigurationKeys.DefaultMonthlyTokenQuota, "5000000", updatedByUserId: 3)]);

        var page = RenderPage<Config>();

        Assert.Contains("by user #3", page.Find("[data-testid='config-table']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_is_disabled_until_something_is_actually_different()
    {
        var page = RenderPage<Config>();

        Assert.True(page.Find("[data-testid='config-save']").HasAttribute("disabled"));

        Edit(page, SystemConfigurationKeys.DefaultMonthlyTokenQuota, "20000000");

        Assert.False(page.Find("[data-testid='config-save']").HasAttribute("disabled"));
    }

    [Fact]
    public void Typing_the_original_value_back_is_not_a_change()
    {
        var page = RenderPage<Config>();

        Edit(page, SystemConfigurationKeys.DefaultMonthlyTokenQuota, "20000000");
        Edit(page, SystemConfigurationKeys.DefaultMonthlyTokenQuota, "5000000");

        Assert.True(page.Find("[data-testid='config-save']").HasAttribute("disabled"));
    }

    [Fact]
    public void The_diff_lists_only_the_dirty_rows()
    {
        Api.ConfigResult = Ok<IReadOnlyList<SystemConfigEntryResponse>>(
        [
            WebTestData.ConfigEntry(SystemConfigurationKeys.DefaultMonthlyTokenQuota, "5000000"),
            WebTestData.ConfigEntry(SystemConfigurationKeys.ResetDayOfMonth, "1"),
        ]);

        var page = RenderPage<Config>();
        Edit(page, SystemConfigurationKeys.DefaultMonthlyTokenQuota, "20000000");
        page.Find("[data-testid='config-save']").Click();

        var diff = page.Find("[data-testid='config-diff-table']").TextContent;
        Assert.Contains("5000000", diff, StringComparison.Ordinal);
        Assert.Contains("20000000", diff, StringComparison.Ordinal);
        Assert.DoesNotContain(SystemConfigurationKeys.ResetDayOfMonth, diff, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_is_written_until_the_diff_is_confirmed()
    {
        var page = RenderPage<Config>();
        Edit(page, SystemConfigurationKeys.DefaultMonthlyTokenQuota, "20000000");

        page.Find("[data-testid='config-save']").Click();
        Assert.Empty(Api.ConfigUpdates);

        page.Find("[data-testid='config-diff-cancel']").Click();
        Assert.Empty(Api.ConfigUpdates);
    }

    [Fact]
    public void Confirming_writes_one_put_per_dirty_key()
    {
        Api.ConfigResult = Ok<IReadOnlyList<SystemConfigEntryResponse>>(
        [
            WebTestData.ConfigEntry(SystemConfigurationKeys.DefaultMonthlyTokenQuota, "5000000"),
            WebTestData.ConfigEntry(SystemConfigurationKeys.ResetDayOfMonth, "1"),
        ]);

        var page = RenderPage<Config>();
        Edit(page, SystemConfigurationKeys.DefaultMonthlyTokenQuota, "20000000");
        Edit(page, SystemConfigurationKeys.ResetDayOfMonth, "15");
        page.Find("[data-testid='config-save']").Click();
        page.Find("[data-testid='config-diff-confirm']").Click();

        Assert.Equal(
            [(SystemConfigurationKeys.DefaultMonthlyTokenQuota, "20000000"), (SystemConfigurationKeys.ResetDayOfMonth, "15")],
            Api.ConfigUpdates);
        Assert.Contains(Snackbars, s => s.Severity == MudBlazor.Severity.Success);
    }

    [Fact]
    public void A_rejected_key_reports_next_to_its_own_field_and_does_not_stop_the_others()
    {
        Api.ConfigResult = Ok<IReadOnlyList<SystemConfigEntryResponse>>(
        [
            WebTestData.ConfigEntry(SystemConfigurationKeys.DefaultMonthlyTokenQuota, "5000000"),
            WebTestData.ConfigEntry(SystemConfigurationKeys.ResetDayOfMonth, "1"),
        ]);
        Api.UpdateConfigResults[SystemConfigurationKeys.ResetDayOfMonth] =
            ApiCallResult<bool>.Fail(ApiCallStatus.Error, "ResetDayOfMonth must be a whole number from 1 to 28.");

        var page = RenderPage<Config>();
        Edit(page, SystemConfigurationKeys.DefaultMonthlyTokenQuota, "20000000");
        Edit(page, SystemConfigurationKeys.ResetDayOfMonth, "31");
        page.Find("[data-testid='config-save']").Click();
        page.Find("[data-testid='config-diff-confirm']").Click();

        Assert.Equal(2, Api.ConfigUpdates.Count);
        Assert.Contains(
            "1 to 28",
            page.Find($"[data-testid='config-result-{SystemConfigurationKeys.ResetDayOfMonth}']").TextContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "Saved.",
            page.Find($"[data-testid='config-result-{SystemConfigurationKeys.DefaultMonthlyTokenQuota}']").TextContent,
            StringComparison.Ordinal);
        Assert.Contains(Snackbars, s => s.Severity == MudBlazor.Severity.Warning);
    }

    [Theory]
    [InlineData(SystemConfigurationKeys.ApimProductId)]
    [InlineData(SystemConfigurationKeys.EntraTenantId)]
    [InlineData(SystemConfigurationKeys.ApimGatewayUrl)]
    public void A_retired_key_is_disabled_with_the_reason_rather_than_offered(string key)
    {
        Api.ConfigResult = Ok<IReadOnlyList<SystemConfigEntryResponse>>([WebTestData.ConfigEntry(key, "something")]);

        var page = RenderPage<Config>();

        Assert.True(page.Find($"[data-testid='config-value-{key}']").HasAttribute("disabled"));
        Assert.Contains("Read-only", page.Find($"[data-testid='config-readonly-{key}']").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Discard_puts_every_field_back()
    {
        var page = RenderPage<Config>();
        Edit(page, SystemConfigurationKeys.DefaultMonthlyTokenQuota, "20000000");

        page.Find("[data-testid='config-discard']").Click();

        Assert.Equal("5000000", page.Find($"[data-testid='config-value-{SystemConfigurationKeys.DefaultMonthlyTokenQuota}']").GetAttribute("value"));
        Assert.True(page.Find("[data-testid='config-save']").HasAttribute("disabled"));
    }

    [Fact]
    public void A_failed_load_renders_an_error()
    {
        Api.ConfigResult = ApiCallResult<IReadOnlyList<SystemConfigEntryResponse>>.Fail(
            ApiCallStatus.Forbidden,
            "You don't have permission to do that.");

        var page = RenderPage<Config>();

        Assert.Contains("permission", page.Find("[data-testid='config-error']").TextContent, StringComparison.Ordinal);
    }

    private static void Edit(IRenderedComponent<Bunit.Rendering.ContainerFragment> page, string key, string value) =>
        page.Find($"[data-testid='config-value-{key}']").Input(value);

    private static ApiCallResult<T> Ok<T>(T value) => ApiCallResult<T>.Ok(value);
}
