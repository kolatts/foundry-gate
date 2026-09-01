# Foundry Gate

Foundry Gate is an open-source AI budget gateway for Azure AI Foundry and Azure API
Management: every developer gets their own key and monthly token budget, enforced in
real time at the gateway (`403` the second a budget is spent, `429` + `Retry-After`
for per-minute caps), with Entra ID user sync, quota increase approvals, and APIM key
lifecycle management. Designed to be forked and configured against any Azure tenant.

**Docs: https://kolatts.github.io/foundry-gate/** — plain-English overview, architecture,
CLI setup (Claude Code + Codex), rate limits, and cost & capacity planning.

See [`foundrygate-spec.md`](foundrygate-spec.md) and [`CONVENTIONS.md`](CONVENTIONS.md) for
the full design and engineering conventions; `infra/` holds the deployable gateway
data plane (Bicep).

## Tech stack

| Layer | Technology |
|---|---|
| Frontend | Blazor WebAssembly (.NET 10), Azure Static Web Apps |
| API | ASP.NET Core 10 Web API, Azure Container Apps |
| Database | Azure SQL, Entity Framework Core 10 |
| Auth | Microsoft Entra ID |
| Background jobs | Azure Functions (.NET 10 isolated worker) |
| Gateway | Azure API Management |
| Infra-as-Code | Bicep + GitHub Actions (OIDC) |

## Solution layout

| Project | Purpose |
|---|---|
| `FoundryGate.Domain` | DTOs, enums, exceptions — no ASP.NET/EF dependencies |
| `FoundryGate.Data` | EF Core entities, `DbContext`, seeding |
| `FoundryGate.Api` | ASP.NET Core Web API |
| `FoundryGate.Functions` | Azure Functions (isolated worker) background jobs |
| `FoundryGate.Web` | Blazor WebAssembly frontend |
| `FoundryGate.Database` | SQL Server database project (`.sqlproj` → dacpac) |
| `FoundryGate.Cli` | Command-line tool for schema deploy/seed/local setup |
| `FoundryGate.Tests.Predeployment` | Unit/integration tests gating every PR |
| `FoundryGate.Tests.Postdeployment` | Playwright tests against a deployed environment |

## Getting started

Prerequisites: .NET 10 SDK (pinned in [`global.json`](global.json)), Docker.

```bash
# Start local SQL Server + Azurite
docker compose up -d

# Build the solution
dotnet build FoundryGate.sln

# Run the predeployment test suite (Postdeployment needs a deployed environment)
dotnet test src/FoundryGate.Tests.Predeployment

# Verify formatting
dotnet format FoundryGate.sln --verify-no-changes
```

Further setup (schema deploy, seeding, running the API/Web/Functions locally) will be
documented as those pieces land.
