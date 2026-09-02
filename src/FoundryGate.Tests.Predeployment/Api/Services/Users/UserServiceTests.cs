using System.Security.Claims;
using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Api.Services.Keys;
using FoundryGate.Api.Services.Lifecycle;
using FoundryGate.Api.Services.Quota;
using FoundryGate.Api.Services.Requests;
using FoundryGate.Api.Services.Security;
using FoundryGate.Api.Services.Users;
using FoundryGate.Core.Configuration;
using FoundryGate.Core.Quota;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Keys;
using FoundryGate.Domain.Users.Contracts;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Web;

namespace FoundryGate.Tests.Predeployment.Api.Services.Users;

/// <summary>
/// <see cref="UserService"/> at the seams the endpoint tests cannot reach from outside: how the CLI
/// config block is sourced on a host with no gateway, what a token that omits a claim is allowed to
/// overwrite, and the group roster on the detail view.
/// </summary>
public class UserServiceTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);

    private readonly MutableTimeProvider _timeProvider = new(Now);
    private readonly FakeApimManagementClient _apim = new();
    private readonly FakeEntraDirectoryClient _directory = new();

    [Fact]
    public async Task Cli_config_reports_an_empty_base_url_on_a_host_with_no_gateway_rather_than_inventing_one()
    {
        var user = await SeedUserAsync("Local Dev");
        var service = CreateService(user.EntraObjectId, gatewayUrl: null);

        var profile = await service.GetMyProfileAsync(CancellationToken.None);

        Assert.Empty(profile.CliConfig.GatewayBaseUrl);
        Assert.Equal(GatewayOptions.AnthropicBasePath, profile.CliConfig.AnthropicBasePath);
        Assert.Equal(GatewayOptions.OpenAiBasePath, profile.CliConfig.OpenAiBasePath);

        // #153: the alias map lives only in bicep today, so the honest answer is "none", not a guess.
        Assert.Empty(profile.CliConfig.ModelAliases);
    }

    [Fact]
    public async Task Cli_config_trims_a_trailing_slash_so_base_url_plus_path_is_always_well_formed()
    {
        var user = await SeedUserAsync("Slash Dev");
        var service = CreateService(user.EntraObjectId, gatewayUrl: "https://ai.contoso.test/");

        var profile = await service.GetMyProfileAsync(CancellationToken.None);

        Assert.Equal("https://ai.contoso.test", profile.CliConfig.GatewayBaseUrl);
    }

    [Fact]
    public async Task A_token_with_no_name_or_email_claim_never_blanks_what_the_directory_sync_already_stored()
    {
        var user = await SeedUserAsync("Synced Name", email: "synced@contoso.test");
        var service = CreateService(user.EntraObjectId, name: null, email: null);

        var profile = await service.GetMyProfileAsync(CancellationToken.None);

        Assert.Equal("Synced Name", profile.DisplayName);
        Assert.Equal("synced@contoso.test", profile.Email);

        // LastSyncedDate means "an Entra sync touched this row" and /me is not a sync, so it stays null
        // — and with nothing to change, the profile read never writes at all (#156 review; #167 adds the
        // honest LastLoginDate column).
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == user.UserId);
        Assert.Null(saved.LastSyncedDate);
    }

    [Fact]
    public async Task Detail_lists_the_users_groups_by_name()
    {
        var user = await SeedUserAsync("Grouped Dev");
        var platform = await SeedGroupAsync("Platform Engineering", user);
        var data = await SeedGroupAsync("Data Science", user);
        var service = CreateService(user.EntraObjectId);

        var detail = await service.GetAsync(user.UserId, CancellationToken.None);

        Assert.Equal([data.GroupId, platform.GroupId], detail.Groups.Select(g => g.GroupId));
        Assert.Equal("Data Science", detail.Groups[0].Name);
        Assert.Equal(Now, detail.Groups[0].AddedDate);
    }

    [Fact]
    public async Task An_invalid_quota_is_rejected_before_the_user_is_even_loaded()
    {
        var admin = await SeedUserAsync("Ada Admin");
        var service = CreateService(admin.EntraObjectId);

        // 400, not 404: the request is malformed whether or not user 987654 exists, and the message
        // that lists the tiers is more useful than "no such user".
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateQuotaAsync(987654, new UpdateUserQuotaRequest { MonthlyTokenQuota = 7 }, CancellationToken.None));

        Assert.Contains("not a configured budget tier", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_orders_by_display_name_and_respects_the_page_size()
    {
        var charlie = await SeedUserAsync("Charlie");
        var alice = await SeedUserAsync("Alice");
        var bob = await SeedUserAsync("Bob");
        var service = CreateService(alice.EntraObjectId);

        var page = await service.ListAsync(new UserListQuery(null, null), new PagedRequest(1, 2), CancellationToken.None);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal([alice.UserId, bob.UserId], page.Items.Select(u => u.UserId));

        var second = await service.ListAsync(new UserListQuery(null, null), new PagedRequest(2, 2), CancellationToken.None);
        Assert.Equal(charlie.UserId, Assert.Single(second.Items).UserId);
    }

    private UserService CreateService(
        string callerOid,
        string? name = "Token Name",
        string? email = "token@contoso.test",
        string? gatewayUrl = "https://apim-foundrygate-test.azure-api.net",
        Func<IAuditService, IAuditService>? wrapAudit = null,
        Func<ICurrentUserAccessor, ICurrentUserAccessor>? wrapAccessor = null)
    {
        var claims = new List<Claim> { new(ClaimConstants.Oid, callerOid), new(ClaimConstants.Roles, RoleNames.Admin) };
        if (name is not null)
        {
            claims.Add(new Claim(ClaimConstants.Name, name));
        }

        if (email is not null)
        {
            claims.Add(new Claim(ClaimConstants.PreferredUserName, email));
        }

        var identity = new ClaimsIdentity(claims, "TestAuth", nameType: ClaimConstants.Name, roleType: ClaimConstants.Roles);
        ICurrentUserAccessor accessor = new CurrentUserAccessor(new FixedHttpContextAccessor(new DefaultHttpContext { User = new ClaimsPrincipal(identity) }), Context);
        accessor = wrapAccessor?.Invoke(accessor) ?? accessor;

        var gateway = TestGatewayTiers.Options();
        gateway.ApimGatewayUrl = gatewayUrl;
        var tierMapper = new GatewayTierMapper(gateway);

        var writer = new AuditWriter(Context, _timeProvider);
        IAuditService audit = new AuditService(Context, writer, accessor);
        audit = wrapAudit?.Invoke(audit) ?? audit;
        var keys = new ApimKeyService(
            Context,
            _apim,
            new DataProtectionKeyProtector(new EphemeralDataProtectionProvider()),
            audit,
            writer,
            accessor,
            TestSecurityOptions.RevealAnomaly(),
            _timeProvider,
            NullLogger<ApimKeyService>.Instance);
        var quotaResolution = new QuotaResolutionService(
            Context,
            tierMapper,
            new ApimGatewayTierSync(_apim, writer, new CurrentUserGatewayTierSyncActor(accessor), NullLogger<ApimGatewayTierSync>.Instance),
            NullLogger<QuotaResolutionService>.Instance);
        var quotaAllocations = new QuotaAllocationService(
            Context,
            quotaResolution,
            new QuotaResetService(Context, quotaResolution, writer, _timeProvider, NullLogger<QuotaResetService>.Instance),
            tierMapper,
            accessor,
            _timeProvider,
            NullLogger<QuotaAllocationService>.Instance);
        var quotaRequests = new QuotaRequestService(Context, quotaResolution, tierMapper, accessor, audit, _timeProvider);
        var lifecycle = new UserLifecycleService(
            Context,
            quotaResolution,
            quotaRequests,
            keys,
            audit,
            writer,
            accessor,
            _directory,
            new AppSettings { Gateway = gateway },
            _timeProvider,
            NullLogger<UserLifecycleService>.Instance);

        return new UserService(
            Context,
            lifecycle,
            quotaAllocations,
            quotaResolution,
            keys,
            tierMapper,
            gateway,
            audit,
            accessor,
            _timeProvider,
            NullLogger<UserService>.Instance);
    }

    // -- #154: the first-login race is the caller's problem no longer -------------------------------

    [Fact]
    public async Task A_first_login_that_loses_the_race_returns_the_winners_profile_instead_of_a_409()
    {
        // Forced deterministically, as #154 asks: the accessor reports "no user" for the first two
        // lookups — the profile read's own and the provision pipeline's — while the winner's row is
        // already committed. That is exactly the state the losing tab is in when its INSERT reaches the
        // unique index on EntraObjectId. Before the fix the developer's very first request 4xx'd.
        await SeedReferenceDataAsync();
        var oid = Guid.NewGuid().ToString();
        var winner = new User { EntraObjectId = oid, DisplayName = "Winning Tab", Email = "winner@contoso.test" };
        Context.Users.Add(winner);
        _ = await Context.SaveChangesAsync();
        var winnerId = winner.UserId;

        var service = CreateService(
            oid,
            name: "Winning Tab",
            email: "winner@contoso.test",
            wrapAccessor: inner => new RaceLosingCurrentUserAccessor(inner, missesBeforeReporting: 2));

        var profile = await service.GetMyProfileAsync(CancellationToken.None);

        Assert.Equal(winnerId, profile.UserId);
        Assert.Equal("Winning Tab", profile.DisplayName);

        // The loser's insert rolled back with its transaction: one row for the oid, not two.
        Context.ChangeTracker.Clear();
        Assert.Equal(1, await Context.Users.AsNoTracking().CountAsync(u => u.EntraObjectId == oid));

        // And the profile handed back is the winner's, complete — quota resolved against their row.
        Assert.Equal(winnerId, profile.Quota.UserId);
    }

    [Fact]
    public async Task A_first_login_whose_insert_fails_for_a_reason_other_than_the_oid_race_is_not_absorbed()
    {
        // #184 review: the earlier version of this failed the *audit* service, which never reaches the
        // catch (DbUpdateException) at all — it proved a weaker claim than its name. This one puts a
        // constraint this fork does not have on the test's own database, so the pipeline's insert fails
        // with a genuine DbUpdateException that has nothing to do with the oid. That is exactly the
        // branch the AnyAsync winner check exists to tell apart, and it must surface, not be absorbed.
        await SeedReferenceDataAsync();
        _ = await Context.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX UX_Test_Users_Email ON Users(Email);");
        _ = await SeedUserAsync("Holds The Email", "taken@contoso.test");

        var oid = Guid.NewGuid().ToString();
        var service = CreateService(oid, name: "New Dev", email: "taken@contoso.test");

        _ = await Assert.ThrowsAnyAsync<DbUpdateException>(() => service.GetMyProfileAsync(CancellationToken.None));

        // Not converted to a ConflictException, and therefore never absorbed — and the transaction still
        // rolled back, so no half-provisioned row survives.
        Context.ChangeTracker.Clear();
        Assert.Equal(0, await Context.Users.AsNoTracking().CountAsync(u => u.EntraObjectId == oid));
    }

    [Fact]
    public async Task A_first_login_whose_audit_row_cannot_be_written_leaves_no_user_behind()
    {
        // The failure the previous test used to cover, kept for what it actually proves: a provision
        // that gets past the insert and then fails still leaves nothing behind, because the whole
        // pipeline is one transaction.
        await SeedReferenceDataAsync();
        var oid = Guid.NewGuid().ToString();

        var service = CreateService(
            oid,
            name: "Broken Provision",
            email: "broken@contoso.test",
            wrapAudit: inner => new FailingAuditService(inner) { FailOn = action => action == AuditActions.UserProvisioned });

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetMyProfileAsync(CancellationToken.None));

        Context.ChangeTracker.Clear();
        Assert.Equal(0, await Context.Users.AsNoTracking().CountAsync(u => u.EntraObjectId == oid));
    }

    // -- #167: LastLoginDate is honest without making every profile load a write ---------------------

    [Fact]
    public async Task First_login_stamps_LastLoginDate_and_a_reload_inside_the_granularity_window_leaves_it_alone()
    {
        await SeedReferenceDataAsync();
        var oid = Guid.NewGuid().ToString();

        var profile = await CreateService(oid, name: "New Dev", email: "new@contoso.test").GetMyProfileAsync(CancellationToken.None);

        Context.ChangeTracker.Clear();
        Assert.Equal(Now, (await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == profile.UserId)).LastLoginDate);

        // A UI that reloads the profile on every navigation must not turn each read into an UPDATE on
        // Users — the problem that made LastSyncedDate dishonest in the first place (#156 review).
        _timeProvider.Advance(UserService.LastLoginGranularity - TimeSpan.FromMinutes(1));
        _ = await CreateService(oid, name: "New Dev", email: "new@contoso.test").GetMyProfileAsync(CancellationToken.None);

        Context.ChangeTracker.Clear();
        Assert.Equal(Now, (await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == profile.UserId)).LastLoginDate);
    }

    [Fact]
    public async Task A_profile_load_past_the_granularity_window_restamps_LastLoginDate_and_surfaces_it()
    {
        await SeedReferenceDataAsync();
        var oid = Guid.NewGuid().ToString();
        var profile = await CreateService(oid, name: "Returning Dev", email: "returning@contoso.test").GetMyProfileAsync(CancellationToken.None);

        _timeProvider.Advance(TimeSpan.FromDays(3));
        var later = _timeProvider.GetUtcNow();
        _ = await CreateService(oid, name: "Returning Dev", email: "returning@contoso.test").GetMyProfileAsync(CancellationToken.None);

        Context.ChangeTracker.Clear();
        Assert.Equal(later, (await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == profile.UserId)).LastLoginDate);

        // And an admin reads it off the user row, which is the point of the column (#167).
        var detail = await CreateService(oid, name: "Returning Dev", email: "returning@contoso.test").GetAsync(profile.UserId, CancellationToken.None);
        Assert.Equal(later, detail.User.LastLoginDate);
    }

    [Fact]
    public async Task A_user_who_has_never_loaded_their_profile_reads_as_null_not_as_created_date()
    {
        // "Never signed in" is a real, interesting state for an offboarding sweep — not a date to
        // invent (#167).
        var provisioned = await SeedUserAsync("Never Signed In");
        var admin = await SeedUserAsync("Ada Admin");

        var detail = await CreateService(admin.EntraObjectId).GetAsync(provisioned.UserId, CancellationToken.None);

        Assert.Null(detail.User.LastLoginDate);
    }

    // -- #163/#158: a quota change the gateway accepted is written down whatever the client does -----

    [Fact]
    public async Task A_quota_change_whose_client_disconnects_the_instant_the_gateway_moves_still_lands()
    {
        // The commit-point rule applied to PUT /users/{id}/quota: past IGatewayTierSync the subscription
        // is already on the new product, so the row and its audit row run on CancellationToken.None.
        var admin = await SeedUserAsync("Ada Admin");
        var developer = await SeedUserAsync("Hangs Up");
        var subscriptionName = ApimSubscriptionNames.ForUser(developer.UserId);
        _ = await CreateService(admin.EntraObjectId).GetMyProfileAsync(CancellationToken.None); // admin row exists
        _ = await BuildKeyServiceFor(admin).ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);
        _ = await Context.SaveChangesAsync();

        using var cts = new CancellationTokenSource();
        _apim.AfterMutation = cts.Cancel;

        _ = await CreateService(admin.EntraObjectId).UpdateQuotaAsync(
            developer.UserId,
            new UpdateUserQuotaRequest { MonthlyTokenQuota = TestGatewayTiers.PowerCap },
            cts.Token);

        _apim.AfterMutation = null;
        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(GatewayTiers.Power, _apim.ProductOf(subscriptionName));

        Context.ChangeTracker.Clear();
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId);
        Assert.Equal(TestGatewayTiers.PowerCap, saved.MonthlyTokenQuota);
        _ = Assert.Single(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.UserQuotaChanged).ToListAsync());
        _ = Assert.Single(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.KeyTierChanged).ToListAsync());
    }

    /// <summary>
    /// An <see cref="ICurrentUserAccessor"/> that reports "no user for this caller" for the first
    /// <c>missesBeforeReporting</c> lookups and delegates afterwards. That is the loser's view of a
    /// first-login race, made deterministic: the profile read and the provision pipeline both see no
    /// row while the winner's is already committed, so the loser's INSERT hits the unique index exactly
    /// as it does in production (#154). Hand-rolled — no mocking library (CONVENTIONS.md).
    /// </summary>
    private sealed class RaceLosingCurrentUserAccessor(ICurrentUserAccessor inner, int missesBeforeReporting) : ICurrentUserAccessor
    {
        private int _lookups;

        public string EntraObjectId => inner.EntraObjectId;

        public bool IsAdmin => inner.IsAdmin;

        public string? DisplayName => inner.DisplayName;

        public string? Email => inner.Email;

        public async Task<User?> TryGetUserAsync(CancellationToken cancellationToken) =>
            _lookups++ < missesBeforeReporting ? null : await inner.TryGetUserAsync(cancellationToken);

        public async Task<User> GetRequiredUserAsync(CancellationToken cancellationToken) =>
            await TryGetUserAsync(cancellationToken)
            ?? throw new UnauthorizedAccessException($"No FoundryGate user exists for the caller (oid {EntraObjectId}).");
    }

    [Fact]
    public async Task A_quota_change_whose_audit_row_cannot_be_written_is_not_committed()
    {
        // The reviewer's second probe (#156 Major 2). MoveToProductAsync used to end with its own
        // SaveChangesAsync, so an audit failure left the new quota committed with nothing describing it.
        var admin = await SeedUserAsync("Ada Admin");
        var developer = await SeedUserAsync("Audit Fails");
        var subscriptionName = ApimSubscriptionNames.ForUser(developer.UserId);
        _ = await CreateService(admin.EntraObjectId).GetMyProfileAsync(CancellationToken.None); // admin row exists
        _ = await BuildKeyServiceFor(admin).ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);
        _ = await Context.SaveChangesAsync();

        var service = CreateService(admin.EntraObjectId, wrapAudit: inner => new FailingAuditService(inner)
        {
            FailOn = action => action == AuditActions.UserQuotaChanged,
        });

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdateQuotaAsync(developer.UserId, new UpdateUserQuotaRequest { MonthlyTokenQuota = TestGatewayTiers.PowerCap }, CancellationToken.None));

        // The gateway did move — that is the accepted, self-healing direction — but the database did not
        // record a budget nobody audited.
        Assert.Equal(GatewayTiers.Power, _apim.ProductOf(subscriptionName));
        Context.ChangeTracker.Clear();
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId);
        Assert.Null(saved.MonthlyTokenQuota);
        Assert.Empty(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.UserQuotaChanged).ToListAsync());

        // And the key.tier-changed row the move added was rolled back with it — no orphan audit row
        // claiming a tier change the database never made.
        Assert.Empty(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.KeyTierChanged).ToListAsync());
    }

    /// <summary>A key service acting as <paramref name="actor"/>, for arranging a provisioned developer.</summary>
    private ApimKeyService BuildKeyServiceFor(User actor)
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimConstants.Oid, actor.EntraObjectId), new Claim(ClaimConstants.Roles, RoleNames.Admin)],
            "TestAuth",
            nameType: ClaimConstants.Name,
            roleType: ClaimConstants.Roles);
        var accessor = new CurrentUserAccessor(new FixedHttpContextAccessor(new DefaultHttpContext { User = new ClaimsPrincipal(identity) }), Context);
        var writer = new AuditWriter(Context, _timeProvider);
        return new ApimKeyService(
            Context,
            _apim,
            new DataProtectionKeyProtector(new EphemeralDataProtectionProvider()),
            new AuditService(Context, writer, accessor),
            writer,
            accessor,
            TestSecurityOptions.RevealAnomaly(),
            _timeProvider,
            NullLogger<ApimKeyService>.Instance);
    }

    private async Task<User> SeedUserAsync(string displayName, string? email = null)
    {
        await SeedReferenceDataAsync();

        var user = new User
        {
            EntraObjectId = Guid.NewGuid().ToString(),
            DisplayName = displayName,
            Email = email ?? $"{Guid.NewGuid():N}@contoso.test",
        };
        Context.Users.Add(user);
        _ = await Context.SaveChangesAsync();
        return user;
    }

    private async Task<Group> SeedGroupAsync(string name, User member)
    {
        var group = new Group { Name = name };
        Context.Groups.Add(group);
        _ = await Context.SaveChangesAsync();

        // AddedDate by hand: this harness's DbContext has no TimestampInterceptor (production's does).
        Context.GroupMembers.Add(new GroupMember { GroupId = group.GroupId, UserId = member.UserId, AddedDate = Now });
        _ = await Context.SaveChangesAsync();
        return group;
    }
}
