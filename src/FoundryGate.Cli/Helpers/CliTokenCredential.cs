using Azure.Core;
using Imagile.Framework.Configuration.Azure;

namespace FoundryGate.Cli.Helpers;

/// <summary>
/// Builds the <see cref="TokenCredential"/> the CLI's Azure calls (ARM firewall rules, and indirectly
/// the <c>Authentication=Active Directory Default</c> SQL connections) run under — the same
/// <see cref="AppTokenCredential"/> chain FoundryGate.Api uses (CONVENTIONS.md: Workload/ManagedIdentity
/// in cloud when <c>AZURE_CLIENT_ID</c> is set, Azure CLI / Visual Studio otherwise; never
/// <c>DefaultAzureCredential</c>).
/// </summary>
public static class CliTokenCredential
{
    /// <summary>Environment variable carrying a user-assigned managed identity's client id (set by Azure hosts).</summary>
    public const string ClientIdVariable = "AZURE_CLIENT_ID";

    /// <summary>Set to <c>true</c> by every GitHub Actions runner.</summary>
    public const string GitHubActionsVariable = "GITHUB_ACTIONS";

    /// <summary>
    /// Picks the managed-identity client id to hand to <see cref="AppTokenCredential"/>: the value of
    /// <c>AZURE_CLIENT_ID</c> when the process runs on an Azure host, <see langword="null"/> (→ the Azure CLI
    /// chain) on a GitHub Actions runner. A runner has no managed identity — <c>azure/login@v2</c> logs the az
    /// CLI in with the OIDC token, and that az session is the only credential available there — but callers
    /// commonly export <c>AZURE_CLIENT_ID</c> for the login step, which would otherwise select a
    /// Workload/ManagedIdentity chain that can only fail.
    /// </summary>
    public static string? SelectManagedIdentityClientId(string? azureClientId, string? gitHubActions)
    {
        if (string.Equals(gitHubActions, "true", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(azureClientId) ? null : azureClientId;
    }

    /// <summary>Creates the credential from the current process environment.</summary>
    public static TokenCredential Create()
    {
        var clientId = SelectManagedIdentityClientId(
            Environment.GetEnvironmentVariable(ClientIdVariable),
            Environment.GetEnvironmentVariable(GitHubActionsVariable));

        return new AppTokenCredential(clientId);
    }
}
