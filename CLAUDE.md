# FoundryGate — repo instructions for Claude

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
