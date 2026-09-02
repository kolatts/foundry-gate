namespace FoundryGate.Cli.Commands.Db.GrantIdentities;

/// <summary>What <c>db grant-identities</c> was asked to do, after option parsing.</summary>
/// <param name="Grants">The identities to provision, in the order they should be applied.</param>
/// <param name="DryRun">Print the batches instead of executing them (<c>--dry-run</c>).</param>
/// <param name="AllowExternalProvider">
/// <c>--allow-external-provider</c>: permits a grant with no client id, which is created
/// <c>FROM EXTERNAL PROVIDER</c> and therefore needs the SQL logical server's identity to hold the Entra
/// Directory Readers role. Off by default because <c>infra/modules/sql.bicep</c> declares no server
/// identity, so that path fails on FoundryGate's own infrastructure — see #106/#142.
/// </param>
public sealed record GrantIdentitiesRequest(
    IReadOnlyList<ContainedUserGrant> Grants,
    bool DryRun,
    bool AllowExternalProvider);

/// <summary>
/// Runs <see cref="ContainedUserSql"/> for each identity in turn, one batch per identity so a failure
/// names the principal it failed on. Nothing here knows about environments or Azure — the command resolves
/// names and the connection string, this just applies them.
/// </summary>
public sealed class GrantIdentitiesRunner(ISqlBatchExecutor? executor, TextWriter output)
{
    private readonly TextWriter _output = output ?? throw new ArgumentNullException(nameof(output));

    /// <summary>Provisions every grant; returns the batches that were executed (or, in a dry run, printed).</summary>
    public async Task<IReadOnlyList<string>> RunAsync(GrantIdentitiesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Grants);

        if (request.Grants.Count == 0)
        {
            throw new InvalidOperationException("At least one identity is required.");
        }

        var duplicate = request.Grants.GroupBy(g => g.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Identity '{duplicate.Key}' was specified more than once.");
        }

        if (!request.AllowExternalProvider)
        {
            var withoutClientId = request.Grants.Where(g => g.ClientId is null).Select(g => g.Name).ToList();
            if (withoutClientId.Count > 0)
            {
                throw new InvalidOperationException(
                    $"No client id was supplied for {string.Join(", ", withoutClientId)}, so the contained user(s) would be created with " +
                    "CREATE USER ... FROM EXTERNAL PROVIDER. That asks Azure SQL to resolve the name in Entra, which requires the logical " +
                    "server's own managed identity to hold the Directory Readers role — infra/modules/sql.bicep gives the server no identity, " +
                    "so it would fail at deploy time. Pass --api-identity-client-id / --functions-identity-client-id (the deployment outputs " +
                    "apiIdentityClientId / functionsIdentityClientId) to create the users WITH SID instead, which needs no directory lookup. " +
                    "Pass --allow-external-provider only if this server's identity really does have Directory Readers.");
            }
        }

        if (!request.DryRun && executor is null)
        {
            throw new InvalidOperationException("A SQL batch executor is required unless --dry-run is set.");
        }

        var batches = new List<string>(request.Grants.Count);
        foreach (var grant in request.Grants)
        {
            var sql = ContainedUserSql.Build(grant);
            batches.Add(sql);

            var how = grant.ClientId is null ? "FROM EXTERNAL PROVIDER" : $"WITH SID from client id {grant.ClientId:D}";
            if (request.DryRun)
            {
                _output.WriteLine($"-- {grant.Name} ({how}); roles: {string.Join(", ", ContainedUserSql.Roles)}");
                _output.WriteLine(sql);
                continue;
            }

            _output.WriteLine($"Granting {grant.Name} ({how}) membership of {string.Join(", ", ContainedUserSql.Roles)}...");
            await executor!.ExecuteAsync(sql, cancellationToken);
            _output.WriteLine($"  {grant.Name}: user present, roles ensured.");
        }

        if (!request.DryRun)
        {
            _output.WriteLine($"Contained users ensured for {request.Grants.Count} identit{(request.Grants.Count == 1 ? "y" : "ies")}.");
        }

        return batches;
    }
}
