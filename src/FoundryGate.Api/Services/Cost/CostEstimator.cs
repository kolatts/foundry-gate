using FoundryGate.Data;
using FoundryGate.Domain.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FoundryGate.Api.Services.Cost;

/// <inheritdoc />
public sealed class CostEstimator(
    AppDbContext dbContext,
    IMemoryCache cache,
    ILogger<CostEstimator> logger) : ICostEstimator
{
    /// <summary><see cref="IMemoryCache"/> key of the parsed rate card.</summary>
    public const string CacheKey = "FoundryGate.Cost.RateCard";

    /// <summary>
    /// How long a parsed rate card is reused. Short enough that an admin who fixes a price on
    /// <c>/config</c> sees it on the next dashboard refresh, long enough that the row is not read
    /// once per allocation page.
    /// </summary>
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task<RateCard> GetRateCardAsync(bool fresh, CancellationToken cancellationToken)
    {
        if (!fresh && cache.TryGetValue(CacheKey, out RateCard? cached) && cached is not null)
        {
            return cached;
        }

        var stored = await dbContext.SystemConfigurations.AsNoTracking()
            .Where(c => c.Key == SystemConfigurationKeys.RateCard)
            .Select(c => c.Value)
            .SingleOrDefaultAsync(cancellationToken);

        RateCard card;
        try
        {
            card = RateCard.Parse(stored);
        }
        catch (ArgumentException exception)
        {
            // PUT /config/{key} validates this, so a bad value here means the row was written around
            // the API (a seed script, a DBA). Reads must not 500 over it: no prices is a worse
            // dashboard than one with prices, but a broken dashboard is worse than both.
            logger.LogError(exception, "The stored {Key} is not a valid rate card; no cost will be estimated until it is corrected via PUT /api/v1/config/{Key}.", SystemConfigurationKeys.RateCard, SystemConfigurationKeys.RateCard);
            card = RateCard.Empty;
        }

        _ = cache.Set(CacheKey, card, CacheDuration);
        return card;
    }

    /// <inheritdoc />
    public void Invalidate() => cache.Remove(CacheKey);
}
