using FoundryGate.Domain.Audit.Contracts;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Config;
using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Dashboard.Contracts;
using FoundryGate.Domain.Foundry.Contracts;
using FoundryGate.Domain.Keys.Contracts;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Quota.Contracts;
using FoundryGate.Domain.Requests;
using FoundryGate.Domain.Requests.Contracts;
using FoundryGate.Domain.Users.Contracts;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// Canned Domain contracts for the Blazor component tests. Every factory has sane defaults and
/// named optional parameters, so a test names only the field it is actually asserting on — a test
/// that says <c>Allocation(percentUsed: 82)</c> reads as the threshold it is checking rather than
/// as a wall of DTO construction.
/// </summary>
public static class WebTestData
{
    public static QuotaAllocationResponse Allocation(
        long? allocatedTokens = 5_000_000,
        long tokensUsed = 1_000_000,
        double? percentUsed = 20,
        bool isUnlimited = false,
        string tierProductId = GatewayTiers.Standard,
        bool isGatewayCapped = false,
        bool isHardStopped = false,
        QuotaLevelType resolvedLevelType = QuotaLevelType.SystemDefault) =>
        new(
            QuotaAllocationId: 1,
            UserId: 7,
            UserUnique: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            UserDisplayName: "Dev Eloper",
            UserEmail: "dev@example.test",
            PeriodYear: 2026,
            PeriodMonth: 9,
            IsUnlimited: isUnlimited,
            AllocatedTokens: isUnlimited ? null : allocatedTokens,
            TokensUsed: tokensUsed,
            PercentUsed: isUnlimited ? null : percentUsed,
            IsHardStopped: isHardStopped,
            ResolvedLevelType: resolvedLevelType,
            TierProductId: tierProductId,
            IsGatewayCapped: isGatewayCapped,
            ResetDate: null);

    public static ApiKeyResponse Key(bool isProvisioned = true, string? maskedKey = "••••••••1a2b") =>
        new(isProvisioned, isProvisioned ? maskedKey : null, isProvisioned ? "/subscriptions/x/apim/sub/dev-7" : null);

    public static ApiKeyRevealResponse Reveal(string plaintext = "plaintext-key-value") =>
        new(plaintext, "••••••••" + plaintext[^4..], "/subscriptions/x/apim/sub/dev-7", DateTimeOffset.UnixEpoch);

    public static GatewayConnectionInfo CliConfig(
        string gatewayBaseUrl = "https://ai.example.test",
        IReadOnlyList<ModelAliasInfo>? aliases = null) =>
        new(gatewayBaseUrl, "/anthropic", "/openai/v1", aliases ?? []);

    public static UserProfileResponse Profile(
        QuotaAllocationResponse? quota = null,
        ApiKeyResponse? key = null,
        GatewayConnectionInfo? cliConfig = null,
        bool isActive = true,
        string displayName = "Dev Eloper") =>
        new(
            UserId: 7,
            UserUnique: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            DisplayName: displayName,
            Email: "dev@example.test",
            IsActive: isActive,
            IsUnlimited: quota?.IsUnlimited ?? false,
            Quota: quota ?? Allocation(),
            ApiKey: key ?? Key(),
            CliConfig: cliConfig ?? CliConfig());

    public static FoundryModelResponse Model(
        string deploymentName = "claude-sonnet-5",
        string modelName = "claude-sonnet-4-5",
        string modelFormat = "Anthropic",
        string provisioningState = "Succeeded") =>
        new(deploymentName, modelName, "1", modelFormat, provisioningState);

    public static IReadOnlyList<QuotaTierResponse> Tiers() =>
    [
        new(GatewayTiers.Standard, "Standard", 5_000_000, false),
        new(GatewayTiers.Power, "Power", 20_000_000, false),
        new(GatewayTiers.Unlimited, "Unlimited", null, true),
    ];

    public static QuotaIncreaseRequestResponse Request(
        int id = 1,
        QuotaRequestStatusType status = QuotaRequestStatusType.Pending,
        long? requestedQuota = 20_000_000,
        string justification = "Running a large migration this month.",
        string? reviewNotes = null) =>
        new(
            QuotaIncreaseRequestId: id,
            QuotaIncreaseRequestUnique: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            UserId: 7,
            UserUnique: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            UserDisplayName: "Dev Eloper",
            RequestedByUserId: 7,
            PeriodYear: 2026,
            PeriodMonth: 9,
            CurrentQuota: 5_000_000,
            RequestedQuota: requestedQuota,
            Justification: justification,
            StatusType: status,
            ReviewedByUserId: status == QuotaRequestStatusType.Pending ? null : 1,
            ReviewedDate: status == QuotaRequestStatusType.Pending ? null : DateTimeOffset.UnixEpoch,
            ReviewNotes: reviewNotes,
            CreatedDate: DateTimeOffset.UnixEpoch);

    public static DashboardSummaryResponse Dashboard(
        int totalUserCount = 42,
        int activeUserCount = 39,
        int unlimitedUserCount = 3,
        int pendingRequestCount = 2,
        long totalTokensUsed = 123_456_789,
        IReadOnlyList<TopConsumerResponse>? topConsumers = null) =>
        new(
            totalUserCount,
            activeUserCount,
            unlimitedUserCount,
            pendingRequestCount,
            totalTokensUsed,
            topConsumers ?? [Consumer()]);

    public static TopConsumerResponse Consumer(
        string displayName = "Heavy User",
        long tokensUsed = 4_900_000,
        long? allocatedTokens = 5_000_000,
        double? percentUsed = 98) =>
        new(9, Guid.Parse("33333333-3333-3333-3333-333333333333"), displayName, tokensUsed, allocatedTokens, percentUsed);

    public static SystemConfigEntryResponse ConfigEntry(
        string key = SystemConfigurationKeys.DefaultMonthlyTokenQuota,
        string value = "5000000",
        int? updatedByUserId = null) =>
        new(key, value, DateTimeOffset.UnixEpoch, updatedByUserId);

    public static AuditLogEntryResponse AuditEntry(
        int id = 1,
        string action = AuditActions.QuotaIncreaseApproved,
        string? actorDisplayName = "Ada Admin",
        string? targetType = AuditTargetTypes.QuotaIncreaseRequest,
        string? details = """{"before":null,"after":20000000}""") =>
        new(id, actorDisplayName is null ? null : 1, actorDisplayName, action, targetType, "5", details, DateTimeOffset.UnixEpoch);

    public static PagedResult<T> Page<T>(params T[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new PagedResult<T>(items, items.Length, 1, 25);
    }

    public static ModelAliasInfo Alias(string alias = "sonnet", string deploymentName = "claude-sonnet-5") =>
        new(alias, deploymentName, ModelProviderType.Anthropic);
}
