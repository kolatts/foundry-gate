using System.Xml.Linq;
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
/// One quirk needs fixing up after publish, discovered by this PR's own live proof: when a column
/// changes, DacFx rewrites the whole <c>CREATE TABLE</c> batch with the primary key declared inline,
/// but leaves this repo's original, separate <c>ALTER TABLE ... ADD CONSTRAINT [PK_x] PRIMARY KEY</c>
/// batch sitting untouched below it — two declarations of the same constraint, which still parses (and
/// still passes <c>SchemaParityTests</c>' first-match regex) but fails to deploy.
/// <see cref="SqlTableFileNormalizer"/> strips the leftover duplicate from every file <c>Publish</c>
/// touches.
/// </para>
/// <para>
/// This class is deliberately not unit tested: it is not logic, it is a thin call into native SQL
/// Server tooling that needs a real connection and a real <c>.sqlproj</c> to do anything. There is
/// nothing to fake around it — <see cref="CompareRunner"/> holds all of the decision and formatting
/// logic, and <see cref="SqlTableFileNormalizer"/> holds the one piece of real text-rewriting logic
/// here, both tested against fakes/fixtures. This class itself is exercised by the PR's live proof
/// (docker SQL Server, a clean compare, a deliberate-drift compare, and a full <c>db deploy</c> of the
/// regenerated file to confirm it does not just parse but actually deploys) instead.
/// </para>
/// </remarks>
public sealed class DacFxSchemaComparer(string connectionString, string projectPath) : ISchemaComparer
{
    private readonly string _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
    private readonly string _projectPath = projectPath ?? throw new ArgumentNullException(nameof(projectPath));
    private SchemaComparisonResult? _result;

    public SchemaCompareOutcome Compare()
    {
        var projectDirectory = Path.GetDirectoryName(_projectPath)
            ?? throw new InvalidOperationException($"Could not determine the directory for {_projectPath}.");

        // FoundryGate.Database.sqlproj is an SDK-style project (Sdk="Microsoft.Build.Sql") that globs
        // dbo/**/*.sql implicitly rather than listing files in explicit <Build Include> items.
        // SchemaCompareProjectEndpoint's own project reader only understands the latter — passed an
        // empty script list it silently loads a zero-object target model (no error, every source table
        // then compares as "Add"), so the actual .sql files have to be enumerated and handed in explicitly.
        var scriptFiles = Directory.GetFiles(projectDirectory, "*.sql", SearchOption.AllDirectories);

        var source = new SchemaCompareDatabaseEndpoint(_connectionString);
        var target = new SchemaCompareProjectEndpoint(_projectPath, scriptFiles, ReadDatabaseSchemaProvider(_projectPath), DacExtractTarget.SchemaObjectType);
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
                diff.SourceObject?.ObjectType.Name ?? diff.TargetObject?.ObjectType.Name ?? "(unknown)",
                diff.SourceObject?.Name.ToString() ?? diff.TargetObject?.Name.ToString() ?? diff.Name,
                diff.UpdateAction.ToString()))
            .ToList();

        return new SchemaCompareOutcome(differences);
    }

    /// <summary>
    /// Reads the <c>&lt;DSP&gt;</c> (Database Schema Provider) MSBuild property straight out of the
    /// <c>.sqlproj</c> — <see cref="SchemaCompareProjectEndpoint"/>'s third constructor argument needs
    /// it to interpret the project's SQL platform (e.g. <c>SqlAzureV12DatabaseSchemaProvider</c>), and
    /// hardcoding it here would silently drift from whatever <c>FoundryGate.Database.sqlproj</c> actually
    /// declares.
    /// </summary>
    private static string ReadDatabaseSchemaProvider(string projectPath)
    {
        var document = XDocument.Load(projectPath);
        var dsp = document.Descendants("DSP").FirstOrDefault()?.Value;

        return string.IsNullOrWhiteSpace(dsp)
            ? throw new InvalidOperationException($"{projectPath} has no <DSP> property; cannot determine its database schema provider.")
            : dsp;
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

        if (publishResult.Success)
        {
            // DacFx returns these as absolute paths in practice, but route them through the project
            // directory regardless: Path.Combine leaves an already-rooted path untouched, so this is
            // correct either way.
            foreach (var path in (publishResult.ChangedFiles ?? []).Concat(publishResult.AddedFiles ?? []))
            {
                NormalizeFile(Path.Combine(projectDirectory, path));
            }
        }

        return new SchemaComparePublishOutcome(
            publishResult.Success,
            publishResult.ErrorMessage,
            publishResult.ChangedFiles ?? [],
            publishResult.AddedFiles ?? [],
            publishResult.DeletedFiles ?? []);
    }

    /// <summary>Applies <see cref="SqlTableFileNormalizer"/> in place, rewriting the file only if it actually changed anything.</summary>
    private static void NormalizeFile(string path)
    {
        var original = File.ReadAllText(path);
        var normalized = SqlTableFileNormalizer.RemoveDuplicatePrimaryKeyAlterStatement(original);
        if (!string.Equals(original, normalized, StringComparison.Ordinal))
        {
            File.WriteAllText(path, normalized);
        }
    }
}
