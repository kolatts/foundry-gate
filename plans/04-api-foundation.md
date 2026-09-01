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
- [x] `dotnet build` passes — zero warnings, whole solution (`TreatWarningsAsErrors` +
      `EnforceCodeStyleInBuild` clean).
- [x] `FoundryGate.Tests.Predeployment` passes — 79/79 (was ~60 before #26/#27; new tests:
      `AppSettingsValidationTests`, `GlobalExceptionHandlerTests`, `HealthEndpointTests`).
- [x] `dotnet format --verify-no-changes` clean.
- [x] `GET /health` returns `200 Healthy` **without docker running** (proves hermetic
      startup — see PR body for the `curl` transcript). `GET /health/ready` correctly
      reports `503`/`Unhealthy` when the docker SQL connection isn't reachable, rather than
      crashing the process.
- [ ] Unauthenticated request to a protected endpoint returns `401` — **deferred**: #26/#27
      is foundation-only (no feature controllers exist yet — see plan Scope). The
      `AuthorizeFilter` global MVC filter and `Microsoft.Identity.Web` JWT bearer wiring are
      in place and build/start correctly; the first PR that adds a controller under
      `/api/v1` should add the end-to-end 401 assertion against a real endpoint.
- [ ] Request with wrong role returns `403` — same deferral as above; `PolicyNames.AdminOnly`
      (role `RoleNames.Admin`, from #91's Domain contracts) is registered in
      `AddAuthorization` and ready for the first admin-only controller action to opt into.
- [x] Unhandled exception returns `500` with `ProblemDetails` JSON body (+ `X-Correlation-Id`
      header) — `GlobalExceptionHandlerTests` covers the exception→status map: `KeyNotFoundException`→404,
      `ArgumentException`→400, `FoundryGate.Domain.Exceptions.ConflictException`→409,
      `UnauthorizedAccessException`→403, anything else→500. A `404` catch-all for unmatched
      routes is wired via `UseStatusCodePages()`.
- [x] OpenAPI UI shows Bearer security scheme — `Microsoft.AspNetCore.OpenApi` (built-in,
      not Swashbuckle, per imagile-app's precedent) + a `BearerSecuritySchemeTransformer` +
      a dev-only Scalar UI at `/scalar/v1` (imagile-app's `AddOpenApi()` ships no UI of its
      own, and the built-in generator has none either — Scalar.AspNetCore fills that gap).

### Deviations from this plan's original text
- **Two role policies → one.** This plan's Approach section (pre-dating #91) named
  `RequireAdmin`/`FoundryGate.Admin` and `RequireDeveloper`/`FoundryGate.Developer`. #91's
  merged Domain contracts define only `RoleNames.Admin` (`FoundryGate.Admin`) and
  `PolicyNames.AdminOnly` — there is no developer role in the spec (§11: "regular users have
  no role"). Implemented the one policy that actually exists; a second policy is easy to add
  later if the spec grows one.
- **`appsettings.Development.json` → `appsettings.local.json`, `ASPNETCORE_ENVIRONMENT` →
  `local`.** CONVENTIONS.md §Configuration & auth: "Environments: lowercase local / qa /
  prod, parsed to an enum, DI singleton" — the file PR #92 merged used ASP.NET Core's own
  `Development` convention, which conflicts. Renamed and repointed `launchSettings.json`;
  added `FoundryGate.Domain.Common.AppEnvironment.Types` and register it as a DI singleton
  in `Program.cs`. Dev-only gating (Scalar/OpenAPI UI) checks `AppEnvironment.Types.local`
  rather than `IHostEnvironment.IsDevelopment()`, since that check is now permanently false
  under this convention.
- **`AppTokenCredential` referenced, not copied.** CONVENTIONS.md: "Reference
  `Imagile.Framework.{Core,Configuration,...}` ... instead of reimplementing Key Vault
  reference resolution, `ValidateRecursively`, ...". `Imagile.Framework.Configuration`
  1.0.12 already ships `Azure.AppTokenCredential` with the exact chain described in
  imagile-app's hand-rolled copy, so `Program.cs` references it directly rather than adding
  a second copy of the same class to this repo.
- **CORS applied globally (`app.UseCors(CorsPolicyNames.Api)`), not scoped to a controller
  filter.** `Microsoft.AspNetCore.Cors.EnableCorsAttribute` isn't compatible with
  `MvcOptions.Filters.Add(IFilterMetadata)` in .NET 10 the way `AuthorizeFilter` is; since
  CORS is a browser-enforced allow-list (not a server-side authorization boundary — `/health`
  carries no sensitive data), scoping it per-request-origin globally is equivalent in
  practice to scoping it to `/api/v1` today, when `/api/v1` is the only browser-facing
  surface. Revisit if a future non-browser-facing route needs a *different* origin policy.
- **`ConflictException` added** in `FoundryGate.Domain.Exceptions` (#91 didn't define one) —
  mapped to `409` in `GlobalExceptionHandler`, per this issue's own text ("define a Domain
  `ConflictException` if not present").
