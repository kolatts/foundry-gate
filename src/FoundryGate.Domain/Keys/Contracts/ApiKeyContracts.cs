namespace FoundryGate.Domain.Keys.Contracts;

/// <summary>
/// A developer's APIM subscription key, masked to only the last 4 characters (spec
/// &#167;4.5: "get own APIM key (masked except last 4)"). Returned anywhere a key is
/// <em>displayed</em>: GET /keys/me, admin user list/detail views, and embedded in
/// <see cref="Users.Contracts.UserProfileResponse"/> for <c>/me</c>. Never returned by
/// rotate/provision — those hand back the one-time plaintext value instead, see
/// <see cref="ApiKeyRevealResponse"/>.
/// </summary>
/// <param name="IsProvisioned">False when the user has no APIM subscription yet (never logged in / not yet provisioned).</param>
/// <param name="MaskedKey">e.g. <c>"••••••••1a2b"</c>. Null when <paramref name="IsProvisioned"/> is false.</param>
/// <param name="ApimSubscriptionId">The APIM subscription resource ID (spec &#167;5.1). Null when not provisioned.</param>
public record ApiKeyResponse(
    bool IsProvisioned,
    string? MaskedKey,
    string? ApimSubscriptionId);

/// <summary>
/// The one-time plaintext delivery of an APIM subscription key. Returned ONLY by the
/// three endpoints that mint or regenerate a key — POST /keys/me/rotate, POST
/// /keys/{userId}/rotate, POST /keys/{userId}/provision (spec &#167;5.1 key
/// provisioning, &#167;5.2 key rotation) — the single moment the real key value crosses
/// the API boundary. Every other read of this user's key (GET /keys/me,
/// <see cref="Users.Contracts.UserProfileResponse.ApiKey"/>, admin lists/detail)
/// returns the masked <see cref="ApiKeyResponse"/> instead; the API must not persist
/// <see cref="PlaintextKey"/> anywhere it can be re-served later (spec &#167;5.1 step 4:
/// only the encrypted key is stored). The UI should render this value once, behind a
/// copy-to-clipboard affordance, and never fetch or display it again after this
/// response is consumed — that's what makes the corresponding
/// <c>getting-started/cli-setup.mdx</c> "Configure your CLI" panel possible at all.
/// </summary>
/// <param name="PlaintextKey">The real APIM subscription key value. Shown once; not retrievable again through any other endpoint.</param>
/// <param name="MaskedKey">The same masked form <see cref="ApiKeyResponse"/> would show, for the UI to fall back to display after this response leaves scope.</param>
/// <param name="ApimSubscriptionId">The APIM subscription resource ID (spec &#167;5.1).</param>
/// <param name="IssuedDate">When this plaintext value was generated (provisioning) or regenerated (rotation).</param>
public record ApiKeyRevealResponse(
    string PlaintextKey,
    string MaskedKey,
    string ApimSubscriptionId,
    DateTimeOffset IssuedDate);
