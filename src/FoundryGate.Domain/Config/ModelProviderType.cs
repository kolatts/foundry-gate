namespace FoundryGate.Domain.Config;

/// <summary>
/// Which upstream API shape a gateway model alias speaks (plans/25-model-aliases-routing.md).
/// Determines which base path and auth header style a CLI client uses — see
/// <see cref="Contracts.GatewayConnectionInfo"/> and docs-site's cli-setup.mdx.
/// </summary>
public enum ModelProviderType
{
    /// <summary>Anthropic Messages API (<c>/anthropic/v1/messages</c>), <c>x-api-key</c> auth.</summary>
    Anthropic = 0,

    /// <summary>OpenAI Responses/Chat Completions API (<c>/openai/v1</c>), <c>api-key</c> auth.</summary>
    OpenAi = 1,
}
