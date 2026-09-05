namespace FoundryGate.Tests.Postdeployment;

/// <summary>
/// A fact that needs a deployed API (<c>FG_API_BASE_URL</c>). Absent it, the test reports as
/// <em>skipped</em> with the reason — never as passed, so an unconfigured run cannot be mistaken
/// for a verified one.
/// </summary>
public sealed class DeployedApiFactAttribute : FactAttribute
{
    public DeployedApiFactAttribute() => Skip = DeployedEnvironment.ApiSkipReason;
}

/// <summary>A fact that needs a deployed API and a bearer token for a <c>FoundryGate.Admin</c> principal.</summary>
public sealed class AdminTokenFactAttribute : FactAttribute
{
    public AdminTokenFactAttribute() => Skip = DeployedEnvironment.AdminSkipReason;
}

/// <summary>A fact that needs a deployed API and a bearer token for a principal without the admin role.</summary>
public sealed class NonAdminTokenFactAttribute : FactAttribute
{
    public NonAdminTokenFactAttribute() => Skip = DeployedEnvironment.NonAdminSkipReason;
}

/// <summary>A theory that needs a deployed API (<c>FG_API_BASE_URL</c>).</summary>
public sealed class DeployedApiTheoryAttribute : TheoryAttribute
{
    public DeployedApiTheoryAttribute() => Skip = DeployedEnvironment.ApiSkipReason;
}

/// <summary>A theory that needs a deployed API and an admin bearer token.</summary>
public sealed class AdminTokenTheoryAttribute : TheoryAttribute
{
    public AdminTokenTheoryAttribute() => Skip = DeployedEnvironment.AdminSkipReason;
}

/// <summary>A theory that needs a deployed API and a non-admin bearer token.</summary>
public sealed class NonAdminTokenTheoryAttribute : TheoryAttribute
{
    public NonAdminTokenTheoryAttribute() => Skip = DeployedEnvironment.NonAdminSkipReason;
}
