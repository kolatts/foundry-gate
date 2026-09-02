using System.Globalization;
using System.Text.Json;
using Azure;
using FoundryGate.Core.Entra;
using FoundryGate.Core.Requests;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Keys;
using FoundryGate.Domain.Requests;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoundryGate.Tests.Predeployment.Core.Entra;

/// <summary>
/// <see cref="DeprovisioningDepartureHandler"/> (#151): plan 21's deprovision Trigger B as the
/// Functions host runs it, over Core's own APIM client. The Api's version of the same pipeline is
/// <c>UserLifecycleServiceTests</c>; #214 tracks converging the two, and until it lands these
/// assertions are what keeps the second one honest — the same end state, the same audit shapes.
/// </summary>
public class DeprovisioningDepartureHandlerTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 2, 0, 0, TimeSpan.Zero);

    private readonly MutableTimeProvider _clock = new(Now);
    private readonly FakeApimManagementClient _apim = new();
    private readonly CapturingLoggerProvider _logs = new();

    [Fact]
    public async Task It_deletes_the_subscription_deactivates_hard_stops_and_closes_pending_requests()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserWithKeyAsync();
        await SeedAllocationAsync(user);
        var request = await SeedPendingRequestAsync(user);
        var subscriptionName = ApimSubscriptionNames.ForUser(user.UserId);

        await CreateHandler().HandleAsync(user, CancellationToken.None);

        Assert.False(_apim.Contains(subscriptionName));

        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == user.UserId);
        Assert.False(saved.IsActive);
        Assert.Empty(saved.ApimSubscriptionId);
        Assert.Empty(saved.ApimSubscriptionKey);
        Assert.Empty(saved.ApimSubscriptionKeyHint);
        Assert.Null(saved.ApimKeyIssuedDate);

        var allocation = await Context.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == user.UserId);
        Assert.True(allocation.IsHardStopped);

        var closed = await Context.QuotaIncreaseRequests.AsNoTracking().SingleAsync(r => r.QuotaIncreaseRequestId == request.QuotaIncreaseRequestId);
        Assert.Equal(QuotaRequestStatusType.Rejected, closed.StatusType);
        Assert.Null(closed.ReviewedByUserId);
        Assert.Equal(DepartureAudit.ReviewNote, closed.ReviewNotes);
    }

    [Fact]
    public async Task Both_audit_rows_are_system_attributed_and_carry_the_departure_reason()
    {
        // The directory's word, not any admin's — and the same shapes UserLifecycleService writes, so
        // the audit viewer cannot tell which host offboarded someone (it should not have to).
        await SeedReferenceDataAsync();
        var user = await SeedUserWithKeyAsync();
        await SeedAllocationAsync(user);
        var targetId = user.UserId.ToString(CultureInfo.InvariantCulture);

        await CreateHandler().HandleAsync(user, CancellationToken.None);

        var revocation = await Context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.KeyRevoked && a.TargetId == targetId);
        Assert.Null(revocation.ActorUserId);
        Assert.Equal(AuditTargetTypes.ApiKey, revocation.TargetType);
        var revocationDetails = JsonDocument.Parse(revocation.Details).RootElement;
        Assert.Equal(DepartureAudit.KeyRevocationReason, revocationDetails.GetProperty("reason").GetString());
        Assert.True(revocationDetails.GetProperty("existedInApim").GetBoolean());

        var deactivation = await Context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.UserDeactivated && a.TargetId == targetId);
        Assert.Null(deactivation.ActorUserId);
        var deactivationDetails = JsonDocument.Parse(deactivation.Details).RootElement;
        Assert.True(deactivationDetails.GetProperty("keyRevoked").GetBoolean());
        Assert.True(deactivationDetails.GetProperty("allocationHardStopped").GetBoolean());
    }

    [Fact]
    public async Task A_user_who_never_held_a_key_is_deactivated_without_reaching_the_gateway()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserAsync();

        await CreateHandler().HandleAsync(user, CancellationToken.None);

        Assert.Empty(_apim.Calls);
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == user.UserId);
        Assert.False(saved.IsActive);
        Assert.False(await Context.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditActions.KeyRevoked));
    }

    [Fact]
    public async Task An_already_inactive_user_is_a_no_op_so_a_second_run_costs_nothing()
    {
        // Someone deactivated between two nightly runs must not fail the run or write a second set of
        // rows; the sync counts them as neither departed nor touched.
        await SeedReferenceDataAsync();
        var user = await SeedUserWithKeyAsync();
        user.IsActive = false;
        _ = await Context.SaveChangesAsync();

        await CreateHandler().HandleAsync(user, CancellationToken.None);

        Assert.Empty(_apim.Calls);
        Assert.Empty(await Context.AuditLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_subscription_APIM_no_longer_has_still_clears_the_row_so_the_retry_converges()
    {
        await SeedReferenceDataAsync();
        var user = await SeedUserWithKeyAsync(seedInApim: false);

        await CreateHandler().HandleAsync(user, CancellationToken.None);

        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == user.UserId);
        Assert.False(saved.IsActive);
        Assert.Empty(saved.ApimSubscriptionId);

        var revocation = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.KeyRevoked);
        Assert.False(JsonDocument.Parse(revocation.Details).RootElement.GetProperty("existedInApim").GetBoolean());
    }

    [Fact]
    public async Task A_gateway_refusal_becomes_an_UpstreamDependencyException_and_changes_nothing()
    {
        // What the sync catches to count a failed departure and carry on with the rest of the run. The
        // user must be left exactly as they were, or tomorrow's retry would start from a half-state.
        await SeedReferenceDataAsync();
        var user = await SeedUserWithKeyAsync();
        _apim.ThrowOnDelete = new RequestFailedException(503, "APIM is having a day.");

        _ = await Assert.ThrowsAsync<UpstreamDependencyException>(
            () => CreateHandler().HandleAsync(user, CancellationToken.None));

        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == user.UserId);
        Assert.True(saved.IsActive);
        Assert.NotEmpty(saved.ApimSubscriptionId);
        Assert.Empty(await Context.AuditLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Once_the_subscription_is_gone_a_cancelled_token_no_longer_stops_the_record_of_it()
    {
        // CONVENTIONS.md's commit point: a delete has no undo, so a caller that hangs up must not turn
        // an accepted revocation into an unaudited one.
        await SeedReferenceDataAsync();
        var user = await SeedUserWithKeyAsync();
        await SeedAllocationAsync(user);
        using var cancelled = new CancellationTokenSource();
        _apim.AfterMutation = cancelled.Cancel;

        await CreateHandler().HandleAsync(user, cancelled.Token);

        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == user.UserId);
        Assert.False(saved.IsActive);
        Assert.Empty(saved.ApimSubscriptionId);
        Assert.Equal(2, await Context.AuditLogs.AsNoTracking().CountAsync());
    }

    // -- Helpers --

    private DeprovisioningDepartureHandler CreateHandler()
    {
        var audit = new AuditWriter(Context, _clock);

        return new DeprovisioningDepartureHandler(
            Context,
            _apim,
            new QuotaRequestExpiry(Context, audit, _clock, NullLogger<QuotaRequestExpiry>.Instance),
            audit,
            _clock,
            _logs.CreateLogger<DeprovisioningDepartureHandler>());
    }

    private async Task<User> SeedUserAsync()
    {
        var user = new User
        {
            EntraObjectId = Guid.NewGuid().ToString(),
            DisplayName = "Departed Developer",
            Email = $"{Guid.NewGuid():N}@contoso.test",
        };
        _ = Context.Users.Add(user);
        _ = await Context.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// A developer holding a key. <paramref name="seedInApim"/> <see langword="false"/> is the
    /// already-deleted case — the row still claims a subscription APIM no longer has, which is what a
    /// half-finished earlier run leaves behind.
    /// </summary>
    private async Task<User> SeedUserWithKeyAsync(bool seedInApim = true)
    {
        var user = await SeedUserAsync();
        var subscriptionName = ApimSubscriptionNames.ForUser(user.UserId);
        if (seedInApim)
        {
            _ = _apim.Seed(subscriptionName, GatewayTiers.Standard);
        }

        user.ApimSubscriptionId = _apim.GetSubscriptionResourceId(subscriptionName);
        user.ApimSubscriptionKey = "protected-ciphertext";
        user.ApimSubscriptionKeyHint = "1a2b";
        user.ApimKeyIssuedDate = Now;
        _ = await Context.SaveChangesAsync();
        return user;
    }

    private async Task SeedAllocationAsync(User user)
    {
        _ = Context.QuotaAllocations.Add(new QuotaAllocation
        {
            UserId = user.UserId,
            PeriodYear = Now.Year,
            PeriodMonth = Now.Month,
            AllocatedTokens = TestGatewayTiers.StandardCap,
            TierProductId = GatewayTiers.Standard,
        });
        _ = await Context.SaveChangesAsync();
    }

    private async Task<QuotaIncreaseRequest> SeedPendingRequestAsync(User user)
    {
        var request = new QuotaIncreaseRequest
        {
            UserId = user.UserId,
            RequestedByUserId = user.UserId,
            PeriodYear = Now.Year,
            PeriodMonth = Now.Month,
            RequestedQuota = TestGatewayTiers.PowerCap,
            Justification = "More tokens, please.",
        };
        _ = Context.QuotaIncreaseRequests.Add(request);
        _ = await Context.SaveChangesAsync();
        return request;
    }
}
