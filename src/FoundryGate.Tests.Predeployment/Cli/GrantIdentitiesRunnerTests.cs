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

    private static readonly IReadOnlyList<ContainedUserGrant> DevGrantsWithClientIds =
    [
        new("id-foundrygate-api-dev", Guid.Parse("11111111-2222-3333-4444-555555555555")),
        new("id-foundrygate-func-dev", Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa"))
    ];

    /// <summary>
    /// The FROM EXTERNAL PROVIDER path, which only an operator who granted the server Directory Readers
    /// may take — the tests below use it to exercise the runner's mechanics; the client-id path is the default.
    /// </summary>
    private static GrantIdentitiesRequest Request(IReadOnlyList<ContainedUserGrant> grants) =>
        new(grants, DryRun: false, AllowExternalProvider: true);

    [Fact]
    public async Task Executes_one_batch_per_identity_in_order()
    {
        var batches = await CreateRunner().RunAsync(Request(DevGrants), CancellationToken.None);

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
        var first = await CreateRunner().RunAsync(Request(DevGrants), CancellationToken.None);
        var second = await CreateRunner().RunAsync(Request(DevGrants), CancellationToken.None);

        // Idempotency is in the T-SQL itself (IF NOT EXISTS / IS_ROLEMEMBER guards); the CLI never needs
        // to inspect state first, so re-running the deploy step is byte-for-byte the same work.
        Assert.Equal(first, second);
        Assert.All(first, sql => Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @principal)", sql));
    }

    [Fact]
    public async Task Dry_run_prints_the_batches_without_executing()
    {
        var batches = await CreateRunner().RunAsync(
            new GrantIdentitiesRequest(
                [new ContainedUserGrant("id-foundrygate-api-dev", Guid.Parse("11111111-2222-3333-4444-555555555555"))],
                DryRun: true,
                AllowExternalProvider: false),
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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunner().RunAsync(Request(DevGrants), CancellationToken.None));

        Assert.Contains("id-foundrygate-func-dev", ex.Message);
        Assert.Single(_executor.Executed);
    }

    [Fact]
    public async Task Rejects_an_empty_grant_list()
    {
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunner().RunAsync(Request([]), CancellationToken.None));
    }

    [Fact]
    public async Task Rejects_the_same_identity_twice()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunner().RunAsync(
            Request([new ContainedUserGrant("id-x", null), new ContainedUserGrant("ID-X", null)]),
            CancellationToken.None));

        Assert.Contains("more than once", ex.Message);
        Assert.Empty(_executor.Executed);
    }

    [Fact]
    public async Task Refuses_an_identity_without_a_client_id_unless_external_provider_is_explicitly_allowed()
    {
        // infra/modules/sql.bicep gives the logical server no managed identity, so FROM EXTERNAL PROVIDER
        // has no way to resolve the name in Entra — failing here beats failing halfway through a deploy.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunner().RunAsync(
            new GrantIdentitiesRequest(DevGrants, DryRun: false, AllowExternalProvider: false),
            CancellationToken.None));

        Assert.Contains("id-foundrygate-api-dev", ex.Message);
        Assert.Contains("id-foundrygate-func-dev", ex.Message);
        Assert.Contains("Directory Readers", ex.Message);
        Assert.Contains("--api-identity-client-id", ex.Message);
        Assert.Contains("--allow-external-provider", ex.Message);
        Assert.Empty(_executor.Executed);
    }

    [Fact]
    public async Task Client_ids_are_the_default_path_and_need_no_external_provider_opt_in()
    {
        var batches = await CreateRunner().RunAsync(
            new GrantIdentitiesRequest(DevGrantsWithClientIds, DryRun: false, AllowExternalProvider: false),
            CancellationToken.None);

        Assert.Equal(2, batches.Count);
        Assert.All(batches, sql => Assert.DoesNotContain("FROM EXTERNAL PROVIDER", sql));
        Assert.All(batches, sql => Assert.Contains("WITH SID = ", sql));
        Assert.Contains("WITH SID from client id 11111111-2222-3333-4444-555555555555", _output.ToString());
    }

    [Fact]
    public async Task A_dry_run_still_refuses_the_external_provider_path()
    {
        // One invariant, not two: --dry-run prints exactly the batches a real run would issue.
        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateRunner().RunAsync(
            new GrantIdentitiesRequest(DevGrants, DryRun: true, AllowExternalProvider: false),
            CancellationToken.None));
    }

    [Fact]
    public async Task A_dry_run_needs_no_executor_but_a_real_run_does()
    {
        var runner = new GrantIdentitiesRunner(executor: null, _output);

        _ = await runner.RunAsync(new GrantIdentitiesRequest(DevGrantsWithClientIds, DryRun: true, AllowExternalProvider: false), CancellationToken.None);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(
            new GrantIdentitiesRequest(DevGrantsWithClientIds, DryRun: false, AllowExternalProvider: false),
            CancellationToken.None));

        Assert.Contains("--dry-run", ex.Message);
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
