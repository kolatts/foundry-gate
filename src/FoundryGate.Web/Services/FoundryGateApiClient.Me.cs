using FoundryGate.Domain.Common;
using FoundryGate.Domain.Foundry.Contracts;
using FoundryGate.Domain.Keys.Contracts;
using FoundryGate.Domain.Quota.Contracts;
using FoundryGate.Domain.Requests.Contracts;

namespace FoundryGate.Web.Services;

/// <inheritdoc cref="IFoundryGateApiClient" />
public sealed partial class FoundryGateApiClient
{
    public Task<ApiCallResult<ApiKeyRevealResponse>> RevealMyKeyAsync(CancellationToken ct = default) =>
        SendAsync<ApiKeyRevealResponse>(HttpMethod.Post, "keys/me/reveal", body: null, ct);

    public Task<ApiCallResult<IReadOnlyList<QuotaTierResponse>>> GetQuotaTiersAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<QuotaTierResponse>>("quota/tiers", ct);

    public Task<ApiCallResult<IReadOnlyList<FoundryModelResponse>>> GetFoundryModelsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<FoundryModelResponse>>("foundry/models", ct);

    public Task<ApiCallResult<PagedResult<QuotaIncreaseRequestResponse>>> GetRequestsAsync(
        QuotaRequestQuery query,
        PagedRequest paging,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(paging);

        var clamped = paging.Clamp();
        List<string> parts = [$"page={clamped.Page}", $"pageSize={clamped.PageSize}"];

        if (query.Status is { } status)
        {
            // The API binds the enum from its int value (api.md: "0 Pending, 1 Approved, 2 Rejected").
            parts.Add($"status={(int)status}");
        }

        if (query.UserId is { } userId)
        {
            parts.Add($"userId={userId}");
        }

        return GetAsync<PagedResult<QuotaIncreaseRequestResponse>>($"requests?{string.Join('&', parts)}", ct);
    }
}
