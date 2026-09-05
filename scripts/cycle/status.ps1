<#
.SYNOPSIS
    What exists right now, roughly what it costs per hour, and how long it has been up.

.DESCRIPTION
    The "did I leave the gateway running?" script. Safe to run at any point in the cycle,
    including before the first up.ps1 and after a teardown.

    The hourly figures are ROUGH retail east-us-2 rates, hard-coded here rather than pulled
    from the pricing API: the number that matters is "APIM StandardV2 is the only thing worth
    turning off", and a $/hr estimate accurate to within a few cents is enough to make that
    point. Foundry accounts cost nothing while idle — consumption is per token — which is
    exactly why the default teardown keeps them.

.EXAMPLE
    pwsh scripts/cycle/status.ps1
#>
[CmdletBinding()]
param(
    [string] $Environment = 'test',
    [string] $Subscription
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/_common.ps1"

$state = Get-CycleState -Environment $Environment
if (-not $Subscription) {
    if (-not $state.ContainsKey('subscription')) { throw 'No -Subscription given and no state file to read one from.' }
    $Subscription = $state.subscription
}
$rgName = if ($state.ContainsKey('resourceGroup')) { $state.resourceGroup } else { "rg-foundrygate-$Environment" }

# Idle cost per hour, retail eastus2 (rounded). Anything not listed is treated as free
# while idle, which for this stack is true: Log Analytics and App Insights bill on ingested
# GB and a cycle ingests megabytes.
$hourlyRates = @{
    'Microsoft.ApiManagement/service'          = 0.28   # StandardV2, ~$203/month for 1 unit
    'Microsoft.CognitiveServices/accounts'     = 0.0    # pay-per-token, nothing while idle
    'Microsoft.OperationalInsights/workspaces' = 0.0    # per ingested GB
    'Microsoft.Insights/components'            = 0.0    # per ingested GB
}

Write-CycleHeading "Status — $rgName in '$Subscription'"

$rg = Invoke-Az -Subscription $Subscription -AllowFailure -Arguments @('group', 'show', '--name', $rgName)
if ($null -eq $rg) {
    Write-Host '  Resource group does not exist. Nothing is running, nothing is costing anything.' -ForegroundColor Green
    Write-Host '  Next: pwsh scripts/cycle/up.ps1 -Subscription "<name>"'
    exit 0
}

$resources = @(Invoke-Az -Subscription $Subscription -Arguments @('resource', 'list', '--resource-group', $rgName))
if ($resources.Count -eq 0) {
    Write-Host '  Resource group is empty.' -ForegroundColor Green
    exit 0
}

$hourly = 0.0
$foundryCount = 0
$apimCount = 0
foreach ($r in ($resources | Sort-Object type, name)) {
    $rate = if ($hourlyRates.ContainsKey($r.type)) { [double]$hourlyRates[$r.type] } else { 0.0 }
    $hourly += $rate
    if ($r.type -eq 'Microsoft.CognitiveServices/accounts') { $foundryCount++ }
    if ($r.type -eq 'Microsoft.ApiManagement/service') { $apimCount++ }
    $note = if ($rate -gt 0) { ("~`${0:N2}/hr" -f $rate) } else { 'no idle cost' }
    Write-Host ("  {0,-45} {1,-32} {2}" -f $r.type, $r.name, $note)
}

Write-Host ''
Write-Host ("  Estimated idle cost: ~`$" + ('{0:N2}' -f $hourly) + "/hr (~`$" + ('{0:N0}' -f ($hourly * 24 * 30)) + "/month if left up)")

if ($state.ContainsKey('upCompletedUtc') -and $state.upCompletedUtc) {
    $up = [datetimeoffset]::Parse($state.upCompletedUtc)
    $age = [datetimeoffset]::UtcNow - $up
    Write-Host ("  Up since {0:u} ({1:%d}d {1:%h}h {1:%m}m)" -f $up, $age)
    if ($apimCount -gt 0) {
        Write-Host ("  Spent so far on APIM: ~`$" + ('{0:N2}' -f ($age.TotalHours * $hourlyRates['Microsoft.ApiManagement/service'])))
    }
}

Write-Host ''
if ($apimCount -eq 0 -and $foundryCount -gt 0) {
    Write-Host "  State: TORN DOWN (KeepFoundry). $foundryCount Foundry account(s) remain and cost nothing idle." -ForegroundColor Green
    Write-Host '  Next up.ps1 will re-run the template with createModelDeployments=false over them.'
}
elseif ($apimCount -gt 0) {
    Write-Host '  State: UP. APIM is the meter that is running — scripts/cycle/down.ps1 stops it.' -ForegroundColor Yellow
    if ($state.ContainsKey('outputs') -and $state.outputs.ContainsKey('apimGatewayUrl')) {
        Write-Host "  Gateway: $($state.outputs.apimGatewayUrl)"
    }
}

if ($state.ContainsKey('claudeAvailable')) {
    $claude = [bool]$state.claudeAvailable
    Write-Host ("  Claude deployment: {0}" -f ($claude ? 'available' : 'NOT available on this cycle (E-007) — the OpenAI path carries the demo')) `
        -ForegroundColor ($claude ? 'Green' : 'Yellow')
}
