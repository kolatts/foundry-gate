namespace FoundryGate.Api.Services.Foundry;

/// <summary>
/// Raised by <see cref="IFoundryManagementClient"/> when the Foundry <em>account</em> a call
/// addresses does not exist (or the host identity cannot see it) — as opposed to a deployment
/// being absent, which the client reports as <see langword="null"/>/<see langword="false"/>.
/// Internal to the Foundry area: <see cref="FoundryDeploymentService"/> turns it into a
/// <see cref="Domain.Exceptions.FeatureNotConfiguredException"/> (503) on admin paths and skips
/// the account on the developer view. Carries only the account name — the resource-group name
/// stays out of anything that could reach the wire.
/// </summary>
public sealed class FoundryAccountNotFoundException : Exception
{
    public FoundryAccountNotFoundException(string accountName)
        : base($"Foundry account '{accountName}' was not found.")
    {
        AccountName = accountName;
    }

    public FoundryAccountNotFoundException(string accountName, Exception innerException)
        : base($"Foundry account '{accountName}' was not found.", innerException)
    {
        AccountName = accountName;
    }

    /// <summary>The account name as configured/requested.</summary>
    public string AccountName { get; }
}
