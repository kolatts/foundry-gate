using FoundryGate.Core.Entra;
using FoundryGate.Domain.Exceptions;

namespace FoundryGate.Tests.Predeployment.Core.Entra;

/// <summary>
/// The <c>Entra:Enabled = false</c> implementation must fail every call with the 503-mapped
/// <see cref="FeatureNotConfiguredException"/> and a message that names the setting and the Graph
/// roles to grant. 503, not 400: nothing about the request is wrong — the host is not configured for
/// the feature, which is the operator's problem to fix from the message (CONVENTIONS.md).
/// </summary>
public class DisabledEntraDirectoryClientTests
{
    private readonly DisabledEntraDirectoryClient _client = new();

    [Fact]
    public async Task GetUserAsync_throws_FeatureNotConfigured_naming_the_setting()
    {
        var exception = await Assert.ThrowsAsync<FeatureNotConfiguredException>(() => _client.GetUserAsync("oid", CancellationToken.None));

        AssertMessage(exception);
    }

    [Fact]
    public async Task ListAssignedUsersAsync_throws_FeatureNotConfigured_naming_the_setting()
    {
        var exception = await Assert.ThrowsAsync<FeatureNotConfiguredException>(() => _client.ListAssignedUsersAsync(CancellationToken.None));

        AssertMessage(exception);
    }

    [Fact]
    public void ListGroupMemberIdsAsync_throws_FeatureNotConfigured_naming_the_setting()
    {
        var exception = Assert.Throws<FeatureNotConfiguredException>(() => _client.ListGroupMemberIdsAsync("group", false, CancellationToken.None));

        AssertMessage(exception);
    }

    private static void AssertMessage(FeatureNotConfiguredException exception)
    {
        Assert.Contains("Entra:Enabled", exception.Message, StringComparison.Ordinal);
        Assert.Contains("User.Read.All", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GroupMember.ReadBasic.All", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Application.Read.All", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ServicePrincipalObjectId", exception.Message, StringComparison.Ordinal); // not an alternative to the roles
    }
}
