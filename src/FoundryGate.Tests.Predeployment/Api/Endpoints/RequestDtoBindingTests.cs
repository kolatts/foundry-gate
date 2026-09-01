using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Domain.Requests.Contracts;
using FoundryGate.Domain.Users.Contracts;
using FoundryGate.Tests.Predeployment.Support;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// The regression guard for #128 through the real pipeline: every request record without a
/// production controller yet is posted to <see cref="RequestDtoBindingController"/> (registered by
/// <see cref="ApiTestFactory"/>) with an invalid body and must come back as a 400 validation
/// ProblemDetails naming the offending member — not the 500 MVC threw for positional records with
/// <c>[property: …]</c> attributes. <see cref="A_valid_body_binds_and_echoes_back"/> is the other
/// half: the init-property shape also binds.
/// </summary>
public class RequestDtoBindingTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private const string BasePath = "/api/v1/requestdtobinding";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static TheoryData<string, string, string> InvalidBodies => new()
    {
        { "groups", """{"name":""}""", nameof(CreateGroupRequest.Name) },
        { "groups", """{"name":"Platform Team","entraGroupId":"not-a-guid"}""", nameof(CreateGroupRequest.EntraGroupId) },
        { "groups/update", """{"name":"Platform Team","monthlyTokenQuota":-1}""", nameof(UpdateGroupRequest.MonthlyTokenQuota) },
        { "groups/members", """{}""", nameof(AddGroupMemberRequest.UserId) },
        { "users/quota", """{"isUnlimited":false,"monthlyTokenQuota":-1}""", nameof(UpdateUserQuotaRequest.MonthlyTokenQuota) },
        { "requests", """{"requestedQuota":2000000,"justification":"short"}""", nameof(SubmitQuotaIncreaseRequest.Justification) },
        { "requests/review", $$"""{"reviewNotes":"{{new string('x', ValidationConstants.ReviewNotesMaxLength + 1)}}"}""", nameof(ReviewQuotaIncreaseRequest.ReviewNotes) },
        { "config", """{}""", nameof(UpdateSystemConfigRequest.Value) },
    };

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task An_invalid_body_returns_400_validation_problem_details_not_500(string path, string json, string expectedMember)
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString());
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync(new Uri($"{BasePath}/{path}", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ApiError>(JsonOptions);
        Assert.NotNull(problem?.Errors);
        Assert.Contains(expectedMember, problem.Errors.Keys);
    }

    [Fact]
    public async Task A_valid_body_binds_and_echoes_back()
    {
        using var client = factory.CreateClientAs(Guid.NewGuid().ToString());
        var request = new CreateGroupRequest
        {
            Name = "Platform Team",
            Description = "Core platform developers",
            EntraGroupId = "11111111-2222-3333-4444-555555555555",
            IsUnlimited = false,
            MonthlyTokenQuota = 5_000_000,
        };

        var response = await client.PostAsJsonAsync(new Uri($"{BasePath}/groups", UriKind.Relative), request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var echoed = await response.Content.ReadFromJsonAsync<CreateGroupRequest>(JsonOptions);
        Assert.Equal(request, echoed); // record value equality — every property round-tripped
    }
}
