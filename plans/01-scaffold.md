# Repo and .NET 10 project scaffold

> GitHub: #1  
> Milestone: v0.1 — Foundation  
> Labels: epic, backend

## Overview
This epic establishes the repository skeleton and .NET 10 solution that every subsequent epic builds on. It creates four projects in the right folder layout, wires up solution-level tooling (`.gitignore`, EditorConfig, `NuGet.config`), and ensures a clean `dotnet build` from day one. Getting this right up front removes friction for all contributors and keeps the project structure consistent throughout the roadmap.

## Approach

### Initialize .NET 10 solution with seven projects and folder structure (#20)
Create the solution file at the repo root (`FoundryGate.sln`) and scaffold seven projects under `src/`:

| Project | Type | Purpose |
|---|---|---|
| `FoundryGate.Api` | ASP.NET Core Web API | REST API, Container App |
| `FoundryGate.Data` | Class library | EF Core entities, DbContext, migrations |
| `FoundryGate.Domain` | Class library | DTOs, enums — no ASP.NET/EF deps |
| `FoundryGate.Functions` | Azure Functions (.NET 10 isolated) | Timer-triggered background jobs |
| `FoundryGate.Web` | Blazor WASM | MudBlazor frontend |
| `FoundryGate.Database` | SQL Server project (.sqlproj) | Dacpac schema source of truth |
| `FoundryGate.Cli` | Console / dotnet tool | `foundrygate` CLI for schema compare, deploy, seed |

Project references: `Api` → `Data` + `Domain`; `Functions` → `Data` + `Domain`; `Web` → `Domain`; `Cli` → `Data` + `Domain`. `FoundryGate.Database` has no C# references — it is a pure SQL project. Use `global.json` to pin the .NET 10 SDK version. Verify `dotnet build` succeeds with zero warnings across all seven projects.

Files expected to be created or modified:
- `global.json`
- `FoundryGate.sln`
- `src/FoundryGate.Api/FoundryGate.Api.csproj`
- `src/FoundryGate.Data/FoundryGate.Data.csproj`
- `src/FoundryGate.Domain/FoundryGate.Domain.csproj`
- `src/FoundryGate.Functions/FoundryGate.Functions.csproj`
- `src/FoundryGate.Web/FoundryGate.Web.csproj`
- `src/FoundryGate.Database/FoundryGate.Database.sqlproj`
- `src/FoundryGate.Cli/FoundryGate.Cli.csproj`

### Add .gitignore, EditorConfig, and solution-level NuGet config (#21)
Add a `.gitignore` (Visual Studio / .NET template), a `.editorconfig` enforcing 4-space indentation, UTF-8, and C# style rules (file-scoped namespaces, `var` usage, etc.), and a `NuGet.config` pointing to `nuget.org`. Optionally add a `Directory.Build.props` to centralise `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>` across all projects so individual csproj files stay minimal.

Files expected to be created or modified:
- `.gitignore`
- `.editorconfig`
- `NuGet.config`
- `Directory.Build.props`

## Verification
- [ ] `dotnet build` passes with zero errors and zero warnings
- [ ] All seven projects appear in the solution explorer
- [ ] `global.json` pins a .NET 10 SDK version
- [ ] `.editorconfig` rules are respected in the IDE
- [ ] `FoundryGate.Domain` has no reference to `Microsoft.EntityFrameworkCore` or `Microsoft.AspNetCore.*`
- [ ] `FoundryGate.Database` builds and produces a `.dacpac` output file
