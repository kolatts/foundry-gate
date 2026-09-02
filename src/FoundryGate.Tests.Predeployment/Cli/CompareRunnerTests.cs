using FoundryGate.Cli.Commands.Db.Compare;

namespace FoundryGate.Tests.Predeployment.Cli;

public class CompareRunnerTests
{
    private readonly StringWriter _output = new();

    [Fact]
    public void No_differences_exits_zero_and_never_publishes()
    {
        var comparer = new FakeSchemaComparer(new SchemaCompareOutcome([]));
        var runner = new CompareRunner(comparer, _output);

        var result = runner.Run(new CompareRequest(Apply: true));

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.HasDifferences);
        Assert.False(result.Published);
        Assert.Equal(0, comparer.PublishCallCount);
        Assert.Contains("Schema is up to date", _output.ToString());
    }

    [Fact]
    public void Differences_without_apply_reports_and_exits_nonzero_without_publishing()
    {
        var comparer = new FakeSchemaComparer(new SchemaCompareOutcome(
            [new SchemaDifferenceSummary("Tables", "[dbo].[Users]", "Change")]));
        var runner = new CompareRunner(comparer, _output);

        var result = runner.Run(new CompareRequest(Apply: false));

        Assert.Equal(1, result.ExitCode);
        Assert.True(result.HasDifferences);
        Assert.False(result.Published);
        Assert.Equal(0, comparer.PublishCallCount);
        Assert.Contains("Found 1 table difference(s):", _output.ToString());
        Assert.Contains("Change Tables [dbo].[Users]", _output.ToString());
    }

    [Fact]
    public void Differences_with_apply_publishes_and_exits_zero_on_success()
    {
        var comparer = new FakeSchemaComparer(
            new SchemaCompareOutcome([new SchemaDifferenceSummary("Tables", "[dbo].[Users]", "Change")]),
            new SchemaComparePublishOutcome(
                Success: true,
                ErrorMessage: null,
                ChangedFiles: ["dbo/Tables/Users.sql"],
                AddedFiles: [],
                DeletedFiles: []));
        var runner = new CompareRunner(comparer, _output);

        var result = runner.Run(new CompareRequest(Apply: true));

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.HasDifferences);
        Assert.True(result.Published);
        Assert.Equal(1, comparer.PublishCallCount);
        Assert.Contains("Changed: dbo/Tables/Users.sql", _output.ToString());
    }

    [Fact]
    public void Publish_failure_exits_nonzero_and_reports_the_error()
    {
        var comparer = new FakeSchemaComparer(
            new SchemaCompareOutcome([new SchemaDifferenceSummary("Tables", "[dbo].[Users]", "Change")]),
            new SchemaComparePublishOutcome(
                Success: false,
                ErrorMessage: "disk full",
                ChangedFiles: [],
                AddedFiles: [],
                DeletedFiles: []));
        var runner = new CompareRunner(comparer, _output);

        var result = runner.Run(new CompareRequest(Apply: true));

        Assert.Equal(1, result.ExitCode);
        Assert.True(result.HasDifferences);
        Assert.False(result.Published);
        Assert.Contains("Failed to regenerate FoundryGate.Database/dbo/Tables: disk full", _output.ToString());
    }

    [Fact]
    public void FormatDifferences_sorts_by_object_type_then_name()
    {
        var lines = CompareRunner.FormatDifferences(
        [
            new SchemaDifferenceSummary("Tables", "[dbo].[Users]", "Change"),
            new SchemaDifferenceSummary("Tables", "[dbo].[Groups]", "Add")
        ]);

        Assert.Equal(["Add Tables [dbo].[Groups]", "Change Tables [dbo].[Users]"], lines);
    }

    [Fact]
    public void FormatPublishSummary_reports_added_changed_and_deleted_separately()
    {
        var lines = CompareRunner.FormatPublishSummary(new SchemaComparePublishOutcome(
            Success: true,
            ErrorMessage: null,
            ChangedFiles: ["dbo/Tables/Users.sql"],
            AddedFiles: ["dbo/Tables/Widgets.sql"],
            DeletedFiles: ["dbo/Tables/Old.sql"]));

        Assert.Equal(
        [
            "Added: dbo/Tables/Widgets.sql",
            "Changed: dbo/Tables/Users.sql",
            "Deleted: dbo/Tables/Old.sql"
        ],
            lines);
    }

    [Fact]
    public void FormatPublishSummary_reports_a_no_op_when_nothing_changed_on_disk()
    {
        var lines = CompareRunner.FormatPublishSummary(new SchemaComparePublishOutcome(
            Success: true,
            ErrorMessage: null,
            ChangedFiles: [],
            AddedFiles: [],
            DeletedFiles: []));

        Assert.Equal(["Regenerated FoundryGate.Database/dbo/Tables (no file-level changes were needed)."], lines);
    }
}
