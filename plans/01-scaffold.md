# Repo and .NET 10 project scaffold

> GitHub: #1  
> Milestone: v0.1 — Foundation  
> Labels: epic, backend

## Overview
This epic establishes the repository skeleton and .NET 10 solution that every subsequent epic builds on. It creates four projects in the right folder layout, wires up solution-level tooling (`.gitignore`, EditorConfig, `NuGet.config`), and ensures a clean `dotnet build` from day one. Getting this right up front removes friction for all contributors and keeps the project structure consistent throughout the roadmap.

## Approach

### Initialize .NET 10 solution with four projects and folder structure (#20)
Create the solution file at the repo root (`FoundryGate.sln`) and scaffold the four projects under `src/`: `FoundryGate.Api` (ASP.NET Core Web API), `FoundryGate.Data` (class library for EF Core entities and DbContext), `FoundryGate.Domain` (class library for shared DTOs and enums), and `FoundryGate.Web` (Blazor WASM). Add project references so that `Api` depends on `Data` and `Contracts`, and `Web` depends on `Contracts`. Use `global.json` to pin the .NET 10 SDK version. Verify `dotnet build` succeeds with zero warnings.

Files expected to be created or modified:
- `global.json`
- `FoundryGate.sln`
- `src/FoundryGate.Api/FoundryGate.Api.csproj`
- `src/FoundryGate.Data/FoundryGate.Data.csproj`
- `src/FoundryGate.Domain/FoundryGate.Domain.csproj`
- `src/FoundryGate.Web/FoundryGate.Web.csproj`

### Add .gitignore, EditorConfig, and solution-level NuGet config (#21)
Add a `.gitignore` (Visual Studio / .NET template), a `.editorconfig` enforcing 4-space indentation, UTF-8, and C# style rules (file-scoped namespaces, `var` usage, etc.), and a `NuGet.config` pointing to `nuget.org`. Optionally add a `Directory.Build.props` to centralise `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>` across all projects so individual csproj files stay minimal.

Files expected to be created or modified:
- `.gitignore`
- `.editorconfig`
- `NuGet.config`
- `Directory.Build.props`

## Verification
- [ ] `dotnet build` passes with zero errors and zero warnings
- [ ] All four projects appear in the solution explorer
- [ ] `global.json` pins a .NET 10 SDK version
- [ ] `.editorconfig` rules are respected in the IDE
