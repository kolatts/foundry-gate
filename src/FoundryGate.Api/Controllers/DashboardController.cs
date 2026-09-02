using FoundryGate.Api.Services.Dashboard;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Dashboard.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FoundryGate.Api.Controllers;

/// <summary>
/// <c>/api/v1/dashboard</c> (spec &#167;4.6; issue #162) — the admin landing page's summary stats
/// (#54). Admin-only: it aggregates every user's usage.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
public sealed class DashboardController(IDashboardService dashboardService) : ApiControllerBase
{
    /// <summary>
    /// Counts, this period's total token usage, and the top ten consumers — all for the current UTC
    /// calendar month. Served from a <see cref="DashboardService.CacheDuration"/> in-memory cache
    /// shared by every admin; pass <c>?fresh=true</c> to recompute (the page does this after a
    /// mutation, and tests after seeding).
    /// </summary>
    /// <response code="200">The summary.</response>
    [HttpGet]
    [ProducesResponseType<DashboardSummaryResponse>(StatusCodes.Status200OK)]
    public Task<DashboardSummaryResponse> GetAsync(
        [FromQuery] bool fresh,
        CancellationToken cancellationToken) =>
        dashboardService.GetSummaryAsync(fresh, cancellationToken);
}
