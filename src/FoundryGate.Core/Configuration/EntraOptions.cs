using System.ComponentModel.DataAnnotations;

namespace FoundryGate.Core.Configuration;

/// <summary>
/// Microsoft Graph directory access for the Entra sync features (issues #40, #41, #110) — the
/// <c>Entra</c> configuration section. Optional feature: <see cref="Enabled"/> is off by default so
/// local dev and the integration-test host start with no Graph connectivity at all
/// (CONVENTIONS.md: "Optional features carry <c>Enabled</c> so absent secrets don't kill startup
/// where the feature is off"). While disabled, <c>POST /users/sync</c> returns <c>400</c> with a
/// message pointing here.
/// </summary>
/// <remarks>
/// There is deliberately <b>no client secret</b> (#110). Graph is called with the app's registered
/// <c>TokenCredential</c> — the API's user-assigned managed identity in cloud (granted Microsoft
/// Graph <em>application</em> roles <c>Application.Read.All</c>, <c>User.Read.All</c> and
/// <c>GroupMember.ReadBasic.All</c> by the owner), the developer's Azure CLI login locally. Nothing to
/// store, nothing to rotate.
/// </remarks>
public class EntraOptions : IValidatableObject
{
    /// <summary>Public-cloud Microsoft Graph v1.0 endpoint.</summary>
    public const string DefaultGraphBaseUrl = "https://graph.microsoft.com/v1.0";

    /// <summary>
    /// Turns Graph-backed directory sync on. Off by default; turning it on requires the Graph
    /// application roles listed in the type remarks to have been granted to the API identity.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Object id of the <em>service principal</em> (enterprise application) whose app-role
    /// assignments define "who is a FoundryGate user". Optional: when absent the service principal
    /// backing <see cref="ApplicationClientId"/> is resolved once via
    /// <c>GET /servicePrincipals(appId='{clientId}')</c> and cached for the process lifetime. Set it
    /// explicitly only when developers are assigned to a <em>different</em> enterprise application
    /// than the API's own registration. Must be a GUID when present.
    /// </summary>
    public string? ServicePrincipalObjectId { get; set; }

    /// <summary>
    /// Client (application) id of the FoundryGate app registration whose service principal carries the
    /// user assignments — the app registration <c>AzureAd:ClientId</c> also names.
    /// </summary>
    /// <remarks>
    /// It is on <em>this</em> section rather than read from <c>AzureAd</c> because since #151 the
    /// directory client lives in Core and both hosts construct it, and the Functions worker has no
    /// <c>AzureAd</c> section at all — nothing there serves a request, so there is no token to
    /// validate. Infra sets <c>Entra__ApplicationClientId</c> on both hosts from the same
    /// <c>entraApiClientId</c> parameter that fills <c>AzureAd__ClientId</c> on the Api, and the Api's
    /// own registration falls back to <c>AzureAd:ClientId</c> when this is blank, so a fork on an
    /// older deployment keeps working. Only read when <see cref="ServicePrincipalObjectId"/> is absent.
    /// </remarks>
    public string? ApplicationClientId { get; set; }

    /// <summary>
    /// Graph endpoint including the API version. Forks in sovereign clouds override this (e.g.
    /// <c>https://graph.microsoft.us/v1.0</c>); the token scope is derived from its authority as
    /// <c>{scheme}://{host}/.default</c> — see <see cref="GraphScope"/>.
    /// </summary>
    public string GraphBaseUrl { get; set; } = DefaultGraphBaseUrl;

    /// <summary>The app-only token scope for <see cref="GraphBaseUrl"/>'s authority (<c>https://graph.microsoft.com/.default</c> by default).</summary>
    public string GraphScope => new Uri(GraphBaseUrl, UriKind.Absolute).GetLeftPart(UriPartial.Authority) + "/.default";

    /// <summary>
    /// Fail-fast, but only when <see cref="Enabled"/>: a disabled feature's settings are never
    /// read, so a stale or blank value must not stop a host that has the feature off.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Enabled)
        {
            yield break;
        }

        if (!Uri.TryCreate(GraphBaseUrl, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme != Uri.UriSchemeHttps)
        {
            yield return new ValidationResult(
                $"{nameof(GraphBaseUrl)} must be an absolute https URL (e.g. {DefaultGraphBaseUrl}) when {nameof(Enabled)} is true.",
                [nameof(GraphBaseUrl)]);
        }

        if (!string.IsNullOrWhiteSpace(ServicePrincipalObjectId) && !Guid.TryParse(ServicePrincipalObjectId, out _))
        {
            yield return new ValidationResult(
                $"{nameof(ServicePrincipalObjectId)} must be the service principal's object id (a GUID) when set.",
                [nameof(ServicePrincipalObjectId)]);
        }

        // NOT validated here: that one of ApplicationClientId / ServicePrincipalObjectId is present.
        // The Api fills ApplicationClientId from AzureAd:ClientId when its own registration builds the
        // directory client, which happens after ValidateRecursively() has run — so the rule would fire
        // on a perfectly good Api. It belongs to the host that has no such fallback, and lives on the
        // Functions AppSettings.
    }
}
