using FoundryGate.Cli.Helpers;

namespace FoundryGate.Tests.Predeployment.Cli;

public class RepoLocatorTests
{
    [Fact]
    public void FindRoot_walks_up_to_the_directory_containing_the_solution_file()
    {
        var root = Directory.CreateTempSubdirectory(nameof(RepoLocatorTests));
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, RepoLocator.SolutionFileName), string.Empty);
            var nested = Directory.CreateDirectory(Path.Combine(root.FullName, "a", "b", "c"));

            Assert.Equal(root.FullName, RepoLocator.FindRoot(nested.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void FindRoot_throws_when_no_solution_file_exists_above_the_starting_directory()
    {
        var root = Directory.CreateTempSubdirectory(nameof(RepoLocatorTests));
        try
        {
            // No FoundryGate.sln is dropped anywhere under `root`, but walking up from `root` will
            // eventually escape it into the real filesystem — which does contain FoundryGate.sln several
            // levels above this test's own temp directory in CI/dev. Point FindRoot at a path guaranteed
            // to have no solution file above it instead: the root of a drive/filesystem has no parent.
            var drive = Path.GetPathRoot(root.FullName)!;

            Assert.Throws<InvalidOperationException>(() => RepoLocator.FindRoot(drive));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void DatabaseSqlProjPath_points_at_the_checked_in_sqlproj()
    {
        var path = RepoLocator.DatabaseSqlProjPath(@"C:\repo");

        Assert.Equal(Path.Combine(@"C:\repo", "src", "FoundryGate.Database", "FoundryGate.Database.sqlproj"), path);
    }

    [Fact]
    public void FindRoot_locates_the_real_repo_from_the_test_assemblys_own_output_directory()
    {
        // The same real-world case CompareCommand hits: AppContext.BaseDirectory is several levels
        // under bin/, and FoundryGate.sln lives at the actual repo root.
        var root = RepoLocator.FindRoot(AppContext.BaseDirectory);

        Assert.True(File.Exists(Path.Combine(root, RepoLocator.SolutionFileName)));
        Assert.True(File.Exists(RepoLocator.DatabaseSqlProjPath(root)));
    }
}
