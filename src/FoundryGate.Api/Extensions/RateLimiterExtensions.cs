using System.Globalization;
using System.Threading.RateLimiting;
using FoundryGate.Domain.Constants;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Identity.Web;

namespace FoundryGate.Api.Extensions;

/// <summary>
/// Per-user rate limiting for the two routes that hand a developer their own gateway credential
/// (#136): <c>POST /keys/me/reveal</c> decrypts and returns the plaintext key, and
/// <c>POST /keys/me/rotate</c> mints a new one. Both are audited, neither was throttled — so a
/// leaked bearer token could replay them indefinitely and the only trace was a growing run of
/// <c>key.revealed</c> rows.
/// </summary>
/// <remarks>
/// <para>
/// <b>Partitioned on the caller's <c>oid</c>, not on IP</b> (the issue's explicit ask): the UI sits
/// behind a shared egress and admins share addresses, so an address partition would throttle a whole
/// office or nobody.
/// </para>
/// <para>
/// <b>An unauthenticated caller is not limited at all</b>, which is not the same as being cheap to
/// abuse. The global authorization is an MVC <c>AuthorizeFilter</c>, not endpoint metadata, so
/// <c>UseRateLimiter</c> runs <em>before</em> anything has rejected an anonymous request — and a single
/// shared "anonymous" partition would then let one scanner, or one UI holding an expired token, spend
/// the whole bucket and turn every other anonymous caller's <c>401</c> into a <c>429</c> (#184 review,
/// reproduced on the branch). There is nothing to protect on that path: the request reaches MVC, is
/// refused with a <c>401</c>, and touches no key material, no database and no gateway. So it gets
/// <see cref="RateLimitPartition.GetNoLimiter{TKey}"/> and the limit starts existing at the moment the
/// caller has an identity to attribute it to.
/// </para>
/// <para>
/// <b>Only the <c>/me</c> routes.</b> The admin routes (<c>POST /keys/{userId}/rotate</c>,
/// <c>POST /keys/{userId}/provision</c>, <c>DELETE /keys/{userId}</c>) are deliberately unlimited: an
/// admin rotating a compromised team's keys, or a script re-provisioning after an incident, is exactly
/// the traffic a limiter would get in the way of, and those routes never disclose the caller's own
/// credential to a token thief.
/// </para>
/// <para>
/// <b>Rejection is a ProblemDetails 429 with <c>Retry-After</c></b>, so the body matches every other
/// error the API produces (CONVENTIONS.md: one exception handler, ProblemDetails everywhere) and a
/// client can back off without parsing prose. The window is fixed rather than sliding: a fixed window
/// is the one the framework reports <c>RetryAfter</c> metadata for, and "wait until the window turns
/// over" is the honest instruction.
/// </para>
/// </remarks>
public static class RateLimiterExtensions
{
    /// <summary>The single unlimited partition every caller with no <c>oid</c> claim shares.</summary>
    private const string AnonymousPartitionKey = "anonymous";

    /// <summary>
    /// The window both policies count within. Constants rather than configuration for now — #181 moves
    /// them to the options pattern so a fork can retune them without recompiling — and a limiter is only
    /// half the answer: a patient drain stays inside the cap, which is what the reveal anomaly signal in
    /// #180 is for.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Reveals allowed per <see cref="Window"/> per user. The UI reveals at most once per page load;
    /// five leaves room for a developer flipping between tabs and still cuts a scripted drain down to a
    /// rate an audit review would catch.
    /// </summary>
    private const int RevealsPerWindow = 5;

    /// <summary>
    /// Rotations allowed per <see cref="Window"/> per user. Lower than reveal because rotation is a
    /// write the gateway feels: each call regenerates both APIM keys and breaks whatever the developer
    /// has configured, so nobody legitimately needs a fourth in a minute.
    /// </summary>
    private const int RotationsPerWindow = 3;

    /// <summary>
    /// Registers <see cref="RateLimitPolicyNames.KeyReveal"/> and
    /// <see cref="RateLimitPolicyNames.KeyRotate"/> as fixed-window policies partitioned on the caller's
    /// <c>oid</c>. Nothing is limited globally: a policy applies only where an action carries
    /// <c>[EnableRateLimiting]</c>.
    /// </summary>
    public static IServiceCollection AddFoundryGateRateLimiter(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddRateLimiter(options =>
        {
            options.AddPolicy(RateLimitPolicyNames.KeyReveal, httpContext => PerUser(httpContext, RevealsPerWindow));
            options.AddPolicy(RateLimitPolicyNames.KeyRotate, httpContext => PerUser(httpContext, RotationsPerWindow));

            options.OnRejected = async (context, cancellationToken) =>
            {
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var window) ? window : Window;

                // The caller is told "tell an administrator"; this is what tells the administrator. A
                // rejection is expected traffic shaping rather than a fault, so Information — but it
                // carries the partition key, because a run of these for one oid is the strongest signal
                // available that a drain is in progress, and it is what #180's anomaly detection builds on.
                context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(RateLimiterExtensions).FullName!)
                    .LogInformation(
                        "Rate limit {Policy} rejected {Method} {Path} for caller {PartitionKey}; retry after {RetryAfterSeconds}s.",
                        context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "(unnamed)",
                        context.HttpContext.Request.Method,
                        context.HttpContext.Request.Path,
                        context.HttpContext.User.GetObjectId() ?? "(anonymous)",
                        Math.Ceiling(retryAfter.TotalSeconds));

                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.HttpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

                var problemDetails = new ProblemDetails
                {
                    Title = "Too many requests",
                    Status = StatusCodes.Status429TooManyRequests,
                    Detail =
                        $"This endpoint is rate-limited per user. Wait {Math.Ceiling(retryAfter.TotalSeconds)} second(s) and try again. " +
                        "If you did not make these requests, your access token may be compromised — tell an administrator, who can revoke your key.",
                    Instance = context.HttpContext.Request.Path,
                };
                problemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier;

                await context.HttpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken);
            };
        });
    }

    /// <summary>
    /// One fixed-window limiter per caller identity, keyed on the <c>oid</c> claim; no limiter at all
    /// for a caller who has no identity yet (see the type remarks — they get a <c>401</c> from MVC a
    /// moment later, and sharing one bucket between all of them is how a scanner denies everyone else
    /// their 401).
    /// </summary>
    private static RateLimitPartition<string> PerUser(HttpContext httpContext, int permitLimit)
    {
        // GetObjectId() accepts both the short "oid" and the long objectidentifier claim type, the same
        // way ICurrentUserAccessor does, so the partition key is the identity the audit trail and the
        // User row are keyed on.
        var entraObjectId = httpContext.User.GetObjectId();

        return string.IsNullOrEmpty(entraObjectId)
            ? RateLimitPartition.GetNoLimiter(AnonymousPartitionKey)
            : RateLimitPartition.GetFixedWindowLimiter(
                entraObjectId,
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitLimit,
                    Window = Window,

                    // No queue: a caller past the limit should be told so immediately, not held on a socket.
                    QueueLimit = 0,
                    AutoReplenishment = true,
                });
    }
}
