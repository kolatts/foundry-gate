namespace FoundryGate.Domain.Keys.Contracts;

/// <summary>
/// A developer's APIM subscription key, masked to only the last 4 characters (spec
/// &#167;4.5: "get own APIM key (masked except last 4)"). Returned by GET /keys/me,
/// POST /keys/me/rotate, POST /keys/{userId}/rotate, and POST /keys/{userId}/provision
/// — and embedded in <see cref="Users.Contracts.UserProfileResponse"/> for <c>/me</c>.
/// </summary>
/// <param name="IsProvisioned">False when the user has no APIM subscription yet (never logged in / not yet provisioned).</param>
/// <param name="MaskedKey">e.g. <c>"••••••••1a2b"</c>. Null when <paramref name="IsProvisioned"/> is false.</param>
/// <param name="ApimSubscriptionId">The APIM subscription resource ID (spec &#167;5.1). Null when not provisioned.</param>
public record ApiKeyResponse(
    bool IsProvisioned,
    string? MaskedKey,
    string? ApimSubscriptionId);
