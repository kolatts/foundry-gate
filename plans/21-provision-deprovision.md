# Provision and deprovision pipelines

> GitHub: #64  
> Milestone: v0.2 — Core API  
> Labels: epic, backend

## Overview
Provisioning and deprovisioning span multiple epics (#5, #9, #11) but must behave as cohesive, atomic pipelines. This plan is the authoritative reference for both flows — every service and endpoint that touches user or key lifecycle must follow these sequences. The implementation lives in a single `IUserLifecycleService` that orchestrates `IApimKeyService`, `IQuotaResolutionService`, and `IAuditService` so that no controller has to know the full sequence.

---

## Provision pipeline

Three triggers, one sequence:

```
Trigger A — First login (GET /users/me, no existing User row)
Trigger B — Admin explicit provision (POST /keys/{userId}/provision, user exists but has no APIM key)
Trigger C — Admin re-activation (POST /users/{id}/activate, user was deactivated and key was deleted)
```

**Steps (all-or-nothing; roll back on failure):**

```
1. [Trigger A only] Call Graph GET /users/{oid} → fetch DisplayName, Email, EmployeeId
2. [Trigger A only] INSERT User row (IsActive=true, no ApimSubscriptionId yet)
3. Run IQuotaResolutionService.ResolveAsync → upsert QuotaAllocation for current period
4. Call IApimKeyService.ProvisionAsync:
     a. APIM Management: POST /subscriptions (name: foundrygate-{userId}, scope: /products/{productId})
     b. Store ApimSubscriptionId and encrypted ApimSubscriptionKey on User
     c. Push resolved quota limit to APIM cache key quota-{subscriptionId}
5. [Trigger C only] Set User.IsActive = true
6. Write audit log: user.provisioned | user.key-provisioned | user.reactivated (as appropriate)
7. Return UserProfileDto with masked key hint
```

**Failure compensation:**

| Fails at step | Action |
|---|---|
| Step 1–2 (Graph or DB) | Roll the transaction back — no `User` row survives. Graph failure → **502** (`UpstreamDependencyException`; amended from this plan's original 503, which now means only "the feature is not configured on this host") |
| Step 3 (quota resolution) | Roll the transaction back; a missing `SystemConfiguration[DefaultMonthlyTokenQuota]` surfaces as a configuration error |
| Step 4a (APIM create) | Roll the transaction back — the claim on `User.ApimSubscriptionId`, the row itself (Trigger A) and the allocation all disappear because the key service joined this transaction. **502** with a retryable message; **503** when APIM is not configured at all |
| Step 4b–4c (DB write after APIM success) | APIM subscription exists but DB doesn't know — on next provision attempt, detect orphan subscription by querying APIM before creating, reuse if found (both keys regenerated so the orphan's key is dead) |

---

## Deprovision pipeline

Three triggers, one sequence:

```
Trigger A — Admin explicit deactivation (POST /users/{id}/deactivate)
Trigger B — Entra bulk sync detects user absent from tenant (POST /users/sync)
Trigger C — Admin key revocation without deactivation (DELETE /keys/{userId})
```

Triggers A and B are `DeprovisionTrigger.AdminDeactivation` / `EntraDeparture` on
`IUserLifecycleService`. **Trigger C is not a deprovision** (#116 ruling) and has no member on that
enum: it runs step 1 and step 5 only, entirely inside `IApimKeyService.RevokeAsync`. Trigger B is
idempotent (an already-inactive user is a no-op, not a 409) and audits as the system; trigger A is a
409 on an already-inactive user and audits as the calling admin.

**Ordering (amended by the #156 review).** Provision and deprovision are shaped differently on
purpose, because their irreversible step points the other way. Provision holds one transaction across
the APIM create — its residue is a harmless orphan the next provision adopts by name, so rolling the
database back is right. Deprovision cannot: an APIM `DELETE` has no undo, so rolling back after it
would leave a deleted key the database knows nothing about. **Step 1 therefore runs first and outside
any transaction the orchestrator owns**; steps 2-5 then take a transaction of their own, on
`CancellationToken.None`.

| Fails at step | State left behind | Recovery |
|---|---|---|
| Step 1 (APIM delete) | Nothing changed — the subscription is still there and the user still active | `502`; retry |
| Steps 2–5 (the DB half) | Subscription deleted, key fields cleared and `key.revoked` audited (step 1 committed on its own), but the user is still `IsActive = true` | Logged at Error naming the user; re-run `POST /users/{id}/deactivate`. `RevokeAsync` is idempotent on a subscription that is already gone, so the retry completes the remaining steps |
| A departure inside `POST /users/sync` | Only that user is affected — each departure is its own unit of work | Counted in `UserSyncResult.FailedCount`, logged, retried by the next run |

**Steps:**

```
1. Call IApimKeyService.RevokeAsync (no-op when the user has no key):
     a. APIM Management: DELETE /subscriptions/foundrygate-{userId}
     b. Clear User.ApimSubscriptionId / ApimSubscriptionKey / ApimSubscriptionKeyHint / ApimKeyIssuedDate
     c. Audit key.revoked
2. [Triggers A + B only] Set User.IsActive = false
3. Set QuotaAllocation.IsHardStopped = true for the current period
4. Cancel any Pending QuotaIncreaseRequests for this user (set Status = Rejected, ReviewNotes = "User deactivated")
5. Write audit log: user.deactivated | user.key-revoked | sync.user-departed (as appropriate)
```

**Every exit deletes — there is no suspended state (#116):**

| Scenario | APIM action | User.IsActive | Key can be restored? |
|---|---|---|---|
| Quota exhausted | None — the gateway's `llm-token-limit` returns `403` until the month resets (#81); the subscription stays `active` | true | n/a — the key keeps working the moment the window resets |
| Admin deactivation | Delete subscription | false | Only via re-activation (Trigger C of provision) |
| Entra departure | Delete subscription | false | Only if user returns to Entra and admin re-activates |
| Admin key revocation only | Delete subscription | true | Yes — admin calls POST /keys/{userId}/provision |

---

## Foundry model provision pipeline

```
Trigger — Admin creates deployment (POST /foundry/deployments)

1. Call IFoundryDeploymentService.CreateAsync → Azure SDK CreateOrUpdateAsync
2. No APIM changes needed — existing product covers all Foundry routes; new deployment
   is immediately accessible by all active subscription keys
3. Write audit log: foundry.deployment.created
```

```
Trigger — Admin removes deployment (DELETE /foundry/deployments/{name})

1. Warn: any in-flight requests to this deployment will receive 404 from Foundry after deletion
2. Call IFoundryDeploymentService.DeleteAsync → Azure SDK DeleteAsync
3. Write audit log: foundry.deployment.deleted
4. No user key changes needed
```

---

## Implementation

### Wire IUserLifecycleService as the single pipeline orchestrator (#65) — **shipped**
`FoundryGate.Api/Services/Lifecycle/IUserLifecycleService` has exactly two methods:

```csharp
Task<User> ProvisionAsync(ProvisionTrigger trigger, ProvisionContext context, CancellationToken ct);
Task DeprovisionAsync(DeprovisionTrigger trigger, int userId, CancellationToken ct);
```

`ProvisionTrigger` is `FirstLogin | AdminProvision | Reactivate` and `DeprovisionTrigger` is
`AdminDeactivation | EntraDeparture`; re-activation is a provision *trigger*, not a third method,
because it is the same sequence with `IsActive = true` in front of it. `ProvisionContext` carries the
`UserId` (absent only for `FirstLogin`, which creates the row from `ICurrentUserAccessor` claims,
enriched by `IEntraDirectoryClient.GetUserAsync` when `Entra:Enabled`).

**Transaction shape (amended):** the orchestrator opens *one* transaction and the APIM call happens
**inside** its lifetime, not outside it. `IApimKeyService` joins an already-open transaction rather
than starting its own (it checks `Database.CurrentTransaction`), so its provisioning claim and its
save land in the orchestrator's unit of work and an APIM failure rolls the whole thing back — which
is what makes "no orphan `User` row on a failed first login" true rather than aspirational. A caller
that already owns a transaction (`EntraUserSyncService` wraps a whole sync run) is joined the same
way.

**Failure taxonomy:** APIM absent from this host → `FeatureNotConfiguredException` (503); a configured
APIM (or Graph) failing → the new `FoundryGate.Domain.Exceptions.UpstreamDependencyException` (502,
one arm in `GlobalExceptionHandler`); caller-caused refusals keep their own 400/403/404/409.

`KeysController` is deliberately **not** wired to the orchestrator: `POST /keys/{userId}/provision`
and `DELETE /keys/{userId}` are key-only operations per #116 and stay on `IApimKeyService`.

Files created or modified:
- `src/FoundryGate.Api/Services/Lifecycle/{IUserLifecycleService,UserLifecycleService,LifecycleTriggers,LifecycleServiceCollectionExtensions}.cs`
- `src/FoundryGate.Api/Services/Users/{IUserService,UserService,UsersServiceCollectionExtensions}.cs`
- `src/FoundryGate.Api/Controllers/UsersController.cs`
- `src/FoundryGate.Api/Services/Entra/EntraUserSyncService.cs` (departure path + one transaction per run)
- `src/FoundryGate.Domain/Exceptions/UpstreamDependencyException.cs`, `src/FoundryGate.Api/Middleware/GlobalExceptionHandler.cs`
- `src/FoundryGate.Api/Services/Keys/IApimKeyService.cs` (unchanged — it already provided the building blocks: `ProvisionAsync`, `RotateAsync`, `RevokeAsync`, `RevokeAsSystemAsync`, `MoveToProductAsync`; no suspend/re-enable surface exists, #116)

### Add re-activation endpoint and orphan subscription detection (#66) — **shipped**
`POST /users/{id}/activate` calls `ProvisionAsync(Reactivate, ForUser(id))`, which sets
`IsActive = true` and runs the whole pipeline. Orphan detection already lives in
`ApimKeyService.ProvisionAsync` (a Management-plane `GET` before the create; an existing subscription
is adopted, re-scoped to the resolved tier and has **both** keys regenerated so the orphan's key is
dead; a non-`active` orphan is deleted and recreated), so re-activation gets it for free — the
orchestrator only has to not create a second one. A user whose subscription was never deleted keeps
their existing key: re-minting one would break a CLI that is already configured.

The response is a `UserResponse`, **not** the plaintext key: the developer reads their own key with
`POST /keys/me/reveal`, so no admin response ever carries someone else's key material.

`DELETE /keys/{userId}` stays key-only (#116): APIM delete + `key.revoked` audit, `IsActive`
untouched, quota untouched, pending requests untouched. `DeprovisionTrigger` deliberately has no
member for it.

Files created or modified:
- `src/FoundryGate.Api/Services/Lifecycle/UserLifecycleService.cs`
- `src/FoundryGate.Api/Services/Keys/ApimKeyService.cs` (unchanged — orphan detection landed with #36)

## Verification
- [x] First login creates User row, QuotaAllocation, and APIM subscription atomically (`UserLifecycleServiceTests`, `UsersEndpointTests`)
- [x] APIM provisioning failure on first login leaves no orphan User row — the key service joins the orchestrator's transaction, so the claim, the row and the allocation all roll back; the caller gets `502`
- [x] APIM unconfigured on the host is `503` (`FeatureNotConfiguredException`), distinct from a configured gateway failing
- [x] Subsequent first-login calls are idempotent (no duplicate User, allocation, subscription or audit row)
- [x] `POST /users/{id}/deactivate` deletes the APIM subscription, clears the key fields, hard-stops the allocation and rejects pending requests
- [x] Entra bulk sync departure detection calls full deprovision (APIM subscription deleted), with system-attributed audit rows, one unit of work per departed user so a single gateway failure cannot undo the others (`FailedCount`)
- [x] Everything after an accepted external change runs on `CancellationToken.None` — a client that disconnects the instant APIM returns still gets a fully recorded, fully audited result (probed in `UserLifecycleServiceTests`)
- [x] A quota change whose audit row cannot be written is not committed, and leaves no orphan `key.tier-changed` row (`MoveToProductAsync` adds its row without saving; the caller owns the save)
- [x] `POST /users/{id}/activate` re-provisions a new APIM key and updates DB; already-active is `409`
- [x] Orphan APIM subscription detected and reused on re-activation (not duplicated), re-scoped to the resolved tier with both keys regenerated
- [x] Quota exhaustion never touches the subscription at all (#116/#81 superseded the "suspends" row above): it is a real-time gateway `403` that clears on the monthly window
- [x] Cancelling a Pending request on deactivation sets Status = Rejected with the system note "User deactivated" and no reviewer
- [x] `DELETE /keys/{userId}` stays key-only — it does not run the deprovision pipeline (`KeysEndpointTests`)
- [ ] Live: deactivate/activate against a real APIM and confirm the subscription in the portal (manual checklist in #132)
