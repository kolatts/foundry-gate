using FoundryGate.Cli.Commands.Db.GrantIdentities;

namespace FoundryGate.Tests.Predeployment.Cli;

public class GrantIdentitiesRunnerTests
{
    private readonly RecordingSqlBatchExecutor _executor = new();
    private readonly StringWriter _output = new();

    private GrantIdentitiesRunner CreateRunner() => new(_executor, _output);

    private static readonly IReadOnlyList<ContainedUserGrant> DevGrants =
    [
        new("id-foundrygate-api-dev", null),
        new("id-foundrygate-func-dev", null)
    ];

    [Fact]
    public async Task Executes_one_batch_per_identity_in_order()
    {
        var batches = await CreateRunner().RunAsync(DevGrants, dryRun: false, CancellationToken.None);

        Assert.Equal(2, batches.Count);
        Assert.Equal(batches, _executor.Executed);
        Assert.Contains("N'id-foundrygate-api-dev'", _executor.Executed[0]);
        Assert.Contains("N'id-foundrygate-func-dev'", _executor.Executed[1]);

        var output = _output.ToString();
        Assert.Contains("Granting id-foundrygate-api-dev (FROM EXTERNAL PROVIDER) membership of db_datareader, db_datawriter...", output);
        Assert.Contains("id-foundrygate-func-dev: user present, roles ensured.", output);
        Assert.Contains("Contained users ensured for 2 identities.", output);
    }

    [Fact]
    public async Task Running_twice_issues_the_identical_batches()
    {
        var first = await CreateRunner().RunAsync(DevGrants, dryRun: false, CancellationToken.None);
        var second = await CreateRunner().RunAsync(DevGrants, dryRun: false, CancellationToken.None);

        // Idempotency is in the T-SQL itself (IF NOT EXISTS / IS_ROLEMEMBER guards); the CLI never needs
        // to inspect state first, so re-running the deploy step is byte-for-byte the same work.
        Assert.Equal(first, second);
        Assert.All(first, sql => Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @principal)", sql));
    }

    [Fact]
    public async Task Dry_run_prints_the_batches_without_executing()
    {
        var batches = await CreateRunner().RunAsync(
            [new ContainedUserGrant("id-foundrygate-api-dev", Guid.Parse("11111111-2222-3333-4444-555555555555"))],
            dryRun: true,
            CancellationToken.None);

        Assert.Single(batches);
        Assert.Empty(_executor.Executed);
        var output = _output.ToString();
        Assert.Contains("-- id-foundrygate-api-dev (WITH SID from client id 11111111-2222-3333-4444-555555555555); roles: db_datareader, db_datawriter", output);
        Assert.Contains("CREATE USER", output);
        Assert.DoesNotContain("Contained users ensured", output);
    }

    [Fact]
    public async Task Stops_at_the_first_failing_identity()
    {
        _executor.FailOn = "id-foundrygate-func-dev";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunner().RunAsync(DevGrants, dryRun: false, CancellationToken.None));

        Assert.Contains("id-foundrygate-func-dev", ex.Message);
        Assert.Single(_executor.Executed);
    }

    [Fact]
    public async Task Rejects_an_empty_grant_list()
    {
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunner().RunAsync([], dryRun: false, CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_the_same_identity_twice()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunner().RunAsync(
            [new ContainedUserGrant("id-x", null), new ContainedUserGrant("ID-X", null)],
            dryRun: false,
            CancellationToken.None));

        Assert.Contains("more than once", ex.Message);
        Assert.Empty(_executor.Executed);
    }

    private sealed class RecordingSqlBatchExecutor : ISqlBatchExecutor
    {
        public List<string> Executed { get; } = [];

        public string? FailOn { get; set; }

        public Task ExecuteAsync(string sql, CancellationToken cancellationToken)
        {
            if (FailOn is not null && sql.Contains($"N'{FailOn}'", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"simulated failure for {FailOn}");
            }

            Executed.Add(sql);
            return Task.CompletedTask;
        }
    }
}
