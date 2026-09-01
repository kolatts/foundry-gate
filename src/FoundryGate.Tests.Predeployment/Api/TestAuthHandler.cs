using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;

namespace FoundryGate.Tests.Predeployment.Api;

/// <summary>
/// Header-driven authentication scheme for integration tests. <c>ApiTestFactory</c> makes it the
/// default authenticate/challenge scheme so a request can act as any identity without a real Entra
/// token: <see cref="OidHeader"/> becomes the <c>oid</c> claim, <see cref="RolesHeader"/>
/// (comma-separated) becomes <c>roles</c> claims, <see cref="NameHeader"/>/<see cref="EmailHeader"/>
/// become <c>name</c>/<c>preferred_username</c>. No <see cref="OidHeader"/> → no result → the global
/// <c>AuthorizeFilter</c> challenges → 401, exactly as an anonymous caller sees in production.
/// </summary>
/// <remarks>
/// The identity is built with <c>roles</c> as its role claim type, matching what
/// Microsoft.Identity.Web configures on real Entra tokens, so <c>IsInRole</c> — and therefore
/// <c>PolicyNames.AdminOnly</c> and <c>ICurrentUserAccessor.IsAdmin</c> — behave identically here
/// and in production.
/// </remarks>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string OidHeader = "X-Test-Oid";
    public const string RolesHeader = "X-Test-Roles";
    public const string NameHeader = "X-Test-Name";
    public const string EmailHeader = "X-Test-Email";

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(OidHeader, out var oidValues) || string.IsNullOrWhiteSpace(oidValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var claims = new List<Claim> { new(ClaimConstants.Oid, oidValues.ToString()) };

        if (Request.Headers.TryGetValue(NameHeader, out var nameValues) && !string.IsNullOrWhiteSpace(nameValues))
        {
            claims.Add(new Claim(ClaimConstants.Name, nameValues.ToString()));
        }

        if (Request.Headers.TryGetValue(EmailHeader, out var emailValues) && !string.IsNullOrWhiteSpace(emailValues))
        {
            claims.Add(new Claim(ClaimConstants.PreferredUserName, emailValues.ToString()));
        }

        if (Request.Headers.TryGetValue(RolesHeader, out var roleValues))
        {
            claims.AddRange(roleValues.ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(role => new Claim(ClaimConstants.Roles, role)));
        }

        var identity = new ClaimsIdentity(claims, SchemeName, nameType: ClaimConstants.Name, roleType: ClaimConstants.Roles);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
