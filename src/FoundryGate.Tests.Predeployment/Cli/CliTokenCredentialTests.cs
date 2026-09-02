using FoundryGate.Cli.Helpers;

namespace FoundryGate.Tests.Predeployment.Cli;

public class CliTokenCredentialTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", null, null)]
    [InlineData("   ", "false", null)]
    [InlineData("11111111-2222-3333-4444-555555555555", null, "11111111-2222-3333-4444-555555555555")]
    [InlineData("11111111-2222-3333-4444-555555555555", "false", "11111111-2222-3333-4444-555555555555")]
    public void Uses_AZURE_CLIENT_ID_as_the_managed_identity_outside_GitHub_Actions(string? clientId, string? gitHubActions, string? expected)
    {
        Assert.Equal(expected, CliTokenCredential.SelectManagedIdentityClientId(clientId, gitHubActions));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    public void Ignores_AZURE_CLIENT_ID_on_a_GitHub_Actions_runner_so_the_az_CLI_session_is_used(string gitHubActions)
    {
        // azure/login@v2 leaves the runner with an az CLI session, not a managed identity; a stray
        // AZURE_CLIENT_ID would otherwise pick a Workload/ManagedIdentity chain that can only fail there.
        Assert.Null(CliTokenCredential.SelectManagedIdentityClientId("11111111-2222-3333-4444-555555555555", gitHubActions));
    }
}
