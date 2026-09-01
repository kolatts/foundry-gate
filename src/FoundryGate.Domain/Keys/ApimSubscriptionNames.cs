using System.Globalization;

namespace FoundryGate.Domain.Keys;

/// <summary>
/// The naming contract between a FoundryGate user and their APIM subscription (spec &#167;5.1,
/// plans/21-provision-deprovision.md): the subscription's ARM name (<c>sid</c>) is
/// <c>foundrygate-{UserId}</c>. Lives in Domain — not the Api's key service — because two hosts
/// depend on it: the Api mints the name when provisioning, and the Functions usage-reconciliation
/// job (#84) reads the subscription id back out of <c>ApiManagementGatewayLlmLog</c> rows and needs
/// the inverse mapping to attribute tokens to a user. One definition, no drift.
/// </summary>
public static class ApimSubscriptionNames
{
    /// <summary>Prefix every FoundryGate-minted subscription name carries.</summary>
    public const string Prefix = "foundrygate-";

    /// <summary>The APIM subscription name for <paramref name="userId"/>, e.g. <c>foundrygate-42</c>.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="userId"/> is not positive — an unsaved <c>User</c> (identity not yet assigned) must not be named.</exception>
    public static string ForUser(int userId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userId);

        return Prefix + userId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Recovers the <c>UserId</c> from a subscription name minted by <see cref="ForUser"/>. Returns
    /// <see langword="false"/> for anything else — APIM's built-in <c>master</c> subscription,
    /// subscriptions created by hand in the portal, or a malformed suffix — so callers can skip
    /// rows that are not FoundryGate developers.
    /// </summary>
    public static bool TryGetUserId(string? subscriptionName, out int userId)
    {
        userId = 0;

        if (subscriptionName is null || !subscriptionName.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        // Substring rather than AsSpan: the span overload drags System.Memory into Domain's
        // reference set, which DomainArchitectureTests pins to an exact BCL allowlist.
        return int.TryParse(subscriptionName.Substring(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out userId)
            && userId > 0;
    }
}
