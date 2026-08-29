[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolsRoot = Split-Path -Parent $PSScriptRoot
$runnerPath =
    Join-Path $toolsRoot 'Run-GpuDrivenInstanceBenchmark.ps1'
if (-not (Test-Path -LiteralPath $runnerPath -PathType Leaf)) {
    throw "Runner is missing: $runnerPath"
}
$runner = Get-Content -LiteralPath $runnerPath -Raw

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Needle,
        [Parameter(Mandatory = $true)][string]$Label
    )
    if (-not $Text.Contains($Needle)) {
        throw "$Label is missing required token: $Needle"
    }
}

foreach ($requirement in @(
        @('$ErrorActionPreference = ''Stop''', 'Fail-fast errors'),
        @('Set-StrictMode -Version Latest', 'Strict mode'),
        @('Get-GpuBenchmarkGitSnapshot', 'Git snapshot'),
        @('Benchmark requires a clean worktree', 'Clean-tree gate'),
        @('sourceHashesStableAcrossBuild', 'Source stability receipt'),
        @('Get-GpuBenchmarkPlayerPayload', 'Payload manifest'),
        @('playerPayloadStableThroughRun', 'Payload stability receipt'),
        @('-gpu-driven-instance-require-complete-gpu-timings',
            'Complete native timing requirement'),
        @('nativeTimestampStatus -cne ''ready''',
            'Per-sample native timestamp gate'),
        @('validation.Count -ne 4', 'Pre/post validation cardinality'),
        @('runtimeShaderSha256', 'Classification shader provenance'),
        @('binningShaderSha256', 'Binning shader provenance'),
        @('runtimeApiSha256', 'Runtime API provenance'),
        @('culled-tail-portable', 'Baseline identity'),
        @('visible-only-discard-key-portable', 'Optimized identity'),
        @('visible5-n1048576-v4', 'Five-percent cell'),
        @('visible25-n1048576-v4', 'Twenty-five-percent cell'),
        @('visible75-n1048576-v4', 'Seventy-five-percent cell'),
        @('visible100-n1048576-v4', 'Full-visibility control cell'),
        @('seeded-coprime-permutation-v1',
            'Dispersed visibility layout gate'),
        @('sampleFrames = 900', 'Formal sample count'),
        @('same-process paired ABBA/BAAB', 'Paired protocol report'),
        @('pairedMedianSpeedupPercent', 'Paired effect estimate'),
        @('$pairMedian -ge 1.0 -and $pairMinimum -gt 0.0',
            'Material improvement threshold'),
        @('formalContractSatisfied', 'Formal completion receipt'))) {
    Assert-Contains $runner $requirement[0] $requirement[1]
}

$tokens = $null
$parseErrors = $null
[Management.Automation.Language.Parser]::ParseFile(
    $runnerPath,
    [ref]$tokens,
    [ref]$parseErrors) | Out-Null
if ($parseErrors.Count -ne 0) {
    throw "Runner has $($parseErrors.Count) PowerShell parse error(s)."
}

Write-Host 'GPU-driven instance benchmark provenance contract validated.'
