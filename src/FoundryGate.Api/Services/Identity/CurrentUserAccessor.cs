using System.Security.Claims;
using FoundryGate.Data;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;

namespace FoundryGate.Api.Services.Identity;

/// <summary>
/// Default <see cref="ICurrentUserAccessor"/>: reads claims from <see cref="IHttpContextAccessor"/>
/// and resolves the <see cref="User"/> row through the request's own <see cref="AppDbContext"/>
/// (so the returned entity is tracked by the same context the calling service saves with).
/// </summary>
public sealed class CurrentUserAccessor(IHttpContextAccessor httpContextAccessor, AppDbContext dbContext)
    : ICurrentUserAccessor
{
    private User? _user;

    /// <inheritdoc />
    public string EntraObjectId =>
        // Microsoft.Identity.Web's GetObjectId() checks both claim types: the short "oid" and the
        // long ".../identity/claims/objectidentifier" URI (which claim a token carries depends on
        // whether the JWT handler's inbound claim-type mapping is on).
        Principal.GetObjectId()
        ?? throw new UnauthorizedAccessException(
            "The authenticated principal carries no object id (oid) claim, so the caller cannot be identified.");

    /// <inheritdoc />
    public bool IsAdmin => Principal.IsInRole(RoleNames.Admin);

    /// <inheritdoc />
    public string? DisplayName =>
        FirstNonEmptyClaim(ClaimConstants.Name, ClaimTypes.Name);

    /// <inheritdoc />
    public string? Email =>
        FirstNonEmptyClaim(ClaimConstants.PreferredUserName, ClaimTypes.Upn, ClaimTypes.Email, "email");

    private ClaimsPrincipal Principal =>
        httpContextAccessor.HttpContext?.User is { Identity.IsAuthenticated: true } principal
            ? principal
            : throw new UnauthorizedAccessException("There is no authenticated principal on the current request.");

    /// <inheritdoc />
    public async Task<User?> TryGetUserAsync(CancellationToken cancellationToken)
    {
        if (_user is not null)
        {
            return _user;
        }

        var entraObjectId = EntraObjectId;

        // SingleOrDefaultAsync (not AsNoTracking) deliberately: callers mutate the returned row, and
        // EF's identity resolution hands back the already-tracked instance if the calling service
        // has loaded this user through the same context — one instance per request, never two.
        _user = await dbContext.Users.SingleOrDefaultAsync(u => u.EntraObjectId == entraObjectId, cancellationToken);
        return _user;
    }

    /// <inheritdoc />
    public async Task<User> GetRequiredUserAsync(CancellationToken cancellationToken) =>
        await TryGetUserAsync(cancellationToken)
        ?? throw new KeyNotFoundException(
            $"No FoundryGate user exists for the caller (oid {EntraObjectId}). GET /users/me provisions one on first login.");

    private string? FirstNonEmptyClaim(params string[] claimTypes)
    {
        var principal = Principal;
        foreach (var claimType in claimTypes)
        {
            var value = principal.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
