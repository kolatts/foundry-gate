using FoundryGate.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Data.Extensions;

/// <summary>
/// The shared paging helper every "(paged)" list endpoint uses (CONVENTIONS.md §EF Core: "a
/// shared <c>PagedResult&lt;T&gt;</c> + <c>.ToPagedAsync(page, size, ct)</c> helper in
/// Domain/Data"). Lives in Data rather than Domain because it needs EF Core's async
/// materialization and Domain has zero dependencies by design.
/// </summary>
public static class QueryableExtensions
{
    /// <summary>
    /// Materializes one page of <paramref name="query"/> plus the total match count. Call it
    /// last, after every <c>Where</c>, <c>OrderBy</c>, and projection — the query must already be
    /// deterministically ordered or pages will overlap/skip between requests.
    /// <paramref name="request"/> is <see cref="PagedRequest.Clamp"/>ed here so callers never
    /// have to remember to.
    /// </summary>
    public static async Task<PagedResult<T>> ToPagedAsync<T>(
        this IQueryable<T> query,
        PagedRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(request);

        var paging = request.Clamp();

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((paging.Page - 1) * paging.PageSize)
            .Take(paging.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<T>(items, totalCount, paging.Page, paging.PageSize);
    }
}
