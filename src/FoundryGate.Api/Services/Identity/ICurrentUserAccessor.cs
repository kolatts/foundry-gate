using FoundryGate.Data.Entities;

namespace FoundryGate.Api.Services.Identity;

/// <summary>
/// Per-request view of the authenticated caller: their Entra identity claims and, on demand, their
/// <see cref="User"/> row. Scoped — inject it into any service that needs to know who is calling
/// (audit actors, "/me" endpoints, owner checks) instead of reaching for <c>HttpContext.User</c>
/// and re-implementing claim parsing.
/// </summary>
/// <remarks>
/// <para>
/// Every member that reads a claim throws <see cref="UnauthorizedAccessException"/> (→ 403 via
/// <c>GlobalExceptionHandler</c>) when there is no authenticated principal or the principal carries
/// no object id — both are configuration/token-shape faults on an already-authenticated request,
/// not "please log in" (the global <c>AuthorizeFilter</c> has already turned anonymous callers away
/// with a 401 before any service runs).
/// </para>
/// <para>
/// <b>"No <c>User</c> row for this caller" is always 403, never 404.</b> An authenticated principal
/// with no row is an authorization-<em>state</em> problem — they haven't been provisioned yet, which
/// <c>GET /users/me</c> does on first call — not a missing resource. <see cref="GetRequiredUserAsync"/>
/// and <c>IAuditService.LogAsync</c> both throw <see cref="UnauthorizedAccessException"/> with a
/// message that says so, so an admin who holds the role but hasn't loaded the UI yet reads one
/// consistent instruction rather than a "Not found" on one endpoint and "Forbidden" on the next.
/// </para>
/// </remarks>
public interface ICurrentUserAccessor
{
    /// <summary>
    /// The caller's Entra object id (the <c>oid</c> claim — accepted as either the short
    /// <c>oid</c> or the long <c>http://schemas.microsoft.com/identity/claims/objectidentifier</c>
    /// claim type). Matches <see cref="User.EntraObjectId"/>.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">No authenticated principal, or no oid claim.</exception>
    string EntraObjectId { get; }

    /// <summary>
    /// <see langword="true"/> when the caller holds the <c>FoundryGate.Admin</c> app role. Evaluated
    /// with <c>ClaimsPrincipal.IsInRole</c> — the same check <c>PolicyNames.AdminOnly</c> uses — so
    /// the two can never disagree.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">No authenticated principal.</exception>
    bool IsAdmin { get; }

    /// <summary>The caller's display name from the token (<c>name</c> claim), if present. Used by first-login auto-provisioning.</summary>
    /// <exception cref="UnauthorizedAccessException">No authenticated principal.</exception>
    string? DisplayName { get; }

    /// <summary>The caller's sign-in name/email from the token (<c>preferred_username</c>, falling back to UPN/email claims), if present. Used by first-login auto-provisioning.</summary>
    /// <exception cref="UnauthorizedAccessException">No authenticated principal.</exception>
    string? Email { get; }

    /// <summary>
    /// The caller's <see cref="User"/> row, or <see langword="null"/> if none exists yet for
    /// <see cref="EntraObjectId"/> — a first-class outcome: <c>GET /users/me</c> auto-provisions on
    /// exactly this path. Returned <em>tracked</em> so callers can mutate and save it. Looks in the
    /// context's change tracker before the database, so a <c>User</c> the calling service has just
    /// <c>Add</c>ed (and not yet saved) is found too — auto-provisioning can add the user, write its
    /// audit row against the same instance, and commit both in one save. A found row is cached for
    /// the rest of the request; a miss is not.
    /// </summary>
    Task<User?> TryGetUserAsync(CancellationToken cancellationToken);

    /// <summary>Like <see cref="TryGetUserAsync"/> but a missing row is an error.</summary>
    /// <exception cref="UnauthorizedAccessException">No <see cref="User"/> exists for the caller's oid (→ 403; see the type remarks for why not 404).</exception>
    Task<User> GetRequiredUserAsync(CancellationToken cancellationToken);
}
