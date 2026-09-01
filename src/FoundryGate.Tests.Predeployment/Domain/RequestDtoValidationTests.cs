using System.ComponentModel.DataAnnotations;
using FoundryGate.Domain.Foundry;
using FoundryGate.Domain.Foundry.Contracts;
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

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("12345")]
    [InlineData("11111111-2222-3333-4444-55555555555")] // one hex digit short
    public void CreateGroupRequest_with_a_non_guid_entraGroupId_fails_validation(string entraGroupId)
    {
        var request = new CreateGroupRequest(
            Name: "Platform Team",
            Description: null,
            EntraGroupId: entraGroupId,
            IsUnlimited: false,
            MonthlyTokenQuota: null);

        IList<ValidationResult> results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateGroupRequest.EntraGroupId)));
    }

    [Fact]
    public void CreateGroupRequest_with_a_guid_shaped_entraGroupId_passes_validation()
    {
        var request = new CreateGroupRequest(
            Name: "Platform Team",
            Description: null,
            EntraGroupId: "11111111-2222-3333-4444-555555555555",
            IsUnlimited: false,
            MonthlyTokenQuota: null);

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

    [Fact]
    public void CreateFoundryDeploymentRequest_valid_OpenAI_request_passes_validation()
    {
        Assert.Empty(Validate(ValidFoundryRequest()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")] // below the 2-char minimum
    [InlineData("-leading-hyphen")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    [InlineData("this-deployment-name-is-far-too-long-for-azure-cognitive-services-naming-1")]
    public void CreateFoundryDeploymentRequest_with_an_invalid_deploymentName_fails_validation(string deploymentName)
    {
        var request = ValidFoundryRequest() with { DeploymentName = deploymentName };

        IList<ValidationResult> results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateFoundryDeploymentRequest.DeploymentName)));
    }

    [Theory]
    [InlineData("gpt-4-1-mini")]
    [InlineData("claude-haiku-4-5")]
    [InlineData("gpt-4.1-mini_v2")]
    public void CreateFoundryDeploymentRequest_with_a_valid_deploymentName_passes_validation(string deploymentName)
    {
        var request = ValidFoundryRequest() with { DeploymentName = deploymentName };

        Assert.Empty(Validate(request));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has_underscore")]
    [InlineData("has.dot")]
    [InlineData("-leading-hyphen")]
    [InlineData("trailing-hyphen-")]
    public void CreateFoundryDeploymentRequest_with_an_invalid_accountName_fails_validation(string accountName)
    {
        var request = ValidFoundryRequest() with { AccountName = accountName };

        IList<ValidationResult> results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateFoundryDeploymentRequest.AccountName)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(100_001)]
    public void CreateFoundryDeploymentRequest_with_an_out_of_range_capacity_fails_validation(int capacity)
    {
        var request = ValidFoundryRequest() with { Capacity = capacity };

        IList<ValidationResult> results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateFoundryDeploymentRequest.Capacity)));
    }

    [Fact]
    public void CreateFoundryDeploymentRequest_with_an_undefined_modelFormat_fails_validation()
    {
        var request = ValidFoundryRequest() with { ModelFormat = (FoundryModelFormatType)42 };

        IList<ValidationResult> results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateFoundryDeploymentRequest.ModelFormat)));
    }

    [Fact]
    public void CreateFoundryDeploymentRequest_with_blank_model_fields_fails_validation()
    {
        var request = ValidFoundryRequest() with { ModelName = string.Empty, ModelVersion = " ", SkuName = string.Empty };

        IList<ValidationResult> results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateFoundryDeploymentRequest.ModelName)));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateFoundryDeploymentRequest.ModelVersion)));
        Assert.Contains(results, r => r.MemberNames.Contains(nameof(CreateFoundryDeploymentRequest.SkuName)));
    }

    [Fact]
    public void CreateFoundryDeploymentRequest_default_instance_fails_on_every_required_member()
    {
        // What an empty JSON body binds to: every [Required] string is "" and Capacity is 0.
        IList<ValidationResult> results = Validate(new CreateFoundryDeploymentRequest());

        string[] expected =
        [
            nameof(CreateFoundryDeploymentRequest.AccountName),
            nameof(CreateFoundryDeploymentRequest.DeploymentName),
            nameof(CreateFoundryDeploymentRequest.ModelName),
            nameof(CreateFoundryDeploymentRequest.ModelVersion),
            nameof(CreateFoundryDeploymentRequest.SkuName),
            nameof(CreateFoundryDeploymentRequest.Capacity),
        ];
        Assert.All(expected, member => Assert.Contains(results, r => r.MemberNames.Contains(member)));
    }

    private static CreateFoundryDeploymentRequest ValidFoundryRequest() =>
        new()
        {
            AccountName = "fgtest-eus2",
            DeploymentName = "gpt-4-1-mini",
            ModelFormat = FoundryModelFormatType.OpenAI,
            ModelName = "gpt-4.1-mini",
            ModelVersion = "2025-04-14",
            SkuName = "GlobalStandard",
            Capacity = 10,
        };
}
