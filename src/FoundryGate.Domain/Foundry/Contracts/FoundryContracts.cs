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
/// One model the configured Foundry accounts can actually serve — <c>GET /foundry/catalog</c>,
/// admin-only (#173). Read from ARM's per-account model list, which is where the create call's
/// <c>model.name</c> / <c>model.version</c> / <c>sku.name</c> have to come from anyway; the create
/// dialog offered a hardcoded array until this existed, and a hardcoded model list goes stale the
/// week after it ships.
/// </summary>
/// <remarks>
/// Not scoped to an account: the gateway runs one account per region and a model is normally
/// deployable in all of them, so a per-account breakdown would be a list of near-duplicates for a
/// form that names one account at a time. Entries are merged across accounts by
/// (<paramref name="ModelFormat"/>, <paramref name="ModelName"/>, <paramref name="ModelVersion"/>) and
/// their SKUs unioned, so what an admin sees is "any configured account can serve this". A model only
/// one region carries is still listed — ARM decides the create, and it will refuse an account that
/// cannot serve it, with a message the admin can act on.
/// </remarks>
/// <param name="ModelFormat">
/// ARM <c>model.format</c> (<c>OpenAI</c>, <c>Anthropic</c>, …). Anthropic models are listed for
/// visibility — what an account can serve is a fact worth showing — but the <em>create dialog</em>
/// filters this endpoint to <c>OpenAI</c>, because its form hardcodes that format and the API refuses
/// an Anthropic create (#107/#126). A picker that offers a model the submit path cannot send is how a
/// refused Anthropic create reaches ARM, which is exactly what E-007 says to avoid.
/// </param>
/// <param name="ModelName">ARM <c>model.name</c>, exactly as a create must spell it.</param>
/// <param name="ModelVersion">ARM <c>model.version</c>. Empty when ARM reports none — a create needs an explicit version, so an entry without one is a shortcut the admin has to complete.</param>
/// <param name="SkuNames">Every SKU this model can be deployed under, deduplicated and ordered for display; empty when ARM reports none. The one to <em>offer</em> is <paramref name="DefaultSkuName"/>, not the first of these.</param>
/// <param name="DefaultCapacity">
/// The capacity ARM suggests for <paramref name="DefaultSkuName"/>, in thousands of TPM — a starting
/// point for the form's capacity field, not a limit. It belongs to that SKU specifically: capacity
/// limits are per-SKU, so pairing it with any other SKU can suggest a create ARM refuses.
/// </param>
/// <param name="DefaultSkuName">
/// The first SKU ARM lists for this model — ARM's own preference order, which is the one to
/// pre-select. <paramref name="SkuNames"/> is sorted for a readable dropdown; sorting is a display
/// decision and must not become a choice.
/// </param>
/// <param name="IsDefaultVersion">
/// ARM's <c>isDefaultVersion</c>. This is how "which version of this model" is answered — never by
/// comparing version strings, which orders <c>turbo-2024-04-09</c> above <c>2025-04-14</c> and
/// <c>1106</c> above <c>0125</c>.
/// </param>
/// <param name="LifecycleStatus">
/// Raw ARM <c>lifecycleStatus</c> (<c>GenerallyAvailable</c>, <c>Preview</c>, <c>Deprecating</c>,
/// <c>Deprecated</c>, <c>Stable</c>, …), or empty when ARM reports none. A string, not an enum: it is
/// an extensible value on ARM's side and a new one must not break the read.
/// </param>
/// <param name="InferenceRetiresOn">
/// ARM <c>deprecation.inference</c> — when this model stops answering requests, if ARM has named a
/// date. A deployment created after it is a deployment that stops working on it.
/// </param>
public record FoundryCatalogEntryResponse(
    string ModelFormat,
    string ModelName,
    string ModelVersion,
    IReadOnlyList<string> SkuNames,
    int? DefaultCapacity,
    string DefaultSkuName,
    bool IsDefaultVersion,
    string LifecycleStatus,
    DateTimeOffset? InferenceRetiresOn)
{
    /// <summary>ARM's <c>lifecycleStatus</c> for a model that is already retired.</summary>
    public const string DeprecatedLifecycleStatus = "Deprecated";

    /// <summary>
    /// Whether this entry is retired as of <paramref name="asOf"/> — ARM has marked it
    /// <c>Deprecated</c>, or the date it stops answering requests has passed. Lives on the contract so
    /// every reader answers it the same way; the caller supplies the clock (CONVENTIONS.md: no naked
    /// <c>UtcNow</c>), and the Web dialog hides these unless the admin asks to see them.
    /// </summary>
    public bool IsRetiredAt(DateTimeOffset asOf) =>
        string.Equals(LifecycleStatus, DeprecatedLifecycleStatus, StringComparison.OrdinalIgnoreCase)
        || InferenceRetiresOn <= asOf;
}

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
