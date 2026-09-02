namespace FoundryGate.Domain.Quota;

/// <summary>
/// One calendar-month billing period (<c>QuotaAllocation.PeriodYear</c>/<c>PeriodMonth</c>),
/// always derived from a UTC instant: the gateway's <c>llm-token-limit</c> monthly window is a
/// UTC-truncated calendar month (plans/24), so FoundryGate's periods must be too or the two would
/// disagree around month boundaries. Lives in Domain (not the Api's <c>Services/Quota</c>) so the
/// Functions host's monthly reset (#38) computes the same period the Api does without referencing Api.
/// </summary>
/// <param name="Year">Calendar year (UTC).</param>
/// <param name="Month">Calendar month, 1-12 (UTC).</param>
public readonly record struct BillingPeriod(int Year, int Month)
{
    /// <summary>The period containing <paramref name="instant"/>, evaluated in UTC (a non-UTC offset is converted first, never read as wall-clock).</summary>
    public static BillingPeriod FromInstant(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return new BillingPeriod(utc.Year, utc.Month);
    }

    /// <summary>The current period per <paramref name="timeProvider"/> — the only way application code should obtain "this month" (CONVENTIONS.md: no naked <c>UtcNow</c>).</summary>
    public static BillingPeriod Current(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return FromInstant(timeProvider.GetUtcNow());
    }

    /// <inheritdoc />
    public override string ToString() => $"{Year:D4}-{Month:D2}";
}
