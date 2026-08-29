[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$toolsRoot = Split-Path -Parent $PSScriptRoot
$selectorPath = Join-Path $toolsRoot 'Select-GpuAutotuningProfile.ps1'
$script:assertionCount = 0

function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )

    $script:assertionCount++
    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Write-ScenarioFixture {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][double]$CalibrationWaveMs,
        [Parameter(Mandatory = $true)][double]$EvaluationWaveMs
    )

    New-Item -ItemType Directory -Path $Root -Force | Out-Null
    [ordered]@{
        rounds = 6
        gitCommit = '0123456789abcdef0123456789abcdef01234567'
    } | ConvertTo-Json | Set-Content -LiteralPath (
        Join-Path $Root 'runner-config.json')
    [ordered]@{
        graphicsDeviceVendorId = 0x10DE
        graphicsDeviceId = 0x2684
        graphicsDeviceVendor = 'NVIDIA'
        graphicsDeviceName = 'Synthetic GPU'
        graphicsDeviceType = 'Direct3D12'
        graphicsDeviceVersion = 'Direct3D 12 [level 12.2]'
        graphicsShaderLevel = 50
    } | ConvertTo-Json | Set-Content -LiteralPath (
        Join-Path $Root 'device.json')

    $rawRows = [System.Collections.Generic.List[object]]::new()
    foreach ($round in 1..2) {
        $rawRows.Add([pscustomobject]@{
            operation = 'exclusive-scan'
            variant = 'portable'
            round = $round
            gpuRegionTimingValid = 1
            nativeTimestampElapsedMs = 1.0
        })
        $rawRows.Add([pscustomobject]@{
            operation = 'exclusive-scan'
            variant = 'wave-ops'
            round = $round
            gpuRegionTimingValid = 1
            nativeTimestampElapsedMs = $CalibrationWaveMs
        })
    }
    $rawRows | Export-Csv -LiteralPath (
        Join-Path $Root 'raw-frames.csv') -NoTypeInformation

    $blockRows = [System.Collections.Generic.List[object]]::new()
    foreach ($round in 3..6) {
        $blockRows.Add([pscustomobject]@{
            operation = 'exclusive-scan'
            variant = 'portable'
            round = $round
            gpuRegionAverageMs = 1.0
            gpuRegionP99Ms = 1.0
        })
        $blockRows.Add([pscustomobject]@{
            operation = 'exclusive-scan'
            variant = 'wave-ops'
            round = $round
            gpuRegionAverageMs = $EvaluationWaveMs
            gpuRegionP99Ms = $EvaluationWaveMs
        })
    }
    $blockRows | Export-Csv -LiteralPath (
        Join-Path $Root 'block-summary.csv') -NoTypeInformation

    $validationRows = [System.Collections.Generic.List[object]]::new()
    foreach ($phase in @('warmup', 'final')) {
        foreach ($variant in @('portable', 'wave-ops')) {
            $validationRows.Add([pscustomobject]@{
                phase = $phase
                operation = 'exclusive-scan'
                variant = $variant
                passed = 1
                resultHash = "equivalent-$phase"
            })
        }
    }
    $validationRows | Export-Csv -LiteralPath (
        Join-Path $Root 'validation.csv') -NoTypeInformation
    @(
        'passed=1',
        'validationFailures=0',
        'measurementReadbackBytes=0'
    ) | Set-Content -LiteralPath (Join-Path $Root 'run-summary.txt')
}

function Invoke-Scenario {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][double]$CalibrationWaveMs,
        [Parameter(Mandatory = $true)][double]$EvaluationWaveMs
    )

    Write-ScenarioFixture `
        -Root $Root `
        -CalibrationWaveMs $CalibrationWaveMs `
        -EvaluationWaveMs $EvaluationWaveMs
    $null = & $selectorPath -ReportDirectory $Root -CalibrationRounds 2
    $profile = Get-Content -LiteralPath (
        Join-Path $Root 'autotune-profile.json') -Raw | ConvertFrom-Json
    $summary = @(Import-Csv -LiteralPath (
        Join-Path $Root 'autotune-workload-summary.csv'))
    if ($summary.Count -ne 1) {
        throw 'Expected one synthetic workload summary row.'
    }
    return [pscustomobject]@{
        profile = $profile
        summary = $summary[0]
    }
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'summit-gpu-autotuning-' + [guid]::NewGuid().ToString('N'))

try {
    $portable = Invoke-Scenario `
        -Root (Join-Path $testRoot 'portable-confirmed') `
        -CalibrationWaveMs 0.99 `
        -EvaluationWaveMs 0.99
    Assert-True (
        $portable.profile.workloads[0].selectedBackend -ceq 'Portable') (
        'A sub-threshold WaveOps calibration keeps Portable.')
    Assert-True ([bool]$portable.profile.workloads[0].accepted) (
        'Independent evaluation accepts the Portable decision.')
    Assert-True (
        [string]$portable.summary.candidateMeetsUpgradeGate -ceq 'False') (
        'Sub-threshold WaveOps does not clear the evaluation gate.')
    Assert-True (
        [string]$portable.summary.selectionConfirmed -ceq 'True') (
        'Portable selection is confirmed independently.')

    $wave = Invoke-Scenario `
        -Root (Join-Path $testRoot 'wave-confirmed') `
        -CalibrationWaveMs 0.8 `
        -EvaluationWaveMs 0.8
    Assert-True (
        $wave.profile.workloads[0].selectedBackend -ceq 'WaveOps') (
        'A decisive WaveOps calibration selects WaveOps.')
    Assert-True ([bool]$wave.profile.workloads[0].accepted) (
        'Independent evaluation accepts the WaveOps decision.')
    Assert-True (
        [string]$wave.summary.candidateMeetsUpgradeGate -ceq 'True') (
        'Decisive WaveOps clears the evaluation gate.')
    Assert-True (
        [string]$wave.summary.selectionConfirmed -ceq 'True') (
        'WaveOps selection is confirmed independently.')

    $mismatch = Invoke-Scenario `
        -Root (Join-Path $testRoot 'calibration-mismatch') `
        -CalibrationWaveMs 0.8 `
        -EvaluationWaveMs 1.1
    Assert-True (
        $mismatch.profile.workloads[0].selectedBackend -ceq 'WaveOps') (
        'Calibration still records the WaveOps choice before evaluation.')
    Assert-True (-not [bool]$mismatch.profile.workloads[0].accepted) (
        'A contradictory evaluation rejects the calibrated choice.')
    Assert-True (
        [string]$mismatch.summary.selectionConfirmed -ceq 'False') (
        'Calibration mismatch remains explicit in compact evidence.')

    "PASS assertions=$script:assertionCount"
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force
    }
}
