using FoundryGate.Domain.Config.Contracts;
using FoundryGate.Domain.Exceptions;

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
    /// <c>config.updated</c> audit row (details
    /// <c>{ key, before, after, reresolvedUserCount, tierChangeCount }</c>) in the same save.
    /// </summary>
    /// <remarks>
    /// <b>Changing <c>DefaultMonthlyTokenQuota</c> re-resolves the developers it governs</b> (#193).
    /// Level 5 of the precedence chain has just moved, so every active user who falls through to it —
    /// and any whose current allocation already reads <c>SystemDefault</c> — is re-resolved for the
    /// current period in the same unit of work, which moves their APIM subscription to the new tier
    /// product. Without it the row said one thing and the gateway enforced another until something else
    /// happened to touch each user, and the first thing to notice was usually the Functions host's
    /// monthly reset, which cannot move a tier at all. Setting the key to the value it already has
    /// re-resolves nobody.
    /// </remarks>
    /// <param name="key">The configuration key; matched case-insensitively, as SQL Server's default collation would.</param>
    /// <param name="request">
    /// The new value, validated and normalized per key before it is stored, plus the optional
    /// <c>ExpectedUpdatedDate</c> concurrency check (#170).
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row as it now stands.</returns>
    /// <exception cref="KeyNotFoundException">
    /// No such configuration key (→ 404). A key retired by #164/#123 lands here too: the row is gone,
    /// so there is nothing to distinguish it from a typo.
    /// </exception>
    /// <exception cref="ArgumentException">The value breaks the key's rule (→ 400); the message states the rule.</exception>
    /// <exception cref="ConflictException">
    /// <c>ExpectedUpdatedDate</c> was supplied and does not match the stored row (→ 409): another admin
    /// wrote the key first. The message carries the row's current value, its timestamp and — when it has
    /// one — the editor's display name, so the caller can re-decide without another round trip. Checked
    /// before the value is validated: a stale view has to be refreshed whatever it was trying to write.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The calling admin has no <c>User</c> row yet (→ 403; call <c>GET /users/me</c> first).</exception>
    Task<SystemConfigEntryResponse> UpdateAsync(string key, UpdateSystemConfigRequest request, CancellationToken cancellationToken);
}
