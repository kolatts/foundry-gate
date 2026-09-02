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
/// <param name="UpdatedByDisplayName">
/// The editing admin's display name, joined from <c>SystemConfiguration.UpdatedByUser</c> so the
/// config editor (#55) can render "last changed by" without a second round trip per row. Null
/// exactly when <paramref name="UpdatedByUserId"/> is.
/// </param>
/// <param name="IsReadOnly">
/// True for a key the system writes for itself (<see cref="SystemConfigurationKeys.SystemManaged"/>):
/// <c>PUT /config/{key}</c> answers <c>409</c> and the admin editor disables the field. Sent on the
/// read so the UI never has to keep a list of its own — the duplication #172 was filed about — and
/// never has to discover the refusal by attempting an edit. Appended to this positional record rather
/// than inserted, because the Web client deserializes it by position.
/// </param>
public record SystemConfigEntryResponse(
    string Key,
    string Value,
    DateTimeOffset UpdatedDate,
    int? UpdatedByUserId,
    string? UpdatedByDisplayName,
    bool IsReadOnly);

/// <summary>PUT /config/{key} body. Init-property record, not positional — see <see cref="Foundry.Contracts.CreateFoundryDeploymentRequest"/>'s remarks (#128).</summary>
public record UpdateSystemConfigRequest
{
    /// <summary>
    /// The new value for the key.
    /// </summary>
    /// <remarks>
    /// <c>AllowEmptyStrings</c> deliberately: <b>empty is a legitimate value for several keys</b> —
    /// clearing <c>ApimResourceId</c> or <c>FoundryResourceId</c> back to
    /// "not addressed yet" is exactly how a fork operator unwires a resource. The default
    /// <c>[Required]</c> (which rejects <c>""</c>) would have MVC answer 400 before the action ran,
    /// so the per-key rule could never be consulted and the documented "or empty" capability was
    /// unreachable over HTTP. Emptiness is the API's decision to make per key
    /// (<c>SystemConfigValidator</c>), not model binding's to make for all of them; the attribute
    /// stays on so a <see langword="null"/> <c>value</c> is still a field-level 400 rather than a
    /// null-reference deeper in.
    /// </remarks>
    [Required(AllowEmptyStrings = true)]
    [StringLength(ValidationConstants.ConfigValueMaxLength)]
    public string Value { get; init; } = string.Empty;

    /// <summary>
    /// Optional optimistic-concurrency check (#170): the <c>updatedDate</c> the caller read from
    /// <c>GET /config</c>. When supplied and it does not match the stored row, the write is refused with
    /// <c>409</c> naming the current value, timestamp and the admin who got there first, instead of
    /// silently overwriting them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Optional, and therefore additive: a caller that omits it keeps the original last-write-wins
    /// behaviour. <c>SystemConfiguration</c> carries no <c>rowversion</c> — it is reference data whose
    /// columns are all <c>[DoNotUpdate]</c>, so a real EF concurrency token would complicate the seeder
    /// for a seven-row table — and the contention this guards against is between two humans with the
    /// config form open, which is exactly where the caller has a timestamp to echo back.
    /// </para>
    /// <para>
    /// Compared as an <em>instant</em>, not as text: <see cref="DateTimeOffset"/> equality ignores the
    /// offset, so a client that normalizes to UTC (or a SQLite-backed test, which always reads back
    /// <c>+00:00</c>) still matches a row SQL Server stored with a different offset.
    /// </para>
    /// </remarks>
    public DateTimeOffset? ExpectedUpdatedDate { get; init; }
}

/// <summary>
/// Gateway connection details a developer needs to point Claude Code, Codex CLI, or
/// any Anthropic/OpenAI-compatible client at this fork's APIM gateway. Rendered by the
/// "Configure your CLI" panel on <c>/me</c> (docs-site's getting-started/cli-setup.mdx).
/// </summary>
/// <param name="GatewayBaseUrl">
/// The APIM gateway origin, e.g. <c>https://ai.yourcompany.com</c>. Comes from
/// <c>Gateway:ApimGatewayUrl</c>, which infra sets from the APIM module's own output — never from a
/// configuration row, so it cannot drift from the gateway that was deployed (#156).
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
