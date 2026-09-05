namespace FoundryGate.Api.Configuration;

/// <summary>
/// Which <c>aud</c> claim values the API accepts on an Entra bearer token.
/// <para>
/// The app registration sets <c>requestedAccessTokenVersion: 2</c>, so Entra mints v2 tokens whose
/// audience is the bare client id — <c>7e7d0561-…</c> — while a v1 token would carry the resource
/// URI <c>api://7e7d0561-…</c>. <c>AzureAd:Audience</c> is documented (and deployed by
/// <c>infra/modules/control-plane.bicep</c>) as <c>api://{clientId}</c>, and Microsoft.Identity.Web
/// validates against exactly that one string when it is set. The result, live, was that every real
/// token the SPA or the CLI could obtain was rejected with
/// <c>Bearer error="invalid_token", error_description="The audience '(null)' is invalid"</c> (#102).
/// </para>
/// <para>
/// Both forms name the same resource, so both are accepted. Nothing else is: an audience that is
/// neither the client id nor its <c>api://</c> URI belongs to a different app registration.
/// </para>
/// <para>
/// Deleting <c>AzureAd:Audience</c> would also have worked — Microsoft.Identity.Web falls back to
/// exactly these two forms when the setting is absent. The setting stays because a fork can expose
/// a custom application ID URI (<c>api://foundrygate.contoso.com</c>) that is derivable from
/// nothing, and honouring it costs one more entry in the list.
/// </para>
/// </summary>
public static class EntraAudiences
{
    /// <summary>Prefix Entra uses for an application ID URI.</summary>
    public const string ApplicationIdUriScheme = "api://";

    /// <summary>
    /// The accepted audiences for <paramref name="clientId"/>: the bare client id, its
    /// <c>api://</c> URI, and whatever <c>AzureAd:Audience</c> was configured as (which is normally
    /// one of the first two, but a fork may expose a custom application ID URI). Order is stable,
    /// duplicates and blanks are dropped.
    /// </summary>
    public static IReadOnlyList<string> Resolve(string? clientId, string? configuredAudience)
    {
        var candidates = new List<string>(3);

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            candidates.Add(clientId.Trim());
            candidates.Add(ApplicationIdUriScheme + clientId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(configuredAudience))
        {
            candidates.Add(configuredAudience.Trim());
        }

        return [.. candidates.Distinct(StringComparer.Ordinal)];
    }
}
