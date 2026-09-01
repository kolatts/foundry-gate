using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Audit.Contracts;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// End-to-end coverage of <c>GET /api/v1/audit</c> through the real pipeline — and, since this is
/// the first controller under <c>/api/v1</c>, the first proof of the auth contract plans/04 deferred:
/// anonymous → 401, authenticated non-admin → 403 on an admin-only route, admin → 200.
/// </summary>
public class AuditEndpointTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private static readonly DateTimeOffset BaseTime = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string AuditPath = "/api/v1/audit";

    [Fact]
    public async Task Anonymous_request_returns_401()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(AuditPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_non_admin_returns_403()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: false);

        var response = await client.GetAsync(new Uri(AuditPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_returns_200_with_the_paged_envelope_and_default_paging()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var response = await client.GetAsync(new Uri(AuditPath, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<AuditLogEntryResponse>>(JsonOptions);
        Assert.NotNull(page);
        Assert.Equal(1, page.Page);
        Assert.Equal(PagedRequest.DefaultPageSize, page.PageSize);
    }

    [Fact]
    public async Task Results_are_newest_first_and_carry_the_actors_display_name()
    {
        var marker = Marker();
        var actor = await factory.SeedUserAsync(displayName: "Grace Hopper");
        await SeedAuditAsync(actor.UserId, AuditActions.GroupCreated, AuditTargetTypes.Group, marker, BaseTime);
        await SeedAuditAsync(actor.UserId, AuditActions.GroupUpdated, AuditTargetTypes.Group, marker, BaseTime.AddHours(2));
        await SeedAuditAsync(null, AuditActions.GroupDeleted, AuditTargetTypes.Group, marker, BaseTime.AddHours(1));

        var page = await GetPageAsync($"?targetId={marker}");

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(
            [AuditActions.GroupUpdated, AuditActions.GroupDeleted, AuditActions.GroupCreated],
            page.Items.Select(i => i.Action));
        Assert.Equal("Grace Hopper", page.Items[0].ActorDisplayName);
        Assert.Null(page.Items[1].ActorDisplayName);
        Assert.Null(page.Items[1].ActorUserId);
    }

    [Fact]
    public async Task Paging_honours_page_and_pageSize_and_reports_totals()
    {
        var marker = Marker();
        for (var i = 0; i < 5; i++)
        {
            await SeedAuditAsync(null, AuditActions.UsageSynced, AuditTargetTypes.QuotaAllocation, marker, BaseTime.AddMinutes(i));
        }

        var page = await GetPageAsync($"?targetId={marker}&page=2&pageSize=2");

        Assert.Equal(5, page.TotalCount);
        Assert.Equal(2, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(2, page.Items.Count);
        // Newest first overall → page 2 of size 2 is the 3rd and 4th newest (minutes 2 and 1).
        Assert.Equal([BaseTime.AddMinutes(2), BaseTime.AddMinutes(1)], page.Items.Select(i => i.OccurredDate));
    }

    [Fact]
    public async Task Filter_by_actorUserId_returns_only_that_actors_rows()
    {
        var marker = Marker();
        var alice = await factory.SeedUserAsync(displayName: "Alice");
        var bob = await factory.SeedUserAsync(displayName: "Bob");
        await SeedAuditAsync(alice.UserId, AuditActions.UserActivated, AuditTargetTypes.User, marker, BaseTime);
        await SeedAuditAsync(bob.UserId, AuditActions.UserActivated, AuditTargetTypes.User, marker, BaseTime);

        var page = await GetPageAsync($"?targetId={marker}&actorUserId={alice.UserId}");

        var entry = Assert.Single(page.Items);
        Assert.Equal(alice.UserId, entry.ActorUserId);
        Assert.Equal("Alice", entry.ActorDisplayName);
    }

    [Fact]
    public async Task Filter_by_action_is_an_exact_match()
    {
        var marker = Marker();
        await SeedAuditAsync(null, AuditActions.KeyRotated, AuditTargetTypes.ApiKey, marker, BaseTime);
        await SeedAuditAsync(null, AuditActions.KeyRevoked, AuditTargetTypes.ApiKey, marker, BaseTime);

        var page = await GetPageAsync($"?targetId={marker}&action={AuditActions.KeyRotated}");

        var entry = Assert.Single(page.Items);
        Assert.Equal(AuditActions.KeyRotated, entry.Action);
    }

    [Fact]
    public async Task Filter_by_targetType_returns_only_that_type()
    {
        var marker = Marker();
        await SeedAuditAsync(null, AuditActions.ConfigUpdated, AuditTargetTypes.SystemConfiguration, marker, BaseTime);
        await SeedAuditAsync(null, AuditActions.GroupCreated, AuditTargetTypes.Group, marker, BaseTime);

        var page = await GetPageAsync($"?targetId={marker}&targetType={AuditTargetTypes.SystemConfiguration}");

        var entry = Assert.Single(page.Items);
        Assert.Equal(AuditTargetTypes.SystemConfiguration, entry.TargetType);
    }

    [Fact]
    public async Task Filter_by_targetId_returns_only_that_target()
    {
        var mine = Marker();
        var theirs = Marker();
        await SeedAuditAsync(null, AuditActions.GroupCreated, AuditTargetTypes.Group, mine, BaseTime);
        await SeedAuditAsync(null, AuditActions.GroupCreated, AuditTargetTypes.Group, theirs, BaseTime);

        var page = await GetPageAsync($"?targetId={mine}");

        var entry = Assert.Single(page.Items);
        Assert.Equal(mine, entry.TargetId);
    }

    [Fact]
    public async Task Filter_by_date_range_is_inclusive_at_both_ends()
    {
        var marker = Marker();
        await SeedAuditAsync(null, AuditActions.UsageSynced, string.Empty, marker, BaseTime.AddDays(-1));
        await SeedAuditAsync(null, AuditActions.UsageSynced, string.Empty, marker, BaseTime);
        await SeedAuditAsync(null, AuditActions.UsageSynced, string.Empty, marker, BaseTime.AddDays(1));
        await SeedAuditAsync(null, AuditActions.UsageSynced, string.Empty, marker, BaseTime.AddDays(2));

        var from = Uri.EscapeDataString(BaseTime.ToString("O"));
        var to = Uri.EscapeDataString(BaseTime.AddDays(1).ToString("O"));
        var page = await GetPageAsync($"?targetId={marker}&fromDate={from}&toDate={to}");

        Assert.Equal(2, page.TotalCount);
        Assert.Equal([BaseTime.AddDays(1), BaseTime], page.Items.Select(i => i.OccurredDate));
        Assert.All(page.Items, i => Assert.Null(i.TargetType)); // "" in the row → null on the wire
    }

    [Fact]
    public async Task Date_filter_with_a_non_utc_offset_compares_by_instant_not_by_wall_clock()
    {
        var marker = Marker();
        await SeedAuditAsync(null, AuditActions.UsageSynced, string.Empty, marker, BaseTime.AddHours(-1));
        await SeedAuditAsync(null, AuditActions.UsageSynced, string.Empty, marker, BaseTime.AddHours(1));

        // 07:00-05:00 is the same instant as BaseTime (12:00Z); only the +1h row is at/after it.
        var from = Uri.EscapeDataString(BaseTime.ToOffset(TimeSpan.FromHours(-5)).ToString("O"));
        var page = await GetPageAsync($"?targetId={marker}&fromDate={from}");

        var entry = Assert.Single(page.Items);
        Assert.Equal(BaseTime.AddHours(1), entry.OccurredDate);
    }

    private static string Marker() => Guid.NewGuid().ToString("N");

    private async Task<PagedResult<AuditLogEntryResponse>> GetPageAsync(string queryString)
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);

        var response = await client.GetAsync(new Uri(AuditPath + queryString, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.Content.ReadFromJsonAsync<PagedResult<AuditLogEntryResponse>>(JsonOptions);
        Assert.NotNull(page);
        return page;
    }

    private async Task SeedAuditAsync(int? actorUserId, string action, string targetType, string targetId, DateTimeOffset occurredDate)
    {
        await using var dbContext = factory.CreateDbContext();
        dbContext.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actorUserId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Details = string.Empty,
            OccurredDate = occurredDate,
        });
        await dbContext.SaveChangesAsync();
    }
}
