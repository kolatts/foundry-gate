using System.Globalization;
using Azure;
using FoundryGate.Core.Gateway;
using FoundryGate.Core.Quota;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Keys;
using FoundryGate.Domain.Quota;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoundryGate.Tests.Predeployment.Core.Quota;

/// <summary>
/// <see cref="ApimGatewayTierSync"/> (#118, moved to Core by #194) over the in-memory APIM: a quota
/// that resolves to a different tier must actually re-scope the developer's subscription, exactly
/// once, without touching their key — and the audit row it writes must be attributed to whoever the
/// host says is acting (an admin in the Api, nobody in the Functions jobs).
/// </summary>
public class ApimGatewayTierSyncTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    private readonly MutableTimeProvider _timeProvider = new(Now);
    private readonly FakeApimManagementClient _apim = new();

    [Fact]
    public async Task Moves_the_subscription_to_the_tier_product_without_changing_the_key()
    {
        var (sync, _, developer) = await CreateAsync();
        var subscriptionName = ApimSubscriptionNames.ForUser(developer.UserId);
        var keysBefore = _apim.KeysOf(subscriptionName);

        await sync.SyncAsync(developer, GatewayTiers.Power, CancellationToken.None);
        _ = await Context.SaveChangesAsync();

        Assert.Equal(GatewayTiers.Power, _apim.ProductOf(subscriptionName));
        Assert.Equal(keysBefore.PrimaryKey, _apim.KeysOf(subscriptionName).PrimaryKey);
        Assert.Equal(keysBefore.SecondaryKey, _apim.KeysOf(subscriptionName).SecondaryKey);
    }

    [Fact]
    public async Task Writes_exactly_one_tier_changed_row_attributed_to_the_hosts_actor()
    {
        var (sync, admin, developer) = await CreateAsync();

        await sync.SyncAsync(developer, GatewayTiers.Power, CancellationToken.None);

        // Added, not saved (#156 review): this runs inside a caller's unit of work, so the row joins
        // the caller's change tracker and commits with everything else.
        Assert.Contains(
            Context.ChangeTracker.Entries<AuditLog>(),
            entry => entry.Entity.Action == AuditActions.KeyTierChanged && entry.State == EntityState.Added);
        _ = await Context.SaveChangesAsync();

        var row = Assert.Single(await RowsAsync(developer));
        Assert.Equal(admin.UserId, row.ActorUserId);
        Assert.Contains($"\"before\":\"{GatewayTiers.Standard}\"", row.Details, StringComparison.Ordinal);
        Assert.Contains($"\"after\":\"{GatewayTiers.Power}\"", row.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_no_actor_the_row_is_system_attributed_which_is_how_the_Functions_reset_writes_it()
    {
        // The Functions host registers SystemGatewayTierSyncActor: a monthly reset is nobody's request,
        // so the row matches its own quota.monthly-reset row rather than blaming a developer (#194).
        var (_, _, developer) = await CreateAsync();
        var sync = Build(new SystemGatewayTierSyncActor());

        await sync.SyncAsync(developer, GatewayTiers.Power, CancellationToken.None);
        _ = await Context.SaveChangesAsync();

        var row = Assert.Single(await RowsAsync(developer));
        Assert.Null(row.ActorUserId);
    }

    [Fact]
    public async Task Is_idempotent_for_a_subscription_already_on_the_target_product()
    {
        var (sync, _, developer) = await CreateAsync();
        var subscriptionName = ApimSubscriptionNames.ForUser(developer.UserId);

        await sync.SyncAsync(developer, "STANDARD", CancellationToken.None);

        Assert.DoesNotContain(_apim.Calls, call => call.StartsWith($"UpdateScope:{subscriptionName}", StringComparison.Ordinal));
        Assert.DoesNotContain(
            Context.ChangeTracker.Entries<AuditLog>(),
            entry => entry.Entity.Action == AuditActions.KeyTierChanged);
    }

    [Fact]
    public async Task Skips_a_user_with_no_subscription_rather_than_asking_APIM_about_one()
    {
        var (sync, _, _) = await CreateAsync();
        var unprovisioned = await SeedUserAsync("No Key");
        var callsBefore = _apim.Calls.Count;

        await sync.SyncAsync(unprovisioned, GatewayTiers.Power, CancellationToken.None);

        Assert.Equal(callsBefore, _apim.Calls.Count);
    }

    [Fact]
    public async Task A_tier_the_gateway_does_not_have_is_refused_before_APIM_is_touched()
    {
        var (sync, _, developer) = await CreateAsync();
        var callsBefore = _apim.Calls.Count;

        _ = await Assert.ThrowsAnyAsync<ArgumentException>(() => sync.SyncAsync(developer, "gold", CancellationToken.None));

        Assert.Equal(callsBefore, _apim.Calls.Count);
    }

    [Fact]
    public async Task A_subscription_the_gateway_no_longer_has_is_a_conflict_not_an_outage()
    {
        var (sync, _, developer) = await CreateAsync();
        Assert.True(_apim.Remove(ApimSubscriptionNames.ForUser(developer.UserId)));

        var exception = await Assert.ThrowsAsync<ConflictException>(
            () => sync.SyncAsync(developer, GatewayTiers.Power, CancellationToken.None));

        Assert.IsType<ApimSubscriptionNotFoundException>(exception.InnerException);
        Assert.Empty(await RowsAsync(developer));
    }

    [Fact]
    public async Task An_ARM_fault_becomes_an_upstream_dependency_failure_and_writes_nothing()
    {
        // Without this the SDK's RequestFailedException escapes as a bare 500 on PUT /users/{id}/quota,
        // POST /users/{id}/activate and request approval — all three of which document 502 (#156 review).
        var (sync, _, developer) = await CreateAsync();
        _apim.ThrowOnUpdateScope = new RequestFailedException(429, "Too many requests.");

        var exception = await Assert.ThrowsAsync<UpstreamDependencyException>(
            () => sync.SyncAsync(developer, GatewayTiers.Power, CancellationToken.None));

        Assert.IsType<RequestFailedException>(exception.InnerException);
        Assert.Empty(await RowsAsync(developer));
    }

    [Fact]
    public async Task An_actor_the_host_refuses_stops_the_move_before_the_gateway_sees_it()
    {
        // The Api's actor throws 403 for a caller with no User row. That refusal has to land before the
        // subscription moves, or the gateway would be enforcing a tier nothing audited.
        var (_, _, developer) = await CreateAsync();
        var subscriptionName = ApimSubscriptionNames.ForUser(developer.UserId);
        var actor = new FixedGatewayTierSyncActor(null) { Throws = new UnauthorizedAccessException("Call GET /users/me first.") };

        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Build(actor).SyncAsync(developer, GatewayTiers.Power, CancellationToken.None));

        Assert.Equal(GatewayTiers.Standard, _apim.ProductOf(subscriptionName));
    }

    [Fact]
    public async Task Resolution_drives_the_move_end_to_end_when_a_users_quota_changes_tier()
    {
        var (sync, _, developer) = await CreateAsync();
        var subscriptionName = ApimSubscriptionNames.ForUser(developer.UserId);
        var resolution = new QuotaResolutionService(
            Context,
            TestGatewayTiers.Mapper(),
            sync,
            NullLogger<QuotaResolutionService>.Instance);

        // An admin pins the Power cap on the user; resolving the period must land the subscription on
        // the Power product before anything is saved.
        developer.MonthlyTokenQuota = TestGatewayTiers.PowerCap;
        var result = await resolution.ResolveAsync(developer.UserId, BillingPeriod.FromInstant(Now), CancellationToken.None);
        _ = await Context.SaveChangesAsync();

        Assert.True(result.TierSyncRequested);
        Assert.Equal(GatewayTiers.Power, result.Allocation.TierProductId);
        Assert.Equal(GatewayTiers.Power, _apim.ProductOf(subscriptionName));
    }

    /// <summary>A developer whose subscription sits on Standard, plus a sync acting as the seeded admin.</summary>
    private async Task<(ApimGatewayTierSync Sync, User Admin, User Developer)> CreateAsync()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Ada Admin");
        var developer = await SeedUserAsync("Dev One");

        var subscriptionName = ApimSubscriptionNames.ForUser(developer.UserId);
        _ = _apim.Seed(subscriptionName, GatewayTiers.Standard, $"FoundryGate {developer.Email}");
        developer.ApimSubscriptionId = _apim.GetSubscriptionResourceId(subscriptionName);
        _ = await Context.SaveChangesAsync();

        return (Build(new FixedGatewayTierSyncActor(admin)), admin, developer);
    }

    private ApimGatewayTierSync Build(IGatewayTierSyncActor actor) =>
        new(_apim, new AuditWriter(Context, _timeProvider), actor, NullLogger<ApimGatewayTierSync>.Instance);

    private Task<List<AuditLog>> RowsAsync(User developer)
    {
        var targetId = developer.UserId.ToString(CultureInfo.InvariantCulture);

        return Context.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditActions.KeyTierChanged && a.TargetId == targetId)
            .ToListAsync();
    }

    private async Task<User> SeedUserAsync(string displayName)
    {
        var user = new User
        {
            EntraObjectId = Guid.NewGuid().ToString(),
            DisplayName = displayName,
            Email = $"{Guid.NewGuid():N}@contoso.test",
        };
        Context.Users.Add(user);
        _ = await Context.SaveChangesAsync();
        return user;
    }
}
