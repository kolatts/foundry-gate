<#
.SYNOPSIS
    The enforcement matrix: auth, model allowlist, TPM 429 and monthly-quota 403 against the live gateway.

.DESCRIPTION
    Stage 3 of the cycle. Every check prints PASS/FAIL/SKIP, is recorded in the state file
    for the evidence report, and the script exits non-zero if anything FAILed.

    The checks map onto the validation matrix in fable-refactor-log.md:

      T3   no key / bad key -> 401 on both front doors
      T2   OpenAI chat completions and responses -> 200 with `api-key`
      T1   Anthropic messages -> 200 with `x-api-key` + `anthropic-version`
           (SKIP, not FAIL, when no Claude deployment reached Succeeded — E-007)
      A1   unknown alias -> 403 model_not_permitted
      A2   the real DEPLOYMENT name used as an alias -> 403 (the alias map is the allowlist)
      A3   a Claude alias sent to the OpenAI front door -> 403 naming the right base path
      T4   TPM: hammer dev-alice to 429 + Retry-After + x-fg-remaining-tpm, while dev-bob
           (same tier, different subscription) still gets 200 — counter isolation
      T5   monthly quota: burn dev-alice's budget to a native 403 with x-fg-remaining-quota
           at 0, while dev-carol (power tier) still gets 200

    WHO SPENDS WHAT. dev-alice is the only subscription this script exhausts. The happy-path
    200s run as dev-carol (power tier, big budget) and dev-bob is touched exactly once, with
    a 16-token request, as the TPM isolation control — because codex-test.ps1 needs bob's
    monthly budget intact to drive a real harness into the same two walls.

.EXAMPLE
    pwsh scripts/cycle/smoke.ps1
#>
[CmdletBinding()]
param(
    [string] $Environment = 'test',
    [Parameter(HelpMessage = 'Wall-clock cap for the monthly-quota burn. TPM throttling makes it take at least MonthlyQuota/TPM minutes.')]
    [int] $QuotaBurnTimeoutMinutes = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/_common.ps1"

$state = Get-CycleState -Environment $Environment -Required
$anthropicUrl = $state.outputs.anthropicApiUrl
$openaiUrl = $state.outputs.openaiApiUrl
$claudeAvailable = [bool]$state.claudeAvailable

$alice = $state.apimSubscriptions['dev-alice'].primaryKey
$bob = $state.apimSubscriptions['dev-bob'].primaryKey
$carol = $state.apimSubscriptions['dev-carol'].primaryKey

$standardTier = @($state.quotaTiers | Where-Object { $_.name -eq 'standard' })[0]
$failures = 0

# Is the monthly wall reachable at all on this environment, in the time this stage is allowed?
#
# The cycle's own `test` gateway is deployed with a deliberately tiny standard tier (40K
# tokens behind a 12K/min cap) precisely so T5 can happen — ~3 minutes of burning. A SHARED
# environment's tiers are the shipped product defaults, and dev's standard tier is 5,000,000
# tokens behind 20,000 TPM: 250 minutes of continuous full-rate traffic, several dollars of
# real tokens, and a monthly counter left exhausted for every developer sharing that tier
# until the month rolls over — because APIM offers no way to reset it.
#
# So T5 is SKIPPED BY DESIGN there rather than failed or fudged. It is not skipped because
# anything is broken and it is not skipped because it is slow: it is arithmetic, stated as
# arithmetic, and the enforcement it would prove is the same policy element T4 exercises on
# the same tier. Editing dev's policy to shrink the tier would test a gateway nobody runs.
$quotaBurnMinutes = Get-CycleQuotaBurnMinutes -Tier $standardTier
$quotaReachable = $quotaBurnMinutes -le $QuotaBurnTimeoutMinutes

function New-OpenAIHeaders { param([string] $Key) if ($Key) { @{ 'api-key' = $Key } } else { @{} } }
function New-AnthropicHeaders {
    param([string] $Key)
    $h = @{ 'anthropic-version' = '2023-06-01' }
    if ($Key) { $h['x-api-key'] = $Key }
    return $h
}

function New-ChatBody {
    param([string] $Model = 'gpt', [string] $Prompt = 'Reply with the single word: pong', [int] $MaxTokens = 16)
    @{ model = $Model; max_tokens = $MaxTokens; messages = @(@{ role = 'user'; content = $Prompt }) } | ConvertTo-Json -Depth 6 -Compress
}
function New-ResponsesBody {
    param([string] $Model = 'gpt', [string] $Prompt = 'Reply with the single word: pong', [int] $MaxTokens = 16)
    @{ model = $Model; input = $Prompt; max_output_tokens = $MaxTokens } | ConvertTo-Json -Depth 6 -Compress
}
function New-MessagesBody {
    param([string] $Model = 'haiku', [string] $Prompt = 'Reply with the single word: pong', [int] $MaxTokens = 16)
    @{ model = $Model; max_tokens = $MaxTokens; messages = @(@{ role = 'user'; content = $Prompt }) } | ConvertTo-Json -Depth 6 -Compress
}

function Assert-Check {
    param([string] $Id, [string] $Name, [bool] $Condition, [string] $Detail)
    Add-CycleCheck -State $state -Id $Id -Name $Name -Status ($Condition ? 'PASS' : 'FAIL') -Detail $Detail
    if (-not $Condition) { $script:failures++ }
}

# ---- T3: subscription enforcement -------------------------------------------------
Write-CycleHeading 'T3 — key enforcement (401 on both front doors)'
foreach ($case in @(
        @{ Id = 'T3a'; Name = 'OpenAI front door, no key'; Uri = "$openaiUrl/chat/completions"; Headers = (New-OpenAIHeaders '') ; Body = (New-ChatBody) }
        @{ Id = 'T3b'; Name = 'OpenAI front door, bad key'; Uri = "$openaiUrl/chat/completions"; Headers = (New-OpenAIHeaders 'not-a-real-key'); Body = (New-ChatBody) }
        @{ Id = 'T3c'; Name = 'Anthropic front door, no key'; Uri = "$anthropicUrl/v1/messages"; Headers = (New-AnthropicHeaders ''); Body = (New-MessagesBody) }
        @{ Id = 'T3d'; Name = 'Anthropic front door, bad key'; Uri = "$anthropicUrl/v1/messages"; Headers = (New-AnthropicHeaders 'not-a-real-key'); Body = (New-MessagesBody) }
    )) {
    $r = Invoke-GatewayRequest -Uri $case.Uri -Headers $case.Headers -Body $case.Body
    Assert-Check -Id $case.Id -Name $case.Name -Condition ($r.StatusCode -eq 401) -Detail "HTTP $($r.StatusCode) — $(Format-BodyExcerpt $r.Body 120)"
}

# ---- T2: OpenAI happy path --------------------------------------------------------
Write-CycleHeading 'T2 — OpenAI front door'
$r = Invoke-GatewayRequest -Uri "$openaiUrl/chat/completions" -Headers (New-OpenAIHeaders $carol) -Body (New-ChatBody)
Assert-Check -Id 'T2a' -Name 'POST /openai/v1/chat/completions with api-key -> 200' -Condition ($r.StatusCode -eq 200) `
    -Detail "HTTP $($r.StatusCode), x-fg-remaining-tpm=$(Get-ResponseHeader $r 'x-fg-remaining-tpm'), x-fg-tokens-consumed=$(Get-ResponseHeader $r 'x-fg-tokens-consumed')"

$r = Invoke-GatewayRequest -Uri "$openaiUrl/responses" -Headers (New-OpenAIHeaders $carol) -Body (New-ResponsesBody)
Assert-Check -Id 'T2b' -Name 'POST /openai/v1/responses with api-key -> 200' -Condition ($r.StatusCode -eq 200) `
    -Detail "HTTP $($r.StatusCode), x-fg-remaining-tpm=$(Get-ResponseHeader $r 'x-fg-remaining-tpm')"

# ---- T1: Anthropic happy path -----------------------------------------------------
Write-CycleHeading 'T1 — Anthropic front door'
if (-not $claudeAvailable) {
    Add-CycleCheck -State $state -Id 'T1' -Name 'POST /anthropic/v1/messages with x-api-key -> 200' -Status 'SKIP' `
        -Detail 'No Anthropic deployment reached Succeeded on this cycle (E-007). Not a gateway failure — the front door, key mapping and policy chain are exercised by T3c/T3d and A3.'
}
else {
    $r = Invoke-GatewayRequest -Uri "$anthropicUrl/v1/messages" -Headers (New-AnthropicHeaders $carol) -Body (New-MessagesBody)
    Assert-Check -Id 'T1' -Name 'POST /anthropic/v1/messages with x-api-key -> 200' -Condition ($r.StatusCode -eq 200) `
        -Detail "HTTP $($r.StatusCode), x-fg-remaining-tpm=$(Get-ResponseHeader $r 'x-fg-remaining-tpm') — $(Format-BodyExcerpt $r.Body 160)"
}

# ---- Alias allowlist --------------------------------------------------------------
# These cost nothing: the alias fragment refuses before llm-token-limit runs, which is
# itself part of the design (a blocked model must not burn the developer's quota).
Write-CycleHeading 'A — model alias allowlist (#86)'
$r = Invoke-GatewayRequest -Uri "$openaiUrl/chat/completions" -Headers (New-OpenAIHeaders $carol) -Body (New-ChatBody -Model 'no-such-alias')
Assert-Check -Id 'A1' -Name 'Unknown alias -> 403 model_not_permitted' `
    -Condition ($r.StatusCode -eq 403 -and (Get-ResponseHeader $r 'x-fg-error') -eq 'model_not_permitted') `
    -Detail "HTTP $($r.StatusCode), x-fg-error=$(Get-ResponseHeader $r 'x-fg-error') — $(Format-BodyExcerpt $r.Body 160)"

$r = Invoke-GatewayRequest -Uri "$openaiUrl/chat/completions" -Headers (New-OpenAIHeaders $carol) -Body (New-ChatBody -Model 'gpt-4-1-mini')
Assert-Check -Id 'A2' -Name 'Real deployment name used as a model -> 403 model_not_permitted' `
    -Condition ($r.StatusCode -eq 403 -and (Get-ResponseHeader $r 'x-fg-error') -eq 'model_not_permitted') `
    -Detail "HTTP $($r.StatusCode), x-fg-error=$(Get-ResponseHeader $r 'x-fg-error') — $(Format-BodyExcerpt $r.Body 160)"

$r = Invoke-GatewayRequest -Uri "$openaiUrl/chat/completions" -Headers (New-OpenAIHeaders $carol) -Body (New-ChatBody -Model 'sonnet')
Assert-Check -Id 'A3' -Name 'Claude alias on the OpenAI front door -> 403 naming the Anthropic path' `
    -Condition ($r.StatusCode -eq 403 -and $r.Body -match 'anthropic') `
    -Detail "HTTP $($r.StatusCode), x-fg-error=$(Get-ResponseHeader $r 'x-fg-error') — $(Format-BodyExcerpt $r.Body 200)"

# ---- T4: TPM cap and counter isolation --------------------------------------------
Write-CycleHeading "T4 — TPM cap ($($standardTier.tpm)/min) and per-subscription counter isolation"
# The burn request has to be BIG. `llm-token-limit` is a token bucket that refills
# continuously at tokens-per-minute — 8000/min is ~133 tokens/second — so a request that
# consumes ~600 tokens and takes ~5 seconds to answer refills almost exactly what it spent,
# and the remaining-tpm header hovers instead of draining. Verified live: 7 consecutive
# ~600-token requests left remaining-tpm oscillating around 7300.
# A few thousand prompt tokens plus a large max_tokens outruns the refill, so the bucket
# empties in two or three requests and the 429 is reached deterministically.
#
# Scaled to the tier, not fixed. A body sized for a 12,000 TPM tier is a rounding error
# against dev's 20,000 and would leave the bucket hovering for thirty attempts before the
# stage gave up and reported a working meter as broken. The sentence is ~10 tokens, and
# ~35% of a minute's budget per request drains a full bucket in three or four requests on
# any tier while staying well inside the model's context window.
$bulkSentence = 'The quick brown fox jumps over the lazy dog. '
$bulkRepeats = [math]::Max(200, [math]::Min(3000, [int]($standardTier.tpm * 0.35 / 10)))
$bulk = ($bulkSentence * $bulkRepeats)
Write-CycleInfo "burn request ~$($bulkRepeats * 10) prompt tokens + 1500 max_tokens against a $($standardTier.tpm) TPM cap"
$burnBody = New-ChatBody -MaxTokens 1500 -Prompt @"
Summarise the following text in exactly 500 words, then write a 500-word critique of its style.

$bulk
"@
$tpm429 = $null
$tpmAttempts = 0
$lastRemainingTpm = ''
while ($tpmAttempts -lt 30 -and $null -eq $tpm429) {
    $tpmAttempts++
    $r = Invoke-GatewayRequest -Uri "$openaiUrl/chat/completions" -Headers (New-OpenAIHeaders $alice) -Body $burnBody
    $remaining = Get-ResponseHeader $r 'x-fg-remaining-tpm'
    if ($remaining) { $lastRemainingTpm = $remaining }
    Write-CycleInfo "attempt $tpmAttempts -> HTTP $($r.StatusCode), remaining-tpm=$remaining"
    if ($r.StatusCode -eq 429) { $tpm429 = $r }
    elseif ($r.StatusCode -eq 403) { break }  # already out of monthly budget — T5 will assert it
}

if ($null -ne $tpm429) {
    $retryAfter = Get-ResponseHeader $tpm429 'Retry-After'

    # WHOSE 429 IS THIS? T4b has always had to tell the two apart; T4a did not, and on an
    # environment where the shared Foundry deployment is smaller than the tier's TPM cap that
    # is the difference between proving the gateway's meter and proving Azure OpenAI's.
    #
    #   gateway  llm-token-limit refusing this subscription's own bucket
    #            -> x-fg-remaining-tpm = 0 on the refusal. This is what T4a is for.
    #   backend  the Foundry deployment saturating (capacity units, not the developer's
    #            budget), passed straight through because the OpenAI API policy deliberately
    #            does not retry a single backend
    #            -> x-fg-remaining-tpm still has headroom, and the body names the model.
    #
    # Observed live on dev 2026-09-05: a 429 with remaining-tpm=6413 and the body "Your
    # requests to gpt-4.1-mini for gpt-4-1-mini in eastus2 have exceeded rate limit" — the
    # deployment was 10 capacity units (~10K TPM) behind a 20K TPM tier, so the backend wall
    # sits in FRONT of the gateway wall and the developer's own meter can never be reached.
    # That is a finding about the environment, not a pass, so it is reported as one.
    $refusalRemaining = Get-ResponseHeader $tpm429 'x-fg-remaining-tpm'
    $isGatewayMeter = $refusalRemaining -eq '0'
    if ($isGatewayMeter) {
        Assert-Check -Id 'T4a' -Name 'TPM cap returns 429 with Retry-After' -Condition ($retryAfter -ne '') `
            -Detail "gateway meter: HTTP 429 after $tpmAttempts requests, Retry-After=$retryAfter, x-fg-remaining-tpm=0 on the refusal — $(Format-BodyExcerpt $tpm429.Body 160)"
    }
    else {
        Add-CycleCheck -State $state -Id 'T4a' -Name 'TPM cap returns 429 with Retry-After' -Status 'SKIP' `
            -Detail ("The 429 came from the BACKEND, not the gateway's meter: x-fg-remaining-tpm=$refusalRemaining still has headroom on the refusal (Retry-After=$retryAfter) and the body names the deployment — $(Format-BodyExcerpt $tpm429.Body 200). " +
                "The shared Foundry deployment saturates before this tier's $($standardTier.tpm) TPM cap binds, so the developer's own meter is unreachable here. Raise the deployment's capacity above the tier's TPM to make it reachable.")
    }

    # Counter isolation: bob is the SAME tier, so a SHARED counter would refuse him too.
    #
    # TWO DIFFERENT 429s live on this path and the difference is the whole assertion:
    #   gateway  llm-token-limit refusing bob's own per-subscription bucket
    #            -> x-fg-remaining-tpm = 0. This is what would mean isolation is broken.
    #   backend  the shared Foundry deployment (gpt-4-1-mini, 10 capacity units) saturating
    #            under alice's hammering, passed straight through by the OpenAI API policy,
    #            which deliberately does not retry a single-backend 429
    #            -> x-fg-remaining-tpm is still FULL, because bob spent nothing.
    # Observed live: "dev-bob HTTP 429, x-fg-remaining-tpm=12000" — a full bucket next to a
    # 429, which is the backend, not the meter. So the check reads the header rather than the
    # status code, and retries first in case the deployment is only briefly saturated.
    $rb = $null
    for ($try = 1; $try -le 3; $try++) {
        $rb = Invoke-GatewayRequest -Uri "$openaiUrl/chat/completions" -Headers (New-OpenAIHeaders $bob) -Body (New-ChatBody)
        if ($rb.StatusCode -eq 200) { break }
        Start-Sleep -Seconds 10
    }
    $bobRemaining = Get-ResponseHeader $rb 'x-fg-remaining-tpm'
    $bobIsolated = ($rb.StatusCode -eq 200) -or ($rb.StatusCode -eq 429 -and $bobRemaining -ne '0' -and $bobRemaining -ne '')
    Assert-Check -Id 'T4b' -Name 'Same-tier dev-bob unaffected while dev-alice is 429 (counter keyed on subscription)' `
        -Condition $bobIsolated `
        -Detail ("dev-bob HTTP {0}, x-fg-remaining-tpm={1} while dev-alice is throttled{2}" -f `
            $rb.StatusCode, $bobRemaining, ($rb.StatusCode -eq 429 ? ' (429 from the shared Foundry deployment, not from bob''s own meter — his bucket is untouched)' : ''))
}
else {
    Assert-Check -Id 'T4a' -Name 'TPM cap returns 429 with Retry-After' -Condition $false `
        -Detail "No 429 after $tpmAttempts requests against a $($standardTier.tpm) TPM cap."
}

# ---- T5: monthly token quota ------------------------------------------------------
Write-CycleHeading "T5 — monthly token quota ($($standardTier.monthlyTokenQuota) tokens) for dev-alice"
if (-not $quotaReachable) {
    $why = [double]::IsPositiveInfinity($quotaBurnMinutes) `
        ? "the '$($standardTier.name)' tier sets no monthly token quota on this environment, so there is no 403 wall to reach" `
        : ("exhausting the $($standardTier.monthlyTokenQuota)-token '$($standardTier.name)' budget through its own $($standardTier.tpm) TPM cap takes at least $quotaBurnMinutes minutes of continuous full-rate traffic (cap: $QuotaBurnTimeoutMinutes min)")
    $skipDetail = "SKIPPED BY DESIGN, not by failure: $why. " +
    'The monthly counter is keyed on the APIM subscription and APIM offers no way to reset it, so burning it would also leave a spent counter on this environment until the calendar month rolls over. ' +
    'The 403 path is proved on the cycle''s own gateway, whose standard tier is deployed deliberately small (see validation/2026-09-05-gateway-cycle.md, T5a-c); ' +
    'it is the same llm-token-limit element T4a/T4b exercise here, and x-fg-remaining-quota is observed counting down on this environment even though it does not reach zero.'
    $skipped = [ordered]@{
        'T5a' = 'Monthly token quota exhausted -> 403'
        'T5b' = 'x-fg-remaining-quota reached 0 before the refusal'
        'T5c' = 'Power-tier dev-carol still 200 while standard-tier dev-alice is quota-blocked'
    }
    foreach ($id in $skipped.Keys) {
        Add-CycleCheck -State $state -Id $id -Name $skipped[$id] -Status 'SKIP' -Detail $skipDetail
    }

    # Not the wall, but the meter: the header proves the monthly counter exists on this
    # environment, is keyed per subscription and is moving. That is the observable part of
    # the quota path that IS reachable here, so record it rather than leaving three SKIPs and
    # no evidence at all.
    #
    # RETRIED, for the same reason T4b and T5c are. This runs seconds after T4 hammered the
    # shared Foundry deployment with 1500-max-token burn bodies, so a transient 429 carries no
    # x-fg-remaining-quota at all — and a check whose entire job is "do not report an
    # environmental fact as a failure" would then report a working meter as broken.
    function Get-RemainingQuota {
        for ($try = 1; $try -le 4; $try++) {
            $r = Invoke-GatewayRequest -Uri "$openaiUrl/chat/completions" -Headers (New-OpenAIHeaders $carol) -Body (New-ChatBody)
            $v = Get-ResponseHeader $r 'x-fg-remaining-quota'
            if ($r.StatusCode -eq 200 -and $v -ne '') { return $v }
            $wait = [int](Get-ResponseHeader $r 'Retry-After')
            if ($wait -le 0 -or $wait -gt 65) { $wait = 15 }
            Write-CycleInfo "  T5d probe got HTTP $($r.StatusCode); waiting ${wait}s and retrying"
            Start-Sleep -Seconds $wait
        }
        return ''
    }
    $before = Get-RemainingQuota
    $after = Get-RemainingQuota
    Assert-Check -Id 'T5d' -Name 'Monthly quota counter is live and decrementing (x-fg-remaining-quota)' `
        -Condition ($before -ne '' -and $after -ne '' -and [long]$after -lt [long]$before) `
        -Detail "dev-carol x-fg-remaining-quota $before -> $after across two requests against the $($standardTier.name)/power products on this gateway"
}
else {
    $deadline = (Get-Date).AddMinutes($QuotaBurnTimeoutMinutes)
    $quota403 = $null
    $quotaAttempts = 0
    $lastRemainingQuota = ''
    while ((Get-Date) -lt $deadline -and $null -eq $quota403) {
        $quotaAttempts++
        $r = Invoke-GatewayRequest -Uri "$openaiUrl/chat/completions" -Headers (New-OpenAIHeaders $alice) -Body $burnBody
        $rq = Get-ResponseHeader $r 'x-fg-remaining-quota'
        if ($rq) { $lastRemainingQuota = $rq }
        Write-CycleInfo "attempt $quotaAttempts -> HTTP $($r.StatusCode), remaining-quota=$rq"

        if ($r.StatusCode -eq 403 -and (Get-ResponseHeader $r 'x-fg-error') -eq '') {
            # Our own policy denials always set x-fg-error; a bare 403 here is APIM's native
            # token-quota refusal, which is the thing under test.
            $quota403 = $r
        }
        elseif ($r.StatusCode -eq 429) {
            # Expected constantly: burning a 40K monthly budget through an 8K/min cap takes at
            # least five minutes of wall clock no matter how the requests are shaped.
            $wait = [int](Get-ResponseHeader $r 'Retry-After')
            if ($wait -le 0 -or $wait -gt 65) { $wait = 20 }
            Write-CycleInfo "  TPM throttled, sleeping ${wait}s"
            Start-Sleep -Seconds $wait
        }
    }

    if ($null -ne $quota403) {
        Assert-Check -Id 'T5a' -Name 'Monthly token quota exhausted -> 403' -Condition $true `
            -Detail "HTTP 403 after $quotaAttempts requests, last x-fg-remaining-quota=$lastRemainingQuota, Retry-After=$(Get-ResponseHeader $quota403 'Retry-After') — $(Format-BodyExcerpt $quota403.Body 200)"
        Assert-Check -Id 'T5b' -Name 'x-fg-remaining-quota reached 0 before the refusal' -Condition ($lastRemainingQuota -eq '0') `
            -Detail "last observed x-fg-remaining-quota=$lastRemainingQuota"

        # What this proves is that the MONTHLY QUOTA counter is per-subscription: alice being out
        # of budget must not touch carol. A transient 429 is a different meter entirely (carol's
        # own TPM bucket, which an earlier stage may have just drained), so it is retried rather
        # than counted as a failure — observed live once, where carol answered 429 with her
        # monthly quota still showing the full 1000000 untouched.
        $rc = $null
        for ($try = 1; $try -le 3; $try++) {
            $rc = Invoke-GatewayRequest -Uri "$openaiUrl/chat/completions" -Headers (New-OpenAIHeaders $carol) -Body (New-ChatBody)
            if ($rc.StatusCode -ne 429) { break }
            $wait = [int](Get-ResponseHeader $rc 'Retry-After')
            if ($wait -le 0 -or $wait -gt 65) { $wait = 20 }
            Write-CycleInfo "dev-carol is TPM-throttled (not quota-blocked); waiting ${wait}s and retrying"
            Start-Sleep -Seconds $wait
        }
        Assert-Check -Id 'T5c' -Name 'Power-tier dev-carol still 200 while standard-tier dev-alice is quota-blocked' `
            -Condition ($rc.StatusCode -eq 200) `
            -Detail "dev-carol HTTP $($rc.StatusCode), x-fg-remaining-quota=$(Get-ResponseHeader $rc 'x-fg-remaining-quota') while dev-alice is 403"
    }
    else {
        Assert-Check -Id 'T5a' -Name 'Monthly token quota exhausted -> 403' -Condition $false `
            -Detail "No quota 403 within $QuotaBurnTimeoutMinutes min / $quotaAttempts requests; last x-fg-remaining-quota=$lastRemainingQuota"
    }
}

$state.smokeCompletedUtc = Get-CycleTimestamp
Save-CycleState -State $state

Write-CycleHeading 'Smoke summary'
foreach ($c in $state.checks) { Write-Host ("  [{0}] {1} {2}" -f $c.status, $c.id, $c.name) }
if ($failures -gt 0) {
    Write-Host "$failures check(s) FAILED." -ForegroundColor Red
    exit 1
}
Write-Host 'All smoke checks passed (skips are recorded, not failures).' -ForegroundColor Green
exit 0
