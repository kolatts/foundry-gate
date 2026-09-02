namespace FoundryGate.Cli.Commands.Db.Compare;

/// <summary>What <c>db compare</c> was asked to do, after option parsing.</summary>
/// <param name="Apply">Regenerate the target project's <c>.sql</c> files when differences are found (<c>--apply</c>).</param>
public sealed record CompareRequest(bool Apply);

/// <summary>The outcome <see cref="CompareCommand"/> turns into a process exit code.</summary>
/// <param name="ExitCode">0 on success (no differences, or <c>--apply</c> published successfully); 1 otherwise.</param>
/// <param name="HasDifferences">Whether the comparison found any table differences.</param>
/// <param name="Published">Whether <c>--apply</c> actually wrote changes (false when there was nothing to publish).</param>
public sealed record CompareRunnerResult(int ExitCode, bool HasDifferences, bool Published);

/// <summary>
/// Runs one comparison through <see cref="ISchemaComparer"/> and decides what to print and what exit
/// code to return. Nothing here knows about DacFx, connection strings, or file paths — the command
/// resolves those and builds the comparer; this just interprets the result, so it is fully testable
/// against a fake <see cref="ISchemaComparer"/> (same split as <c>GrantIdentitiesRunner</c> /
/// <c>ISqlBatchExecutor</c>).
/// </summary>
public sealed class CompareRunner(ISchemaComparer comparer, TextWriter output)
{
    private readonly ISchemaComparer _comparer = comparer ?? throw new ArgumentNullException(nameof(comparer));
    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));

    public CompareRunnerResult Run(CompareRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var outcome = _comparer.Compare();
        if (outcome.Differences.Count == 0)
        {
            _output.WriteLine("Schema is up to date: the source database matches FoundryGate.Database/dbo/Tables.");
            return new CompareRunnerResult(ExitCode: 0, HasDifferences: false, Published: false);
        }

        _output.WriteLine($"Found {outcome.Differences.Count} table difference(s):");
        foreach (var line in FormatDifferences(outcome.Differences))
        {
            _output.WriteLine($"  {line}");
        }

        if (!request.Apply)
        {
            return new CompareRunnerResult(ExitCode: 1, HasDifferences: true, Published: false);
        }

        var publish = _comparer.Publish();
        if (!publish.Success)
        {
            _output.WriteLine($"Failed to regenerate FoundryGate.Database/dbo/Tables: {publish.ErrorMessage}");
            return new CompareRunnerResult(ExitCode: 1, HasDifferences: true, Published: false);
        }

        foreach (var line in FormatPublishSummary(publish))
        {
            _output.WriteLine(line);
        }

        return new CompareRunnerResult(ExitCode: 0, HasDifferences: true, Published: true);
    }

    /// <summary>One <c>"{Action} {ObjectType} {Name}"</c> line per difference, sorted for stable output.</summary>
    public static IReadOnlyList<string> FormatDifferences(IReadOnlyList<SchemaDifferenceSummary> differences)
    {
        ArgumentNullException.ThrowIfNull(differences);

        return
        [.. differences
            .OrderBy(difference => difference.ObjectType, StringComparer.Ordinal)
            .ThenBy(difference => difference.Name, StringComparer.Ordinal)
            .Select(difference => $"{difference.Action} {difference.ObjectType} {difference.Name}")];
    }

    /// <summary>The added/changed/deleted file report for a successful <c>--apply</c>.</summary>
    public static IReadOnlyList<string> FormatPublishSummary(SchemaComparePublishOutcome publish)
    {
        ArgumentNullException.ThrowIfNull(publish);

        var lines = new List<string>();

        if (publish.AddedFiles.Count > 0)
        {
            lines.Add($"Added: {string.Join(", ", publish.AddedFiles.Order(StringComparer.Ordinal))}");
        }

        if (publish.ChangedFiles.Count > 0)
        {
            lines.Add($"Changed: {string.Join(", ", publish.ChangedFiles.Order(StringComparer.Ordinal))}");
        }

        if (publish.DeletedFiles.Count > 0)
        {
            lines.Add($"Deleted: {string.Join(", ", publish.DeletedFiles.Order(StringComparer.Ordinal))}");
        }

        if (lines.Count == 0)
        {
            lines.Add("Regenerated FoundryGate.Database/dbo/Tables (no file-level changes were needed).");
        }

        return lines;
    }
}
