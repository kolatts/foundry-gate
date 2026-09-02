namespace FoundryGate.Cli.Helpers;

/// <summary>
/// Finds the FoundryGate repo root by walking up from a starting directory looking for
/// <c>FoundryGate.sln</c> — the same technique
/// <c>FoundryGate.Tests.Predeployment.Data.Conventions.SchemaParityTests</c> uses to find
/// <c>dbo/Tables</c> from a test assembly's output directory. <c>db compare</c> needs the same trick:
/// unlike every other Cli command, it must locate <c>FoundryGate.Database.sqlproj</c> on disk rather
/// than just a connection string or an ARM resource, and the working directory `dotnet run`/the
/// installed tool is launched from is not reliably the repo root.
/// </summary>
public static class RepoLocator
{
    public const string SolutionFileName = "FoundryGate.sln";

    /// <summary>Walks up from <paramref name="startDirectory"/> until a directory containing <see cref="SolutionFileName"/> is found.</summary>
    public static string FindRoot(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException($"Could not find {SolutionFileName} by walking up from {startDirectory}.");
        }

        return directory.FullName;
    }

    /// <summary>Walks up from the running Cli's own base directory.</summary>
    public static string FindRoot() => FindRoot(AppContext.BaseDirectory);

    /// <summary>The path to <c>src/FoundryGate.Database/FoundryGate.Database.sqlproj</c> under a repo root.</summary>
    public static string DatabaseSqlProjPath(string repoRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);
        return Path.Combine(repoRoot, "src", "FoundryGate.Database", "FoundryGate.Database.sqlproj");
    }
}
