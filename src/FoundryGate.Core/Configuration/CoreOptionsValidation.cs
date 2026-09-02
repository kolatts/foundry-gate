using System.ComponentModel.DataAnnotations;

namespace FoundryGate.Core.Configuration;

/// <summary>
/// The bridge between a host's <c>AppSettings</c> and the option classes that live in Core (#119).
/// </summary>
/// <remarks>
/// <para>
/// <c>Imagile.Framework.Configuration</c>'s <c>ValidateRecursively()</c> recurses only into property
/// types <b>declared in the root object's own assembly</b> — deliberately, so it never walks off into
/// BCL or third-party graphs. <see cref="GatewayOptions"/> now lives in <c>FoundryGate.Core</c> rather
/// than in either host, so that rule would silently skip it: a fork could start with no
/// <c>Gateway:Tiers</c> at all and only find out when the first quota resolution ran.
/// </para>
/// <para>
/// So each host's <c>AppSettings</c> is an <see cref="IValidatableObject"/> that yields
/// <see cref="ValidateGateway"/>. Same errors, same <c>Gateway.Member</c> paths in the message, same
/// fail-fast-at-startup behaviour as before the move — and one place to add the next Core-owned
/// section to.
/// </para>
/// </remarks>
public static class CoreOptionsValidation
{
    /// <summary>
    /// Runs <paramref name="gateway"/>'s own data annotations and <see cref="GatewayOptions.Validate"/>,
    /// re-prefixing every member name with <paramref name="memberPrefix"/> so the aggregated startup
    /// message reads <c>Gateway.Tiers</c> exactly as it did when the type lived in the host.
    /// </summary>
    /// <param name="gateway">The bound <c>Gateway</c> section; <see langword="null"/> yields nothing (the host's own <c>[Required]</c> reports it).</param>
    /// <param name="memberPrefix">The host property's name — <c>Gateway</c>.</param>
    public static IEnumerable<ValidationResult> ValidateGateway(GatewayOptions? gateway, string memberPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(memberPrefix);

        if (gateway is null)
        {
            yield break;
        }

        var results = new List<ValidationResult>();
        _ = Validator.TryValidateObject(gateway, new ValidationContext(gateway), results, validateAllProperties: true);

        foreach (var result in results)
        {
            var members = result.MemberNames.Select(name => $"{memberPrefix}.{name}").ToList();
            yield return new ValidationResult(result.ErrorMessage, members.Count == 0 ? [memberPrefix] : members);
        }
    }
}
