using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace FoundryGate.Web.Services;

/// <summary>
/// Reads the string constants off a Domain constants class so a filter dropdown offers exactly the
/// values the rest of the system writes — <c>AuditActions</c> and <c>AuditTargetTypes</c> are
/// deliberately open-ended string sets rather than enums (see their remarks), so the UI has no
/// enum to enumerate and restating the list here would guarantee it drifts.
/// </summary>
public static class ConstantSet
{
    /// <summary>
    /// Every <c>public const string</c> declared on <paramref name="type"/>, de-duplicated and
    /// ordered. Takes a <see cref="Type"/> rather than a generic parameter because the constants
    /// classes it reads are <c>static</c>, and a static type cannot be a type argument. The
    /// parameter is annotated so the WebAssembly trimmer keeps those fields: they are reachable
    /// only through this reflection call, and a trimmed-away constant would silently empty the
    /// dropdown in a published build.
    /// </summary>
    public static IReadOnlyList<string> StringConstants(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => field.GetRawConstantValue() as string)
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
    }
}
