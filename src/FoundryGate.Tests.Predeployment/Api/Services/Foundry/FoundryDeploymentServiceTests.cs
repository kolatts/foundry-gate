using System.Security.Claims;
using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Foundry;
using FoundryGate.Api.Services.Identity;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Web;

namespace FoundryGate.Tests.Predeployment.Api.Services.Foundry;

/// <summary>
/// The provisioning service's own rules, against the in-memory ARM fake and a real
/// <see cref="AuditService"/>/<see cref="CurrentUserAccessor"/> over SQLite: multi-account
/// aggregation, the developer view's de-duplication, create-once (409 before any PUT), the
/// Anthropic refusal (#107), and that every refusal happens <em>before</em> ARM is touched.
/// </summary>
public class FoundryDeploymentServiceTests : InMemoryDatabaseTest
{
    private const string Primary = "fg-eus2";
    private const string Secondary = "fg-swc";

    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeFoundryManagementClient _client = new();
    private readonly MutableTimeProvider _timeProvider = new(Now);

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
    public async Task ListDeploymentsAsync_throws_when_the_Gateway_section_is_not_configured()
    {
        var service = CreateService(oid: null, configured: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListDeploymentsAsync(CancellationToken.None));

        Assert.Contains("Gateway:FoundryAccountNames", exception.Message, StringComparison.Ordinal);
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
    public async Task GetDeploymentAsync_throws_KeyNotFound_for_an_absent_deployment_and_for_an_unconfigured_account()
    {
        _client.Seed(Primary, "gpt-4-1-mini");
        var service = CreateService(oid: null);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.GetDeploymentAsync(Primary, "nope", CancellationToken.None));
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
    private FoundryDeploymentService CreateService(string? oid, bool configured = true)
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
                    FoundryAccountNames = [Primary, Secondary],
                }
                : new GatewayOptions(),
        };

        return new FoundryDeploymentService(_client, appSettings, auditService, accessor, Context, NullLogger<FoundryDeploymentService>.Instance);
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
