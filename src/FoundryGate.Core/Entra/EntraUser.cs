namespace FoundryGate.Core.Entra;

/// <summary>
/// The directory's view of a person, already reduced to the fields FoundryGate stores on
/// <c>User</c> (spec &#167;7: displayName, mail, employeeId). Internal to the Api's Entra services —
/// not an API contract, so it lives here rather than in Domain.
/// </summary>
/// <param name="ObjectId">The Entra object id (<c>oid</c>); matches <c>User.EntraObjectId</c>.</param>
/// <param name="DisplayName">Graph <c>displayName</c>, falling back to the UPN when the directory has none.</param>
/// <param name="Email">Graph <c>mail</c>, falling back to <c>userPrincipalName</c> — many tenants leave <c>mail</c> unset for accounts without a mailbox.</param>
/// <param name="EmployeeId">Graph <c>employeeId</c>; <see langword="null"/> when the directory has none (never empty string, so "not on record" stays distinguishable downstream).</param>
public sealed record EntraUser(string ObjectId, string DisplayName, string Email, string? EmployeeId);
