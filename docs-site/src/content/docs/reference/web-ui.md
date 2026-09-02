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

## Pages still being built

These routes exist and are gated, but their feature pages land with the user and group management
wave; until then they render a placeholder.

| Route | Auth | Will do |
|---|---|---|
| `/users` | Admin | The user list, with search and per-user actions |
| `/users/{id}` | Admin | One user: groups, allocation, key, quota editor |
| `/users/sync` | Admin | Run and report the Entra user sync |
| `/groups` | Admin | The group list |
| `/groups/new` | Admin | Create a group |
| `/groups/{id}` | Admin | One group: roster, quota, Entra link |
| `/requests` | Any | The quota increase review queue. **Not admin-only**: a developer sees their own requests here (the nav links it as "My Requests"), an admin sees everyone's — the API scopes the list, not the route |
| `/requests/{id}` | Owner or Admin | One request, with the approve/reject panel for admins |
