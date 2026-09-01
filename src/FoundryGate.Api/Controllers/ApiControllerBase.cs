using Microsoft.AspNetCore.Mvc;

namespace FoundryGate.Api.Controllers;

/// <summary>
/// Base class for every FoundryGate.Api controller. Fixes the three attributes spec &#167;4 implies
/// for the whole surface — <c>[ApiController]</c> (automatic 400 ProblemDetails on model-binding
/// failure), the <c>api/v1/[controller]</c> route prefix, and JSON-only responses — so individual
/// controllers declare only what is specific to them: their <c>[Authorize(Policy = ...)]</c> and
/// their actions. Authentication itself is already global (the <c>AuthorizeFilter</c> in
/// <c>Program.cs</c>); a controller inheriting this is authenticated-only by default and opts into
/// admin-only with <c>[Authorize(Policy = PolicyNames.AdminOnly)]</c> at class or action level.
/// </summary>
/// <remarks>
/// Controllers stay thin: expression-bodied (or near-enough) delegations into a
/// <c>Services/&lt;Area&gt;</c> service, which owns the query/mutation and throws the exception
/// types <c>GlobalExceptionHandler</c> maps (404/400/409/403). See CONVENTIONS.md
/// "API service/controller conventions".
/// </remarks>
[ApiController]
[Route("api/v1/[controller]")]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
}
