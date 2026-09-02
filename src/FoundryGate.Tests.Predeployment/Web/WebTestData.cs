using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Foundry;
using FoundryGate.Domain.Foundry.Contracts;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Domain.Keys.Contracts;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Quota.Contracts;
using FoundryGate.Domain.Requests;
using FoundryGate.Domain.Requests.Contracts;
using FoundryGate.Domain.Users.Contracts;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// Response shapes for the component tests, with every field an admin page reads already
/// filled in. Each factory takes only what a test varies; everything else is a plausible
/// default, so a test body says what it is actually about.
/// </summary>
public static class WebTestData
{
    /// <summary>The tier catalogue the pages render budgets against — the same three <c>GatewayTiers</c> ships.</summary>
    public static IReadOnlyList<QuotaTierResponse> Tiers { get; } =
    [
        new(GatewayTiers.Standard, "Standard", 5_000_000, false),
        new(GatewayTiers.Power, "Power", 25_000_000, false),
        new(GatewayTiers.Unlimited, "Unlimited", null, true),
    ];

    public static UserResponse User(
        int userId = 1,
        string displayName = "Ada Lovelace",
        string email = "ada@example.com",
        bool isActive = true,
        bool isUnlimited = false,
        long? monthlyTokenQuota = 5_000_000,
        bool isApiKeyProvisioned = true) =>
        new(
            userId,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            displayName,
            email,
            EmployeeId: "E-1",
            isActive,
            isUnlimited,
            monthlyTokenQuota,
            isApiKeyProvisioned,
            CreatedDate: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LastSyncedDate: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

    public static UserDetailResponse UserDetail(
        UserResponse? user = null,
        QuotaAllocationResponse? allocation = null,
        ApiKeyResponse? apiKey = null,
        IReadOnlyList<UserGroupMembershipResponse>? groups = null)
    {
        var row = user ?? User();
        return new UserDetailResponse(
            row,
            allocation ?? Allocation(row.UserId),
            apiKey ?? new ApiKeyResponse(row.IsApiKeyProvisioned, row.IsApiKeyProvisioned ? "fg-…-9f2a" : null, row.IsApiKeyProvisioned ? "sub-1" : null),
            groups ?? []);
    }

    public static QuotaAllocationResponse Allocation(
        int userId = 1,
        bool isUnlimited = false,
        long? allocatedTokens = 5_000_000,
        long tokensUsed = 1_250_000,
        QuotaLevelType level = QuotaLevelType.SystemDefault,
        bool isGatewayCapped = false,
        bool isHardStopped = false) =>
        new(
            QuotaAllocationId: 10,
            userId,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            UserDisplayName: "Ada Lovelace",
            UserEmail: "ada@example.com",
            PeriodYear: 2026,
            PeriodMonth: 9,
            isUnlimited,
            allocatedTokens,
            tokensUsed,
            PercentUsed: allocatedTokens is > 0 ? tokensUsed / (double)allocatedTokens.Value : null,
            isHardStopped,
            level,
            TierProductId: GatewayTiers.Standard,
            isGatewayCapped,
            ResetDate: null);

    public static UserGroupMembershipResponse Membership(int groupId = 7, string name = "Platform") =>
        new(groupId, Guid.Parse("22222222-2222-2222-2222-222222222222"), name, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

    public static GroupResponse Group(
        int groupId = 7,
        string name = "Platform",
        string? entraGroupId = null,
        bool isUnlimited = false,
        long? monthlyTokenQuota = 25_000_000,
        int memberCount = 2) =>
        new(
            groupId,
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            name,
            Description: "The platform team.",
            entraGroupId,
            IsEntraSynced: entraGroupId is not null,
            isUnlimited,
            monthlyTokenQuota,
            memberCount,
            CreatedDate: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    public static GroupMemberResponse Member(int userId = 1, string displayName = "Ada Lovelace", string email = "ada@example.com") =>
        new(userId, Guid.Parse("11111111-1111-1111-1111-111111111111"), displayName, email, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), AddedByUserId: 99);

    public static QuotaIncreaseRequestResponse Request(
        int requestId = 5,
        int userId = 1,
        string userDisplayName = "Ada Lovelace",
        long? requestedQuota = 25_000_000,
        QuotaRequestStatusType status = QuotaRequestStatusType.Pending,
        string? reviewNotes = null) =>
        new(
            requestId,
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            userId,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            userDisplayName,
            RequestedByUserId: userId,
            PeriodYear: 2026,
            PeriodMonth: 9,
            CurrentQuota: 5_000_000,
            requestedQuota,
            Justification: "Migrating the monolith; the agent runs all week.",
            status,
            ReviewedByUserId: status == QuotaRequestStatusType.Pending ? null : 99,
            ReviewedDate: status == QuotaRequestStatusType.Pending ? null : new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero),
            reviewNotes,
            CreatedDate: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));

    public static FoundryDeploymentResponse Deployment(
        string accountName = "fg-eastus",
        string deploymentName = "gpt-5-codex",
        FoundryModelFormatType format = FoundryModelFormatType.OpenAI,
        string provisioningState = "Succeeded",
        int? capacity = 25) =>
        new(
            accountName,
            deploymentName,
            format.ToString(),
            ModelName: deploymentName,
            ModelVersion: "2026-01-01",
            SkuName: "GlobalStandard",
            capacity,
            provisioningState,
            CreatedDate: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ModifiedDate: null);

    /// <summary>Wraps items as the single full page a paged endpoint would return.</summary>
    public static PagedResult<T> Page<T>(params T[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new PagedResult<T>(items, items.Length, 1, 25);
    }
}
