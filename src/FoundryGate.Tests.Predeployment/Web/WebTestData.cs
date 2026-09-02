using FoundryGate.Domain.Audit.Contracts;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Config;
using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Dashboard.Contracts;
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
/// Response shapes for the Blazor component tests, with every field a page reads already filled in.
/// Each factory takes only what a test varies and everything else is a plausible default, so a test
/// body says what it is actually about — <c>Allocation(percentUsed: 82)</c> reads as the threshold
/// it is checking rather than as a wall of DTO construction.
/// </summary>
public static class WebTestData
{
    /// <summary>
    /// The tier catalogue the pages render budgets against. Mirrors <c>infra/main.bicep</c>'s
    /// <c>quotaTiers</c> so a test's expected numbers can be checked against what the fork actually
    /// ships: Standard 5M, Power 20M, Unlimited uncapped.
    /// </summary>
    public static IReadOnlyList<QuotaTierResponse> Tiers { get; } =
    [
        new(GatewayTiers.Standard, "Standard", 5_000_000, false),
        new(GatewayTiers.Power, "Power", 20_000_000, false),
        new(GatewayTiers.Unlimited, "Unlimited", null, true),
    ];

    public static UserResponse User(
        int userId = 7,
        string displayName = "Dev Eloper",
        string email = "dev@example.test",
        bool isActive = true,
        bool isUnlimited = false,
        long? monthlyTokenQuota = 5_000_000,
        bool isApiKeyProvisioned = true) =>
        new(
            userId,
            UserUnique,
            displayName,
            email,
            EmployeeId: "E-1",
            isActive,
            isUnlimited,
            monthlyTokenQuota,
            isApiKeyProvisioned,
            CreatedDate: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LastSyncedDate: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),

            // #167: distinct from LastSyncedDate on purpose — a fixture where the two agreed would let a
            // page bind the wrong one and still look right.
            LastLoginDate: new DateTimeOffset(2026, 9, 1, 8, 30, 0, TimeSpan.Zero));

    public static UserDetailResponse UserDetail(
        UserResponse? user = null,
        QuotaAllocationResponse? allocation = null,
        ApiKeyResponse? apiKey = null,
        IReadOnlyList<UserGroupMembershipResponse>? groups = null)
    {
        var row = user ?? User();
        return new UserDetailResponse(
            row,
            allocation ?? Allocation(userId: row.UserId),
            apiKey ?? Key(row.IsApiKeyProvisioned),
            groups ?? []);
    }

    public static UserGroupMembershipResponse Membership(int groupId = 7, string name = "Platform") =>
        new(groupId, GroupUnique, name, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

    public static QuotaAllocationResponse Allocation(
        int userId = 7,
        long? allocatedTokens = 5_000_000,
        long tokensUsed = 1_000_000,
        double? percentUsed = 20,
        bool isUnlimited = false,
        string tierProductId = GatewayTiers.Standard,
        bool isGatewayCapped = false,
        bool isHardStopped = false,
        string userDisplayName = "Dev Eloper",
        string userEmail = "dev@example.test",
        decimal? estimatedCost = null,
        QuotaLevelType level = QuotaLevelType.SystemDefault) =>
        new(
            QuotaAllocationId: 1,
            userId,
            UserUnique,
            UserDisplayName: userDisplayName,
            UserEmail: userEmail,
            PeriodYear: 2026,
            PeriodMonth: 9,
            IsUnlimited: isUnlimited,
            AllocatedTokens: isUnlimited ? null : allocatedTokens,
            TokensUsed: tokensUsed,
            PercentUsed: isUnlimited ? null : percentUsed,
            IsHardStopped: isHardStopped,
            ResolvedLevelType: level,
            TierProductId: tierProductId,
            IsGatewayCapped: isGatewayCapped,
            ResetDate: null,
            EstimatedCost: estimatedCost);

    public static ApiKeyResponse Key(bool isProvisioned = true, string? maskedKey = "••••••••1a2b") =>
        new(isProvisioned, isProvisioned ? maskedKey : null, isProvisioned ? "/subscriptions/x/apim/sub/dev-7" : null);

    public static ApiKeyRevealResponse Reveal(string plaintext = "plaintext-key-value") =>
        new(plaintext, "••••••••" + plaintext[^4..], "/subscriptions/x/apim/sub/dev-7", DateTimeOffset.UnixEpoch);

    public static GatewayConnectionInfo CliConfig(
        string gatewayBaseUrl = "https://ai.example.test",
        IReadOnlyList<ModelAliasInfo>? aliases = null) =>
        new(gatewayBaseUrl, "/anthropic", "/openai/v1", aliases ?? [Alias(), Alias("gpt", "gpt-4-1-mini", ModelProviderType.OpenAi)]);

    public static ModelAliasInfo Alias(
        string alias = "sonnet",
        string deploymentName = "claude-sonnet-4-5",
        ModelProviderType provider = ModelProviderType.Anthropic) =>
        new(alias, deploymentName, provider);

    public static UserProfileResponse Profile(
        QuotaAllocationResponse? quota = null,
        ApiKeyResponse? key = null,
        GatewayConnectionInfo? cliConfig = null,
        bool isActive = true,
        string displayName = "Dev Eloper",
        int userId = 7) =>
        new(
            userId,
            UserUnique,
            displayName,
            Email: "dev@example.test",
            isActive,
            IsUnlimited: quota?.IsUnlimited ?? false,
            Quota: quota ?? Allocation(userId: userId),
            ApiKey: key ?? Key(),
            CliConfig: cliConfig ?? CliConfig());

    public static FoundryModelResponse Model(
        string deploymentName = "claude-sonnet-4-5",
        string modelName = "claude-sonnet-4-5",
        string modelFormat = "Anthropic",
        string provisioningState = "Succeeded") =>
        new(deploymentName, modelName, "1", modelFormat, provisioningState);

    public static FoundryDeploymentResponse Deployment(
        string accountName = "fg-eastus",
        string deploymentName = "gpt-4-1-mini",
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

    /// <summary>
    /// One <c>GET /foundry/catalog</c> row — a model the configured accounts can serve (#173).
    /// <paramref name="defaultSkuName"/> defaults to the first of <paramref name="skuNames"/>, which is
    /// what ARM's own ordering would give; pass it explicitly to make the two differ, which is the case
    /// worth testing (the list is sorted for display, the default is not).
    /// </summary>
    public static FoundryCatalogEntryResponse CatalogEntry(
        string modelName = "gpt-4.1-mini",
        string modelVersion = "2025-04-14",
        IReadOnlyList<string>? skuNames = null,
        int? defaultCapacity = 10,
        string modelFormat = "OpenAI",
        string? defaultSkuName = null,
        bool isDefaultVersion = true,
        string lifecycleStatus = "GenerallyAvailable",
        DateTimeOffset? inferenceRetiresOn = null)
    {
        IReadOnlyList<string> skus = skuNames ?? ["GlobalStandard"];
        return new(
            modelFormat,
            modelName,
            modelVersion,
            skus,
            defaultCapacity,
            defaultSkuName ?? (skus.Count > 0 ? skus[0] : string.Empty),
            isDefaultVersion,
            lifecycleStatus,
            inferenceRetiresOn);
    }

    public static GroupResponse Group(
        int groupId = 7,
        string name = "Platform",
        string? entraGroupId = null,
        bool isUnlimited = false,
        long? monthlyTokenQuota = 20_000_000,
        int memberCount = 2) =>
        new(
            groupId,
            GroupUnique,
            name,
            Description: "The platform team.",
            entraGroupId,
            IsEntraSynced: entraGroupId is not null,
            isUnlimited,
            monthlyTokenQuota,
            memberCount,
            CreatedDate: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    public static GroupMemberResponse Member(int userId = 7, string displayName = "Dev Eloper", string email = "dev@example.test") =>
        new(userId, UserUnique, displayName, email, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero), AddedByUserId: 99);

    public static QuotaIncreaseRequestResponse Request(
        int requestId = 1,
        int userId = 7,
        string userDisplayName = "Dev Eloper",
        QuotaRequestStatusType status = QuotaRequestStatusType.Pending,
        long? requestedQuota = 20_000_000,
        string justification = "Running a large migration this month.",
        string? reviewNotes = null) =>
        new(
            requestId,
            RequestUnique,
            userId,
            UserUnique,
            userDisplayName,
            RequestedByUserId: userId,
            PeriodYear: 2026,
            PeriodMonth: 9,
            CurrentQuota: 5_000_000,
            requestedQuota,
            justification,
            status,
            ReviewedByUserId: status == QuotaRequestStatusType.Pending ? null : 99,
            ReviewedDate: status == QuotaRequestStatusType.Pending ? null : DateTimeOffset.UnixEpoch,
            reviewNotes,
            CreatedDate: DateTimeOffset.UnixEpoch);

    public static DashboardSummaryResponse Dashboard(
        int totalUserCount = 42,
        int activeUserCount = 39,
        int unlimitedUserCount = 3,
        int pendingRequestCount = 2,
        long totalTokensUsed = 123_456_789,
        IReadOnlyList<TopConsumerResponse>? topConsumers = null,
        int hardStoppedUserCount = 0,
        int overBudgetUserCount = 0,
        decimal? estimatedCostThisPeriod = null) =>
        new(
            totalUserCount,
            activeUserCount,
            unlimitedUserCount,
            pendingRequestCount,
            totalTokensUsed,
            topConsumers ?? [Consumer()],
            hardStoppedUserCount,
            overBudgetUserCount,
            estimatedCostThisPeriod);

    public static TopConsumerResponse Consumer(
        string displayName = "Heavy User",
        long tokensUsed = 4_900_000,
        long? allocatedTokens = 5_000_000,
        double? percentUsed = 98,
        decimal? estimatedCostThisPeriod = null) =>
        new(9, Guid.Parse("33333333-3333-3333-3333-333333333333"), displayName, tokensUsed, allocatedTokens, percentUsed, estimatedCostThisPeriod);

    public static SystemConfigEntryResponse ConfigEntry(
        string key = SystemConfigurationKeys.DefaultMonthlyTokenQuota,
        string value = "5000000",
        int? updatedByUserId = null,
        string? updatedByDisplayName = null,
        bool isReadOnly = false) =>
        new(key, value, DateTimeOffset.UnixEpoch, updatedByUserId, updatedByDisplayName, isReadOnly);

    public static AuditLogEntryResponse AuditEntry(
        int id = 1,
        string action = AuditActions.QuotaIncreaseApproved,
        string? actorDisplayName = "Ada Admin",
        string? targetType = AuditTargetTypes.QuotaIncreaseRequest,
        string? details = """{"before":null,"after":20000000}""") =>
        new(id, actorDisplayName is null ? null : 1, actorDisplayName, action, targetType, "5", details, DateTimeOffset.UnixEpoch);

    /// <summary>Wraps items as the single full page a paged endpoint would return.</summary>
    public static PagedResult<T> Page<T>(params T[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new PagedResult<T>(items, items.Length, 1, 25);
    }

    private static readonly Guid UserUnique = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GroupUnique = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RequestUnique = Guid.Parse("33333333-3333-3333-3333-333333333333");
}
