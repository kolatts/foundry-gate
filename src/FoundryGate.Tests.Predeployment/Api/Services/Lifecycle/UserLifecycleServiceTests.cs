using System.Globalization;
using System.Security.Claims;
using Azure;
using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Entra;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Api.Services.Keys;
using FoundryGate.Api.Services.Lifecycle;
using FoundryGate.Api.Services.Requests;
using FoundryGate.Api.Services.Security;
using FoundryGate.Core.Quota;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Keys;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Requests;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Web;

namespace FoundryGate.Tests.Predeployment.Api.Services.Lifecycle;

/// <summary>
/// Plan 21's two pipelines end to end over a real SQLite context: the real
/// <see cref="QuotaResolutionService"/>, the real <see cref="ApimKeyService"/> over
/// <see cref="FakeApimManagementClient"/>, the real audit path, and a real
/// <see cref="CurrentUserAccessor"/>. Nothing here is stubbed except the two external systems (APIM
/// and the directory), which is the point — the guarantees under test are about what commits together.
/// </summary>
public class UserLifecycleServiceTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly BillingPeriod Period = BillingPeriod.FromInstant(Now);

    private readonly MutableTimeProvider _timeProvider = new(Now);
    private readonly FakeApimManagementClient _apim = new();
    private readonly FakeEntraDirectoryClient _directory = new();
    private readonly AppSettings _settings = new();

    // -- Provision: first login (Trigger A) --------------------------------------------------------

    [Fact]
    public async Task First_login_creates_the_user_the_allocation_and_the_subscription_in_one_unit_of_work()
    {
        await SeedReferenceDataAsync();
        var oid = Guid.NewGuid().ToString();
        var service = CreateService(oid, name: "Dana Developer", email: "dana@contoso.test");

        var user = await service.ProvisionAsync(ProvisionTrigger.FirstLogin, ProvisionContext.FirstLogin(), CancellationToken.None);

        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.EntraObjectId == oid);
        Assert.Equal("Dana Developer", saved.DisplayName);
        Assert.Equal("dana@contoso.test", saved.Email);
        Assert.True(saved.IsActive);
        Assert.Equal(Now, saved.LastSyncedDate);

        // The allocation is resolved for the current period, at the system default, on a real tier.
        var allocation = await Context.QuotaAllocations.AsNoTracking()
            .SingleAsync(a => a.UserId == user.UserId && a.PeriodYear == Period.Year && a.PeriodMonth == Period.Month);
        Assert.Equal(QuotaLevelType.SystemDefault, allocation.ResolvedLevelType);
        Assert.Contains(allocation.TierProductId, GatewayTiers.All, StringComparer.Ordinal);
        Assert.False(allocation.IsHardStopped);

        // The subscription exists in APIM, under the tier the allocation resolved to, and the key is
        // stored encrypted on the row.
        var subscriptionName = ApimSubscriptionNames.ForUser(user.UserId);
        Assert.True(_apim.Contains(subscriptionName));
        Assert.Equal(allocation.TierProductId, _apim.ProductOf(subscriptionName));
        Assert.StartsWith("dp1:", saved.ApimSubscriptionKey, StringComparison.Ordinal);
        Assert.NotEmpty(saved.ApimSubscriptionId);

        // Audited to the user themselves — the actor of a first login is the person logging in.
        var audit = await SingleAuditAsync(AuditActions.UserProvisioned, AuditTargetTypes.User, user.UserId);
        Assert.Equal(user.UserId, audit.ActorUserId);
        Assert.Contains("\"trigger\":\"FirstLogin\"", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"keyProvisioned\":true", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"directoryEnriched\":false", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_login_enriches_from_the_directory_when_Entra_is_enabled()
    {
        await SeedReferenceDataAsync();
        var oid = Guid.NewGuid().ToString();
        _settings.Entra.Enabled = true;
        _directory.AssignedUsers.Add(new EntraUser(oid, "Directory Name", "directory@contoso.test", "E4711"));
        var service = CreateService(oid, name: "Stale Token Name", email: "stale@contoso.test");

        var user = await service.ProvisionAsync(ProvisionTrigger.FirstLogin, ProvisionContext.FirstLogin(), CancellationToken.None);

        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == user.UserId);
        Assert.Equal("Directory Name", saved.DisplayName);
        Assert.Equal("directory@contoso.test", saved.Email);
        Assert.Equal("E4711", saved.EmployeeId);

        var audit = await SingleAuditAsync(AuditActions.UserProvisioned, AuditTargetTypes.User, user.UserId);
        Assert.Contains("\"directoryEnriched\":true", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_login_falls_back_to_token_claims_when_the_directory_has_no_such_user()
    {
        await SeedReferenceDataAsync();
        var oid = Guid.NewGuid().ToString();
        _settings.Entra.Enabled = true; // directory is on, but knows nobody
        var service = CreateService(oid, name: "Claims Only", email: "claims@contoso.test");

        var user = await service.ProvisionAsync(ProvisionTrigger.FirstLogin, ProvisionContext.FirstLogin(), CancellationToken.None);

        Assert.Equal("Claims Only", user.DisplayName);
        Assert.Equal("claims@contoso.test", user.Email);
        Assert.Null(user.EmployeeId);
    }

    [Fact]
    public async Task First_login_never_touches_the_directory_while_Entra_is_disabled()
    {
        await SeedReferenceDataAsync();
        var oid = Guid.NewGuid().ToString();
        var service = CreateService(oid, name: "Local Dev", email: "local@contoso.test");

        _ = await service.ProvisionAsync(ProvisionTrigger.FirstLogin, ProvisionContext.FirstLogin(), CancellationToken.None);

        // DisabledEntraDirectoryClient would have thrown; the fake simply must not be consulted.
        Assert.Empty(_directory.AssignedUsers);
    }

    [Fact]
    public async Task First_login_that_APIM_refuses_leaves_no_User_row_and_surfaces_502()
    {
        await SeedReferenceDataAsync();
        var oid = Guid.NewGuid().ToString();
        _apim.ThrowOnCreate = new RequestFailedException(403, "The client does not have authorization to perform action.");
        var service = CreateService(oid, name: "Never Created", email: "never@contoso.test");

        var exception = await Assert.ThrowsAsync<UpstreamDependencyException>(() =>
            service.ProvisionAsync(ProvisionTrigger.FirstLogin, ProvisionContext.FirstLogin(), CancellationToken.None));

        Assert.Contains("nothing was saved", exception.Message, StringComparison.Ordinal);

        // The whole pipeline rolled back: no user, no allocation, no audit row. This is what proves the
        // key service joined the orchestrator's transaction rather than committing its own claim.
        Assert.Equal(0, await Context.Users.AsNoTracking().CountAsync(u => u.EntraObjectId == oid));
        Assert.Equal(0, await Context.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditActions.UserProvisioned));
    }

    [Fact]
    public async Task First_login_on_a_host_without_APIM_is_503_and_creates_nothing()
    {
        await SeedReferenceDataAsync();
        var oid = Guid.NewGuid().ToString();
        var service = CreateService(oid, apim: new UnconfiguredApimManagementClient());

        var exception = await Assert.ThrowsAsync<FeatureNotConfiguredException>(() =>
            service.ProvisionAsync(ProvisionTrigger.FirstLogin, ProvisionContext.FirstLogin(), CancellationToken.None));

        Assert.Contains("Gateway:ApimName", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, await Context.Users.AsNoTracking().CountAsync(u => u.EntraObjectId == oid));
    }

    [Fact]
    public async Task First_login_for_an_oid_that_already_has_a_row_is_a_conflict()
    {
        var existing = await SeedUserAsync("Already There");
        var service = CreateService(existing.EntraObjectId);

        _ = await Assert.ThrowsAsync<ConflictException>(() =>
            service.ProvisionAsync(ProvisionTrigger.FirstLogin, ProvisionContext.FirstLogin(), CancellationToken.None));
    }

    // -- Provision: admin provision (Trigger B) ----------------------------------------------------

    [Fact]
    public async Task Admin_provision_mints_a_key_for_an_existing_user_without_creating_a_row()
    {
        var admin = await SeedUserAsync("Ada Admin");
        var developer = await SeedUserAsync("Dev One");
        var service = CreateService(admin.EntraObjectId);

        var user = await service.ProvisionAsync(ProvisionTrigger.AdminProvision, ProvisionContext.ForUser(developer.UserId), CancellationToken.None);

        Assert.Equal(developer.UserId, user.UserId);
        Assert.True(_apim.Contains(ApimSubscriptionNames.ForUser(developer.UserId)));

        var audit = await SingleAuditAsync(AuditActions.UserProvisioned, AuditTargetTypes.User, developer.UserId);
        Assert.Equal(admin.UserId, audit.ActorUserId);
    }

    [Fact]
    public async Task Admin_provision_refuses_a_deactivated_user_and_one_that_already_holds_a_key()
    {
        var admin = await SeedUserAsync("Ada Admin");
        var inactive = await SeedUserAsync("Gone", isActive: false);
        var keyed = await SeedUserAsync("Keyed");
        var service = CreateService(admin.EntraObjectId);
        _ = await service.ProvisionAsync(ProvisionTrigger.AdminProvision, ProvisionContext.ForUser(keyed.UserId), CancellationToken.None);

        var deactivatedConflict = await Assert.ThrowsAsync<ConflictException>(() =>
            service.ProvisionAsync(ProvisionTrigger.AdminProvision, ProvisionContext.ForUser(inactive.UserId), CancellationToken.None));
        Assert.Contains("Re-activate", deactivatedConflict.Message, StringComparison.Ordinal);

        _ = await Assert.ThrowsAsync<ConflictException>(() =>
            service.ProvisionAsync(ProvisionTrigger.AdminProvision, ProvisionContext.ForUser(keyed.UserId), CancellationToken.None));
    }

    [Fact]
    public async Task Provision_for_an_unknown_user_is_404_and_without_a_user_id_is_400()
    {
        var admin = await SeedUserAsync("Ada Admin");
        var service = CreateService(admin.EntraObjectId);

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.ProvisionAsync(ProvisionTrigger.AdminProvision, ProvisionContext.ForUser(987654), CancellationToken.None));

        _ = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ProvisionAsync(ProvisionTrigger.Reactivate, ProvisionContext.FirstLogin(), CancellationToken.None));
    }

    // -- Provision: reactivation (Trigger C) and orphan adoption (#66) -----------------------------

    [Fact]
    public async Task Reactivation_reuses_an_orphan_subscription_instead_of_creating_a_second_one()
    {
        var admin = await SeedUserAsync("Ada Admin");
        var developer = await SeedUserAsync("Returning Dev", isActive: false);
        var subscriptionName = ApimSubscriptionNames.ForUser(developer.UserId);

        // The orphan a failed deprovision left behind: still on APIM, unknown to the database.
        var orphanKeys = _apim.Seed(subscriptionName, GatewayTiers.Power);
        var service = CreateService(admin.EntraObjectId);

        var user = await service.ProvisionAsync(ProvisionTrigger.Reactivate, ProvisionContext.ForUser(developer.UserId), CancellationToken.None);

        Assert.True(user.IsActive);
        Assert.DoesNotContain(_apim.Calls, call => call.StartsWith($"CreateOrUpdate:{subscriptionName}", StringComparison.Ordinal));
        Assert.Contains($"Get:{subscriptionName}", _apim.Calls);

        // Adopted, re-scoped to the tier the user actually resolves to, and both keys regenerated so
        // whatever the orphan held is dead.
        var allocation = await Context.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == developer.UserId);
        Assert.Equal(allocation.TierProductId, _apim.ProductOf(subscriptionName));
        Assert.NotEqual(orphanKeys.PrimaryKey, _apim.KeysOf(subscriptionName).PrimaryKey);
        Assert.NotEqual(orphanKeys.SecondaryKey, _apim.KeysOf(subscriptionName).SecondaryKey);

        var audit = await SingleAuditAsync(AuditActions.UserActivated, AuditTargetTypes.User, developer.UserId);
        Assert.Equal(admin.UserId, audit.ActorUserId);
        Assert.Contains("\"trigger\":\"Reactivate\"", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reactivating_an_active_user_is_a_conflict()
    {
        var admin = await SeedUserAsync("Ada Admin");
        var developer = await SeedUserAsync("Already Active");
        var service = CreateService(admin.EntraObjectId);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            service.ProvisionAsync(ProvisionTrigger.Reactivate, ProvisionContext.ForUser(developer.UserId), CancellationToken.None));

        Assert.Contains("already active", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reactivation_keeps_a_key_the_user_still_holds_rather_than_re_minting_it()
    {
        var admin = await SeedUserAsync("Ada Admin");
        var developer = await SeedUserAsync("Key Survivor");
        var service = CreateService(admin.EntraObjectId);
        _ = await service.ProvisionAsync(ProvisionTrigger.AdminProvision, ProvisionContext.ForUser(developer.UserId), CancellationToken.None);
        var subscriptionName = ApimSubscriptionNames.ForUser(developer.UserId);
        var keyBefore = _apim.KeysOf(subscriptionName).PrimaryKey;

        // Deactivated by hand (not through the pipeline), so the key fields survive.
        developer.IsActive = false;
        _ = await Context.SaveChangesAsync();

        _ = await service.ProvisionAsync(ProvisionTrigger.Reactivate, ProvisionContext.ForUser(developer.UserId), CancellationToken.None);

        Assert.Equal(keyBefore, _apim.KeysOf(subscriptionName).PrimaryKey);
        var audit = await SingleAuditAsync(AuditActions.UserActivated, AuditTargetTypes.User, developer.UserId);
        Assert.Contains("\"keyProvisioned\":false", audit.Details, StringComparison.Ordinal);
    }

    // -- Deprovision --------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_deactivation_deletes_the_subscription_hard_stops_the_allocation_and_rejects_pending_requests()
    {
        var admin = await SeedUserAsync("Ada Admin");
        var developer = await SeedUserAsync("Leaver");
        var service = CreateService(admin.EntraObjectId);
        _ = await service.ProvisionAsync(ProvisionTrigger.AdminProvision, ProvisionContext.ForUser(developer.UserId), CancellationToken.None);
        var subscriptionName = ApimSubscriptionNames.ForUser(developer.UserId);
        var pending = await SeedPendingRequestAsync(developer);
        var otherUsersPending = await SeedPendingRequestAsync(admin);

        await service.DeprovisionAsync(DeprovisionTrigger.AdminDeactivation, developer.UserId, CancellationToken.None);

        Assert.False(_apim.Contains(subscriptionName));

        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId);
        Assert.False(saved.IsActive);
        Assert.Empty(saved.ApimSubscriptionId);
        Assert.Empty(saved.ApimSubscriptionKey);
        Assert.Empty(saved.ApimSubscriptionKeyHint);
        Assert.Null(saved.ApimKeyIssuedDate);

        var allocation = await Context.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == developer.UserId);
        Assert.True(allocation.IsHardStopped);

        var rejected = await Context.QuotaIncreaseRequests.AsNoTracking().SingleAsync(r => r.QuotaIncreaseRequestId == pending.QuotaIncreaseRequestId);
        Assert.Equal(QuotaRequestStatusType.Rejected, rejected.StatusType);
        Assert.Equal("User deactivated", rejected.ReviewNotes);
        Assert.Null(rejected.ReviewedByUserId);
        Assert.Equal(Now, rejected.ReviewedDate);

        // Somebody else's pending request is untouched.
        var untouched = await Context.QuotaIncreaseRequests.AsNoTracking().SingleAsync(r => r.QuotaIncreaseRequestId == otherUsersPending.QuotaIncreaseRequestId);
        Assert.Equal(QuotaRequestStatusType.Pending, untouched.StatusType);

        var deactivation = await SingleAuditAsync(AuditActions.UserDeactivated, AuditTargetTypes.User, developer.UserId);
        Assert.Equal(admin.UserId, deactivation.ActorUserId);
        Assert.Contains("\"cancelledRequestCount\":1", deactivation.Details, StringComparison.Ordinal);
        Assert.Contains("\"allocationHardStopped\":true", deactivation.Details, StringComparison.Ordinal);

        var revocation = await SingleAuditAsync(AuditActions.KeyRevoked, AuditTargetTypes.ApiKey, developer.UserId);
        Assert.Equal(admin.UserId, revocation.ActorUserId);
    }

    [Fact]
    public async Task Deactivating_a_user_with_no_key_and_no_allocation_still_deactivates_them()
    {
        var admin = await SeedUserAsync("Ada Admin");
        var developer = await SeedUserAsync("Never Logged In");
        var service = CreateService(admin.EntraObjectId);

        await service.DeprovisionAsync(DeprovisionTrigger.AdminDeactivation, developer.UserId, CancellationToken.None);

        Assert.False((await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId)).IsActive);
        var audit = await SingleAuditAsync(AuditActions.UserDeactivated, AuditTargetTypes.User, developer.UserId);
        Assert.Contains("\"keyRevoked\":false", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"allocationHardStopped\":false", audit.Details, StringComparison.Ordinal);
        Assert.Equal(0, await Context.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditActions.KeyRevoked));
    }

    [Fact]
    public async Task Deactivating_an_already_deactivated_user_is_a_conflict_for_an_admin()
    {
        var admin = await SeedUserAsync("Ada Admin");
        var developer = await SeedUserAsync("Gone", isActive: false);
        var service = CreateService(admin.EntraObjectId);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            service.DeprovisionAsync(DeprovisionTrigger.AdminDeactivation, developer.UserId, CancellationToken.None));

        Assert.Contains("already deactivated", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Entra_departure_audits_as_the_system_and_is_idempotent()
    {
        var developer = await SeedUserAsync("Departed");
        var callerless = CreateCallerlessService();
        var admin = await SeedUserAsync("Ada Admin");
        _ = await CreateService(admin.EntraObjectId)
            .ProvisionAsync(ProvisionTrigger.AdminProvision, ProvisionContext.ForUser(developer.UserId), CancellationToken.None);

        await callerless.DeprovisionAsync(DeprovisionTrigger.EntraDeparture, developer.UserId, CancellationToken.None);

        Assert.False(_apim.Contains(ApimSubscriptionNames.ForUser(developer.UserId)));
        var deactivation = await SingleAuditAsync(AuditActions.UserDeactivated, AuditTargetTypes.User, developer.UserId);
        Assert.Null(deactivation.ActorUserId);
        Assert.Contains("\"trigger\":\"EntraDeparture\"", deactivation.Details, StringComparison.Ordinal);

        var revocation = await SingleAuditAsync(AuditActions.KeyRevoked, AuditTargetTypes.ApiKey, developer.UserId);
        Assert.Null(revocation.ActorUserId);
        Assert.Contains("entra-departure", revocation.Details, StringComparison.Ordinal);

        // A second run over the same (now inactive) user changes nothing and writes nothing.
        var auditCountBefore = await Context.AuditLogs.AsNoTracking().CountAsync();
        await callerless.DeprovisionAsync(DeprovisionTrigger.EntraDeparture, developer.UserId, CancellationToken.None);
        Assert.Equal(auditCountBefore, await Context.AuditLogs.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Deactivation_that_APIM_refuses_is_502_and_leaves_the_user_active()
    {
        var admin = await SeedUserAsync("Ada Admin");
        var developer = await SeedUserAsync("Stays Active");
        var service = CreateService(admin.EntraObjectId);
        _ = await service.ProvisionAsync(ProvisionTrigger.AdminProvision, ProvisionContext.ForUser(developer.UserId), CancellationToken.None);
        _apim.ThrowOnDelete = new RequestFailedException(429, "Too many requests.");

        var exception = await Assert.ThrowsAsync<UpstreamDependencyException>(() =>
            service.DeprovisionAsync(DeprovisionTrigger.AdminDeactivation, developer.UserId, CancellationToken.None));

        Assert.Contains("have not been deactivated", exception.Message, StringComparison.Ordinal);

        Context.ChangeTracker.Clear();
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId);
        Assert.True(saved.IsActive);
        Assert.NotEmpty(saved.ApimSubscriptionId);
        Assert.Equal(0, await Context.AuditLogs.AsNoTracking().CountAsync(a => a.Action == AuditActions.UserDeactivated));
    }

    [Fact]
    public async Task Deprovisioning_an_unknown_user_is_404()
    {
        var admin = await SeedUserAsync("Ada Admin");
        var service = CreateService(admin.EntraObjectId);

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.DeprovisionAsync(DeprovisionTrigger.AdminDeactivation, 987654, CancellationToken.None));
    }

    // -- Round trip ---------------------------------------------------------------------------------

    [Fact]
    public async Task Deactivate_then_reactivate_leaves_the_user_active_with_a_fresh_key_and_a_live_allocation()
    {
        var admin = await SeedUserAsync("Ada Admin");
        var developer = await SeedUserAsync("Round Trip");
        var service = CreateService(admin.EntraObjectId);
        _ = await service.ProvisionAsync(ProvisionTrigger.AdminProvision, ProvisionContext.ForUser(developer.UserId), CancellationToken.None);
        var subscriptionName = ApimSubscriptionNames.ForUser(developer.UserId);

        await service.DeprovisionAsync(DeprovisionTrigger.AdminDeactivation, developer.UserId, CancellationToken.None);
        Assert.False(_apim.Contains(subscriptionName));

        _ = await service.ProvisionAsync(ProvisionTrigger.Reactivate, ProvisionContext.ForUser(developer.UserId), CancellationToken.None);

        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId);
        Assert.True(saved.IsActive);
        Assert.NotEmpty(saved.ApimSubscriptionId);
        Assert.True(_apim.Contains(subscriptionName));

        // Re-activation lifts the hard stop its own deactivation set (#156 review); without that, a
        // deactivate-by-mistake would leave the developer showing hard-stopped for the rest of the month.
        var allocation = await Context.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == developer.UserId);
        Assert.False(allocation.IsHardStopped);
    }

    // -- Commit point: nothing an accepted gateway change did may be lost to a cancelled token -------

    [Fact]
    public async Task A_client_that_disconnects_the_instant_APIM_deletes_the_subscription_still_gets_a_full_deactivation()
    {
        // The reviewer's probe (#156 Major 1). Before the fix this left the subscription gone, the row
        // still IsActive = true with a live-looking key ciphertext, and *zero* audit rows.
        var admin = await SeedUserAsync("Ada Admin");
        var developer = await SeedUserAsync("Disconnecting Client");
        var service = CreateService(admin.EntraObjectId);
        _ = await service.ProvisionAsync(ProvisionTrigger.AdminProvision, ProvisionContext.ForUser(developer.UserId), CancellationToken.None);
        var subscriptionName = ApimSubscriptionNames.ForUser(developer.UserId);
        _ = await SeedPendingRequestAsync(developer);

        using var cts = new CancellationTokenSource();
        _apim.AfterMutation = cts.Cancel;

        await service.DeprovisionAsync(DeprovisionTrigger.AdminDeactivation, developer.UserId, cts.Token);

        _apim.AfterMutation = null;
        Assert.True(cts.IsCancellationRequested);
        Assert.False(_apim.Contains(subscriptionName));

        Context.ChangeTracker.Clear();
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId);
        Assert.False(saved.IsActive);
        Assert.Empty(saved.ApimSubscriptionId);
        Assert.Empty(saved.ApimSubscriptionKey);

        _ = await SingleAuditAsync(AuditActions.UserDeactivated, AuditTargetTypes.User, developer.UserId);
        _ = await SingleAuditAsync(AuditActions.KeyRevoked, AuditTargetTypes.ApiKey, developer.UserId);

        var allocation = await Context.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == developer.UserId);
        Assert.True(allocation.IsHardStopped);
        Assert.Equal(QuotaRequestStatusType.Rejected, (await Context.QuotaIncreaseRequests.AsNoTracking().SingleAsync(r => r.UserId == developer.UserId)).StatusType);
    }

    [Fact]
    public async Task A_client_that_disconnects_the_instant_APIM_mints_the_key_still_gets_a_saved_user()
    {
        // Same probe on the other pipeline: an abandoned first login must not leave an orphan
        // subscription plus an unaudited, un-keyed user row.
        await SeedReferenceDataAsync();
        var oid = Guid.NewGuid().ToString();
        var service = CreateService(oid, name: "Hangs Up", email: "hangsup@contoso.test");

        using var cts = new CancellationTokenSource();
        _apim.AfterMutation = cts.Cancel;

        var user = await service.ProvisionAsync(ProvisionTrigger.FirstLogin, ProvisionContext.FirstLogin(), cts.Token);

        _apim.AfterMutation = null;
        Assert.True(cts.IsCancellationRequested);

        Context.ChangeTracker.Clear();
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == user.UserId);
        Assert.NotEmpty(saved.ApimSubscriptionId);
        Assert.NotEmpty(saved.ApimSubscriptionKey);
        _ = await SingleAuditAsync(AuditActions.UserProvisioned, AuditTargetTypes.User, user.UserId);
        _ = await SingleAuditAsync(AuditActions.KeyProvisioned, AuditTargetTypes.ApiKey, user.UserId);
        Assert.True(_apim.Contains(ApimSubscriptionNames.ForUser(user.UserId)));
    }

    [Fact]
    public async Task A_deactivation_whose_database_half_fails_leaves_a_recoverable_state_not_a_silent_one()
    {
        // Plan 21's documented residue: the APIM delete cannot be undone, so it runs first and outside
        // the transaction. If the database steps then fail, the key is already revoked *and audited*,
        // and re-running the deactivation is idempotent.
        var admin = await SeedUserAsync("Ada Admin");
        var developer = await SeedUserAsync("Half Failed");
        var subscriptionName = ApimSubscriptionNames.ForUser(developer.UserId);
        _ = await CreateService(admin.EntraObjectId)
            .ProvisionAsync(ProvisionTrigger.AdminProvision, ProvisionContext.ForUser(developer.UserId), CancellationToken.None);

        var failing = CreateService(admin.EntraObjectId, wrapAudit: inner => new FailingAuditService(inner)
        {
            FailOn = action => action == AuditActions.UserDeactivated,
        });

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failing.DeprovisionAsync(DeprovisionTrigger.AdminDeactivation, developer.UserId, CancellationToken.None));

        // Step 1 committed on its own: subscription gone, key fields cleared, key.revoked recorded.
        Assert.False(_apim.Contains(subscriptionName));
        Context.ChangeTracker.Clear();
        var midway = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId);
        Assert.Empty(midway.ApimSubscriptionId);
        Assert.True(midway.IsActive); // the un-recorded half
        _ = await SingleAuditAsync(AuditActions.KeyRevoked, AuditTargetTypes.ApiKey, developer.UserId);

        // And the retry completes it, because revocation tolerates a subscription that is already gone.
        await CreateService(admin.EntraObjectId).DeprovisionAsync(DeprovisionTrigger.AdminDeactivation, developer.UserId, CancellationToken.None);

        Context.ChangeTracker.Clear();
        Assert.False((await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId)).IsActive);
        _ = await SingleAuditAsync(AuditActions.UserDeactivated, AuditTargetTypes.User, developer.UserId);
    }

    // -- Harness ------------------------------------------------------------------------------------

    private UserLifecycleService CreateService(
        string callerOid,
        string? name = null,
        string? email = null,
        IApimManagementClient? apim = null,
        Func<IAuditService, IAuditService>? wrapAudit = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimConstants.Oid, callerOid),
            new(ClaimConstants.Roles, RoleNames.Admin),
        };
        if (name is not null)
        {
            claims.Add(new Claim(ClaimConstants.Name, name));
        }

        if (email is not null)
        {
            claims.Add(new Claim(ClaimConstants.PreferredUserName, email));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth", nameType: ClaimConstants.Name, roleType: ClaimConstants.Roles);
        var accessor = new CurrentUserAccessor(new FixedHttpContextAccessor(new DefaultHttpContext { User = new ClaimsPrincipal(identity) }), Context);
        return Build(accessor, apim ?? _apim, wrapAudit);
    }

    /// <summary>The shape a background job builds: no HTTP caller at all (plan 21 deprovision Trigger B).</summary>
    private UserLifecycleService CreateCallerlessService() =>
        Build(new CurrentUserAccessor(new FixedHttpContextAccessor(null), Context), _apim, wrapAudit: null);

    private UserLifecycleService Build(ICurrentUserAccessor accessor, IApimManagementClient apim, Func<IAuditService, IAuditService>? wrapAudit)
    {
        var writer = new AuditWriter(Context, _timeProvider);
        IAuditService audit = new AuditService(Context, writer, accessor);
        audit = wrapAudit?.Invoke(audit) ?? audit;
        var keys = new ApimKeyService(
            Context,
            apim,
            new DataProtectionKeyProtector(new EphemeralDataProtectionProvider()),
            audit,
            writer,
            accessor,
            _timeProvider,
            NullLogger<ApimKeyService>.Instance);
        var tierMapper = TestGatewayTiers.Mapper();
        var quotaResolution = new QuotaResolutionService(
            Context,
            tierMapper,
            new NullGatewayTierSync(NullLogger<NullGatewayTierSync>.Instance),
            NullLogger<QuotaResolutionService>.Instance);

        // The real request service: deactivation delegates its pending-request cancellation to it
        // (#148's CancelPendingForUserAsync), so a stub would prove nothing about what commits.
        var quotaRequests = new QuotaRequestService(Context, quotaResolution, tierMapper, accessor, audit, _timeProvider);

        return new UserLifecycleService(
            Context,
            quotaResolution,
            quotaRequests,
            keys,
            audit,
            writer,
            accessor,
            _directory,
            _settings,
            _timeProvider,
            NullLogger<UserLifecycleService>.Instance);
    }

    private async Task<User> SeedUserAsync(string displayName, bool isActive = true)
    {
        await SeedReferenceDataAsync();

        var user = new User
        {
            EntraObjectId = Guid.NewGuid().ToString(),
            DisplayName = displayName,
            Email = $"{Guid.NewGuid():N}@contoso.test",
            IsActive = isActive,
        };
        Context.Users.Add(user);
        _ = await Context.SaveChangesAsync();
        return user;
    }

    private async Task<QuotaIncreaseRequest> SeedPendingRequestAsync(User user)
    {
        var request = new QuotaIncreaseRequest
        {
            UserId = user.UserId,
            RequestedByUserId = user.UserId,
            PeriodYear = Period.Year,
            PeriodMonth = Period.Month,
            RequestedQuota = TestGatewayTiers.PowerCap,
            Justification = "More tokens please",
        };
        Context.QuotaIncreaseRequests.Add(request);
        _ = await Context.SaveChangesAsync();
        return request;
    }

    private async Task<AuditLog> SingleAuditAsync(string action, string targetType, int userId)
    {
        var targetId = userId.ToString(CultureInfo.InvariantCulture);
        return await Context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == action && a.TargetType == targetType && a.TargetId == targetId);
    }
}
