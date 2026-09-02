using FoundryGate.Domain.Config.Contracts;

namespace FoundryGate.Api.Services.Config;

/// <summary>
/// The fork-wide <c>SystemConfiguration</c> key/value editor behind <c>/api/v1/config</c>
/// (spec &#167;4.6; issue #161). Admin-only at the controller; the service itself owns the read
/// projection, the per-key validation table (<see cref="SystemConfigValidator"/>), the audit row and
/// the single save.
/// </summary>
/// <remarks>
/// Seeding and editing share the table without fighting over it: <c>SystemConfiguration.Value</c>,
/// <c>UpdatedDate</c> and <c>UpdatedByUserId</c> all carry <c>[DoNotUpdate]</c>, so the reference-data
/// seeder only ever <em>inserts</em> a missing key — re-running a deploy can never revert what an
/// admin set here.
/// </remarks>
public interface IConfigService
{
    /// <summary>Every configuration row, ordered by key, with the editing admin's display name joined in. Read-only projection — nothing is tracked.</summary>
    Task<IReadOnlyList<SystemConfigEntryResponse>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Sets one key's value, stamping the calling admin and the current time, and writes one
    /// <c>config.updated</c> audit row (details <c>{ key, before, after }</c>) in the same save.
    /// </summary>
    /// <param name="key">The configuration key; matched case-insensitively, as SQL Server's default collation would.</param>
    /// <param name="request">The new value. Validated and normalized per key before it is stored.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row as it now stands.</returns>
    /// <exception cref="KeyNotFoundException">
    /// No such configuration key (→ 404). A key retired by #164/#123 lands here too: the row is gone,
    /// so there is nothing to distinguish it from a typo.
    /// </exception>
    /// <exception cref="ArgumentException">The value breaks the key's rule (→ 400); the message states the rule.</exception>
    /// <exception cref="Domain.Exceptions.ConflictException">
    /// The key is system-managed (→ 409) — written by a job rather than by an admin (#171). Its rows are
    /// flagged <c>IsReadOnly</c> on the read, so the editor disables the field rather than offering an
    /// edit that can only fail.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The calling admin has no <c>User</c> row yet (→ 403; call <c>GET /users/me</c> first).</exception>
    Task<SystemConfigEntryResponse> UpdateAsync(string key, UpdateSystemConfigRequest request, CancellationToken cancellationToken);
}
