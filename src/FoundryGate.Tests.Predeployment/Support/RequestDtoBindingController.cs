using FoundryGate.Api.Controllers;
using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Domain.Requests.Contracts;
using FoundryGate.Domain.Users.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// Test-only controller — this assembly is registered as an application part by
/// <see cref="Api.ApiTestFactory"/> — that binds every request record Domain defines ahead of
/// its production controller (the users/groups/requests/config waves). Each action echoes the
/// bound body; what matters is what happens <em>before</em> the action runs: an invalid body must
/// be a 400 ProblemDetails, never the 500 a positional record with <c>[property: …]</c> attributes
/// produced (#128). Once a production controller binds one of these records, its own endpoint
/// tests take over and the matching action here can go.
/// </summary>
public sealed class RequestDtoBindingController : ApiControllerBase
{
    [HttpPost("groups")]
    public CreateGroupRequest CreateGroup([FromBody] CreateGroupRequest request) => request;

    [HttpPost("groups/update")]
    public UpdateGroupRequest UpdateGroup([FromBody] UpdateGroupRequest request) => request;

    [HttpPost("groups/members")]
    public AddGroupMemberRequest AddGroupMember([FromBody] AddGroupMemberRequest request) => request;

    [HttpPost("users/quota")]
    public UpdateUserQuotaRequest UpdateUserQuota([FromBody] UpdateUserQuotaRequest request) => request;

    [HttpPost("requests")]
    public SubmitQuotaIncreaseRequest SubmitQuotaIncrease([FromBody] SubmitQuotaIncreaseRequest request) => request;

    [HttpPost("requests/review")]
    public ReviewQuotaIncreaseRequest ReviewQuotaIncrease([FromBody] ReviewQuotaIncreaseRequest request) => request;

    [HttpPost("config")]
    public UpdateSystemConfigRequest UpdateSystemConfig([FromBody] UpdateSystemConfigRequest request) => request;
}
