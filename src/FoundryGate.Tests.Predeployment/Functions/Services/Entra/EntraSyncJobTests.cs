using System.Text.Json;
using FoundryGate.Core.Configuration;
using FoundryGate.Core.Entra;
using FoundryGate.Core.Quota;
using FoundryGate.Core.Requests;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Keys;
using FoundryGate.Functions.Services.Entra;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoundryGate.Tests.Predeployment.Functions.Services.Entra;

/// <summary>
/// <see cref="EntraSyncJob"/> — the nightly tick behind <c>EntraSyncFunction</c> (#151): the
/// <c>Entra:Enabled</c> off switch, the cross-replica lock, the users-then-groups order, and the one
/// audit row per run. What each reconciliation actually writes is <c>EntraUserSyncServiceTests</c>'
/// and <c>EntraGroupSyncServiceTests</c>' business; this class is about the schedule around them.
/// </summary>
public class EntraSyncJobTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 2, 0, 0, TimeSpan.Zero);

    private readonly MutableTimeProvider _clock = new(Now);
    private readonly FakeEntraDirectoryClient _directory = new();
    private readonly FakeApimManagementClient _apim = new();
    private readonly CapturingLoggerProvider _logs = new();

    [Fact]
    public async Task A_disabled_directory_is_skipped_at_Information_without_touching_the_lock()
    {
        // Not Warning and not Error: a fork that has not granted the Graph application roles has
        // deliberately left this off, and an error every night is how the one night that matters gets
        // ignored (#151).
        var jobLock = new FakeJobLock();

        var outcome = await CreateJob(jobLock, enabled: false).RunAsync(CancellationToken.None);

        Assert.False(outcome.Ran);
        Assert.Equal(EntraSyncSkipReasonType.DirectoryDisabled, outcome.SkipReasonType);
        Assert.Null(outcome.Users);
        Assert.Null(outcome.Groups);
        Assert.Empty(jobLock.Requested);
        Assert.Equal(0, _directory.ListAssignedUsersCalls);
        Assert.Empty(await Context.AuditLogs.AsNoTracking().ToListAsync());

        var skip = Assert.Single(_logs.Messages, message => message.Message.Contains("Entra:Enabled is false", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, skip.Level);
    }

    [Fact]
    public async Task Another_replica_holding_the_lock_means_this_one_does_nothing()
    {
        await SeedReferenceDataAsync();
        var jobLock = new FakeJobLock(acquire: false);

        var outcome = await CreateJob(jobLock).RunAsync(CancellationToken.None);

        Assert.False(outcome.Ran);
        Assert.Equal(EntraSyncSkipReasonType.LockNotAcquired, outcome.SkipReasonType);
        Assert.Equal([EntraSyncJob.LockName], jobLock.Requested);
        Assert.Equal(0, _directory.ListAssignedUsersCalls);
        Assert.Empty(await Context.AuditLogs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Its_lock_name_is_not_the_monthly_resets_so_the_two_jobs_never_block_each_other()
    {
        Assert.NotEqual(FoundryGate.Functions.Services.Quota.MonthlyResetJob.LockName, EntraSyncJob.LockName);
    }

    [Fact]
    public async Task It_runs_the_users_sync_first_so_the_group_sync_can_see_the_people_it_imported()
    {
        // The whole reason the two are one job rather than two timers: group sync skips directory
        // members with no User row. Run the other way round, this joiner would be a
        // skippedUnknownUserCount tonight and a member only tomorrow.
        await SeedReferenceDataAsync();
        var group = await SeedLinkedGroupAsync("Platform");
        _directory.AssignedUsers.Add(new EntraUser("oid-joiner", "New Joiner", "joiner@contoso.test", null));
        _directory.GroupMembers[group.EntraGroupId] = ["oid-joiner"];
        var jobLock = new FakeJobLock();

        var outcome = await CreateJob(jobLock).RunAsync(CancellationToken.None);

        Assert.True(outcome.Ran);
        Assert.Equal(1, outcome.Users!.AddedCount);
        var groupResult = Assert.Single(outcome.Groups!);
        Assert.Equal(1, groupResult.AddedCount);
        Assert.Equal(0, groupResult.SkippedUnknownUserCount);
        Assert.Equal(1, jobLock.Released);

        var joiner = await Context.Users.AsNoTracking().SingleAsync(u => u.EntraObjectId == "oid-joiner");
        Assert.True(await Context.GroupMembers.AsNoTracking().AnyAsync(m => m.GroupId == group.GroupId && m.UserId == joiner.UserId));
    }

    [Fact]
    public async Task One_system_attributed_row_describes_the_whole_run()
    {
        await SeedReferenceDataAsync();
        var group = await SeedLinkedGroupAsync("Platform");
        _directory.AssignedUsers.Add(new EntraUser("oid-joiner", "New Joiner", "joiner@contoso.test", null));
        _directory.GroupMembers[group.EntraGroupId] = ["oid-joiner"];

        _ = await CreateJob(new FakeJobLock()).RunAsync(CancellationToken.None);

        var row = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.EntraScheduledSync);
        Assert.Null(row.ActorUserId); // nobody asked for this; the schedule did
        Assert.Equal(string.Empty, row.TargetType);

        var details = JsonDocument.Parse(row.Details).RootElement;
        Assert.Equal(1, details.GetProperty("users").GetProperty("addedCount").GetInt32());
        Assert.Equal(1, details.GetProperty("groups").GetProperty("groupCount").GetInt32());
        Assert.Equal(1, details.GetProperty("groups").GetProperty("addedCount").GetInt32());
        Assert.Equal(0, details.GetProperty("groups").GetProperty("failedCount").GetInt32());

        // The services' own rows are still there and still system-attributed on this host.
        var usersRow = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.UsersSynced);
        Assert.Null(usersRow.ActorUserId);
        var groupRow = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.GroupEntraSynced);
        Assert.Null(groupRow.ActorUserId);
    }

    [Fact]
    public async Task A_fork_with_no_linked_groups_still_completes_the_users_half()
    {
        await SeedReferenceDataAsync();
        _directory.AssignedUsers.Add(new EntraUser("oid-joiner", "New Joiner", "joiner@contoso.test", null));

        var outcome = await CreateJob(new FakeJobLock()).RunAsync(CancellationToken.None);

        Assert.True(outcome.Ran);
        Assert.Equal(1, outcome.Users!.AddedCount);
        Assert.Empty(outcome.Groups!);
    }

    [Fact]
    public async Task A_departure_is_deprovisioned_for_real_not_merely_flagged_inactive()
    {
        // The reason this host implements IDepartureHandler rather than skipping departures: a nightly
        // run that flipped IsActive while the gateway still honoured the key would make the admin UI
        // lie about who has access (#151 / #214).
        await SeedReferenceDataAsync();

        // Somebody has to remain assigned: an empty assigned-user list with a populated table is the
        // "refusing to deactivate every user" guard, not a mass departure.
        var staying = await SeedUserAsync("oid-staying", "Staying");
        _directory.AssignedUsers.Add(new EntraUser(staying.EntraObjectId, staying.DisplayName, staying.Email, null));

        var departed = await SeedUserAsync("oid-departed", "Departed");
        var subscriptionName = ApimSubscriptionNames.ForUser(departed.UserId);
        _ = _apim.Seed(subscriptionName, GatewayTiers.Standard);
        departed.ApimSubscriptionId = _apim.GetSubscriptionResourceId(subscriptionName);
        departed.ApimSubscriptionKeyHint = "1a2b";
        departed.ApimKeyIssuedDate = Now;
        _ = await Context.SaveChangesAsync();

        var outcome = await CreateJob(new FakeJobLock()).RunAsync(CancellationToken.None);

        Assert.Equal(1, outcome.Users!.DeactivatedCount);
        Assert.False(_apim.Contains(subscriptionName));

        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == departed.UserId);
        Assert.False(saved.IsActive);
        Assert.Empty(saved.ApimSubscriptionId);
    }

    // There is deliberately no test for the OTHER half of this job's CommitToken — "a pass that
    // offboarded somebody keeps its own row through a cancellation". The window that protects is real
    // but narrow: cancellation strictly between SyncAllAsync returning and the save below it, i.e. a
    // host shutdown at that instant. Cancelling any earlier (say, the moment APIM accepts a deletion)
    // aborts inside one of the two syncs instead — each honours the raw token up to its own commit
    // point, which is correct — so the job's row is never reached and the assertion would be testing
    // the sync, not this. Constructing the real window needs a seam between the two calls that exists
    // only for the test, which is a worse trade than this comment. The predicate itself is pinned
    // below.
    [Fact]
    public async Task A_pass_that_offboarded_nobody_still_honours_cancellation()
    {
        // Half of CommitToken.For's predicate that IS reachable: nothing outside the database was
        // touched, so an abandoned run stops rather than forcing its row through on
        // CancellationToken.None (PR #216 review).
        await SeedReferenceDataAsync();
        var staying = await SeedUserAsync("oid-staying", "Staying");
        _directory.AssignedUsers.Add(new EntraUser(staying.EntraObjectId, staying.DisplayName, staying.Email, null));

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateJob(new FakeJobLock()).RunAsync(cancelled.Token));

        Assert.False(await Context.AuditLogs.AsNoTracking().AnyAsync(a => a.Action == AuditActions.EntraScheduledSync));
    }

    // -- Helpers --

    private EntraSyncJob CreateJob(FakeJobLock jobLock, bool enabled = true)
    {
        var entra = new EntraOptions { Enabled = enabled, ApplicationClientId = Guid.Empty.ToString() };
        var audit = new AuditWriter(Context, _clock);
        var expiry = new QuotaRequestExpiry(Context, audit, _clock, NullLogger<QuotaRequestExpiry>.Instance);
        var resolution = new QuotaResolutionService(
            Context,
            TestGatewayTiers.Mapper(),
            new NullGatewayTierSync(NullLogger<NullGatewayTierSync>.Instance),
            NullLogger<QuotaResolutionService>.Instance);

        // Exactly the graph the Functions host composes: the system actor and Core's own departure
        // handler over the in-memory APIM.
        var departures = new DeprovisioningDepartureHandler(
            Context,
            _apim,
            expiry,
            audit,
            _clock,
            NullLogger<DeprovisioningDepartureHandler>.Instance);
        var actor = new SystemEntraSyncActor();

        var users = new EntraUserSyncService(Context, _directory, departures, actor, audit, _clock, NullLogger<EntraUserSyncService>.Instance);
        var groups = new EntraGroupSyncService(Context, _directory, resolution, actor, audit, _clock, NullLogger<EntraGroupSyncService>.Instance);

        return new EntraSyncJob(Context, entra, users, groups, jobLock, audit, _logs.CreateLogger<EntraSyncJob>());
    }

    private async Task<User> SeedUserAsync(string entraObjectId, string displayName)
    {
        var user = new User
        {
            EntraObjectId = entraObjectId,
            DisplayName = displayName,
            Email = $"{Guid.NewGuid():N}@contoso.test",
        };
        _ = Context.Users.Add(user);
        _ = await Context.SaveChangesAsync();
        return user;
    }

    private async Task<Group> SeedLinkedGroupAsync(string name)
    {
        var group = new Group
        {
            Name = name,
            EntraGroupId = Guid.NewGuid().ToString(),
            MonthlyTokenQuota = TestGatewayTiers.PowerCap,
        };
        _ = Context.Groups.Add(group);
        _ = await Context.SaveChangesAsync();
        return group;
    }
}
