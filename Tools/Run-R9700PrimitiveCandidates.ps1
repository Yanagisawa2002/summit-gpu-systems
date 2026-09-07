[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SerializationScript,
    [string]$OutputDirectory = '',
    [switch]$Comparison,
    [switch]$AllowMissingGpuTiming
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$fixture = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Fixtures/r9700-primitive-candidates-v1.json') -Raw | ConvertFrom-Json
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'Reports/R9700PrimitiveCandidates' }
$cells = if ($Comparison) { @($fixture.cells) } else { @($fixture.cells[0]) }
if ($Comparison) {
    if ($AllowMissingGpuTiming) { throw 'Comparison cannot allow missing native GPU timing.' }
    if (@(& git -C $root status --porcelain).Count -ne 0) { throw 'Commit/clean the integration worktree before comparison.' }
}
# Hold the cross-task mutex through every child exit. Smoke is the default;
# comparison must be explicitly requested after integration.
& $SerializationScript -Action {
    $built = $false
    $frozenSource = $null
    $frozenPayload = $null
    foreach ($cell in $cells) {
        $argsForRun = @{
            OutputDirectory = Join-Path $OutputDirectory $cell.id
            ElementCount = [int]$cell.count
            Distribution = [string]$cell.distribution
            KeyBitCount = [int]$cell.keyBits
            Backends = [string]$fixture.backends
            Operations = [string]$fixture.operations
            Rounds = $(if ($Comparison) { 3 } else { 1 })
            WarmupFrames = $(if ($Comparison) { 60 } else { 5 })
            SampleFrames = $(if ($Comparison) { 900 } else { 60 })
            CooldownFrames = $(if ($Comparison) { 15 } else { 0 })
            SkipBuild = $built
            SkipSummary = $true
            AllowMissingGpuTiming = [bool]$AllowMissingGpuTiming
        }
        & (Join-Path $PSScriptRoot 'Run-GpuPrimitiveBenchmark.ps1') @argsForRun
        $report = $argsForRun.OutputDirectory
        & (Join-Path $PSScriptRoot 'Summarize-R9700PrimitiveCandidates.ps1') -ReportDirectory $report -Comparison:$Comparison
        $run = Get-Content (Join-Path $report 'runner-config.json') -Raw | ConvertFrom-Json
        if ($Comparison) {
            if ($null -eq $frozenSource) { $frozenSource = $run.sourceSnapshotSha256; $frozenPayload = $run.playerPayload.sha256 }
            if ($run.sourceSnapshotSha256 -ne $frozenSource -or $run.playerPayload.sha256 -ne $frozenPayload) {
                throw 'Source or player payload changed between comparison cells.'
            }
        }
        $built = $true
    }
}
