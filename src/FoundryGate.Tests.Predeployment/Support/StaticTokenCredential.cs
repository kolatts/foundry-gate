using Azure.Core;

namespace FoundryGate.Tests.Predeployment.Support;

/// <summary>A <see cref="TokenCredential"/> that hands out a fixed token and never talks to Entra — for constructing Azure clients that must not be exercised.</summary>
public sealed class StaticTokenCredential : TokenCredential
{
    private static readonly AccessToken Token = new("test-token", DateTimeOffset.MaxValue);

    /// <inheritdoc />
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => Token;

    /// <inheritdoc />
    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => new(Token);
}
