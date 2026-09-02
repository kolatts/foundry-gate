namespace FoundryGate.Domain.Constants;

/// <summary>
/// ASP.NET Core rate-limiting policy names registered in FoundryGate.Api's
/// <c>RateLimiterExtensions</c> and referenced from <c>[EnableRateLimiting(...)]</c> (#136).
/// </summary>
/// <remarks>
/// Every policy here partitions on the caller's Entra <c>oid</c>, never on IP: the UI sits behind a
/// shared egress and admins share addresses, so an IP partition would either throttle a whole office
/// or nobody at all. Only the <c>/me</c> key routes are limited — they are the ones a leaked bearer
/// token can replay for the plaintext credential.
/// </remarks>
public static class RateLimitPolicyNames
{
    /// <summary>
    /// <c>POST /keys/me/reveal</c>. A leaked bearer token can otherwise pull the plaintext key
    /// indefinitely, leaving nothing behind but a growing run of <c>key.revealed</c> audit rows.
    /// </summary>
    public const string KeyReveal = "KeyReveal";

    /// <summary>
    /// <c>POST /keys/me/rotate</c>. Less acute than reveal — each call mints a fresh key rather than
    /// disclosing the current one — but a token holder can still churn a developer's credentials.
    /// </summary>
    public const string KeyRotate = "KeyRotate";
}
