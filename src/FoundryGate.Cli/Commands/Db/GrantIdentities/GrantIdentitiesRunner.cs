namespace FoundryGate.Cli.Commands.Db.GrantIdentities;

/// <summary>
/// Runs <see cref="ContainedUserSql"/> for each identity in turn, one batch per identity so a failure
/// names the principal it failed on. Nothing here knows about environments or Azure — the command resolves
/// names and the connection string, this just applies them.
/// </summary>
public sealed class GrantIdentitiesRunner(ISqlBatchExecutor executor, TextWriter output)
{
    private readonly ISqlBatchExecutor _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));

    /// <summary>Provisions every grant; returns the batches that were executed (or, in a dry run, printed).</summary>
    public async Task<IReadOnlyList<string>> RunAsync(IReadOnlyList<ContainedUserGrant> grants, bool dryRun, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grants);

        if (grants.Count == 0)
        {
            throw new InvalidOperationException("At least one identity is required.");
        }

        var duplicate = grants.GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Identity '{duplicate.Key}' was specified more than once.");
        }

        var batches = new List<string>(grants.Count);
        foreach (var grant in grants)
        {
            var sql = ContainedUserSql.Build(grant);
            batches.Add(sql);

            var how = grant.ClientId is null ? "FROM EXTERNAL PROVIDER" : $"WITH SID from client id {grant.ClientId:D}";
            if (dryRun)
            {
                _output.WriteLine($"-- {grant.Name} ({how}); roles: {string.Join(", ", ContainedUserSql.Roles)}");
                _output.WriteLine(sql);
                continue;
            }

            _output.WriteLine($"Granting {grant.Name} ({how}) membership of {string.Join(", ", ContainedUserSql.Roles)}...");
            await _executor.ExecuteAsync(sql, cancellationToken);
            _output.WriteLine($"  {grant.Name}: user present, roles ensured.");
        }

        if (!dryRun)
        {
            _output.WriteLine($"Contained users ensured for {grants.Count} identit{(grants.Count == 1 ? "y" : "ies")}.");
        }

        return batches;
    }
}
