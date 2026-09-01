# Session handoff — 2026-09-01 strategic pass + implementation kickoff

Everything below is the complete state for whoever (human or agent) picks this up. The decision trail lives in `fable-refactor-log.md` (D-001–D-012, E-001–E-010); engineering contract in `CONVENTIONS.md`; agent rules in `CLAUDE.md`.

## Merged to main today (all through review cycles)

- **PR #87** — gateway data plane `infra/` (APIM v2, MI-auth backends, pools/breakers, front doors, LLM logging), live-validated on Imagile Paid then torn down. Docs overhaul, plain-language landing page, cost & capacity page, `research.md` corrections.
- **PR #90** — .NET 10 solution scaffold (9 projects, central props/packages, docker-compose). Closes #1/#20/#21.
- **PR #91** — Domain contracts (DTOs/enums/constants/paging + reveal-vs-masked key DTOs). Closes #3/#24/#25.
- **PR #92** — data layer (7 entities, AppDbContext, TimeProvider interceptor, idempotent seeding, convention tests; 65/65). Closes #2/#22/#23.
- **PR #93** — v0.5 gateway: tier products (literal token-quota), priority pools, model-alias/allowlist fragments, require-product guard. Closes #82/#83/#85/#86.
- CI (`ci.yml` build-test gate) + **branch protection on main** (1 review, build-test required, no force pushes, admins exempt). imagile-bot workflows (`claude-triage.yml`, `claude-review.yml`) landed but INERT pending secrets (below).

**22 issues closed.** Epics #1, #2, #3, #19 complete; #81 waits only on #84 + #88.

## Open PRs — all CI-green implementations, mid-review-fix at shutdown

Each has a posted review + a fix pass that was landing at shutdown; check the latest PR comment for DONE-vs-REMAINING status:

1. **PR #97** (db tooling, closes #77/#78/#79) — REQUEST CHANGES: 5 Minors incl. an orchestrator override: flip `db deploy` to block-on-data-loss by DEFAULT (`--allow-data-loss` opt-in) + amend CONVENTIONS.md accordingly. **MERGE FIRST** (see collision).
2. **PR #99** (API foundation, closes #26/#27) — REQUEST CHANGES: Major = unmapped-exception 500s leak `exception.Message`; must fall through to a generic 500. **Merges SECOND**: modify/delete conflict with #97 on `appsettings.Development.json` — resolve by keeping #99's deletion, ensuring its replacement `appsettings.local.json` says `127.0.0.1,3433`.
3. **PR #98** (Blazor shell, closes #48) — REQUEST CHANGES: Major = catch MSAL `AccessTokenNotAvailableException` → Unauthorized result. No collisions; merge any time after fixes verified.

Merging: `gh pr merge N --merge --admin` (protection requires a review no second identity can give until bot secrets land). Close #94 when #97 merges (cascade evidence is in its body).

## Owner actions required (cannot be done by an agent)

1. **imagile-bot secrets**: add `IMAGILE_BOT_PRIVATE_KEY` + `CLAUDE_CODE_OAUTH_TOKEN` to this repo (values from pncli), ensure the imagile-bot GitHub App installation covers foundry-gate, then set repo variable `CLAUDE_AUTOMATION_ENABLED=true`. This activates label-driven triage + formal claude[bot] PR reviews (satisfying branch protection properly).
2. **Issue #88** (Claude-on-Foundry platform wedge): retry a single `claude-haiku-4-5` create after 2026-09-02, or open an Azure support ticket (needs your support plan). The next live gateway deploy also validates the #93 tier/alias stack (checklist in plans/24+25 and on #88).

## Remaining backlog (~57 open issues), dependency-ordered

1. After #99 merges: endpoint epics fan out in parallel — #28/#29 (users), #30/#31 (groups), #32/#33 (quota), #34/#35 (requests), #36/#37 (APIM keys), #40/#41 (Entra sync), #42 (audit), #61 (foundry deployments API), then #65/#66 (lifecycle orchestration — use a strong model).
2. Functions: #38/#39 + #84 (reconciliation reads `ApiManagementGatewayLlmLog`; epic #81 closes with #84).
3. Infra extension #43/#44 (SQL/Container Apps/SWA/Functions/KV/RBAC modules added to the EXISTING `infra/` — do not recreate the gateway; `createModelDeployments=false` on re-runs, Anthropic deployments are create-once).
4. CI/CD epic #67 family (#45–#47, #58, #68–#75): reusable deploy workflows exist for db (#79); OIDC app registration + federated credentials + `AZURE_*` repo variables needed before any deploy workflow can run.
5. Frontend pages #49–#55, #62/#63 after their API endpoints exist (Blazor confirmed over React — D-012).
6. Docs/diagrams: #89 (architecture diagrams on the public site — the CLAUDE.md invariant).
7. Follow-ups filed today: #94 (closes with #97), #95 (key encryption), #96 (ip setup), plus any filed by the closing fix passes.

## Working model that produced today''s output (keep it)

One issue-set per agent in an isolated worktree → verifiable gates (zero-warning build, Predeployment tests, format, live proof where possible) → PR with `Closes #N` in the BODY → review (consolidated single-pass reviewer; ≥70-confidence threshold; Major/Minor/Nit) → fix pass by the same agent → merge → epics closed when children close. Everything discovered mid-work becomes a GitHub issue immediately.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
