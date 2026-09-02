using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FoundryGate.Core.Entra;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// <c>/api/v1/groups</c> through the real pipeline (#30, #31, #41): the admin-only auth matrix, the
/// created/updated/deleted status codes and their conflicts, the paged envelopes, the member roster,
/// and Entra sync — <c>503</c> on the default host (<c>Entra:Enabled</c> false) and the real
/// reconciliation on a derived host whose directory is a <see cref="FakeEntraDirectoryClient"/>.
/// </summary>
/// <remarks>
/// One database per class, shared by its tests: every group is named with a unique marker and every
/// assertion is about rows the test itself created — never an absolute count.
/// </remarks>
public class GroupsEndpointTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private const string GroupsPath = "/api/v1/groups";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // -- Auth contract --

    [Theory]
    [InlineData("GET", GroupsPath)]
    [InlineData("POST", GroupsPath)]
    [InlineData("GET", GroupsPath + "/1")]
    [InlineData("PUT", GroupsPath + "/1")]
    [InlineData("DELETE", GroupsPath + "/1")]
    [InlineData("GET", GroupsPath + "/1/members")]
    [InlineData("POST", GroupsPath + "/1/members")]
    [InlineData("DELETE", GroupsPath + "/1/members/2")]
    [InlineData("POST", GroupsPath + "/1/sync-entra")]
    [InlineData("POST", GroupsPath + "/sync-entra")]
    public async Task Anonymous_request_returns_401(string method, string path)
    {
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", GroupsPath)]
    [InlineData("POST", GroupsPath)]
    [InlineData("GET", GroupsPath + "/1")]
    [InlineData("PUT", GroupsPath + "/1")]
    [InlineData("DELETE", GroupsPath + "/1")]
    [InlineData("GET", GroupsPath + "/1/members")]
    [InlineData("POST", GroupsPath + "/1/members")]
    [InlineData("DELETE", GroupsPath + "/1/members/2")]
    [InlineData("POST", GroupsPath + "/1/sync-entra")]
    [InlineData("POST", GroupsPath + "/sync-entra")]
    public async Task Authenticated_non_admin_returns_403(string method, string path)
    {
        var dev = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(dev.EntraObjectId, isAdmin: false);

        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative));
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // -- CRUD --

    [Fact]
    public async Task Create_returns_201_with_a_location_that_serves_the_group()
    {
        var admin = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var name = Marker("Create");

        var response = await client.PostAsJsonAsync(
            new Uri(GroupsPath, UriKind.Relative),
            new CreateGroupRequest { Name = name, MonthlyTokenQuota = 20_000_000 },
            JsonOptions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<GroupResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.Equal(name, created.Name);
        Assert.Equal(20_000_000, created.MonthlyTokenQuota);

        // Generated URLs are lowercase (#129) and the Location must actually resolve.
        var location = response.Headers.Location;
        Assert.NotNull(location);
        Assert.Equal($"/api/v1/groups/{created.GroupId}", location.AbsolutePath);
        var followed = await client.GetFromJsonAsync<GroupDetailResponse>(location, JsonOptions);
        Assert.NotNull(followed);
        Assert.Equal(created.GroupId, followed.Group.GroupId);
        Assert.Empty(followed.Members);
        Assert.Equal(factory.TimeProvider.GetUtcNow(), followed.Group.CreatedDate);
    }

    [Fact]
    public async Task Create_with_a_duplicate_name_returns_409_and_with_a_non_tier_quota_returns_400()
    {
        var admin = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var name = Marker("Duplicate");
        _ = await CreateGroupAsync(client, name);

        var duplicate = await client.PostAsJsonAsync(
            new Uri(GroupsPath, UriKind.Relative),
            new CreateGroupRequest { Name = name },
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var offTier = await client.PostAsJsonAsync(
            new Uri(GroupsPath, UriKind.Relative),
            new CreateGroupRequest { Name = Marker("OffTier"), MonthlyTokenQuota = 1234 },
            JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, offTier.StatusCode);
        var problem = await offTier.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions);
        Assert.NotNull(problem);
        Assert.Contains("not a configured budget tier", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_returns_the_paged_envelope_and_honours_search()
    {
        var admin = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var marker = Guid.NewGuid().ToString("N")[..8];
        _ = await CreateGroupAsync(client, $"Zeta {marker}");
        _ = await CreateGroupAsync(client, $"Alpha {marker}");

        var page = await client.GetFromJsonAsync<PagedResult<GroupResponse>>(
            new Uri($"{GroupsPath}?search={marker}", UriKind.Relative), JsonOptions);

        Assert.NotNull(page);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(1, page.Page);
        Assert.Equal(PagedRequest.DefaultPageSize, page.PageSize);
        Assert.Equal([$"Alpha {marker}", $"Zeta {marker}"], page.Items.Select(g => g.Name));
    }

    [Fact]
    public async Task Get_and_update_of_an_unknown_group_return_404()
    {
        var admin = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);

        var get = await client.GetAsync(new Uri($"{GroupsPath}/987654", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);

        var put = await client.PutAsJsonAsync(
            new Uri($"{GroupsPath}/987654", UriKind.Relative),
            new UpdateGroupRequest { Name = Marker("Ghost") },
            JsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
    }

    [Fact]
    public async Task Update_changes_the_quota_and_re_resolves_the_members_allocation()
    {
        var admin = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var name = Marker("Update");
        var group = await CreateGroupAsync(client, name, quota: 5_000_000);
        var member = await factory.SeedUserAsync();
        _ = await AddMemberAsync(client, group.GroupId, member.UserId);

        var response = await client.PutAsJsonAsync(
            new Uri($"{GroupsPath}/{group.GroupId}", UriKind.Relative),
            new UpdateGroupRequest { Name = name, Description = "now a power group", MonthlyTokenQuota = 20_000_000 },
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.Content.ReadFromJsonAsync<GroupResponse>(JsonOptions);
        Assert.NotNull(updated);
        Assert.Equal(20_000_000, updated.MonthlyTokenQuota);
        Assert.Equal("now a power group", updated.Description);

        await using var dbContext = factory.CreateDbContext();
        var allocation = await dbContext.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == member.UserId);
        Assert.Equal(20_000_000, allocation.AllocatedTokens);
        Assert.Equal(GatewayTiers.Power, allocation.TierProductId);
        Assert.True(await dbContext.AuditLogs.AsNoTracking()
            .AnyAsync(a => a.Action == AuditActions.GroupUpdated && a.TargetId == group.GroupId.ToString()));
    }

    [Fact]
    public async Task Delete_of_a_populated_group_needs_force_and_leaves_the_users_alone()
    {
        var admin = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var group = await CreateGroupAsync(client, Marker("Delete"), quota: 20_000_000);
        var member = await factory.SeedUserAsync();
        _ = await AddMemberAsync(client, group.GroupId, member.UserId);

        var refused = await client.DeleteAsync(new Uri($"{GroupsPath}/{group.GroupId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var forced = await client.DeleteAsync(new Uri($"{GroupsPath}/{group.GroupId}?force=true", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, forced.StatusCode);

        await using var dbContext = factory.CreateDbContext();
        Assert.False(await dbContext.Groups.AsNoTracking().AnyAsync(g => g.GroupId == group.GroupId));
        Assert.False(await dbContext.GroupMembers.AsNoTracking().AnyAsync(m => m.GroupId == group.GroupId));
        Assert.True(await dbContext.Users.AsNoTracking().AnyAsync(u => u.UserId == member.UserId));

        // The group's budget is gone, so the member falls back to the seeded system default.
        var allocation = await dbContext.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == member.UserId);
        Assert.Equal(5_000_000, allocation.AllocatedTokens);

        var gone = await client.DeleteAsync(new Uri($"{GroupsPath}/{group.GroupId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    // -- Membership --

    [Fact]
    public async Task Members_can_be_added_listed_and_removed()
    {
        var admin = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var group = await CreateGroupAsync(client, Marker("Members"), quota: 20_000_000);
        var member = await factory.SeedUserAsync(displayName: "Roster Member");

        var added = await AddMemberAsync(client, group.GroupId, member.UserId);
        Assert.Equal(member.UserId, added.UserId);
        Assert.Equal(admin.UserId, added.AddedByUserId);
        Assert.Equal(factory.TimeProvider.GetUtcNow(), added.AddedDate);

        var roster = await client.GetFromJsonAsync<PagedResult<GroupMemberResponse>>(
            new Uri($"{GroupsPath}/{group.GroupId}/members", UriKind.Relative), JsonOptions);
        Assert.NotNull(roster);
        Assert.Equal([member.UserId], roster.Items.Select(m => m.UserId));

        var repeat = await client.PostAsJsonAsync(
            new Uri($"{GroupsPath}/{group.GroupId}/members", UriKind.Relative),
            new AddGroupMemberRequest { UserId = member.UserId },
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);

        var unknownUser = await client.PostAsJsonAsync(
            new Uri($"{GroupsPath}/{group.GroupId}/members", UriKind.Relative),
            new AddGroupMemberRequest { UserId = 987654 },
            JsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, unknownUser.StatusCode);

        var removed = await client.DeleteAsync(new Uri($"{GroupsPath}/{group.GroupId}/members/{member.UserId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        var removedTwice = await client.DeleteAsync(new Uri($"{GroupsPath}/{group.GroupId}/members/{member.UserId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, removedTwice.StatusCode);

        await using var dbContext = factory.CreateDbContext();
        var allocation = await dbContext.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == member.UserId);
        Assert.Equal(5_000_000, allocation.AllocatedTokens); // back to the system default
    }

    [Fact]
    public async Task Manual_membership_edits_on_an_Entra_linked_group_return_409()
    {
        // The trap: without this, the add succeeds and the next sync-entra silently undoes it.
        var admin = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var group = await CreateGroupAsync(client, Marker("DirectoryOwned"), entraGroupId: Guid.NewGuid().ToString());
        var user = await factory.SeedUserAsync();

        var add = await client.PostAsJsonAsync(
            new Uri($"{GroupsPath}/{group.GroupId}/members", UriKind.Relative),
            new AddGroupMemberRequest { UserId = user.UserId },
            JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, add.StatusCode);
        var problem = await add.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions);
        Assert.NotNull(problem);
        Assert.Contains("sync-entra", problem.Detail, StringComparison.Ordinal);

        await using (var seed = factory.CreateDbContext())
        {
            _ = seed.GroupMembers.Add(new GroupMember { GroupId = group.GroupId, UserId = user.UserId });
            _ = await seed.SaveChangesAsync();
        }

        var remove = await client.DeleteAsync(new Uri($"{GroupsPath}/{group.GroupId}/members/{user.UserId}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Conflict, remove.StatusCode);
    }

    [Fact]
    public async Task Create_refuses_a_second_group_linked_to_the_same_Entra_group()
    {
        var admin = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var entraGroupId = Guid.NewGuid().ToString();
        _ = await CreateGroupAsync(client, Marker("LinkOne"), entraGroupId: entraGroupId);

        var response = await client.PostAsJsonAsync(
            new Uri(GroupsPath, UriKind.Relative),
            new CreateGroupRequest { Name = Marker("LinkTwo"), EntraGroupId = entraGroupId },
            JsonOptions);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // -- Entra sync --

    [Fact]
    public async Task Sync_entra_on_a_host_with_Entra_disabled_returns_503_naming_the_setting()
    {
        var admin = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var group = await CreateGroupAsync(client, Marker("Disabled"), entraGroupId: Guid.NewGuid().ToString());

        var response = await client.PostAsync(new Uri($"{GroupsPath}/{group.GroupId}/sync-entra", UriKind.Relative), null);

        // 503, not 400: the request is fine, the host is not configured for the feature.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions);
        Assert.NotNull(problem);
        Assert.Contains("Entra:Enabled", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sync_entra_for_a_group_with_no_link_returns_400_and_for_an_unknown_group_404()
    {
        var admin = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var group = await CreateGroupAsync(client, Marker("Unlinked"));

        var unlinked = await client.PostAsync(new Uri($"{GroupsPath}/{group.GroupId}/sync-entra", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.BadRequest, unlinked.StatusCode);
        var problem = await unlinked.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions);
        Assert.NotNull(problem);
        Assert.Contains("not linked to an Entra group", problem.Detail, StringComparison.Ordinal);

        var unknown = await client.PostAsync(new Uri($"{GroupsPath}/987654/sync-entra", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Fact]
    public async Task Sync_entra_with_a_directory_reconciles_the_roster_and_is_idempotent()
    {
        var admin = await factory.SeedUserAsync();
        var entraGroupId = Guid.NewGuid().ToString();
        using var seedClient = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var group = await CreateGroupAsync(seedClient, Marker("Synced"), quota: 20_000_000, entraGroupId: entraGroupId);

        var joining = await factory.SeedUserAsync();
        var departing = await factory.SeedUserAsync();

        // Seeded directly, not via POST /members: a linked group's roster is Entra's, and the endpoint
        // refuses manual edits (see Manual_membership_edits_on_an_Entra_linked_group_return_409).
        await using (var seed = factory.CreateDbContext())
        {
            _ = seed.GroupMembers.Add(new GroupMember { GroupId = group.GroupId, UserId = departing.UserId });
            _ = await seed.SaveChangesAsync();
        }

        var directory = new FakeEntraDirectoryClient();
        directory.GroupMembers[entraGroupId] = [joining.EntraObjectId, Guid.NewGuid().ToString()];
        using var host = WithDirectory(directory);
        using var client = CreateAdminClient(host, admin.EntraObjectId);

        var response = await client.PostAsync(new Uri($"{GroupsPath}/{group.GroupId}/sync-entra", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<GroupSyncResult>(JsonOptions);
        Assert.Equal(new(group.GroupId, AddedCount: 1, RemovedCount: 1, SkippedUnknownUserCount: 1), result);

        await using (var dbContext = factory.CreateDbContext())
        {
            var membership = await dbContext.GroupMembers.AsNoTracking()
                .SingleAsync(m => m.GroupId == group.GroupId && m.UserId == joining.UserId);
            Assert.Null(membership.AddedByUserId); // system-added, not attributed to the admin
            Assert.False(await dbContext.GroupMembers.AsNoTracking().AnyAsync(m => m.GroupId == group.GroupId && m.UserId == departing.UserId));
            Assert.Equal(20_000_000, (await dbContext.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == joining.UserId)).AllocatedTokens);
            Assert.True(await dbContext.AuditLogs.AsNoTracking()
                .AnyAsync(a => a.Action == AuditActions.GroupEntraSynced && a.TargetId == group.GroupId.ToString()));
        }

        var again = await client.PostAsync(new Uri($"{GroupsPath}/{group.GroupId}/sync-entra", UriKind.Relative), null);
        var second = await again.Content.ReadFromJsonAsync<GroupSyncResult>(JsonOptions);
        Assert.Equal(new(group.GroupId, AddedCount: 0, RemovedCount: 0, SkippedUnknownUserCount: 1), second);
    }

    [Fact]
    public async Task Sync_entra_for_all_groups_returns_one_summary_per_linked_group()
    {
        var admin = await factory.SeedUserAsync();
        var entraGroupId = Guid.NewGuid().ToString();
        using var seedClient = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);
        var linked = await CreateGroupAsync(seedClient, Marker("All-linked"), quota: 20_000_000, entraGroupId: entraGroupId);
        var native = await CreateGroupAsync(seedClient, Marker("All-native"));
        var member = await factory.SeedUserAsync();

        var directory = new FakeEntraDirectoryClient();
        directory.GroupMembers[entraGroupId] = [member.EntraObjectId];
        using var host = WithDirectory(directory);
        using var client = CreateAdminClient(host, admin.EntraObjectId);

        var response = await client.PostAsync(new Uri($"{GroupsPath}/sync-entra", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summaries = await response.Content.ReadFromJsonAsync<List<GroupSyncResult>>(JsonOptions);
        Assert.NotNull(summaries);
        Assert.Contains(summaries, s => s.GroupId == linked.GroupId);
        Assert.DoesNotContain(summaries, s => s.GroupId == native.GroupId);
    }

    // -- Helpers --

    private static string Marker(string prefix) => $"{prefix} {Guid.NewGuid():N}";

    private static async Task<GroupResponse> CreateGroupAsync(HttpClient client, string name, long? quota = null, string? entraGroupId = null)
    {
        var response = await client.PostAsJsonAsync(
            new Uri(GroupsPath, UriKind.Relative),
            new CreateGroupRequest { Name = name, MonthlyTokenQuota = quota, EntraGroupId = entraGroupId },
            JsonOptions);
        _ = response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<GroupResponse>(JsonOptions))!;
    }

    private static async Task<GroupMemberResponse> AddMemberAsync(HttpClient client, int groupId, int userId)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"{GroupsPath}/{groupId}/members", UriKind.Relative),
            new AddGroupMemberRequest { UserId = userId },
            JsonOptions);
        _ = response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<GroupMemberResponse>(JsonOptions))!;
    }

    /// <summary>A host identical to the fixture's except that the directory is <paramref name="directory"/>.</summary>
    private WebApplicationFactory<Program> WithDirectory(FakeEntraDirectoryClient directory) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEntraDirectoryClient>();
            services.AddSingleton<IEntraDirectoryClient>(directory);
        }));

    private static HttpClient CreateAdminClient(WebApplicationFactory<Program> host, string oid)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.OidHeader, oid);
        client.DefaultRequestHeaders.Add(TestAuthHandler.RolesHeader, RoleNames.Admin);
        return client;
    }
}
