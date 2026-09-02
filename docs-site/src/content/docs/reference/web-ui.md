---
title: Web UI Routes
description: Every page in the Foundry Gate Blazor app — its route, who can reach it, which API endpoints it calls, and what it lets you do.
---

The portal is a Blazor WebAssembly app (`src/FoundryGate.Web`) signed in with Entra ID via MSAL.
Every route except `/` requires an authenticated caller — `/` is the sign-in pitch, and is the only
page an anonymous visitor sees. Admin routes additionally require the `FoundryGate.Admin` app role,
and an authenticated caller who lacks it gets an "Access denied" page rather than a redirect loop.
The app references `FoundryGate.Domain` only — it renders the same request/response records the API
returns, so a contract change is a compile error, not a runtime surprise.

Signing in at `/` sends an admin to `/dashboard` and everyone else to `/me`.

## Developer pages

| Route | Auth | What it does |
|---|---|---|
| `/` | Any | Sign-in pitch when signed out; redirects to `/dashboard` or `/me` when signed in |
| `/me` | Any | The developer's own account: budget gauge, gateway key, CLI setup, request history |
| `/me/request` | Any | Ask for a bigger monthly budget |

### `/me`

One call to [`GET /users/me`](/foundry-gate/reference/api/#users) supplies the whole page — the
resolved quota, the masked key and the gateway addressing arrive together — plus
`GET /foundry/models` for the deployment names and `GET /requests` for the caller's own history.

- **Budget gauge.** Tokens used against the monthly budget, coloured green below 80%, amber from
  80% through 95%, and red above 95%; an unlimited budget shows a chip instead of a bar. The tier
  name sits beside the number because **the tier is what the gateway enforces** — if the two ever
  disagree (a legacy value that matches no tier), the page says so and names the tier actually in
  force. The gauge also states which level of the quota-resolution chain produced the number, and
  that usage is reconciled from gateway logs rather than live.
- **Gateway key.** Masked to its last four characters. *Reveal* calls
  `POST /keys/me/reveal` and holds the plaintext in component state for that render only — never in
  `localStorage`, a cookie, or any other browser storage. *Rotate* is behind a confirmation dialog
  and calls `POST /keys/me/rotate`, showing the new value once. A developer whose key has not been
  provisioned yet sees a "being set up" state, not an error.
- **Configure your AI CLI.** Copy-paste snippets for Claude Code, Codex CLI and `curl`, filled in
  with this gateway's URL and this developer's **model aliases** — and with the real key while it is
  revealed. The alias, not the deployment name, is what goes in `model`: the gateway's per-tier
  alias map is also its allowlist, so a deployment name comes back `403 model_not_permitted`. Every
  instruction is copied from [CLI Setup](/foundry-gate/getting-started/cli-setup/), which carries
  only empirically verified configuration; the page never invents an environment variable or header,
  and a test reads that page's fenced blocks and fails if the two drift.
- **Request history.** The caller's quota increase requests with their review state and any
  reviewer notes.

### `/me/request`

A monthly budget **is** a gateway tier, so this form asks for a tier rather than a number: APIM's
`token-quota` is a per-product literal, and a request for an arbitrary figure is one the gateway
could never enforce. The dropdown is `GET /quota/tiers` filtered to the tiers strictly above the
caller's current one (a developer already on the top tier is told there is nothing bigger to ask
for). The justification field validates against the same `DataAnnotations` the API enforces.
Submitting is disabled while a request is in flight and while one is already pending; if the API
answers `409` anyway, the page says so and locks the form instead of navigating away.

## Admin pages

| Route | Auth | What it does |
|---|---|---|
| `/dashboard` | Admin | Fork-wide stats and the busiest consumers this period |
| `/users` | Admin | Searchable, server-paged list of everyone |
| `/users/{id}` | Admin | One person: fields, budget, groups and gateway key |
| `/users/sync` | Admin | Reconcile the user list against Entra |
| `/groups` | Admin | Groups with member count, budget and roster source |
| `/groups/new` | Admin | Create a group, optionally linked to an Entra group |
| `/groups/{id}` | Admin | A group's policy and roster |
| `/requests` | Any | The quota increase review queue. **Not admin-only**: a developer sees their own requests here (the nav links it as "My Requests"), an admin sees everyone's — the API scopes the list, not the route |
| `/requests/{id}` | Owner or Admin | One request, with the approve/reject panel for admins |
| `/foundry` | Admin | Foundry model deployments |
| `/config` | Admin | Edit the `SystemConfiguration` key-value rows |
| `/audit` | Admin | Browse and filter the audit trail |

### `/dashboard`

`GET /dashboard` fills four stat cards — total users, active users (with the unlimited count),
pending quota increase requests, and tokens used this period
([#190](https://github.com/kolatts/foundry-gate/issues/190) adds the hard-stopped count the summary
does not carry yet) — plus a top-ten consumers grid with a
per-row usage bar. The pending count is a badge linking to the filtered review queue, and the same
count badges the "All Requests" link in the nav. The page re-reads the summary every 60 seconds and
stops the moment you navigate away; a failed background refresh leaves the last good numbers on
screen rather than replacing them with an error.

Every figure here is a reconciliation number from the Log Analytics sync, refreshed on that job's
cadence — not a live view of gateway enforcement.

### `/users` and `/users/{id}`

`/users` is a server-paged grid over [`GET /users`](/foundry-gate/reference/api/#users): the search
box is debounced by 300 ms and matches display name or email, the status filter narrows to active or
deactivated accounts, and changing either starts again from page one. The budget column shows a
**tier name, never a token count** — the same rule the whole portal follows, because the tier is what
the gateway enforces. A row opens the detail page.

`/users/{id}` renders `GET /users/{id}` — the row plus group memberships, this period's allocation
and the masked key — in four tabs:

- **Overview.** Identity, employee id, creation and last-sync dates, and the active switch. Toggling
  it asks for confirmation first and spells out the consequence: deactivating deletes the person's
  APIM subscription, hard-stops their allocation and rejects their pending requests, so calls through
  the gateway stop immediately; activating runs the provision pipeline again and mints a fresh key,
  which only they can reveal.
- **Quota.** Where this month's budget came from (the resolved level), the tier, and usage. A budget
  whose stored number matches no configured tier says so, because the gateway is enforcing the next
  tier up. The editor below is a tier picker plus an unlimited switch — never a free-form number —
  and saving re-scopes the APIM subscription to the new tier product without changing the key.
- **Groups.** Which groups feed this person's budget, each linked. Memberships are **edited on the
  group, not here**: a roster is what an Entra sync owns.
- **Keys.** The masked key, and provision / rotate / revoke, each behind a confirmation. Provision and
  rotate show the plaintext exactly once — nothing stores it, so a lost key means another rotation.
  Revoking takes the key away and leaves the account active; offboarding is the Overview switch.

Every mutation re-reads the user afterwards, so nothing on screen is left stale.

### `/users/sync`

One button runs [`POST /users/sync`](/foundry-gate/reference/api/#users) and reports what it did.
Two of the counts need more than a number, and the page gives them one:

- **Group assignments that could not be expanded.** Access granted through an Entra *group* is
  expanded to that group's transitive members, so assigning developers through a group is a
  first-class setup. A non-zero count is the groups whose expansion **failed** — Graph refused the
  read (typically a missing `GroupMember.ReadBasic.All` on the API's managed identity), the group
  has been deleted, or Graph was briefly unreachable. The run therefore saw only part of the
  population, so "not in the list" cannot mean "has left" and departure detection is suspended for
  it; people are still added and updated. The page says which fix applies — grant the role, or
  unassign the deleted group — rather than letting a `deactivatedCount: 0` read as "nobody left".
  On a healthy tenant the count is always zero
  ([#120](https://github.com/kolatts/foundry-gate/issues/120) is the live-tenant checklist,
  [#183](https://github.com/kolatts/foundry-gate/issues/183) the Graph paging caveat).
- **Departures failed.** The gateway refused to delete somebody's subscription, so that person still
  holds a working key. That is an error, not a footnote; deprovisioning is idempotent, so the fix is
  to run the sync again.

The page shows only the run you just triggered. Foundry Gate keeps no durable last-sync metadata yet
([#171](https://github.com/kolatts/foundry-gate/issues/171)) — every run does write an audit row, so
`/audit` has the history meanwhile.

### `/groups`, `/groups/new` and `/groups/{id}`

`/groups` grids `GET /groups` with each group's member count, budget tier and whether its roster is
managed by Entra or by hand. `/groups/new` creates one; the Entra group id is optional, validated as
a GUID before it is sent, and **cannot be changed afterwards**.

`/groups/{id}` is the policy editor beside the roster. On an **Entra-linked group the add and remove
controls are not shown at all**, and the page explains why: the API refuses membership edits there
with a `409`, because the next sync would undo the change. Change who is in the group in Entra, then
press "Sync from Entra" — the result dialog reports members added, removed, and skipped because they
have no Foundry Gate account yet, with a link onward to `/users/sync` for that last group.

Deleting a group that still has members needs `?force=true`; the confirmation says what that costs
(members keep their accounts and keys, but lose this group's budget) before sending it.

### `/requests` and `/requests/{id}`

One page, two audiences. An admin gets every request with a status filter; a developer gets their
own, read-only — the API already scopes the list, so the page does not have to. Clicking a row opens
an end-anchored drawer holding the requester, the current-to-requested tier change, the
justification, a review notes field and the two verdict buttons. A request that is already decided
opens in the same drawer, read-only, showing the verdict and notes.

A verdict shows on the row immediately rather than after a re-fetch. `?status=Pending` — the link the
dashboard's pending badge uses — arrives as a filter, and `/requests/{id}` renders the same panel as
a page, so there is one description of a request behind two routes.

### `/foundry`

`GET /foundry/deployments` across every configured account, with capacity read as thousands of tokens
per minute and a colour-coded provisioning-state chip. ARM provisions asynchronously, so a new
deployment lands as `Creating` and there is a refresh button rather than a promise that the grid is
live.

Creating is **OpenAI-format only**, and the dialog says why instead of letting the API's `400` be the
first mention of it: Claude deployments need an attestation block the ARM SDK cannot send, and Bicep
can only recreate a whole account's deployments at once, so infra owns them end to end
([#126](https://github.com/kolatts/foundry-gate/issues/126) would lift that). For the same reason,
delete is disabled outright on Anthropic rows. The model and SKU suggestions accept anything typed —
they are a shortcut, not a catalogue
([#173](https://github.com/kolatts/foundry-gate/issues/173) replaces them with what an account can
actually serve).

A new deployment reaches developers through the gateway only once it is in the right backend pool
([#83](https://github.com/kolatts/foundry-gate/issues/83)), which the page also says.

### `/config`

`GET /config` lists every key with its value, when it last changed and who changed it. Edits are
staged locally with per-row dirty tracking; "Save changes" opens a before/after diff you must
confirm, and only then does each dirty key get its own `PUT /config/{key}`. A value the API refuses
stops that row alone and reports next to its own field, so one bad entry never loses the rest — and
a rejected value stays in its box to be corrected rather than being retyped. The editor keeps no
list of its own of which keys are retired: the API answers `409` naming the replacement, and that
message is what the admin reads.

### `/audit`

`GET /audit`, paged and filtered on the server: the log is append-only and unbounded, so the page
never holds more than one page of it. Filters are action, target type, an actor's user id
([#191](https://github.com/kolatts/foundry-gate/issues/191) turns that into a name type-ahead), and
a date range read in the reader's own time zone whose end day is included in full. Changing any
filter goes back to page one. The action and target-type choices come from the Domain constants the
audit writers use, so the dropdowns cannot drift from what is actually written. Expanding a row shows its `details` blob, pretty-printed when it is JSON and verbatim when
it is not — a viewer that hid malformed rows would hide exactly the rows worth reading.
