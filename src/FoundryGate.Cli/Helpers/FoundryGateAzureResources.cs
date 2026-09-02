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
    /// (<c>test</c>, <c>dev</c>, <c>prod</c>, ...) every resource name embeds — trim, alias, lowercase, and
    /// nothing else. It deliberately does <em>not</em> validate: the workflow passes the GitHub Environment
    /// name through <c>--env</c> even on the paths that never derive a resource name from it (both <c>ip</c>
    /// commands take an explicit <c>--server</c>/<c>--resource-group</c>), so a fork whose Environment is
    /// called <c>pre-prod-eu</c> must not fail on a value nothing was going to read. The shape rules live
    /// where they matter, in the name builders below.
    /// </summary>
    public static string NormalizeEnvironment(string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);

        var trimmed = environment.Trim();
        return EnvironmentAliases.TryGetValue(trimmed, out var alias) ? alias : trimmed.ToLowerInvariant();
    }

    /// <summary>
    /// <see cref="NormalizeEnvironment"/> plus the check that the result can actually be embedded in an
    /// Azure resource name, so a typo fails here with a printable message rather than as a confusing
    /// ARM 404. Applied only by the name builders, and only after alias mapping.
    /// </summary>
    public static string ValidatedEnvironment(string environment)
    {
        var normalized = NormalizeEnvironment(environment);

        if (!EnvironmentNamePattern().IsMatch(normalized))
        {
            throw new ArgumentException(
                $"Environment '{environment}' cannot be used to derive FoundryGate resource names: expected 1-24 lowercase " +
                "letters, digits and inner hyphens (e.g. dev, prod, pre-prod). Pass --server/--resource-group to address " +
                "resources whose names do not follow the convention.",
                nameof(environment));
        }

        return normalized;
    }

    /// <summary><c>rg-foundrygate-{env}</c> — every resource of an environment lives in one group.</summary>
    public static string ResourceGroupName(string environment) => $"rg-foundrygate-{ValidatedEnvironment(environment)}";

    /// <summary>
    /// <c>sql-foundrygate-{env}-</c> — the server name ends in the deployment's <c>nameSuffix</c> (a global
    /// DNS label), so callers list the resource group's SQL servers and match on this prefix.
    /// </summary>
    public static string SqlServerNamePrefix(string environment) => $"sql-foundrygate-{ValidatedEnvironment(environment)}-";

    /// <summary><c>sqldb-foundrygate-{env}</c> — the single FoundryGate database.</summary>
    public static string SqlDatabaseName(string environment) => $"sqldb-foundrygate-{ValidatedEnvironment(environment)}";

    /// <summary><c>id-foundrygate-api-{env}</c> — the API's user-assigned managed identity.</summary>
    public static string ApiIdentityName(string environment) => $"id-foundrygate-api-{ValidatedEnvironment(environment)}";

    /// <summary><c>id-foundrygate-func-{env}</c> — the Functions host's user-assigned managed identity.</summary>
    public static string FunctionsIdentityName(string environment) => $"id-foundrygate-func-{ValidatedEnvironment(environment)}";

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

    // Lowercase letters/digits with inner hyphens, 1-24 characters: wide enough for a fork's `pre-prod`
    // GitHub Environment, narrow enough that the derived rg-/sql-/id- names stay legal Azure names.
    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,22}[a-z0-9])?$")]
    private static partial Regex EnvironmentNamePattern();
}
