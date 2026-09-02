using FoundryGate.Data.Entities;

namespace FoundryGate.Core.Entra;

/// <summary>
/// What happens to a user the directory no longer lists — plan 21's deprovision Trigger B, behind a
/// seam because the two hosts that can discover a departure reach the pipeline differently (#151).
/// </summary>
/// <remarks>
/// <para>
/// The Api implements it by delegating to <c>IUserLifecycleService.DeprovisionAsync(EntraDeparture,
/// …)</c>, which is the one orchestrator plan 21 defines and which needs <c>IApimKeyService</c> and
/// <c>IQuotaRequestService</c> — both Api-only. Core cannot reference the Api, so
/// <c>EntraUserSyncService</c> depends on this one method instead of on the orchestrator; the Functions
/// host supplies <see cref="DeprovisioningDepartureHandler"/>, which does the same work over the
/// pieces Core already owns (the APIM management client, <see cref="Data.Audit.IAuditWriter"/>, the
/// <c>AppDbContext</c>).
/// </para>
/// <para>
/// <b>Contract, identical for both implementations.</b> Deleting the departed user's APIM
/// subscription comes first and is irreversible, so everything after it commits on
/// <see cref="CancellationToken.None"/> (CONVENTIONS.md §External side effects have a commit point).
/// The user ends up <c>IsActive = false</c> with their current-period allocation hard-stopped and
/// every Pending quota increase request of theirs rejected, described by system-attributed
/// (<c>ActorUserId IS NULL</c>) <c>key.revoked</c> and <c>user.deactivated</c> rows — the directory's
/// word, not any admin's. It is <b>idempotent</b>: a user who is already inactive is a no-op, and a
/// subscription APIM no longer has is not an error, so the next run retries a failure cleanly.
/// </para>
/// </remarks>
public interface IDepartureHandler
{
    /// <summary>
    /// Deprovisions <paramref name="user"/>, who is no longer assigned to the FoundryGate application
    /// in Entra. See the type remarks for the contract.
    /// </summary>
    /// <param name="user">The departed user, tracked by the sync's <c>AppDbContext</c>.</param>
    /// <param name="cancellationToken">
    /// Honoured up to the point the gateway accepts the deletion; everything after that commits on
    /// <see cref="CancellationToken.None"/>.
    /// </param>
    /// <exception cref="Domain.Exceptions.UpstreamDependencyException">
    /// The gateway refused the deletion, so nothing was changed for this user. The caller counts it,
    /// logs it and carries on with the rest of the run — the next run retries.
    /// </exception>
    Task HandleAsync(User user, CancellationToken cancellationToken);
}
