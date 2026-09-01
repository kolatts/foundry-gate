using FoundryGate.Domain.Foundry.Contracts;

namespace FoundryGate.Api.Services.Foundry;

/// <summary>
/// The thin seam between FoundryGate and Azure Resource Manager for Foundry model deployments:
/// four primitive operations against one account, no policy. Everything that <em>decides</em> —
/// which accounts exist, whether a name may be created, whether a format is allowed, what to
/// audit — lives in <see cref="IFoundryDeploymentService"/>; this interface only translates
/// between <see cref="FoundryDeploymentResponse"/> and the SDK. It exists so the service and the
/// controllers are testable without Azure (tests substitute an in-memory fake) and so the ARM SDK
/// types never leak past <c>Services/Foundry</c>.
/// </summary>
/// <remarks>
/// Implementations map ARM's <c>404</c> to "absent" (<see langword="null"/> / <see langword="false"/>)
/// and its <c>409</c> to <see cref="Domain.Exceptions.ConflictException"/>; any other ARM failure
/// (403 from an under-privileged identity, 400 from a quota or model-catalog rejection, 5xx) is
/// left to surface as <c>Azure.RequestFailedException</c> — a 500 with the detail in the server
/// log, never on the wire — because it describes the <em>gateway's</em> identity or quota, not
/// the caller's request.
/// </remarks>
public interface IFoundryManagementClient
{
    /// <summary>Every deployment in <paramref name="accountName"/>, in ARM's enumeration order.</summary>
    /// <exception cref="KeyNotFoundException">The account itself does not exist (ARM 404 on the account).</exception>
    Task<IReadOnlyList<FoundryDeploymentResponse>> ListDeploymentsAsync(string accountName, CancellationToken cancellationToken);

    /// <summary>One deployment, or <see langword="null"/> when no deployment of that name exists in the account.</summary>
    Task<FoundryDeploymentResponse?> GetDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken);

    /// <summary>
    /// Starts creating a deployment and returns its state as ARM reports it immediately after the
    /// PUT is accepted — typically <c>Accepted</c>/<c>Creating</c>; ARM validates asynchronously
    /// (minutes, for some models), so callers poll <see cref="GetDeploymentAsync"/> for
    /// <c>Succeeded</c>. The caller (<see cref="IFoundryDeploymentService"/>) has already
    /// established the name does not exist: this method is a PUT and must never be pointed at an
    /// existing deployment (CLAUDE.md: never re-PUT).
    /// </summary>
    /// <exception cref="Domain.Exceptions.ConflictException">ARM refused with 409 (a concurrent create of the same name, or a name racing this call).</exception>
    Task<FoundryDeploymentResponse> CreateDeploymentAsync(CreateFoundryDeploymentRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Starts deleting a deployment. <see langword="true"/> when ARM accepted the delete,
    /// <see langword="false"/> when no such deployment existed (ARM 404) — nothing is retried or
    /// recreated either way.
    /// </summary>
    Task<bool> DeleteDeploymentAsync(string accountName, string deploymentName, CancellationToken cancellationToken);
}
