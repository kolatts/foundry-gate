using FoundryGate.Api.Configuration;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>
/// The <c>Security</c> section as shipped (#180/#181), for service-level tests that construct a
/// service directly. <see cref="RevealAnomaly"/> takes overrides so a test can put the threshold
/// within reach of a handful of reveals instead of writing ten of them.
/// </summary>
public static class TestSecurityOptions
{
    /// <summary>Shipped defaults: ten reveals in a rolling hour.</summary>
    public static RevealAnomalyOptions RevealAnomaly(int threshold = 10, int windowMinutes = 60) =>
        new() { Threshold = threshold, WindowMinutes = windowMinutes };

    /// <summary>Shipped defaults: 5 reveals and 3 rotations per minute per caller.</summary>
    public static KeyRateLimitOptions RateLimits() => new();
}
