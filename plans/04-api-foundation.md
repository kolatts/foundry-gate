# API project foundation — Entra auth, middleware, and health

> GitHub: #4  
> Milestone: v0.1 — Foundation  
> Labels: epic, backend

## Overview
This epic bootstraps `FoundryGate.Api` so it is production-ready at the infrastructure level before any business endpoints are added. It wires up Microsoft Entra ID bearer token validation via `Microsoft.Identity.Web`, configures CORS for the Blazor WASM origin, registers request logging and exception-handling middleware, exposes a `/health` endpoint, documents the API with OpenAPI (Scalar or Swashbuckle) including bearer security scheme, and installs a global error handler that produces RFC 7807 `ProblemDetails` responses. Every subsequent endpoint epic simply adds controllers on top of this foundation.

## Approach

### Configure Entra ID bearer auth, CORS, and request middleware in Program.cs (#26)
Call `AddMicrosoftIdentityWebApiAuthentication` with settings from `appsettings.json` (`AzureAd` section: `TenantId`, `ClientId`, `Audience`). Define two authorization policies: `RequireAdmin` (role claim `FoundryGate.Admin`) and `RequireDeveloper` (role claim `FoundryGate.Developer`). Configure CORS with a named policy (`Foundry GateCors`) that allows the Blazor WASM origin; read the origin from configuration so it can differ between dev and prod. Register `Serilog` (or `Microsoft.Extensions.Logging`) for structured request logging with a middleware that logs method, path, status code, and elapsed time. Register `Foundry GateDbContext` with a SQL Server connection string from configuration.

Files expected to be created or modified:
- `src/FoundryGate.Api/Program.cs`
- `src/FoundryGate.Api/appsettings.json`
- `src/FoundryGate.Api/appsettings.Development.json`
- `src/FoundryGate.Api/Middleware/RequestLoggingMiddleware.cs`
- `src/FoundryGate.Api/FoundryGate.Api.csproj`

### Add health endpoint, OpenAPI doc with bearer support, and global error handler (#27)
Register `services.AddHealthChecks()` with an EF Core health check against `Foundry GateDbContext` and map it to `GET /health`. Register OpenAPI generation (Swashbuckle or the built-in .NET 9+ `Microsoft.AspNetCore.OpenApi`) and add a `SecurityDefinition` for `Bearer` so the Swagger UI includes an Authorize button. Add a global exception-handling middleware (or use `app.UseExceptionHandler`) that catches unhandled exceptions and returns a `ProblemDetails` JSON body with a correlation ID header. Add a `404` catch-all that also returns `ProblemDetails`.

Files expected to be created or modified:
- `src/FoundryGate.Api/Program.cs`
- `src/FoundryGate.Api/Middleware/GlobalExceptionMiddleware.cs`
- `src/FoundryGate.Api/Extensions/SwaggerExtensions.cs`

## Verification
- [ ] `dotnet build` passes
- [ ] `GET /health` returns `200 Healthy` with database reachable
- [ ] Unauthenticated request to a protected endpoint returns `401`
- [ ] Request with wrong role returns `403`
- [ ] Unhandled exception returns `500` with `ProblemDetails` JSON body
- [ ] OpenAPI UI shows Bearer security scheme
