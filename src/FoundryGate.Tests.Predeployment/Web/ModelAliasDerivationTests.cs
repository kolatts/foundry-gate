using FoundryGate.Domain.Config;
using FoundryGate.Domain.Constants;
using FoundryGate.Web.Services;

namespace FoundryGate.Tests.Predeployment.Web;

/// <summary>
/// The two fields the alias dialog never asks for (#225). Both are derived from the deployment's ARM
/// <c>model.format</c> because getting either wrong produces a failure that looks like something
/// else: a Claude alias routed at the OpenAI backend dies as an opaque 404, and one declared with
/// the wrong provider is refused by the policy naming a base path the caller did not use.
/// </summary>
public class ModelAliasDerivationTests
{
    [Theory]
    [InlineData("Anthropic", ModelProviderType.Anthropic)]
    [InlineData("anthropic", ModelProviderType.Anthropic)]
    [InlineData("OpenAI", ModelProviderType.OpenAi)]
    [InlineData("", ModelProviderType.OpenAi)]
    public void The_front_door_follows_the_model_format(string modelFormat, ModelProviderType expected) =>
        Assert.Equal(expected, ModelAliasDerivation.ProviderFor(modelFormat));

    [Theory]
    [InlineData("Anthropic", GatewayModelMap.AnthropicPool)]
    [InlineData("OpenAI", GatewayModelMap.OpenAiPool)]
    public void The_backend_follows_the_model_format(string modelFormat, string expectedPool) =>
        Assert.Equal(expectedPool, ModelAliasDerivation.PoolFor(modelFormat));

    [Fact]
    public void A_claude_model_is_provisioned_into_every_region_and_an_openai_model_into_the_primary_one()
    {
        // infra/main.bicep's pooled / primary-only split: a Claude model has to exist everywhere,
        // because the pool sends a throttled request to another region.
        string[] accounts = ["fg-eastus2", "fg-swedencentral"];

        Assert.Equal(accounts, ModelAliasDerivation.DefaultAccountsFor("Anthropic", accounts));
        Assert.Equal(["fg-eastus2"], ModelAliasDerivation.DefaultAccountsFor("OpenAI", accounts));
    }

    [Fact]
    public void With_no_configured_accounts_there_is_nothing_to_pre_select() =>
        Assert.Empty(ModelAliasDerivation.DefaultAccountsFor("OpenAI", []));
}
