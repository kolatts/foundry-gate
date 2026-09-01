using FoundryGate.Api.Services.Entra;

namespace FoundryGate.Tests.Predeployment.Api.Services.Entra;

/// <summary>
/// The <c>Entra:Enabled = false</c> implementation must fail every call with the 400-mapped
/// <see cref="ArgumentException"/> and a message that names the setting and the Graph roles to grant.
/// </summary>
public class DisabledEntraDirectoryClientTests
{
    private readonly DisabledEntraDirectoryClient _client = new();

    [Fact]
    public async Task GetUserAsync_throws_ArgumentException_naming_the_setting()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => _client.GetUserAsync("oid", CancellationToken.None));

        AssertMessage(exception);
    }

    [Fact]
    public void ListAssignedUsersAsync_throws_ArgumentException_naming_the_setting()
    {
        var exception = Assert.Throws<ArgumentException>(() => _client.ListAssignedUsersAsync(CancellationToken.None));

        AssertMessage(exception);
    }

    [Fact]
    public void ListGroupMemberIdsAsync_throws_ArgumentException_naming_the_setting()
    {
        var exception = Assert.Throws<ArgumentException>(() => _client.ListGroupMemberIdsAsync("group", false, CancellationToken.None));

        AssertMessage(exception);
    }

    private static void AssertMessage(ArgumentException exception)
    {
        Assert.Contains("Entra:Enabled", exception.Message, StringComparison.Ordinal);
        Assert.Contains("User.Read.All", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GroupMember.Read.All", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Application.Read.All", exception.Message, StringComparison.Ordinal);
    }
}
