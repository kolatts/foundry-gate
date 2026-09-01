namespace FoundryGate.Domain.Foundry;

/// <summary>
/// The <c>model.format</c> of an Azure AI Foundry model deployment — the publisher family ARM
/// uses to decide how a deployment is provisioned (infra/modules/foundry.bicep declares the same
/// two values). Distinct from <see cref="Config.ModelProviderType"/>, which is the <em>gateway</em>
/// front door an alias is served through: the two usually line up (an Anthropic-format deployment
/// sits behind the Anthropic front door) but they answer different questions, and future formats
/// (e.g. a Microsoft- or Meta-published model served through the OpenAI schema) would map many
/// formats onto one provider.
/// </summary>
/// <remarks>
/// Only the formats the API is willing to <em>create</em> are enumerated here; the read side
/// (<see cref="Contracts.FoundryDeploymentResponse.ModelFormat"/>) carries the raw ARM string so a
/// deployment of any other format created outside FoundryGate still lists correctly.
/// </remarks>
public enum FoundryModelFormatType
{
    /// <summary>ARM <c>model.format = "OpenAI"</c> — Azure OpenAI models (gpt-*, o-series, codex).</summary>
    OpenAI = 0,

    /// <summary>
    /// ARM <c>model.format = "Anthropic"</c> — Claude models. Provisioning requires the Marketplace
    /// attestation block (<c>modelProviderData</c>) and is create-once under ARM (CLAUDE.md;
    /// fable-refactor-log.md E-007). Creation through the API is not yet supported — see
    /// <c>IFoundryDeploymentService.CreateDeploymentAsync</c>.
    /// </summary>
    Anthropic = 1,
}
