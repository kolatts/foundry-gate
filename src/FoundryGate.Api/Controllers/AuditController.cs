using FoundryGate.Api.Services.Audit;
using FoundryGate.Domain.Audit.Contracts;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FoundryGate.Api.Controllers;

/// <summary>
/// <c>GET /api/v1/audit</c> — admin-only, paged, filterable view of the audit trail (spec &#167;4.6,
/// issue #42). Newest first.
/// </summary>
[Authorize(Policy = PolicyNames.AdminOnly)]
public sealed class AuditController(IAuditService auditService) : ApiControllerBase
{
    /// <summary>
    /// Lists audit log entries, newest first. Every <see cref="AuditLogQuery"/> member is an
    /// optional query-string filter (exact match on <c>actorUserId</c>/<c>action</c>/
    /// <c>targetType</c>/<c>targetId</c>; inclusive <c>fromDate</c>/<c>toDate</c> range on
    /// <c>OccurredDate</c>); <c>page</c>/<c>pageSize</c> come from <see cref="PagedRequest"/>.
    /// </summary>
    /// <remarks>
    /// No <c>ThrowIfNull</c> on the bound records: <c>[ApiController]</c> always materializes a
    /// <c>[FromQuery]</c> complex type (an empty query string yields an instance with defaults), so
    /// the guard would be unreachable ceremony — controllers stay expression-bodied delegations.
    /// </remarks>
    [HttpGet]
    [ProducesResponseType<PagedResult<AuditLogEntryResponse>>(StatusCodes.Status200OK)]
    public Task<PagedResult<AuditLogEntryResponse>> ListAsync(
        [FromQuery] AuditLogQuery filter,
        [FromQuery] PagedRequest paging,
        CancellationToken cancellationToken) =>
        auditService.QueryAsync(filter, paging, cancellationToken);
}
