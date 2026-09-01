using FoundryGate.Domain.Quota;
using FoundryGate.Tests.Predeployment.Support;

namespace FoundryGate.Tests.Predeployment.Domain;

/// <summary>
/// <see cref="BillingPeriod"/> must agree with the gateway's UTC-truncated monthly window: a local-time
/// instant late on the last day of a month is still that month only if it is in UTC.
/// </summary>
public class BillingPeriodTests
{
    [Fact]
    public void FromInstant_uses_the_UTC_calendar_month_not_the_offsets_wall_clock()
    {
        // 2026-09-30 22:00 at -05:00 is 2026-10-01 03:00Z → October, not September.
        var instant = new DateTimeOffset(2026, 9, 30, 22, 0, 0, TimeSpan.FromHours(-5));

        Assert.Equal(new BillingPeriod(2026, 10), BillingPeriod.FromInstant(instant));
    }

    [Fact]
    public void Current_reads_the_injected_TimeProvider()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(new BillingPeriod(2026, 9), BillingPeriod.Current(clock));

        clock.SetUtcNow(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(new BillingPeriod(2027, 1), BillingPeriod.Current(clock));
    }

    [Fact]
    public void Start_is_midnight_UTC_on_the_first()
    {
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new BillingPeriod(2026, 9).Start);
    }

    [Theory]
    [InlineData(2026, 8, 2026, 9, true)]
    [InlineData(2025, 12, 2026, 1, true)]
    [InlineData(2026, 9, 2026, 9, false)]
    [InlineData(2026, 10, 2026, 9, false)]
    public void IsBefore_orders_by_year_then_month(int year, int month, int otherYear, int otherMonth, bool expected)
    {
        Assert.Equal(expected, new BillingPeriod(year, month).IsBefore(new BillingPeriod(otherYear, otherMonth)));
    }

    [Fact]
    public void ToString_is_yyyy_MM()
    {
        Assert.Equal("2026-09", new BillingPeriod(2026, 9).ToString());
    }
}
