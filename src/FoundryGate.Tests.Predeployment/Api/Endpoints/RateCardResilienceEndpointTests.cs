using System.Net;
using System.Net.Http.Json;
using FoundryGate.Data.Entities;
using FoundryGate.Domain.Common;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Dashboard.Contracts;
using FoundryGate.Domain.Quota;
using FoundryGate.Domain.Quota.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FoundryGate.Tests.Predeployment.Api.Endpoints;

/// <summary>
/// A stored <c>RateCard</c> that <c>PUT /config/{key}</c> would never have accepted must not take a
/// read path down (#177 review). Rows do arrive around the API — a seed script, a DBA, a restored
/// backup — and the three endpoints that price tokens include
/// <c>GET /quota/allocations/me</c>, which every authenticated developer hits, so one bad row would
/// be a portal-wide outage rather than a missing number.
/// </summary>
/// <remarks>
/// The value used here is the reviewer's own probe: <see cref="decimal.MaxValue"/> in both prices,
/// which overflowed the blended-rate addition before <c>RateCard.MaxPricePerMillion</c>
/// existed. It is written straight to the table precisely because the validator now refuses it —
/// see <c>ConfigEndpointTests</c> for the <c>400</c> that keeps it out in the first place.
/// </remarks>
public class RateCardResilienceEndpointTests(ApiTestFactory factory) : IClassFixture<ApiTestFactory>
{
    private const string OverflowingRateCard =
        """[{"modelPrefix":"*","inputPerMillion":79228162514264337593543950335,"outputPerMillion":79228162514264337593543950335}]""";

    [Fact]
    public async Task Every_endpoint_that_prices_tokens_still_answers_200_and_simply_reports_no_cost()
    {
        var oid = Guid.NewGuid().ToString();
        var developer = await factory.SeedUserAsync(oid, configure: u => u.MonthlyTokenQuota = 5_000_000);
        await SeedAllocationAsync(developer.UserId);
        await StoreRateCardAsync(OverflowingRateCard);

        using var admin = factory.CreateClientAs(Guid.NewGuid().ToString(), isAdmin: true);
        using var dev = factory.CreateClientAs(oid);

        // ?fresh=true bypasses the dashboard's own cache, so this reads the poisoned row rather than
        // a summary computed before it was written.
        var dashboard = await admin.GetAsync(new Uri("/api/v1/dashboard?fresh=true", UriKind.Relative));
        var allocations = await admin.GetAsync(new Uri("/api/v1/quota/allocations", UriKind.Relative));
        var mine = await dev.GetAsync(new Uri("/api/v1/quota/allocations/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allocations.StatusCode);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);

        // Unknown, not zero, and not a wrong number: the row is unusable, so nothing is priced.
        var summary = await dashboard.Content.ReadFromJsonAsync<DashboardSummaryResponse>();
        Assert.Null(summary?.EstimatedCostThisPeriod);
        Assert.All(summary!.TopConsumers, c => Assert.Null(c.EstimatedCostThisPeriod));

        var page = await allocations.Content.ReadFromJsonAsync<PagedResult<QuotaAllocationResponse>>();
        Assert.All(page!.Items, a => Assert.Null(a.EstimatedCost));

        var allocation = await mine.Content.ReadFromJsonAsync<QuotaAllocationResponse>();
        Assert.Null(allocation?.EstimatedCost);
    }

    private async Task StoreRateCardAsync(string value)
    {
        await using var dbContext = factory.CreateDbContext();
        var row = await dbContext.SystemConfigurations.SingleAsync(c => c.Key == SystemConfigurationKeys.RateCard);
        row.Value = value;
        await dbContext.SaveChangesAsync();
    }

    private async Task SeedAllocationAsync(int userId)
    {
        var period = BillingPeriod.Current(factory.TimeProvider);
        await using var dbContext = factory.CreateDbContext();
        dbContext.QuotaAllocations.Add(new QuotaAllocation
        {
            UserId = userId,
            PeriodYear = period.Year,
            PeriodMonth = period.Month,
            AllocatedTokens = 5_000_000,
            TokensUsed = 1_000_000,
            ResolvedLevelType = QuotaLevelType.UserOverride,
            TierProductId = GatewayTiers.Standard,
        });
        await dbContext.SaveChangesAsync();
    }
}
