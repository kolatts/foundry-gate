using System.Runtime.CompilerServices;
using FoundryGate.Api.Services.Entra;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// In-memory <see cref="IEntraDirectoryClient"/>: whatever a test puts in <see cref="AssignedUsers"/>
/// and <see cref="GroupMembers"/> is what "the directory" returns. Completes asynchronously (a
/// <c>Task.Yield</c> per call or item) so consumers are exercised over genuinely asynchronous results,
/// not synchronously completed ones. Hand-rolled — no mocking library
/// (CONVENTIONS.md).
/// </summary>
public sealed class FakeEntraDirectoryClient : IEntraDirectoryClient
{
    /// <summary>Users assigned to the FoundryGate application. Duplicates are returned as-is so de-duplication is the consumer's job to prove.</summary>
    public List<EntraUser> AssignedUsers { get; } = [];

    /// <summary>
    /// Group-principal app-role assignments the directory could not expand to their members (#121) —
    /// the only reason a real client reports one, and what makes the sync suspend departure detection.
    /// A group that expands successfully shows up as its members in <see cref="AssignedUsers"/> instead.
    /// </summary>
    public List<EntraGroupAssignment> SkippedGroupAssignments { get; } = [];

    /// <summary>Group object id → member object ids.</summary>
    public Dictionary<string, List<string>> GroupMembers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times <see cref="ListAssignedUsersAsync"/> was enumerated.</summary>
    public int ListAssignedUsersCalls { get; private set; }

    /// <inheritdoc />
    public Task<EntraUser?> GetUserAsync(string objectId, CancellationToken cancellationToken) =>
        Task.FromResult(AssignedUsers.FirstOrDefault(u => string.Equals(u.ObjectId, objectId, StringComparison.OrdinalIgnoreCase)));

    /// <inheritdoc />
    public async Task<EntraAssignedUsers> ListAssignedUsersAsync(CancellationToken cancellationToken)
    {
        ListAssignedUsersCalls++;
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();

        return new EntraAssignedUsers(AssignedUsers.ToList(), SkippedGroupAssignments.ToList());
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListGroupMemberIdsAsync(
        string groupObjectId,
        bool transitive,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!GroupMembers.TryGetValue(groupObjectId, out var members))
        {
            yield break;
        }

        foreach (var member in members.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return member;
        }
    }
}
