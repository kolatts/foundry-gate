using FoundryGate.Domain.Constants;

namespace FoundryGate.Tests.Predeployment.Domain;

/// <summary>
/// The naming contract between <c>infra/modules/ai-gateway.bicep</c> and the control plane (#225).
/// A map written by a deploy and a map written by the API have to be the same document — the policy
/// substitutes it verbatim — so the named-value name and the pool↔backend mapping are checked here
/// against the literals the bicep uses, not against the service's own opinion of them.
/// </summary>
public class GatewayModelMapTests
{
    [Fact]
    public void The_named_value_name_is_the_one_the_bicep_creates()
    {
        // infra/modules/ai-gateway.bicep: 'fg-model-map-${tier.name}', one per quotaTiers entry.
        Assert.Equal("fg-model-map-standard", GatewayModelMap.NamedValueName(GatewayTiers.Standard));
        Assert.Equal("fg-model-map-unlimited", GatewayModelMap.NamedValueName(GatewayTiers.Unlimited));
    }

    [Fact]
    public void A_tier_id_in_the_wrong_case_still_names_the_same_named_value()
    {
        // Route values reach the service however the caller spelled them, and APIM named values are
        // created lower-case.
        Assert.Equal("fg-model-map-power", GatewayModelMap.NamedValueName("POWER"));
    }

    [Theory]
    [InlineData(GatewayModelMap.AnthropicPool, GatewayModelMap.AnthropicPoolBackend)]
    [InlineData(GatewayModelMap.OpenAiPool, "foundry-openai-fg-eastus2")]
    public void A_pool_resolves_to_the_backend_id_the_bicep_would_have_written(string pool, string expectedBackend) =>
        Assert.Equal(expectedBackend, GatewayModelMap.BackendForPool(pool, "fg-eastus2"));

    [Fact]
    public void An_unrecognised_pool_falls_back_to_the_anthropic_pool_exactly_as_the_bicep_does() =>
        // The bicep's own expression is `pool == 'openai' ? openai : pool`, so anything else is the pool.
        Assert.Equal(GatewayModelMap.AnthropicPoolBackend, GatewayModelMap.BackendForPool("something-else", "fg-eastus2"));

    [Theory]
    [InlineData(GatewayModelMap.AnthropicPoolBackend, GatewayModelMap.AnthropicPool)]
    [InlineData("foundry-openai-fg-eastus2", GatewayModelMap.OpenAiPool)]
    [InlineData(null, GatewayModelMap.AnthropicPool)]
    public void A_stored_backend_id_reads_back_as_the_pool_that_produced_it(string? backend, string expectedPool) =>
        Assert.Equal(expectedPool, GatewayModelMap.PoolForBackend(backend));

    [Fact]
    public void Every_pool_survives_the_round_trip_through_a_backend_id()
    {
        // What makes a map written by the API editable in the UI's own vocabulary afterwards.
        foreach (var pool in GatewayModelMap.Pools)
        {
            Assert.Equal(pool, GatewayModelMap.PoolForBackend(GatewayModelMap.BackendForPool(pool, "fg-eastus2")));
        }
    }
}
