using System.ComponentModel.DataAnnotations;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Domain.Requests.Contracts;

namespace FoundryGate.Tests.Predeployment.Domain;

/// <summary>
/// Smoke-checks that request DTOs' <c>DataAnnotations</c> attributes actually fire
/// (plans/03-shared-dtos.md verification: "All request DTOs have at least one
/// validation attribute"). Not exhaustive over every DTO — enough to prove the
/// pattern (<c>[Required]</c>/<c>[StringLength]</c>/<c>[Range]</c> composed on a
/// positional record via <c>[property: ...]</c>) actually validates.
/// </summary>
public class RequestDtoValidationTests
{
    private static IList<ValidationResult> Validate(object dto)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void CreateGroupRequest_with_empty_name_fails_validation()
    {
        var request = new CreateGroupRequest(
            Name: string.Empty,
            Description: null,
            EntraGroupId: null,
            IsUnlimited: false,
            MonthlyTokenQuota: null);

        IList<ValidationResult> results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateGroupRequest.Name)));
    }

    [Fact]
    public void CreateGroupRequest_with_a_valid_name_passes_validation()
    {
        var request = new CreateGroupRequest(
            Name: "Platform Team",
            Description: "Core platform developers",
            EntraGroupId: null,
            IsUnlimited: false,
            MonthlyTokenQuota: 5_000_000);

        Assert.Empty(Validate(request));
    }

    [Fact]
    public void SubmitQuotaIncreaseRequest_with_too_short_justification_fails_validation()
    {
        var request = new SubmitQuotaIncreaseRequest(RequestedQuota: 2_000_000, Justification: "need more");

        IList<ValidationResult> results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(SubmitQuotaIncreaseRequest.Justification)));
    }

    [Fact]
    public void SubmitQuotaIncreaseRequest_with_negative_requestedQuota_fails_validation()
    {
        var request = new SubmitQuotaIncreaseRequest(
            RequestedQuota: -1,
            Justification: "Running large batch evals against the shared model this sprint.");

        IList<ValidationResult> results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(SubmitQuotaIncreaseRequest.RequestedQuota)));
    }
}
