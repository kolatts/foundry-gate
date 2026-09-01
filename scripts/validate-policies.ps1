<#
.SYNOPSIS
    Static validation for the APIM policy documents in infra/policies.

.DESCRIPTION
    APIM policy XML is only parsed by Azure at deploy time, and Bicep injects the files
    verbatim via loadTextContent() — a malformed document or a stale placeholder is not
    caught until a live deployment fails. This script closes that gap offline:

      1. Every file is token-substituted exactly the way modules/ai-gateway.bicep does
         (same __TOKEN__ names), with representative values.
      2. The substituted document must load as well-formed XML.
      3. No __TOKEN__ placeholder may survive substitution, and no file may contain a
         token this script does not know about (which would mean Bicep is not
         substituting it either).
      4. The root element must be <policies> for scope policies and <fragment> for
         policy fragments.
      5. Every fragment-id referenced by an <include-fragment> must correspond to a
         fragment file that actually exists, and each API policy must declare at most
         one llm-token-limit (double counting is silent and expensive).

    Exits non-zero on the first category of failure, printing every problem found.

.EXAMPLE
    pwsh ./scripts/validate-policies.ps1
#>
[CmdletBinding()]
param(
    [string] $PolicyDirectory = (Join-Path $PSScriptRoot '..' 'infra' 'policies')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Representative substitutions — same token names modules/ai-gateway.bicep replaces.
# __QUOTA_ATTRS__ gets the "quota configured" rendering because it is the harder of the
# two shapes (the empty rendering cannot break XML).
$tokenValues = @{
    '__ANTHROPIC_POOL_ID__'      = 'foundry-anthropic-pool'
    '__OPENAI_BACKEND_ID__'      = 'foundry-openai-fgtest-e7k2-eus2'
    '__ANTHROPIC_API_ID__'       = 'foundrygate-anthropic'
    '__TIER_TPM__'               = '20000'
    '__MODEL_MAP_NAMED_VALUE__'  = 'fg-model-map-standard'
    '__QUOTA_ATTRS__'            = 'token-quota="5000000" token-quota-period="Monthly" remaining-quota-tokens-header-name="x-fg-remaining-quota"'
}

$policyDir = (Resolve-Path $PolicyDirectory).Path
$files = Get-ChildItem -Path $policyDir -Filter '*.xml' -File | Sort-Object Name
if ($files.Count -eq 0) {
    Write-Error "No policy XML found under $policyDir"
}

$fragmentIds = @{}
foreach ($f in $files) {
    if ($f.Name -like '*-fragment.xml') {
        # modules/ai-gateway.bicep names each fragment resource fg-<file stem minus -fragment>.
        $fragmentIds['fg-' + ($f.BaseName -replace '-fragment$', '')] = $f.Name
    }
}

$problems = [System.Collections.Generic.List[string]]::new()
$referencedFragments = [System.Collections.Generic.List[string]]::new()

foreach ($file in $files) {
    $raw = Get-Content -Path $file.FullName -Raw

    # (3a) every token in the file must be one Bicep knows how to substitute
    $found = [regex]::Matches($raw, '__[A-Z0-9_]+__') | ForEach-Object { $_.Value } | Sort-Object -Unique
    foreach ($token in $found) {
        if (-not $tokenValues.ContainsKey($token)) {
            $problems.Add("$($file.Name): unknown placeholder $token (Bicep will not substitute it, and it will reach APIM verbatim)")
        }
    }

    $substituted = $raw
    foreach ($token in $tokenValues.Keys) {
        $substituted = $substituted.Replace($token, $tokenValues[$token])
    }

    # (3b) nothing may survive
    if ($substituted -match '__[A-Z0-9_]+__') {
        $problems.Add("$($file.Name): placeholder survived substitution")
    }

    # (2) well-formedness
    $doc = $null
    try {
        $doc = [xml]$substituted
    }
    catch {
        $problems.Add("$($file.Name): not well-formed XML after substitution — $($_.Exception.Message)")
        continue
    }

    # (4) expected root element
    $expectedRoot = if ($file.Name -like '*-fragment.xml') { 'fragment' } else { 'policies' }
    if ($doc.DocumentElement.Name -ne $expectedRoot) {
        $problems.Add("$($file.Name): root element is <$($doc.DocumentElement.Name)>, expected <$expectedRoot>")
    }

    # (5a) collect include-fragment references
    foreach ($node in $doc.SelectNodes('//include-fragment')) {
        $id = $node.GetAttribute('fragment-id')
        if ([string]::IsNullOrWhiteSpace($id)) {
            $problems.Add("$($file.Name): <include-fragment> with no fragment-id")
        }
        else {
            $referencedFragments.Add("$($file.Name)|$id")
        }
    }

    # (5b) llm-token-limit must be declared in exactly one scope; the tier product policy
    # owns it (see infra/policies/product-policy.xml). Two scopes = double counting.
    $limitCount = $doc.SelectNodes('//llm-token-limit').Count
    if ($file.Name -eq 'product-policy.xml') {
        if ($limitCount -ne 1) {
            $problems.Add("product-policy.xml: expected exactly one <llm-token-limit>, found $limitCount")
        }
    }
    elseif ($limitCount -gt 0) {
        $problems.Add("$($file.Name): declares <llm-token-limit> ($limitCount) — enforcement belongs to the tier product policy only, or requests are counted twice")
    }
}

foreach ($reference in $referencedFragments) {
    $parts = $reference.Split('|')
    if (-not $fragmentIds.ContainsKey($parts[1])) {
        $problems.Add("$($parts[0]): include-fragment '$($parts[1])' has no matching *-fragment.xml in $policyDir")
    }
}

Write-Host "Checked $($files.Count) policy document(s) in $policyDir"
foreach ($file in $files) { Write-Host "  - $($file.Name)" }

if ($problems.Count -gt 0) {
    Write-Host ''
    Write-Host "$($problems.Count) problem(s):" -ForegroundColor Red
    foreach ($p in $problems) { Write-Host "  * $p" -ForegroundColor Red }
    exit 1
}

Write-Host ''
Write-Host 'All policy documents are well-formed and fully substituted.' -ForegroundColor Green
exit 0
