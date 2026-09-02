using FoundryGate.Domain.Quota;

namespace FoundryGate.Core.Quota;

/// <summary>
/// The monthly quota reset, shared by the two hosts that run it (#119): the scheduled
/// <c>MonthlyQuotaResetFunction</c> (#38) and the admin's <c>POST /quota/reset</c> — one
/// implementation, so "what a reset does" cannot drift between a button and a timer.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it does.</b> In one unit of work: re-resolve every <em>active</em> user's allocation for the
/// current UTC calendar month through <see cref="IQuotaResolutionService.ResolveManyAsync"/> (insert
/// where missing, re-resolve where present), clear <c>IsHardStopped</c>, stamp <c>ResetDate</c>, add
/// exactly one audit row, and save once.
/// </para>
/// <para>
/// <b>What it deliberately does not do.</b> It never zeroes <c>TokensUsed</c>: those numbers come from
/// reconciliation (#39) and a new period's rows start at zero on their own, while re-running the reset
/// mid-month must not erase a month's consumption. It never touches APIM either — the gateway's
/// <c>llm-token-limit</c> monthly window is a UTC-truncated calendar month that resets itself (#10
/// direction update), so there is no counter to clear and no subscription state to change.
/// </para>
/// <para>
/// <b>Idempotent.</b> Running it twice in a month produces the same rows, the same
/// <see cref="QuotaResetOutcome.UsersResetCount"/>, and a second audit row saying so. That is what
/// lets the Function's day-of-month gate and its blob lease be belt-and-braces rather than
/// correctness-critical.
/// </para>
/// </remarks>
public interface IQuotaResetService
{
    /// <summary>Resets the current period's allocations. See the type remarks for the exact contract.</summary>
    /// <param name="trigger">Who is resetting and under which audit action — <see cref="QuotaResetTrigger.Scheduled"/> or <see cref="QuotaResetTrigger.Admin"/>.</param>
    /// <param name="cancellationToken">
    /// Honoured up to the point resolution asks the gateway to move a subscription; past that commit
    /// point the audit row and the save run on <see cref="CancellationToken.None"/> (CONVENTIONS.md:
    /// "external side effects have a commit point").
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
/// <param name="UsersResetCount">Active users whose allocation was created or re-resolved.</param>
/// <param name="TierSyncCount">How many of those asked <see cref="IGatewayTierSync"/> to move a subscription — zero for a scheduled reset, whose inputs have not changed since the last resolution.</param>
/// <param name="Period">The UTC calendar month that was reset.</param>
/// <param name="ResetDate">The instant written to every touched row's <c>ResetDate</c>.</param>
public readonly record struct QuotaResetOutcome(
    int UsersResetCount,
    int TierSyncCount,
    BillingPeriod Period,
    DateTimeOffset ResetDate);
