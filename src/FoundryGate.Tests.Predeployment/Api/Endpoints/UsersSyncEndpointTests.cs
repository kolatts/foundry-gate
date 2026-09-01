using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FoundryGate.Api.Services.Entra;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Users.Contracts;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// <c>POST /api/v1/users/sync</c> through the real pipeline: the auth matrix (401/403/200), the
/// <c>400</c> the default host returns while <c>Entra:Enabled</c> is false, and — on a derived host
/// whose <see cref="IEntraDirectoryClient"/> is a <see cref="FakeEntraDirectoryClient"/> — the 200
/// body, the 403 for an unprovisioned admin, and the 409 empty-directory guard.
/// </summary>
/// <remarks>
/// The derived host (<see cref="WebApplicationFactory{TEntryPoint}.WithWebHostBuilder"/>) shares the
/// fixture's SQLite connection and clock, so rows seeded through <paramref name="factory"/> are
/// visible to it. Because the fake directory cannot know about rows other tests in this class seed,
/// a 200 run may deactivate those; every assertion here is therefore on rows this test owns.
/// </remarks>
public class UsersSyncEndpointTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private const string SyncPath = "/api/v1/users/sync";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Anonymous_request_returns_401()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync(new Uri(SyncPath, UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_non_admin_returns_403()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: false);

        var response = await client.PostAsync(new Uri(SyncPath, UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Admin_on_a_host_with_Entra_disabled_gets_400_naming_the_setting()
    {
        var admin = await factory.SeedUserAsync();
        using var client = factory.CreateClientAs(admin.EntraObjectId, isAdmin: true);

        var response = await client.PostAsync(new Uri(SyncPath, UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions);
        Assert.NotNull(problem);
        Assert.Contains("Entra:Enabled", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Admin_with_a_directory_gets_200_with_the_counts_and_the_rows_are_persisted()
    {
        var admin = await factory.SeedUserAsync(displayName: "Sync Admin");
        var newOid = Guid.NewGuid().ToString();
        var directory = new FakeEntraDirectoryClient();
        directory.AssignedUsers.Add(new EntraUser(admin.EntraObjectId, "Sync Admin", admin.Email, null));
        directory.AssignedUsers.Add(new EntraUser(newOid, "Imported Dev", "imported@contoso.test", "E42"));
        using var host = WithDirectory(directory);
        using var client = CreateAdminClient(host, admin.EntraObjectId);

        var response = await client.PostAsync(new Uri(SyncPath, UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var result = await response.Content.ReadFromJsonAsync<UserSyncResult>(JsonOptions);
        Assert.NotNull(result);
        Assert.Equal(1, result.AddedCount);
        Assert.True(result.UpdatedCount >= 1, "the calling admin is in the directory and must count as updated");
        Assert.Equal(0, result.SkippedGroupAssignmentCount);

        await using var dbContext = factory.CreateDbContext();
        var imported = await dbContext.Users.AsNoTracking().SingleAsync(u => u.EntraObjectId == newOid);
        Assert.Equal("Imported Dev", imported.DisplayName);
        Assert.Equal("E42", imported.EmployeeId);
        Assert.True(imported.IsActive);
        Assert.Equal(string.Empty, imported.ApimSubscriptionId);
        Assert.Equal(factory.TimeProvider.GetUtcNow(), imported.LastSyncedDate);

        var audit = await dbContext.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditActions.UsersSynced && a.ActorUserId == admin.UserId)
            .ToListAsync();
        _ = Assert.Single(audit);
    }

    [Fact]
    public async Task Admin_without_a_User_row_who_is_not_in_the_directory_gets_403_pointing_at_users_me()
    {
        var directory = new FakeEntraDirectoryClient();
        directory.AssignedUsers.Add(new EntraUser(Guid.NewGuid().ToString(), "Someone", "someone@contoso.test", null));
        using var host = WithDirectory(directory);
        using var client = CreateAdminClient(host, Guid.NewGuid().ToString());

        var response = await client.PostAsync(new Uri(SyncPath, UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(JsonOptions);
        Assert.NotNull(problem);
        Assert.Contains("GET /users/me", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_directory_with_active_local_users_gets_409_and_deactivates_nobody()
    {
        var admin = await factory.SeedUserAsync();
        using var host = WithDirectory(new FakeEntraDirectoryClient());
        using var client = CreateAdminClient(host, admin.EntraObjectId);

        var response = await client.PostAsync(new Uri(SyncPath, UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var dbContext = factory.CreateDbContext();
        Assert.True((await dbContext.Users.AsNoTracking().SingleAsync(u => u.UserId == admin.UserId)).IsActive);
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
