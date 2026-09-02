namespace FoundryGate.Cli.Commands.Db.Compare;

/// <summary>One schema difference DacFx found, reduced to the shape <see cref="CompareRunner"/> needs to report it.</summary>
/// <param name="ObjectType">The DacFx object type the differing item belongs to (e.g. <c>Tables</c>).</param>
/// <param name="Name">DacFx's fully-qualified name for the object, e.g. <c>[dbo].[Users]</c>.</param>
/// <param name="Action">What publishing this difference would do: <c>Add</c>, <c>Change</c>, or <c>Delete</c>.</param>
public sealed record SchemaDifferenceSummary(string ObjectType, string Name, string Action);

/// <summary>The result of running the comparison: every difference DacFx found between the source database and the target project.</summary>
public sealed record SchemaCompareOutcome(IReadOnlyList<SchemaDifferenceSummary> Differences);

/// <summary>The result of publishing the comparison's differences back into the target project's <c>.sql</c> files.</summary>
/// <param name="Success">Whether DacFx wrote the project files without error.</param>
/// <param name="ErrorMessage">DacFx's error message when <paramref name="Success"/> is <see langword="false"/>.</param>
/// <param name="ChangedFiles">Project-relative paths of files whose content changed.</param>
/// <param name="AddedFiles">Project-relative paths of files newly created.</param>
/// <param name="DeletedFiles">Project-relative paths of files removed.</param>
public sealed record SchemaComparePublishOutcome(
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> AddedFiles,
    IReadOnlyList<string> DeletedFiles);

/// <summary>
/// The seam between <see cref="CompareRunner"/>'s decision/formatting logic (unit-tested with a fake) and
/// the real DacFx <c>SchemaComparison</c> call (<see cref="DacFxSchemaComparer"/>, exercised only by the
/// live proof — DacFx talks to a real SQL Server and a real <c>.sqlproj</c>, so there is nothing
/// meaningful to fake around the call itself).
/// </summary>
public interface ISchemaComparer
{
    /// <summary>Runs the comparison and returns every difference found (already scoped to tables only).</summary>
    SchemaCompareOutcome Compare();

    /// <summary>
    /// Publishes the differences found by the most recent <see cref="Compare"/> call into the target
    /// project. Throws <see cref="InvalidOperationException"/> if <see cref="Compare"/> has not run.
    /// </summary>
    SchemaComparePublishOutcome Publish();
}
