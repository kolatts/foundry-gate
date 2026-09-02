using FoundryGate.Core.Requests;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Requests;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoundryGate.Tests.Predeployment.Core.Requests;

/// <summary>
/// The shared stale-request sweep (#159) at the level both hosts use it: which rows it closes, what it
/// leaves alone, the single audit row it writes, and its no-save contract.
/// </summary>
public class QuotaRequestExpiryTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 1, 0, TimeSpan.Zero);
    private static readonly BillingPeriod Current = new(2026, 10);

    private readonly MutableTimeProvider _clock = new(Now);

    [Fact]
    public async Task A_pending_request_from_a_closed_period_becomes_rejected_with_a_system_note()
    {
        var dev = await SeedUserAsync();
        var stale = await SeedRequestAsync(dev, new BillingPeriod(2026, 7), QuotaRequestStatusType.Pending);

        var expired = await CreateService().ExpireStaleAsync(Current, CancellationToken.None);
        await Context.SaveChangesAsync();

        Assert.Equal(1, expired);
        var row = await Context.QuotaIncreaseRequests.AsNoTracking().SingleAsync(r => r.QuotaIncreaseRequestId == stale);
        Assert.Equal(QuotaRequestStatusType.Rejected, row.StatusType);
        Assert.Equal(Now, row.ReviewedDate);
        Assert.Equal(QuotaRequestExpiry.SystemNote, row.ReviewNotes);

        // No reviewer: that is what distinguishes a lapsed request from one an admin turned down, and the
        // period it was filed for is left intact so the trail still says which month it was about.
        Assert.Null(row.ReviewedByUserId);
        Assert.Equal((2026, 7), (row.PeriodYear, row.PeriodMonth));
    }

    [Fact]
    public async Task The_current_periods_pending_request_is_left_alone()
    {
        var dev = await SeedUserAsync();
        var live = await SeedRequestAsync(dev, Current, QuotaRequestStatusType.Pending);

        var expired = await CreateService().ExpireStaleAsync(Current, CancellationToken.None);

        Assert.Equal(0, expired);
        var row = await Context.QuotaIncreaseRequests.AsNoTracking().SingleAsync(r => r.QuotaIncreaseRequestId == live);
        Assert.Equal(QuotaRequestStatusType.Pending, row.StatusType);
    }

    [Theory]
    [InlineData(QuotaRequestStatusType.Approved)]
    [InlineData(QuotaRequestStatusType.Rejected)]
    public async Task An_already_decided_request_from_a_closed_period_is_not_touched(QuotaRequestStatusType decided)
    {
        var dev = await SeedUserAsync();
        var old = await SeedRequestAsync(dev, new BillingPeriod(2026, 7), decided);

        var expired = await CreateService().ExpireStaleAsync(Current, CancellationToken.None);

        Assert.Equal(0, expired);
        var row = await Context.QuotaIncreaseRequests.AsNoTracking().SingleAsync(r => r.QuotaIncreaseRequestId == old);
        Assert.Equal(decided, row.StatusType);
        Assert.Equal(string.Empty, row.ReviewNotes);
    }

    [Fact]
    public async Task The_previous_December_expires_against_a_January_period()
    {
        // The comparison is (year, month) as an ordered pair, not month alone — December 2025 is earlier
        // than January 2026 and must expire, which a naive `PeriodMonth < current.Month` would miss.
        var dev = await SeedUserAsync();
        var december = await SeedRequestAsync(dev, new BillingPeriod(2025, 12), QuotaRequestStatusType.Pending);

        var expired = await CreateService().ExpireStaleAsync(new BillingPeriod(2026, 1), CancellationToken.None);
        await Context.SaveChangesAsync();

        Assert.Equal(1, expired);
        Assert.Equal(
            QuotaRequestStatusType.Rejected,
            (await Context.QuotaIncreaseRequests.AsNoTracking().SingleAsync(r => r.QuotaIncreaseRequestId == december)).StatusType);
    }

    [Fact]
    public async Task Many_stale_requests_produce_one_audit_row_carrying_the_count_and_the_ids()
    {
        var first = await SeedUserAsync();
        var second = await SeedUserAsync();
        var a = await SeedRequestAsync(first, new BillingPeriod(2026, 7), QuotaRequestStatusType.Pending);
        var b = await SeedRequestAsync(first, new BillingPeriod(2026, 8), QuotaRequestStatusType.Pending);
        var c = await SeedRequestAsync(second, new BillingPeriod(2026, 8), QuotaRequestStatusType.Pending);

        var expired = await CreateService().ExpireStaleAsync(Current, CancellationToken.None);
        await Context.SaveChangesAsync();

        Assert.Equal(3, expired);
        var audit = Assert.Single(await Context.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal(AuditActions.QuotaRequestsExpired, audit.Action);
        Assert.Null(audit.ActorUserId); // no human decided these
        Assert.Equal(string.Empty, audit.TargetType);
        Assert.Equal(string.Empty, audit.TargetId);
        Assert.Contains("\"expiredCount\":3", audit.Details, StringComparison.Ordinal);
        Assert.Contains($"[{a},{b},{c}]", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sweep_that_finds_nothing_writes_no_audit_row()
    {
        // Otherwise every reset of every month would leave a row saying nothing happened, and the audit
        // viewer's signal-to-noise is the whole point of one row per run.
        _ = await SeedUserAsync();

        var expired = await CreateService().ExpireStaleAsync(Current, CancellationToken.None);
        await Context.SaveChangesAsync();

        Assert.Equal(0, expired);
        Assert.Empty(await Context.AuditLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Nothing_is_saved_until_the_caller_saves()
    {
        // The Core no-save contract: the closed rows and the audit row belong to the caller's unit of
        // work, so a reset that fails afterwards rolls the sweep back with it.
        var dev = await SeedUserAsync();
        var stale = await SeedRequestAsync(dev, new BillingPeriod(2026, 7), QuotaRequestStatusType.Pending);

        _ = await CreateService().ExpireStaleAsync(Current, CancellationToken.None);

        await using var verify = CreateVerificationContext();
        Assert.Equal(
            QuotaRequestStatusType.Pending,
            (await verify.QuotaIncreaseRequests.AsNoTracking().SingleAsync(r => r.QuotaIncreaseRequestId == stale)).StatusType);
        Assert.Empty(await verify.AuditLogs.AsNoTracking().ToListAsync());
    }

    private QuotaRequestExpiry CreateService() =>
        new(Context, new AuditWriter(Context, _clock), _clock, NullLogger<QuotaRequestExpiry>.Instance);

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

    private async Task<int> SeedRequestAsync(User user, BillingPeriod period, QuotaRequestStatusType status)
    {
        var request = new QuotaIncreaseRequest
        {
            UserId = user.UserId,
            RequestedByUserId = user.UserId,
            PeriodYear = period.Year,
            PeriodMonth = period.Month,
            CurrentQuota = 5_000_000,
            RequestedQuota = 20_000_000,
            Justification = "Running a batch evaluation this sprint.",
            StatusType = status,
        };
        Context.QuotaIncreaseRequests.Add(request);
        await Context.SaveChangesAsync();
        Context.Entry(request).State = EntityState.Detached;
        return request.QuotaIncreaseRequestId;
    }
}
