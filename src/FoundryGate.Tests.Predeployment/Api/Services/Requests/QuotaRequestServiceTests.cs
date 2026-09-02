using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Api.Services.Quota;
using FoundryGate.Api.Services.Requests;
using FoundryGate.Data;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Requests;
using FoundryGate.Domain.Requests.Contracts;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Web;

namespace FoundryGate.Tests.Predeployment.Api.Services.Requests;

/// <summary>
/// The quota increase workflow (#34, #35) over the real resolution service, accessor, audit writer,
/// tier mapper and a movable clock: submission's four refusals (non-tier value, not an increase,
/// already unlimited, duplicate pending), role-scoped listing and reads, and the review transitions —
/// including that an approval actually moves the user's budget, re-resolves the period and asks the
/// gateway to move the subscription.
/// </summary>
public class QuotaRequestServiceTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly BillingPeriod Period = new(2026, 9);
    private const string Justification = "Running a batch evaluation this sprint that needs more headroom.";

    private readonly MutableTimeProvider _clock = new(Now);
    private readonly RecordingGatewayTierSync _tierSync = new();

    // -- SubmitAsync --

    [Fact]
    public async Task SubmitAsync_captures_the_current_quota_period_and_requester_and_audits_the_submission()
    {
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);

        var result = await CreateService(me.EntraObjectId).SubmitAsync(
            new SubmitQuotaIncreaseRequest { RequestedQuota = TestGatewayTiers.PowerCap, Justification = Justification },
            CancellationToken.None);

        Assert.Equal(me.UserId, result.UserId);
        Assert.Equal(me.UserUnique, result.UserUnique);
        Assert.Equal("Ada", result.UserDisplayName);
        Assert.Equal(me.UserId, result.RequestedByUserId);
        Assert.Equal((Period.Year, Period.Month), (result.PeriodYear, result.PeriodMonth));
        Assert.Equal(TestGatewayTiers.StandardCap, result.CurrentQuota);
        Assert.Equal(TestGatewayTiers.PowerCap, result.RequestedQuota);
        Assert.Equal(Justification, result.Justification);
        Assert.Equal(QuotaRequestStatusType.Pending, result.StatusType);
        Assert.Null(result.ReviewedByUserId);
        Assert.Null(result.ReviewedDate);
        Assert.Null(result.ReviewNotes);
        Assert.NotEqual(Guid.Empty, result.QuotaIncreaseRequestUnique);

        // The allocation the request measured itself against was created and committed with it.
        Assert.True(await Context.QuotaAllocations.AsNoTracking().AnyAsync(a => a.UserId == me.UserId && a.PeriodYear == Period.Year));

        var audit = Assert.Single(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.QuotaIncreaseSubmitted).ToListAsync());
        Assert.Equal(me.UserId, audit.ActorUserId);
        Assert.Equal(AuditTargetTypes.QuotaIncreaseRequest, audit.TargetType);
        Assert.Equal(result.QuotaIncreaseRequestId.ToString(CultureInfo.InvariantCulture), audit.TargetId);
        using var details = JsonDocument.Parse(audit.Details);
        Assert.Equal(me.UserId, details.RootElement.GetProperty("userId").GetInt32());
        Assert.Equal(TestGatewayTiers.StandardCap, details.RootElement.GetProperty("currentQuota").GetInt64());
        Assert.Equal(TestGatewayTiers.PowerCap, details.RootElement.GetProperty("requestedQuota").GetInt64());
    }

    [Fact]
    public async Task SubmitAsync_accepts_a_request_for_unlimited_from_a_finite_budget()
    {
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.PowerCap);

        var result = await CreateService(me.EntraObjectId).SubmitAsync(
            new SubmitQuotaIncreaseRequest { RequestedQuota = null, Justification = Justification },
            CancellationToken.None);

        Assert.Null(result.RequestedQuota);
        Assert.Equal(TestGatewayTiers.PowerCap, result.CurrentQuota);
    }

    [Fact]
    public async Task SubmitAsync_measures_against_the_existing_allocation_rather_than_re_resolving()
    {
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.PowerCap);
        await SeedAllocationAsync(me, Period, allocated: TestGatewayTiers.StandardCap); // mid-month state, not yet re-resolved

        var result = await CreateService(me.EntraObjectId).SubmitAsync(
            new SubmitQuotaIncreaseRequest { RequestedQuota = TestGatewayTiers.PowerCap, Justification = Justification },
            CancellationToken.None);

        Assert.Equal(TestGatewayTiers.StandardCap, result.CurrentQuota);
        Assert.Equal(1, await Context.QuotaAllocations.AsNoTracking().CountAsync(a => a.UserId == me.UserId));
    }

    [Theory]
    [InlineData(7_000_000)]
    [InlineData(1)]
    [InlineData(0)]
    public async Task SubmitAsync_rejects_a_value_that_is_not_a_configured_tier_cap(long requestedQuota)
    {
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => CreateService(me.EntraObjectId).SubmitAsync(
            new SubmitQuotaIncreaseRequest { RequestedQuota = requestedQuota, Justification = Justification },
            CancellationToken.None));

        Assert.Contains("not a configured budget tier", exception.Message, StringComparison.Ordinal);
        Assert.Contains("20,000,000", exception.Message, StringComparison.Ordinal); // the allowed values are listed
        Assert.False(await Context.QuotaIncreaseRequests.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task SubmitAsync_rejects_a_request_for_the_tier_the_caller_is_already_on()
    {
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => CreateService(me.EntraObjectId).SubmitAsync(
            new SubmitQuotaIncreaseRequest { RequestedQuota = TestGatewayTiers.StandardCap, Justification = Justification },
            CancellationToken.None));

        Assert.Contains("is not an increase", exception.Message, StringComparison.Ordinal);
        Assert.False(await Context.QuotaIncreaseRequests.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task SubmitAsync_rejects_a_smaller_tier_as_not_an_increase_naming_the_current_budget()
    {
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.PowerCap);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => CreateService(me.EntraObjectId).SubmitAsync(
            new SubmitQuotaIncreaseRequest { RequestedQuota = TestGatewayTiers.StandardCap, Justification = Justification },
            CancellationToken.None));

        Assert.Contains("is not an increase", exception.Message, StringComparison.Ordinal);
        Assert.Contains("20,000,000", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitAsync_rejects_a_caller_who_is_already_unlimited()
    {
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Ada", u => u.IsUnlimited = true);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => CreateService(me.EntraObjectId).SubmitAsync(
            new SubmitQuotaIncreaseRequest { RequestedQuota = null, Justification = Justification },
            CancellationToken.None));

        Assert.Contains("already has an unlimited budget", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitAsync_rejects_a_second_pending_request_in_the_same_period()
    {
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        var service = CreateService(me.EntraObjectId);
        var body = new SubmitQuotaIncreaseRequest { RequestedQuota = TestGatewayTiers.PowerCap, Justification = Justification };

        _ = await service.SubmitAsync(body, CancellationToken.None);
        var exception = await Assert.ThrowsAsync<ConflictException>(() => service.SubmitAsync(body, CancellationToken.None));

        Assert.Contains("already has a pending quota increase request for 2026-09", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, await Context.QuotaIncreaseRequests.AsNoTracking().CountAsync(r => r.UserId == me.UserId));
    }

    [Fact]
    public async Task SubmitAsync_allows_a_new_request_once_the_previous_one_was_decided()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var me = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        var body = new SubmitQuotaIncreaseRequest { RequestedQuota = TestGatewayTiers.PowerCap, Justification = Justification };

        var first = await CreateService(me.EntraObjectId).SubmitAsync(body, CancellationToken.None);
        _ = await CreateService(admin.EntraObjectId, isAdmin: true).RejectAsync(
            first.QuotaIncreaseRequestId, new ReviewQuotaIncreaseRequest { ReviewNotes = "Not this month." }, CancellationToken.None);

        var second = await CreateService(me.EntraObjectId).SubmitAsync(body, CancellationToken.None);

        Assert.NotEqual(first.QuotaIncreaseRequestId, second.QuotaIncreaseRequestId);
        Assert.Equal(QuotaRequestStatusType.Pending, second.StatusType);
    }

    [Fact]
    public async Task SubmitAsync_is_not_blocked_by_a_pending_request_from_an_earlier_period()
    {
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        await SeedRequestAsync(me, me, new BillingPeriod(2026, 8), requestedQuota: TestGatewayTiers.PowerCap);

        var result = await CreateService(me.EntraObjectId).SubmitAsync(
            new SubmitQuotaIncreaseRequest { RequestedQuota = TestGatewayTiers.PowerCap, Justification = Justification },
            CancellationToken.None);

        Assert.Equal((Period.Year, Period.Month), (result.PeriodYear, result.PeriodMonth));
    }

    [Fact]
    public async Task SubmitAsync_refuses_a_caller_with_no_User_row()
    {
        await SeedReferenceDataAsync();

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreateService("no-such-oid").SubmitAsync(
            new SubmitQuotaIncreaseRequest { RequestedQuota = TestGatewayTiers.PowerCap, Justification = Justification },
            CancellationToken.None));

        Assert.Contains("GET /users/me", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitAsync_refuses_a_deactivated_caller_and_creates_nothing()
    {
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Gone", u =>
        {
            u.IsActive = false;
            u.MonthlyTokenQuota = TestGatewayTiers.StandardCap;
        });

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreateService(me.EntraObjectId).SubmitAsync(
            new SubmitQuotaIncreaseRequest { RequestedQuota = TestGatewayTiers.PowerCap, Justification = Justification },
            CancellationToken.None));

        Assert.Contains("deactivated", exception.Message, StringComparison.Ordinal);
        Assert.False(await Context.QuotaIncreaseRequests.AsNoTracking().AnyAsync());
        Assert.False(await Context.QuotaAllocations.AsNoTracking().AnyAsync());
    }

    // -- SubmitForUserAsync --

    [Fact]
    public async Task SubmitForUserAsync_files_against_the_subject_but_records_the_admin_as_requester()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var dev = await SeedUserAsync("Dev", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);

        var result = await CreateService(admin.EntraObjectId, isAdmin: true).SubmitForUserAsync(
            dev.UserId,
            new SubmitQuotaIncreaseRequest { RequestedQuota = TestGatewayTiers.PowerCap, Justification = Justification },
            CancellationToken.None);

        Assert.Equal(dev.UserId, result.UserId);
        Assert.Equal(admin.UserId, result.RequestedByUserId);
        Assert.Equal(TestGatewayTiers.StandardCap, result.CurrentQuota);

        var audit = Assert.Single(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.QuotaIncreaseSubmitted).ToListAsync());
        Assert.Equal(admin.UserId, audit.ActorUserId);
    }

    [Fact]
    public async Task SubmitForUserAsync_unknown_user_is_404()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");

        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() => CreateService(admin.EntraObjectId, isAdmin: true).SubmitForUserAsync(
            999_999,
            new SubmitQuotaIncreaseRequest { RequestedQuota = TestGatewayTiers.PowerCap, Justification = Justification },
            CancellationToken.None));

        Assert.Contains("999999", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SubmitForUserAsync_for_a_deactivated_subject_is_409()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var dev = await SeedUserAsync("Gone", u =>
        {
            u.IsActive = false;
            u.MonthlyTokenQuota = TestGatewayTiers.StandardCap;
        });

        var exception = await Assert.ThrowsAsync<ConflictException>(() => CreateService(admin.EntraObjectId, isAdmin: true).SubmitForUserAsync(
            dev.UserId,
            new SubmitQuotaIncreaseRequest { RequestedQuota = TestGatewayTiers.PowerCap, Justification = Justification },
            CancellationToken.None));

        Assert.Contains("deactivated", exception.Message, StringComparison.Ordinal);
    }

    // -- ListAsync --

    [Fact]
    public async Task ListAsync_for_a_non_admin_returns_only_their_own_requests_newest_first()
    {
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        var other = await SeedUserAsync("Bob", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        var older = await SeedRequestAsync(me, me, new BillingPeriod(2026, 7), requestedQuota: TestGatewayTiers.PowerCap, createdDate: Now.AddDays(-40));
        var newer = await SeedRequestAsync(me, me, Period, requestedQuota: TestGatewayTiers.PowerCap, createdDate: Now);
        _ = await SeedRequestAsync(other, other, Period, requestedQuota: TestGatewayTiers.PowerCap);

        var page = await CreateService(me.EntraObjectId).ListAsync(new QuotaRequestQuery(null, null), new PagedRequest(), CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(
            [newer.QuotaIncreaseRequestId, older.QuotaIncreaseRequestId],
            page.Items.Select(i => i.QuotaIncreaseRequestId));
    }

    [Fact]
    public async Task ListAsync_for_a_non_admin_naming_another_user_is_403()
    {
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Ada");
        var other = await SeedUserAsync("Bob");

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CreateService(me.EntraObjectId)
            .ListAsync(new QuotaRequestQuery(null, other.UserId), new PagedRequest(), CancellationToken.None));

        Assert.Contains("Only an admin", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAsync_for_a_non_admin_naming_themselves_is_allowed()
    {
        await SeedReferenceDataAsync();
        var me = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        _ = await SeedRequestAsync(me, me, Period, requestedQuota: TestGatewayTiers.PowerCap);

        var page = await CreateService(me.EntraObjectId).ListAsync(new QuotaRequestQuery(null, me.UserId), new PagedRequest(), CancellationToken.None);

        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task ListAsync_for_an_admin_returns_every_users_requests_and_honours_both_filters_and_paging()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var ada = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        var bob = await SeedUserAsync("Bob", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        _ = await SeedRequestAsync(ada, ada, Period, requestedQuota: TestGatewayTiers.PowerCap, createdDate: Now.AddMinutes(-2));
        var adaApproved = await SeedRequestAsync(ada, ada, new BillingPeriod(2026, 8), requestedQuota: TestGatewayTiers.PowerCap, status: QuotaRequestStatusType.Approved, createdDate: Now.AddMinutes(-1));
        _ = await SeedRequestAsync(bob, bob, Period, requestedQuota: TestGatewayTiers.PowerCap, createdDate: Now);
        var service = CreateService(admin.EntraObjectId, isAdmin: true);

        var all = await service.ListAsync(new QuotaRequestQuery(null, null), new PagedRequest(), CancellationToken.None);
        var pending = await service.ListAsync(new QuotaRequestQuery(QuotaRequestStatusType.Pending, null), new PagedRequest(), CancellationToken.None);
        var adasOnly = await service.ListAsync(new QuotaRequestQuery(null, ada.UserId), new PagedRequest(), CancellationToken.None);
        var firstPage = await service.ListAsync(new QuotaRequestQuery(null, null), new PagedRequest(Page: 1, PageSize: 2), CancellationToken.None);
        var secondPage = await service.ListAsync(new QuotaRequestQuery(null, null), new PagedRequest(Page: 2, PageSize: 2), CancellationToken.None);

        Assert.Equal(3, all.TotalCount);
        Assert.Equal(2, pending.TotalCount);
        Assert.DoesNotContain(pending.Items, i => i.QuotaIncreaseRequestId == adaApproved.QuotaIncreaseRequestId);
        Assert.Equal(2, adasOnly.TotalCount);
        Assert.All(adasOnly.Items, i => Assert.Equal(ada.UserId, i.UserId));
        Assert.Equal(2, firstPage.Items.Count);
        Assert.Single(secondPage.Items);
        Assert.Equal(2, firstPage.TotalPages);
    }

    // -- GetAsync --

    [Fact]
    public async Task GetAsync_returns_the_request_to_its_owner_and_to_an_admin()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var ada = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        var request = await SeedRequestAsync(ada, ada, Period, requestedQuota: TestGatewayTiers.PowerCap);

        var asOwner = await CreateService(ada.EntraObjectId).GetAsync(request.QuotaIncreaseRequestId, CancellationToken.None);
        var asAdmin = await CreateService(admin.EntraObjectId, isAdmin: true).GetAsync(request.QuotaIncreaseRequestId, CancellationToken.None);

        Assert.Equal(request.QuotaIncreaseRequestId, asOwner.QuotaIncreaseRequestId);
        Assert.Equal("Ada", asAdmin.UserDisplayName);
    }

    [Fact]
    public async Task GetAsync_for_someone_elses_request_is_404_not_403()
    {
        await SeedReferenceDataAsync();
        var ada = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        var bob = await SeedUserAsync("Bob");
        var request = await SeedRequestAsync(ada, ada, Period, requestedQuota: TestGatewayTiers.PowerCap);

        var exception = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateService(bob.EntraObjectId).GetAsync(request.QuotaIncreaseRequestId, CancellationToken.None));

        // Byte-for-byte the message an unknown id produces: no enumeration oracle.
        Assert.Equal($"Quota increase request {request.QuotaIncreaseRequestId} was not found.", exception.Message);
    }

    [Fact]
    public async Task GetAsync_unknown_id_is_404()
    {
        await SeedReferenceDataAsync();
        var ada = await SeedUserAsync("Ada");

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(() => CreateService(ada.EntraObjectId).GetAsync(999_999, CancellationToken.None));
    }

    // -- ApproveAsync --

    [Fact]
    public async Task ApproveAsync_applies_the_tier_re_resolves_the_period_moves_the_gateway_and_audits_before_and_after()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var ada = await SeedUserAsync("Ada", u =>
        {
            u.MonthlyTokenQuota = TestGatewayTiers.StandardCap;
            u.ApimSubscriptionId = "sub-ada";
        });
        await SeedAllocationAsync(ada, Period, allocated: TestGatewayTiers.StandardCap, tokensUsed: 4_000_000);
        var request = await SeedRequestAsync(ada, ada, Period, requestedQuota: TestGatewayTiers.PowerCap);
        _clock.Advance(TimeSpan.FromHours(3));

        var result = await CreateService(admin.EntraObjectId, isAdmin: true).ApproveAsync(
            request.QuotaIncreaseRequestId, new ReviewQuotaIncreaseRequest { ReviewNotes = "Approved for the eval sprint." }, CancellationToken.None);

        Assert.Equal(QuotaRequestStatusType.Approved, result.StatusType);
        Assert.Equal(admin.UserId, result.ReviewedByUserId);
        Assert.Equal(Now.AddHours(3), result.ReviewedDate);
        Assert.Equal("Approved for the eval sprint.", result.ReviewNotes);

        var user = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == ada.UserId);
        Assert.Equal(TestGatewayTiers.PowerCap, user.MonthlyTokenQuota);
        Assert.False(user.IsUnlimited);

        var allocation = await Context.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == ada.UserId && a.PeriodYear == Period.Year);
        Assert.Equal(TestGatewayTiers.PowerCap, allocation.AllocatedTokens);
        Assert.Equal(GatewayTiers.Power, allocation.TierProductId);
        Assert.Equal(4_000_000, allocation.TokensUsed); // usage is reconciliation's, not the approval's
        Assert.Equal(QuotaLevelType.UserOverride, allocation.ResolvedLevelType);

        Assert.Equal([(ada.UserId, GatewayTiers.Power)], _tierSync.Calls);

        var audit = Assert.Single(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.QuotaIncreaseApproved).ToListAsync());
        Assert.Equal(admin.UserId, audit.ActorUserId);
        Assert.Equal(AuditTargetTypes.QuotaIncreaseRequest, audit.TargetType);
        using var details = JsonDocument.Parse(audit.Details);
        Assert.Equal(TestGatewayTiers.StandardCap, details.RootElement.GetProperty("before").GetProperty("monthlyTokenQuota").GetInt64());
        Assert.Equal(TestGatewayTiers.PowerCap, details.RootElement.GetProperty("after").GetProperty("monthlyTokenQuota").GetInt64());
        Assert.Equal(GatewayTiers.Power, details.RootElement.GetProperty("tierProductId").GetString());
        Assert.True(details.RootElement.GetProperty("tierSyncRequested").GetBoolean());
    }

    [Fact]
    public async Task ApproveAsync_for_an_unlimited_request_sets_the_flag_and_clears_the_number()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var ada = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.PowerCap);
        var request = await SeedRequestAsync(ada, ada, Period, requestedQuota: null);

        var result = await CreateService(admin.EntraObjectId, isAdmin: true).ApproveAsync(
            request.QuotaIncreaseRequestId, new ReviewQuotaIncreaseRequest(), CancellationToken.None);

        Assert.Equal(QuotaRequestStatusType.Approved, result.StatusType);
        Assert.Null(result.ReviewNotes); // "" on the row reads back as null

        var user = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == ada.UserId);
        Assert.True(user.IsUnlimited);
        Assert.Null(user.MonthlyTokenQuota);

        var allocation = await Context.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == ada.UserId);
        Assert.Null(allocation.AllocatedTokens);
        Assert.Equal(GatewayTiers.Unlimited, allocation.TierProductId);
        Assert.Equal(QuotaLevelType.UserUnlimited, allocation.ResolvedLevelType);
    }

    [Fact]
    public async Task ApproveAsync_on_an_already_decided_request_is_409_and_changes_nothing()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var ada = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        var request = await SeedRequestAsync(ada, ada, Period, requestedQuota: TestGatewayTiers.PowerCap);
        var service = CreateService(admin.EntraObjectId, isAdmin: true);
        _ = await service.ApproveAsync(request.QuotaIncreaseRequestId, new ReviewQuotaIncreaseRequest(), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            service.ApproveAsync(request.QuotaIncreaseRequestId, new ReviewQuotaIncreaseRequest(), CancellationToken.None));

        Assert.Contains("already approved", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, await Context.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditActions.QuotaIncreaseApproved));
    }

    [Fact]
    public async Task ApproveAsync_unknown_id_is_404()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(() => CreateService(admin.EntraObjectId, isAdmin: true)
            .ApproveAsync(999_999, new ReviewQuotaIncreaseRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task ApproveAsync_refuses_a_stored_quota_that_is_no_longer_a_configured_tier()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var ada = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        // Filed when 7M was a tier; the tier table has since changed under it.
        var request = await SeedRequestAsync(ada, ada, Period, requestedQuota: 7_000_000);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => CreateService(admin.EntraObjectId, isAdmin: true)
            .ApproveAsync(request.QuotaIncreaseRequestId, new ReviewQuotaIncreaseRequest(), CancellationToken.None));

        Assert.Contains("not a configured budget tier", exception.Message, StringComparison.Ordinal);
        var user = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == ada.UserId);
        Assert.Equal(TestGatewayTiers.StandardCap, user.MonthlyTokenQuota); // untouched
    }

    [Fact]
    public async Task ApproveAsync_for_a_deactivated_subject_is_409()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var ada = await SeedUserAsync("Gone", u =>
        {
            u.MonthlyTokenQuota = TestGatewayTiers.StandardCap;
            u.IsActive = false;
        });
        var request = await SeedRequestAsync(ada, ada, Period, requestedQuota: TestGatewayTiers.PowerCap);

        var exception = await Assert.ThrowsAsync<ConflictException>(() => CreateService(admin.EntraObjectId, isAdmin: true)
            .ApproveAsync(request.QuotaIncreaseRequestId, new ReviewQuotaIncreaseRequest(), CancellationToken.None));

        Assert.Contains("deactivated", exception.Message, StringComparison.Ordinal);
        Assert.Empty(_tierSync.Calls);
    }

    // -- RejectAsync --

    [Fact]
    public async Task RejectAsync_records_the_decision_and_notes_and_leaves_the_quota_alone()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var ada = await SeedUserAsync("Ada", u =>
        {
            u.MonthlyTokenQuota = TestGatewayTiers.StandardCap;
            u.ApimSubscriptionId = "sub-ada";
        });
        var request = await SeedRequestAsync(ada, ada, Period, requestedQuota: TestGatewayTiers.PowerCap);

        var result = await CreateService(admin.EntraObjectId, isAdmin: true).RejectAsync(
            request.QuotaIncreaseRequestId, new ReviewQuotaIncreaseRequest { ReviewNotes = "Use the shared batch account instead." }, CancellationToken.None);

        Assert.Equal(QuotaRequestStatusType.Rejected, result.StatusType);
        Assert.Equal(admin.UserId, result.ReviewedByUserId);
        Assert.Equal(Now, result.ReviewedDate);
        Assert.Equal("Use the shared batch account instead.", result.ReviewNotes);

        var user = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == ada.UserId);
        Assert.Equal(TestGatewayTiers.StandardCap, user.MonthlyTokenQuota);
        Assert.Empty(_tierSync.Calls);
        Assert.False(await Context.QuotaAllocations.AsNoTracking().AnyAsync()); // no re-resolution on a rejection

        var audit = Assert.Single(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.QuotaIncreaseRejected).ToListAsync());
        Assert.Equal(admin.UserId, audit.ActorUserId);
        Assert.Equal(request.QuotaIncreaseRequestId.ToString(CultureInfo.InvariantCulture), audit.TargetId);
    }

    [Fact]
    public async Task RejectAsync_on_an_already_decided_request_is_409()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Admin");
        var ada = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        var request = await SeedRequestAsync(ada, ada, Period, requestedQuota: TestGatewayTiers.PowerCap, status: QuotaRequestStatusType.Rejected);

        var exception = await Assert.ThrowsAsync<ConflictException>(() => CreateService(admin.EntraObjectId, isAdmin: true)
            .RejectAsync(request.QuotaIncreaseRequestId, new ReviewQuotaIncreaseRequest(), CancellationToken.None));

        Assert.Contains("already rejected", exception.Message, StringComparison.Ordinal);
    }

    // -- CancelPendingForUserAsync --

    [Fact]
    public async Task CancelPendingForUserAsync_closes_only_that_users_pending_requests_without_saving_or_auditing()
    {
        await SeedReferenceDataAsync();
        var ada = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        var bob = await SeedUserAsync("Bob", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        var pendingNow = await SeedRequestAsync(ada, ada, Period, requestedQuota: TestGatewayTiers.PowerCap);
        var pendingEarlier = await SeedRequestAsync(ada, ada, new BillingPeriod(2026, 8), requestedQuota: TestGatewayTiers.PowerCap);
        var approved = await SeedRequestAsync(ada, ada, new BillingPeriod(2026, 7), requestedQuota: TestGatewayTiers.PowerCap, status: QuotaRequestStatusType.Approved);
        var bobs = await SeedRequestAsync(bob, bob, Period, requestedQuota: TestGatewayTiers.PowerCap);
        var service = CreateService(ada.EntraObjectId);

        var cancelled = await service.CancelPendingForUserAsync(ada.UserId, "Account deprovisioned.", CancellationToken.None);

        Assert.Equal(2, cancelled);
        Assert.Empty(Context.ChangeTracker.Entries<AuditLog>()); // the caller audits, not us

        // Nothing is persisted until the orchestrating caller saves.
        await using (var probe = NewContext())
        {
            Assert.Equal(QuotaRequestStatusType.Pending, (await probe.QuotaIncreaseRequests.SingleAsync(r => r.QuotaIncreaseRequestId == pendingNow.QuotaIncreaseRequestId)).StatusType);
        }

        await Context.SaveChangesAsync();

        var rows = await Context.QuotaIncreaseRequests.AsNoTracking().ToDictionaryAsync(r => r.QuotaIncreaseRequestId);
        Assert.Equal(QuotaRequestStatusType.Rejected, rows[pendingNow.QuotaIncreaseRequestId].StatusType);
        Assert.Equal("Account deprovisioned.", rows[pendingNow.QuotaIncreaseRequestId].ReviewNotes);
        Assert.Null(rows[pendingNow.QuotaIncreaseRequestId].ReviewedByUserId); // no human decided it
        Assert.Equal(Now, rows[pendingNow.QuotaIncreaseRequestId].ReviewedDate);
        Assert.Equal(QuotaRequestStatusType.Rejected, rows[pendingEarlier.QuotaIncreaseRequestId].StatusType); // every period, not just the current one
        Assert.Equal(QuotaRequestStatusType.Approved, rows[approved.QuotaIncreaseRequestId].StatusType); // decided rows untouched
        Assert.Equal(QuotaRequestStatusType.Pending, rows[bobs.QuotaIncreaseRequestId].StatusType); // another user's untouched
    }

    [Fact]
    public async Task CancelPendingForUserAsync_is_idempotent()
    {
        await SeedReferenceDataAsync();
        var ada = await SeedUserAsync("Ada", u => u.MonthlyTokenQuota = TestGatewayTiers.StandardCap);
        _ = await SeedRequestAsync(ada, ada, Period, requestedQuota: TestGatewayTiers.PowerCap);
        var service = CreateService(ada.EntraObjectId);

        var first = await service.CancelPendingForUserAsync(ada.UserId, "Account deprovisioned.", CancellationToken.None);
        await Context.SaveChangesAsync();
        var second = await service.CancelPendingForUserAsync(ada.UserId, "Account deprovisioned.", CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
    }

    [Fact]
    public async Task CancelPendingForUserAsync_for_a_user_with_nothing_pending_returns_zero()
    {
        await SeedReferenceDataAsync();
        var ada = await SeedUserAsync("Ada");

        Assert.Equal(0, await CreateService(ada.EntraObjectId).CancelPendingForUserAsync(ada.UserId, "Account deprovisioned.", CancellationToken.None));
    }

    // -- Helpers --

    /// <summary>Real accessor + real audit + real resolution over this test's context, as DI would wire them per request.</summary>
    private QuotaRequestService CreateService(string oid, bool isAdmin = false)
    {
        List<Claim> claims = [new Claim(ClaimConstants.Oid, oid)];
        if (isAdmin)
        {
            claims.Add(new Claim(ClaimConstants.Roles, RoleNames.Admin));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth", nameType: ClaimConstants.Name, roleType: ClaimConstants.Roles);
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = new CurrentUserAccessor(new FixedHttpContextAccessor(httpContext), Context);
        var auditWriter = new AuditWriter(Context, _clock);

        return new QuotaRequestService(
            Context,
            new QuotaResolutionService(Context, TestGatewayTiers.Mapper(), _tierSync, NullLogger<QuotaResolutionService>.Instance),
            TestGatewayTiers.Mapper(),
            accessor,
            new AuditService(Context, auditWriter, accessor),
            _clock);
    }

    private async Task<User> SeedUserAsync(string displayName, Action<User>? configure = null)
    {
        var user = new User
        {
            EntraObjectId = Guid.NewGuid().ToString(),
            DisplayName = displayName,
            Email = $"{Guid.NewGuid():N}@contoso.test",
        };
        configure?.Invoke(user);
        Context.Users.Add(user);
        await Context.SaveChangesAsync();
        return user;
    }

    private async Task SeedAllocationAsync(User user, BillingPeriod period, long? allocated, long tokensUsed = 0)
    {
        Context.QuotaAllocations.Add(new QuotaAllocation
        {
            UserId = user.UserId,
            PeriodYear = period.Year,
            PeriodMonth = period.Month,
            AllocatedTokens = allocated,
            TokensUsed = tokensUsed,
            ResolvedLevelType = allocated is null ? QuotaLevelType.UserUnlimited : QuotaLevelType.UserOverride,
            TierProductId = allocated is null ? GatewayTiers.Unlimited : GatewayTiers.Standard,
        });
        await Context.SaveChangesAsync();
    }

    private async Task<QuotaIncreaseRequest> SeedRequestAsync(
        User subject,
        User requestedBy,
        BillingPeriod period,
        long? requestedQuota,
        QuotaRequestStatusType status = QuotaRequestStatusType.Pending,
        DateTimeOffset? createdDate = null)
    {
        var request = new QuotaIncreaseRequest
        {
            UserId = subject.UserId,
            RequestedByUserId = requestedBy.UserId,
            PeriodYear = period.Year,
            PeriodMonth = period.Month,
            CurrentQuota = subject.MonthlyTokenQuota,
            RequestedQuota = requestedQuota,
            Justification = Justification,
            StatusType = status,
        };
        Context.QuotaIncreaseRequests.Add(request);
        await Context.SaveChangesAsync();

        if (createdDate is { } stamped)
        {
            // The interceptor stamps CreatedDate from the clock; ordering tests need distinct values.
            request.CreatedDate = stamped;
            await Context.SaveChangesAsync();
        }

        return request;
    }

    /// <summary>A second context on the same in-memory database, for asserting what is (not yet) persisted.</summary>
    private AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(Context.Database.GetConnectionString()!)
            .Options);
}
