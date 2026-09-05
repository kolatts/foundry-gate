---
name: gateway-cycle
description: Spin the FoundryGate gateway data plane up in Azure, prove enforcement and measurement against it with real CLI harnesses, and spin it back down — unattended. Use when asked to "spin up the gateway", "run the cycle", "deploy the test gateway", "tear down the gateway", "test codex against the gateway", "prove the quota works", "check the gateway is torn down", or when live evidence is needed for a claim about enforcement, quotas, token accounting or CLI compatibility.
---

# Gateway spin-up / test / spin-down cycle

The scripts in `scripts/cycle/` deploy the gateway, drive real Codex and Claude Code
sessions through it into its 429 and 403 walls, reconcile the token counts out of Log
Analytics, and tear it back down — writing a markdown evidence report to `validation/`.

**The one command:**

```sh
pwsh scripts/cycle/cycle.ps1 -Subscription "Imagile Paid"
```

Roughly 35-55 minutes end to end. It is safe to run unattended: no stage prompts, no stage
waits on a human, and the teardown runs from a `finally` block so a mid-cycle failure still
stops the APIM meter.

---

## Before you start

| Requirement | Check |
|---|---|
| Azure CLI logged in, Owner on the target subscription | `az account show --subscription "<name>"` |
| PowerShell 7 | `pwsh -v` |
| Codex CLI (for the harness stage) | `codex --version` |
| Claude Code CLI (only used when a Claude deployment exists) | `claude --version` |
| Claude GlobalStandard quota in the subscription | see below |

Pass `-SkipHarness` if `codex` is not installed; every other stage still runs.

**The subscription is always passed explicitly.** These scripts never read or change the
operator's default `az` subscription.

---

## What each stage does, and what PASS looks like

Run them individually if you only need one; they all read and write the same state file at
`scripts/cycle/.state/<env>.json` (gitignored — it holds live APIM keys).

### `up.ps1` — 8-14 min

Purges a soft-deleted APIM holding the name (see below), validates the policy XML offline,
builds the Bicep, deploys `infra/main.bicep` at subscription scope with two demo tiers of
deliberately **opposite shapes**, because no single tier can demonstrate both meters:

| Tier | Monthly | TPM | Proves |
|---|---|---|---|
| `standard` | 40 000 | 12 000 | the **429** wall (`T4`, `C2`) — tight per-minute cap |
| `power` | 60 000 | 100 000 | the **403** wall (`T5c`, `C3`) — small budget, room to spend it |

The TPM floor matters. `codex exec` spends ~9–10K tokens on its system prompt before it does
anything, so a tier whose per-minute cap is *below* one exec **deadlocks the harness**: Codex
429s, retries, keeps the bucket empty, gives up without finishing, and therefore never spends
its monthly budget at all. Verified live across 25 consecutive execs at both 8 000 and 12 000
TPM — the 403 is simply unreachable from behind a tight 429. That is why `C3` runs on the
generous-TPM tier ([#237](https://github.com/kolatts/foundry-gate/issues/237)).

PASS looks like: `UP-1 PASS`, a gateway URL printed, and every deployment listed as
`Succeeded`. APIM StandardV2 provisioning dominates the wall clock.

### `subscriptions.ps1` — seconds

Creates three APIM subscriptions against tier products, through the Management REST API
(`az apim` has no subscription verb):

| Key | Tier | Role in the tests |
|---|---|---|
| `dev-alice` | standard | the victim — smoke.ps1 drives her into 429 then 403 |
| `dev-bob` | standard | TPM isolation control, then the Codex harness subject |
| `dev-carol` | power | monthly-quota control, and the `C3` harness subject |

**Every cycle mints fresh subscription ids** (`dev-alice-202609051530`). This is not
cosmetic: the monthly `token-quota` counter is keyed on the APIM subscription and there is no
way to reset it, so reusing `dev-alice` means the *second* cycle in a calendar month starts
her at 403 and can demonstrate neither wall. `-Reuse` keeps the ids already in the state file.

The stage then **waits for each key to propagate**. A subscription the Management API has
already created is not immediately accepted by the gateway; without the wait, the first few
smoke checks fail with `401 Access denied due to invalid subscription key` on a key that
works a minute later.

### `smoke.ps1` — 8-15 min

The enforcement matrix. Every row prints PASS/FAIL/SKIP and the script exits non-zero on
any FAIL.

| ID | What it proves |
|---|---|
| `T3a-d` | no key and bad key are 401 on **both** front doors |
| `T2a/T2b` | `/openai/v1/chat/completions` and `/openai/v1/responses` are 200 with `api-key` |
| `T1` | `/anthropic/v1/messages` is 200 with `x-api-key` + `anthropic-version` (SKIP when no Claude deployment) |
| `A1` | an unknown alias is 403 `model_not_permitted` |
| `A2` | the **real deployment name** is also 403 — the alias map is the allowlist |
| `A3` | a Claude alias on the OpenAI front door is 403 naming the right base path |
| `T4a/T4b` | TPM cap returns 429 + `Retry-After` + `x-fg-remaining-tpm`, and same-tier `dev-bob` is unaffected (counter keyed on subscription) |
| `T5a-c` | monthly quota returns a native 403 with `x-fg-remaining-quota` at 0, while power-tier `dev-carol` is still 200 |

Most of the wall clock is T5: burning a 40K budget through an 8K/min cap takes at least
five minutes no matter how the requests are shaped. That is arithmetic, not a hang.

### `codex-test.ps1` — 10-25 min

The part the owner actually asked for. Writes an **isolated `CODEX_HOME`** under the state
directory (the operator's own `~/.codex` is never read or written) with the `config.toml`
from `getting-started/cli-setup.mdx`, then runs `codex exec` as `dev-bob` until the gateway
answers 429 and then 403, recording Codex's exit code and stderr at each wall.

The one non-obvious config line, and the first thing to check if C1 starts returning 401:

```toml
env_http_headers = { "api-key" = "FOUNDRYGATE_API_KEY" }
```

`env_key` alone makes Codex send `Authorization: Bearer`, which the gateway rejects (E-010).

`C4` runs `claude -p` with `CLAUDE_CODE_USE_FOUNDRY=1` and the `ANTHROPIC_FOUNDRY_*` /
`ANTHROPIC_DEFAULT_*_MODEL` variables from the same doc page. It is **SKIP, not FAIL**, when
no Claude deployment reached `Succeeded`.

### `measure.ps1` — 2-15 min

Polls for `ApiManagementGatewayLlmLog` rows (Log Analytics ingestion lags — the 2026-09-01
validation ended with this "pending"), runs `src/FoundryGate.Functions/Kql/UsageBySubscription.kql`
verbatim, and checks the D-017 assumption empirically: do duplicate `CorrelationId`s
actually occur, and how far would a naive `sum()` have been off? That is the KQL half of #178.

If M1 times out, that is ingestion lag, not a failure of the gateway. Re-run `measure.ps1`
later against the same state file, then `report.ps1`, and the report picks up the numbers.

### `down.ps1` — 3-8 min

See the teardown section below.

### `status.ps1` — seconds

What exists, roughly what it costs per hour, how long it has been up. Run it any time,
including before the first deploy and after a teardown. Use it to answer "did I leave the
gateway running?"

---

## Anthropic (Claude) deployments — the rules that are not negotiable

Anthropic deployments under ARM are **create-once per account** (`fable-refactor-log.md`
E-006/E-007). Re-PUTing one drives it to `Failed`; delete/recreate churn makes the *account*
start refusing new Claude deployments, first asynchronously and then synchronously, even
under fresh deployment names.

1. **One create attempt per fresh account per cycle**, made by the day-0 ARM deploy. That is
   it.
2. **Never retry a failed Claude create.** Not with a different name, not "just once more".
3. **Never delete and recreate** a Claude deployment to fix it.
4. If it fails: record the correlation id, mark it, and **continue on the OpenAI path**.
   Codex is the primary harness and needs only `gpt-4-1-mini`. `up.ps1` does exactly this on
   its own and sets `claudeAvailable=false` in the state file; every Claude-dependent check
   then reports SKIP.
5. `up.ps1` auto-detects day-0 (no Cognitive Services account in the resource group) and
   passes `createModelDeployments=false` on every other run. Do not override that with
   `-CreateModelDeployments` unless the account is genuinely brand new.
6. `-SkipClaude` deploys no Anthropic models at all. Use it when a Claude create attempt has
   already been spent in this subscription and you only need the OpenAI demo.

There is one deliberate exception to "ARM owns deployments": `up.ps1` will create a
**missing OpenAI** deployment out of band, because OpenAI deployments provision
synchronously and reliably in the same accounts where Anthropic ones are fragile (E-007e).
It never touches an existing deployment and never runs for `format=Anthropic`.

---

## Teardown: KeepFoundry vs Full

```sh
pwsh scripts/cycle/down.ps1                # KeepFoundry — the default, use this
pwsh scripts/cycle/down.ps1 -Mode Full     # clean slate, spends Claude create attempts
```

**`KeepFoundry` (default).** Deletes everything in the resource group *except* the
`Microsoft.CognitiveServices/accounts` and the telemetry stores (Log Analytics workspace,
Application Insights), **and purges the soft-deleted APIM service**. The Foundry accounts
survive for the create-once reason above; the telemetry stores survive because Log Analytics
ingestion lags the traffic by longer than a cycle takes, so deleting them at teardown
destroys the measurement evidence minutes before it arrives. Keeping them lets `measure.ps1`
be re-run against the same state file after the gateway is gone. Neither bills at rest. That
purge is load-bearing: deleting an APIM service only soft-deletes it and the name stays
reserved, so without it the very next `up.ps1` fails with
`ServiceAlreadyExistsInSoftDeletedState` and "spin up and down frequently" is impossible
after exactly one cycle. Purging APIM has nothing to do with the Anthropic create-once
problem, which is about Cognitive Services accounts — and those this mode keeps.
APIM goes — it is the only meaningful idle cost —
and the Foundry accounts and their model deployments survive, so the next `up.ps1` re-runs
the template with `createModelDeployments=false` over them. Idle Foundry accounts cost
nothing; consumption is per token. This is what makes "spin up and down frequently"
survivable given rule 1 above.

**`Full`.** Deletes the resource group, then purges the soft-deleted APIM service
(`az apim deletedservice purge`) and the soft-deleted Cognitive Services accounts
(`az cognitiveservices account purge`) so a redeploy is not blocked by an invisible name
conflict. **This spends the account's Claude create attempts** — the next day-0 may come up
with no working Claude deployment. Only use it when a clean slate is genuinely required and
that cost is understood.

Confirm a teardown landed with `status.ps1`: `State: TORN DOWN (KeepFoundry)` and only
Foundry accounts listed.

---

## Cost while it is up

| Resource | Idle cost |
|---|---|
| APIM StandardV2 | **~$0.28/hr** (~$203/month) — the whole reason to tear down |
| Foundry accounts | $0 idle; pay per token |
| Log Analytics + App Insights | per ingested GB; a cycle ingests megabytes |

A full cycle costs well under a dollar of APIM time plus a few cents of tokens. Leaving the
gateway up overnight costs about $7. `status.ps1` prints the running total.

---

## Reading the evidence report

`cycle.ps1` writes `validation/<yyyy-MM-dd>-gateway-cycle.md`: a summary table (gateway URL,
tier under test, teardown mode, PASS/FAIL/SKIP counts), stage timings, every check with its
raw header and body evidence, the model-deployment states, the reconciliation totals and the
D-017 finding.

Keys are redacted on the way into the state file and again on the way out. **SKIP is not
FAIL** — a SKIP row says a capability was not exercised on this run and names why.

Re-render at any time without re-running the cycle:

```sh
pwsh scripts/cycle/report.ps1 -Path validation/2026-09-05-gateway-cycle.md
```

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `401` on the OpenAI front door | key sent as `Authorization: Bearer` | Codex needs `env_http_headers = { "api-key" = "…" }`, not `env_key` (E-010) |
| `401` on the Anthropic front door | key sent as `api-key` | Claude Code sends `x-api-key`; the APIM API is configured for that header |
| `403` with `x-fg-error: model_not_permitted` | the `model` is not an alias in that tier's map | use the alias (`gpt`, `sonnet`, `haiku`), never the deployment name |
| `403` with `x-fg-error: plan_required` | the key is not scoped to a tier product | reissue it against a product, not at API scope |
| `403` with **no** `x-fg-error` | APIM's native monthly `token-quota` refusal | expected in T5/C3; otherwise move the developer to a bigger tier |
| `429` + `Retry-After`, `x-fg-remaining-tpm: 0` | the gateway's per-subscription TPM cap | expected in T4/C2; honour `Retry-After` |
| `429` but `x-fg-remaining-tpm` still **full** | not the meter — the shared Foundry deployment is saturated, passed through because the OpenAI policy does not retry a single backend | raise the deployment's capacity, or spread load; the developer's own budget is untouched |
| Codex 429s forever and never finishes an exec | the tier's TPM is below one exec (~10K) | raise the tier's TPM — from behind this wall the monthly quota is unreachable (#237) |
| `ServiceAlreadyExistsInSoftDeletedState` on deploy | APIM soft-delete reserves the name | `az apim deletedservice purge`; `up.ps1` and `down.ps1` both do this now |
| `401` on a key that was just created | the key has not propagated to the gateway nodes yet | wait ~30–60s; `subscriptions.ps1` polls for this |
| `Conflict: Link already exists between specified Product and Api` | `apiLinkId` must be unique across the whole APIM **service**, not per product | tier-prefix the link id, and delete the old links first (#230) |
| `404` from the backend on a Claude alias | the alias resolved but the deployment does not exist | E-007 — do **not** recreate it in a loop |
| M1 times out with no LLM log rows | Log Analytics ingestion lag | re-run `measure.ps1` then `report.ps1` later; not a gateway failure |
| `az` "Failed to parse string as JSON" on a deployment | cmd.exe ate the quotes out of a JSON parameter | use `Format-AzJsonArg` from `_common.ps1` |
| Deployment fails with `DeploymentActive` | a previous nested deployment is still running | wait for it or cancel it; never start a second Claude create alongside one in flight |

## Files

- `scripts/cycle/*.ps1` — the stages; `_common.ps1` holds the state file, `az` wrapper,
  HTTP probe and redaction helpers.
- `scripts/cycle/.state/` — gitignored state, including live keys.
- `validation/` — committed evidence reports.
- `fable-refactor-log.md` — E-006/E-007 (Anthropic), E-010 (Codex header), D-017 (the KQL
  de-duplication).
