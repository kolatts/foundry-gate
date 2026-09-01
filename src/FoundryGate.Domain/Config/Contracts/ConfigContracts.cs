using System.ComponentModel.DataAnnotations;
using FoundryGate.Domain.Constants;

namespace FoundryGate.Domain.Config.Contracts;

/// <summary>One <c>SystemConfiguration</c> row (spec &#167;3.1). GET /config, admin-only.</summary>
/// <param name="Key">The configuration key.</param>
/// <param name="Value">The configured value.</param>
/// <param name="UpdatedDate">When this row was last inserted or updated.</param>
/// <param name="UpdatedByUserId">
/// Null for a seeded row an admin has never edited (the data-layer entity is nullable for the
/// same reason — reconciled in #92, see the row's <c>[DoNotUpdate]</c> seeding semantics).
/// </param>
public record SystemConfigEntryResponse(
    string Key,
    string Value,
    DateTimeOffset UpdatedDate,
    int? UpdatedByUserId);

/// <summary>PUT /config/{key} body.</summary>
public record UpdateSystemConfigRequest(
    [property: Required, StringLength(ValidationConstants.ConfigValueMaxLength)]
    string Value);

/// <summary>
/// Gateway connection details a developer needs to point Claude Code, Codex CLI, or
/// any Anthropic/OpenAI-compatible client at this fork's APIM gateway. Rendered by the
/// "Configure your CLI" panel on <c>/me</c> (docs-site's getting-started/cli-setup.mdx).
/// </summary>
/// <param name="GatewayBaseUrl">
/// The APIM gateway origin, e.g. <c>https://ai.yourcompany.com</c>
/// (<see cref="SystemConfigurationKeys.ApimGatewayUrl"/>).
/// </param>
/// <param name="AnthropicBasePath">Path segment for the Anthropic Messages front door, e.g. <c>/anthropic</c>.</param>
/// <param name="OpenAiBasePath">Path segment for the OpenAI Responses front door, e.g. <c>/openai/v1</c>.</param>
/// <param name="ModelAliases">
/// The virtual model names this developer's product allows (plans/25-model-aliases-routing.md).
/// CLI config should pin to the alias, not the underlying Foundry deployment name, so
/// deployments can rotate underneath without every developer editing env vars.
/// </param>
public record GatewayConnectionInfo(
    string GatewayBaseUrl,
    string AnthropicBasePath,
    string OpenAiBasePath,
    IReadOnlyList<ModelAliasInfo> ModelAliases);

/// <summary>
/// One virtual model alias exposed by the gateway's alias map. <paramref name="Alias"/>
/// is the stable name a developer's CLI config references (e.g. <c>sonnet</c>);
/// <paramref name="DeploymentName"/> is the underlying Foundry deployment it currently
/// resolves to, shown for transparency/debugging only.
/// </summary>
public record ModelAliasInfo(
    string Alias,
    string DeploymentName,
    ModelProviderType Provider);
