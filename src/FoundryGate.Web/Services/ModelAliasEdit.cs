using FoundryGate.Domain.Config;
using FoundryGate.Domain.Constants;
using FoundryGate.Domain.Foundry;
using FoundryGate.Domain.Gateway.Contracts;

namespace FoundryGate.Web.Services;

/// <summary>
/// What <c>ModelAliasDialog</c> hands back to <c>/models</c>: which tier's allowlist to change, and
/// the one row to put in it. The page turns it into a full-replace <see cref="ReplaceTierModelsRequest"/>,
/// because that is the only shape the gateway's named value has (#225).
/// </summary>
/// <param name="Tier">The quota-tier product id whose allowlist gains (or re-points) this alias.</param>
/// <param name="Alias">The row itself, with <c>pool</c> and <c>provider</c> already derived from the deployment.</param>
public sealed record ModelAliasEdit(string Tier, TierModelAliasRequest Alias);

/// <summary>
/// How a Foundry deployment's model format decides the two fields an admin should never have to
/// answer: which front door the alias belongs to, and which APIM backend it routes at.
/// </summary>
/// <remarks>
/// Both are derived rather than offered because getting either wrong produces a failure that looks
/// like something else — a Claude alias routed at the OpenAI backend dies as an opaque 404, and one
/// declared with the wrong provider is refused by the policy naming a base path the caller did not
/// use. The deployment's ARM <c>model.format</c> already answers both, so the form asks the question
/// once, in the deployment picker.
/// </remarks>
public static class ModelAliasDerivation
{
    /// <summary>The front door a deployment of this ARM <c>model.format</c> is served through.</summary>
    public static ModelProviderType ProviderFor(string? modelFormat) =>
        IsAnthropic(modelFormat) ? ModelProviderType.Anthropic : ModelProviderType.OpenAi;

    /// <summary>
    /// The logical backend pool it routes at. Anthropic models are deployed in every region and
    /// served through the multi-region pool; OpenAI models live in the primary account alone
    /// (<c>infra/main.bicep</c>'s pooled / primary-only split).
    /// </summary>
    public static string PoolFor(string? modelFormat) =>
        IsAnthropic(modelFormat) ? GatewayModelMap.AnthropicPool : GatewayModelMap.OpenAiPool;

    /// <summary>
    /// The accounts a model of this format should be deployed into, out of the gateway's configured
    /// accounts: every account for a pooled (Anthropic) model, the primary account alone otherwise.
    /// What the provision dialog pre-selects.
    /// </summary>
    public static IReadOnlyList<string> DefaultAccountsFor(string? modelFormat, IReadOnlyList<string> configuredAccounts)
    {
        ArgumentNullException.ThrowIfNull(configuredAccounts);

        return IsAnthropic(modelFormat) || configuredAccounts.Count == 0 ? configuredAccounts : [configuredAccounts[0]];
    }

    private static bool IsAnthropic(string? modelFormat) =>
        string.Equals(modelFormat, nameof(FoundryModelFormatType.Anthropic), StringComparison.OrdinalIgnoreCase);
}
