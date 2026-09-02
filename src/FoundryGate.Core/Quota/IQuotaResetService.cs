using FoundryGate.Domain.Quota;

namespace FoundryGate.Core.Quota;

/// <summary>
/// The monthly quota reset, shared by the two hosts that run it (#119): the scheduled
/// <c>MonthlyQuotaResetFunction</c> (#38) and the admin's <c>POST /quota/reset</c> — one
/// implementation, so "what a reset does" cannot drift between a button and a timer.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it does.</b> Re-resolve every <em>active</em> user's allocation for the current UTC calendar
/// month through <see cref="IQuotaResolutionService.ResolveManyAsync"/> (insert where missing,
/// re-resolve where present), clear <c>IsHardStopped</c>, stamp <c>ResetDate</c>, move the APIM
/// subscription of any developer whose tier actually changed, and add exactly one audit row describing
/// the run.
/// </para>
/// <para>
/// <b>It does touch APIM, and that is why it is not one transaction.</b> There is no monthly
/// <em>counter</em> to clear — the gateway's <c>llm-token-limit</c> window is a UTC-truncated calendar
/// month that resets itself (#10 direction update) — but a reset is the first thing to notice a changed
/// <c>SystemConfiguration[DefaultMonthlyTokenQuota]</c>, and since #194 the host that runs it can act
/// on that instead of logging drift. Each such move is committed with its own <c>key.tier-changed</c>
/// row the moment APIM accepts it, because a single end-of-run save would discard the rows for every
/// move that already landed when a later one failed (#211 review). Developers whose tier did not move
/// reach nothing external and still share the final save with the run's audit row.
/// </para>
/// <para>
/// <b>A refused move does not fail the run.</b> It is logged at Error with the developer's full
/// identity and counted into <see cref="QuotaResetOutcome.TierSyncFailureCount"/> and the audit row,
/// and that developer is simply <em>skipped</em>: no allocation row is written for them this period.
/// Writing one would claim a budget the gateway is not enforcing, and their previous period's row —
/// which still matches the gateway — is left alone, so the two never disagree. Their next
/// <c>GET /quota/allocations/me</c>, or the next manual reset, resolves and retries the move. Aborting
/// instead meant one subscription deleted out of band in the APIM portal deterministically failed
/// every developer's reset, on every retry.
/// </para>
/// <para>
/// <b>What it deliberately does not do.</b> It never zeroes <c>TokensUsed</c>: those numbers come from
/// reconciliation (#39) and a new period's rows start at zero on their own, while re-running the reset
/// mid-month must not erase a month's consumption.
/// </para>
/// <para>
/// <b>Idempotent.</b> Running it twice in a month produces the same rows, the same
/// <see cref="QuotaResetOutcome.UsersResetCount"/>, and a second audit row saying so — and a rerun
/// after a partial run picks up exactly the developers whose move did not land. That is what lets the
/// Function's day-of-month gate and its blob lease be belt-and-braces rather than correctness-critical.
/// </para>
/// </remarks>
public interface IQuotaResetService
{
    /// <summary>Resets the current period's allocations. See the type remarks for the exact contract.</summary>
    /// <param name="trigger">Who is resetting and under which audit action — <see cref="QuotaResetTrigger.Scheduled"/> or <see cref="QuotaResetTrigger.Admin"/>.</param>
    /// <param name="cancellationToken">
    /// Honoured while nothing outside the database has happened. Each time the gateway accepts a move,
    /// the save recording it runs on <see cref="CancellationToken.None"/> via <c>CommitToken.For</c>
    /// (CONVENTIONS.md: "external side effects have a commit point"); the final save, which carries only
    /// the run's audit row and the developers who needed no move, keeps the caller's own token. A reset
    /// that moved nobody's tier never reaches APIM at all.
    /// </param>
    Task<QuotaResetOutcome> ResetAsync(QuotaResetTrigger trigger, CancellationToken cancellationToken);
}

/// <summary>
/// Who asked for a reset, and therefore how it is audited: the scheduled job writes
/// <c>quota.monthly-reset</c> with no actor, an admin writes <c>quota.reset</c> attributed to
/// themselves. Passed in rather than resolved inside the service because Core has no notion of a
/// "current user" — and because resolving the actor before the work starts is what CONVENTIONS.md's
/// commit-point rule asks for.
/// </summary>
/// <param name="ActorUserId">The acting admin's <c>UserId</c>, or <see langword="null"/> for a system run.</param>
/// <param name="AuditAction">The <c>AuditActions</c> constant the run's single audit row carries.</param>
public readonly record struct QuotaResetTrigger(int? ActorUserId, string AuditAction)
{
    /// <summary>The scheduled monthly job: no human actor, audited as <c>quota.monthly-reset</c> (spec §6).</summary>
    public static QuotaResetTrigger Scheduled() =>
        new(null, Domain.Constants.AuditActions.QuotaMonthlyReset);

    /// <summary>An admin running <c>POST /quota/reset</c> off-schedule: audited as <c>quota.reset</c>, attributed to them.</summary>
    /// <param name="actorUserId">The calling admin's <c>UserId</c> — already resolved, so every refusal happens before the first gateway call.</param>
    public static QuotaResetTrigger Admin(int actorUserId) =>
        new(actorUserId, Domain.Constants.AuditActions.QuotaAllocationReset);
}

/// <summary>What one <see cref="IQuotaResetService.ResetAsync"/> run did.</summary>
/// <param name="UsersResetCount">
/// Active users whose allocation was created or re-resolved <em>and committed</em>. A developer whose
/// gateway move the run could not make is excluded: no row is written for them this period, on
/// purpose.
/// </param>
/// <param name="TierSyncCount">
/// How many developers' APIM subscriptions this run actually moved between tier products. Usually zero
/// — a reset re-resolves inputs nobody changed — but not by contract: a changed
/// <c>SystemConfiguration[DefaultMonthlyTokenQuota]</c> re-resolves nobody until a reset notices, and a
/// developer with no earlier allocation has no known previous tier (#194).
/// </param>
/// <param name="TierSyncFailureCount">
/// How many developers' moves the gateway refused. Each is logged at Error with its full identity and
/// left recording the tier APIM still enforces; the run completed for everybody else. Non-zero means
/// somebody should look.
/// </param>
/// <param name="Period">The UTC calendar month that was reset.</param>
/// <param name="ResetDate">The instant written to every touched row's <c>ResetDate</c>.</param>
public readonly record struct QuotaResetOutcome(
    int UsersResetCount,
    int TierSyncCount,
    int TierSyncFailureCount,
    BillingPeriod Period,
    DateTimeOffset ResetDate);
