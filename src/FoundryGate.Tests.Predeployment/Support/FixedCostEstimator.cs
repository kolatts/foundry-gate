using System.Globalization;
using FoundryGate.Api.Services.Cost;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// An <see cref="ICostEstimator"/> that answers with a rate card the test chose, instead of reading
/// the <c>RateCard</c> configuration row (#177). The parsing and the configuration read are covered
/// on their own — <c>Api/Services/Cost/RateCardTests</c> and
/// <c>Api/Services/Cost/CostEstimatorTests</c> — so a dashboard or allocation test can say what a
/// token costs in one line.
/// </summary>
/// <remarks>Defaults to <see cref="RateCard.Empty"/>: no rate card is how a fork ships, so it is also the default a test inherits.</remarks>
public sealed class FixedCostEstimator(RateCard? rateCard = null) : ICostEstimator
{
    /// <summary>A rate card with a single blended <c>*</c> entry at <paramref name="perMillion"/> for both directions.</summary>
    public static RateCard Blended(decimal perMillion)
    {
        var rate = perMillion.ToString(CultureInfo.InvariantCulture);
        return RateCard.Parse($"[{{\"modelPrefix\":\"*\",\"inputPerMillion\":{rate},\"outputPerMillion\":{rate}}}]");
    }

    /// <summary>What every call answers. Settable so a test can change the prices mid-way.</summary>
    public RateCard RateCard { get; set; } = rateCard ?? RateCard.Empty;

    /// <inheritdoc />
    public Task<RateCard> GetRateCardAsync(CancellationToken cancellationToken) => Task.FromResult(RateCard);
}
