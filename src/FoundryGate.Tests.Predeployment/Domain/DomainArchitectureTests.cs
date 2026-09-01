using System.Reflection;
using FoundryGate.Domain.Common;

namespace FoundryGate.Tests.Predeployment.Domain;

/// <summary>
/// FoundryGate.Domain must stay a pure enums/records/constants library with zero
/// dependencies (CONVENTIONS.md: "Domain (zero deps: enums, DTO records, exceptions,
/// validation)"). FoundryGate.Web (Blazor WASM) references Domain only — anything
/// Domain pulls in becomes part of the WASM client's dependency graph, and EF Core /
/// ASP.NET Core types have no business running in the browser.
/// </summary>
/// <remarks>
/// <see cref="Domain_assembly_references_exactly_the_allowlisted_BCL_assemblies"/>
/// reflects over the compiled FoundryGate.Domain assembly's own
/// <see cref="Assembly.GetReferencedAssemblies"/> manifest (its AssemblyRef metadata
/// table) rather than parsing the .csproj — but this only catches assemblies Domain's
/// own IL <em>directly</em> references. It does NOT catch a dependency that is present
/// on disk / in the lock file but never actually used by any type in Domain's code (a
/// stray <c>PackageReference</c> with zero call sites emits no AssemblyRef), and it
/// cannot see further than one hop — an allowed BCL assembly's own transitive
/// dependencies are irrelevant here since the BCL doesn't depend on EF
/// Core/ASP.NET Core/Azure SDK etc. The real "zero PackageReference/ProjectReference"
/// guarantee is the empty <c>FoundryGate.Domain.csproj</c> itself (see its comment);
/// this test is the automated, CI-enforced cross-check that whatever code lands in
/// Domain doesn't start actually pulling types from something Domain never declared a
/// reference to.
///
/// Deliberately an exact allowlist, not a prefix pattern like <c>"System."</c>: the
/// list below is every assembly this project happened to compile against as of this
/// writing (captured via a throwaway <c>ITestOutputHelper</c> probe run against the
/// built DLL). A new BCL assembly showing up (e.g. Domain code starts using
/// <c>System.Net.Http</c>) fails this test just as loudly as EF Core would, and must be
/// added here deliberately — with a one-line justification in the same PR — rather than
/// silently passing because it happened to start with "System.".
/// </remarks>
public class DomainArchitectureTests
{
    private static readonly Assembly DomainAssembly = typeof(PagedResult<>).Assembly;

    /// <summary>
    /// Every assembly FoundryGate.Domain.dll is allowed to reference, by exact name.
    /// Captured from a clean build's <see cref="Assembly.GetReferencedAssemblies"/> —
    /// re-capture and update deliberately (with justification) when Domain code starts
    /// using a new BCL type; never widen this to a prefix/wildcard match.
    /// </summary>
    private static readonly string[] AllowedAssemblyNames =
    [
        "System.Collections",
        "System.ComponentModel.Annotations",
        "System.Runtime",
    ];

    [Fact]
    public void Domain_assembly_references_exactly_the_allowlisted_BCL_assemblies()
    {
        List<string?> disallowed = DomainAssembly.GetReferencedAssemblies()
            .Where(referenced => !AllowedAssemblyNames.Contains(referenced.Name))
            .Select(referenced => referenced.Name)
            .ToList();

        Assert.True(
            disallowed.Count == 0,
            "FoundryGate.Domain references assemblies outside the allowlist: "
                + $"{string.Join(", ", disallowed)}. If this is an intentional new BCL "
                + "dependency, add it to AllowedAssemblyNames with a justification; if "
                + "it's EF Core/ASP.NET Core/Azure SDK/a third-party package, Domain "
                + "must not reference it (CONVENTIONS.md zero-dependency rule).");
    }

    [Fact]
    public void Domain_assembly_has_no_references_to_other_FoundryGate_projects()
    {
        // Domain must not depend on Data, Api, Web, Functions, or any other
        // FoundryGate.* project — it is the leaf of the dependency graph. Redundant
        // with the allowlist test above (FoundryGate.* names aren't in
        // AllowedAssemblyNames either) but kept as its own assertion so a failure here
        // reads immediately as "wrong architectural layer", not "update the allowlist".
        List<string?> foundryGateReferences = DomainAssembly.GetReferencedAssemblies()
            .Where(referenced => referenced.Name?.StartsWith("FoundryGate.", StringComparison.Ordinal) == true)
            .Select(referenced => referenced.Name)
            .ToList();

        Assert.True(
            foundryGateReferences.Count == 0,
            $"FoundryGate.Domain references other FoundryGate projects: {string.Join(", ", foundryGateReferences)}");
    }
}
