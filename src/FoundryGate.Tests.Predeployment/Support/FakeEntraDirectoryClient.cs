using System.Runtime.CompilerServices;
using FoundryGate.Api.Services.Entra;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// In-memory <see cref="IEntraDirectoryClient"/>: whatever a test puts in <see cref="AssignedUsers"/>
/// and <see cref="GroupMembers"/> is what "the directory" returns. Streams asynchronously (one
/// <c>Task.Yield</c> per item) so consumers are exercised as genuine <see cref="IAsyncEnumerable{T}"/>
/// callers, not over a synchronously completed sequence. Hand-rolled — no mocking library
/// (CONVENTIONS.md).
/// </summary>
public sealed class FakeEntraDirectoryClient : IEntraDirectoryClient
{
    /// <summary>Users assigned to the FoundryGate application. Duplicates are yielded as-is so de-duplication is the consumer's job to prove.</summary>
    public List<EntraUser> AssignedUsers { get; } = [];

    /// <summary>Group object id → member object ids.</summary>
    public Dictionary<string, List<string>> GroupMembers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times <see cref="ListAssignedUsersAsync"/> was enumerated.</summary>
    public int ListAssignedUsersCalls { get; private set; }

    /// <inheritdoc />
    public Task<EntraUser?> GetUserAsync(string objectId, CancellationToken cancellationToken) =>
        Task.FromResult(AssignedUsers.FirstOrDefault(u => string.Equals(u.ObjectId, objectId, StringComparison.OrdinalIgnoreCase)));

    /// <inheritdoc />
    public async IAsyncEnumerable<EntraUser> ListAssignedUsersAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ListAssignedUsersCalls++;

        foreach (var user in AssignedUsers.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return user;
        }
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
