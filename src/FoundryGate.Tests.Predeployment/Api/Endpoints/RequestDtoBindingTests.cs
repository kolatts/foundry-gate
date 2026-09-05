using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Foundry.Contracts;
using FoundryGate.Domain.Gateway.Contracts;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Domain.Requests.Contracts;
using FoundryGate.Domain.Users.Contracts;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// The regression guard for #128 through the real pipeline: every request record Domain defines is
/// posted to <b>its own production endpoint</b> with an invalid body and must come back as a 400
/// validation ProblemDetails naming the offending member — not the 500 MVC threw for positional
/// records carrying <c>[property: …]</c> attributes.
/// </summary>
/// <remarks>
/// <para>
/// Until #145 these bodies went to a test-only <c>RequestDtoBindingController</c>, because the records
/// had no controller of their own. Every one of them now does, so the stand-in is gone and the bodies
/// are bound by the code that will bind them in production — a strictly better guard, since a route
/// that stops binding the record is now a failure here rather than a silently passing echo.
/// </para>
/// <para>
/// The ids in the paths deliberately name nothing: model validation runs before the action, so an
/// invalid body is a 400 whether or not group 999999 exists. A 404 from any of these rows would mean
/// validation had stopped running first, which is itself the bug this file exists to catch.
/// <c>UpdateSystemConfigRequest</c> is covered by <c>ConfigEndpointTests</c>'s over-long-value test,
/// where the key has to exist for the request to reach validation at all.
/// </para>
/// </remarks>
public class RequestDtoBindingTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private const string MissingId = "999999";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static TheoryData<string, string, string, string> InvalidBodies => new()
    {
        { "POST", "/api/v1/groups", """{"name":""}""", nameof(CreateGroupRequest.Name) },
        { "POST", "/api/v1/groups", """{"name":"Platform Team","entraGroupId":"not-a-guid"}""", nameof(CreateGroupRequest.EntraGroupId) },
        { "PUT", $"/api/v1/groups/{MissingId}", """{"name":"Platform Team","monthlyTokenQuota":-1}""", nameof(UpdateGroupRequest.MonthlyTokenQuota) },
        { "POST", $"/api/v1/groups/{MissingId}/members", """{}""", nameof(AddGroupMemberRequest.UserId) },
        { "PUT", $"/api/v1/users/{MissingId}/quota", """{"isUnlimited":false,"monthlyTokenQuota":-1}""", nameof(UpdateUserQuotaRequest.MonthlyTokenQuota) },
        { "POST", "/api/v1/requests", """{"requestedQuota":2000000,"justification":"short"}""", nameof(SubmitQuotaIncreaseRequest.Justification) },
        { "POST", $"/api/v1/requests/{MissingId}/approve", $$"""{"reviewNotes":"{{new string('x', ValidationConstants.ReviewNotesMaxLength + 1)}}"}""", nameof(ReviewQuotaIncreaseRequest.ReviewNotes) },
        { "POST", $"/api/v1/requests/{MissingId}/reject", $$"""{"reviewNotes":"{{new string('x', ValidationConstants.ReviewNotesMaxLength + 1)}}"}""", nameof(ReviewQuotaIncreaseRequest.ReviewNotes) },
        { "PATCH", "/api/v1/foundry/deployments/nowhere/nothing/capacity", """{"capacity":0}""", nameof(UpdateFoundryDeploymentCapacityRequest.Capacity) },
        { "PUT", $"/api/v1/gateway/tiers/{GatewayTiers.Standard}/models", """{"aliases":[{"alias":"NotLowerCase","deploymentName":"whatever","pool":"openai"}]}""", $"{nameof(ReplaceTierModelsRequest.Aliases)}[0].{nameof(TierModelAliasRequest.Alias)}" },
        { "PUT", $"/api/v1/gateway/tiers/{GatewayTiers.Standard}/models", """{"aliases":[{"alias":"sonnet","deploymentName":"","pool":"openai"}]}""", $"{nameof(ReplaceTierModelsRequest.Aliases)}[0].{nameof(TierModelAliasRequest.DeploymentName)}" },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task An_invalid_body_returns_400_validation_problem_details_not_500(string method, string path, string json, string expectedMember)
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(path, UriKind.Relative)) { Content = content };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions);
        Assert.NotNull(problem?.Errors);
        Assert.Contains(expectedMember, problem.Errors.Keys);
    }

    [Fact]
    public async Task A_valid_body_binds_every_property_of_an_init_property_record()
    {
        // The other half of #128: the init-property shape MVC needs must also round-trip. Asserted on
        // the created group rather than an echo, so it is the production binder being proven.
        var oid = Guid.NewGuid().ToString();
        _ = await factory.SeedUserAsync(oid);
        using var client = factory.CreateClientAs(oid, isAdmin: true);
        var entraGroupId = Guid.NewGuid().ToString();
        var request = new CreateGroupRequest
        {
            Name = $"Platform Team {Guid.NewGuid():N}",
            Description = "Core platform developers",
            EntraGroupId = entraGroupId,
            IsUnlimited = false,
            MonthlyTokenQuota = 5_000_000,
        };

        var response = await client.PostAsJsonAsync(new Uri("/api/v1/groups", UriKind.Relative), request, JsonOptions);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<GroupResponse>(JsonOptions);
        Assert.NotNull(created);
        Assert.Equal(request.Name, created.Name);
        Assert.Equal(request.Description, created.Description);
        Assert.Equal(entraGroupId, created.EntraGroupId);
        Assert.False(created.IsUnlimited);
        Assert.Equal(5_000_000, created.MonthlyTokenQuota);
    }
}
