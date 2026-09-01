using FoundryGate.Data.Entities;

namespace FoundryGate.Api.Services.Identity;

/// <summary>
/// Per-request view of the authenticated caller: their Entra identity claims and, on demand, their
/// <see cref="User"/> row. Scoped — inject it into any service that needs to know who is calling
/// (audit actors, "/me" endpoints, owner checks) instead of reaching for <c>HttpContext.User</c>
/// and re-implementing claim parsing.
/// </summary>
/// <remarks>
/// Every member that reads a claim throws <see cref="UnauthorizedAccessException"/> (→ 403 via
/// <c>GlobalExceptionHandler</c>) when there is no authenticated principal or the principal carries
/// no object id — both are configuration/token-shape faults on an already-authenticated request,
/// not "please log in" (the global <c>AuthorizeFilter</c> has already turned anonymous callers away
/// with a 401 before any service runs).
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
    /// exactly this path. Returned <em>tracked</em> so callers can mutate and save it. A found row is
    /// cached for the rest of the request; a miss is not, so a caller that provisions the user and
    /// asks again gets the new row.
    /// </summary>
    Task<User?> TryGetUserAsync(CancellationToken cancellationToken);

    /// <summary>Like <see cref="TryGetUserAsync"/> but a missing row is an error.</summary>
    /// <exception cref="KeyNotFoundException">No <see cref="User"/> exists for the caller's oid (→ 404).</exception>
    Task<User> GetRequiredUserAsync(CancellationToken cancellationToken);
}
