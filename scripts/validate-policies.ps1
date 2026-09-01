<#
.SYNOPSIS
    Static validation for the APIM policy documents in infra/policies.

.DESCRIPTION
    APIM policy XML is only parsed by Azure at deploy time, and Bicep injects the files
    verbatim via loadTextContent() — a malformed document or a stale placeholder is not
    caught until a live deployment fails. This script closes that gap offline:

      1. Every file is token-substituted exactly the way modules/ai-gateway.bicep does
         (same __TOKEN__ names), with representative values — once per RENDER VARIANT,
         because __QUOTA_ATTRS__ renders two genuinely different documents: the quota
         attributes for a tier with monthlyTokenQuota > 0, and the empty string for the
         unlimited tier (which leaves llm-token-limit's last attribute line bare).
      2. Each substituted document must load as well-formed XML.
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
$tokenValues = @{
    '__ANTHROPIC_POOL_ID__'      = 'foundry-anthropic-pool'
    '__OPENAI_BACKEND_ID__'      = 'foundry-openai-fgtest-e7k2-eus2'
    '__ANTHROPIC_API_ID__'       = 'foundrygate-anthropic'
    '__ANTHROPIC_API_PATH__'     = 'anthropic'
    '__OPENAI_API_PATH__'        = 'openai/v1'
    '__TIER_TPM__'               = '20000'
    '__MODEL_MAP_NAMED_VALUE__'  = 'fg-model-map-standard'
    '__QUOTA_ATTRS__'            = 'token-quota="5000000" token-quota-period="Monthly" remaining-quota-tokens-header-name="x-fg-remaining-quota"'
}

# Render variants: each is a set of overrides applied on top of $tokenValues. Both must
# produce a well-formed document, because Bicep really does emit both — the second is the
# "unlimited" tier, whose empty __QUOTA_ATTRS__ leaves the llm-token-limit element to be
# closed by the preceding attribute line.
$renderVariants = @(
    @{ Name = 'quota-configured'; Overrides = @{} }
    @{ Name = 'quota-disabled (unlimited tier)'; Overrides = @{ '__QUOTA_ATTRS__' = '' } }
)

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

$renderCount = 0

foreach ($file in $files) {
    $raw = Get-Content -Path $file.FullName -Raw

    # (3a) every token in the file must be one Bicep knows how to substitute. This is a
    # property of the file, not of a variant, so it is checked once.
    $found = [regex]::Matches($raw, '__[A-Z0-9_]+__') | ForEach-Object { $_.Value } | Sort-Object -Unique
    foreach ($token in $found) {
        if (-not $tokenValues.ContainsKey($token)) {
            $problems.Add("$($file.Name): unknown placeholder $token (Bicep will not substitute it, and it will reach APIM verbatim)")
        }
    }

    foreach ($variant in $renderVariants) {
        $label = "$($file.Name) [$($variant.Name)]"
        $renderCount++

        $substituted = $raw
        foreach ($token in $tokenValues.Keys) {
            $value = if ($variant.Overrides.ContainsKey($token)) { $variant.Overrides[$token] } else { $tokenValues[$token] }
            $substituted = $substituted.Replace($token, $value)
        }

        # (3b) nothing may survive
        if ($substituted -match '__[A-Z0-9_]+__') {
            $problems.Add("${label}: placeholder survived substitution")
        }

        # (2) well-formedness
        $doc = $null
        try {
            $doc = [xml]$substituted
        }
        catch {
            $problems.Add("${label}: not well-formed XML after substitution — $($_.Exception.Message)")
            continue
        }

        # (4) expected root element
        $expectedRoot = if ($file.Name -like '*-fragment.xml') { 'fragment' } else { 'policies' }
        if ($doc.DocumentElement.Name -ne $expectedRoot) {
            $problems.Add("${label}: root element is <$($doc.DocumentElement.Name)>, expected <$expectedRoot>")
        }

        # (5a) collect include-fragment references (once per file — the first variant)
        if ($variant.Name -eq $renderVariants[0].Name) {
            foreach ($node in $doc.SelectNodes('//include-fragment')) {
                $id = $node.GetAttribute('fragment-id')
                if ([string]::IsNullOrWhiteSpace($id)) {
                    $problems.Add("$($file.Name): <include-fragment> with no fragment-id")
                }
                else {
                    $referencedFragments.Add("$($file.Name)|$id")
                }
            }
        }

        # (5b) llm-token-limit must be declared in exactly one scope; the tier product
        # policy owns it (see infra/policies/product-policy.xml). Two = double counting.
        $limitCount = $doc.SelectNodes('//llm-token-limit').Count
        if ($file.Name -eq 'product-policy.xml') {
            if ($limitCount -ne 1) {
                $problems.Add("${label}: expected exactly one <llm-token-limit>, found $limitCount")
            }
            # The unlimited render must NOT carry quota attributes, and the configured
            # render must — a silent swap here is the whole tier system failing quietly.
            $hasQuota = $null -ne $doc.SelectSingleNode('//llm-token-limit/@token-quota')
            $wantsQuota = -not $variant.Overrides.ContainsKey('__QUOTA_ATTRS__')
            if ($hasQuota -ne $wantsQuota) {
                $problems.Add("${label}: token-quota attribute present=$hasQuota, expected=$wantsQuota")
            }
        }
        elseif ($limitCount -gt 0) {
            $problems.Add("${label}: declares <llm-token-limit> ($limitCount) — enforcement belongs to the tier product policy only, or requests are counted twice")
        }
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
Write-Host "Render variants: $(($renderVariants | ForEach-Object { $_.Name }) -join ', ')  ($renderCount renders)"

if ($problems.Count -gt 0) {
    Write-Host ''
    Write-Host "$($problems.Count) problem(s):" -ForegroundColor Red
    foreach ($p in $problems) { Write-Host "  * $p" -ForegroundColor Red }
    exit 1
}

Write-Host ''
Write-Host 'All policy documents are well-formed and fully substituted.' -ForegroundColor Green
exit 0
