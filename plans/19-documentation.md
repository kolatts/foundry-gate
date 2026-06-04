# Documentation — Astro/Starlight docs site on GitHub Pages

> GitHub: #19  
> Milestone: v0.1 — Foundation  
> Labels: epic, docs

## Overview
Documentation lives in a `docs-site/` Astro project using the Starlight theme, deployed to GitHub Pages via a `docs.yml` workflow. Starlight gives FoundryGate a polished, searchable docs site with zero custom CSS needed — sidebar navigation, dark mode, and a built-in search index out of the box. The existing `docs/` markdown files become content pages in the Astro site. The README links to the live site rather than repeating docs inline.

## Approach

### Scaffold Astro Starlight docs site and GitHub Pages deployment workflow (#58)
Create `docs-site/` at the repo root using `npm create astro@latest -- --template starlight`. Configure `astro.config.mjs` with `site: 'https://kolatts.github.io/foundry-gate'`, `base: '/foundry-gate'`, and the Starlight sidebar with four top-level sections: **Getting Started** (index + fork guide), **Architecture** (system overview + component diagram), **Reference** (configuration keys + API surface), **Contributing**. Add `docs-site/` to `.gitignore` for `node_modules` and `dist/`. Create `.github/workflows/docs.yml` triggered on `push` to `main` (paths: `docs-site/**`) that runs `npm ci && npm run build` then deploys `dist/` using `actions/deploy-pages@v4` with `permissions: pages: write, id-token: write`.

Files expected to be created or modified:
- `docs-site/astro.config.mjs`
- `docs-site/package.json`
- `docs-site/src/content/docs/index.mdx`
- `.github/workflows/docs.yml`

### Write fork guide as Astro content — step-by-step setup for a new Azure tenant (#56)
Create `docs-site/src/content/docs/getting-started/fork-guide.md`. Checklist-driven: Prerequisites (Azure CLI, Bicep CLI, .NET 10 SDK, `gh` CLI); Step 1 — Entra App Registration (app roles, Graph API permissions, redirect URI); Step 2 — **APIM product and API setup** (this is the most common point of failure for forks and must be covered in full):
  - Create an APIM product named `foundrygate` (or your configured name)
  - Add an API in APIM that backends to your Azure AI Foundry endpoint (`https://{foundry-name}.openai.azure.com/`) with appropriate path prefix
  - Apply the `llm-emit-token-metric` policy on the API — this is how token usage reaches Application Insights
  - Apply the `azure-openai-token-limit` policy with a large safe ceiling and `counter-key="@(context.Subscription.Id)"` — APIM will manage per-user counters; FoundryGate suspends subscriptions when the DB-tracked quota is exceeded
  - Grant the APIM identity `Cognitive Services User` on the Foundry resource so APIM can forward requests
  - Add the API to the `foundrygate` product

Step 3 — GitHub OIDC federated credentials; Step 4 — GitHub Actions variables and secrets; Step 5 — `az deployment sub create` with `dev.bicepparam`; Step 6 — Push to main for first CI/CD run. Each step includes exact CLI commands, expected output, and a troubleshooting note.

Files expected to be created or modified:
- `docs-site/src/content/docs/getting-started/fork-guide.md`

### Write developer CLI quickstart page (#57-cli)
> This content is part of sub-issue #57 — add a page at `docs-site/src/content/docs/getting-started/cli-setup.md`. Target audience: a developer who has just received their APIM subscription key from the admin and wants to point Codex or Claude Code at the Foundry gateway. Cover: finding your APIM gateway URL (visible on the `/me` page), verifying the key works with a `curl` one-liner, then per-tool configuration snippets for Claude Code (`claude config set ...`), Codex CLI (`export OPENAI_BASE_URL=...`), and a generic OpenAI-compatible client. Include a section on available model names and how to check them from the `/me` page.

### Write architecture, configuration reference, and finalize README (#57)
Create `docs-site/src/content/docs/architecture/overview.md` with a Mermaid system-context diagram, explanation of the four solution projects, the five-level quota resolution flowchart, the APIM key lifecycle state machine, and the Azure Functions data flow (timer triggers → DB → Application Insights). Create `docs-site/src/content/docs/reference/configuration.md` documenting every `SystemConfiguration` DB key and every `appsettings.json` / environment variable. Finalize `README.md` at the repo root with a one-paragraph description, a features bullet list, a link to the live GitHub Pages site, and build/license badges.

Files expected to be created or modified:
- `docs-site/src/content/docs/architecture/overview.md`
- `docs-site/src/content/docs/reference/configuration.md`
- `README.md`

## Verification
- [ ] `npm run build` in `docs-site/` produces a `dist/` with no errors
- [ ] `docs.yml` deploys to GitHub Pages and the site is accessible at `kolatts.github.io/foundry-gate`
- [ ] Starlight sidebar matches the configured structure
- [ ] All Mermaid diagrams render in the Astro build
- [ ] Fork guide is completable end-to-end against a real Azure subscription
- [ ] All `SystemConfiguration` keys and environment variables are documented
