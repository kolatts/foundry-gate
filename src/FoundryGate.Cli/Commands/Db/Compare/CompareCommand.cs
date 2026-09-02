using System.CommandLine;
using FoundryGate.Cli.Commands.Local.Setup;
using FoundryGate.Cli.Helpers;
using Microsoft.Data.SqlClient;

namespace FoundryGate.Cli.Commands.Db.Compare;

/// <summary>
/// <c>foundrygate db compare [--connection-string &lt;cs&gt;] [--apply] [--check]</c> — DacFx schema-compare
/// between a live SQL Server database (by default the local docker one <c>foundrygate local setup</c>
/// keeps current via <c>EnsureCreated</c> against the EF model) and FoundryGate.Database's checked-in
/// <c>dbo/Tables/*.sql</c>, per plans/23-database-tooling.md and #103.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately Windows-only: DacFx <c>SchemaComparison</c> needs native SQL Server tooling that only
/// ships for Windows. On another OS this fails fast with a message pointing at
/// <c>FoundryGate.Tests.Predeployment.Data.Conventions.SchemaParityTests</c> — the regex-level parity
/// test that is the cross-platform CI backstop this command complements (a developer convenience for
/// the "I changed an entity, regenerate the script for me" step) and never replaces.
/// </para>
/// <para>
/// With no flags, or with <c>--check</c>: prints the differences and exits non-zero if any exist — usable
/// as a local pre-commit gate. <c>--apply</c> additionally regenerates the affected table files via
/// DacFx's own <c>PublishChangesToProject</c> and exits 0 on success. Only table shape is compared —
/// <see cref="SchemaCompareOptions"/> excludes every other DacFx object type — and pure column reordering
/// is never reported (<see cref="SchemaCompareOptions.Create"/>'s <c>IgnoreColumnOrder</c>), so a clean
/// working tree with no real drift produces zero differences and <c>--apply</c> never touches a file.
/// </para>
/// </remarks>
internal sealed class CompareCommand : Command
{
    public CompareCommand() : base(
        "compare",
        "Compares a database's schema to FoundryGate.Database/dbo/Tables and optionally regenerates the .sql files (Windows-only, DacFx)")
    {
        var connectionStringOption = new Option<string>("--connection-string")
        {
            Description = "Connection string to the source database " +
                $"(default: the local docker SQL Server, '{SetupCommand.LocalConnectionString}')",
            DefaultValueFactory = _ => SetupCommand.LocalConnectionString
        };

        var applyOption = new Option<bool>("--apply")
        {
            Description = "Regenerate FoundryGate.Database/dbo/Tables/*.sql to match the source database"
        };

        var checkOption = new Option<bool>("--check")
        {
            Description = "Print differences and exit non-zero if any exist, without changing files " +
                "(this is also the default behavior when neither --apply nor --check is given)"
        };

        Add(connectionStringOption);
        Add(applyOption);
        Add(checkOption);

        SetAction((parseResult, cancellationToken) =>
        {
            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine(
                    "db compare requires DacFx schema-compare, which is Windows-only native SQL Server " +
                    "tooling. On macOS/Linux, rely on FoundryGate.Tests.Predeployment's SchemaParityTests " +
                    "instead — it checks the same EF-model-to-.sql drift cross-platform, without a database.");
                return Task.FromResult(1);
            }

            var connectionString = parseResult.GetValue(connectionStringOption)!;
            var apply = parseResult.GetValue(applyOption);

            // --check has no effect beyond documenting the default (report-only) behavior explicitly for
            // scripting callers; it is intentionally read but not branched on.
            _ = parseResult.GetValue(checkOption);

            return ExecuteAsync(connectionString, apply, cancellationToken);
        });
    }

    private static async Task<int> ExecuteAsync(string connectionString, bool apply, CancellationToken cancellationToken)
    {
        try
        {
            var repoRoot = RepoLocator.FindRoot();
            var projectPath = RepoLocator.DatabaseSqlProjPath(repoRoot);
            if (!File.Exists(projectPath))
            {
                throw new InvalidOperationException($"SQL project not found at {projectPath}.");
            }

            var databaseName = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
            Console.WriteLine($"Comparing {databaseName} to {Path.GetRelativePath(repoRoot, projectPath)}...");

            var comparer = new DacFxSchemaComparer(connectionString, projectPath);
            var runner = new CompareRunner(comparer, Console.Out);

            // DacFx's SchemaComparison.Compare()/PublishChangesToProject are blocking native calls —
            // run them off the calling thread the same way DeployCommand backgrounds DacServices.Deploy.
            var result = await Task.Run(() => runner.Run(new CompareRequest(apply)), cancellationToken);
            return result.ExitCode;
        }
        catch (Exception ex) when (CliErrors.IsExpected(ex))
        {
            Console.Error.WriteLine($"db compare failed: {CliErrors.Describe(ex)}");
            return 1;
        }
    }
}
