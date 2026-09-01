using FoundryGate.Api.Services.Quota;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FoundryGate.Api.Controllers;

/// <summary>
/// <c>/api/v1/quota</c> (spec &#167;4.3; issue #33): current-period quota allocations and the manual
/// reset. Authenticated by the global filter; the three admin actions opt into
/// <see cref="PolicyNames.AdminOnly"/> individually because <c>allocations/me</c> is for every developer.
/// </summary>
public sealed class QuotaController(IQuotaAllocationService quotaAllocations) : ApiControllerBase
{
    /// <summary>Admin: every allocation for the current UTC calendar month, paged, ordered by user display name.</summary>
    [HttpGet("allocations")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<PagedResult<QuotaAllocationResponse>>(StatusCodes.Status200OK)]
    public Task<PagedResult<QuotaAllocationResponse>> ListAsync(
        [FromQuery] PagedRequest paging,
        CancellationToken cancellationToken) =>
        quotaAllocations.ListCurrentPeriodAsync(paging, cancellationToken);

    /// <summary>The caller's own current-period allocation; resolved and created on first call of the month.</summary>
    [HttpGet("allocations/me")]
    [ProducesResponseType<QuotaAllocationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<QuotaAllocationResponse> GetMineAsync(CancellationToken cancellationToken) =>
        quotaAllocations.GetMyAllocationAsync(cancellationToken);

    /// <summary>Admin: one user's current-period allocation (404 if the user or their allocation for this period does not exist).</summary>
    [HttpGet("allocations/{userId:int}")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<QuotaAllocationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<QuotaAllocationResponse> GetForUserAsync(int userId, CancellationToken cancellationToken) =>
        quotaAllocations.GetUserAllocationAsync(userId, cancellationToken);

    /// <summary>Admin, idempotent: re-resolve every active user's allocation for the current UTC calendar month (see <see cref="QuotaResetResult"/>).</summary>
    [HttpPost("reset")]
    [Authorize(Policy = PolicyNames.AdminOnly)]
    [ProducesResponseType<QuotaResetResult>(StatusCodes.Status200OK)]
    public Task<QuotaResetResult> ResetAsync(CancellationToken cancellationToken) =>
        quotaAllocations.ResetAsync(cancellationToken);
}
