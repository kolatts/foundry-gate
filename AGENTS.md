# FoundryGate — repo instructions for coding agents (Codex, Claude, others)

## Implementation phase (started 2026-09-01)

- This file mirrors `CLAUDE.md` exactly; keep both in sync (CI does not check this yet).
- **Read `CONVENTIONS.md` before writing any code** — it is the engineering
  contract (imagile-app-derived EF Core/Azure SQL patterns minus multitenant
  sharding, storage-account rules, fully-automated CI/CD shape, testing/verification
  requirements). Where it conflicts with foundrygate-spec.md or an issue body,
  CONVENTIONS.md wins.
- Issues #7/#9/#10/#13/#32/#33/#38/#39/#43/#44 have direction-update comments that
  supersede parts of their bodies (gateway-centric shift; `infra/` already exists
  with the validated gateway data plane — extend it, don't recreate it). Read the
  comments and plans/24+25 before implementing those.
- One issue per implementation agent, isolated worktree, verifiable steps: build
  passes, `FoundryGate.Tests.Predeployment` passes, `dotnet format
  --verify-no-changes` clean, plan-file Verification items checked. PR + review
  cycle before merge.
- **Everything is a GitHub issue.** All work — features, fixes, follow-ups, tech
  debt, deferred validation — is tracked as a GitHub issue before it is worked.
  Never leave follow-up work as inline TODOs, PR-body notes, or plan-file
  side-remarks: file an issue (labeled appropriately, on the right milestone) and
  reference it. A PR that discovers new work files the issue in the same breath.
  PRs close their issues via `Closes #N`.
- **Live gateway testing.** To stand the gateway up, prove enforcement and measurement
  against it with real Codex/Claude Code sessions, and tear it back down, use the
  `gateway-cycle` skill (`.claude/skills/gateway-cycle/SKILL.md`) and `scripts/cycle/`.
  Teardown defaults to KeepFoundry because Anthropic deployments are create-once (E-007).
- **imagile-bot automation** (mirrored from pncli): `claude-triage.yml` implements
  issues labeled `claude-triage` as `claude/issue-N` PRs under the imagile-bot app
  identity; `claude-review.yml` gives every PR a formal Claude review. Both are
  gated on the repo variable `CLAUDE_AUTOMATION_ENABLED='true'` (requires secrets
  `CLAUDE_CODE_OAUTH_TOKEN` + `IMAGILE_BOT_PRIVATE_KEY` and the imagile-bot app
  installed on this repo). Never modify `.github/` from triage-driven work.

## Docs invariants — check on EVERY change

1. **Landing page** (`docs-site/src/pages/index.astro`) is a dead-simple explainer for
   people who don't understand technology. Plain language, no jargon in the hero or
   story sections, demonstrative fake data (hardcoded, Bogus-style) plus real test
   evidence. If a change alters what FoundryGate does or how it behaves (enforcement
   semantics, limits, models, pricing story), re-read the landing page and update it
   to stay true and simple.
2. **Progressive "how it works" docs**: `getting-started/why-foundrygate` (simplest) →
   `architecture/overview` (moderate) → `architecture/feasibility` + `reference/*`
   (full detail). Every behavioral change must be reflected at ALL levels it touches,
   in the right register for that level — simple stays simple, detailed stays precise.
3. **CLI setup instructions** (`getting-started/cli-setup.mdx`) contain only
   empirically verified configuration (wire-captured headers, tested env vars/config
   keys). Never add an instruction that hasn't been tested against a real gateway.
4. Docs build must pass: `npm run build` in `docs-site/` (Node 20, `npm ci` first).
   `.md` files cannot use MDX components — pages importing Starlight components must
   be `.mdx`.

## Architecture ground truths (Sep 2026)

- Enforcement is real-time at the APIM gateway (`llm-token-limit`: per-dev TPM 429s +
  monthly token quota 403s, keyed on APIM subscription). The Log Analytics sync is
  reconciliation, not enforcement.
- `token-quota` accepts only literals (no policy expressions) → quota tiers are APIM
  products.
- Anthropic (Claude) model deployments are create-once under ARM: never re-PUT an
  existing one, never delete/recreate in a loop (see fable-refactor-log.md E-007).
- Claude Code sends `x-api-key`; Codex needs `env_http_headers = { "api-key" = ... }`.
- Infra lives in `infra/` (subscription-scope `main.bicep`); re-runs must pass
  `createModelDeployments=false`.

## Conventions

- Plans in `plans/{nn}-{kebab}.md`, one per epic issue; issue conventions in PLANS.md.
- Decision log for the 2026-09 strategic pass: `fable-refactor-log.md`.
