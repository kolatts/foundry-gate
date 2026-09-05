using FoundryGate.Api.Configuration;

namespace FoundryGate.Tests.Predeployment.Api.Configuration;

/// <summary>
/// <see cref="EntraAudiences"/> decides which <c>aud</c> claims survive token validation. It exists
/// because the deployed configuration and the deployed app registration disagreed: infra sets
/// <c>AzureAd:Audience</c> to <c>api://{clientId}</c> while the registration's
/// <c>requestedAccessTokenVersion: 2</c> makes Entra mint tokens whose audience is the bare client
/// id, and pinning Microsoft.Identity.Web to the configured string alone rejected every real token
/// the API could ever be sent (#102). These are the cases that would have caught it offline.
/// </summary>
public class EntraAudiencesTests
{
    private const string ClientId = "7e7d0561-0973-411d-ba62-a667cbfec1d9";

    [Fact]
    public void Both_the_client_id_and_its_api_uri_are_accepted()
    {
        var audiences = EntraAudiences.Resolve(ClientId, $"api://{ClientId}");

        Assert.Contains(ClientId, audiences);
        Assert.Contains($"api://{ClientId}", audiences);
    }

    [Fact]
    public void The_deployed_configuration_shape_yields_exactly_the_two_forms_of_one_identifier()
    {
        // infra/modules/control-plane.bicep: AzureAd__Audience = api://{entraApiClientId}.
        var audiences = EntraAudiences.Resolve(ClientId, $"api://{ClientId}");

        Assert.Equal([ClientId, $"api://{ClientId}"], audiences);
    }

    [Fact]
    public void A_configured_audience_that_is_the_bare_client_id_does_not_duplicate_it()
    {
        var audiences = EntraAudiences.Resolve(ClientId, ClientId);

        Assert.Equal([ClientId, $"api://{ClientId}"], audiences);
    }

    [Fact]
    public void A_forks_custom_application_id_uri_is_kept_alongside_the_defaults()
    {
        var audiences = EntraAudiences.Resolve(ClientId, "api://foundrygate.contoso.com");

        Assert.Equal([ClientId, $"api://{ClientId}", "api://foundrygate.contoso.com"], audiences);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_configured_audience_still_leaves_the_client_id_forms(string? configured)
    {
        var audiences = EntraAudiences.Resolve(ClientId, configured);

        Assert.Equal([ClientId, $"api://{ClientId}"], audiences);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_client_id_leaves_only_the_configured_audience(string? clientId)
    {
        var audiences = EntraAudiences.Resolve(clientId, $"api://{ClientId}");

        Assert.Equal([$"api://{ClientId}"], audiences);
    }

    [Fact]
    public void Nothing_configured_yields_no_audiences_rather_than_a_wildcard()
    {
        Assert.Empty(EntraAudiences.Resolve(null, null));
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_so_a_padded_setting_still_matches()
    {
        var audiences = EntraAudiences.Resolve($"  {ClientId}  ", $"  api://{ClientId}  ");

        Assert.Equal([ClientId, $"api://{ClientId}"], audiences);
    }

    [Fact]
    public void An_unrelated_audience_is_not_accepted_merely_because_it_looks_like_an_api_uri()
    {
        var audiences = EntraAudiences.Resolve(ClientId, $"api://{ClientId}");

        Assert.DoesNotContain("api://00000000-0000-0000-0000-000000000000", audiences);
    }
}
