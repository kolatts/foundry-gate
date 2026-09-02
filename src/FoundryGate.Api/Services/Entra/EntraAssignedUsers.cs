namespace FoundryGate.Api.Services.Entra;

/// <summary>
/// What <see cref="IEntraDirectoryClient.ListAssignedUsersAsync"/> found on the FoundryGate service
/// principal's app-role assignments: every person the assignments cover — direct user assignees and
/// the transitive user members of group assignees, merged (#121) — plus the group assignments this
/// run could <em>not</em> read, which have to be surfaced so the sync can suspend departure detection
/// instead of deactivating everyone such a group covers on a view it knows is incomplete.
/// </summary>
/// <param name="Users">Distinct people the assignments cover, with their directory fields.</param>
/// <param name="SkippedGroupAssignments">
/// Group assignees whose member expansion failed (Graph refused or the group is gone). Empty on a
/// clean run — including one where every assignee is a group, because a group that expanded is
/// represented by its members.
/// </param>
public sealed record EntraAssignedUsers(IReadOnlyList<EntraUser> Users, IReadOnlyList<EntraGroupAssignment> SkippedGroupAssignments);

/// <summary>A group that holds an app-role assignment on the FoundryGate application.</summary>
/// <param name="GroupObjectId">The group's Entra object id (<c>appRoleAssignment.principalId</c>).</param>
/// <param name="DisplayName">The group's display name (<c>appRoleAssignment.principalDisplayName</c>), for logs and the audit row.</param>
public sealed record EntraGroupAssignment(string GroupObjectId, string DisplayName);
