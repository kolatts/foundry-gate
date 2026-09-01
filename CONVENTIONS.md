# FoundryGate engineering conventions

> Derived from imagile-app (C:\Code\imagile-app, surveyed 2026-09-01) per the owner's
> direction: "similar EF Core backing store, similar to imagile-app — but not
> multitenant sharding." Sections marked **[improvement]** deliberately upgrade on
> imagile-app where its own history shows the gap. Every implementation agent reads
> this file before writing code. Where this file conflicts with foundrygate-spec.md
> or an issue body, THIS FILE WINS (it is newer).

## Solution structure

- Projects: `FoundryGate.Domain` (zero deps: enums, DTO `record`s, exceptions,
  validation), `FoundryGate.Data` (DbContext + entities + seeding),
  `FoundryGate.Api` (controllers + **services live here** under `Services/<Area>/` —
  NO separate Services project), `FoundryGate.Functions` (isolated worker),
  `FoundryGate.Web` (Blazor WASM — references **Domain only**, hard boundary),
  `FoundryGate.Database` (.sqlproj → dacpac), `FoundryGate.Cli` (System.CommandLine
  tool: db deploy/seed, local setup), `FoundryGate.Tests.Predeployment`,
  `FoundryGate.Tests.Postdeployment`.
- Every csproj: `net10.0`, `ImplicitUsings`, `Nullable enable`,
  **`TreatWarningsAsErrors true`**. No repository pattern, no unit-of-work, ever.
- Reference `Imagile.Framework.{Core,Configuration,EntityFrameworkCore,
  EntityFrameworkCore.Testing}` (public on nuget.org, ≥1.0.12) instead of
  reimplementing Key Vault reference resolution, `ValidateRecursively`,
  `[DoNotUpdate]`, or convention-test bases.

## EF Core / Azure SQL

- Single database, single DbContext (`AppDbContext`), registered plainly in
  Program.cs: `AddDbContext<AppDbContext>(o => o.UseSqlServer(cs))`. No sharding:
  no metabase/shard split, no `ITenantIdEntity`, no `InTenant()`, no
  tenant-connection resolvers/factories, no `CurrentTenantContext`, no shard math.
  A fork IS the tenant.
- Cloud SQL auth: `Authentication=Active Directory Default` (no SQL passwords);
  local: `Server=127.0.0.1,3433;User Id=sa;Password=<local only>` via docker-compose (use the
  literal `127.0.0.1`, not `localhost` — SqlClient's dual-stack resolution can time out probing the
  container's IPv6 loopback, which docker's port mapping doesn't listen on, before falling back to
  IPv4, even though the port is reachable).
- Entity config: data annotations first; `internal sealed` `IEntityTypeConfiguration`
  **co-located in the entity's file** only for what annotations can't express
  (delete behavior, composite keys); applied via `ApplyConfigurationsFromAssembly`.
  At most one cascade path per entity; everything else `DeleteBehavior.NoAction`
  with a comment.
- Keys: **`int` identity PK named `{Entity}Id`**; where an externally-shared stable
  id is needed, add a separate `Guid {Entity}Unique` column. (Amends
  foundrygate-spec §3.1's Guid PKs — spec predates the imagile-alignment decision.)
- Temporal: `DateTimeOffset`, names end in `Date` (`CreatedDate`, `ReviewedDate`).
  **[improvement]** All `CreatedDate`/`ModifiedDate` set by ONE
  `SaveChangesInterceptor` using injected `TimeProvider` — never inline
  `DateTimeOffset.UtcNow` in services.
- **[improvement]** `TimeProvider` injected everywhere time is read. No naked
  `UtcNow` outside the interceptor's TimeProvider.
- Strings: non-nullable, `[Required]` + `[StringLength(n)]`, default
  `= string.Empty`. Nullable only when null means something (justify in the
  convention-test exclusion). **Nullable `bool` is banned** — use an enum.
- Enums stored as `int`, property suffixed `Type`. Navigation collections
  `ICollection<T> X { get; set; } = [];`; required navs `= null!;`.
- Indexes/composite PKs via attributes (`[Index(...)]`, `[PrimaryKey(...)]`).
  DbSet name (plural) = table name; property name = column name (test-enforced).
- Queries: services take the DbContext directly; compose `IQueryable` inline;
  shared fragments = `private static IQueryable<T>` helpers or a static
  `XxxQueries` class. **Projection-to-record inside the query** is the default read
  pattern. **[improvement]** `AsNoTracking()` on every read path that doesn't
  project, and a shared `PagedResult<T>` + `.ToPagedAsync(page, size, ct)` helper in
  Domain/Data (spec's paged endpoints need it).
- `CancellationToken` threaded through every async method, last parameter.
- Exceptions → HTTP via one `IExceptionHandler` + ProblemDetails
  (404/400/403/409 mapping), not per-controller try/catch. Controllers are thin
  expression-bodied delegations with class-level `[Authorize]`.

## API service/controller conventions

Landed with #42 (the first `/api/v1` controller); every endpoint wave builds on these.

- **Controllers** inherit `Controllers/ApiControllerBase` (`[ApiController]`,
  `[Route("api/v1/[controller]")]`, `[Produces("application/json")]`) and declare only
  their `[Authorize(Policy = PolicyNames.AdminOnly)]` (class- or action-level) plus thin
  actions that delegate to a service. Authentication is already global (the
  `AuthorizeFilter` in Program.cs): anonymous → 401, non-admin on an admin-only route →
  403, with no per-controller code.
- **Services** live in `Services/<Area>/` with the single-implementation interface
  co-located (`IAuditService.cs` + `AuditService.cs`); primary constructors; take
  `AppDbContext` directly; throw `KeyNotFoundException` (404) / `ArgumentException` (400)
  / `ConflictException` (409) / `UnauthorizedAccessException` (403) and never touch HTTP
  status codes themselves.
- **DI — one line per area, one file.** Each area owns
  `Services/<Area>/<Area>ServiceCollectionExtensions.cs` exposing `Add<Area>Services()`;
  register it by adding ONE line to `Services/ServiceCollectionExtensions
  .AddFoundryGateApiServices()`, which Program.cs calls once. Never register application
  services in Program.cs — parallel waves would collide there.
- **Current user**: inject `ICurrentUserAccessor` (scoped) rather than reading
  `HttpContext.User`. `EntraObjectId` (accepts both `oid` and the long
  `.../objectidentifier` claim type), `IsAdmin` (same `IsInRole` check as the policy),
  `DisplayName`/`Email` (token claims, for auto-provisioning), `TryGetUserAsync(ct)`
  (null = not provisioned yet — a first-class path; `GET /users/me` provisions on it),
  `GetRequiredUserAsync(ct)` (→ 404). The returned `User` is tracked by the request's
  context, so mutate it and `SaveChangesAsync`. No oid on an authenticated principal →
  `UnauthorizedAccessException` (403).
- **Audit**: inject `IAuditService` and call
  `await audit.LogAsync(AuditActions.X, AuditTargetTypes.Y, id.ToString(), new { before, after }, ct)`
  *before* the caller's own `SaveChangesAsync` — the row is added to the SAME
  `AppDbContext` and commits atomically with the mutation. No fire-and-forget audit: a
  mutation without its audit row (or vice versa) is worse than a failed request. System
  actors (Functions, sync jobs) use the `LogAsync(int? actorUserId, ...)` overload with
  `null`. New action/target kinds are constants in Domain's `AuditActions` /
  `AuditTargetTypes`, never inline strings. `details` is JSON-serialized (camelCase) —
  never put a key or token in it.
- **Paging**: bind `[FromQuery] PagedRequest paging` (alongside any `[FromQuery]` filter
  record) and finish the query with `.OrderBy(...).Select(projection).ToPagedAsync(paging, ct)`
  (`FoundryGate.Data.Extensions`) → `PagedResult<T>`. Order deterministically first;
  the helper clamps page/size itself.
- **Integration tests** use `IClassFixture<ApiTestFactory>` (real pipeline, SQLite
  in-memory, header-driven `TestAuthHandler`): `factory.CreateClient()` is anonymous,
  `factory.CreateClientAs(oid, isAdmin: true)` is an admin, `factory.SeedUserAsync(...)`
  and `factory.CreateDbContext()` arrange/assert rows, `factory.TimeProvider` moves the
  clock. One database per test class — seed with unique markers, never assert absolute
  counts. Service-level tests use `InMemoryDatabaseTest` + `Support/MutableTimeProvider`
  + hand-rolled stubs (no mocking library). Under SQLite, `AppDbContext` stores
  `DateTimeOffset` as UTC ticks (provider-gated) so ordering and date-range filters
  translate; the SQL Server model is untouched.

## Schema pipeline (no EF migrations)

- EF entities are the model source of truth. Local: CLI `local setup` runs
  `EnsureCreated` against docker SQL; DacFx schema-compare regenerates the checked-in
  `FoundryGate.Database/dbo/Tables/*.sql`; `.sqlproj` builds the **dacpac**; CI
  deploys via CLI `db deploy` (DacServices, `--drop-objects` in CI). Data-loss blocking is **on by
  default** (DacFx's own `BlockOnPossibleDataLoss=true`) and requires an explicit
  `--allow-data-loss`/`allow-data-loss: true` override to bypass — a deliberate deviation from
  imagile-app (which inverts this to opt-in blocking), because a fork's production database
  deserves a safe default more than CI convenience does.
- Seeding is code, idempotent, run post-deploy: reference data via the
  `IReferenceDataEntity`/`SyncReferenceDataAsync` pattern
  (Imagile.Framework `[DoNotUpdate]` respected); demo/test data via **Bogus**;
  order: db deploy → seed-reference → seed-test.

## Storage accounts

- Use for: Functions runtime, and high-volume non-relational data ONLY (SQL is the
  system of record). SDK: `Microsoft.Extensions.Azure` `AddAzureClients` with
  blob/queue/table clients; queues Base64 encoding; managed identity in cloud,
  Azurite (`UseDevelopmentStorage=true`) locally.
- Typed access via static-abstract interfaces (`static abstract string
  DefaultQueueName` etc.) + `GetQueueClient<T>()` extensions — no magic strings.

## Configuration & auth

- Options pattern, fail-fast: one `Configuration/AppSettings.cs` per host, nested
  option classes with DataAnnotations, `ValidateRecursively()` at startup,
  `ConfigurationException` on failure. Optional features carry `Enabled` so absent
  secrets don't kill startup where the feature is off.
- Secrets: `@KeyVault(SecretName)` reference tokens in appsettings, resolved at
  startup via `ReplaceKeyVaultReferences` + `AppTokenCredential`-style
  `TokenCredential` (Workload/ManagedIdentity chain in cloud, AzureCli/VS locally —
  copy the class, not `DefaultAzureCredential`).
- Environments: lowercase `local` / `qa` / `prod`, parsed to an enum, DI singleton.
- Logging: `Microsoft.Extensions.Logging` + **OpenTelemetry → Azure Monitor**
  (ASP.NET Core + HttpClient + EF Core instrumentation, `RecordException`), gated
  by an `Enabled` option (off locally). No Serilog.

## Testing (the verification backbone)

- `FoundryGate.Tests.Predeployment` gates every PR: xUnit; DbContext tests on
  **SQLite in-memory** (`DataSource=file:{guid}?mode=memory&cache=shared`,
  `EnsureCreated`, seed helpers exercising the real seeders).
- **Convention tests are mandatory from day one**: inherit
  `DbContextConventionTests` from `Imagile.Framework.EntityFrameworkCore.Testing`;
  plus naming tests (property==column, `Id` casing, `{Entity}Unique` for Guids,
  plural DbSets, FK name == `{Nav}Id`) and design tests (non-nullable strings
  default `string.Empty`). Violations aggregate into one assertion with a bullet
  list. Exclusions declared in code with a justifying comment.
- `FoundryGate.Tests.Postdeployment`: Playwright (+ optional Reqnroll) against a
  deployed environment; desktop + mobile viewports; mutating scenarios `@LocalOnly`;
  failure screenshots/traces to TestResults. References Domain only.
- `dotnet format --verify-no-changes` gates CI.

## CI/CD (fully automated — deliberate upgrade over imagile-app)

- GitHub Actions, **OIDC only** (azure/login@v2 with client/tenant/subscription ids;
  no credential JSON). Reusable `_deploy-*.yml` child workflows taking
  `environment:` via `workflow_call`; GitHub Environments (`dev`, `production`,
  destroy variants) provide gates. GitVersion Mainline. `concurrency` locks on
  deploys; cancel-in-progress on PR CI.
- PR `ci.yml`: build each project → Predeployment tests → format check → artifacts
  of failures → Claude review job (anthropics/claude-code-action, sonnet, scoped
  allowedTools) consuming those artifacts.
- **Unlike imagile-app, infra deploys automatically**: merge to main runs Bicep
  what-if (posted to PR pre-merge per #69) then deploys — no manual dispatch as the
  normal path. Full chain on merge: infra → dacpac db deploy (runner IP whitelist →
  firewall wait → deploy → seed) → api/functions/ui → postdeployment tests → summary.
- SQL deploy niceties to copy verbatim: CLI-driven runner IP whitelist, 60s firewall
  propagation wait, TCP 1433 probe, Entra-admin-group membership verification with
  retries.

## Style

- `ArgumentNullException.ThrowIfNull(x)` first line of public methods taking
  reference params. Primary constructors on services. `var` only when the type is
  apparent. `_ =` discards to satisfy analyzers. Single-implementation interfaces
  co-located with the implementation. 16KB-grade `.editorconfig` enforced by
  `dotnet format`.
- Process: PR-only into main; follow-ups become labeled GitHub issues, never inline
  TODOs; `+semver:` markers in commit messages.

## Architecture diagrams (public docs site)

Every wave that changes the architecture updates the docs-site diagram components
(`docs-site/src/components/*.astro`) and the architecture pages — the public site
must always show the current system: gateway data plane, control plane
(Api/SQL/Functions/storage), and the deploy pipeline flow.
