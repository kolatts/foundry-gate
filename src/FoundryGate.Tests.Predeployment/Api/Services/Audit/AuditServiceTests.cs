using System.Security.Claims;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Audit.Contracts;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;

namespace FoundryGate.Tests.Predeployment.Api.Services.Audit;

/// <summary>
/// The Api wrapper's own contract: current-caller attribution through the <em>real</em>
/// <see cref="CurrentUserAccessor"/> + <see cref="AuditWriter"/> (so the first-login pattern is tested
/// end-to-end at the service level), the 403 for an unprovisioned caller, and the admin read query.
/// Row-building semantics (JSON, timestamps, no-save) are covered in <c>AuditWriterTests</c>.
/// </summary>
public class AuditServiceTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MutableTimeProvider _timeProvider = new(Now);

    [Fact]
    public async Task LogAsync_attributes_the_row_to_the_caller_resolved_from_the_oid_claim()
    {
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId);

        var entry = await service.LogAsync(AuditActions.UserQuotaChanged, AuditTargetTypes.User, "42", new { After = 5 }, CancellationToken.None);
        await Context.SaveChangesAsync();

        var saved = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.AuditLogId == entry.AuditLogId);
        Assert.Equal(actor.UserId, saved.ActorUserId);
        Assert.Equal("{\"after\":5}", saved.Details);
    }

    [Fact]
    public async Task LogAsync_attributes_a_caller_whose_User_was_added_but_not_yet_saved_the_auto_provision_pattern()
    {
        // What GET /users/me (#28) does on first login, in one unit of work: Add user → LogAsync →
        // SaveChanges. Before the fix, TryGetUserAsync went straight to the database, missed the
        // unsaved row, and LogAsync 403'd.
        var oid = Guid.NewGuid().ToString();
        var service = CreateService(oid);
        var newUser = new User { EntraObjectId = oid, DisplayName = "New Joiner", Email = "new@contoso.test" };
        Context.Users.Add(newUser);

        var entry = await service.LogAsync(AuditActions.UserProvisioned, AuditTargetTypes.User, string.Empty, null, CancellationToken.None);
        await Context.SaveChangesAsync();

        Assert.Same(newUser, entry.ActorUser);
        var saved = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.AuditLogId == entry.AuditLogId);
        Assert.Equal(newUser.UserId, saved.ActorUserId);
        Assert.NotEqual(0, saved.ActorUserId);
    }

    [Fact]
    public async Task LogAsync_throws_UnauthorizedAccessException_when_the_caller_has_no_User_row()
    {
        var service = CreateService("no-such-user");

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.LogAsync(AuditActions.GroupCreated, AuditTargetTypes.Group, "7", null, CancellationToken.None));

        Assert.Contains("no-such-user", exception.Message, StringComparison.Ordinal);
        Assert.Contains("GET /users/me", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Context.ChangeTracker.Entries<AuditLog>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LogAsync_rejects_a_blank_action(string action)
    {
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.LogAsync(action, AuditTargetTypes.User, "1", null, CancellationToken.None));
    }

    [Fact]
    public async Task QueryAsync_returns_newest_first_and_maps_empty_target_and_details_to_null()
    {
        var actor = await SeedUserAsync("Grace Hopper");
        var writer = new AuditWriter(Context, _timeProvider);
        var service = CreateService(actor.EntraObjectId);

        _timeProvider.SetUtcNow(Now);
        var older = writer.Add(actor, AuditActions.GroupCreated, AuditTargetTypes.Group, "1", new { Name = "A" });
        _timeProvider.SetUtcNow(Now.AddMinutes(5));
        var newer = writer.AddSystem(AuditActions.UsageSynced, string.Empty, string.Empty, null);
        await Context.SaveChangesAsync();

        var page = await service.QueryAsync(new AuditLogQuery(null, null, null, null, null, null), new PagedRequest(), CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal([newer.AuditLogId, older.AuditLogId], page.Items.Select(i => i.AuditLogId));

        var system = page.Items[0];
        Assert.Null(system.ActorUserId);
        Assert.Null(system.ActorDisplayName);
        Assert.Null(system.TargetType);
        Assert.Null(system.TargetId);
        Assert.Null(system.Details);

        var human = page.Items[1];
        Assert.Equal(actor.UserId, human.ActorUserId);
        Assert.Equal("Grace Hopper", human.ActorDisplayName);
        Assert.Equal(AuditTargetTypes.Group, human.TargetType);
        Assert.Equal("1", human.TargetId);
        Assert.Equal("{\"name\":\"A\"}", human.Details);
    }

    /// <summary>Wires the real accessor + real writer over this test's context, as the DI container would per request.</summary>
    private AuditService CreateService(string oid)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimConstants.Oid, oid)], "TestAuth", nameType: ClaimConstants.Name, roleType: ClaimConstants.Roles);
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = new CurrentUserAccessor(new FixedHttpContextAccessor(httpContext), Context);
        return new AuditService(Context, new AuditWriter(Context, _timeProvider), accessor);
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
        await Context.SaveChangesAsync();
        return user;
    }
}
