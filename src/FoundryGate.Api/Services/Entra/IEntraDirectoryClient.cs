namespace FoundryGate.Api.Services.Entra;

/// <summary>
/// The thin seam between FoundryGate and Microsoft Graph. Every Entra-facing feature — bulk user
/// sync (#40), first-login lookup (#28), group member sync (#41) — talks to the directory through
/// this interface and nothing else, so the sync logic is tested against an in-memory fake and the
/// one Graph-backed implementation (<see cref="GraphEntraDirectoryClient"/>) is tested against
/// stubbed HTTP. No live tenant is ever needed in the test suite.
/// </summary>
/// <remarks>
/// Implementations: <see cref="GraphEntraDirectoryClient"/> when <c>Entra:Enabled</c> is true,
/// <see cref="DisabledEntraDirectoryClient"/> otherwise (every call → <see cref="ArgumentException"/>
/// → 400 with a message that names the setting). Registered as a singleton — the Graph client is
/// thread-safe and the only state is a cached service-principal id.
/// </remarks>
public interface IEntraDirectoryClient
{
    /// <summary>
    /// Looks up one user by Entra object id (<c>GET /users/{id}</c>, <c>$select</c>ed to the fields
    /// FoundryGate stores). <see langword="null"/> when the directory has no such user (Graph 404) —
    /// a normal outcome for a deleted account, not an error.
    /// </summary>
    /// <param name="objectId">The <c>oid</c> to look up.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<EntraUser?> GetUserAsync(string objectId, CancellationToken cancellationToken);

    /// <summary>
    /// Every <em>user</em> that holds an app-role assignment on the FoundryGate service principal
    /// (<c>GET /servicePrincipals/{id}/appRoleAssignedTo</c>, every page), resolved to their directory
    /// fields, plus the assignments whose principal is a <em>group</em> — those are not expanded to
    /// their members in this wave (issue #121) and are reported so the caller can refuse to treat
    /// "not in the user list" as "departed". Service-principal assignees are dropped silently (they
    /// are not people). The same user appears at most once even if they hold several role
    /// assignments. Buffered rather than streamed: a fork's developer population is at most a few
    /// thousand small records, and the caller needs the group list before it can act on the users.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<EntraAssignedUsers> ListAssignedUsersAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Streams the object ids of the <em>user</em> members of an Entra group
    /// (<c>GET /groups/{id}/members</c> or <c>/transitiveMembers</c> with <c>$select=id</c>, every
    /// page). Non-user members (nested groups, devices, service principals) are filtered out; pass
    /// <paramref name="transitive"/> to flatten nested groups into their user members instead.
    /// </summary>
    /// <param name="groupObjectId">The Entra group's object id (<c>Group.EntraGroupId</c>).</param>
    /// <param name="transitive">
    /// <see langword="false"/>: direct members only. <see langword="true"/>: members of nested
    /// groups too (<c>transitiveMembers</c>).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    IAsyncEnumerable<string> ListGroupMemberIdsAsync(string groupObjectId, bool transitive, CancellationToken cancellationToken);
}
