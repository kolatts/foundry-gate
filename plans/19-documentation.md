# Documentation — fork guide, architecture, and configuration reference

> GitHub: #19  
> Milestone: v0.4 — Frontend  
> Labels: epic, docs

## Overview
This epic produces the documentation that makes FoundryGate forkable and operable by a team that was not involved in building it. Three documents are needed: a step-by-step fork guide for setting up a new Azure tenant from scratch, an architecture document explaining the system design and component interactions, and a configuration reference covering every `SystemConfiguration` key and every environment variable. The README is also finalised here with a concise project description and links to the three docs.

## Approach

### Write docs/fork-guide.md — step-by-step setup checklist for a new Azure tenant (#56)
Write a checklist-driven guide that takes a reader from a blank Azure subscription to a running FoundryGate deployment. Sections: Prerequisites (Azure CLI, Bicep CLI, .NET 10 SDK, GitHub CLI), Step 1 — Register the Entra application and configure app roles (`FoundryGate.Admin`, `FoundryGate.Developer`), Step 2 — Create an APIM instance and a FoundryGate product, Step 3 — Set up GitHub OIDC federated credentials (one per environment), Step 4 — Create GitHub Actions variables and secrets, Step 5 — Run `az deployment sub create` with `dev.bicepparam`, Step 6 — Push to main to trigger the first CI/CD run. Each step lists the exact CLI commands to run, the expected output, and what to do if something goes wrong. The guide should be completable in under two hours by someone familiar with Azure but new to this project.

Files expected to be created or modified:
- `docs/fork-guide.md`

### Write docs/architecture.md, docs/configuration.md, and finalize README (#57)
Write `docs/architecture.md` covering: system context diagram (described in Mermaid), the four solution projects and their responsibilities, the five-level quota resolution algorithm with a flowchart, the APIM key lifecycle state machine, and the data flow for usage sync from Application Insights back to `QuotaAllocation`. Write `docs/configuration.md` as a two-section reference: the seven `SystemConfiguration` database keys (name, default value, description, valid range) and all environment variables / appsettings keys consumed by the API at runtime (database connection string, Entra settings, APIM resource names, Application Insights workspace ID, internal sync secret). Finalise `README.md` with a one-paragraph project description, a "Features" bullet list, a "Getting started" section that links to the fork guide, and a badges row (build status, license).

Files expected to be created or modified:
- `docs/architecture.md`
- `docs/configuration.md`
- `README.md`

## Verification
- [ ] Fork guide is completable end-to-end against a real Azure subscription
- [ ] All seven `SystemConfiguration` keys are documented with defaults
- [ ] All environment variables in `appsettings.json` are documented
- [ ] Architecture Mermaid diagrams render correctly on GitHub
- [ ] README links to all three docs and the build badge reflects the `api.yml` workflow
