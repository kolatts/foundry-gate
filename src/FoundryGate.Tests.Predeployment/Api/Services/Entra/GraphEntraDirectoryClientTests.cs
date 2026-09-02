using System.Net;
using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Entra;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;

namespace FoundryGate.Tests.Predeployment.Api.Services.Entra;

/// <summary>
/// The real <see cref="GraphEntraDirectoryClient"/> over the real <see cref="GraphServiceClient"/>
/// pipeline, with HTTP scripted by <see cref="StubHttpMessageHandler"/> — no tenant, no token. Pins
/// the wire shape (which endpoints, which <c>$select</c>, <c>$top</c>, the 15-value <c>in</c> chunks),
/// next-link paging, principal-type filtering, the once-only service-principal resolution, and the
/// null/404 and field-fallback mapping rules.
/// </summary>
public class GraphEntraDirectoryClientTests
{
    private const string BaseUrl = "https://graph.test/v1.0";
    private const string ClientId = "11111111-1111-1111-1111-111111111111";
    private const string ServicePrincipalId = "22222222-2222-2222-2222-222222222222";

    /// <summary>A user who reaches the app only through a group nested inside the assigned group.</summary>
    private const string NestedMember = "aaaaaaaa-0000-0000-0000-000000000003";

    private readonly StubHttpMessageHandler _http = new();

    [Fact]
    public async Task ListAssignedUsersAsync_follows_nextLink_skips_non_user_principals_and_hydrates_in_chunks_of_15()
    {
        var ids = Enumerable.Range(0, 20).Select(i => Guid.Parse($"aaaaaaaa-0000-0000-0000-{i:D12}").ToString()).ToArray();
        var assignmentsUrl = $"{BaseUrl}/servicePrincipals/{ServicePrincipalId}/appRoleAssignedTo";
        _http
            .OnJson(
                url => url.StartsWith(assignmentsUrl + "?", StringComparison.Ordinal) && !url.Contains("skiptoken", StringComparison.Ordinal),
                AssignmentsPage(ids.Take(10), nextLink: $"{assignmentsUrl}?$skiptoken=page2", extra: """{"principalId":"33333333-3333-3333-3333-333333333333","principalType":"Group","principalDisplayName":"AI Developers"}"""))
            .OnJson(
                url => url.Contains("skiptoken=page2", StringComparison.Ordinal),
                AssignmentsPage(ids.Skip(10), nextLink: null, extra: """{"principalId":"44444444-4444-4444-4444-444444444444","principalType":"ServicePrincipal"}"""))
            .OnJson(
                url => url.Contains("/groups/33333333-3333-3333-3333-333333333333/transitiveMembers", StringComparison.Ordinal),
                """{"value":[]}""")
            .OnJson(
                url => url.StartsWith($"{BaseUrl}/users?$filter=id in ('{ids[0]}'", StringComparison.Ordinal),
                UsersPage(ids.Take(15)))
            .OnJson(
                url => url.StartsWith($"{BaseUrl}/users?$filter=id in ('{ids[15]}'", StringComparison.Ordinal),
                UsersPage(ids.Skip(15)));

        var assigned = await CreateClient(ServicePrincipalId).ListAssignedUsersAsync(CancellationToken.None);

        var users = assigned.Users;
        Assert.Equal(20, users.Count);
        Assert.Equal(ids, users.Select(u => u.ObjectId));
        Assert.All(users, u => Assert.StartsWith("User ", u.DisplayName, StringComparison.Ordinal));
        // The Group principal is expanded (and so reported as skipped by nothing); the ServicePrincipal
        // one is dropped without a directory call at all.
        Assert.Empty(assigned.SkippedGroupAssignments);
        _ = Assert.Single(_http.Requests, r => r.Contains("/groups/33333333-3333-3333-3333-333333333333/transitiveMembers", StringComparison.Ordinal));
        Assert.DoesNotContain(_http.Requests, r => r.Contains("44444444-4444-4444-4444-444444444444", StringComparison.Ordinal));

        var firstAssignments = _http.Requests[0];
        Assert.StartsWith(assignmentsUrl, firstAssignments, StringComparison.Ordinal);
        Assert.Contains("$select=principalId,principalType,principalDisplayName", firstAssignments, StringComparison.Ordinal);
        Assert.Contains("$top=999", firstAssignments, StringComparison.Ordinal);
        Assert.Equal($"{assignmentsUrl}?$skiptoken=page2", _http.Requests[1]);

        var userRequests = _http.Requests.Where(r => r.StartsWith($"{BaseUrl}/users?", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, userRequests.Count); // 20 ids → 15 + 5
        Assert.All(userRequests, r => Assert.Contains("$select=id,displayName,mail,userPrincipalName,employeeId", r, StringComparison.Ordinal));
        Assert.Equal(15, userRequests[0].Split("','").Length);
        Assert.Equal(5, userRequests[1].Split("','").Length);
        Assert.DoesNotContain(_http.Requests, r => r.Contains("servicePrincipals(appId=", StringComparison.Ordinal)); // configured id → no resolution
    }

    [Fact]
    public async Task ListAssignedUsersAsync_expands_a_group_assignment_to_its_transitive_members_and_reports_no_skipped_group()
    {
        // The enterprise pattern: one person assigned directly, the rest through a security group —
        // one of whom is ALSO assigned directly, so de-duplication has something to prove (#121).
        const string GroupId = "33333333-3333-3333-3333-333333333333";
        var direct = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001").ToString();
        var viaGroup = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002").ToString();
        var membersUrl = $"{BaseUrl}/groups/{GroupId}/transitiveMembers";
        _http
            .OnJson(
                url => url.Contains("/appRoleAssignedTo", StringComparison.Ordinal),
                AssignmentsPage([direct, viaGroup], nextLink: null, extra: $$"""{"principalId":"{{GroupId}}","principalType":"Group","principalDisplayName":"AI Developers"}"""))
            .OnJson(
                url => url.StartsWith(membersUrl + "?", StringComparison.Ordinal) && !url.Contains("skiptoken", StringComparison.Ordinal),
                $$"""
                {"value":[
                  {"@odata.type":"#microsoft.graph.user","id":"{{viaGroup}}"},
                  {"@odata.type":"#microsoft.graph.group","id":"nested-group"}
                ],"@odata.nextLink":"{{membersUrl}}?$skiptoken=p2"}
                """)
            .OnJson(
                url => url.Contains("skiptoken=p2", StringComparison.Ordinal),
                $$"""{"value":[{"@odata.type":"#microsoft.graph.user","id":"{{NestedMember}}"}]}""")
            .OnJson(
                url => url.StartsWith($"{BaseUrl}/users?", StringComparison.Ordinal),
                UsersPage([direct, viaGroup, NestedMember]));

        var assigned = await CreateClient(ServicePrincipalId).ListAssignedUsersAsync(CancellationToken.None);

        // Expansion succeeded, so nothing is reported as skipped — departure detection stays on.
        Assert.Empty(assigned.SkippedGroupAssignments);
        Assert.Equal([direct, viaGroup, NestedMember], assigned.Users.Select(u => u.ObjectId).Order());

        // One hydration request for the merged set: the duplicate did not become a fourth id.
        var hydration = Assert.Single(_http.Requests, r => r.StartsWith($"{BaseUrl}/users?", StringComparison.Ordinal));
        Assert.Equal(3, hydration.Split("','").Length);
        Assert.Contains("$select=id", _http.Requests[1], StringComparison.Ordinal); // members are read by id only
        Assert.Contains("$top=999", _http.Requests[1], StringComparison.Ordinal);
        Assert.Equal($"{membersUrl}?$skiptoken=p2", _http.Requests[2]); // nextLink followed
    }

    [Fact]
    public async Task ListAssignedUsersAsync_reports_a_group_it_cannot_expand_and_still_returns_the_users_it_could_read()
    {
        // Graph refuses one of two groups (a missing role, or a deleted group). The other group's
        // members still land; the failed one is reported so the sync suspends departure detection
        // rather than deactivating everyone it covers on a knowingly partial view.
        const string ReadableGroupId = "33333333-3333-3333-3333-333333333333";
        const string DeniedGroupId = "88888888-8888-8888-8888-888888888888";
        var viaGroup = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002").ToString();
        _http
            .OnJson(
                url => url.Contains("/appRoleAssignedTo", StringComparison.Ordinal),
                AssignmentsPage(
                    [],
                    nextLink: null,
                    extra: $$"""
                    {"principalId":"{{ReadableGroupId}}","principalType":"Group","principalDisplayName":"AI Developers"},
                    {"principalId":"{{DeniedGroupId}}","principalType":"Group","principalDisplayName":"Platform Team"}
                    """))
            .OnJson(
                url => url.StartsWith($"{BaseUrl}/groups/{ReadableGroupId}/transitiveMembers", StringComparison.Ordinal),
                $$"""{"value":[{"@odata.type":"#microsoft.graph.user","id":"{{viaGroup}}"}]}""")
            .OnJson(
                url => url.StartsWith($"{BaseUrl}/groups/{DeniedGroupId}/transitiveMembers", StringComparison.Ordinal),
                """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}""",
                HttpStatusCode.Forbidden)
            .OnJson(
                url => url.StartsWith($"{BaseUrl}/users?", StringComparison.Ordinal),
                UsersPage([viaGroup]));

        var assigned = await CreateClient(ServicePrincipalId).ListAssignedUsersAsync(CancellationToken.None);

        var skipped = Assert.Single(assigned.SkippedGroupAssignments);
        Assert.Equal(new EntraGroupAssignment(DeniedGroupId, "Platform Team"), skipped);
        Assert.Equal(viaGroup, Assert.Single(assigned.Users).ObjectId);
    }

    [Fact]
    public async Task ListAssignedUsersAsync_resolves_the_service_principal_from_the_client_id_once_and_caches_it()
    {
        _http
            .OnJson(
                url => url.StartsWith($"{BaseUrl}/servicePrincipals(appId='{ClientId}')", StringComparison.Ordinal),
                $$$"""{"id":"{{{ServicePrincipalId}}}"}""")
            .OnJson(
                url => url.StartsWith($"{BaseUrl}/servicePrincipals/{ServicePrincipalId}/appRoleAssignedTo", StringComparison.Ordinal),
                """{"value":[]}""");
        var client = CreateClient(servicePrincipalObjectId: null);

        _ = await client.ListAssignedUsersAsync(CancellationToken.None);
        _ = await client.ListAssignedUsersAsync(CancellationToken.None);

        var resolutions = _http.Requests.Where(r => r.Contains("servicePrincipals(appId=", StringComparison.Ordinal)).ToList();
        var resolution = Assert.Single(resolutions);
        Assert.Contains("$select=id", resolution, StringComparison.Ordinal);
        Assert.Equal(2, _http.Requests.Count(r => r.Contains("/appRoleAssignedTo", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ListAssignedUsersAsync_fails_clearly_when_no_service_principal_exists_for_the_client_id()
    {
        _http.OnNotFound(url => url.Contains("servicePrincipals(appId=", StringComparison.Ordinal));
        var client = CreateClient(servicePrincipalObjectId: null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ListAssignedUsersAsync(CancellationToken.None));

        Assert.Contains(ClientId, exception.Message, StringComparison.Ordinal);
        Assert.Contains("Entra:ServicePrincipalObjectId", exception.Message, StringComparison.Ordinal);

        // Not cached: the next call tries again (so a permission granted afterwards takes effect).
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ListAssignedUsersAsync(CancellationToken.None));
        Assert.Equal(2, _http.Requests.Count);
    }

    [Fact]
    public async Task GetUserAsync_selects_the_stored_fields_and_falls_back_to_the_upn_for_blank_name_and_mail()
    {
        const string Oid = "55555555-5555-5555-5555-555555555555";
        _http.OnJson(
            url => url.StartsWith($"{BaseUrl}/users/{Oid}", StringComparison.Ordinal),
            $$"""{"id":"{{Oid}}","displayName":null,"mail":null,"userPrincipalName":"ada@contoso.test","employeeId":"   "}""");

        var user = await CreateClient(ServicePrincipalId).GetUserAsync(Oid, CancellationToken.None);

        Assert.NotNull(user);
        Assert.Equal(Oid, user.ObjectId);
        Assert.Equal("ada@contoso.test", user.DisplayName);
        Assert.Equal("ada@contoso.test", user.Email);
        Assert.Null(user.EmployeeId);
        Assert.Contains("$select=id,displayName,mail,userPrincipalName,employeeId", Assert.Single(_http.Requests), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetUserAsync_prefers_displayName_and_mail_when_present_and_trims_them()
    {
        const string Oid = "55555555-5555-5555-5555-555555555555";
        _http.OnJson(
            url => url.StartsWith($"{BaseUrl}/users/{Oid}", StringComparison.Ordinal),
            $$"""{"id":"{{Oid}}","displayName":" Ada Lovelace ","mail":"Ada.Lovelace@contoso.test","userPrincipalName":"ada@contoso.test","employeeId":"E1"}""");

        var user = await CreateClient(ServicePrincipalId).GetUserAsync(Oid, CancellationToken.None);

        Assert.Equal(new EntraUser(Oid, "Ada Lovelace", "Ada.Lovelace@contoso.test", "E1"), user);
    }

    [Fact]
    public async Task GetUserAsync_returns_null_when_Graph_says_404()
    {
        _http.OnNotFound(url => url.Contains("/users/", StringComparison.Ordinal));

        var user = await CreateClient(ServicePrincipalId).GetUserAsync("66666666-6666-6666-6666-666666666666", CancellationToken.None);

        Assert.Null(user);
    }

    [Theory]
    [InlineData(false, "members")]
    [InlineData(true, "transitiveMembers")]
    public async Task ListGroupMemberIdsAsync_pages_the_requested_relationship_and_yields_only_user_ids(bool transitive, string relationship)
    {
        const string GroupId = "77777777-7777-7777-7777-777777777777";
        var membersUrl = $"{BaseUrl}/groups/{GroupId}/{relationship}";
        _http
            .OnJson(
                url => url.StartsWith(membersUrl + "?", StringComparison.Ordinal) && !url.Contains("skiptoken", StringComparison.Ordinal),
                $$"""
                {"value":[
                  {"@odata.type":"#microsoft.graph.user","id":"u1"},
                  {"@odata.type":"#microsoft.graph.group","id":"nested-group"},
                  {"@odata.type":"#microsoft.graph.device","id":"d1"}
                ],"@odata.nextLink":"{{membersUrl}}?$skiptoken=p2"}
                """)
            .OnJson(
                url => url.Contains("skiptoken=p2", StringComparison.Ordinal),
                """{"value":[{"@odata.type":"#microsoft.graph.user","id":"u2"},{"@odata.type":"#microsoft.graph.servicePrincipal","id":"sp1"}]}""");

        var ids = await CreateClient(ServicePrincipalId).ListGroupMemberIdsAsync(GroupId, transitive, CancellationToken.None).ToListAsync();

        Assert.Equal(["u1", "u2"], ids);
        Assert.Equal(2, _http.Requests.Count);
        Assert.Contains("$select=id", _http.Requests[0], StringComparison.Ordinal);
        Assert.Contains("$top=999", _http.Requests[0], StringComparison.Ordinal);
        Assert.Equal($"{membersUrl}?$skiptoken=p2", _http.Requests[1]);
    }

    [Fact]
    public async Task ListGroupMemberIdsAsync_rejects_a_blank_group_id()
    {
        var client = CreateClient(ServicePrincipalId);

        _ = await Assert.ThrowsAsync<ArgumentException>(() => client.ListGroupMemberIdsAsync(" ", false, CancellationToken.None).ToListAsync().AsTask());
        Assert.Empty(_http.Requests);
    }

    private GraphEntraDirectoryClient CreateClient(string? servicePrincipalObjectId)
    {
        var graph = new GraphServiceClient(new HttpClient(_http), new AnonymousAuthenticationProvider(), BaseUrl);
        var entra = new EntraOptions { Enabled = true, ServicePrincipalObjectId = servicePrincipalObjectId, GraphBaseUrl = BaseUrl };
        var azureAd = new AzureAdOptions { TenantId = Guid.Empty.ToString(), ClientId = ClientId, Audience = $"api://{ClientId}" };

        return new GraphEntraDirectoryClient(graph, entra, azureAd, NullLogger<GraphEntraDirectoryClient>.Instance);
    }

    private static string AssignmentsPage(IEnumerable<string> userIds, string? nextLink, string extra)
    {
        var items = userIds.Select(id => $$"""{"principalId":"{{id}}","principalType":"User","principalDisplayName":"User {{id}}"}""").Append(extra);
        var next = nextLink is null ? string.Empty : $$""","@odata.nextLink":"{{nextLink}}" """;
        return $$"""{"value":[{{string.Join(",", items)}}]{{next}}}""";
    }

    private static string UsersPage(IEnumerable<string> ids)
    {
        var items = ids.Select(id => $$"""{"id":"{{id}}","displayName":"User {{id}}","mail":"{{id}}@contoso.test","userPrincipalName":"{{id}}@contoso.test","employeeId":null}""");
        return $$"""{"value":[{{string.Join(",", items)}}]}""";
    }
}
