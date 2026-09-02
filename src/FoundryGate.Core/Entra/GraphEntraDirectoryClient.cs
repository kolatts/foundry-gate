using System.Runtime.CompilerServices;
using FoundryGate.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using GraphUser = Microsoft.Graph.Models.User;

namespace FoundryGate.Core.Entra;

/// <summary>
/// <see cref="IEntraDirectoryClient"/> over <see cref="GraphServiceClient"/> (Microsoft Graph v1.0).
/// Authenticates with the app's registered <c>TokenCredential</c> (#110) — no client secret; the
/// scope is <see cref="EntraOptions.GraphScope"/>. Every collection call follows
/// <c>@odata.nextLink</c> until the directory runs out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Graph application roles the API identity needs</b>: <c>Application.Read.All</c> (resolve the
/// service principal and read its <c>appRoleAssignedTo</c>), <c>User.Read.All</c> (user fields),
/// <c>GroupMember.ReadBasic.All</c> (group member ids — the least-privileged role for
/// <c>/members</c> and <c>/transitiveMembers</c> per the Graph reference, for group sync <em>and</em>
/// for expanding group-principal app-role assignments (#121); only <c>id</c> is selected, so
/// <c>GroupMember.Read.All</c> stays unnecessary). Least privilege per the
/// Graph reference for each call; <c>Directory.Read.All</c> is deliberately <em>not</em> required —
/// which is why user details are fetched with <c>GET /users?$filter=id in (...)</c> in chunks of
/// <see cref="InFilterMaxValues"/> rather than <c>directoryObjects/getByIds</c> (that action needs
/// <c>Directory.Read.All</c> and supports no <c>$select</c>, so it would not return
/// <c>employeeId</c> anyway).
/// </para>
/// <para>
/// <b>Transient faults</b> are handled by the SDK's own pipeline: <c>GraphServiceClient</c> is
/// built through <c>GraphClientFactory</c>, whose default handlers include Kiota's
/// <c>RetryHandler</c> (3 retries, exponential back-off, honours <c>Retry-After</c> on
/// 429/503/504) plus redirect and compression handling. No Polly, no extra package
/// (CONVENTIONS.md: no unnecessary packages).
/// </para>
/// <para>
/// <b>Paging</b> uses <c>WithUrl(nextLink)</c> loops rather than <c>PageIterator</c>: the iterator
/// drives a callback and cannot <c>yield</c> into an <see cref="IAsyncEnumerable{T}"/> (used for group
/// members) without buffering the whole collection first; a plain next-link loop streams each page
/// as it arrives and keeps the code a dozen lines.
/// </para>
/// </remarks>
public sealed class GraphEntraDirectoryClient(
    GraphServiceClient graph,
    EntraOptions entraOptions,
    ILogger<GraphEntraDirectoryClient> logger) : IEntraDirectoryClient
{
    /// <summary>
    /// Graph's documented default cap on the <c>in</c> filter operator ("limited to 15 expressions
    /// in the filter clause by default" — Graph known issues, query parameters).
    /// </summary>
    public const int InFilterMaxValues = 15;

    /// <summary>Graph's maximum page size for directory collections.</summary>
    private const int MaxPageSize = 999;

    /// <summary>
    /// The one HTTP status this class reads off an <c>ODataError</c>. A literal rather than
    /// <c>StatusCodes.Status404NotFound</c>: that constant is ASP.NET Core's, and this class moved to
    /// Core precisely so the isolated Functions worker can use it (#151).
    /// </summary>
    private const int GraphNotFound = 404;

    /// <summary><c>appRoleAssignment.principalType</c> values (the third is <c>ServicePrincipal</c>).</summary>
    private const string UserPrincipalType = "User";
    private const string GroupPrincipalType = "Group";

    private static readonly string[] UserSelect = ["id", "displayName", "mail", "userPrincipalName", "employeeId"];
    private static readonly string[] AssignmentSelect = ["principalId", "principalType", "principalDisplayName"];
    private static readonly string[] IdSelect = ["id"];

    private readonly SemaphoreSlim _servicePrincipalLock = new(1, 1);
    private readonly string _applicationClientId = entraOptions.ApplicationClientId ?? string.Empty;
    private string? _servicePrincipalObjectId = string.IsNullOrWhiteSpace(entraOptions.ServicePrincipalObjectId) ? null : entraOptions.ServicePrincipalObjectId;

    /// <inheritdoc />
    public async Task<EntraUser?> GetUserAsync(string objectId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        try
        {
            var user = await graph.Users[objectId].GetAsync(
                request => request.QueryParameters.Select = UserSelect,
                cancellationToken);

            return user is null ? null : Map(user);
        }
        catch (ODataError error) when (error.ResponseStatusCode == GraphNotFound)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<EntraAssignedUsers> ListAssignedUsersAsync(CancellationToken cancellationToken)
    {
        var servicePrincipalObjectId = await GetServicePrincipalObjectIdAsync(cancellationToken);

        // Pass 1: principals. appRoleAssignedTo carries no user fields (only principalId /
        // principalType / principalDisplayName), so ids are collected here — direct user assignees now,
        // group members in pass 2 — and the whole set is hydrated in pass 3.
        var userIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<EntraGroupAssignment>();
        var seenGroupIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedServicePrincipals = 0;

        var assignments = graph.ServicePrincipals[servicePrincipalObjectId].AppRoleAssignedTo;
        var page = await assignments.GetAsync(
            request =>
            {
                request.QueryParameters.Select = AssignmentSelect;
                request.QueryParameters.Top = MaxPageSize;
            },
            cancellationToken);

        while (page is not null)
        {
            foreach (var assignment in page.Value ?? [])
            {
                if (assignment.PrincipalId is not { } principalId)
                {
                    continue;
                }

                var id = principalId.ToString();
                if (string.Equals(assignment.PrincipalType, UserPrincipalType, StringComparison.OrdinalIgnoreCase))
                {
                    _ = userIds.Add(id);
                }
                else if (string.Equals(assignment.PrincipalType, GroupPrincipalType, StringComparison.OrdinalIgnoreCase))
                {
                    if (seenGroupIds.Add(id))
                    {
                        groups.Add(new EntraGroupAssignment(id, assignment.PrincipalDisplayName ?? id));
                    }
                }
                else
                {
                    skippedServicePrincipals++;
                }
            }

            page = string.IsNullOrEmpty(page.OdataNextLink)
                ? null
                : await assignments.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }

        if (skippedServicePrincipals > 0)
        {
            logger.LogDebug(
                "Ignored {Count} app-role assignment(s) on service principal {ServicePrincipalObjectId} whose principal is a service principal.",
                skippedServicePrincipals,
                servicePrincipalObjectId);
        }

        // Pass 2: expand every group assignee to its user members (#121). Entra evaluates a security
        // group's app-role assignment transitively, so /transitiveMembers is what matches the access
        // the tenant actually granted. Ids merge into the same set as the direct assignees, so a person
        // assigned both ways is hydrated once.
        var unexpandedGroups = new List<EntraGroupAssignment>();
        foreach (var group in groups)
        {
            try
            {
                await foreach (var memberId in ListGroupMemberIdsAsync(group.GroupObjectId, transitive: true, cancellationToken))
                {
                    _ = userIds.Add(memberId);
                }
            }
            catch (Exception error) when (IsRecoverableGroupReadFault(error, cancellationToken))
            {
                // A group this run could not read leaves a partial view of the population, which is
                // exactly the case departure detection must not act on — so it is reported rather than
                // swallowed, and EntraUserSyncService suspends deactivation for the run. Whatever the
                // other groups yielded still counts: adds and updates stay correct.
                unexpandedGroups.Add(group);
                logger.LogWarning(
                    error,
                    "Could not expand app-role assignment group {GroupObjectId} ('{GroupDisplayName}') to its members; departure detection will be suspended for this run.",
                    group.GroupObjectId,
                    group.DisplayName);
            }
        }

        if (groups.Count > 0)
        {
            logger.LogInformation(
                "Expanded {ExpandedCount} of {GroupCount} group app-role assignment(s) on service principal {ServicePrincipalObjectId} to their members.",
                groups.Count - unexpandedGroups.Count,
                groups.Count,
                servicePrincipalObjectId);
        }

        // Pass 3: hydrate in chunks of ≤15 ids per request (the `in` operator's default cap).
        var users = new List<EntraUser>(userIds.Count);
        foreach (var chunk in userIds.Chunk(InFilterMaxValues))
        {
            var filter = "id in (" + string.Join(",", chunk.Select(id => $"'{id}'")) + ")";
            var response = await graph.Users.GetAsync(
                request =>
                {
                    request.QueryParameters.Filter = filter;
                    request.QueryParameters.Select = UserSelect;
                },
                cancellationToken);

            users.AddRange((response?.Value ?? []).Select(Map));
        }

        return new EntraAssignedUsers(users, unexpandedGroups);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListGroupMemberIdsAsync(
        string groupObjectId,
        bool transitive,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupObjectId);

        // Plain /members (a heterogeneous directoryObject collection, filtered to users client-side
        // by @odata.type) rather than the /members/microsoft.graph.user OData cast: the cast is an
        // "advanced query" that requires ConsistencyLevel: eventual + $count and reads from an
        // eventually-consistent index, which is exactly what a reconciliation sync should not do.
        var group = graph.Groups[groupObjectId];
        DirectoryObjectCollectionResponse? page = transitive
            ? await group.TransitiveMembers.GetAsync(
                request =>
                {
                    request.QueryParameters.Select = IdSelect;
                    request.QueryParameters.Top = MaxPageSize;
                },
                cancellationToken)
            : await group.Members.GetAsync(
                request =>
                {
                    request.QueryParameters.Select = IdSelect;
                    request.QueryParameters.Top = MaxPageSize;
                },
                cancellationToken);

        while (page is not null)
        {
            foreach (var member in page.Value ?? [])
            {
                if (member is GraphUser { Id: { } id })
                {
                    yield return id;
                }
            }

            if (string.IsNullOrEmpty(page.OdataNextLink))
            {
                break;
            }

            page = transitive
                ? await group.TransitiveMembers.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken)
                : await group.Members.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Whether a failure expanding one group assignee should degrade that <em>group</em> to
    /// "unexpanded" rather than fail the whole <see cref="ListAssignedUsersAsync"/> run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="ODataError"/> is Graph's structured error body — a refused permission, a group that
    /// no longer exists. But a fault that never reaches an API error body looks nothing like one:
    /// once Kiota's retry handler gives up on DNS, a reset connection or a timeout, the caller sees
    /// <see cref="HttpRequestException"/> or a <see cref="TaskCanceledException"/> whose token was
    /// never cancelled. Letting those propagate would turn "Graph was briefly unreachable while
    /// reading one group" into a <c>500</c> for the entire <c>POST /users/sync</c> — the opposite of
    /// what per-group degradation exists for, and worse than the suspension it replaces.
    /// </para>
    /// <para>
    /// The caller's own cancellation is never swallowed: with
    /// <paramref name="cancellationToken"/> signalled, every exception propagates, because a client
    /// that walked away is not a directory fault.
    /// </para>
    /// </remarks>
    private static bool IsRecoverableGroupReadFault(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && exception is ODataError or HttpRequestException or TaskCanceledException;

    /// <summary>
    /// The service principal whose assignments define FoundryGate's user population:
    /// <c>Entra:ServicePrincipalObjectId</c> when configured, otherwise resolved once from
    /// <c>AzureAd:ClientId</c> (<c>GET /servicePrincipals(appId='{clientId}')</c>) and cached for
    /// the process lifetime. A failed resolution is <em>not</em> cached, so a transient Graph fault
    /// or a not-yet-granted permission is retried on the next call.
    /// </summary>
    /// <exception cref="InvalidOperationException">No service principal exists for <c>AzureAd:ClientId</c> in this tenant.</exception>
    private async Task<string> GetServicePrincipalObjectIdAsync(CancellationToken cancellationToken)
    {
        if (_servicePrincipalObjectId is { } cached)
        {
            return cached;
        }

        await _servicePrincipalLock.WaitAsync(cancellationToken);
        try
        {
            if (_servicePrincipalObjectId is { } cachedAfterWait)
            {
                return cachedAfterWait;
            }

            ServicePrincipal? servicePrincipal;
            try
            {
                servicePrincipal = await graph.ServicePrincipalsWithAppId(_applicationClientId).GetAsync(
                    request => request.QueryParameters.Select = IdSelect,
                    cancellationToken);
            }
            catch (ODataError error) when (error.ResponseStatusCode == GraphNotFound)
            {
                servicePrincipal = null;
            }

            if (string.IsNullOrEmpty(servicePrincipal?.Id))
            {
                throw new InvalidOperationException(
                    $"No Entra service principal exists for Entra:ApplicationClientId (AzureAd:ClientId on the Api) '{_applicationClientId}' in this tenant, so the FoundryGate " +
                    "user population cannot be enumerated. Check Entra:ApplicationClientId (the Api falls back to AzureAd:ClientId), or set Entra:ServicePrincipalObjectId to the object id " +
                    "of the enterprise application developers are assigned to.");
            }

            logger.LogInformation(
                "Resolved FoundryGate service principal {ServicePrincipalObjectId} from Entra:ApplicationClientId {ClientId}.",
                servicePrincipal.Id,
                _applicationClientId);

            _servicePrincipalObjectId = servicePrincipal.Id;
            return servicePrincipal.Id;
        }
        finally
        {
            _ = _servicePrincipalLock.Release();
        }
    }

    private static EntraUser Map(GraphUser user)
    {
        var objectId = user.Id ?? throw new InvalidOperationException("Microsoft Graph returned a user without an id.");
        var userPrincipalName = user.UserPrincipalName ?? string.Empty;

        return new EntraUser(
            objectId,
            FirstNonBlank(user.DisplayName, userPrincipalName),
            FirstNonBlank(user.Mail, userPrincipalName),
            string.IsNullOrWhiteSpace(user.EmployeeId) ? null : user.EmployeeId.Trim());
    }

    private static string FirstNonBlank(string? preferred, string fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();
}
