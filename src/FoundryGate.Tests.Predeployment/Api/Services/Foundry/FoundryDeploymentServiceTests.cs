using System.Security.Claims;
using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Foundry;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Core.Configuration;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Exceptions;
using FoundryGate.Domain.Foundry;
using FoundryGate.Domain.Foundry.Contracts;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Web;

namespace FoundryGate.Tests.Predeployment.Api.Services.Foundry;

/// <summary>
/// The provisioning service's own rules, against the in-memory ARM fake and a real
/// <see cref="AuditService"/>/<see cref="CurrentUserAccessor"/> over SQLite: multi-account
/// aggregation, the developer view's de-duplication and cache, create-once (409 before any PUT),
/// the Anthropic refusals on create <em>and</em> delete (#126), missing-account vs
/// missing-deployment (503 vs 404; skipped on the developer view), that every refusal happens
/// <em>before</em> ARM is touched, and that the audit row survives a client cancelling after ARM
/// accepted.
/// </summary>
public class FoundryDeploymentServiceTests : InMemoryDatabaseTest
{
    private const string Primary = "fg-eus2";
    private const string Secondary = "fg-swc";
    private const string Missing = "fg-decommissioned";

    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeFoundryManagementClient _client = new();
    private readonly MutableTimeProvider _timeProvider = new(Now);
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public FoundryDeploymentServiceTests()
    {
        _client.AddAccount(Primary);
        _client.AddAccount(Secondary);
    }

    [Fact]
    public async Task ListDeploymentsAsync_aggregates_every_configured_account_primary_first_then_by_name()
    {
        _client.Seed(Secondary, "claude-haiku-4-5", "Anthropic", "claude-haiku-4-5", "20251001", capacity: 5);
        _client.Seed(Primary, "gpt-4-1-mini");
        _client.Seed(Primary, "claude-haiku-4-5", "Anthropic", "claude-haiku-4-5", "20251001", capacity: 5);
        var service = CreateService(oid: null);

        var deployments = await service.ListDeploymentsAsync(CancellationToken.None);

        Assert.Equal(
            [(Primary, "claude-haiku-4-5"), (Primary, "gpt-4-1-mini"), (Secondary, "claude-haiku-4-5")],
            deployments.Select(d => (d.AccountName, d.DeploymentName)));
    }

    [Fact]
    public async Task ListDeploymentsAsync_throws_FeatureNotConfigured_when_the_Gateway_section_is_absent()
    {
        var service = CreateService(oid: null, configured: false);

        var exception = await Assert.ThrowsAsync<FeatureNotConfiguredException>(() => service.ListDeploymentsAsync(CancellationToken.None));

        Assert.Contains("Gateway:FoundryAccountNames", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListDeploymentsAsync_throws_FeatureNotConfigured_naming_a_configured_account_Azure_does_not_have()
    {
        _client.Seed(Primary, "gpt-4-1-mini");
        var service = CreateService(oid: null, accounts: [Primary, Missing]);

        var exception = await Assert.ThrowsAsync<FeatureNotConfiguredException>(() => service.ListDeploymentsAsync(CancellationToken.None));

        Assert.Contains(Missing, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("rg-", exception.Message, StringComparison.Ordinal); // never the resource group
    }

    [Fact]
    public async Task ListModelsAsync_lists_a_pooled_deployment_once_and_prefers_a_Succeeded_region()
    {
        _client.Seed(Primary, "claude-sonnet-4-5", "Anthropic", "claude-sonnet-4-5", "20250929", provisioningState: "Creating");
        _client.Seed(Secondary, "claude-sonnet-4-5", "Anthropic", "claude-sonnet-4-5", "20250929", provisioningState: "Succeeded");
        _client.Seed(Primary, "gpt-4-1-mini", provisioningState: "Creating");
        _client.Seed(Secondary, "gpt-4-1-mini", provisioningState: "Failed");
        var service = CreateService(oid: null);

        var models = await service.ListModelsAsync(CancellationToken.None);

        Assert.Equal(2, models.Count);
        var sonnet = Assert.Single(models, m => m.DeploymentName == "claude-sonnet-4-5");
        Assert.Equal("Succeeded", sonnet.ProvisioningState);
        Assert.Equal("Anthropic", sonnet.ModelFormat);
        var gpt = Assert.Single(models, m => m.DeploymentName == "gpt-4-1-mini");
        Assert.Equal("Creating", gpt.ProvisioningState); // no region Succeeded → the primary's state
    }

    [Fact]
    public async Task ListModelsAsync_skips_a_missing_account_and_serves_the_rest()
    {
        _client.Seed(Primary, "gpt-4-1-mini");
        var service = CreateService(oid: null, accounts: [Primary, Missing]);

        var models = await service.ListModelsAsync(CancellationToken.None);

        var model = Assert.Single(models);
        Assert.Equal("gpt-4-1-mini", model.DeploymentName);
    }

    [Fact]
    public async Task ListModelsAsync_is_served_from_cache_until_a_create_or_delete_invalidates_it()
    {
        _client.Seed(Primary, "gpt-4-1-mini");
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId);

        _ = await service.ListModelsAsync(CancellationToken.None);
        _ = await service.ListModelsAsync(CancellationToken.None);
        Assert.Equal(2, _client.ListCalls.Count); // one per account, once — the second call was cached

        _ = await service.CreateDeploymentAsync(Request(Primary, "gpt-4-1-nano") with { ModelName = "gpt-4.1-nano" }, CancellationToken.None);
        var afterCreate = await service.ListModelsAsync(CancellationToken.None);
        Assert.Equal(4, _client.ListCalls.Count);
        Assert.Contains(afterCreate, m => m.DeploymentName == "gpt-4-1-nano");

        await service.DeleteDeploymentAsync(Primary, "gpt-4-1-nano", CancellationToken.None);
        var afterDelete = await service.ListModelsAsync(CancellationToken.None);
        Assert.Equal(6, _client.ListCalls.Count);
        Assert.DoesNotContain(afterDelete, m => m.DeploymentName == "gpt-4-1-nano");
    }

    [Fact]
    public async Task ListModelsAsync_cache_entry_expires_after_the_configured_duration()
    {
        // MemoryCache's expiry is wall-clock based; assert the entry is registered with the intended TTL
        // rather than sleeping: the absolute expiration must be set and not longer than the constant.
        _client.Seed(Primary, "gpt-4-1-mini");
        var service = CreateService(oid: null);

        _ = await service.ListModelsAsync(CancellationToken.None);

        Assert.True(_cache.TryGetValue(FoundryDeploymentService.ModelsCacheKey, out IReadOnlyList<FoundryModelResponse>? cached));
        Assert.NotNull(cached);
        Assert.Equal(TimeSpan.FromSeconds(30), FoundryDeploymentService.ModelsCacheDuration);
    }

    [Fact]
    public async Task GetDeploymentAsync_distinguishes_missing_deployment_404_from_missing_account_503_and_unconfigured_account_404()
    {
        _client.Seed(Primary, "gpt-4-1-mini");
        var service = CreateService(oid: null, accounts: [Primary, Secondary, Missing]);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetDeploymentAsync(Primary, "nope", CancellationToken.None));
        await Assert.ThrowsAsync<FeatureNotConfiguredException>(() => service.GetDeploymentAsync(Missing, "gpt-4-1-mini", CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetDeploymentAsync("someone-elses-account", "gpt-4-1-mini", CancellationToken.None));
    }

    [Fact]
    public async Task CreateDeploymentAsync_creates_an_OpenAI_deployment_and_audits_it_in_one_save()
    {
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId);
        // Account name in a different case than configured: resolved to the configured spelling.
        var request = Request(accountName: Primary.ToUpperInvariant(), deploymentName: "gpt-4-1-mini");

        var created = await service.CreateDeploymentAsync(request, CancellationToken.None);

        Assert.Equal(Primary, created.AccountName);
        Assert.Equal("gpt-4-1-mini", created.DeploymentName);
        Assert.Equal("OpenAI", created.ModelFormat);
        Assert.Equal("Creating", created.ProvisioningState);
        var call = Assert.Single(_client.CreateCalls);
        Assert.Equal(Primary, call.AccountName);

        var audit = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.FoundryDeploymentCreated);
        Assert.Equal(actor.UserId, audit.ActorUserId);
        Assert.Equal(AuditTargetTypes.FoundryDeployment, audit.TargetType);
        Assert.Equal($"{Primary}/gpt-4-1-mini", audit.TargetId);
        Assert.Contains("\"modelName\":\"gpt-4.1-mini\"", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"capacity\":10", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateDeploymentAsync_still_audits_when_the_caller_cancels_after_ARM_accepted()
    {
        // The reviewer's probe: a client that drops during the ARM call. Past the commit point the audit
        // row and save must not observe the request token — an accepted deployment is never unaudited.
        var actor = await SeedUserAsync("Ada Lovelace");
        using var cts = new CancellationTokenSource();
        _client.OnCreate = _ => cts.Cancel();
        var service = CreateService(actor.EntraObjectId);

        var created = await service.CreateDeploymentAsync(Request(Primary, "gpt-4-1-mini"), cts.Token);

        Assert.NotNull(await _client.GetDeploymentAsync(Primary, "gpt-4-1-mini", CancellationToken.None));
        var audit = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.FoundryDeploymentCreated);
        Assert.Equal($"{Primary}/{created.DeploymentName}", audit.TargetId);
    }

    [Fact]
    public async Task DeleteDeploymentAsync_still_audits_when_the_caller_cancels_after_ARM_accepted()
    {
        _client.Seed(Primary, "gpt-4-1-mini");
        var actor = await SeedUserAsync("Ada Lovelace");
        using var cts = new CancellationTokenSource();
        _client.OnDelete = (_, _) => cts.Cancel();
        var service = CreateService(actor.EntraObjectId);

        await service.DeleteDeploymentAsync(Primary, "gpt-4-1-mini", cts.Token);

        Assert.Null(await _client.GetDeploymentAsync(Primary, "gpt-4-1-mini", CancellationToken.None));
        Assert.Single(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.FoundryDeploymentDeleted).ToListAsync());
    }

    [Fact]
    public async Task CreateDeploymentAsync_returns_409_for_an_existing_name_without_ever_reaching_ARM()
    {
        // CLAUDE.md / E-006: never re-PUT an existing deployment. The fake would accept a "create" of an
        // existing name only by throwing; the service must not even ask.
        _client.Seed(Primary, "gpt-4-1-mini", provisioningState: "Succeeded");
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId);

        var exception = await Assert.ThrowsAsync<ConflictException>(() =>
            service.CreateDeploymentAsync(Request(Primary, "GPT-4-1-MINI"), CancellationToken.None));

        Assert.Contains("already exists", exception.Message, StringComparison.Ordinal);
        Assert.Empty(_client.CreateCalls);
        Assert.Empty(Context.ChangeTracker.Entries<AuditLog>());
    }

    [Fact]
    public async Task CreateDeploymentAsync_refuses_Anthropic_format_before_any_ARM_call()
    {
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId);
        var request = Request(Primary, "claude-haiku-4-5") with
        {
            ModelFormat = FoundryModelFormatType.Anthropic,
            ModelName = "claude-haiku-4-5",
            ModelVersion = "20251001",
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateDeploymentAsync(request, CancellationToken.None));

        Assert.Contains("#107", exception.Message, StringComparison.Ordinal);
        Assert.Contains("#126", exception.Message, StringComparison.Ordinal);
        Assert.Contains("modelProviderData", exception.Message, StringComparison.Ordinal);
        Assert.Empty(_client.CreateCalls);
        Assert.Empty(Context.ChangeTracker.Entries<AuditLog>());
    }

    [Fact]
    public async Task CreateDeploymentAsync_rejects_an_account_that_is_not_configured()
    {
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateDeploymentAsync(Request("not-ours", "gpt-4-1-mini"), CancellationToken.None));

        Assert.Contains("not-ours", exception.Message, StringComparison.Ordinal);
        Assert.Contains(Primary, exception.Message, StringComparison.Ordinal);
        Assert.Empty(_client.CreateCalls);
    }

    [Fact]
    public async Task CreateDeploymentAsync_throws_FeatureNotConfigured_when_the_configured_account_is_missing_in_Azure()
    {
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId, accounts: [Primary, Missing]);

        await Assert.ThrowsAsync<FeatureNotConfiguredException>(() =>
            service.CreateDeploymentAsync(Request(Missing, "gpt-4-1-mini"), CancellationToken.None));

        Assert.Empty(_client.CreateCalls);
        Assert.Empty(Context.ChangeTracker.Entries<AuditLog>());
    }

    [Fact]
    public async Task CreateDeploymentAsync_refuses_an_unprovisioned_caller_before_any_ARM_call()
    {
        var service = CreateService("no-user-row");

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CreateDeploymentAsync(Request(Primary, "gpt-4-1-mini"), CancellationToken.None));

        Assert.Contains("GET /users/me", exception.Message, StringComparison.Ordinal);
        Assert.Empty(_client.CreateCalls);
    }

    [Fact]
    public async Task CreateDeploymentAsync_writes_no_audit_row_when_ARM_rejects_the_create()
    {
        var actor = await SeedUserAsync("Ada Lovelace");
        _client.ThrowOnCreate = new ConflictException("Fake: ARM 409 — concurrent create on this account.");
        var service = CreateService(actor.EntraObjectId);

        await Assert.ThrowsAsync<ConflictException>(() => service.CreateDeploymentAsync(Request(Primary, "gpt-4-1-mini"), CancellationToken.None));

        Assert.Single(_client.CreateCalls);
        Assert.Empty(Context.ChangeTracker.Entries<AuditLog>());
        Assert.False(await Context.AuditLogs.AnyAsync());
    }

    [Fact]
    public async Task DeleteDeploymentAsync_deletes_once_and_audits_what_was_deleted()
    {
        _client.Seed(Secondary, "gpt-4-1-mini", provisioningState: "Succeeded", capacity: 25);
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId);

        await service.DeleteDeploymentAsync(Secondary, "gpt-4-1-mini", CancellationToken.None);

        Assert.Equal([(Secondary, "gpt-4-1-mini")], _client.DeleteCalls);
        Assert.Empty(_client.CreateCalls); // never delete-and-recreate
        Assert.Null(await _client.GetDeploymentAsync(Secondary, "gpt-4-1-mini", CancellationToken.None));

        var audit = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.FoundryDeploymentDeleted);
        Assert.Equal(actor.UserId, audit.ActorUserId);
        Assert.Equal($"{Secondary}/gpt-4-1-mini", audit.TargetId);
        Assert.Contains("\"previousProvisioningState\":\"Succeeded\"", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"capacity\":25", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteDeploymentAsync_refuses_an_Anthropic_deployment_before_any_ARM_delete()
    {
        // Symmetric with the create refusal: the API cannot recreate a Claude deployment (#126) and infra
        // can only recreate all of an account's deployments (re-PUTting the survivors, E-006).
        _client.Seed(Primary, "claude-sonnet-4-5", "Anthropic", "claude-sonnet-4-5", "20250929");
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.DeleteDeploymentAsync(Primary, "claude-sonnet-4-5", CancellationToken.None));

        Assert.Contains("#126", exception.Message, StringComparison.Ordinal);
        Assert.Empty(_client.DeleteCalls);
        Assert.NotNull(await _client.GetDeploymentAsync(Primary, "claude-sonnet-4-5", CancellationToken.None));
        Assert.Empty(Context.ChangeTracker.Entries<AuditLog>());
    }

    [Fact]
    public async Task DeleteDeploymentAsync_throws_KeyNotFound_when_the_deployment_is_absent_and_audits_nothing()
    {
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DeleteDeploymentAsync(Primary, "ghost", CancellationToken.None));

        Assert.Empty(_client.DeleteCalls);
        Assert.Empty(Context.ChangeTracker.Entries<AuditLog>());
    }

    [Fact]
    public async Task DeleteDeploymentAsync_throws_KeyNotFound_for_an_unconfigured_account()
    {
        _client.Seed("not-ours", "gpt-4-1-mini");
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DeleteDeploymentAsync("not-ours", "gpt-4-1-mini", CancellationToken.None));

        Assert.Empty(_client.DeleteCalls);
    }

    [Fact]
    public async Task DeleteDeploymentAsync_refuses_an_unprovisioned_caller_before_any_ARM_call()
    {
        _client.Seed(Primary, "gpt-4-1-mini");
        var service = CreateService("no-user-row");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteDeploymentAsync(Primary, "gpt-4-1-mini", CancellationToken.None));

        Assert.Empty(_client.DeleteCalls);
    }

    [Fact]
    public async Task UpdateCapacityAsync_patches_the_sku_capacity_in_place_and_audits_before_and_after()
    {
        // The one mutation that is safe on an existing deployment (#130): ARM's Deployments_Update is a
        // PATCH whose body is { sku, tags } — no model, so it is not the re-PUT that wedges a create-once
        // resource. The sku name goes back unchanged because ARM's patch body requires it.
        _client.Seed(Primary, "gpt-4-1-mini", capacity: 10);
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId);

        var updated = await service.UpdateCapacityAsync(Primary, "gpt-4-1-mini", new UpdateFoundryDeploymentCapacityRequest { Capacity = 25 }, CancellationToken.None);

        Assert.Equal(25, updated.Capacity);
        Assert.Equal("gpt-4.1-mini", updated.ModelName);
        Assert.Equal("2025-04-14", updated.ModelVersion);
        Assert.Equal([(Primary, "gpt-4-1-mini", "GlobalStandard", 25)], _client.CapacityCalls);
        Assert.Empty(_client.CreateCalls);
        Assert.Empty(_client.DeleteCalls);

        var audit = await Context.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.FoundryDeploymentCapacityChanged);
        Assert.Equal(actor.UserId, audit.ActorUserId);
        Assert.Equal($"{Primary}/gpt-4-1-mini", audit.TargetId);
        Assert.Contains("\"before\":10", audit.Details, StringComparison.Ordinal);
        Assert.Contains("\"after\":25", audit.Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateCapacityAsync_to_the_capacity_it_already_has_touches_nothing()
    {
        // A PATCH to the same value changes nothing at ARM; an audit row claiming otherwise would be a
        // lie, and the call would be pure churn on a create-once resource.
        _client.Seed(Primary, "gpt-4-1-mini", capacity: 10);
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId);

        var unchanged = await service.UpdateCapacityAsync(Primary, "gpt-4-1-mini", new UpdateFoundryDeploymentCapacityRequest { Capacity = 10 }, CancellationToken.None);

        Assert.Equal(10, unchanged.Capacity);
        Assert.Empty(_client.CapacityCalls);
        Assert.Empty(Context.ChangeTracker.Entries<AuditLog>());
    }

    [Fact]
    public async Task UpdateCapacityAsync_refuses_an_Anthropic_deployment_until_a_live_PATCH_has_been_proven()
    {
        // Not the same reason as create and delete: the reasoning says a capacity PATCH is safe, but
        // nobody has run one against a live Claude deployment, and E-007 is what happens when a Claude
        // write is assumed rather than observed.
        _client.Seed(Primary, "claude-sonnet-4-5", "Anthropic", "claude-sonnet-4-5", "20250929", capacity: 5);
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateCapacityAsync(Primary, "claude-sonnet-4-5", new UpdateFoundryDeploymentCapacityRequest { Capacity = 25 }, CancellationToken.None));

        Assert.Contains("#205", exception.Message, StringComparison.Ordinal);
        Assert.Empty(_client.CapacityCalls);
        Assert.Equal(5, (await _client.GetDeploymentAsync(Primary, "claude-sonnet-4-5", CancellationToken.None))!.Capacity);
        Assert.Empty(Context.ChangeTracker.Entries<AuditLog>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ValidationConstants.FoundryDeploymentMaxCapacity + 1)]
    public async Task UpdateCapacityAsync_refuses_a_capacity_out_of_range_before_any_ARM_call(int capacity)
    {
        _client.Seed(Primary, "gpt-4-1-mini");
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateCapacityAsync(Primary, "gpt-4-1-mini", new UpdateFoundryDeploymentCapacityRequest { Capacity = capacity }, CancellationToken.None));

        Assert.Empty(_client.CapacityCalls);
    }

    [Fact]
    public async Task UpdateCapacityAsync_throws_KeyNotFound_for_an_absent_deployment_or_an_unconfigured_account()
    {
        _client.Seed("not-ours", "gpt-4-1-mini");
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId);
        var request = new UpdateFoundryDeploymentCapacityRequest { Capacity = 25 };

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.UpdateCapacityAsync(Primary, "ghost", request, CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.UpdateCapacityAsync("not-ours", "gpt-4-1-mini", request, CancellationToken.None));

        Assert.Empty(_client.CapacityCalls);
    }

    [Fact]
    public async Task UpdateCapacityAsync_refuses_an_unprovisioned_caller_before_any_ARM_call()
    {
        _client.Seed(Primary, "gpt-4-1-mini");
        var service = CreateService("no-user-row");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.UpdateCapacityAsync(Primary, "gpt-4-1-mini", new UpdateFoundryDeploymentCapacityRequest { Capacity = 25 }, CancellationToken.None));

        Assert.Empty(_client.CapacityCalls);
    }

    [Fact]
    public async Task UpdateCapacityAsync_audits_even_when_the_caller_disconnects_the_moment_ARM_accepts()
    {
        // CONVENTIONS.md commit point: once ARM has taken the change, a client hanging up must not turn
        // an accepted resize into an unaudited one.
        _client.Seed(Primary, "gpt-4-1-mini", capacity: 10);
        var actor = await SeedUserAsync("Ada Lovelace");
        var service = CreateService(actor.EntraObjectId);
        using var cancellation = new CancellationTokenSource();
        _client.OnUpdateCapacity = (_, _) => cancellation.Cancel();

        var updated = await service.UpdateCapacityAsync(Primary, "gpt-4-1-mini", new UpdateFoundryDeploymentCapacityRequest { Capacity = 25 }, cancellation.Token);

        Assert.Equal(25, updated.Capacity);
        Assert.Single(await Context.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.FoundryDeploymentCapacityChanged).ToListAsync());
    }

    private static CreateFoundryDeploymentRequest Request(string accountName, string deploymentName) =>
        new()
        {
            AccountName = accountName,
            DeploymentName = deploymentName,
            ModelFormat = FoundryModelFormatType.OpenAI,
            ModelName = "gpt-4.1-mini",
            ModelVersion = "2025-04-14",
            SkuName = "GlobalStandard",
            Capacity = 10,
        };

    /// <summary>Wires the real accessor + audit service over this test's context, as the DI container would per request.</summary>
    [Fact]
    public async Task ListCatalogAsync_merges_the_accounts_and_unions_their_skus()
    {
        // #173: the create form names one account at a time, so a per-account catalogue would be a
        // list of near-duplicates. A model both regions serve is one entry carrying both SKUs.
        _client.SeedCatalog(Primary, "gpt-4.1-mini", "2025-04-14", defaultCapacity: 10, skuNames: ["GlobalStandard"]);
        _client.SeedCatalog(Secondary, "gpt-4.1-mini", "2025-04-14", defaultCapacity: 10, skuNames: ["DataZoneStandard", "GlobalStandard"]);
        // A model only one region carries is still offered — ARM decides the create.
        _client.SeedCatalog(Secondary, "gpt-5-codex", "2026-01-01", defaultCapacity: 25, skuNames: ["GlobalStandard"]);

        var catalog = await CreateService(oid: null).ListCatalogAsync(CancellationToken.None);

        Assert.Equal(["gpt-4.1-mini", "gpt-5-codex"], catalog.Select(e => e.ModelName));
        var mini = catalog[0];
        Assert.Equal(["DataZoneStandard", "GlobalStandard"], mini.SkuNames);
        Assert.Equal(10, mini.DefaultCapacity);
    }

    [Fact]
    public async Task ListCatalogAsync_puts_arms_default_version_first_whatever_the_version_string_sorts_as()
    {
        // A create needs an explicit version, so two versions of one model are two answers, not one —
        // and which one to suggest is ARM's `isDefaultVersion`, never a string comparison. Sorting
        // these descending would put "turbo-2024-04-09" above "2025-04-14"; ARM says otherwise.
        _client.SeedCatalog(Primary, "gpt-4.1-mini", "turbo-2024-04-09", isDefaultVersion: false);
        _client.SeedCatalog(Primary, "gpt-4.1-mini", "2025-04-14", isDefaultVersion: true);

        var catalog = await CreateService(oid: null).ListCatalogAsync(CancellationToken.None);

        Assert.Equal(["2025-04-14", "turbo-2024-04-09"], catalog.Select(e => e.ModelVersion));
        Assert.True(catalog[0].IsDefaultVersion);
    }

    [Fact]
    public async Task ListCatalogAsync_carries_arms_default_sku_and_that_skus_capacity_together()
    {
        // SkuNames is sorted for a readable dropdown; the SKU to *offer* is ARM's own first one.
        // Capacity limits are per-SKU, so splitting the two suggests a create ARM refuses.
        _client.SeedCatalog(Primary, "gpt-5-codex", defaultCapacity: 30, skuNames: ["GlobalStandard", "DataZoneStandard"]);

        var entry = Assert.Single(await CreateService(oid: null).ListCatalogAsync(CancellationToken.None));

        Assert.Equal(["DataZoneStandard", "GlobalStandard"], entry.SkuNames);
        Assert.Equal("GlobalStandard", entry.DefaultSkuName);
        Assert.Equal(30, entry.DefaultCapacity);
    }

    [Fact]
    public async Task ListCatalogAsync_reports_the_lifecycle_and_the_earliest_retirement_any_region_named()
    {
        // The first date this stops working somewhere is the date an admin needs to know about.
        var earlier = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var later = new DateTimeOffset(2027, 6, 1, 0, 0, 0, TimeSpan.Zero);
        _client.SeedCatalog(Primary, "gpt-35-turbo", "0301", lifecycleStatus: "Deprecating", inferenceRetiresOn: later);
        _client.SeedCatalog(Secondary, "gpt-35-turbo", "0301", lifecycleStatus: "Deprecating", inferenceRetiresOn: earlier);

        var entry = Assert.Single(await CreateService(oid: null).ListCatalogAsync(CancellationToken.None));

        Assert.Equal("Deprecating", entry.LifecycleStatus);
        Assert.Equal(earlier, entry.InferenceRetiresOn);

        // Deprecating is not yet retired; a date in the future has not passed.
        Assert.False(entry.IsRetiredAt(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.True(entry.IsRetiredAt(later));
    }

    [Fact]
    public async Task ListCatalogAsync_does_not_cache_an_empty_answer()
    {
        // Emptiness means every account 404'd or ARM answered nothing — a transient window that would
        // otherwise pin "catalogue unavailable" on the create dialog for five minutes, with nothing to
        // invalidate it (deploying a model does not change what is deployable).
        var service = CreateService(oid: null);
        Assert.Empty(await service.ListCatalogAsync(CancellationToken.None));

        _client.SeedCatalog(Primary, "gpt-4.1-mini");

        Assert.Single(await service.ListCatalogAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ListCatalogAsync_lists_anthropic_models_even_though_creating_one_is_refused()
    {
        // What the account can serve is a fact worth showing; the create refusal explains itself.
        _client.SeedCatalog(Primary, "claude-sonnet-4-5", "20250929", modelFormat: "Anthropic");

        var catalog = await CreateService(oid: null).ListCatalogAsync(CancellationToken.None);

        Assert.Equal("Anthropic", Assert.Single(catalog).ModelFormat);
    }

    [Fact]
    public async Task ListCatalogAsync_serves_a_second_call_from_cache()
    {
        _client.SeedCatalog(Primary, "gpt-4.1-mini");
        var service = CreateService(oid: null);

        _ = await service.ListCatalogAsync(CancellationToken.None);
        _ = await service.ListCatalogAsync(CancellationToken.None);

        // One round of ARM calls, not two: every open of the create dialog asks for this.
        Assert.Equal([Primary, Secondary], _client.CatalogCalls);
    }

    [Fact]
    public async Task ListCatalogAsync_skips_a_missing_account_rather_than_failing_the_whole_read()
    {
        // One decommissioned region must not leave the create form with no suggestions at all.
        _client.SeedCatalog(Primary, "gpt-4.1-mini");
        var service = CreateService(oid: null, accounts: [Primary, Missing]);

        var catalog = await service.ListCatalogAsync(CancellationToken.None);

        Assert.Equal("gpt-4.1-mini", Assert.Single(catalog).ModelName);
    }

    [Fact]
    public async Task ListCatalogAsync_is_503_when_foundry_is_not_configured_at_all()
    {
        var service = CreateService(oid: null, configured: false);

        _ = await Assert.ThrowsAsync<FeatureNotConfiguredException>(
            () => service.ListCatalogAsync(CancellationToken.None));
    }

    private FoundryDeploymentService CreateService(string? oid, bool configured = true, List<string>? accounts = null)
    {
        var claims = oid is null ? [] : new List<Claim> { new(ClaimConstants.Oid, oid) };
        var identity = new ClaimsIdentity(claims, "TestAuth", nameType: ClaimConstants.Name, roleType: ClaimConstants.Roles);
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var accessor = new CurrentUserAccessor(new FixedHttpContextAccessor(httpContext), Context);
        var auditService = new AuditService(Context, new AuditWriter(Context, _timeProvider), accessor);

        var appSettings = new AppSettings
        {
            Gateway = configured
                ? new GatewayOptions
                {
                    SubscriptionId = "00000000-0000-0000-0000-000000000001",
                    ResourceGroup = "rg-foundrygate-test",
                    FoundryAccountNames = accounts ?? [Primary, Secondary],
                }
                : new GatewayOptions(),
        };

        return new FoundryDeploymentService(_client, appSettings, auditService, accessor, Context, _cache, NullLogger<FoundryDeploymentService>.Instance);
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
