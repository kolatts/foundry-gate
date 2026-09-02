using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FoundryGate.Data.Seeding;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Constants;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// End-to-end coverage of <c>/api/v1/config</c> (#161) through the real pipeline: the auth matrix,
/// the per-key validation table's HTTP mapping (400/404/409), the stamped actor/timestamp, the audit
/// row, and the guarantee that a later deploy's reference-data seed never reverts an edit.
/// </summary>
/// <remarks>
/// The factory's <c>SystemConfiguration</c> rows are shared by this class's tests, so each test
/// asserts only on the key it just wrote — never on another key's value.
/// </remarks>
public class ConfigEndpointTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string ConfigPath = "/api/v1/config";

    [Fact]
    public async Task Anonymous_request_returns_401()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(ConfigPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_update_returns_401()
    {
        using var client = factory.CreateClient();

        var response = await PutAsync(client, SystemConfigurationKeys.ResetDayOfMonth, "1");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_non_admin_returns_403_on_both_actions()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: false);

        var list = await client.GetAsync(new Uri(ConfigPath, UriKind.Relative));
        var update = await PutAsync(client, SystemConfigurationKeys.ResetDayOfMonth, "1");

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
    }

    [Fact]
    public async Task Admin_with_no_user_row_cannot_update()
    {
        // Reading is fine — nothing is attributed. Writing needs an actor for the audit row, and
        // "authenticated principal with no User row" is 403 everywhere (CONVENTIONS.md).
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var list = await client.GetAsync(new Uri(ConfigPath, UriKind.Relative));
        var update = await PutAsync(client, SystemConfigurationKeys.ResetDayOfMonth, "1");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        var problem = await update.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions);
        Assert.Contains("GET /users/me", problem!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_lists_every_seeded_key_ordered_by_key()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var response = await client.GetAsync(new Uri(ConfigPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var entries = await response.Content.ReadFromJsonAsync<IReadOnlyList<SystemConfigEntryResponse>>(JsonOptions);
        Assert.NotNull(entries);

        var keys = entries.Select(e => e.Key).ToList();
        Assert.Equal([.. SystemConfigurationKeys.All.Order(StringComparer.Ordinal)], keys);
    }

    [Fact]
    public async Task Update_stores_the_value_stamps_the_admin_and_audits_before_and_after()
    {
        const string Key = SystemConfigurationKeys.ApimResourceId;
        const string NewValue =
            "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-foundrygate/providers/Microsoft.ApiManagement/service/apim-foundrygate";

        var oid = Guid.NewGuid().ToString();
        var admin = await factory.SeedUserAsync(oid, displayName: "Ada Lovelace");
        var before = await ValueOfAsync(Key);
        factory.TimeProvider.Advance(TimeSpan.FromMinutes(7));
        var now = factory.TimeProvider.GetUtcNow();

        using var client = factory.CreateClientAs(oid, isAdmin: true);
        var response = await PutAsync(client, Key, NewValue);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = await response.Content.ReadFromJsonAsync<SystemConfigEntryResponse>(JsonOptions);
        Assert.NotNull(entry);
        Assert.Equal(Key, entry.Key);
        Assert.Equal(NewValue, entry.Value);
        Assert.Equal(admin.UserId, entry.UpdatedByUserId);
        Assert.Equal("Ada Lovelace", entry.UpdatedByDisplayName);
        Assert.Equal(now, entry.UpdatedDate);

        await using var dbContext = factory.CreateDbContext();
        var row = await dbContext.SystemConfigurations.SingleAsync(c => c.Key == Key);
        Assert.Equal(NewValue, row.Value);
        Assert.Equal(admin.UserId, row.UpdatedByUserId);
        Assert.Equal(now, row.UpdatedDate);

        var audit = await dbContext.AuditLogs
            .Where(a => a.Action == AuditActions.ConfigUpdated && a.TargetId == Key)
            .OrderByDescending(a => a.AuditLogId)
            .FirstAsync();
        Assert.Equal(admin.UserId, audit.ActorUserId);
        Assert.Equal(AuditTargetTypes.SystemConfiguration, audit.TargetType);
        using var details = JsonDocument.Parse(audit.Details);
        Assert.Equal(Key, details.RootElement.GetProperty("key").GetString());
        Assert.Equal(before, details.RootElement.GetProperty("before").GetString());
        Assert.Equal(NewValue, details.RootElement.GetProperty("after").GetString());
    }

    [Fact]
    public async Task Update_normalizes_the_stored_value()
    {
        const string Key = SystemConfigurationKeys.EntraGroupSyncEnabled;
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);

        using var client = factory.CreateClientAs(oid, isAdmin: true);
        var response = await PutAsync(client, Key, "  TRUE ");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = await response.Content.ReadFromJsonAsync<SystemConfigEntryResponse>(JsonOptions);
        Assert.Equal("true", entry!.Value);
        Assert.Equal("true", await ValueOfAsync(Key));
    }

    [Fact]
    public async Task Update_matches_the_key_case_insensitively()
    {
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);

        using var client = factory.CreateClientAs(oid, isAdmin: true);
        var response = await PutAsync(client, "resetdayofmonth", "1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = await response.Content.ReadFromJsonAsync<SystemConfigEntryResponse>(JsonOptions);
        // The canonical key comes back, not the casing the caller typed.
        Assert.Equal(SystemConfigurationKeys.ResetDayOfMonth, entry!.Key);
    }

    [Fact]
    public async Task Update_of_an_unknown_key_returns_404()
    {
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);

        using var client = factory.CreateClientAs(oid, isAdmin: true);
        var response = await PutAsync(client, "NoSuchKey", "whatever");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions);
        Assert.Contains("NoSuchKey", problem!.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ApimGatewayUrl")]
    [InlineData("ApimProductId")]
    [InlineData("EntraTenantId")]
    public async Task A_retired_key_is_neither_listed_nor_editable(string retiredKey)
    {
        // #164/#123: these three used to be seeded and refused with a 409 "read-only". The rows are
        // gone now, so there is nothing to list and nothing to edit — the same 404 any typo gets.
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);

        using var client = factory.CreateClientAs(oid, isAdmin: true);
        var listed = await client.GetFromJsonAsync<List<SystemConfigEntryResponse>>(new Uri(ConfigPath, UriKind.Relative), JsonOptions);
        Assert.DoesNotContain(listed!, entry => string.Equals(entry.Key, retiredKey, StringComparison.OrdinalIgnoreCase));

        var response = await PutAsync(client, retiredKey, "something-new");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(SystemConfigurationKeys.DefaultMonthlyTokenQuota, "1234567")]
    [InlineData(SystemConfigurationKeys.ResetDayOfMonth, "31")]
    [InlineData(SystemConfigurationKeys.EntraGroupSyncEnabled, "yes")]
    [InlineData(SystemConfigurationKeys.ApimResourceId, "not-an-arm-id")]
    [InlineData(SystemConfigurationKeys.FoundryResourceId, "not-an-arm-id")]
    public async Task Update_with_a_value_the_key_does_not_allow_returns_400_and_changes_nothing(string key, string value)
    {
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);
        var before = await ValueOfAsync(key);

        using var client = factory.CreateClientAs(oid, isAdmin: true);
        var response = await PutAsync(client, key, value);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await ValueOfAsync(key));
    }

    [Fact]
    public async Task Update_accepts_a_configured_tier_cap_for_the_system_default_quota()
    {
        const string Key = SystemConfigurationKeys.DefaultMonthlyTokenQuota;
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);

        using var client = factory.CreateClientAs(oid, isAdmin: true);
        var response = await PutAsync(client, Key, "20000000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("20000000", await ValueOfAsync(Key));
    }

    [Theory]
    [InlineData(SystemConfigurationKeys.ApimResourceId)]
    [InlineData(SystemConfigurationKeys.FoundryResourceId)]
    public async Task An_empty_value_clears_a_key_whose_rule_allows_it(string key)
    {
        // The regression this pins: `[Required]` defaults to AllowEmptyStrings = false, so MVC's
        // automatic model validation used to 400 an empty body before the per-key rule — which
        // explicitly accepts "" — was ever consulted. Unwiring a resource is a real operation.
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);

        using var client = factory.CreateClientAs(oid, isAdmin: true);
        var response = await PutAsync(client, key, string.Empty);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = await response.Content.ReadFromJsonAsync<SystemConfigEntryResponse>(JsonOptions);
        Assert.Equal(string.Empty, entry!.Value);
        Assert.Equal(string.Empty, await ValueOfAsync(key));
    }

    [Theory]
    [InlineData(SystemConfigurationKeys.DefaultMonthlyTokenQuota)]
    [InlineData(SystemConfigurationKeys.ResetDayOfMonth)]
    [InlineData(SystemConfigurationKeys.EntraGroupSyncEnabled)]
    public async Task An_empty_value_is_still_refused_where_the_keys_rule_forbids_it(string key)
    {
        // Emptiness is the per-key rule's decision, not model binding's: a quota, a reset day and a
        // feature flag have no empty form, and quota resolution reads all three.
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);
        var before = await ValueOfAsync(key);

        using var client = factory.CreateClientAs(oid, isAdmin: true);
        var response = await PutAsync(client, key, string.Empty);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await ValueOfAsync(key));
    }

    [Fact]
    public async Task An_over_long_value_is_a_field_level_400_not_a_500()
    {
        // This record's share of #128's guard: an invalid body is a 400 ProblemDetails naming the
        // member, never the 500 a positional record with [property: …] attributes produced. It lives
        // here rather than in RequestDtoBindingTests because the key has to exist for the request to
        // reach validation at all.
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);

        using var client = factory.CreateClientAs(oid, isAdmin: true);
        var response = await PutAsync(
            client,
            SystemConfigurationKeys.ApimResourceId,
            new string('x', ValidationConstants.ConfigValueMaxLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions);
        Assert.NotNull(problem?.Errors);
        Assert.Contains(nameof(UpdateSystemConfigRequest.Value), problem.Errors.Keys);
    }

    [Fact]
    public async Task A_later_reference_data_seed_never_reverts_an_edited_value()
    {
        // SystemConfiguration.Value/UpdatedDate/UpdatedByUserId are [DoNotUpdate], so the seeder only
        // inserts missing keys. Pinned here because the whole config editor is worthless if the next
        // deploy silently undoes it.
        const string Key = SystemConfigurationKeys.ApimResourceId;
        const string EditedValue = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-contoso/providers/Microsoft.ApiManagement/service/apim-contoso";

        var oid = Guid.NewGuid().ToString();
        var admin = await factory.SeedUserAsync(oid, displayName: "Grace Hopper");

        using var client = factory.CreateClientAs(oid, isAdmin: true);
        var response = await PutAsync(client, Key, EditedValue);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var edited = await response.Content.ReadFromJsonAsync<SystemConfigEntryResponse>(JsonOptions);

        await using (var seedContext = factory.CreateDbContext())
        {
            _ = await ReferenceDataSeeder.SeedAsync(seedContext);
            _ = await ReferenceDataSeeder.SeedAsync(seedContext);
        }

        await using var dbContext = factory.CreateDbContext();
        var row = await dbContext.SystemConfigurations.SingleAsync(c => c.Key == Key);
        Assert.Equal(EditedValue, row.Value);
        Assert.Equal(admin.UserId, row.UpdatedByUserId);
        Assert.Equal(edited!.UpdatedDate, row.UpdatedDate);
        Assert.Equal(SystemConfigurationKeys.All.Count, await dbContext.SystemConfigurations.CountAsync());
    }

    [Theory]
    [InlineData(SystemConfigurationKeys.LastUserSyncDate)]
    [InlineData(SystemConfigurationKeys.LastUserSyncResult)]
    public async Task A_system_managed_key_is_refused_with_409_and_the_reason(string key)
    {
        // #171: these rows are written by POST /users/sync itself. A 409, not a 403 — the caller's
        // permissions are fine, the resource is not theirs to set — and not a 404 either: the row
        // exists and is worth reading.
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);
        _ = await factory.SeedUserAsync();
        var before = await ValueOfAsync(key);

        var response = await PutAsync(client, key, "tampered");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions);
        Assert.Contains("read-only", problem?.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(before, await ValueOfAsync(key));
    }

    [Fact]
    public async Task The_list_flags_which_keys_the_editor_must_not_offer_to_change()
    {
        // #172: the Web page keeps no list of its own — this flag is what disables the field, and it
        // comes from the same Domain map the 409 above does.
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var entries = await client.GetFromJsonAsync<IReadOnlyList<SystemConfigEntryResponse>>(
            new Uri(ConfigPath, UriKind.Relative), JsonOptions);

        Assert.NotNull(entries);
        foreach (var entry in entries)
        {
            Assert.Equal(SystemConfigurationKeys.SystemManaged.ContainsKey(entry.Key), entry.IsReadOnly);
        }

        Assert.Contains(entries, e => e.IsReadOnly);
    }

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string key, string value) =>
        client.PutAsJsonAsync(
            new Uri($"{ConfigPath}/{Uri.EscapeDataString(key)}", UriKind.Relative),
            new UpdateSystemConfigRequest { Value = value });

    private async Task<string> ValueOfAsync(string key)
    {
        await using var dbContext = factory.CreateDbContext();
        return await dbContext.SystemConfigurations
            .Where(c => c.Key == key)
            .Select(c => c.Value)
            .SingleAsync();
    }
}
