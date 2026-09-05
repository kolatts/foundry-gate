namespace FoundryGate.Domain.Constants;

/// <summary>
/// Well-known values for <c>AuditLog.TargetType</c> (spec &#167;3.1: e.g. <c>"User"</c>,
/// <c>"Group"</c>, <c>"Request"</c>). String constants rather than an enum for the same reason
/// as <see cref="AuditActions"/>: the audit trail is open-ended and the column is free text.
/// Every audit call site should pass one of these so the admin audit viewer's
/// <c>targetType</c> filter never has to guess at spelling.
/// </summary>
public static class AuditTargetTypes
{
    /// <summary>A <c>User</c> row; <c>TargetId</c> is the <c>UserId</c>.</summary>
    public const string User = "User";

    /// <summary>A <c>Group</c> row; <c>TargetId</c> is the <c>GroupId</c>.</summary>
    public const string Group = "Group";

    /// <summary>A <c>QuotaIncreaseRequest</c> row (spec &#167;3.1's "Request"); <c>TargetId</c> is the <c>QuotaIncreaseRequestId</c>.</summary>
    public const string QuotaIncreaseRequest = "Request";

    /// <summary>A <c>QuotaAllocation</c> row; <c>TargetId</c> is the <c>QuotaAllocationId</c>.</summary>
    public const string QuotaAllocation = "QuotaAllocation";

    /// <summary>A developer's APIM subscription key; <c>TargetId</c> is the owning <c>UserId</c> (the key itself is never logged).</summary>
    public const string ApiKey = "ApiKey";

    /// <summary>A <c>SystemConfiguration</c> row; <c>TargetId</c> is the configuration <c>Key</c>.</summary>
    public const string SystemConfiguration = "SystemConfiguration";

    /// <summary>An Azure AI Foundry model deployment; <c>TargetId</c> is <c>{accountName}/{deploymentName}</c> — the gateway runs one account per region, so the name alone is ambiguous for pooled models.</summary>
    public const string FoundryDeployment = "FoundryDeployment";

    /// <summary>A gateway quota-tier product; <c>TargetId</c> is the tier's APIM product id (a <see cref="GatewayTiers"/> value). What a model-allowlist change is recorded against (#225).</summary>
    public const string GatewayTier = "GatewayTier";
}
