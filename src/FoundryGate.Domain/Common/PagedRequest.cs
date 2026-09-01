namespace FoundryGate.Domain.Common;

/// <summary>
/// Paging parameters accepted by every "(paged)" list endpoint (spec &#167;4). Bind
/// directly from the query string via <c>[FromQuery]</c>.
/// </summary>
/// <remarks>
/// Deliberately carries no <c>System.ComponentModel.DataAnnotations</c> attributes.
/// An out-of-range <see cref="Page"/> or <see cref="PageSize"/> here is a client being
/// sloppy about pagination, not a validation failure worth a 400 — call
/// <see cref="Clamp"/> before using the values in a query and the request degrades to a
/// safe default instead. Request/response DTOs that represent actual domain input (a
/// quota justification, a group name, ...) DO carry validation attributes; see the
/// per-area <c>Contracts</c> types.
/// </remarks>
/// <param name="Page">1-based page number. Values below 1 clamp up to 1.</param>
/// <param name="PageSize">Rows per page. Clamps into [1, <see cref="MaxPageSize"/>].</param>
public record PagedRequest(int Page = 1, int PageSize = PagedRequest.DefaultPageSize)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 200;

    /// <summary>Returns a copy with <see cref="Page"/>/<see cref="PageSize"/> normalized into sane bounds.</summary>
    public PagedRequest Clamp() => this with
    {
        Page = Page < 1 ? 1 : Page,
        PageSize = PageSize switch
        {
            < 1 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => PageSize,
        },
    };
}
