using FoundryGate.Api.Services.Cost;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Constants;
using FoundryGate.Tests.Predeployment.Data;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace FoundryGate.Tests.Predeployment.Api.Services.Cost;

/// <summary>
/// <see cref="CostEstimator"/>: the one configuration read behind every estimated cost (#177) — that
/// it is read at all, that it is cached, and that a row written around the API cannot take the
/// dashboard down with it.
/// </summary>
public class CostEstimatorTests : InMemoryDatabaseTest
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    [Fact]
    public async Task No_rate_card_row_is_an_empty_card()
    {
        var card = await CreateEstimator().GetRateCardAsync(fresh: false, CancellationToken.None);

        Assert.Empty(card.Entries);
        Assert.Null(card.Estimate(1_000_000));
    }

    [Fact]
    public async Task The_stored_rate_card_is_what_prices_tokens()
    {
        await SeedRateCardAsync("""[{"modelPrefix":"*","inputPerMillion":3,"outputPerMillion":15}]""");

        var card = await CreateEstimator().GetRateCardAsync(fresh: false, CancellationToken.None);

        Assert.Equal(9m, card.BlendedRatePerMillion);
        Assert.Equal(90m, card.Estimate(10_000_000));
    }

    [Fact]
    public async Task A_malformed_stored_value_estimates_nothing_rather_than_failing_the_read()
    {
        // PUT /config/{key} validates this, so a broken row means someone wrote around the API. A
        // dashboard with no prices on it beats a dashboard that 500s over a price list.
        await SeedRateCardAsync("{ not a rate card }");

        var card = await CreateEstimator().GetRateCardAsync(fresh: false, CancellationToken.None);

        Assert.Empty(card.Entries);
        Assert.Null(card.Estimate(1_000_000));
    }

    [Fact]
    public async Task The_card_is_read_once_and_then_served_from_the_cache()
    {
        await SeedRateCardAsync("""[{"modelPrefix":"*","inputPerMillion":2,"outputPerMillion":2}]""");
        var estimator = CreateEstimator();

        var first = await estimator.GetRateCardAsync(fresh: false, CancellationToken.None);
        var second = await estimator.GetRateCardAsync(fresh: false, CancellationToken.None);

        // Same instance, not merely equal: the allocations list prices a whole page of rows, and one
        // configuration row per row would be the cost of the feature.
        Assert.Same(first, second);
        Assert.Equal(1, CountRateCardReads());
    }

    private CostEstimator CreateEstimator() => new(Context, _cache, NullLogger<CostEstimator>.Instance);

    private async Task SeedRateCardAsync(string value)
    {
        Context.SystemConfigurations.Add(new SystemConfiguration
        {
            Key = SystemConfigurationKeys.RateCard,
            Value = value,
        });
        await Context.SaveChangesAsync();
    }

    private int CountRateCardReads() =>
        ExecutedCommands.Count(sql =>
            sql.Contains("FROM \"SystemConfigurations\"", StringComparison.Ordinal)
            && sql.Contains("SELECT", StringComparison.Ordinal));
}
