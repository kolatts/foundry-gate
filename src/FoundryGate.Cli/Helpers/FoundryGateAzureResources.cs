using System.Text.RegularExpressions;

namespace FoundryGate.Cli.Helpers;

/// <summary>
/// The Azure resource naming convention <c>infra/main.bicep</c> + <c>infra/modules/control-plane.bicep</c>
/// implement, mirrored in code so the CLI can address an environment's resources from <c>--env</c>
/// alone (documented in <c>docs-site/.../reference/infrastructure.md</c> "Naming convention" — changing
/// either side is a contract change). Only the names the CLI actually needs live here; DNS-global names
/// carry a <c>{suffix}</c> the CLI cannot know, so those are exposed as a <em>prefix</em> to match against
/// a resource listing instead.
/// </summary>
public static partial class FoundryGateAzureResources
{
    /// <summary>
    /// GitHub Environment names → Bicep <c>environmentName</c>. The deploy workflows are keyed on the
    /// GitHub Environment (<c>production</c>, for the deployment gate) while every Azure resource is
    /// named from the short Bicep value (<c>prod</c>); this is the single place that reconciles them.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> EnvironmentAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["production"] = "prod",
        ["development"] = "dev"
    };

    /// <summary>
    /// Maps a user/workflow-supplied environment to the lowercase Bicep <c>environmentName</c>
    /// (<c>test</c>, <c>dev</c>, <c>prod</c>, ...) every resource name embeds. Throws for values that
    /// cannot be part of an Azure resource name, so a typo fails here rather than as a confusing ARM 404.
    /// </summary>
    public static string NormalizeEnvironment(string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);

        var trimmed = environment.Trim();
        var normalized = EnvironmentAliases.TryGetValue(trimmed, out var alias) ? alias : trimmed.ToLowerInvariant();

        if (!EnvironmentNamePattern().IsMatch(normalized))
        {
            throw new ArgumentException(
                $"Environment '{environment}' is not a valid FoundryGate environment name: expected 1-10 lowercase letters/digits (e.g. dev, prod).",
                nameof(environment));
        }

        return normalized;
    }

    /// <summary><c>rg-foundrygate-{env}</c> — every resource of an environment lives in one group.</summary>
    public static string ResourceGroupName(string environment) => $"rg-foundrygate-{NormalizeEnvironment(environment)}";

    /// <summary>
    /// <c>sql-foundrygate-{env}-</c> — the server name ends in the deployment's <c>nameSuffix</c> (a global
    /// DNS label), so callers list the resource group's SQL servers and match on this prefix.
    /// </summary>
    public static string SqlServerNamePrefix(string environment) => $"sql-foundrygate-{NormalizeEnvironment(environment)}-";

    /// <summary><c>sqldb-foundrygate-{env}</c> — the single FoundryGate database.</summary>
    public static string SqlDatabaseName(string environment) => $"sqldb-foundrygate-{NormalizeEnvironment(environment)}";

    /// <summary><c>id-foundrygate-api-{env}</c> — the API's user-assigned managed identity.</summary>
    public static string ApiIdentityName(string environment) => $"id-foundrygate-api-{NormalizeEnvironment(environment)}";

    /// <summary><c>id-foundrygate-func-{env}</c> — the Functions host's user-assigned managed identity.</summary>
    public static string FunctionsIdentityName(string environment) => $"id-foundrygate-func-{NormalizeEnvironment(environment)}";

    /// <summary>
    /// The Entra-auth connection string shape <c>infra/modules/sql.bicep</c> outputs and
    /// <c>_deploy-database.yml</c> computes — no secret in it; <c>Active Directory Default</c> makes
    /// SqlClient pick up the same az CLI / managed identity session the rest of the CLI uses.
    /// </summary>
    public static string EntraConnectionString(string serverFqdn, string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverFqdn);
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);

        return $"Server=tcp:{serverFqdn},1433;Database={databaseName};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;";
    }

    [GeneratedRegex("^[a-z0-9]{1,10}$")]
    private static partial Regex EnvironmentNamePattern();
}
