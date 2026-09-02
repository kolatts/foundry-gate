using System.Globalization;
using System.Security.Claims;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Api.Services.Keys;
using FoundryGate.Api.Services.Quota;
using FoundryGate.Api.Services.Security;
using FoundryGate.Core.Quota;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Keys;
using FoundryGate.Domain.Quota;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Web;

namespace FoundryGate.Tests.Predeployment.Api.Services.Quota;

/// <summary>
/// <see cref="ApimGatewayTierSync"/> (#118) over the real <see cref="ApimKeyService"/> and the in-memory
/// APIM: a quota that resolves to a different tier must actually re-scope the developer's subscription,
/// exactly once, without touching their key or writing a second audit row.
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
    public async Task Writes_exactly_one_tier_changed_audit_row_and_never_a_second_one_of_its_own()
    {
        var (sync, admin, developer) = await CreateAsync();

        await sync.SyncAsync(developer, GatewayTiers.Power, CancellationToken.None);
        _ = await Context.SaveChangesAsync();

        var targetId = developer.UserId.ToString(CultureInfo.InvariantCulture);
        var rows = await Context.AuditLogs.AsNoTracking()
            .Where(a => a.Action == AuditActions.KeyTierChanged && a.TargetId == targetId)
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal(admin.UserId, row.ActorUserId);
        Assert.Contains($"\"before\":\"{GatewayTiers.Standard}\"", row.Details, StringComparison.Ordinal);
        Assert.Contains($"\"after\":\"{GatewayTiers.Power}\"", row.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Is_idempotent_for_a_subscription_already_on_the_target_product()
    {
        var (sync, _, developer) = await CreateAsync();
        var subscriptionName = ApimSubscriptionNames.ForUser(developer.UserId);

        await sync.SyncAsync(developer, GatewayTiers.Standard, CancellationToken.None);

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

    /// <summary>A provisioned developer on the Standard product, plus the sync wired over the real key service.</summary>
    private async Task<(ApimGatewayTierSync Sync, User Admin, User Developer)> CreateAsync()
    {
        await SeedReferenceDataAsync();
        var admin = await SeedUserAsync("Ada Admin");
        var developer = await SeedUserAsync("Dev One");

        var identity = new ClaimsIdentity(
            [new Claim(ClaimConstants.Oid, admin.EntraObjectId), new Claim(ClaimConstants.Roles, RoleNames.Admin)],
            "TestAuth",
            nameType: ClaimConstants.Name,
            roleType: ClaimConstants.Roles);
        var accessor = new CurrentUserAccessor(new FixedHttpContextAccessor(new DefaultHttpContext { User = new ClaimsPrincipal(identity) }), Context);
        var writer = new AuditWriter(Context, _timeProvider);
        var audit = new AuditService(Context, writer, accessor);
        var keys = new ApimKeyService(
            Context,
            _apim,
            new DataProtectionKeyProtector(new EphemeralDataProtectionProvider()),
            audit,
            writer,
            accessor,
            _timeProvider,
            NullLogger<ApimKeyService>.Instance);

        _ = await keys.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);

        return (new ApimGatewayTierSync(keys, NullLogger<ApimGatewayTierSync>.Instance), admin, developer);
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
