using FoundryGate.Domain.Dashboard.Contracts;

namespace FoundryGate.Api.Services.Dashboard;

/// <summary>
/// The admin dashboard's one read (spec &#167;4.6; issue #162): the counts and top-consumer list
/// behind <c>GET /api/v1/dashboard</c>, all for the current UTC billing period.
/// </summary>
/// <remarks>
/// Every usage figure is a reconciliation number from the Log Analytics sync (spec &#167;5.4), not a
/// live view of gateway enforcement — see <see cref="DashboardSummaryResponse"/>.
/// </remarks>
public interface IDashboardService
{
    /// <summary>
    /// The current period's summary.
    /// </summary>
    /// <param name="fresh">
    /// <see langword="true"/> bypasses the short in-memory cache and re-queries. The page
    /// auto-refreshes every 60 s (plans/18) against a cache that holds for
    /// <see cref="DashboardService.CacheDuration"/>, so an admin who just changed something —
    /// approved a request, deactivated a user — would otherwise watch a stale number for up to half a
    /// refresh cycle. Tests use it for the same reason.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<DashboardSummaryResponse> GetSummaryAsync(bool fresh, CancellationToken cancellationToken);
}
