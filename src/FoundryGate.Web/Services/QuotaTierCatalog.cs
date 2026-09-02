using FoundryGate.Domain.Quota.Contracts;

namespace FoundryGate.Web.Services;

/// <summary>
/// The configured budget tiers, fetched once per session instead of once per page.
/// </summary>
/// <remarks>
/// Seven admin pages render a tier name somewhere, and every one of them opened by calling
/// <c>GET /quota/tiers</c> and unwrapping the result itself — the same six lines, refetching a
/// catalogue that changes only when the gateway's Bicep does. Scoped rather than singleton, for
/// the reason <see cref="DashboardStateService"/> gives: in WebAssembly the two are the same
/// object, but this is state derived from an authorized response.
/// <para>
/// A failed fetch is not cached and is not an error the caller has to handle: the tier list stays
/// empty and <see cref="TierDisplay"/> falls back to formatting the raw number, so a page renders
/// with worse labels rather than not at all. The next navigation tries again.
/// </para>
/// </remarks>
public sealed class QuotaTierCatalog(IFoundryGateApiClient apiClient)
{
    private IReadOnlyList<QuotaTierResponse>? _tiers;

    /// <summary>The tiers, fetched on first use. Empty when the call has not succeeded yet.</summary>
    public IReadOnlyList<QuotaTierResponse> Tiers => _tiers ?? [];

    /// <summary>Fetches the catalogue if this session has not got it yet, and returns it either way.</summary>
    public async Task<IReadOnlyList<QuotaTierResponse>> GetAsync(CancellationToken ct = default)
    {
        if (_tiers is not null)
        {
            return _tiers;
        }

        var result = await apiClient.GetQuotaTiersAsync(ct);
        if (result.IsSuccess && result.Value is not null)
        {
            _tiers = result.Value;
        }

        return Tiers;
    }
}
