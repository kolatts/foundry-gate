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
    // -- Users (spec 4.1, 7.1) --

    /// <summary>First-login auto-provisioning via GET /users/me (spec &#167;7.1).</summary>
    public const string UserProvisioned = "user.provisioned";

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

    /// <summary>POST /quota/reset — an admin manually triggering the (idempotent) reset outside the schedule.</summary>
    public const string QuotaAllocationReset = "quota.reset";

    /// <summary>POST /requests — a developer (or an admin on their behalf) submitting a quota increase request.</summary>
    public const string QuotaIncreaseSubmitted = "quota.requested";

    /// <summary>PUT /requests/{id}/approve (spec &#167;3.1, exact example string from spec).</summary>
    public const string QuotaIncreaseApproved = "quota.approved";

    /// <summary>PUT /requests/{id}/reject.</summary>
    public const string QuotaIncreaseRejected = "quota.rejected";

    // -- Keys (spec 4.5, 5.2, 5.3) --

    public const string KeyProvisioned = "key.provisioned";

    /// <summary>Spec &#167;3.1, exact example string from spec.</summary>
    public const string KeyRotated = "key.rotated";

    public const string KeyRevoked = "key.revoked";

    /// <summary>A developer revealing their own full key (spec &#167;11: "reveal action fetches directly, not stored in browser").</summary>
    public const string KeyRevealed = "key.revealed";

    /// <summary>
    /// More <see cref="KeyRevealed"/> rows for one key inside the configured window than a person
    /// plausibly produces (<c>Security:RevealAnomaly</c>, #180). Written once per window alongside the
    /// reveal that crossed the line, so a patient drain — which stays inside the rate limiter — leaves
    /// a mark in the trail rather than only in the logs.
    /// </summary>
    public const string KeyRevealAnomaly = "key.reveal-anomaly";

    /// <summary>The APIM subscription behind a key was re-scoped to another quota-tier product (#82: a tier change moves the subscription between tier products).</summary>
    public const string KeyTierChanged = "key.tier-changed";

    /// <summary>
    /// APIM regenerated the keys but the new primary could not be stored — the ciphertext on the user is
    /// now stale and the remedy (rotate again, or revoke and re-provision) is in the details. Written so
    /// an unrevealable key has a trail, not just a log line.
    /// </summary>
    public const string KeyRotationFailed = "key.rotation-failed";

    // -- Usage sync (spec 5.4, issue #39) --

    /// <summary>The Log Analytics → <c>QuotaAllocation.TokensUsed</c> reconciliation job (reconciliation, not enforcement).</summary>
    public const string UsageSynced = "usage.synced";

    // -- Foundry model deployments (issue #61) --

    public const string FoundryDeploymentCreated = "foundry.deployment.created";
    public const string FoundryDeploymentDeleted = "foundry.deployment.deleted";

    // -- Config (spec 4.6) --

    public const string ConfigUpdated = "config.updated";
}
