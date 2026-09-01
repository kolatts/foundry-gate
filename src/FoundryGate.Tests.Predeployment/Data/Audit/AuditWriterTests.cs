using System.Text.Json;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Data.Audit;

/// <summary>
/// The host-agnostic audit writer every host shares: actor attribution (navigation vs id vs none),
/// JSON details, the <see cref="TimeProvider"/> timestamp, and — the design decision worth pinning —
/// that it adds to the caller's context without saving, so mutation and audit row commit together.
/// </summary>
public class AuditWriterTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MutableTimeProvider _timeProvider = new(Now);

    [Fact]
    public async Task Add_with_an_unsaved_actor_attributes_the_row_once_the_caller_saves_the_auto_provision_pattern()
    {
        // The exact GET /users/me (#28) sequence: Add the new user, audit it, save once.
        var newUser = NewUser("New Joiner");
        Context.Users.Add(newUser);
        var writer = CreateWriter();

        var entry = writer.Add(newUser, AuditActions.UserProvisioned, AuditTargetTypes.User, string.Empty, new { newUser.DisplayName });
        Assert.Equal(0, newUser.UserId); // still unsaved — nothing to put in ActorUserId yet
        await Context.SaveChangesAsync();

        Assert.NotEqual(0, newUser.UserId);
        var saved = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.AuditLogId == entry.AuditLogId);
        Assert.Equal(newUser.UserId, saved.ActorUserId);
        Assert.Equal(AuditActions.UserProvisioned, saved.Action);
    }

    [Fact]
    public async Task Add_with_a_saved_actor_serializes_details_as_camelCase_json()
    {
        var actor = await SeedActorAsync();
        var writer = CreateWriter();

        var entry = writer.Add(
            actor,
            AuditActions.UserQuotaChanged,
            AuditTargetTypes.User,
            "42",
            new { Before = 1_000_000L, After = (long?)null, IsUnlimited = true });
        await Context.SaveChangesAsync();

        var saved = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.AuditLogId == entry.AuditLogId);
        Assert.Equal(actor.UserId, saved.ActorUserId);
        Assert.Equal(AuditTargetTypes.User, saved.TargetType);
        Assert.Equal("42", saved.TargetId);

        using var details = JsonDocument.Parse(saved.Details);
        Assert.Equal(1_000_000L, details.RootElement.GetProperty("before").GetInt64());
        Assert.Equal(JsonValueKind.Null, details.RootElement.GetProperty("after").ValueKind);
        Assert.True(details.RootElement.GetProperty("isUnlimited").GetBoolean());
    }

    [Fact]
    public async Task Add_by_id_records_the_given_UserId()
    {
        var actor = await SeedActorAsync();
        var writer = CreateWriter();

        var entry = writer.Add(actor.UserId, AuditActions.KeyRotated, AuditTargetTypes.ApiKey, actor.UserId.ToString(), null);
        await Context.SaveChangesAsync();

        var saved = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.AuditLogId == entry.AuditLogId);
        Assert.Equal(actor.UserId, saved.ActorUserId);
    }

    [Fact]
    public async Task AddSystem_records_a_null_actor()
    {
        var writer = CreateWriter();

        var entry = writer.AddSystem(
            AuditActions.QuotaMonthlyReset,
            string.Empty,
            string.Empty,
            new { PeriodYear = 2026, PeriodMonth = 9, AllocationsWritten = 12 });
        await Context.SaveChangesAsync();

        var saved = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.AuditLogId == entry.AuditLogId);
        Assert.Null(saved.ActorUserId);
        Assert.Equal(AuditActions.QuotaMonthlyReset, saved.Action);
        Assert.Contains("\"periodMonth\":9", saved.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_stamps_OccurredDate_from_the_injected_TimeProvider_and_it_survives_the_round_trip()
    {
        var writer = CreateWriter();
        var frozen = new DateTimeOffset(2031, 2, 3, 4, 5, 6, TimeSpan.Zero);
        _timeProvider.SetUtcNow(frozen);

        var entry = writer.AddSystem(AuditActions.UsageSynced, string.Empty, string.Empty, null);
        await Context.SaveChangesAsync();

        Assert.Equal(frozen, entry.OccurredDate);
        var saved = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.AuditLogId == entry.AuditLogId);
        Assert.Equal(frozen, saved.OccurredDate);
    }

    [Fact]
    public async Task Add_adds_to_the_context_but_does_not_save_so_the_caller_commits_mutation_and_audit_atomically()
    {
        var writer = CreateWriter();

        var entry = writer.AddSystem(AuditActions.GroupCreated, AuditTargetTypes.Group, "7", null);

        Assert.Equal(EntityState.Added, Context.Entry(entry).State);
        Assert.Equal(0, await Context.AuditLogs.AsNoTracking().CountAsync());

        await Context.SaveChangesAsync();

        Assert.Equal(1, await Context.AuditLogs.AsNoTracking().CountAsync());
    }

    [Fact]
    public void Add_with_null_details_stores_an_empty_string_not_the_literal_null()
    {
        var writer = CreateWriter();

        var entry = writer.AddSystem(AuditActions.UserDeactivated, AuditTargetTypes.User, "1", null);

        Assert.Equal(string.Empty, entry.Details);
    }

    [Fact]
    public void Add_tolerates_object_cycles_in_details_instead_of_throwing()
    {
        // A tracked entity graph (Group <-> GroupMember <-> Group) is the realistic way a caller
        // passes a cycle by accident. Default System.Text.Json throws; that would turn the caller's
        // mutation into an unmapped 500.
        var group = new Group { Name = "Cyclic" };
        var member = new GroupMember { Group = group, User = NewUser("Member") };
        group.GroupMemberships.Add(member);
        var writer = CreateWriter();

        var entry = writer.AddSystem(AuditActions.GroupUpdated, AuditTargetTypes.Group, "1", group);

        Assert.Contains("\"name\":\"Cyclic\"", entry.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Add_rejects_a_blank_action_on_every_overload(string action)
    {
        var writer = CreateWriter();
        var actor = NewUser("Anyone");

        Assert.Throws<ArgumentException>(() => writer.Add(actor, action, AuditTargetTypes.User, "1", null));
        Assert.Throws<ArgumentException>(() => writer.Add(1, action, AuditTargetTypes.User, "1", null));
        Assert.Throws<ArgumentException>(() => writer.AddSystem(action, AuditTargetTypes.User, "1", null));
    }

    private AuditWriter CreateWriter() => new(Context, _timeProvider);

    private async Task<User> SeedActorAsync()
    {
        var user = NewUser("Ada Lovelace");
        Context.Users.Add(user);
        await Context.SaveChangesAsync();
        return user;
    }

    private static User NewUser(string displayName) => new()
    {
        EntraObjectId = Guid.NewGuid().ToString(),
        DisplayName = displayName,
        Email = $"{Guid.NewGuid():N}@contoso.test",
    };
}
