using System.Reflection;
using FoundryGate.Domain.Common;

namespace FoundryGate.Tests.Predeployment.Domain;

/// <summary>
/// FoundryGate.Domain must stay a pure enums/records/constants library with zero
/// dependencies (CONVENTIONS.md: "Domain (zero deps: enums, DTO records, exceptions,
/// validation)"). FoundryGate.Web (Blazor WASM) references Domain only — anything
/// Domain pulls in becomes part of the WASM client's dependency graph, and EF Core /
/// ASP.NET Core types have no business running in the browser.
///
/// These tests reflect over the compiled FoundryGate.Domain assembly's own
/// <see cref="Assembly.GetReferencedAssemblies"/> manifest rather than parsing the
/// .csproj, so they fail if a disallowed dependency sneaks in transitively too (e.g.
/// via a future PackageReference that itself pulls in EF Core).
/// </summary>
public class DomainArchitectureTests
{
    private static readonly Assembly DomainAssembly = typeof(PagedResult<>).Assembly;

    private static readonly string[] DisallowedAssemblyNamePrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Microsoft.Data.SqlClient",
        "System.Data.SqlClient",
        "Azure.",
        "Microsoft.Azure.",
        "Microsoft.Graph",
    ];

    [Fact]
    public void Domain_assembly_references_no_disallowed_packages()
    {
        List<string?> violations = DomainAssembly.GetReferencedAssemblies()
            .Where(referenced => DisallowedAssemblyNamePrefixes.Any(prefix =>
                referenced.Name?.StartsWith(prefix, StringComparison.Ordinal) == true))
            .Select(referenced => referenced.Name)
            .ToList();

        Assert.True(
            violations.Count == 0,
            $"FoundryGate.Domain references disallowed assemblies: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Domain_assembly_has_no_references_to_other_FoundryGate_projects()
    {
        // Domain must not depend on Data, Api, Web, Functions, or any other
        // FoundryGate.* project — it is the leaf of the dependency graph.
        List<string?> foundryGateReferences = DomainAssembly.GetReferencedAssemblies()
            .Where(referenced => referenced.Name?.StartsWith("FoundryGate.", StringComparison.Ordinal) == true)
            .Select(referenced => referenced.Name)
            .ToList();

        Assert.True(
            foundryGateReferences.Count == 0,
            $"FoundryGate.Domain references other FoundryGate projects: {string.Join(", ", foundryGateReferences)}");
    }
}
