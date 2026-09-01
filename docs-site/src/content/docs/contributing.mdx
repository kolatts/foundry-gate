---
title: Contributing
description: How to contribute to FoundryGate.
---

import { Steps } from '@astrojs/starlight/components';

Foundry Gate is open-source and welcomes contributions. The project uses GitHub Issues for tracking work — every issue has a corresponding plan file in `/plans/` with implementation detail.

## Before you start

- Read `PLANS.md` in the repo root for the planning convention.
- Find an open issue in the milestone you want to work on.
- Check the linked plan file (e.g., `plans/05-users.md`) for approach and file expectations.
- Comment on the issue to claim it.

## Local development setup

<Steps>

1. **Prerequisites**: .NET 10 SDK, Docker Desktop, Node.js 20+, Azure CLI

2. **Install the Foundry Gate CLI** (once the CLI project is built):
   ```sh
   dotnet tool install --global FoundryGate.Cli
   ```

3. **Bootstrap local database**:
   ```sh
   Foundry Gate local setup
   ```
   This starts a SQL Server Docker container, runs EF migrations, and seeds `SystemConfiguration` defaults.

4. **Run the API**:
   ```sh
   cd src/FoundryGate.Api
   dotnet run
   ```

5. **Run the UI** (separate terminal):
   ```sh
   cd src/FoundryGate.Web
   dotnet run
   ```

6. **Run the docs site** (separate terminal):
   ```sh
   cd docs-site
   npm install
   npm run dev
   ```

</Steps>

## Schema changes

1. Modify the EF entity in `FoundryGate.Data/Entities/`
2. `dotnet ef migrations add <Name> --project src/FoundryGate.Data --startup-project src/FoundryGate.Api`
3. `dotnet ef database update ...`
4. `Foundry Gate db compare` — syncs changes back to `FoundryGate.Database/.sqlproj`
5. Commit both the migration file and the updated `.sql` file together.

## Commit convention

```
feat(scope): short description   # new feature
fix(scope): short description    # bug fix
docs(scope): short description   # docs only
refactor(scope): short description
```

Reference the issue number: `feat(quota): add five-level resolution (#32)`

## Pull requests

- One PR per issue.
- The PR description should summarise what changed and link to the issue.
- All PRs must pass `dotnet build` and `dotnet test`.
- Update the relevant plan file if the approach changed during implementation.
