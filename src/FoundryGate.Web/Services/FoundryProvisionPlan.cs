using FoundryGate.Domain.Foundry.Contracts;

namespace FoundryGate.Web.Services;

/// <summary>
/// What <c>FoundryDeploymentDialog</c> hands back to <c>/foundry</c>: the same deployment, to be
/// created in one or several of the gateway's Foundry accounts (#225).
/// </summary>
/// <remarks>
/// The API deliberately creates <b>one</b> deployment in <b>one</b> account per call — a pooled model
/// is several requests, so that each create is an explicit, auditable decision rather than a loop the
/// API drives on an admin's behalf (fable-refactor-log E-007). This plan keeps that property while
/// letting the admin say "and the other region too" once: the page still issues one POST per account,
/// in order, and reports each result on its own.
/// </remarks>
/// <param name="AccountNames">Accounts to create in, chosen account first. Never empty; entries are distinct.</param>
/// <param name="Template">The deployment to create; its <c>AccountName</c> is the first entry and is replaced per account.</param>
public sealed record FoundryProvisionPlan(IReadOnlyList<string> AccountNames, CreateFoundryDeploymentRequest Template);
