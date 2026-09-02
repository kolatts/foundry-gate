using FoundryGate.Data.Entities;
using FoundryGate.Domain.Requests;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Data.Entities;

/// <summary>
/// The filtered unique index behind "one pending quota increase request per user per period" (#147),
/// exercised against the database rather than through the service that also read-then-writes for it.
/// </summary>
/// <remarks>
/// Every case here uses a <b>second context</b> for the competing insert: the service's
/// <c>AnyAsync</c> pre-check is what a serial double-submit hits, and only a writer that never saw the
/// first row can reach the constraint — which is exactly the concurrent submission (a double-clicked
/// button, a retrying client) the index exists to refuse.
/// </remarks>
public class QuotaIncreaseRequestIndexTests : InMemoryDatabaseTest
{
    [Fact]
    public async Task A_second_pending_request_for_the_same_user_and_period_is_refused()
    {
        var user = await SeedUserAsync();

        Context.QuotaIncreaseRequests.Add(Request(user, QuotaRequestStatusType.Pending));
        await Context.SaveChangesAsync();

        await using var other = CreateVerificationContext();
        other.QuotaIncreaseRequests.Add(Request(user, QuotaRequestStatusType.Pending));

        _ = await Assert.ThrowsAsync<DbUpdateException>(() => other.SaveChangesAsync());
    }

    [Theory]
    [InlineData(QuotaRequestStatusType.Approved)]
    [InlineData(QuotaRequestStatusType.Rejected)]
    public async Task A_decided_request_never_blocks_the_next_one_for_the_same_period(QuotaRequestStatusType decided)
    {
        // The whole reason the index is filtered. Unfiltered, a developer whose September request was
        // approved on the 2nd could not file another one until October — and CancelPendingForUserAsync,
        // which closes a departing user's requests as Rejected, would leave the same trap behind.
        var user = await SeedUserAsync();

        Context.QuotaIncreaseRequests.Add(Request(user, decided));
        await Context.SaveChangesAsync();

        await using var other = CreateVerificationContext();
        other.QuotaIncreaseRequests.Add(Request(user, QuotaRequestStatusType.Pending));
        await other.SaveChangesAsync();

        Assert.Equal(2, await Context.QuotaIncreaseRequests.AsNoTracking().CountAsync(r => r.UserId == user.UserId));
    }

    [Fact]
    public async Task Two_pending_requests_for_the_same_user_in_different_periods_are_allowed()
    {
        var user = await SeedUserAsync();

        Context.QuotaIncreaseRequests.Add(Request(user, QuotaRequestStatusType.Pending, month: 9));
        await Context.SaveChangesAsync();

        await using var other = CreateVerificationContext();
        other.QuotaIncreaseRequests.Add(Request(user, QuotaRequestStatusType.Pending, month: 10));
        await other.SaveChangesAsync();

        Assert.Equal(2, await Context.QuotaIncreaseRequests.AsNoTracking().CountAsync(r => r.UserId == user.UserId));
    }

    [Fact]
    public async Task Two_users_may_both_have_a_pending_request_for_the_same_period()
    {
        var first = await SeedUserAsync();
        var second = await SeedUserAsync();

        Context.QuotaIncreaseRequests.Add(Request(first, QuotaRequestStatusType.Pending));
        await Context.SaveChangesAsync();

        await using var other = CreateVerificationContext();
        other.QuotaIncreaseRequests.Add(Request(second, QuotaRequestStatusType.Pending));
        await other.SaveChangesAsync();

        Assert.Equal(2, await Context.QuotaIncreaseRequests.AsNoTracking().CountAsync());
    }

    private static QuotaIncreaseRequest Request(User user, QuotaRequestStatusType status, int month = 9) =>
        new()
        {
            UserId = user.UserId,
            RequestedByUserId = user.UserId,
            PeriodYear = 2026,
            PeriodMonth = month,
            CurrentQuota = 5_000_000,
            RequestedQuota = 20_000_000,
            Justification = "Batch evaluation this sprint.",
            StatusType = status,
        };

    private async Task<User> SeedUserAsync()
    {
        var user = new User
        {
            EntraObjectId = Guid.NewGuid().ToString(),
            DisplayName = "Ada Lovelace",
            Email = $"{Guid.NewGuid():N}@contoso.test",
        };
        Context.Users.Add(user);
        await Context.SaveChangesAsync();
        return user;
    }
}
