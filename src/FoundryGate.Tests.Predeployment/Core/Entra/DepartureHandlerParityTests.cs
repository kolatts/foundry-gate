using System.Security.Claims;
using System.Text.Json;
using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Audit;
using FoundryGate.Api.Services.Entra;
using FoundryGate.Api.Services.Identity;
using FoundryGate.Api.Services.Keys;
using FoundryGate.Api.Services.Lifecycle;
using FoundryGate.Api.Services.Requests;
using FoundryGate.Api.Services.Security;
using FoundryGate.Core.Entra;
using FoundryGate.Core.Quota;
using FoundryGate.Core.Requests;
using FoundryGate.Data.Audit;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Keys;
using FoundryGate.Domain.Requests;
using FoundryGate.Tests.Predeployment.Data;
using FoundryGate.Tests.Predeployment.Support;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Identity.Web;

namespace FoundryGate.Tests.Predeployment.Core.Entra;

/// <summary>
/// The guard #214 needs until it lands: <b>the two departure pipelines must leave the same trail</b>.
/// The Api's <see cref="UserLifecycleService"/> and Core's
/// <see cref="DeprovisioningDepartureHandler"/> are separate implementations of plan 21's deprovision
/// Trigger B, so "same audit actions, same details shapes" is a claim only an assertion can keep.
/// </summary>
/// <remarks>
/// <para>
/// It is not hypothetical. The first version of the Core handler wrote
/// <c>trigger = nameof(IDepartureHandler)</c> — <c>"IDepartureHandler"</c> — where the Api writes
/// <c>"EntraDeparture"</c>, so an operator filtering the audit log on
/// <c>details.trigger == "EntraDeparture"</c> would have seen every admin-found departure and none of
/// the nightly ones. The old tests asserted <c>keyRevoked</c> and <c>allocationHardStopped</c> and
/// never <c>trigger</c>, which is exactly why nothing caught it (PR #216 review).
/// </para>
/// <para>
/// <b>Both departures run against one database, on one clock, from identical fixtures</b>, so the two
/// details objects are comparable field for field — including <c>deactivatedDate</c>. Only the target
/// id and the subscription name legitimately differ, and those are compared per user rather than
/// excluded, so a field appearing on one side and not the other still fails.
/// </para>
/// </remarks>
public class DepartureHandlerParityTests : InMemoryDatabaseTest
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 2, 0, 0, TimeSpan.Zero);

    private readonly MutableTimeProvider _clock = new(Now);
    private readonly FakeApimManagementClient _apim = new();

    [Fact]
    public async Task The_two_pipelines_write_identical_user_deactivated_details_for_the_same_departure()
    {
        await SeedReferenceDataAsync();
        var viaCore = await SeedDepartingDeveloperAsync();
        var viaApi = await SeedDepartingDeveloperAsync();

        await CoreHandler().HandleAsync(viaCore, CancellationToken.None);
        await ApiHandler().HandleAsync(viaApi, CancellationToken.None);

        var core = await DetailsAsync(AuditActions.UserDeactivated, viaCore);
        var api = await DetailsAsync(AuditActions.UserDeactivated, viaApi);

        // Field names first, so a field added to one side and not the other fails as "shape" rather
        // than as a confusing single-value mismatch.
        Assert.Equal(PropertyNames(api), PropertyNames(core));

        Assert.Equal(api.GetProperty("trigger").GetString(), core.GetProperty("trigger").GetString());
        Assert.Equal(DepartureAudit.Trigger, core.GetProperty("trigger").GetString());
        Assert.Equal(api.GetProperty("keyRevoked").GetBoolean(), core.GetProperty("keyRevoked").GetBoolean());
        Assert.Equal(api.GetProperty("allocationHardStopped").GetBoolean(), core.GetProperty("allocationHardStopped").GetBoolean());
        Assert.Equal(api.GetProperty("cancelledRequestCount").GetInt32(), core.GetProperty("cancelledRequestCount").GetInt32());
        Assert.Equal(api.GetProperty("period").GetString(), core.GetProperty("period").GetString());
        Assert.Equal(api.GetProperty("deactivatedDate").GetDateTimeOffset(), core.GetProperty("deactivatedDate").GetDateTimeOffset());
    }

    [Fact]
    public async Task The_two_pipelines_write_identical_key_revoked_details_for_the_same_departure()
    {
        await SeedReferenceDataAsync();
        var viaCore = await SeedDepartingDeveloperAsync();
        var viaApi = await SeedDepartingDeveloperAsync();

        await CoreHandler().HandleAsync(viaCore, CancellationToken.None);
        await ApiHandler().HandleAsync(viaApi, CancellationToken.None);

        var core = await DetailsAsync(AuditActions.KeyRevoked, viaCore);
        var api = await DetailsAsync(AuditActions.KeyRevoked, viaApi);

        Assert.Equal(PropertyNames(api), PropertyNames(core));
        Assert.Equal(api.GetProperty("reason").GetString(), core.GetProperty("reason").GetString());
        Assert.Equal(DepartureAudit.KeyRevocationReason, core.GetProperty("reason").GetString());
        Assert.Equal(api.GetProperty("existedInApim").GetBoolean(), core.GetProperty("existedInApim").GetBoolean());

        // The two identity fields differ by user, on purpose — assert they name the right one rather
        // than skipping them, or a handler that wrote somebody else's subscription would pass.
        Assert.Equal(ApimSubscriptionNames.ForUser(viaCore.UserId), core.GetProperty("subscriptionName").GetString());
        Assert.Equal(ApimSubscriptionNames.ForUser(viaApi.UserId), api.GetProperty("subscriptionName").GetString());
    }

    [Fact]
    public async Task Both_rows_are_system_attributed_by_both_pipelines()
    {
        // The directory's word, not an admin's — even when an admin's POST /users/sync found it.
        await SeedReferenceDataAsync();
        var viaCore = await SeedDepartingDeveloperAsync();
        var viaApi = await SeedDepartingDeveloperAsync();

        await CoreHandler().HandleAsync(viaCore, CancellationToken.None);
        await ApiHandler().HandleAsync(viaApi, CancellationToken.None);

        foreach (var action in new[] { AuditActions.KeyRevoked, AuditActions.UserDeactivated })
        {
            Assert.Null(await ActorAsync(action, viaCore));
            Assert.Null(await ActorAsync(action, viaApi));
        }
    }

    [Fact]
    public async Task Both_pipelines_close_pending_requests_with_the_same_note_and_no_reviewer()
    {
        await SeedReferenceDataAsync();
        var viaCore = await SeedDepartingDeveloperAsync();
        var viaApi = await SeedDepartingDeveloperAsync();

        await CoreHandler().HandleAsync(viaCore, CancellationToken.None);
        await ApiHandler().HandleAsync(viaApi, CancellationToken.None);

        foreach (var user in new[] { viaCore, viaApi })
        {
            var closed = await Context.QuotaIncreaseRequests.AsNoTracking().SingleAsync(r => r.UserId == user.UserId);
            Assert.Equal(QuotaRequestStatusType.Rejected, closed.StatusType);
            Assert.Null(closed.ReviewedByUserId);
            Assert.Equal(DepartureAudit.ReviewNote, closed.ReviewNotes);
        }
    }

    [Fact]
    public void The_Api_deprovision_trigger_name_is_the_constant_Core_writes()
    {
        // Core cannot reference DeprovisionTrigger (an Api enum), and the Api's details come from
        // trigger.ToString() because the same field has to keep saying "AdminDeactivation" for the
        // other trigger. So the tie between the enum member and DepartureAudit.Trigger is this
        // assertion — renaming the member without updating the constant fails here.
        Assert.Equal(DepartureAudit.Trigger, nameof(DeprovisionTrigger.EntraDeparture));
    }

    // There is deliberately no test asserting UserLifecycleService.DeactivationReviewNote ==
    // DepartureAudit.ReviewNote (and likewise for the revocation reason). The review asked for one, but
    // those two are now `internal const string X = DepartureAudit.Y;` — an alias, not a copy — so the
    // compiler enforces the identity and a test could only ever agree with it. (It could not be written
    // as-is anyway: the Api's constants are internal and FoundryGate.Api grants no InternalsVisibleTo.)
    // What the compiler CANNOT enforce is the enum-member name above, and the details the two pipelines
    // build independently, which is what the rest of this class is for.

    // -- Helpers --

    private static IEnumerable<string> PropertyNames(JsonElement details) =>
        details.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal);

    private async Task<JsonElement> DetailsAsync(string action, User user)
    {
        var row = await Context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == action && a.TargetId == user.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return JsonDocument.Parse(row.Details).RootElement.Clone();
    }

    private async Task<int?> ActorAsync(string action, User user) =>
        (await Context.AuditLogs.AsNoTracking()
            .SingleAsync(a => a.Action == action && a.TargetId == user.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        .ActorUserId;

    private DeprovisioningDepartureHandler CoreHandler()
    {
        var audit = new AuditWriter(Context, _clock);

        return new DeprovisioningDepartureHandler(
            Context,
            _apim,
            new QuotaRequestExpiry(Context, audit, _clock, NullLogger<QuotaRequestExpiry>.Instance),
            audit,
            _clock,
            NullLogger<DeprovisioningDepartureHandler>.Instance);
    }

    /// <summary>
    /// The Api's answer to the same seam, over the real <see cref="UserLifecycleService"/> — a stub
    /// would prove nothing about the trail the Api actually writes. The caller is an admin who exists,
    /// so the only reason a row could come out attributed is a bug, not a missing fixture.
    /// </summary>
    private LifecycleDepartureHandler ApiHandler()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimConstants.Oid, Guid.NewGuid().ToString()), new Claim(ClaimConstants.Roles, RoleNames.Admin)],
            "TestAuth",
            nameType: ClaimConstants.Name,
            roleType: ClaimConstants.Roles);
        var accessor = new CurrentUserAccessor(
            new FixedHttpContextAccessor(new DefaultHttpContext { User = new ClaimsPrincipal(identity) }),
            Context);
        var writer = new AuditWriter(Context, _clock);
        var audit = new AuditService(Context, writer, accessor);

        var keys = new ApimKeyService(
            Context,
            _apim,
            new DataProtectionKeyProtector(new EphemeralDataProtectionProvider()),
            audit,
            writer,
            accessor,
            TestSecurityOptions.RevealAnomaly(),
            _clock,
            NullLogger<ApimKeyService>.Instance);
        var tierMapper = TestGatewayTiers.Mapper();
        var quotaResolution = new QuotaResolutionService(
            Context,
            tierMapper,
            new NullGatewayTierSync(NullLogger<NullGatewayTierSync>.Instance),
            NullLogger<QuotaResolutionService>.Instance);
        var quotaRequests = new QuotaRequestService(
            Context,
            quotaResolution,
            new QuotaRequestExpiry(Context, writer, _clock, NullLogger<QuotaRequestExpiry>.Instance),
            tierMapper,
            accessor,
            audit,
            _clock);

        return new LifecycleDepartureHandler(new UserLifecycleService(
            Context,
            quotaResolution,
            quotaRequests,
            keys,
            audit,
            writer,
            accessor,
            new FakeEntraDirectoryClient(),
            new AppSettings(),
            _clock,
            NullLogger<UserLifecycleService>.Instance));
    }

    /// <summary>An active developer with a live key, this period's allocation and one pending request — every branch of the pipeline's details object populated.</summary>
    private async Task<User> SeedDepartingDeveloperAsync()
    {
        var user = new User
        {
            EntraObjectId = Guid.NewGuid().ToString(),
            DisplayName = "Departed Developer",
            Email = $"{Guid.NewGuid():N}@contoso.test",
        };
        _ = Context.Users.Add(user);
        _ = await Context.SaveChangesAsync();

        var subscriptionName = ApimSubscriptionNames.ForUser(user.UserId);
        _ = _apim.Seed(subscriptionName, GatewayTiers.Standard);
        user.ApimSubscriptionId = _apim.GetSubscriptionResourceId(subscriptionName);
        user.ApimSubscriptionKey = "protected-ciphertext";
        user.ApimSubscriptionKeyHint = "1a2b";
        user.ApimKeyIssuedDate = Now;

        _ = Context.QuotaAllocations.Add(new QuotaAllocation
        {
            UserId = user.UserId,
            PeriodYear = Now.Year,
            PeriodMonth = Now.Month,
            AllocatedTokens = TestGatewayTiers.StandardCap,
            TierProductId = GatewayTiers.Standard,
        });
        _ = Context.QuotaIncreaseRequests.Add(new QuotaIncreaseRequest
        {
            UserId = user.UserId,
            RequestedByUserId = user.UserId,
            PeriodYear = Now.Year,
            PeriodMonth = Now.Month,
            RequestedQuota = TestGatewayTiers.PowerCap,
            Justification = "More tokens, please.",
        });
        _ = await Context.SaveChangesAsync();

        return user;
    }
}
