using FoundryGate.Domain.Keys;

namespace FoundryGate.Tests.Predeployment.Domain;

/// <summary>The user ↔ APIM subscription naming contract shared by the Api (mint) and Functions (attribute usage).</summary>
public class ApimSubscriptionNamesTests
{
    [Fact]
    public void ForUser_is_the_prefix_plus_the_invariant_user_id()
    {
        Assert.Equal("foundrygate-42", ApimSubscriptionNames.ForUser(42));
        Assert.Equal("foundrygate-1", ApimSubscriptionNames.ForUser(1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public void ForUser_refuses_an_unsaved_or_invalid_id(int userId) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ApimSubscriptionNames.ForUser(userId));

    [Fact]
    public void TryGetUserId_round_trips_a_minted_name()
    {
        Assert.True(ApimSubscriptionNames.TryGetUserId(ApimSubscriptionNames.ForUser(4711), out var userId));
        Assert.Equal(4711, userId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("master")]
    [InlineData("foundrygate-")]
    [InlineData("foundrygate-abc")]
    [InlineData("foundrygate--1")]
    [InlineData("foundrygate-0")]
    [InlineData("FoundryGate-42")]
    [InlineData("foundrygate-42-extra")]
    public void TryGetUserId_rejects_names_FoundryGate_did_not_mint(string? name)
    {
        Assert.False(ApimSubscriptionNames.TryGetUserId(name, out var userId));
        Assert.Equal(0, userId);
    }
}
