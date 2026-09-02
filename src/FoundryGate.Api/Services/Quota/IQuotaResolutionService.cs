using FoundryGate.Domain.Quota;

namespace FoundryGate.Api.Services.Quota;

/// <summary>
/// The five-level quota resolution (spec &#167;3.2; issue #32) — the core business rule of FoundryGate —
/// and its output: an upserted <c>QuotaAllocation</c> row carrying the numeric quota, the level that
/// produced it, and the APIM tier product the gateway will enforce (#7 direction update).
/// </summary>
/// <remarks>
/// <para><b>Precedence, first match wins.</b> User-level settings always beat group-level ones:</para>
/// <list type="number">
/// <item><c>User.IsUnlimited</c> → unlimited (<see cref="QuotaLevelType.UserUnlimited"/>).</item>
/// <item><c>User.MonthlyTokenQuota</c> set → that value (<see cref="QuotaLevelType.UserOverride"/>) — even if a group would grant unlimited.</item>
/// <item>Any membership whose <c>Group.IsUnlimited</c> → unlimited (<see cref="QuotaLevelType.GroupUnlimited"/>).</item>
/// <item>Max <c>Group.MonthlyTokenQuota</c> across memberships (<see cref="QuotaLevelType.GroupMax"/>).</item>
/// <item><c>SystemConfiguration[DefaultMonthlyTokenQuota]</c> (<see cref="QuotaLevelType.SystemDefault"/>).</item>
/// </list>
/// <para>
/// <b>Nothing here saves.</b> Both methods add/modify rows on the request's <c>AppDbContext</c> and
/// return; the orchestrating caller commits the mutation together with its audit row in one
/// <c>SaveChangesAsync</c> (CONVENTIONS.md audit pattern). The upsert inserts new rows with
/// <c>TokensUsed = 0</c> and, on an existing row, rewrites only <c>AllocatedTokens</c>/level/tier/capped —
/// never <c>TokensUsed</c> or <c>IsHardStopped</c>, which belong to reconciliation and offboarding.
/// </para>
/// <para>
/// <b>Pending changes count.</b> Levels 3-4 read group membership and group policy <em>through the
/// change tracker</em>: a <c>GroupMember</c> the caller has just added or removed, and a
/// <c>Group.MonthlyTokenQuota</c>/<c>IsUnlimited</c> it has just edited, are visible here before
/// anything is saved. That is what lets a group mutation and the re-resolution it triggers commit in
/// one <c>SaveChangesAsync</c> instead of racing the database (#30/#31/#41).
/// </para>
/// <para>
/// <b>Gateway.</b> The numeric quota is mapped to a tier product by <see cref="GatewayTierMapper"/>; when
/// the user has an APIM subscription and the tier differs from their previous allocation's (or none is
/// known), <see cref="IGatewayTierSync.SyncAsync"/> is invoked — before the caller saves, so a failed
/// gateway move fails the request. Nothing else touches APIM.
/// </para>
/// </remarks>
public interface IQuotaResolutionService
{
    /// <summary>Resolves and upserts the allocation for one user and period.</summary>
    /// <exception cref="KeyNotFoundException">No such user (→ 404).</exception>
    /// <exception cref="Imagile.Framework.Configuration.Exceptions.ConfigurationValidationException">
    /// Resolution fell through to the system default and <c>SystemConfiguration[DefaultMonthlyTokenQuota]</c>
    /// is missing or not a non-negative integer — a misconfigured fork, never a silent 0.
    /// </exception>
    Task<QuotaResolution> ResolveAsync(int userId, BillingPeriod period, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves and upserts allocations for many users in one pass (the monthly/manual reset). Loads
    /// users, memberships and existing rows in bulk rather than per user; the same rules and
    /// no-save contract as <see cref="ResolveAsync"/>. Unknown ids are skipped, not an error — the
    /// caller enumerated them a moment ago and a concurrent delete is not the reset's problem.
    /// </summary>
    /// <returns>One <see cref="QuotaResolution"/> per resolved user, in <paramref name="userIds"/> order.</returns>
    Task<IReadOnlyList<QuotaResolution>> ResolveManyAsync(IReadOnlyCollection<int> userIds, BillingPeriod period, CancellationToken cancellationToken);

    /// <summary>
    /// Runs the same five-level chain and returns the answer <em>without writing anything</em> — no
    /// allocation row added or modified, no <see cref="IGatewayTierSync"/> call, nothing left on the
    /// change tracker. Period-independent: the chain reads only the user's and their groups' current
    /// settings, so there is nothing for a period to select.
    /// </summary>
    /// <remarks>
    /// For callers that need "what is this developer's budget right now?" in order to decide whether to
    /// act at all — quota increase requests measure both submission and approval against it (#34/#35).
    /// Reading the stored <c>QuotaAllocation</c> instead would answer with whatever the last resolution
    /// wrote, which a since-changed user override or group membership has already invalidated; calling
    /// <see cref="ResolveAsync"/> would answer correctly but move the caller's gateway tier as a side
    /// effect of a question that may end in a refusal (CONVENTIONS.md: every refusal before the external
    /// call).
    /// </remarks>
    /// <exception cref="KeyNotFoundException">No such user (→ 404).</exception>
    /// <exception cref="Imagile.Framework.Configuration.Exceptions.ConfigurationValidationException">As <see cref="ResolveAsync"/>.</exception>
    Task<QuotaPreview> PreviewAsync(int userId, CancellationToken cancellationToken);
}
