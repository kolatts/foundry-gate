namespace FoundryGate.Domain.Common;

/// <summary>
/// Generic paged-list envelope returned by every "(paged)" list endpoint in the API
/// surface (spec &#167;4). Shared between FoundryGate.Api and FoundryGate.Web so the
/// Blazor client can bind directly to it without a translation layer.
/// </summary>
/// <param name="Items">The page of results, in the order the query returned them.</param>
/// <param name="TotalCount">Total number of matching rows across all pages, not just this one.</param>
/// <param name="Page">The 1-based page number this result represents.</param>
/// <param name="PageSize">The page size used to produce this result.</param>
public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize)
{
    /// <summary>
    /// Total number of pages implied by <see cref="TotalCount"/> and <see cref="PageSize"/>.
    /// Never negative; reports 0 only when <see cref="PageSize"/> is non-positive (defensive
    /// only — a well-formed <see cref="PagedRequest"/> never produces that).
    /// </summary>
    public int TotalPages => PageSize <= 0
        ? 0
        : (int)Math.Ceiling(TotalCount / (double)PageSize);

    /// <summary>Convenience factory for an empty page (e.g. an admin list with zero matches).</summary>
    public static PagedResult<T> Empty(int page, int pageSize) => new([], 0, page, pageSize);
}
