using FoundryGate.Domain.Common;

namespace FoundryGate.Web.Services;

/// <summary>
/// Coarse outcome of one <see cref="IFoundryGateApiClient"/> call. FoundryGate.Api does
/// not exist yet at the time this shell ships (#48 is scaffold-only, no feature pages) —
/// every call site has to render something reasonable for "API is down", "not signed in
/// yet", and "resource doesn't exist" without treating any of them as an unhandled
/// exception. See <see cref="FoundryGateApiClient"/> for how each status is produced.
/// </summary>
public enum ApiCallStatus
{
    /// <summary>2xx with a body that deserialized successfully.</summary>
    Success,

    /// <summary>
    /// 401 — the bearer token was missing, expired, or rejected by the API — or MSAL
    /// itself couldn't silently acquire/refresh one (an
    /// <see cref="Microsoft.AspNetCore.Components.WebAssembly.Authentication.AccessTokenNotAvailableException"/>
    /// from <c>AuthorizationMessageHandler</c>: expired session, blocked third-party
    /// cookies, ...). Either way the caller's session needs re-establishing; the client
    /// deliberately does not navigate on this itself — see
    /// <see cref="FoundryGateApiClient"/>'s catch clauses.
    /// </summary>
    Unauthorized,

    /// <summary>403 — authenticated, but not authorized for this resource.</summary>
    Forbidden,

    /// <summary>404 — the resource doesn't exist (or, today, the API doesn't exist yet).</summary>
    NotFound,

    /// <summary>
    /// The call never got a response to interpret: DNS/TLS/connection failure, timeout, or
    /// a non-JSON/malformed body. Covers "the API isn't deployed yet" during this shell's
    /// lifetime.
    /// </summary>
    Unavailable,

    /// <summary>Any other non-success status code, surfaced with the API's <see cref="ApiError"/> body when one came back.</summary>
    Error,
}

/// <summary>Result envelope every <see cref="IFoundryGateApiClient"/> method returns instead of throwing.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Value">Populated only when <paramref name="Status"/> is <see cref="ApiCallStatus.Success"/>.</param>
/// <param name="Error">
/// The API's RFC 7807 body when one was returned and parsed; null for <see cref="ApiCallStatus.Unavailable"/>
/// or when the error response wasn't valid <see cref="ApiError"/> JSON.
/// </param>
/// <param name="Message">A friendly, UI-ready fallback string — always populated on failure, even without an <see cref="ApiError"/>.</param>
public record ApiCallResult<T>(ApiCallStatus Status, T? Value, ApiError? Error, string? Message)
{
    public bool IsSuccess => Status == ApiCallStatus.Success;

    public static ApiCallResult<T> Ok(T value) => new(ApiCallStatus.Success, value, null, null);

    public static ApiCallResult<T> Fail(ApiCallStatus status, string message, ApiError? error = null) =>
        new(status, default, error, message);
}
