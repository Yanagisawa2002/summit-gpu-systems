[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaselineReceipt,
    [Parameter(Mandatory = $true)]
    [string]$CandidateReceipt,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-Receipt {
    param([Parameter(Mandatory = $true)][string]$Path)
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    $receipt = Get-Content -Raw -LiteralPath $resolved | ConvertFrom-Json
    if ([int]$receipt.schemaVersion -ne 1 -or
        [string]$receipt.suite -cne
            'gpu-systems.external-brg-shooter.run') {
        throw "Unsupported receipt: $resolved"
    }
    return [pscustomobject]@{
        path = $resolved
        value = $receipt
        sha256 = (Get-FileHash -Algorithm SHA256 `
            -LiteralPath $resolved).Hash
    }
}

function Read-Metric {
    param(
        [Parameter(Mandatory = $true)][object]$Receipt,
        [Parameter(Mandatory = $true)][string]$Name
    )
    $raw = [string]$Receipt.$Name
    $value = 0.0
    if ($raw -ceq 'unavailable' -or
        -not [double]::TryParse(
            $raw,
            [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$value)) {
        return $null
    }
    return $value
}

function Get-ImprovementPercent {
    param([double]$Baseline, [double]$Candidate)
    if ($Baseline -le 0.0) {
        return $null
    }
    return (($Baseline - $Candidate) / $Baseline) * 100.0
}

$baselineSource = Read-Receipt $BaselineReceipt
$candidateSource = Read-Receipt $CandidateReceipt
$baseline = $baselineSource.value
$candidate = $candidateSource.value

$failures = [Collections.Generic.List[string]]::new()
if ([string]$baseline.path -cne 'engine-brg') {
    $failures.Add('Baseline receipt path is not engine-brg.')
}
if ([string]$candidate.path -cne 'gpu-systems') {
    $failures.Add('Candidate receipt path is not gpu-systems.')
}
foreach ($name in @(
        'upstreamCommit', 'packageCommit', 'unityVersion',
        'graphicsDeviceName', 'graphicsDeviceVersion', 'graphicsDeviceType',
        'cell', 'instanceCount', 'visiblePercent', 'dirtyPercent',
        'warmupFrames', 'convergenceFrames', 'measuredFrames', 'stateHash')) {
    if ([string]$baseline.$name -cne [string]$candidate.$name) {
        $failures.Add("Paired field mismatch: $name")
    }
}
if (-not [bool]$baseline.accepted -or -not [bool]$candidate.accepted) {
    $failures.Add('At least one individual run receipt was not accepted.')
}
if (-not [bool]$baseline.imageContainsGeometry -or
    -not [bool]$candidate.imageContainsGeometry) {
    $failures.Add('At least one representative image is black.')
}
$imageParity = [string]$baseline.imageHash -ceq
    [string]$candidate.imageHash
if (-not $imageParity) {
    $failures.Add('Representative image hashes differ.')
}
if ([double]$baseline.frameCpuCoverage -lt 0.95 -or
    [double]$candidate.frameCpuCoverage -lt 0.95) {
    $failures.Add('Whole-frame CPU timing coverage is below 95%.')
}
if ([double]$baseline.frameGpuCoverage -lt 0.95 -or
    [double]$candidate.frameGpuCoverage -lt 0.95) {
    $failures.Add('Whole-frame GPU timing coverage is below 95%.')
}

$baselineCpuP95 = Read-Metric $baseline 'frameCpuP95Ms'
$candidateCpuP95 = Read-Metric $candidate 'frameCpuP95Ms'
$baselineGpuP99 = Read-Metric $baseline 'frameGpuP99Ms'
$candidateGpuP99 = Read-Metric $candidate 'frameGpuP99Ms'
$cpuImprovementPercent = if ($null -ne $baselineCpuP95 -and
    $null -ne $candidateCpuP95) {
    Get-ImprovementPercent $baselineCpuP95 $candidateCpuP95
} else { $null }
$cpuImprovementMs = if ($null -ne $baselineCpuP95 -and
    $null -ne $candidateCpuP95) {
    $baselineCpuP95 - $candidateCpuP95
} else { $null }
$gpuRegressionPercent = if ($null -ne $baselineGpuP99 -and
    $null -ne $candidateGpuP99 -and $baselineGpuP99 -gt 0.0) {
    (($candidateGpuP99 - $baselineGpuP99) / $baselineGpuP99) * 100.0
} else { $null }
$gpuRegressionMs = if ($null -ne $baselineGpuP99 -and
    $null -ne $candidateGpuP99) {
    $candidateGpuP99 - $baselineGpuP99
} else { $null }

$cpuGate = $null -ne $cpuImprovementPercent -and
    $null -ne $cpuImprovementMs -and
    $cpuImprovementPercent -ge 20.0 -and
    $cpuImprovementMs -ge 0.20
$gpuGate = $null -ne $gpuRegressionPercent -and
    $null -ne $gpuRegressionMs -and
    $gpuRegressionPercent -le 5.0 -and
    $gpuRegressionMs -le 0.25
if (-not $cpuGate) {
    $failures.Add('Whole-frame CPU P95 improvement gate failed or unavailable.')
}
if (-not $gpuGate) {
    $failures.Add('Whole-frame GPU P99 guardrail failed or unavailable.')
}

$result = [pscustomobject][ordered]@{
    schemaVersion = 1
    suite = 'gpu-systems.external-brg-shooter.pair'
    accepted = $failures.Count -eq 0
    claimEligible = $failures.Count -eq 0
    cell = [string]$baseline.cell
    baselineReceipt = [pscustomobject][ordered]@{
        path = $baselineSource.path
        sha256 = $baselineSource.sha256
    }
    candidateReceipt = [pscustomobject][ordered]@{
        path = $candidateSource.path
        sha256 = $candidateSource.sha256
    }
    imageParity = $imageParity
    baselineImageHash = [string]$baseline.imageHash
    candidateImageHash = [string]$candidate.imageHash
    stateHash = [string]$baseline.stateHash
    frameCpuCoverage = [pscustomobject][ordered]@{
        baseline = [double]$baseline.frameCpuCoverage
        candidate = [double]$candidate.frameCpuCoverage
    }
    frameGpuCoverage = [pscustomobject][ordered]@{
        baseline = [double]$baseline.frameGpuCoverage
        candidate = [double]$candidate.frameGpuCoverage
    }
    frameCpuP95 = [pscustomobject][ordered]@{
        baselineMs = $baselineCpuP95
        candidateMs = $candidateCpuP95
        improvementMs = $cpuImprovementMs
        improvementPercent = $cpuImprovementPercent
        gatePassed = $cpuGate
    }
    frameGpuP99 = [pscustomobject][ordered]@{
        baselineMs = $baselineGpuP99
        candidateMs = $candidateGpuP99
        regressionMs = $gpuRegressionMs
        regressionPercent = $gpuRegressionPercent
        gatePassed = $gpuGate
    }
    failures = [string[]]$failures.ToArray()
    evidenceBoundary =
        'Performance deltas are diagnostic unless this pair and the frozen ' +
        'counterbalanced formal matrix both pass. adapterCpu is not used for ' +
        'the portability gate because the engine BRG callback is opaque.'
}

$json = $result | ConvertTo-Json -Depth 8
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
    $directory = Split-Path -Parent $resolvedOutput
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $temporary = $resolvedOutput + '.partial'
    Set-Content -LiteralPath $temporary -Value $json -Encoding utf8NoBOM
    Move-Item -LiteralPath $temporary -Destination $resolvedOutput -Force
}
$json
if (-not $result.accepted) {
    exit 2
}
