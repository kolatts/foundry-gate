using FoundryGate.Domain.Quota;
using FoundryGate.Functions.Services.Usage;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// Hand-rolled <see cref="IUsageQueryClient"/> standing in for Azure Monitor Logs, which has no
/// emulator: the sync job's whole contract is "given these rows, do this to the database", so the rows
/// are the input. Set <see cref="Rows"/> per pass to simulate usage growing between ticks.
/// </summary>
public sealed class FakeUsageQueryClient(params SubscriptionUsage[] rows) : IUsageQueryClient
{
    /// <summary>The rows the next query returns.</summary>
    public List<SubscriptionUsage> Rows { get; } = [.. rows];

    /// <summary>Periods this fake was asked about, in order.</summary>
    public List<BillingPeriod> Queried { get; } = [];

    /// <summary>Set to throw from the next query — for the "workspace not configured" path.</summary>
    public Exception? Throws { get; set; }

    /// <inheritdoc />
    public Task<IReadOnlyList<SubscriptionUsage>> QueryPeriodUsageAsync(BillingPeriod period, CancellationToken cancellationToken)
    {
        Queried.Add(period);

        return Throws is not null
            ? Task.FromException<IReadOnlyList<SubscriptionUsage>>(Throws)
            : Task.FromResult<IReadOnlyList<SubscriptionUsage>>([.. Rows]);
    }
}
