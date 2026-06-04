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
| Step 1–2 (Graph or DB) | Return 503; no cleanup needed |
| Step 3 (quota resolution) | Delete the User row if just created (Trigger A); return 500 |
| Step 4a (APIM create) | Set `User.ApimSubscriptionId = null`; return 500 with retryable error |
| Step 4b–4c (DB write after APIM success) | APIM subscription exists but DB doesn't know — on next provision attempt, detect orphan subscription by querying APIM before creating, reuse if found |

---

## Deprovision pipeline

Three triggers, one sequence:

```
Trigger A — Admin explicit deactivation (POST /users/{id}/deactivate)
Trigger B — Entra bulk sync detects user absent from tenant (POST /users/sync)
Trigger C — Admin key revocation without deactivation (DELETE /keys/{userId})
```

**Steps:**

```
1. Call IApimKeyService.DeleteSubscriptionAsync:
     a. APIM Management: DELETE /subscriptions/{apimSubscriptionId}
     b. Set User.ApimSubscriptionId = null, User.ApimSubscriptionKey = null
2. [Triggers A + B only] Set User.IsActive = false
3. Set QuotaAllocation.IsHardStopped = true for the current period
4. Cancel any Pending QuotaIncreaseRequests for this user (set Status = Rejected, ReviewNotes = "User deactivated")
5. Write audit log: user.deactivated | user.key-revoked | sync.user-departed (as appropriate)
```

**Key distinction — suspend vs. delete:**

| Scenario | APIM action | User.IsActive | Key can be restored? |
|---|---|---|---|
| Quota exhausted (usage sync) | Suspend subscription | true | Yes — monthly reset re-enables |
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

### Wire IUserLifecycleService as the single pipeline orchestrator (#65)
Create `IUserLifecycleService` with methods `ProvisionAsync(trigger, userId?, entroClaims?)`, `DeprovisionAsync(trigger, userId)`, and `ReactivateAsync(userId)`. Inject it into `UsersController`, `EntraUserSyncService`, and `KeysController` so that all lifecycle triggers call this single service rather than duplicating the sequence. The service uses a DB transaction wrapping steps 2–5 (the DB portions) and calls APIM outside the transaction — on APIM failure it rolls back the transaction and returns a structured error.

Ensure `EntraUserSyncService` (epic #11, sub-issue #40) calls `DeprovisionAsync(trigger: EntrapDeparture, userId)` for each user found in DB but absent from Entra — currently it only sets `IsActive = false` and misses APIM cleanup.

Files expected to be created or modified:
- `src/FoundryGate.Api/Services/IUserLifecycleService.cs`
- `src/FoundryGate.Api/Services/UserLifecycleService.cs`
- `src/FoundryGate.Api/Controllers/UsersController.cs` (deactivate/activate wired to lifecycle service)
- `src/FoundryGate.Api/Services/EntraUserSyncService.cs` (departure path wired to lifecycle service)
- `src/FoundryGate.Api/Controllers/KeysController.cs` (provision/revoke wired to lifecycle service)
- `src/FoundryGate.Api/Services/IApimKeyService.cs` (add SuspendAsync, ReenableAsync, DeleteAsync)

### Add re-activation endpoint and orphan subscription detection (#66)
`POST /users/{id}/activate` currently just sets `User.IsActive = true`. It must call `IUserLifecycleService.ReactivateAsync` which runs the full provision pipeline (Trigger C). Before calling APIM to create a new subscription, check if a subscription named `foundrygate-{userId}` already exists on the APIM Management plane — if it does (orphan from a failed previous deprovision), reuse it rather than creating a duplicate. `DELETE /keys/{userId}` (admin key revocation without deactivation) leaves `User.IsActive = true` and must not call the full deprovision pipeline — only step 1 (APIM delete) and step 5 (audit log).

Files expected to be created or modified:
- `src/FoundryGate.Api/Services/UserLifecycleService.cs`
- `src/FoundryGate.Api/Services/ApimKeyService.cs` (orphan detection via APIM Management GET before POST)

## Verification
- [ ] First login creates User row, QuotaAllocation, and APIM subscription atomically
- [ ] APIM provisioning failure on first login leaves no orphan User row
- [ ] Subsequent first-login calls are idempotent (no duplicate User or APIM subscription)
- [ ] `POST /users/{id}/deactivate` deletes the APIM subscription (verified in Azure portal)
- [ ] Entra bulk sync departure detection calls full deprovision (APIM subscription deleted)
- [ ] `POST /users/{id}/activate` re-provisions a new APIM key and updates DB
- [ ] Orphan APIM subscription detected and reused on re-activation (not duplicated)
- [ ] Quota exhaustion suspends (not deletes) the subscription; monthly reset re-enables it
- [ ] Cancelling a Pending request on deactivation sets Status = Rejected with system note
