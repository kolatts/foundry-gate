using System.Security.Claims;
using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Entra;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Api.Services.Keys;
using FoundryGate.Api.Services.Lifecycle;
using FoundryGate.Api.Services.Quota;
using FoundryGate.Api.Services.Security;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Keys;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Web;

namespace FoundryGate.Tests.Predeployment.Api.Services.Entra;

/// <summary>
/// The reconciliation contract of <see cref="EntraUserSyncService"/> (spec &#167;7.2, #40) against an
/// in-memory directory: add / update / deactivate semantics, idempotency, paging volume, the
/// no-reactivation and no-delete rules, the single audit row, and the atomicity of the one save.
/// Wired with the <em>real</em> <see cref="CurrentUserAccessor"/> + <see cref="AuditService"/> so the
/// caller-attribution path is the production one.
/// </summary>
public class EntraUserSyncServiceTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MutableTimeProvider _timeProvider = new(Now);
    private readonly FakeEntraDirectoryClient _directory = new();

    /// <summary>The in-memory APIM the departure path's deprovision deletes subscriptions from.</summary>
    protected FakeApimManagementClient Apim { get; } = new();

    [Fact]
    public async Task Adds_users_present_in_Entra_but_not_in_the_table_with_defaults_and_no_apim_key()
    {
        var admin = await SeedCallerAsync();
        _directory.AssignedUsers.Add(Present(admin));
        _directory.AssignedUsers.Add(new EntraUser("oid-new-1", "New Joiner", "new1@contoso.test", "E100"));
        _directory.AssignedUsers.Add(new EntraUser("oid-new-2", "Second Joiner", "new2@contoso.test", null));

        var result = await CreateService(admin.EntraObjectId).SyncUsersAsync(CancellationToken.None);

        Assert.Equal(2, result.AddedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(0, result.DeactivatedCount);

        var joiner = await Context.Users.AsNoTracking().SingleAsync(u => u.EntraObjectId == "oid-new-1");
        Assert.Equal("New Joiner", joiner.DisplayName);
        Assert.Equal("new1@contoso.test", joiner.Email);
        Assert.Equal("E100", joiner.EmployeeId);
        Assert.True(joiner.IsActive);
        Assert.False(joiner.IsUnlimited);
        Assert.Null(joiner.MonthlyTokenQuota);
        Assert.Equal(string.Empty, joiner.ApimSubscriptionId);
        Assert.Equal(string.Empty, joiner.ApimSubscriptionKey);
        Assert.Equal(Now, joiner.LastSyncedDate);
        Assert.NotEqual(Guid.Empty, joiner.UserUnique);

        var second = await Context.Users.AsNoTracking().SingleAsync(u => u.EntraObjectId == "oid-new-2");
        Assert.Null(second.EmployeeId);
    }

    [Fact]
    public async Task Updates_directory_fields_and_LastSyncedDate_of_users_present_in_both()
    {
        var admin = await SeedCallerAsync();
        var existing = await SeedUserAsync("oid-existing", displayName: "Old Name", email: "old@contoso.test", employeeId: null);
        _directory.AssignedUsers.Add(Present(admin));
        _directory.AssignedUsers.Add(new EntraUser("oid-existing", "New Name", "new@contoso.test", "E7"));

        var result = await CreateService(admin.EntraObjectId).SyncUsersAsync(CancellationToken.None);

        Assert.Equal(0, result.AddedCount);
        Assert.Equal(2, result.UpdatedCount);
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == existing.UserId);
        Assert.Equal("New Name", saved.DisplayName);
        Assert.Equal("new@contoso.test", saved.Email);
        Assert.Equal("E7", saved.EmployeeId);
        Assert.Equal(Now, saved.LastSyncedDate);
        Assert.True(saved.IsActive);
    }

    [Fact]
    public async Task Deactivates_users_absent_from_Entra_but_never_deletes_them()
    {
        var admin = await SeedCallerAsync();
        var departed = await SeedUserAsync("oid-departed");
        _directory.AssignedUsers.Add(Present(admin));

        var result = await CreateService(admin.EntraObjectId).SyncUsersAsync(CancellationToken.None);

        Assert.Equal(1, result.DeactivatedCount);
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == departed.UserId);
        Assert.False(saved.IsActive);
        Assert.Equal(Now, saved.LastSyncedDate);
        Assert.Equal("Test User", saved.DisplayName); // fields untouched: the directory has nothing newer
    }

    [Fact]
    public async Task A_departure_runs_the_full_deprovision_pipeline_not_just_the_inactive_flag()
    {
        // #65: before this, a departed employee kept a working gateway key until someone noticed.
        await SeedReferenceDataAsync();
        var admin = await SeedCallerAsync();
        var departed = await SeedUserAsync("oid-departed-with-key");
        var subscriptionName = ApimSubscriptionNames.ForUser(departed.UserId);
        _ = Apim.Seed(subscriptionName, GatewayTiers.Standard);
        departed.ApimSubscriptionId = Apim.GetSubscriptionResourceId(subscriptionName);
        departed.ApimSubscriptionKeyHint = "1a2b";
        departed.ApimKeyIssuedDate = Now;
        Context.QuotaAllocations.Add(new QuotaAllocation
        {
            UserId = departed.UserId,
            PeriodYear = Now.Year,
            PeriodMonth = Now.Month,
            AllocatedTokens = TestGatewayTiers.StandardCap,
            TierProductId = GatewayTiers.Standard,
        });
        _ = await Context.SaveChangesAsync();
        _directory.AssignedUsers.Add(Present(admin));

        var result = await CreateService(admin.EntraObjectId).SyncUsersAsync(CancellationToken.None);

        Assert.Equal(1, result.DeactivatedCount);

        // The APIM subscription is gone and the key fields are cleared — not merely a flag flip.
        Assert.False(Apim.Contains(subscriptionName));
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == departed.UserId);
        Assert.False(saved.IsActive);
        Assert.Empty(saved.ApimSubscriptionId);
        Assert.Empty(saved.ApimSubscriptionKeyHint);
        Assert.Null(saved.ApimKeyIssuedDate);

        var allocation = await Context.QuotaAllocations.AsNoTracking().SingleAsync(a => a.UserId == departed.UserId);
        Assert.True(allocation.IsHardStopped);

        // The departure's own rows are system-attributed; the users.synced row still names the caller.
        var targetId = departed.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var deactivation = await Context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.UserDeactivated && a.TargetId == targetId);
        Assert.Null(deactivation.ActorUserId);
        Assert.Contains("EntraDeparture", deactivation.Details, StringComparison.Ordinal);

        var revocation = await Context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.KeyRevoked && a.TargetId == targetId);
        Assert.Null(revocation.ActorUserId);

        var synced = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.UsersSynced);
        Assert.Equal(admin.UserId, synced.ActorUserId);
    }

    [Fact]
    public async Task Already_inactive_users_still_absent_are_not_counted_or_touched_again()
    {
        var admin = await SeedCallerAsync();
        var longGone = await SeedUserAsync("oid-long-gone", isActive: false);
        _directory.AssignedUsers.Add(Present(admin));

        var result = await CreateService(admin.EntraObjectId).SyncUsersAsync(CancellationToken.None);

        Assert.Equal(0, result.DeactivatedCount);
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == longGone.UserId);
        Assert.False(saved.IsActive);
        Assert.Null(saved.LastSyncedDate);
    }

    [Fact]
    public async Task Does_not_reactivate_an_inactive_user_who_is_still_or_again_in_Entra()
    {
        // Plan #21: a departed user who returns to Entra needs an admin to re-activate (which
        // re-provisions their key); an admin-deactivated user who is still in Entra stays deactivated.
        var admin = await SeedCallerAsync();
        var deactivated = await SeedUserAsync("oid-deactivated", isActive: false);
        _directory.AssignedUsers.Add(Present(admin));
        _directory.AssignedUsers.Add(new EntraUser("oid-deactivated", "Back Again", "back@contoso.test", null));

        var result = await CreateService(admin.EntraObjectId).SyncUsersAsync(CancellationToken.None);

        Assert.Equal(2, result.UpdatedCount);
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == deactivated.UserId);
        Assert.False(saved.IsActive);
        Assert.Equal("Back Again", saved.DisplayName);
        Assert.Equal(Now, saved.LastSyncedDate);
    }

    [Fact]
    public async Task Is_idempotent_a_second_run_with_no_directory_changes_adds_and_deactivates_nothing()
    {
        var admin = await SeedCallerAsync();
        _ = await SeedUserAsync("oid-departed");
        _directory.AssignedUsers.Add(Present(admin));
        _directory.AssignedUsers.Add(new EntraUser("oid-new", "New Joiner", "new@contoso.test", null));

        var first = await CreateService(admin.EntraObjectId).SyncUsersAsync(CancellationToken.None);
        var rowsAfterFirst = await Context.Users.CountAsync();
        _timeProvider.Advance(TimeSpan.FromHours(1));
        var second = await CreateService(admin.EntraObjectId).SyncUsersAsync(CancellationToken.None);

        Assert.Equal((1, 1, 1, 0), (first.AddedCount, first.UpdatedCount, first.DeactivatedCount, first.SkippedGroupAssignmentCount));
        Assert.Equal((0, 2, 0, 0), (second.AddedCount, second.UpdatedCount, second.DeactivatedCount, second.SkippedGroupAssignmentCount));
        Assert.Equal(rowsAfterFirst, await Context.Users.CountAsync());
        Assert.Equal(2, await Context.AuditLogs.CountAsync(a => a.Action == AuditActions.UsersSynced));
    }

    [Fact]
    public async Task Handles_a_directory_larger_than_one_Graph_page()
    {
        var admin = await SeedCallerAsync();
        _directory.AssignedUsers.Add(Present(admin));
        for (var i = 0; i < 250; i++)
        {
            _directory.AssignedUsers.Add(new EntraUser($"oid-bulk-{i:D3}", $"Bulk User {i}", $"bulk{i}@contoso.test", i % 2 == 0 ? $"E{i}" : null));
        }

        var result = await CreateService(admin.EntraObjectId).SyncUsersAsync(CancellationToken.None);

        Assert.Equal(250, result.AddedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(251, await Context.Users.CountAsync());
        Assert.Equal(250, await Context.Users.CountAsync(u => u.EntraObjectId.StartsWith("oid-bulk-")));
    }

    [Fact]
    public async Task A_principal_listed_twice_by_the_directory_is_added_and_counted_once()
    {
        var admin = await SeedCallerAsync();
        _directory.AssignedUsers.Add(Present(admin));
        _directory.AssignedUsers.Add(new EntraUser("oid-twice", "Twice Assigned", "twice@contoso.test", null));
        _directory.AssignedUsers.Add(new EntraUser("OID-TWICE", "Twice Assigned", "twice@contoso.test", null));

        var result = await CreateService(admin.EntraObjectId).SyncUsersAsync(CancellationToken.None);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, await Context.Users.CountAsync(u => u.EntraObjectId == "oid-twice"));
    }

    [Fact]
    public async Task Clips_an_over_long_display_name_to_the_column_length_instead_of_failing_the_run()
    {
        var admin = await SeedCallerAsync();
        _directory.AssignedUsers.Add(Present(admin));
        _directory.AssignedUsers.Add(new EntraUser("oid-long", new string('x', 256), "long@contoso.test", null));

        _ = await CreateService(admin.EntraObjectId).SyncUsersAsync(CancellationToken.None);

        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.EntraObjectId == "oid-long");
        Assert.Equal(200, saved.DisplayName.Length);
    }

    [Fact]
    public async Task Writes_exactly_one_users_synced_audit_row_attributed_to_the_caller_with_the_counts()
    {
        var admin = await SeedCallerAsync();
        var departed = await SeedUserAsync("oid-departed");
        _directory.AssignedUsers.Add(Present(admin));
        _directory.AssignedUsers.Add(new EntraUser("oid-new", "New Joiner", "new@contoso.test", null));

        _ = await CreateService(admin.EntraObjectId).SyncUsersAsync(CancellationToken.None);

        var entry = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.UsersSynced);
        Assert.Equal(admin.UserId, entry.ActorUserId);
        Assert.Equal(string.Empty, entry.TargetType);
        Assert.Equal(string.Empty, entry.TargetId);
        Assert.Equal(Now, entry.OccurredDate);
        Assert.Contains("\"addedCount\":1", entry.Details, StringComparison.Ordinal);
        Assert.Contains("\"updatedCount\":1", entry.Details, StringComparison.Ordinal);
        Assert.Contains("\"deactivatedCount\":1", entry.Details, StringComparison.Ordinal);
        Assert.Contains("\"skippedGroupAssignmentCount\":0", entry.Details, StringComparison.Ordinal);
        Assert.Contains("\"departureDetectionSuspended\":false", entry.Details, StringComparison.Ordinal);
        Assert.Contains($"\"deactivatedUserIds\":[{departed.UserId}]", entry.Details, StringComparison.Ordinal);
        Assert.Contains("\"addedEntraObjectIds\":[\"oid-new\"]", entry.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unprovisioned_caller_who_is_among_the_assigned_users_is_imported_and_attributed_in_the_same_save()
    {
        // The first admin of a fresh fork: no User row yet, but assigned to the app. The sync adds
        // them; CurrentUserAccessor sees the unsaved row in the change tracker; the audit row is theirs.
        const string CallerOid = "oid-first-admin";
        _directory.AssignedUsers.Add(new EntraUser(CallerOid, "First Admin", "admin@contoso.test", null));

        var result = await CreateService(CallerOid).SyncUsersAsync(CancellationToken.None);

        Assert.Equal(1, result.AddedCount);
        var caller = await Context.Users.AsNoTracking().SingleAsync(u => u.EntraObjectId == CallerOid);
        var entry = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.UsersSynced);
        Assert.Equal(caller.UserId, entry.ActorUserId);
    }

    [Fact]
    public async Task An_unprovisioned_caller_absent_from_the_directory_gets_403_and_nothing_is_persisted()
    {
        _directory.AssignedUsers.Add(new EntraUser("oid-someone-else", "Someone Else", "else@contoso.test", null));

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            CreateService("oid-not-provisioned").SyncUsersAsync(CancellationToken.None));

        Assert.Contains("GET /users/me", exception.Message, StringComparison.Ordinal);
        await using var verification = CreateVerificationContext();
        Assert.Equal(0, await verification.Users.CountAsync());
        Assert.Equal(0, await verification.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task A_group_principal_assignment_suspends_departure_detection_but_not_adds_and_updates()
    {
        // The enterprise pattern: the app is assigned to a security group. Its members are invisible to
        // the sync until #121, so an active user missing from the user list must NOT be deactivated.
        var admin = await SeedCallerAsync();
        var coveredByGroup = await SeedUserAsync("oid-covered-by-group");
        _directory.AssignedUsers.Add(Present(admin));
        _directory.AssignedUsers.Add(new EntraUser("oid-new", "New Joiner", "new@contoso.test", null));
        _directory.SkippedGroupAssignments.Add(new EntraGroupAssignment("group-1", "AI Developers"));
        _directory.SkippedGroupAssignments.Add(new EntraGroupAssignment("group-2", "Platform Team"));

        var result = await CreateService(admin.EntraObjectId).SyncUsersAsync(CancellationToken.None);

        Assert.Equal((1, 1, 0, 2), (result.AddedCount, result.UpdatedCount, result.DeactivatedCount, result.SkippedGroupAssignmentCount));
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == coveredByGroup.UserId);
        Assert.True(saved.IsActive);
        Assert.Null(saved.LastSyncedDate);
        _ = await Context.Users.AsNoTracking().SingleAsync(u => u.EntraObjectId == "oid-new");

        var entry = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.UsersSynced);
        Assert.Contains("\"skippedGroupAssignmentCount\":2", entry.Details, StringComparison.Ordinal);
        Assert.Contains("\"departureDetectionSuspended\":true", entry.Details, StringComparison.Ordinal);
        Assert.Contains("\"displayName\":\"AI Developers\"", entry.Details, StringComparison.Ordinal);
        Assert.Contains("\"deactivatedUserIds\":[]", entry.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_group_principal_assignment_with_no_user_assignees_is_not_a_conflict()
    {
        // Everyone is assigned through the group: zero users in the list is expected, not a misconfiguration.
        var admin = await SeedCallerAsync();
        _directory.SkippedGroupAssignments.Add(new EntraGroupAssignment("group-1", "AI Developers"));

        var result = await CreateService(admin.EntraObjectId).SyncUsersAsync(CancellationToken.None);

        Assert.Equal((0, 0, 0, 1), (result.AddedCount, result.UpdatedCount, result.DeactivatedCount, result.SkippedGroupAssignmentCount));
        Assert.True((await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == admin.UserId)).IsActive);
    }

    [Fact]
    public async Task Refuses_to_deactivate_everyone_when_the_directory_returns_no_assigned_users()
    {
        var admin = await SeedCallerAsync();
        _ = await SeedUserAsync("oid-other");

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            CreateService(admin.EntraObjectId).SyncUsersAsync(CancellationToken.None));

        Assert.Contains("refusing to deactivate", exception.Message, StringComparison.Ordinal);
        await using var verification = CreateVerificationContext();
        Assert.Equal(2, await verification.Users.CountAsync(u => u.IsActive));
        Assert.Equal(0, await verification.AuditLogs.CountAsync());
    }

    private static EntraUser Present(User user) => new(user.EntraObjectId, user.DisplayName, user.Email, user.EmployeeId);

    /// <summary>Wires the real accessor + audit service over this test's context, as DI would per request.</summary>
    private EntraUserSyncService CreateService(string callerOid)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimConstants.Oid, callerOid), new Claim(ClaimConstants.Roles, RoleNames.Admin)],
            "TestAuth",
            nameType: ClaimConstants.Name,
            roleType: ClaimConstants.Roles);
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = new CurrentUserAccessor(new FixedHttpContextAccessor(httpContext), Context);
        var writer = new AuditWriter(Context, _timeProvider);
        var audit = new AuditService(Context, writer, accessor);

        // The real lifecycle orchestrator, not a stub: a departure must actually delete the APIM
        // subscription and hard-stop the allocation (#65), and only the real pipeline proves it.
        var keys = new ApimKeyService(
            Context,
            Apim,
            new DataProtectionKeyProtector(new EphemeralDataProtectionProvider()),
            audit,
            writer,
            accessor,
            _timeProvider,
            NullLogger<ApimKeyService>.Instance);
        var quotaResolution = new QuotaResolutionService(
            Context,
            TestGatewayTiers.Mapper(),
            new NullGatewayTierSync(NullLogger<NullGatewayTierSync>.Instance),
            NullLogger<QuotaResolutionService>.Instance);
        var lifecycle = new UserLifecycleService(
            Context,
            quotaResolution,
            keys,
            audit,
            writer,
            accessor,
            _directory,
            new AppSettings(),
            _timeProvider,
            NullLogger<UserLifecycleService>.Instance);

        return new EntraUserSyncService(Context, _directory, lifecycle, audit, _timeProvider, NullLogger<EntraUserSyncService>.Instance);
    }

    /// <summary>A second context on the same database, so "nothing was saved" assertions cannot be fooled by the change tracker.</summary>
    private FoundryGate.Data.AppDbContext CreateVerificationContext()
    {
        var options = new DbContextOptionsBuilder<FoundryGate.Data.AppDbContext>()
            .UseSqlite(Context.Database.GetDbConnection())
            .Options;
        return new FoundryGate.Data.AppDbContext(options);
    }

    private Task<User> SeedCallerAsync() => SeedUserAsync(Guid.NewGuid().ToString(), displayName: "Admin Caller", email: "admin@contoso.test");

    private async Task<User> SeedUserAsync(
        string entraObjectId,
        string displayName = "Test User",
        string? email = null,
        string? employeeId = null,
        bool isActive = true)
    {
        var user = new User
        {
            EntraObjectId = entraObjectId,
            DisplayName = displayName,
            Email = email ?? $"{Guid.NewGuid():N}@contoso.test",
            EmployeeId = employeeId,
            IsActive = isActive,
        };
        Context.Users.Add(user);
        _ = await Context.SaveChangesAsync();
        return user;
    }
}
