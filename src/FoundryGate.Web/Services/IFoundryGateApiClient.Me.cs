using FoundryGate.Domain.Common;
using FoundryGate.Domain.Foundry.Contracts;
using FoundryGate.Domain.Keys.Contracts;
using FoundryGate.Domain.Quota.Contracts;
using FoundryGate.Domain.Requests.Contracts;

namespace FoundryGate.Web.Services;

/// <summary>
/// The endpoints the developer pages (<c>/me</c>, <c>/me/request</c> — issues #49/#50) and the
/// admin pages (#54/#55) need on top of the original scaffold surface (#48). Split into its own
/// partial-file rather than appended to <see cref="IFoundryGateApiClient"/>'s main file so two
/// frontend waves can add client methods concurrently without colliding on the same lines.
/// </summary>
public partial interface IFoundryGateApiClient
{
    /// <summary>
    /// <c>POST /keys/me/reveal</c> — decrypts and returns the caller's full key once. Audited
    /// (<c>key.revealed</c>). The value must live in component state for the current render only:
    /// never <c>localStorage</c>, never a cookie (spec &#167;11).
    /// </summary>
    Task<ApiCallResult<ApiKeyRevealResponse>> RevealMyKeyAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>GET /quota/tiers</c> — the configured budget tiers. Since D-013 a monthly quota
    /// <em>is</em> a tier, so these are the only values a quota request or an admin quota edit may
    /// name.
    /// </summary>
    Task<ApiCallResult<IReadOnlyList<QuotaTierResponse>>> GetQuotaTiersAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>GET /foundry/models</c> — the developer view of the deployment names a CLI can pin
    /// <c>model</c> to, for <c>/me</c>'s "Configure your AI CLI" panel.
    /// </summary>
    Task<ApiCallResult<IReadOnlyList<FoundryModelResponse>>> GetFoundryModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// <c>GET /requests</c> with the filter record — the same endpoint as the unfiltered overload,
    /// which stays for callers that want the whole list. A non-admin caller only ever sees their
    /// own requests whatever this asks for.
    /// </summary>
    Task<ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>> GetRequestsAsync(
        QuotaRequestQuery query,
        PagedRequest paging,
        CancellationToken ct = default);
}
