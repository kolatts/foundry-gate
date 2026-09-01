using System.ComponentModel.DataAnnotations;
using FoundryGate.Api.Configuration;
using Imagile.Framework.Configuration.Exceptions;
using Imagile.Framework.Configuration.Extensions;

namespace FoundryGate.Tests.Predeployment.Api.Configuration;

/// <summary>
/// <see cref="EntraOptions"/> fail-fast rules: nothing is enforced while the feature is off; once on,
/// the Graph URL must be absolute https and an explicit service-principal id must be a GUID. Also
/// pins the scope derivation forks in sovereign clouds rely on.
/// </summary>
public class EntraOptionsValidationTests
{
    [Fact]
    public void Disabled_options_are_valid_whatever_the_other_values_are()
    {
        var options = new EntraOptions { Enabled = false, GraphBaseUrl = "not a url", ServicePrincipalObjectId = "not a guid" };

        Assert.Empty(Validate(options));
    }

    [Fact]
    public void Enabled_defaults_are_valid()
    {
        var options = new EntraOptions { Enabled = true };

        Assert.Empty(Validate(options));
        Assert.Equal("https://graph.microsoft.com/.default", options.GraphScope);
    }

    [Theory]
    [InlineData("")]
    [InlineData("graph.microsoft.com/v1.0")]
    [InlineData("http://graph.microsoft.com/v1.0")]
    public void Enabled_with_a_non_https_or_relative_GraphBaseUrl_fails(string graphBaseUrl)
    {
        var options = new EntraOptions { Enabled = true, GraphBaseUrl = graphBaseUrl };

        var result = Assert.Single(Validate(options));
        Assert.Contains(nameof(EntraOptions.GraphBaseUrl), result.MemberNames);
    }

    [Fact]
    public void Enabled_with_a_non_guid_ServicePrincipalObjectId_fails()
    {
        var options = new EntraOptions { Enabled = true, ServicePrincipalObjectId = "FoundryGate" };

        var result = Assert.Single(Validate(options));
        Assert.Contains(nameof(EntraOptions.ServicePrincipalObjectId), result.MemberNames);
    }

    [Fact]
    public void Sovereign_cloud_GraphBaseUrl_derives_its_own_scope()
    {
        var options = new EntraOptions { Enabled = true, GraphBaseUrl = "https://graph.microsoft.us/v1.0" };

        Assert.Empty(Validate(options));
        Assert.Equal("https://graph.microsoft.us/.default", options.GraphScope);
    }

    [Fact]
    public void AppSettings_ValidateRecursively_reports_Entra_violations_with_the_nested_member_name()
    {
        var appSettings = new AppSettings
        {
            AzureAd = new AzureAdOptions
            {
                TenantId = Guid.Empty.ToString(),
                ClientId = Guid.Empty.ToString(),
                Audience = "api://" + Guid.Empty,
            },
            ConnectionStrings = new ConnectionStringOptions { FoundryGate = "Server=localhost;Database=FoundryGate;" },
            Entra = new EntraOptions { Enabled = true, GraphBaseUrl = "http://graph.microsoft.com/v1.0" },
        };

        var exception = Assert.Throws<ConfigurationValidationException>(appSettings.ValidateRecursively);

        Assert.Contains("Entra.GraphBaseUrl", exception.Message, StringComparison.Ordinal);
    }

    private static List<ValidationResult> Validate(EntraOptions options) =>
        options.Validate(new ValidationContext(options)).ToList();
}
