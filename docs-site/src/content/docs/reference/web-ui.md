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
| `/dashboard` | Admin | Fork-wide stats, who is cut off, and the busiest consumers this period |
| `/quota` | Admin | This period's allocations, filterable by hard-stopped, over budget, tier, name and account status |
| `/users` | Admin | Searchable, server-paged list of everyone |
| `/users/{id}` | Admin | One person: fields, budget, groups and gateway key |
| `/users/sync` | Admin | Reconcile the user list against Entra |
| `/groups` | Admin | Groups with member count, budget and roster source |
| `/groups/new` | Admin | Create a group, optionally linked to an Entra group |
| `/groups/{id}` | Admin | A group's policy and roster |
| `/requests` | Any | The quota increase review queue. **Not admin-only**: a developer sees their own requests here (the nav links it as "My Requests"), an admin sees everyone's — the API scopes the list, not the route |
| `/requests/{id}` | Owner or Admin | One request, with the approve/reject panel for admins |
| `/foundry` | Admin | Foundry model deployments |
| `/models` | Admin | Which models each quota tier is allowed to use — the gateway's own allowlist |
| `/config` | Admin | Edit the `SystemConfiguration` key-value rows (the `RateCard` gets a multi-line box — it is JSON) |
| `/audit` | Admin | Browse and filter the audit trail |

### `/dashboard`

`GET /dashboard` fills four stat cards — total users, active users (with the unlimited count),
pending quota increase requests, and **hard-stopped** users — plus a top-ten consumers grid with a
per-row usage bar. The pending count is a badge linking to the filtered review queue, and the same
count badges the "All Requests" link in the nav. The page re-reads the summary every 60 seconds and
stops the moment you navigate away; a failed background refresh leaves the last good numbers on
screen rather than replacing them with an error.

The hard-stopped card counts active users whose current allocation is hard-stopped — someone whose
key was taken away while their account is still live, which is an outage for that developer — so a
non-zero count is rendered as an alert and links to `/quota?isHardStopped=true&isActive=true`: the
people it counted, filtered exactly the way the count was computed. Quota exhaustion never sets that
flag: the gateway 403s an over-budget request itself. **Who has run out of tokens** is the separate
`overBudgetUserCount`, shown with the tokens reconciled this period as a caption above the consumers
grid — both are usage figures, so they sit next to the list they describe rather than in an
enforcement-looking card — and it links to `/quota?isOverBudget=true&isActive=true` the same way.

Once the fork has priced its tokens (the `RateCard` configuration key), the caption also carries an
**estimated cost** for the period and the consumers grid gains an "Est. cost" column. Both are
hover-labelled as estimates and neither appears at all until a rate card exists — a zero would read as
"free". See [Cost estimates](/foundry-gate/reference/configuration/#cost-estimates-ratecard).

Every figure here is a reconciliation number from the Log Analytics sync, refreshed on that job's
cadence — not a live view of gateway enforcement.

### `/quota`

The list behind the dashboard's counts. A server-paged grid of this period's allocations —
developer, tier, tokens used, budget, and the same usage bar `/me` shows — with a **Hard-stopped**,
**Over budget** and **Active accounts** chip, a tier picker and a name/email search. A row opens the
user.

The filters are the API's filters (`GET /quota/allocations`), and they live in the URL: turning one
on rewrites the query string, so the page you are looking at is the page you can send someone, and
the dashboard's cards link straight in with the filter already applied. A chip that is off means "no
opinion", not "only the ones that are not" — turning **Hard-stopped** off shows everyone again
rather than hiding the rows you came for.

An "Est. cost" column appears here too, on the same terms: only once a `RateCard` is configured, and
labelled an estimate wherever it shows.

Rows exist only for developers who have been resolved this period: their first visit of the month,
or the last `POST /quota/reset`. A budget that matches no configured tier carries a warning icon —
the gateway is enforcing it at the next tier up, and correcting it is a `/users/{id}` edit away.

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

The page also shows the **previous** run — when it happened and what it did — on a cold load, from
`GET /users/sync/last`, which reads the `LastUserSyncDate` / `LastUserSyncResult` rows the sync writes
in its own unit of work. So a run triggered from anywhere (another admin, another browser, a script)
shows up here, and finishing a run re-reads the record rather than assuming it. A fork that has never
synced says so instead of showing a blank, and a read that *failed* says that instead — "couldn't
read the last run" and "there was no last run" are different facts, and only one of them is something
the page learned. Only the most recent run is kept; `/audit` filtered to `users.synced` has the full
history, which the note links to.

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
delete is disabled outright on Anthropic rows.

The model, version and SKU pickers are filled from
[`GET /foundry/catalog`](/foundry-gate/reference/api/#get-foundrycatalog) — what the configured
accounts can actually serve — rather than from a list typed into the dialog, which went stale the
week after it shipped. The catalogue is filtered to **OpenAI-format** models here: the endpoint lists
Claude models too, but this form submits `modelFormat: OpenAI`, so offering one would send an
Anthropic create disguised as an OpenAI one, straight past the API's refusal. Retired models
(`Deprecated`, or past their retirement date) are hidden behind a toggle — deploying one is
legitimate, but it should be deliberate.

Picking a catalogued model fills in **ARM's default version**, **ARM's default SKU** and the capacity
ARM suggests for that SKU, so the common case is one pick rather than four. Those three are *derived*
values: change the model and they are re-derived, because the previous model's version and SKU are
not an answer to the new question. A field you typed yourself is yours and is left alone. Every field
still coerces whatever is typed, so a model Azure lists before this endpoint does is still
deployable — ARM decides either way. If the catalogue can't be read, the dialog says so and the
fields fall back to plain free text rather than to another hardcoded list.

The dialog creates one deployment per account and can name **several accounts** — the chosen one,
plus anything picked under "Also create in". That is one `POST /foundry/deployments` per account, run
in order and reported separately, so a second region failing never reads as the first one having
failed too. Which accounts a model belongs in is not a preference: a model served through the
multi-region Anthropic pool must exist in every region, because the pool sends a throttled request to
another one. OpenAI-format models are primary-account-only, so nothing extra is pre-selected today.

After a create the page **polls each new deployment** until ARM stops moving it (`Succeeded`,
`Failed` or `Canceled`), refreshing the grid as it goes, so you find out whether the thing you asked
for works without pressing refresh. It gives up after about a minute; the state chips are still the
truth at that point, they just stop updating themselves.

A new deployment reaches developers through the gateway only once it is in the right backend pool
([#83](https://github.com/kolatts/foundry-gate/issues/83)), which the page also says — and only once
a tier is allowed to use it. So a successful create ends with a link into `/models` carrying the new
deployment's name, which that page pre-fills into its "allow a model" dialog.

### `/models`

**Models &amp; access** — which models each quota tier is allowed to use, backed by
[`GET`/`PUT /gateway/tiers/{tier}/models`](/foundry-gate/reference/api/#gateway--model-allowlist).
This list *is* the rule the gateway enforces: a developer asking for a model their tier does not list
is refused `403 model_not_permitted` at the gateway, before it costs them any quota. Changes take
effect without redeploying anything.

The page has three parts:

- **At a glance** — a matrix of every alias any tier permits against every tier, so "who can use
  `opus`?" is one look rather than three page visits. A tier that does not permit an alias shows
  `—`.
- **One panel per tier** — the tier's rows (alias, deployment, front door, backend) with *allow*,
  *retarget* and *remove*. Each of those sends the tier's whole map, because the gateway reads one
  JSON document; the page composes the new list from what is already there. Remove asks first and
  says what happens: developers who have that model configured start being refused, and nothing is
  deleted in Azure.
- **What developers on this tier see** — the aliases as they would appear in a developer's
  "Configure your CLI" panel. A tier with no models says so plainly: nothing is available to anyone
  on it.

Two fields the dialog **derives rather than asks**: the front door (`provider`) and the backend
(`pool`) both follow from the chosen deployment's ARM `model.format`. Getting either wrong produces a
failure that looks like something else — a Claude alias routed at the OpenAI backend dies as an
opaque 404 — so the form asks the question once, in the deployment picker. Rows whose deployment is
missing entirely, or missing from a region a pooled alias needs, are flagged in the grid rather than
hidden; the API refuses to *write* such a map, so a flagged row is one a deploy or a later deletion
left behind.

### `/config`

`GET /config` lists every key with its value, when it last changed and who changed it. Edits are
staged locally with per-row dirty tracking; "Save changes" opens a before/after diff you must
confirm, and only then does each dirty key get its own `PUT /config/{key}`. A value the API refuses
stops that row alone and reports next to its own field, so one bad entry never loses the rest — and
a rejected value stays in its box to be corrected rather than being retyped.

**The editor keeps no list of its own of which keys it may not write.** System-managed rows
(`LastUserSyncDate`, `LastUserSyncResult`) arrive flagged `isReadOnly`, so their field is disabled and
labelled "System-managed" without a round trip; every other refusal is reported in the API's own words
next to the field it belongs to. One map in `FoundryGate.Domain.Constants` decides read-only-ness, and
both the API's `409` and this flag read it, so the two can no longer drift
([#172](https://github.com/kolatts/foundry-gate/issues/172) — they had, by one entry).

### `/audit`

`GET /audit`, paged and filtered on the server: the log is append-only and unbounded, so the page
never holds more than one page of it. Filters are action, target type, an **actor type-ahead**, and
a date range read in the reader's own time zone whose end day is included in full. Changing any
filter goes back to page one. The action and target-type choices come from the Domain constants the
audit writers use, so the dropdowns cannot drift from what is actually written.

The actor filter searches [`GET /users?search=`](/foundry-gate/reference/api/#users) as you type
(debounced, active and deactivated alike — a departed employee's entries are still in the log) and
resolves the pick to `actorUserId`. An admin reading the log knows "Ada Lovelace", not "user 41".
Typing a bare **id** still works: type it and tab out, and the id is resolved when the field loses
focus rather than while you type — `GET /users/{id}` is the detail endpoint, far too heavy to fire at
every pause in typing "412" — and an id that matches nobody leaves the filter alone rather than
emptying the grid over a typo. Both query parameters are deep links applied once, when the page
loads: `?actor=41` applies the id and shows whose it is (and still filters if the name cannot be
resolved), and `?action=` filters to one kind of entry, which is how the dashboard's hard-stopped
card hands over the relevant trail rather than the whole log. An action the constants do not name is
ignored. Neither is re-applied afterwards — nothing rewrites the URL when a filter changes, so
re-applying would quietly restore the value you just cleared. Expanding a row shows its `details` blob, pretty-printed when it is JSON and verbatim when
it is not — a viewer that hid malformed rows would hide exactly the rows worth reading.
