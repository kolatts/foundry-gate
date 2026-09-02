using System.Globalization;
using System.Security.Claims;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Api.Services.Keys;
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
using Microsoft.Identity.Web;

namespace FoundryGate.Tests.Predeployment.Api.Services.Keys;

/// <summary>
/// <see cref="ApimKeyService"/> over a real SQLite context, the real audit path (<see cref="AuditService"/>
/// + <see cref="AuditWriter"/> + <see cref="CurrentUserAccessor"/>), the local Data Protection
/// protector, and <see cref="FakeApimManagementClient"/>. The acting caller is a seeded admin; the
/// target is a seeded developer.
/// </summary>
public class ApimKeyServiceTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly MutableTimeProvider _timeProvider = new(Now);
    private readonly FakeApimManagementClient _apim = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly DataProtectionKeyProtector _protector = new(new EphemeralDataProtectionProvider());

    [Fact]
    public async Task Provision_creates_the_subscription_under_the_tier_product_and_stores_only_ciphertext()
    {
        var (service, admin) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev One", "dev.one@contoso.test");
        var name = ApimSubscriptionNames.ForUser(developer.UserId);

        var reveal = await service.ProvisionAsync(developer, GatewayTiers.Power, CancellationToken.None);

        // APIM side
        Assert.Contains($"CreateOrUpdate:{name}:power", _apim.Calls);
        Assert.Equal("power", _apim.ProductOf(name));
        var apimKeys = _apim.KeysOf(name);
        Assert.Equal(apimKeys.PrimaryKey, reveal.PlaintextKey);

        // Response
        Assert.Equal($"{FakeApimManagementClient.ServiceId}/subscriptions/{name}", reveal.ApimSubscriptionId);
        Assert.Equal("••••••••" + apimKeys.PrimaryKey[^4..], reveal.MaskedKey);
        Assert.Equal(Now, reveal.IssuedDate);

        // Row: encrypted, never plaintext
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId);
        Assert.Equal(reveal.ApimSubscriptionId, saved.ApimSubscriptionId);
        Assert.StartsWith("dp1:", saved.ApimSubscriptionKey, StringComparison.Ordinal);
        Assert.DoesNotContain(reveal.PlaintextKey, saved.ApimSubscriptionKey, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(reveal.PlaintextKey, await _protector.UnprotectAsync(saved.ApimSubscriptionKey, CancellationToken.None));
        Assert.Equal(reveal.PlaintextKey[^4..], saved.ApimSubscriptionKeyHint);
        Assert.Equal(Now, saved.ApimKeyIssuedDate);
        Assert.True(saved.IsActive);

        // Audit: actor = admin, target = the developer's key, details carry no key material
        var audit = await SingleAuditAsync(AuditActions.KeyProvisioned, developer.UserId);
        Assert.Equal(admin.UserId, audit.ActorUserId);
        Assert.Contains("\"productId\":\"power\"", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"reusedOrphan\":false", audit.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(reveal.PlaintextKey, audit.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provision_display_name_carries_the_email_within_APIMs_100_char_limit()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", new string('x', 300) + "@contoso.test");

        _ = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);

        var subscription = await _apim.GetSubscriptionAsync(ApimSubscriptionNames.ForUser(developer.UserId), CancellationToken.None);
        Assert.NotNull(subscription);
        Assert.StartsWith("FoundryGate xxx", subscription.DisplayName, StringComparison.Ordinal);
        Assert.Equal(100, subscription.DisplayName.Length);
    }

    [Fact]
    public async Task Provision_accepts_tier_ids_case_insensitively_and_normalizes_them()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");

        _ = await service.ProvisionAsync(developer, " Standard ", CancellationToken.None);

        Assert.Equal("standard", _apim.ProductOf(ApimSubscriptionNames.ForUser(developer.UserId)));
    }

    [Fact]
    public async Task Provision_throws_Conflict_when_the_user_already_has_a_key_without_touching_APIM()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        _ = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);
        var callsBefore = _apim.Calls.Count;

        await Assert.ThrowsAsync<ConflictException>(() => service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None));

        Assert.Equal(callsBefore, _apim.Calls.Count);
    }

    [Theory]
    [InlineData("gold")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Provision_rejects_an_unknown_tier_before_calling_APIM(string tier)
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.ProvisionAsync(developer, tier, CancellationToken.None));

        Assert.Empty(_apim.Calls);
        Assert.Empty(Context.ChangeTracker.Entries<AuditLog>());
    }

    [Fact]
    public async Task Provision_refuses_an_unsaved_user()
    {
        var (service, _) = await CreateServiceAsync();
        var unsaved = new User { EntraObjectId = Guid.NewGuid().ToString(), DisplayName = "New", Email = "new@contoso.test" };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProvisionAsync(unsaved, GatewayTiers.Standard, CancellationToken.None));
    }

    [Fact]
    public async Task Provision_reuses_an_orphan_subscription_rescopes_it_and_regenerates_both_keys()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        var name = ApimSubscriptionNames.ForUser(developer.UserId);
        var orphanKeys = _apim.Seed(name, GatewayTiers.Power);

        var reveal = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);

        Assert.DoesNotContain(_apim.Calls, call => call.StartsWith("CreateOrUpdate:", StringComparison.Ordinal));
        Assert.Contains($"UpdateScope:{name}:standard", _apim.Calls);
        Assert.Contains($"RegeneratePrimary:{name}", _apim.Calls);
        Assert.Contains($"RegenerateSecondary:{name}", _apim.Calls);
        var current = _apim.KeysOf(name);
        Assert.NotEqual(orphanKeys.PrimaryKey, current.PrimaryKey);
        Assert.NotEqual(orphanKeys.SecondaryKey, current.SecondaryKey);
        Assert.Equal(current.PrimaryKey, reveal.PlaintextKey);
        Assert.Equal("standard", _apim.ProductOf(name));

        var audit = await SingleAuditAsync(AuditActions.KeyProvisioned, developer.UserId);
        Assert.Contains("\"reusedOrphan\":true", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provision_reusing_an_orphan_already_on_the_right_product_does_not_rescope()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        var name = ApimSubscriptionNames.ForUser(developer.UserId);
        _ = _apim.Seed(name, GatewayTiers.Standard);

        _ = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);

        Assert.DoesNotContain(_apim.Calls, call => call.StartsWith("UpdateScope:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Provision_when_APIM_fails_leaves_the_row_and_audit_trail_untouched()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        _apim.ThrowOnCreate = new InvalidOperationException("ARM said no");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None));

        Assert.Equal("ARM said no", exception.Message);
        Assert.Equal(string.Empty, developer.ApimSubscriptionId);
        Assert.Equal(string.Empty, developer.ApimSubscriptionKey);
        Assert.Empty(await Context.AuditLogs.AsNoTracking().Where(a => a.TargetId == developer.UserId.ToString()).ToListAsync());
        // The provisioning claim was written before APIM was called and must have rolled back with the transaction.
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId);
        Assert.Equal(string.Empty, saved.ApimSubscriptionId);
        Assert.Null(Context.Database.CurrentTransaction);

        // ...and the user is provisionable once APIM is healthy again.
        _apim.ThrowOnCreate = null;
        _ = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);
    }

    [Fact]
    public async Task Provision_throws_Conflict_when_a_concurrent_request_already_claimed_the_row_and_never_calls_APIM()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        // Another request (other scope) won the race: the database row now carries a claim while this
        // request's tracked entity still says "no key".
        _ = await Context.Users.Where(u => u.UserId == developer.UserId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.ApimSubscriptionId, "claimed-by-the-other-request"));
        Assert.Equal(string.Empty, developer.ApimSubscriptionId);

        await Assert.ThrowsAsync<ConflictException>(() => service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None));

        Assert.Empty(_apim.Calls);
        Assert.Null(Context.Database.CurrentTransaction);
    }

    [Fact]
    public async Task Provision_joins_a_transaction_the_caller_already_opened_instead_of_starting_its_own()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");

        await using var transaction = await Context.Database.BeginTransactionAsync();
        _ = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);
        Assert.Same(transaction, Context.Database.CurrentTransaction); // still ours, not committed by the service
        await transaction.RollbackAsync();

        Context.ChangeTracker.Clear();
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId);
        Assert.Equal(string.Empty, saved.ApimSubscriptionId); // the orchestrator's rollback took the provision with it
    }

    [Fact]
    public async Task Provision_replaces_an_orphan_that_is_not_active_instead_of_adopting_it()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        var name = ApimSubscriptionNames.ForUser(developer.UserId);
        _ = _apim.Seed(name, GatewayTiers.Standard);
        _apim.SetState(name, "suspended");

        var reveal = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);

        Assert.Contains($"Delete:{name}", _apim.Calls);
        Assert.Contains($"CreateOrUpdate:{name}:standard", _apim.Calls);
        Assert.DoesNotContain(_apim.Calls, call => call.StartsWith("Regenerate", StringComparison.Ordinal));
        var subscription = await _apim.GetSubscriptionAsync(name, CancellationToken.None);
        Assert.Equal("active", subscription!.State);
        Assert.Equal(_apim.KeysOf(name).PrimaryKey, reveal.PlaintextKey);
        var audit = await SingleAuditAsync(AuditActions.KeyProvisioned, developer.UserId);
        Assert.Contains("\"reusedOrphan\":false", audit.Details, StringComparison.Ordinal);
        Assert.Contains(_logs.Entries, entry => entry.Contains("suspended", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rotate_failure_after_APIM_regenerated_keeps_the_previous_ciphertext_logs_an_error_and_audits_rotation_failed()
    {
        var (service, admin) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        var provisioned = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);
        var envelopeBefore = developer.ApimSubscriptionKey;
        _apim.ThrowOnListSecrets = new IOException("ARM timed out");

        var exception = await Assert.ThrowsAsync<IOException>(() => service.RotateAsync(developer, CancellationToken.None));

        Assert.Equal("ARM timed out", exception.Message);
        // Row is self-consistent (previous values), even though APIM now holds different keys.
        Assert.Equal(envelopeBefore, developer.ApimSubscriptionKey);
        Assert.Equal(provisioned.PlaintextKey[^4..], developer.ApimSubscriptionKeyHint);
        Assert.Equal(provisioned.IssuedDate, developer.ApimKeyIssuedDate);
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId);
        Assert.Equal(envelopeBefore, saved.ApimSubscriptionKey);
        // Trail: no key.rotated, one key.rotation-failed naming the remedy, and an Error log.
        Assert.Empty(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.KeyRotated).ToListAsync());
        var failed = await SingleAuditAsync(AuditActions.KeyRotationFailed, developer.UserId);
        Assert.Equal(admin.UserId, failed.ActorUserId);
        Assert.Contains("rotate again", failed.Details, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"error\":\"IOException\"", failed.Details, StringComparison.Ordinal);
        Assert.Contains(_logs.Entries, entry => entry.Contains("STALE", StringComparison.Ordinal) && entry.Contains("Rotate again", StringComparison.Ordinal));

        // Remedy works: a second rotate stores a fresh, revealable key.
        _apim.ThrowOnListSecrets = null;
        var rotated = await service.RotateAsync(developer, CancellationToken.None);
        Assert.Equal(rotated.PlaintextKey, (await service.RevealAsync(developer, CancellationToken.None)).PlaintextKey);
    }

    [Fact]
    public async Task RevokeAsSystem_deletes_the_subscription_and_writes_a_system_audit_row_without_any_HTTP_caller()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        _ = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);
        var name = ApimSubscriptionNames.ForUser(developer.UserId);
        var callerless = CreateCallerlessService();

        // The caller-attributed variant cannot run here; the system variant can.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => callerless.RevokeAsync(developer, CancellationToken.None));
        Assert.True(_apim.Contains(name));

        var revoked = await callerless.RevokeAsSystemAsync(developer, "entra-departure", CancellationToken.None);

        Assert.True(revoked);
        Assert.False(_apim.Contains(name));
        Assert.Equal(string.Empty, developer.ApimSubscriptionId);
        Assert.True(developer.IsActive);
        var audit = await SingleAuditAsync(AuditActions.KeyRevoked, developer.UserId);
        Assert.Null(audit.ActorUserId);
        Assert.Contains("\"reason\":\"entra-departure\"", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"existedInApim\":true", audit.Details, StringComparison.Ordinal);

        Assert.False(await callerless.RevokeAsSystemAsync(developer, "entra-departure", CancellationToken.None)); // idempotent
        await Assert.ThrowsAnyAsync<ArgumentException>(() => callerless.RevokeAsSystemAsync(developer, " ", CancellationToken.None));
    }

    [Fact]
    public async Task Reveal_refuses_a_row_that_has_a_key_but_no_issued_date_rather_than_inventing_one()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        _ = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);
        developer.ApimKeyIssuedDate = null;

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RevealAsync(developer, CancellationToken.None));

        Assert.Empty(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.KeyRevealed).ToListAsync());
    }

    [Fact]
    public async Task Rotate_regenerates_both_APIM_keys_stores_the_new_primary_and_audits()
    {
        var (service, admin) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        var first = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);
        var name = ApimSubscriptionNames.ForUser(developer.UserId);
        var keysBefore = _apim.KeysOf(name);
        var envelopeBefore = developer.ApimSubscriptionKey;
        _timeProvider.Advance(TimeSpan.FromDays(3));

        var rotated = await service.RotateAsync(developer, CancellationToken.None);

        var keysAfter = _apim.KeysOf(name);
        Assert.NotEqual(keysBefore.PrimaryKey, keysAfter.PrimaryKey);
        Assert.NotEqual(keysBefore.SecondaryKey, keysAfter.SecondaryKey);
        Assert.Equal(keysAfter.PrimaryKey, rotated.PlaintextKey);
        Assert.NotEqual(first.PlaintextKey, rotated.PlaintextKey);
        Assert.Equal(first.ApimSubscriptionId, rotated.ApimSubscriptionId);
        Assert.Equal(Now.AddDays(3), rotated.IssuedDate);

        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId);
        Assert.NotEqual(envelopeBefore, saved.ApimSubscriptionKey);
        Assert.Equal(rotated.PlaintextKey, await _protector.UnprotectAsync(saved.ApimSubscriptionKey, CancellationToken.None));
        Assert.Equal(rotated.PlaintextKey[^4..], saved.ApimSubscriptionKeyHint);
        Assert.Equal(Now.AddDays(3), saved.ApimKeyIssuedDate);

        var audit = await SingleAuditAsync(AuditActions.KeyRotated, developer.UserId);
        Assert.Equal(admin.UserId, audit.ActorUserId);
        Assert.Contains("\"keysRegenerated\":[\"primary\",\"secondary\"]", audit.Details, StringComparison.Ordinal);
        Assert.DoesNotContain(rotated.PlaintextKey, audit.Details, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(first.PlaintextKey, audit.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rotate_without_a_key_throws_KeyNotFound()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RotateAsync(developer, CancellationToken.None));

        Assert.Empty(_apim.Calls);
    }

    [Fact]
    public async Task Rotate_when_the_subscription_vanished_from_APIM_throws_Conflict_with_reprovision_guidance()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        _ = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);
        Assert.True(_apim.Remove(ApimSubscriptionNames.ForUser(developer.UserId)));

        var exception = await Assert.ThrowsAsync<ConflictException>(() => service.RotateAsync(developer, CancellationToken.None));

        Assert.Contains($"DELETE /keys/{developer.UserId}", exception.Message, StringComparison.Ordinal);
        Assert.IsType<ApimSubscriptionNotFoundException>(exception.InnerException);
    }

    [Fact]
    public async Task Revoke_deletes_the_subscription_clears_every_key_field_keeps_the_user_active_and_audits()
    {
        var (service, admin) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        var provisioned = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);
        var name = ApimSubscriptionNames.ForUser(developer.UserId);

        var revoked = await service.RevokeAsync(developer, CancellationToken.None);

        Assert.True(revoked);
        Assert.False(_apim.Contains(name));
        var saved = await Context.Users.AsNoTracking().SingleAsync(u => u.UserId == developer.UserId);
        Assert.Equal(string.Empty, saved.ApimSubscriptionId);
        Assert.Equal(string.Empty, saved.ApimSubscriptionKey);
        Assert.Equal(string.Empty, saved.ApimSubscriptionKeyHint);
        Assert.Null(saved.ApimKeyIssuedDate);
        Assert.True(saved.IsActive); // key-only revocation (#116)

        var audit = await SingleAuditAsync(AuditActions.KeyRevoked, developer.UserId);
        Assert.Equal(admin.UserId, audit.ActorUserId);
        Assert.Contains(provisioned.ApimSubscriptionId, audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"existedInApim\":true", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Revoke_without_a_key_is_a_no_op()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");

        var revoked = await service.RevokeAsync(developer, CancellationToken.None);

        Assert.False(revoked);
        Assert.Empty(_apim.Calls);
        Assert.Empty(await Context.AuditLogs.AsNoTracking().Where(a => a.TargetId == developer.UserId.ToString()).ToListAsync());
    }

    [Fact]
    public async Task Revoke_when_APIM_already_lost_the_subscription_still_clears_the_row_and_audits()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        _ = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);
        Assert.True(_apim.Remove(ApimSubscriptionNames.ForUser(developer.UserId)));

        var revoked = await service.RevokeAsync(developer, CancellationToken.None);

        Assert.True(revoked);
        Assert.Equal(string.Empty, developer.ApimSubscriptionId);
        var audit = await SingleAuditAsync(AuditActions.KeyRevoked, developer.UserId);
        Assert.Contains("\"existedInApim\":false", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_revoke_the_user_can_be_provisioned_again_with_a_fresh_key()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        var first = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);
        _ = await service.RevokeAsync(developer, CancellationToken.None);

        var second = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);

        Assert.NotEqual(first.PlaintextKey, second.PlaintextKey);
        Assert.Equal(first.ApimSubscriptionId, second.ApimSubscriptionId); // same name → same resource id
    }

    [Fact]
    public async Task GetMasked_shows_exactly_the_last_four_characters_and_nothing_else_of_the_key()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        var reveal = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);

        var masked = service.GetMasked(developer);

        Assert.True(masked.IsProvisioned);
        Assert.Equal(reveal.ApimSubscriptionId, masked.ApimSubscriptionId);
        Assert.NotNull(masked.MaskedKey);
        Assert.EndsWith(reveal.PlaintextKey[^4..], masked.MaskedKey, StringComparison.Ordinal);
        Assert.Equal("••••••••" + reveal.PlaintextKey[^4..], masked.MaskedKey);
        // No 5-character window of the key survives into the masked form.
        for (var i = 0; i + 5 <= reveal.PlaintextKey.Length; i++)
        {
            Assert.DoesNotContain(reveal.PlaintextKey.Substring(i, 5), masked.MaskedKey, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task GetMasked_for_an_unprovisioned_user_reports_not_provisioned()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");

        var masked = service.GetMasked(developer);

        Assert.False(masked.IsProvisioned);
        Assert.Null(masked.MaskedKey);
        Assert.Null(masked.ApimSubscriptionId);
    }

    [Fact]
    public async Task Reveal_decrypts_the_stored_key_and_audits_without_calling_APIM()
    {
        var (service, admin) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        var provisioned = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);
        var callsBefore = _apim.Calls.Count;
        _timeProvider.Advance(TimeSpan.FromHours(1));

        var revealed = await service.RevealAsync(developer, CancellationToken.None);

        Assert.Equal(provisioned.PlaintextKey, revealed.PlaintextKey);
        Assert.Equal(provisioned.MaskedKey, revealed.MaskedKey);
        Assert.Equal(provisioned.IssuedDate, revealed.IssuedDate); // when it was minted, not when it was revealed
        Assert.Equal(callsBefore, _apim.Calls.Count);
        var audit = await SingleAuditAsync(AuditActions.KeyRevealed, developer.UserId);
        Assert.Equal(admin.UserId, audit.ActorUserId);
        Assert.DoesNotContain(revealed.PlaintextKey, audit.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reveal_without_a_key_throws_KeyNotFound()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RevealAsync(developer, CancellationToken.None));
    }

    [Fact]
    public async Task MoveToProduct_rescopes_the_subscription_and_audits_before_and_after()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        _ = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);
        var name = ApimSubscriptionNames.ForUser(developer.UserId);
        var keysBefore = _apim.KeysOf(name);

        await service.MoveToProductAsync(developer, GatewayTiers.Unlimited, CancellationToken.None);

        Assert.Equal("unlimited", _apim.ProductOf(name));
        Assert.Equal(keysBefore, _apim.KeysOf(name)); // keys untouched by a tier move

        // The row is added, not saved (#156 review): this runs inside quota resolution, in the middle of
        // a caller's unit of work, so saving here would commit that caller's half-finished mutation.
        Assert.Contains(Context.ChangeTracker.Entries<AuditLog>(), e => e.Entity.Action == AuditActions.KeyTierChanged && e.State == EntityState.Added);
        _ = await Context.SaveChangesAsync();

        var audit = await SingleAuditAsync(AuditActions.KeyTierChanged, developer.UserId);
        Assert.Contains("\"before\":\"standard\"", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"after\":\"unlimited\"", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoveToProduct_to_the_current_product_is_a_no_op()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        _ = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);

        await service.MoveToProductAsync(developer, "STANDARD", CancellationToken.None);

        Assert.DoesNotContain(_apim.Calls, call => call.StartsWith("UpdateScope:", StringComparison.Ordinal));
        Assert.Empty(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.KeyTierChanged).ToListAsync());
    }

    [Fact]
    public async Task MoveToProduct_without_a_key_throws_KeyNotFound_and_with_a_bad_tier_throws_Argument()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.MoveToProductAsync(developer, GatewayTiers.Power, CancellationToken.None));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.MoveToProductAsync(developer, "gold", CancellationToken.None));
    }

    [Fact]
    public async Task MoveToProduct_when_the_subscription_vanished_throws_Conflict()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        _ = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);
        Assert.True(_apim.Remove(ApimSubscriptionNames.ForUser(developer.UserId)));

        await Assert.ThrowsAsync<ConflictException>(() => service.MoveToProductAsync(developer, GatewayTiers.Power, CancellationToken.None));
    }

    [Fact]
    public async Task ForUser_operations_refuse_an_unknown_user()
    {
        var (service, _) = await CreateServiceAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RotateForUserAsync(999_999, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RevokeForUserAsync(999_999, CancellationToken.None));
        Assert.Empty(_apim.Calls);
    }

    [Fact]
    public async Task Mine_operations_act_on_the_caller_and_refuse_a_deactivated_caller()
    {
        var (service, admin) = await CreateServiceAsync();
        _ = await service.ProvisionAsync(admin, GatewayTiers.Standard, CancellationToken.None);

        var mine = await service.GetMineAsync(CancellationToken.None);
        var revealed = await service.RevealMineAsync(CancellationToken.None);
        var rotated = await service.RotateMineAsync(CancellationToken.None);

        Assert.True(mine.IsProvisioned);
        Assert.NotEqual(revealed.PlaintextKey, rotated.PlaintextKey);

        admin.IsActive = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RevealMineAsync(CancellationToken.None));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RotateMineAsync(CancellationToken.None));
        Assert.True((await service.GetMineAsync(CancellationToken.None)).IsProvisioned); // reading the masked form is still fine
    }

    [Fact]
    public async Task Plaintext_key_material_never_reaches_the_logger_or_an_audit_row()
    {
        var (service, _) = await CreateServiceAsync();
        var developer = await SeedUserAsync("Dev", "d@contoso.test");
        var name = ApimSubscriptionNames.ForUser(developer.UserId);

        var provisioned = await service.ProvisionAsync(developer, GatewayTiers.Standard, CancellationToken.None);
        var secondaryAfterProvision = _apim.KeysOf(name).SecondaryKey;
        var revealed = await service.RevealAsync(developer, CancellationToken.None);
        var rotated = await service.RotateAsync(developer, CancellationToken.None);
        var secondaryAfterRotate = _apim.KeysOf(name).SecondaryKey;
        await service.MoveToProductAsync(developer, GatewayTiers.Power, CancellationToken.None);
        _ = await service.RevokeAsync(developer, CancellationToken.None);

        string[] secrets = [provisioned.PlaintextKey, revealed.PlaintextKey, rotated.PlaintextKey, secondaryAfterProvision, secondaryAfterRotate];
        Assert.NotEmpty(_logs.Entries);
        var auditDetails = await Context.AuditLogs.AsNoTracking().Select(a => a.Details).ToListAsync();
        Assert.NotEmpty(auditDetails);

        foreach (var secret in secrets)
        {
            Assert.All(_logs.Entries, entry => Assert.DoesNotContain(secret, entry, StringComparison.OrdinalIgnoreCase));
            Assert.All(auditDetails, details => Assert.DoesNotContain(secret, details, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Wires the real accessor + audit path over this test's context as the DI container would per request, acting as a seeded admin.</summary>
    private async Task<(ApimKeyService Service, User Admin)> CreateServiceAsync()
    {
        var admin = await SeedUserAsync("Ada Admin", "ada@contoso.test");
        var identity = new ClaimsIdentity(
            [new Claim(ClaimConstants.Oid, admin.EntraObjectId), new Claim(ClaimConstants.Roles, RoleNames.Admin)],
            "TestAuth",
            nameType: ClaimConstants.Name,
            roleType: ClaimConstants.Roles);
        var accessor = new CurrentUserAccessor(new FixedHttpContextAccessor(new DefaultHttpContext { User = new ClaimsPrincipal(identity) }), Context);
        var writer = new AuditWriter(Context, _timeProvider);
        var audit = new AuditService(Context, writer, accessor);

        var service = new ApimKeyService(Context, _apim, _protector, audit, writer, accessor, _timeProvider, _logs.CreateLogger<ApimKeyService>());
        return (service, admin);
    }

    /// <summary>A service with no HTTP caller at all — what a sync job or a Functions host would build.</summary>
    private ApimKeyService CreateCallerlessService()
    {
        var accessor = new CurrentUserAccessor(new FixedHttpContextAccessor(null), Context);
        var writer = new AuditWriter(Context, _timeProvider);
        var audit = new AuditService(Context, writer, accessor);
        return new ApimKeyService(Context, _apim, _protector, audit, writer, accessor, _timeProvider, _logs.CreateLogger<ApimKeyService>());
    }

    private async Task<User> SeedUserAsync(string displayName, string email, bool isActive = true)
    {
        var user = new User
        {
            EntraObjectId = Guid.NewGuid().ToString(),
            DisplayName = displayName,
            Email = email,
            IsActive = isActive,
        };
        Context.Users.Add(user);
        await Context.SaveChangesAsync();
        return user;
    }

    private async Task<AuditLog> SingleAuditAsync(string action, int userId)
    {
        var targetId = userId.ToString(CultureInfo.InvariantCulture);
        return await Context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == action && a.TargetType == AuditTargetTypes.ApiKey && a.TargetId == targetId);
    }
}
