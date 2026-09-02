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
        // — and with nothing to change, the profile read never writes at all (#156 review; #157 adds the
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
        Func<IAuditService, IAuditService>? wrapAudit = null)
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
        var accessor = new CurrentUserAccessor(new FixedHttpContextAccessor(new DefaultHttpContext { User = new ClaimsPrincipal(identity) }), Context);

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
            _timeProvider,
            NullLogger<ApimKeyService>.Instance);
        var quotaResolution = new QuotaResolutionService(
            Context,
            tierMapper,
            new ApimGatewayTierSync(keys, NullLogger<ApimGatewayTierSync>.Instance),
            NullLogger<QuotaResolutionService>.Instance);
        var quotaAllocations = new QuotaAllocationService(
            Context,
            quotaResolution,
            tierMapper,
            accessor,
            audit,
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
