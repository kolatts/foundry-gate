namespace FoundryGate.Api.Services;

/// <summary>
/// The one place the commit-point rule is expressed (CONVENTIONS.md "External side effects have a
/// commit point"; #163/#158). Once ARM, APIM or Graph has <em>accepted</em> a change, the audit row and
/// <c>SaveChangesAsync</c> that record it may not be abandoned because the client hung up — a
/// disconnect must never turn an accepted change into an unaudited one. Before that moment the
/// request's own token still applies: an abandoned request that has changed nothing outside the
/// database should stop.
/// </summary>
/// <remarks>
/// <para>
/// The predicate is "did we reach the external system", not "did we call something that might have".
/// For quota resolution that is <c>QuotaResolution.TierSyncRequested</c> — an empty member list, or a
/// tier that did not move, never touched APIM and is not a commit point. For the key service it is
/// "APIM minted/regenerated/deleted a subscription", which those paths know unconditionally and so
/// pass <see cref="CancellationToken.None"/> directly rather than through this helper.
/// </para>
/// <para>
/// A save that fails <em>anyway</em> is the residual orphan CONVENTIONS.md describes: log it at Error
/// with the change's full identity and rethrow, so an operator can reconcile it
/// (<c>FoundryDeploymentService.AuditAfterCommitAsync</c> is the reference implementation).
/// </para>
/// </remarks>
public static class CommitToken
{
    /// <summary>
    /// The token to finish a unit of work on: <see cref="CancellationToken.None"/> when
    /// <paramref name="reachedExternal"/> (the external system has accepted a change and the database
    /// now owes it a record), otherwise <paramref name="cancellationToken"/>.
    /// </summary>
    /// <param name="reachedExternal">Whether an external system has already accepted a change in this unit of work.</param>
    /// <param name="cancellationToken">The request's own token, used while nothing outside the database has happened yet.</param>
    public static CancellationToken For(bool reachedExternal, CancellationToken cancellationToken) =>
        reachedExternal ? CancellationToken.None : cancellationToken;
}
