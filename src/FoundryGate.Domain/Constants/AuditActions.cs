namespace FoundryGate.Domain.Constants;

/// <summary>
/// Well-known values for <c>AuditLog.Action</c> (spec &#167;3.1: a free-form string,
/// e.g. <c>"quota.approved"</c>, <c>"key.rotated"</c>). Kept as string constants rather
/// than an enum: the audit trail is an open-ended, append-only log and new action
/// kinds get added as the feature set grows (new endpoints, new admin tools) — an enum
/// would force a schema-affecting change (the column is a plain string, not an
/// <c>int</c>-backed enum column per CONVENTIONS.md) every time a new action is added,
/// and would block a future action namespace external integrations might write without
/// a Domain code change. A closed, small, structurally significant set
/// (<see cref="Requests.QuotaRequestStatusType"/>, <see cref="Config.ModelProviderType"/>)
/// stays an enum; this open-ended label set stays strings.
/// </summary>
public static class AuditActions
{
    // -- Users (spec 4.1) --

    /// <summary>PUT /users/{id}/activate.</summary>
    public const string UserActivated = "user.activated";

    /// <summary>PUT /users/{id}/deactivate.</summary>
    public const string UserDeactivated = "user.deactivated";

    /// <summary>PUT /users/{id}/quota.</summary>
    public const string UserQuotaChanged = "user.quota-changed";

    /// <summary>POST /users/sync.</summary>
    public const string UsersSynced = "users.synced";

    // -- Groups (spec 4.2) --

    public const string GroupCreated = "group.created";
    public const string GroupUpdated = "group.updated";
    public const string GroupDeleted = "group.deleted";
    public const string GroupMemberAdded = "group.member-added";
    public const string GroupMemberRemoved = "group.member-removed";

    /// <summary>POST /groups/sync-entra.</summary>
    public const string GroupEntraSynced = "group.entra-synced";

    // -- Quota / requests (spec 4.3, 4.4, 6) --

    /// <summary>The scheduled monthly reset job (spec &#167;6, exact string from spec).</summary>
    public const string QuotaMonthlyReset = "quota.monthly-reset";

    /// <summary>PUT /requests/{id}/approve (spec &#167;3.1, exact example string from spec).</summary>
    public const string QuotaIncreaseApproved = "quota.approved";

    /// <summary>PUT /requests/{id}/reject.</summary>
    public const string QuotaIncreaseRejected = "quota.rejected";

    // -- Keys (spec 4.5, 5.2, 5.3) --

    public const string KeyProvisioned = "key.provisioned";

    /// <summary>Spec &#167;3.1, exact example string from spec.</summary>
    public const string KeyRotated = "key.rotated";

    public const string KeyRevoked = "key.revoked";

    // -- Config (spec 4.6) --

    public const string ConfigUpdated = "config.updated";
}
