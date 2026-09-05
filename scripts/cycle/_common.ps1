<#
.SYNOPSIS
    Shared helpers for the scripts/cycle/* spin-up -> test -> spin-down scripts.

.DESCRIPTION
    Dot-source this from every cycle script:  . "$PSScriptRoot/_common.ps1"

    It owns four things that would otherwise be re-derived (differently) in eight places:

      * the state file            one JSON blob per environment under .state/ (gitignored,
                                  because it holds APIM subscription keys). Every script
                                  reads what the previous one wrote; nothing is passed by
                                  argument between stages.
      * az invocation             always with --subscription, always --output json, always
                                  throwing on a non-zero exit. The subscription is passed
                                  explicitly on every call on purpose: these scripts must
                                  never depend on, or change, the operator's default.
      * check recording           Add-CycleCheck writes PASS/FAIL/SKIP rows into the state
                                  file so cycle.ps1 can render one evidence report at the
                                  end regardless of which stages ran.
      * secret redaction          Protect-CycleSecret masks anything that looks like an APIM
                                  key before it reaches a console or the evidence report.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:CycleRoot = $PSScriptRoot
$script:CycleRepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..' '..')).Path
$script:CycleStateDir = Join-Path $PSScriptRoot '.state'

function Get-CycleRepoRoot { $script:CycleRepoRoot }

function Get-CycleStatePath {
    param([Parameter(Mandatory)][string] $Environment)
    if (-not (Test-Path $script:CycleStateDir)) {
        New-Item -ItemType Directory -Path $script:CycleStateDir -Force | Out-Null
    }
    Join-Path $script:CycleStateDir "$Environment.json"
}

<#
 Reads the state file, or returns a fresh skeleton when there is none. Returns a
 [hashtable] (not a PSCustomObject) so callers can add keys without Add-Member noise.
#>
function Get-CycleState {
    param(
        [Parameter(Mandatory)][string] $Environment,
        [switch] $Required
    )
    $path = Get-CycleStatePath -Environment $Environment
    if (-not (Test-Path $path)) {
        if ($Required) {
            throw "No cycle state for environment '$Environment' at $path. Run scripts/cycle/up.ps1 first."
        }
        return @{
            environment = $Environment
            checks      = @()
        }
    }
    $raw = Get-Content -Path $path -Raw
    return (ConvertFrom-Json $raw -AsHashtable)
}

function Save-CycleState {
    param(
        [Parameter(Mandatory)][hashtable] $State
    )
    $path = Get-CycleStatePath -Environment $State.environment
    $State | ConvertTo-Json -Depth 12 | Set-Content -Path $path -Encoding utf8
}

function Write-CycleHeading {
    param([Parameter(Mandatory)][string] $Text)
    Write-Host ''
    Write-Host "=== $Text ===" -ForegroundColor Cyan
}

function Write-CycleInfo {
    param([Parameter(Mandatory)][string] $Text)
    Write-Host "    $Text" -ForegroundColor DarkGray
}

<#
 Records one check into the state file's `checks` array and prints it. Status is
 PASS / FAIL / SKIP; `Detail` is what lands in the evidence report, so it should be
 the observable fact (a status code, a header value), not a narrative.
#>
function Add-CycleCheck {
    param(
        [Parameter(Mandatory)][hashtable] $State,
        [Parameter(Mandatory)][string] $Id,
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][ValidateSet('PASS', 'FAIL', 'SKIP')][string] $Status,
        [string] $Detail = ''
    )
    if (-not $State.ContainsKey('checks') -or $null -eq $State.checks) { $State.checks = @() }
    # Re-running a stage replaces its rows rather than appending duplicates.
    $State.checks = @($State.checks | Where-Object { $_.id -ne $Id })
    $State.checks += @{
        id       = $Id
        name     = $Name
        status   = $Status
        detail   = Protect-CycleSecret -Text $Detail -State $State
        recorded = (Get-Date).ToUniversalTime().ToString('o')
    }
    $color = switch ($Status) { 'PASS' { 'Green' } 'FAIL' { 'Red' } default { 'Yellow' } }
    Write-Host ("  [{0}] {1} — {2}" -f $Status, $Id, $Name) -ForegroundColor $color
    if ($Detail) { Write-Host "        $(Protect-CycleSecret -Text $Detail -State $State)" -ForegroundColor DarkGray }
    Save-CycleState -State $State
}

<#
 Masks every APIM subscription key currently in the state file. Keys are 32 hex chars;
 the value-based replacement is exact rather than pattern-based so a legitimate hex
 string in a response body is not mangled.
#>
function Protect-CycleSecret {
    param(
        [string] $Text,
        [hashtable] $State
    )
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    if ($null -ne $State -and $State.ContainsKey('apimSubscriptions') -and $null -ne $State.apimSubscriptions) {
        foreach ($name in $State.apimSubscriptions.Keys) {
            $entry = $State.apimSubscriptions[$name]
            if ($entry.ContainsKey('primaryKey') -and $entry.primaryKey) {
                $Text = $Text.Replace([string]$entry.primaryKey, "<$name-key redacted>")
            }
        }
    }
    return $Text
}

<#
 az wrapper. Splats the argument array, forces JSON, and throws with the CLI's own stderr
 when the exit code is non-zero — a silently-ignored az failure is how a cycle script ends
 up asserting against a resource that was never created.

 stderr goes to a temp FILE rather than through `2>&1`: az writes Bicep warnings to stderr
 on every deployment, and under $ErrorActionPreference='Stop' a native command's stderr
 merged into the success stream terminates the script. Separating the streams keeps
 warnings out of the JSON *and* out of the error path, and $script:LastAzError holds the
 stderr text for callers that want to report a failure themselves.
#>
$script:LastAzError = ''

function Invoke-Az {
    param(
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][string] $Subscription,
        [switch] $Raw,
        [switch] $AllowFailure
    )
    $all = @($Arguments) + @('--subscription', $Subscription, '--only-show-errors')
    if (-not $Raw) { $all += @('--output', 'json') }

    $errFile = [System.IO.Path]::GetTempFileName()
    try {
        $stdout = & az @all 2> $errFile
        $exit = $LASTEXITCODE
        $script:LastAzError = (Get-Content -Path $errFile -Raw -ErrorAction SilentlyContinue) ?? ''
    }
    finally {
        Remove-Item -Path $errFile -Force -ErrorAction SilentlyContinue
    }

    if ($exit -ne 0) {
        if ($AllowFailure) { return $null }
        throw "az $($Arguments -join ' ') failed (exit $exit): $script:LastAzError"
    }
    $text = ($stdout -join [Environment]::NewLine)
    if ($Raw) { return $text }
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return (ConvertFrom-Json $text -AsHashtable)
}

function Get-LastAzError { $script:LastAzError }

<#
 Renders an object as a JSON value safe to hand to `az ... --parameters name=<json>`.

 On Windows the Azure CLI is `az.cmd`, and cmd.exe's argument parsing eats the double
 quotes out of a JSON literal before Python ever sees it — the symptom is az's
 "Failed to parse string as JSON: [{name:standard,...}]". Backslash-escaping the quotes
 survives that round trip. Elsewhere az is a shell script that receives the argument
 verbatim, and the same escaping would land literal backslashes in the value, so the
 escape is conditional.

 -AsArray is forced because ConvertTo-Json unwraps a single-element array, which would
 turn an array parameter into an object and fail Bicep type-checking (BCP033).
#>
function Format-AzJsonArg {
    param([Parameter(Mandatory)] $Value, [switch] $AsArray)
    $json = if ($AsArray) { $Value | ConvertTo-Json -Depth 8 -Compress -AsArray } else { $Value | ConvertTo-Json -Depth 8 -Compress }
    if ($IsWindows) { return ($json -replace '"', '\"') }
    return $json
}

<#
 One HTTP probe against the gateway. Returns a plain object with StatusCode, Headers and
 Body for *every* outcome including 4xx/5xx — the whole point of these tests is that a 403
 is the expected result, so a non-2xx must not throw.

 Invoke-WebRequest -SkipHttpErrorCheck (pwsh 7+) does exactly this; the try/catch is for
 transport-level failures (DNS, TLS, connection reset) which are genuinely errors.
#>
function Invoke-GatewayRequest {
    param(
        [Parameter(Mandatory)][string] $Uri,
        [string] $Method = 'POST',
        [hashtable] $Headers = @{},
        [string] $Body,
        [int] $TimeoutSec = 120
    )
    $params = @{
        Uri                = $Uri
        Method             = $Method
        Headers            = $Headers
        SkipHttpErrorCheck = $true
        TimeoutSec         = $TimeoutSec
        ErrorAction        = 'Stop'
    }
    if ($PSBoundParameters.ContainsKey('Body') -and $null -ne $Body) {
        $params.Body = [System.Text.Encoding]::UTF8.GetBytes($Body)
        $params.ContentType = 'application/json'
    }
    try {
        $response = Invoke-WebRequest @params
        $headers = @{}
        foreach ($k in $response.Headers.Keys) { $headers[$k] = ($response.Headers[$k] -join ', ') }
        return [pscustomobject]@{
            StatusCode = [int]$response.StatusCode
            Headers    = $headers
            Body       = [string]$response.Content
            Transport  = $null
        }
    }
    catch {
        return [pscustomobject]@{
            StatusCode = -1
            Headers    = @{}
            Body       = ''
            Transport  = $_.Exception.Message
        }
    }
}

<# Convenience: a header value, or '' when absent (header names are case-insensitive on the wire
   but the dictionary we build above is not, so probe both casings). #>
function Get-ResponseHeader {
    param(
        [Parameter(Mandatory)][pscustomobject] $Response,
        [Parameter(Mandatory)][string] $Name
    )
    foreach ($k in $Response.Headers.Keys) {
        if ($k -ieq $Name) { return [string]$Response.Headers[$k] }
    }
    return ''
}

<# Truncated single-line form of a response body, for evidence lines. #>
function Format-BodyExcerpt {
    param([string] $Body, [int] $Max = 220)
    if ([string]::IsNullOrEmpty($Body)) { return '<empty>' }
    $one = ($Body -replace '\s+', ' ').Trim()
    if ($one.Length -le $Max) { return $one }
    return $one.Substring(0, $Max) + '...'
}
