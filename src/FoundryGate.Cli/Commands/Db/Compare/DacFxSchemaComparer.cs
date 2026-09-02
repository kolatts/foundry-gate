using Microsoft.SqlServer.Dac;
using Microsoft.SqlServer.Dac.Compare;

namespace FoundryGate.Cli.Commands.Db.Compare;

/// <summary>
/// Wraps the real DacFx <see cref="SchemaComparison"/>: source = the live local database (kept current
/// by <c>foundrygate local setup</c>'s <c>EnsureCreated</c> run against the current EF model), target =
/// the FoundryGate.Database <c>.sqlproj</c> folder (<see cref="SchemaCompareProjectEndpoint"/>, one file
/// per schema object under <c>dbo/Tables</c> per CONVENTIONS.md's Schema pipeline).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Publish"/> uses DacFx's own <c>PublishChangesToProject</c> — the same call
/// <c>SqlPackage</c>/SSDT's schema-compare tooling uses — rather than a hand-rolled SQL renderer. The
/// checked-in <c>dbo/Tables/*.sql</c> files already look like that tool's output (bracket-quoted
/// identifiers, aligned column lists, one <c>GO</c> batch per statement); regenerating through the same
/// engine that produces that shape is both the most faithful way to keep matching it and the least code
/// to get subtly wrong.
/// </para>
/// <para>
/// This class is deliberately not unit tested: it is not logic, it is a thin call into native SQL
/// Server tooling that needs a real connection and a real <c>.sqlproj</c> to do anything. There is
/// nothing to fake around it — <see cref="CompareRunner"/> holds all of the decision and formatting
/// logic, tested against the <see cref="ISchemaComparer"/> seam this class implements. This class itself
/// is exercised by the PR's live proof (docker SQL Server, a clean compare and a deliberate-drift
/// compare) instead.
/// </para>
/// </remarks>
public sealed class DacFxSchemaComparer(string connectionString, string projectPath) : ISchemaComparer
{
    private readonly string _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    private readonly string _projectPath = projectPath ?? throw new ArgumentNullException(nameof(projectPath));
    private SchemaComparisonResult? _result;

    public SchemaCompareOutcome Compare()
    {
        var source = new SchemaCompareDatabaseEndpoint(_connectionString);
        var target = new SchemaCompareProjectEndpoint(_projectPath, [], "dbo", DacExtractTarget.SchemaObjectType);
        var comparison = new SchemaComparison(source, target)
        {
            Options = SchemaCompareOptions.Create()
        };

        var result = comparison.Compare();
        _result = result;

        var errors = result.GetErrors().Where(message => message.MessageType == DacMessageType.Error).ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Schema comparison failed: {string.Join("; ", errors.Select(error => error.Message))}");
        }

        var differences = result.Differences
            .Select(diff => new SchemaDifferenceSummary(
                diff.SourceObject?.ObjectType.ToString() ?? diff.TargetObject?.ObjectType.ToString() ?? "(unknown)",
                diff.Name,
                diff.UpdateAction.ToString()))
            .ToList();

        return new SchemaCompareOutcome(differences);
    }

    public SchemaComparePublishOutcome Publish()
    {
        if (_result is null)
        {
            throw new InvalidOperationException($"{nameof(Compare)}() must run before {nameof(Publish)}().");
        }

        var projectDirectory = Path.GetDirectoryName(_projectPath)
            ?? throw new InvalidOperationException($"Could not determine the directory for {_projectPath}.");

        var publishResult = _result.PublishChangesToProject(projectDirectory, DacExtractTarget.SchemaObjectType);

        return new SchemaComparePublishOutcome(
            publishResult.Success,
            publishResult.ErrorMessage,
            publishResult.ChangedFiles ?? [],
            publishResult.AddedFiles ?? [],
            publishResult.DeletedFiles ?? []);
    }
}
