using System.ComponentModel.DataAnnotations;
using FoundryGate.Domain.Constants;

namespace FoundryGate.Domain.Foundry.Contracts;

/// <summary>
/// One Azure AI Foundry model deployment as ARM reports it — the admin view behind
/// <c>GET /foundry/deployments</c> (issue #61, plans/20-foundry-provisioning.md). The gateway
/// runs one Foundry account per region (infra/main.bicep <c>foundryRegions</c>), so every row
/// names its <paramref name="AccountName"/>; pooled models appear once per account.
/// </summary>
/// <param name="AccountName">The Foundry (Cognitive Services <c>AIServices</c>) account the deployment lives in.</param>
/// <param name="DeploymentName">The deployment's ARM resource name — what a request's <c>model</c> resolves to at the backend.</param>
/// <param name="ModelFormat">Raw ARM <c>model.format</c> (<c>OpenAI</c>, <c>Anthropic</c>, …). A string, not <see cref="FoundryModelFormatType"/>, so deployments of formats the API cannot create still list.</param>
/// <param name="ModelName">ARM <c>model.name</c>, e.g. <c>gpt-4.1-mini</c> or <c>claude-haiku-4-5</c>.</param>
/// <param name="ModelVersion">ARM <c>model.version</c>; null when ARM reports none.</param>
/// <param name="SkuName">ARM <c>sku.name</c>, e.g. <c>GlobalStandard</c>.</param>
/// <param name="Capacity">ARM <c>sku.capacity</c> — in <b>thousands of tokens per minute</b> (capacity 25 = 25K TPM; #60 direction update). Null when ARM reports none.</param>
/// <param name="ProvisioningState">Raw ARM <c>provisioningState</c>: <c>Accepted</c>, <c>Creating</c>, <c>Succeeded</c>, <c>Failed</c>, <c>Deleting</c>, <c>Canceled</c>, <c>Disabled</c>, <c>Moving</c>. Only <c>Succeeded</c> serves traffic (E-007 d).</param>
/// <param name="CreatedDate">ARM <c>systemData.createdAt</c>, when present.</param>
/// <param name="ModifiedDate">ARM <c>systemData.lastModifiedAt</c>, when present.</param>
public record FoundryDeploymentResponse(
    string AccountName,
    string DeploymentName,
    string ModelFormat,
    string ModelName,
    string? ModelVersion,
    string SkuName,
    int? Capacity,
    string ProvisioningState,
    DateTimeOffset? CreatedDate,
    DateTimeOffset? ModifiedDate);

/// <summary>
/// The developer-facing subset of a deployment — <c>GET /foundry/models</c>, any authenticated
/// user. Just enough to configure a CLI (the <c>/me</c> "Configure your CLI" panel): which
/// deployment names exist, what model they are, and whether they are serving yet. No SKU,
/// capacity, or per-account breakdown: a pooled model is listed once, not once per region.
/// </summary>
/// <param name="DeploymentName">The deployment name — what a developer's CLI pins <c>model</c> to (or the alias that resolves to it; plans/25).</param>
/// <param name="ModelName">ARM <c>model.name</c>.</param>
/// <param name="ModelVersion">ARM <c>model.version</c>; null when ARM reports none.</param>
/// <param name="ModelFormat">Raw ARM <c>model.format</c>, which tells the developer which front door / auth header style the model speaks (see <see cref="Config.ModelProviderType"/>).</param>
/// <param name="ProvisioningState">Raw ARM <c>provisioningState</c>. For a model deployed in several accounts this is <c>Succeeded</c> if <em>any</em> account is serving it (the APIM pool routes around the rest), otherwise the primary account's state.</param>
public record FoundryModelResponse(
    string DeploymentName,
    string ModelName,
    string? ModelVersion,
    string ModelFormat,
    string ProvisioningState);

/// <summary>
/// <c>POST /foundry/deployments</c> body (admin). Creates <b>one</b> deployment in <b>one</b>
/// account; a pooled model (one per region) is several requests, one per account, so that each
/// create is an explicit, auditable decision — never a loop the API drives on the admin's
/// behalf (E-007: Anthropic deployments punish churn).
/// </summary>
/// <remarks>
/// An init-property record, not a positional one: ASP.NET Core MVC throws at validation time for
/// a positional record whose validation attributes sit on the generated <em>properties</em>
/// (<c>[property: …]</c>) — "validation metadata must be associated with the constructor
/// parameter" — while <c>System.ComponentModel.DataAnnotations.Validator</c> and Blazor's
/// <c>DataAnnotationsValidator</c> read <em>properties</em> only. Attributes on plain properties
/// are the one placement all three agree on.
/// </remarks>
public record CreateFoundryDeploymentRequest
{
    /// <summary>Target Foundry account — must be one of the gateway's configured accounts (<c>Gateway:FoundryAccountNames</c>). 2–64 characters, letters/digits/hyphens.</summary>
    [Required]
    [StringLength(ValidationConstants.FoundryAccountNameMaxLength, MinimumLength = 2)]
    [RegularExpression(ValidationConstants.FoundryAccountNamePattern)]
    public string AccountName { get; init; } = string.Empty;

    /// <summary>New deployment's name. 2–64 characters; letters, digits, <c>.</c>, <c>_</c>, <c>-</c>; must start with a letter or digit. An existing name is a <c>409</c> — the API never re-PUTs.</summary>
    [Required]
    [StringLength(ValidationConstants.FoundryDeploymentNameMaxLength, MinimumLength = 2)]
    [RegularExpression(ValidationConstants.FoundryDeploymentNamePattern)]
    public string DeploymentName { get; init; } = string.Empty;

    /// <summary>ARM <c>model.format</c>; defaults to <see cref="FoundryModelFormatType.OpenAI"/> when omitted. Only OpenAI can be created through the API today (Anthropic → <c>400</c>, see #107).</summary>
    [EnumDataType(typeof(FoundryModelFormatType))]
    public FoundryModelFormatType ModelFormat { get; init; } = FoundryModelFormatType.OpenAI;

    /// <summary>ARM <c>model.name</c>, exactly as <c>az cognitiveservices model list</c> reports it (e.g. <c>gpt-4.1-mini</c>).</summary>
    [Required]
    [StringLength(ValidationConstants.FoundryModelNameMaxLength)]
    public string ModelName { get; init; } = string.Empty;

    /// <summary>ARM <c>model.version</c> (e.g. <c>2025-04-14</c>). Required so a create is deterministic rather than "whatever the current default is".</summary>
    [Required]
    [StringLength(ValidationConstants.FoundryModelVersionMaxLength)]
    public string ModelVersion { get; init; } = string.Empty;

    /// <summary>ARM <c>sku.name</c>, e.g. <c>GlobalStandard</c>, <c>Standard</c>, <c>DataZoneStandard</c>.</summary>
    [Required]
    [StringLength(ValidationConstants.FoundrySkuNameMaxLength)]
    public string SkuName { get; init; } = string.Empty;

    /// <summary>ARM <c>sku.capacity</c> in <b>thousands of TPM</b> (10 = 10K tokens/minute). Must fit inside the subscription's remaining quota for the model or ARM rejects the create.</summary>
    [Range(1, ValidationConstants.FoundryDeploymentMaxCapacity)]
    public int Capacity { get; init; }
}
