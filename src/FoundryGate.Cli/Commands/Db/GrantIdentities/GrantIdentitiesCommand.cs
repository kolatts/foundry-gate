using System.CommandLine;
using FoundryGate.Cli.Helpers;
using Microsoft.Data.SqlClient;

namespace FoundryGate.Cli.Commands.Db.GrantIdentities;

/// <summary>
/// <c>foundrygate db grant-identities --env &lt;env&gt; [--api-identity &lt;name&gt;] [--functions-identity &lt;name&gt;]
/// [--api-identity-client-id &lt;guid&gt;] [--functions-identity-client-id &lt;guid&gt;] [--connection-string &lt;cs&gt;]
/// [--dry-run] [--server &lt;name&gt;] [--resource-group &lt;rg&gt;] [--subscription &lt;id&gt;]</c> — creates the
/// contained database users for the API and Functions managed identities after the dacpac deploy (#106).
/// A managed identity cannot be granted database access through ARM; this is the T-SQL step Bicep cannot
/// express (<c>infra/modules/sql.bicep</c> header). Runs under the same Entra-authenticated connection the
/// deploy already uses, so the executing principal must be in the SQL Entra admin group (#109).
/// </summary>
internal sealed class GrantIdentitiesCommand : Command
{
    public GrantIdentitiesCommand() : base("grant-identities", "Creates contained database users (db_datareader + db_datawriter) for the API and Functions managed identities")
    {
        var envOption = new Option<string>("--env")
        {
            Description = "Target environment (dev, prod; GitHub Environment names like 'production' are accepted)",
            Required = true
        };

        var apiIdentityOption = new Option<string?>("--api-identity")
        {
            Description = "API managed identity name (default: id-foundrygate-api-{env})"
        };

        var functionsIdentityOption = new Option<string?>("--functions-identity")
        {
            Description = "Functions managed identity name (default: id-foundrygate-func-{env})"
        };

        var apiClientIdOption = new Option<Guid?>("--api-identity-client-id")
        {
            Description = "API identity client id; when set the user is created WITH SID (no Directory Readers needed) instead of FROM EXTERNAL PROVIDER"
        };

        var functionsClientIdOption = new Option<Guid?>("--functions-identity-client-id")
        {
            Description = "Functions identity client id; when set the user is created WITH SID (no Directory Readers needed) instead of FROM EXTERNAL PROVIDER"
        };

        var connectionStringOption = new Option<string?>("--connection-string")
        {
            Description = "Entra-auth connection string to the target database (default: built from the resolved sql-foundrygate-{env}-* server and sqldb-foundrygate-{env})"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Print the T-SQL that would run without connecting to the database"
        };

        var serverOption = new Option<string?>("--server")
        {
            Description = "SQL server name used to build the default connection string (default: the single sql-foundrygate-{env}-* server in the resource group)"
        };

        var resourceGroupOption = new Option<string?>("--resource-group")
        {
            Description = "Resource group containing the server (default: rg-foundrygate-{env})"
        };

        var subscriptionOption = new Option<string?>("--subscription")
        {
            Description = "Azure subscription id (default: $AZURE_SUBSCRIPTION_ID, then the credential's default subscription)"
        };

        Add(envOption);
        Add(apiIdentityOption);
        Add(functionsIdentityOption);
        Add(apiClientIdOption);
        Add(functionsClientIdOption);
        Add(connectionStringOption);
        Add(dryRunOption);
        Add(serverOption);
        Add(resourceGroupOption);
        Add(subscriptionOption);

        SetAction(async (parseResult, cancellationToken) =>
        {
            try
            {
                var environment = FoundryGateAzureResources.NormalizeEnvironment(parseResult.GetValue(envOption)!);
                var grants = BuildGrants(
                    environment,
                    parseResult.GetValue(apiIdentityOption),
                    parseResult.GetValue(apiClientIdOption),
                    parseResult.GetValue(functionsIdentityOption),
                    parseResult.GetValue(functionsClientIdOption));

                var dryRun = parseResult.GetValue(dryRunOption);
                var connectionString = dryRun
                    ? null
                    : await ResolveConnectionStringAsync(
                        environment,
                        parseResult.GetValue(connectionStringOption),
                        parseResult.GetValue(serverOption),
                        parseResult.GetValue(resourceGroupOption),
                        parseResult.GetValue(subscriptionOption),
                        cancellationToken);

                ISqlBatchExecutor executor = connectionString is null
                    ? new NoOpSqlBatchExecutor()
                    : new SqlClientBatchExecutor(connectionString);

                _ = await new GrantIdentitiesRunner(executor, Console.Out).RunAsync(grants, dryRun, cancellationToken);
                return 0;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or SqlException or Azure.RequestFailedException)
            {
                Console.Error.WriteLine($"db grant-identities failed: {ex.Message}");
                return 1;
            }
        });
    }

    /// <summary>Applies the naming-convention defaults for whichever identity names were not given explicitly.</summary>
    internal static IReadOnlyList<ContainedUserGrant> BuildGrants(
        string environment,
        string? apiIdentity,
        Guid? apiClientId,
        string? functionsIdentity,
        Guid? functionsClientId)
    {
        return
        [
            new ContainedUserGrant(
                string.IsNullOrWhiteSpace(apiIdentity) ? FoundryGateAzureResources.ApiIdentityName(environment) : apiIdentity.Trim(),
                apiClientId),
            new ContainedUserGrant(
                string.IsNullOrWhiteSpace(functionsIdentity) ? FoundryGateAzureResources.FunctionsIdentityName(environment) : functionsIdentity.Trim(),
                functionsClientId)
        ];
    }

    private static async Task<string> ResolveConnectionStringAsync(
        string environment,
        string? connectionString,
        string? serverName,
        string? resourceGroupName,
        string? subscriptionId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        var client = ArmAzureSqlServerClient.Create(subscriptionId);
        var (_, server) = await AzureSqlServerResolver.ResolveAsync(client, environment, serverName, resourceGroupName, cancellationToken);
        var databaseName = FoundryGateAzureResources.SqlDatabaseName(environment);
        Console.WriteLine($"Target database: {server.Fqdn}/{databaseName} (Entra auth)");

        return FoundryGateAzureResources.EntraConnectionString(server.Fqdn, databaseName);
    }

    /// <summary>Stands in for the database during <c>--dry-run</c>; the runner prints instead of executing.</summary>
    private sealed class NoOpSqlBatchExecutor : ISqlBatchExecutor
    {
        public Task ExecuteAsync(string sql, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
