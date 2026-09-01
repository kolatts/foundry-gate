using FoundryGate.Api.Configuration;
using FoundryGate.Api.Services.Keys;
using FoundryGate.Tests.Predeployment.Support;

namespace FoundryGate.Tests.Predeployment.Api.Services.Keys;

/// <summary>
/// What can be tested of the ARM client without Azure: construction guards and the scope → product
/// mapping. Live behaviour is the manual checklist in the follow-up issue this wave filed.
/// </summary>
public class ArmApimManagementClientTests
{
    private const string ServiceId = FakeApimManagementClient.ServiceId;

    [Theory]
    [InlineData(ServiceId + "/products/standard", "standard")]
    [InlineData(ServiceId + "/products/Power/", "Power")]
    [InlineData("/products/unlimited", "unlimited")]
    [InlineData(ServiceId + "/apis", null)]
    [InlineData(ServiceId + "/apis/anthropic", null)]
    [InlineData(ServiceId + "/products/", null)]
    [InlineData("", null)]
    public void ProductIdFromScope_extracts_the_product_segment_or_null(string scope, string? expected) =>
        Assert.Equal(expected, ArmApimManagementClient.ProductIdFromScope(scope));

    [Fact]
    public void Constructor_refuses_a_gateway_that_does_not_address_APIM()
    {
        var gateway = new GatewayOptions { ApimName = "apim-only" };

        Assert.Throws<ArgumentException>(() => new ArmApimManagementClient(gateway, new StaticTokenCredential()));
    }

    [Fact]
    public void Constructor_accepts_a_fully_addressed_gateway_without_calling_Azure()
    {
        var gateway = new GatewayOptions
        {
            SubscriptionId = "00000000-0000-0000-0000-000000000000",
            ResourceGroup = "rg-foundrygate-test",
            ApimName = "apim-foundrygate-test",
        };

        var client = new ArmApimManagementClient(gateway, new StaticTokenCredential());

        Assert.NotNull(client);
    }
}
