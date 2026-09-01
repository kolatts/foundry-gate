using System.Text.Json;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Audit.Contracts;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Api.Services.Audit;

/// <summary>
/// Write-side contract of <see cref="AuditService"/>: actor attribution, JSON details, the
/// <see cref="TimeProvider"/> timestamp, and — the design decision worth pinning — that it adds to
/// the caller's context without saving, so mutation and audit row commit together.
/// </summary>
public class AuditServiceTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MutableTimeProvider _timeProvider = new(Now);
    private readonly StubCurrentUserAccessor _currentUser = new();

    [Fact]
    public async Task LogAsync_attributes_the_row_to_the_current_user_and_serializes_details_as_camelCase_json()
    {
        var actor = await SeedActorAsync();
        _currentUser.User = actor;
        var service = CreateService();

        var entry = await service.LogAsync(
            AuditActions.UserQuotaChanged,
            AuditTargetTypes.User,
            "42",
            new { Before = 1_000_000L, After = (long?)null, IsUnlimited = true },
            CancellationToken.None);
        await Context.SaveChangesAsync();

        var saved = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.AuditLogId == entry.AuditLogId);
        Assert.Equal(actor.UserId, saved.ActorUserId);
        Assert.Equal(AuditActions.UserQuotaChanged, saved.Action);
        Assert.Equal(AuditTargetTypes.User, saved.TargetType);
        Assert.Equal("42", saved.TargetId);

        using var details = JsonDocument.Parse(saved.Details);
        Assert.Equal(1_000_000L, details.RootElement.GetProperty("before").GetInt64());
        Assert.Equal(JsonValueKind.Null, details.RootElement.GetProperty("after").ValueKind);
        Assert.True(details.RootElement.GetProperty("isUnlimited").GetBoolean());
    }

    [Fact]
    public async Task LogAsync_stamps_OccurredDate_from_the_injected_TimeProvider()
    {
        _currentUser.User = await SeedActorAsync();
        var service = CreateService();
        var frozen = new DateTimeOffset(2031, 2, 3, 4, 5, 6, TimeSpan.Zero);
        _timeProvider.SetUtcNow(frozen);

        var entry = await service.LogAsync(AuditActions.UserActivated, AuditTargetTypes.User, "1", null, CancellationToken.None);
        await Context.SaveChangesAsync();

        Assert.Equal(frozen, entry.OccurredDate);
        var saved = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.AuditLogId == entry.AuditLogId);
        Assert.Equal(frozen, saved.OccurredDate); // survives the SQLite round-trip, too
    }

    [Fact]
    public async Task LogAsync_adds_to_the_context_but_does_not_save_so_the_caller_commits_mutation_and_audit_atomically()
    {
        _currentUser.User = await SeedActorAsync();
        var service = CreateService();

        var entry = await service.LogAsync(AuditActions.GroupCreated, AuditTargetTypes.Group, "7", null, CancellationToken.None);

        Assert.Equal(EntityState.Added, Context.Entry(entry).State);
        Assert.Equal(0, await Context.AuditLogs.AsNoTracking().CountAsync());

        await Context.SaveChangesAsync();

        Assert.Equal(1, await Context.AuditLogs.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task LogAsync_with_null_details_stores_an_empty_string_not_the_literal_null()
    {
        _currentUser.User = await SeedActorAsync();
        var service = CreateService();

        var entry = await service.LogAsync(AuditActions.UserDeactivated, AuditTargetTypes.User, "1", null, CancellationToken.None);

        Assert.Equal(string.Empty, entry.Details);
    }

    [Fact]
    public async Task LogAsync_throws_UnauthorizedAccessException_when_the_caller_has_no_User_row()
    {
        _currentUser.User = null;
        _currentUser.EntraObjectId = "no-such-user";
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.LogAsync(AuditActions.GroupCreated, AuditTargetTypes.Group, "7", null, CancellationToken.None));

        Assert.Contains("no-such-user", exception.Message, StringComparison.Ordinal);
        Assert.Empty(Context.ChangeTracker.Entries<AuditLog>());
    }

    [Fact]
    public async Task LogAsync_explicit_actor_overload_accepts_null_for_system_actors_and_never_touches_the_current_user()
    {
        // No User set and a throwing accessor: a system job (Functions) has no HttpContext at all.
        _currentUser.ThrowOnAccess = true;
        var service = CreateService();

        var entry = await service.LogAsync(
            actorUserId: null,
            AuditActions.QuotaMonthlyReset,
            string.Empty,
            string.Empty,
            new { PeriodYear = 2026, PeriodMonth = 9, AllocationsWritten = 12 },
            CancellationToken.None);
        await Context.SaveChangesAsync();

        var saved = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.AuditLogId == entry.AuditLogId);
        Assert.Null(saved.ActorUserId);
        Assert.Equal(AuditActions.QuotaMonthlyReset, saved.Action);
        Assert.Contains("\"periodMonth\":9", saved.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogAsync_explicit_actor_overload_records_the_given_UserId()
    {
        var actor = await SeedActorAsync();
        var service = CreateService();

        var entry = await service.LogAsync(actor.UserId, AuditActions.KeyRotated, AuditTargetTypes.ApiKey, actor.UserId.ToString(), null, CancellationToken.None);

        Assert.Equal(actor.UserId, entry.ActorUserId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task LogAsync_rejects_a_blank_action(string action)
    {
        _currentUser.User = await SeedActorAsync();
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.LogAsync(action, AuditTargetTypes.User, "1", null, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.LogAsync(null, action, AuditTargetTypes.User, "1", null, CancellationToken.None));
    }

    [Fact]
    public async Task QueryAsync_returns_newest_first_and_maps_empty_target_and_details_to_null()
    {
        var actor = await SeedActorAsync(displayName: "Grace Hopper");
        var service = CreateService();

        _timeProvider.SetUtcNow(Now);
        var older = await service.LogAsync(actor.UserId, AuditActions.GroupCreated, AuditTargetTypes.Group, "1", new { Name = "A" }, CancellationToken.None);
        _timeProvider.SetUtcNow(Now.AddMinutes(5));
        var newer = await service.LogAsync(null, AuditActions.UsageSynced, string.Empty, string.Empty, null, CancellationToken.None);
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

    private AuditService CreateService() => new(Context, _currentUser, _timeProvider);

    private async Task<User> SeedActorAsync(string displayName = "Ada Lovelace")
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

    /// <summary>Hand-rolled stub (no mocking library in this repo): returns a preset user, or throws like the real accessor does off-request.</summary>
    private sealed class StubCurrentUserAccessor : ICurrentUserAccessor
    {
        private string _entraObjectId = "stub-oid";

        public User? User { get; set; }

        public bool ThrowOnAccess { get; set; }

        public string EntraObjectId
        {
            get => ThrowOnAccess ? throw new UnauthorizedAccessException("no principal") : _entraObjectId;
            set => _entraObjectId = value;
        }

        public bool IsAdmin => ThrowOnAccess ? throw new UnauthorizedAccessException("no principal") : false;

        public string? DisplayName => User?.DisplayName;

        public string? Email => User?.Email;

        public Task<User?> TryGetUserAsync(CancellationToken cancellationToken) =>
            ThrowOnAccess
                ? throw new UnauthorizedAccessException("no principal")
                : Task.FromResult(User);

        public async Task<User> GetRequiredUserAsync(CancellationToken cancellationToken) =>
            await TryGetUserAsync(cancellationToken) ?? throw new KeyNotFoundException("no user");
    }
}
