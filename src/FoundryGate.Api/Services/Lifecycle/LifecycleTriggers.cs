namespace FoundryGate.Api.Services.Lifecycle;

/// <summary>
/// Why <see cref="IUserLifecycleService.ProvisionAsync"/> is running (plan 21's three provision
/// triggers). The trigger decides what the pipeline does <em>around</em> its shared middle — resolve
/// quota, mint the APIM subscription — not the middle itself.
/// </summary>
public enum ProvisionTrigger
{
    /// <summary>
    /// Trigger A — a developer's first <c>GET /users/me</c>. No <c>User</c> row exists yet: one is
    /// created from the caller's token claims, enriched from the directory when <c>Entra:Enabled</c>.
    /// Audited <c>user.provisioned</c>.
    /// </summary>
    FirstLogin = 0,

    /// <summary>
    /// Trigger B — an admin provisioning a key for an existing, active user who has none. The row is
    /// untouched apart from the key fields. Audited <c>user.provisioned</c>.
    /// </summary>
    AdminProvision = 1,

    /// <summary>
    /// Trigger C — <c>POST /users/{id}/activate</c> on a deactivated user: <c>IsActive</c> goes back to
    /// true and the whole pipeline runs, re-minting the key that deactivation deleted (adopting an
    /// orphan subscription if one survived). Audited <c>user.activated</c>.
    /// </summary>
    Reactivate = 2,
}

/// <summary>Why <see cref="IUserLifecycleService.DeprovisionAsync"/> is running (plan 21's deprovision triggers A and B).</summary>
/// <remarks>
/// Plan 21's Trigger C — admin key revocation without deactivation (<c>DELETE /keys/{userId}</c>) — is
/// deliberately absent: per the #116 ruling it is <em>not</em> a deprovision. It deletes the
/// subscription and stops, leaving the user active and re-provisionable, and lives entirely in
/// <c>IApimKeyService.RevokeAsync</c>. Routing it through here would hard-stop their quota and reject
/// their pending requests, which is not what "revoke the key" means.
/// </remarks>
public enum DeprovisionTrigger
{
    /// <summary>Trigger A — <c>POST /users/{id}/deactivate</c>. The <c>key.revoked</c> row is attributed to the calling admin.</summary>
    AdminDeactivation = 0,

    /// <summary>
    /// Trigger B — <c>POST /users/sync</c> found the user absent from the directory's assigned-user
    /// list. There is no human caller, so both audit rows are system-attributed
    /// (<c>IAuditWriter.AddSystem</c>) and the run is idempotent: an already-deactivated user is a
    /// no-op rather than a conflict.
    /// </summary>
    EntraDeparture = 1,
}

/// <summary>
/// What <see cref="IUserLifecycleService.ProvisionAsync"/> is provisioning. Deliberately tiny: for
/// <see cref="ProvisionTrigger.FirstLogin"/> the identity comes from <c>ICurrentUserAccessor</c> and the
/// directory, so there is nothing to pass; every other trigger names an existing row.
/// </summary>
public sealed record ProvisionContext
{
    /// <summary>The user being provisioned; <see langword="null"/> only for <see cref="ProvisionTrigger.FirstLogin"/>, which creates the row.</summary>
    public int? UserId { get; init; }

    /// <summary>Context for <see cref="ProvisionTrigger.FirstLogin"/> — the row does not exist yet.</summary>
    public static ProvisionContext FirstLogin() => new();

    /// <summary>Context for a trigger that acts on an existing user.</summary>
    public static ProvisionContext ForUser(int userId) => new() { UserId = userId };
}
