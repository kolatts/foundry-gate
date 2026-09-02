using FoundryGate.Domain.Foundry;
using FoundryGate.Domain.Foundry.Contracts;
using FoundryGate.Domain.Groups.Contracts;
using FoundryGate.Domain.Keys.Contracts;
using FoundryGate.Domain.Quota.Contracts;
using FoundryGate.Domain.Users.Contracts;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// The contracts only the admin pages render (#51, #52, #53, #62, #63): users as an admin sees
/// them, groups and their rosters, and Foundry deployments. A separate class from
/// <see cref="WebTestData"/> rather than more members on it — the two frontend waves author
/// their own fixtures, and one shared class would collide on every name they both wanted
/// (<c>Request</c>, <c>Allocation</c>) for no benefit.
/// </summary>
/// <remarks>Everything the developer surface already models — tiers, allocations, keys, requests, paging — comes from <see cref="WebTestData"/>.</remarks>
public static class AdminTestData
{
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
            allocation ?? WebTestData.Allocation(),
            apiKey ?? WebTestData.Key(row.IsApiKeyProvisioned),
            groups ?? []);
    }

    public static UserGroupMembershipResponse Membership(int groupId = 7, string name = "Platform") =>
        new(groupId, Guid.Parse("22222222-2222-2222-2222-222222222222"), name, new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));

    public static GroupResponse Group(
        int groupId = 7,
        string name = "Platform",
        string? entraGroupId = null,
        bool isUnlimited = false,
        long? monthlyTokenQuota = 20_000_000,
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
}
