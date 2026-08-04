[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DataRoot,

    [ValidateRange(0, 16)]
    [int]$DeviceIndex = 0,

    [ValidateSet(1, 4, 6)]
    [int]$CameraCount = 1,

    [ValidateRange(0.0, 16.0)]
    [double]$ScreenCullPixels = 1.0,

    [ValidateRange(5, 300)]
    [int]$WarmupSeconds = 60,

    [ValidateRange(120, 3600)]
    [int]$SampleFrames = 900,

    [ValidateRange(2, 12)]
    [int]$Repeats = 4,

    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$projectRoot = Split-Path -Parent $PSScriptRoot
$playerPath = Join-Path $projectRoot 'Builds\Validation\FullCityWeather\NYCGISFullCityWeatherQA.exe'
if (-not (Test-Path -LiteralPath $playerPath -PathType Leaf)) {
    throw "Benchmark Player is missing: $playerPath"
}

$resolvedDataRoot = (Resolve-Path -LiteralPath $DataRoot).Path
if (-not (Test-Path -LiteralPath $resolvedDataRoot -PathType Container)) {
    throw "NYC GIS data root is missing: $resolvedDataRoot"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $projectRoot (
        "Reports\GpuClusterLocalIndex16\ab-$timestamp-device-$DeviceIndex-cameras-$CameraCount")
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}

$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$env:NYCGIS_DATA_ROOT = $resolvedDataRoot

function Read-NYCGISReport {
    param([Parameter(Mandatory = $true)][string]$Path)
    $values = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        $parts = $line -split '=', 2
        if ($parts.Count -eq 2) {
            $values[$parts[0]] = $parts[1]
        }
    }
    return $values
}

function Convert-ToDouble {
    param([Parameter(Mandatory = $true)][string]$Value)
    return [double]::Parse(
        $Value,
        [System.Globalization.NumberStyles]::Float,
        $invariant)
}

function Get-Median {
    param([Parameter(Mandatory = $true)][double[]]$Values)
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) {
        return [double]::NaN
    }
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return $sorted[$middle]
    }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2.0
}

$variants = @(
    [pscustomobject]@{
        Name = 'wave-tile-32'
        Expected = 'WaveTile32'
        UsesIndex16 = $false
    },
    [pscustomobject]@{
        Name = 'wave-tile-32-index16'
        Expected = 'WaveTile32Index16'
        UsesIndex16 = $true
    }
)
$rows = [System.Collections.Generic.List[object]]::new()

for ($round = 1; $round -le $Repeats; $round++) {
    $roundVariants = if (($round % 2) -eq 1) {
        @($variants[0], $variants[1])
    }
    else {
        @($variants[1], $variants[0])
    }

    foreach ($variant in $roundVariants) {
        $stem = "device-$DeviceIndex-cameras-$CameraCount-$($variant.Name)-r$round"
        $reportPath = Join-Path $outputRoot "$stem.txt"
        $logPath = Join-Path $outputRoot "$stem.log"
        $screenshotPath = Join-Path $outputRoot "$stem.png"
        $arguments = @(
            '-force-d3d12',
            '-force-device-index', [string]$DeviceIndex,
            '-screen-fullscreen', '0',
            '-screen-width', '1280',
            '-screen-height', '720',
            '-nycgis-weather-perf',
            '-nycgis-weather-cameras', [string]$CameraCount,
            '-nycgis-weather-warmup-seconds', [string]$WarmupSeconds,
            '-nycgis-weather-sample-frames', [string]$SampleFrames,
            '-nycgis-bfp2-screen-cull-pixels',
            $ScreenCullPixels.ToString($invariant),
            '-nycgis-bfp2-cull-algorithm', $variant.Name,
            '-nycgis-weather-screenshot', "`"$screenshotPath`"",
            '-nycgis-weather-report', "`"$reportPath`"",
            '-logFile', "`"$logPath`""
        )

        $process = Start-Process `
            -FilePath $playerPath `
            -ArgumentList $arguments `
            -WorkingDirectory $projectRoot `
            -WindowStyle Minimized `
            -Wait `
            -PassThru
        if ($process.ExitCode -ne 0) {
            throw "$stem failed with Player exit code $($process.ExitCode). See $logPath"
        }
        if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            throw "$stem did not write a report. See $logPath"
        }

        $report = Read-NYCGISReport -Path $reportPath
        if ($report.status -ne 'completed') {
            throw "$stem did not complete successfully."
        }
        if ([int]$report.gpuFrameTimingValidSamples -lt $SampleFrames) {
            throw "$stem has incomplete GPU samples."
        }
        if ($report.bfp2ActiveCullAlgorithm -ne $variant.Expected) {
            throw "$stem requested $($variant.Expected) but ran $($report.bfp2ActiveCullAlgorithm)."
        }
        if ($report.bfp2CullValidationValid -ne '1') {
            throw "$stem failed synchronized cull-output validation."
        }
        if ($report.bfp2WaveTileCullSupported -ne '1') {
            throw "$stem did not expose the WaveTile32 control kernel."
        }
        if ($report.bfp2WaveTileIndex16CullSupported -ne '1') {
            throw "$stem did not expose the cluster-local uint16 kernel."
        }
        if ([int]$report.bfp2ClusterLocalIndex16ReadyPacks -ne
            [int]$report.bfp2ResidentPacks) {
            throw "$stem has a resident pack that failed cluster-local uint16 encoding."
        }
        if ($report.screenshotSaved -ne '1' -or
            -not (Test-Path -LiteralPath $screenshotPath -PathType Leaf)) {
            throw "$stem did not save its visual-validation screenshot."
        }

        $row = [pscustomobject]@{
            Round = $round
            Variant = $variant.Name
            GpuMsAverage = Convert-ToDouble $report.gpuFrameMsAverage
            GpuMsP99 = Convert-ToDouble $report.gpuFrameMsP99
            FrameMsAverage = Convert-ToDouble $report.frameMsAverage
            FrameMsP99 = Convert-ToDouble $report.frameMsP99
            FpsAverage = Convert-ToDouble $report.fpsAverage
            ResidentGpuBytes = [long]$report.bfp2ResidentGpuBytes
            ResidentPacks = [int]$report.bfp2ResidentPacks
            ReadyIndex16Packs = [int]$report.bfp2ClusterLocalIndex16ReadyPacks
            SourceIndex32Bytes = [long]$report.bfp2ClusterLocalIndex32SourceBytes
            PackedIndex16Bytes = [long]$report.bfp2ClusterLocalIndex16PackedBytes
            BaseVertexBytes = [long]$report.bfp2ClusterLocalIndex16BaseVertexBytes
            ShippingIndex16Bytes = [long]$report.bfp2ClusterLocalIndex16ShippingBytes
            PotentialSavedBytes = [long]$report.bfp2ClusterLocalIndex16PotentialSavedBytes
            MaximumIndexSpan = [uint32]$report.bfp2ClusterLocalIndex16MaximumSpan
            VisibleIndices = [long]$report.bfp2ValidationVisibleIndices
            VisibleTiles = [long]$report.bfp2ValidationVisibleTiles
            CullHash = $report.bfp2ValidationPerPackHash
            ScreenshotSha256 = (Get-FileHash -LiteralPath $screenshotPath -Algorithm SHA256).Hash
            Report = $reportPath
        }
        $rows.Add($row)
        Write-Host (
            "{0}: GPU {1:F3}/{2:F3} ms, frame {3:F3}/{4:F3} ms, FPS {5:F2}" -f
            $stem,
            $row.GpuMsAverage,
            $row.GpuMsP99,
            $row.FrameMsAverage,
            $row.FrameMsP99,
            $row.FpsAverage)
    }
}

$reference = $rows[0]
foreach ($row in $rows) {
    if ($row.CullHash -ne $reference.CullHash) {
        throw "Cull-output hash mismatch in $($row.Variant), round $($row.Round)."
    }
    if ($row.SourceIndex32Bytes -ne $reference.SourceIndex32Bytes -or
        $row.PackedIndex16Bytes -ne $reference.PackedIndex16Bytes -or
        $row.BaseVertexBytes -ne $reference.BaseVertexBytes) {
        throw "Resident index-byte contract changed in $($row.Variant), round $($row.Round)."
    }
}

$rows | Export-Csv -LiteralPath (Join-Path $outputRoot 'runs.csv') -NoTypeInformation -Encoding utf8

$baseline = @($rows | Where-Object Variant -eq 'wave-tile-32')
$optimized = @($rows | Where-Object Variant -eq 'wave-tile-32-index16')
$baselineGpu = Get-Median @($baseline | ForEach-Object GpuMsAverage)
$optimizedGpu = Get-Median @($optimized | ForEach-Object GpuMsAverage)
$baselineGpuP99 = Get-Median @($baseline | ForEach-Object GpuMsP99)
$optimizedGpuP99 = Get-Median @($optimized | ForEach-Object GpuMsP99)
$baselineFrame = Get-Median @($baseline | ForEach-Object FrameMsAverage)
$optimizedFrame = Get-Median @($optimized | ForEach-Object FrameMsAverage)
$baselineFrameP99 = Get-Median @($baseline | ForEach-Object FrameMsP99)
$optimizedFrameP99 = Get-Median @($optimized | ForEach-Object FrameMsP99)
$pairedGpuWins = 0
$pairedFrameWins = 0
for ($round = 1; $round -le $Repeats; $round++) {
    $a = $baseline | Where-Object Round -eq $round | Select-Object -First 1
    $b = $optimized | Where-Object Round -eq $round | Select-Object -First 1
    if ($b.GpuMsAverage -lt $a.GpuMsAverage) { $pairedGpuWins++ }
    if ($b.FrameMsAverage -lt $a.FrameMsAverage) { $pairedFrameWins++ }
}

$summary = @(
    '# GPU Cluster-local uint16 Index A/B',
    '',
    "Device index: $DeviceIndex; cameras: $CameraCount; screen cull: $ScreenCullPixels px; repeats: $Repeats.",
    "Cull-output hash: $($reference.CullHash) (identical across all runs).",
    '',
    '| Metric | WaveTile32 uint32 | WaveTile32 cluster-local uint16 | Improvement |',
    '|---|---:|---:|---:|',
    ('| GPU avg | {0:F3} ms | {1:F3} ms | {2:F2}% |' -f $baselineGpu, $optimizedGpu, (100.0 * ($baselineGpu - $optimizedGpu) / $baselineGpu)),
    ('| GPU P99 | {0:F3} ms | {1:F3} ms | {2:F2}% |' -f $baselineGpuP99, $optimizedGpuP99, (100.0 * ($baselineGpuP99 - $optimizedGpuP99) / $baselineGpuP99)),
    ('| Frame avg | {0:F3} ms | {1:F3} ms | {2:F2}% |' -f $baselineFrame, $optimizedFrame, (100.0 * ($baselineFrame - $optimizedFrame) / $baselineFrame)),
    ('| Frame P99 | {0:F3} ms | {1:F3} ms | {2:F2}% |' -f $baselineFrameP99, $optimizedFrameP99, (100.0 * ($baselineFrameP99 - $optimizedFrameP99) / $baselineFrameP99)),
    '',
    "Paired GPU-average wins: $pairedGpuWins/$Repeats; frame-average wins: $pairedFrameWins/$Repeats.",
    '',
    '| Resident source-index contract | Bytes |',
    '|---|---:|',
    "| uint32 source stream | $($reference.SourceIndex32Bytes) |",
    "| packed uint16 stream | $($reference.PackedIndex16Bytes) |",
    "| per-cluster base vertices | $($reference.BaseVertexBytes) |",
    "| shipping uint16 total | $($reference.ShippingIndex16Bytes) |",
    "| potential bytes removed after dropping uint32 fallback | $($reference.PotentialSavedBytes) |",
    '',
    "Maximum resident cluster index span: $($reference.MaximumIndexSpan) (uint16 limit: 65535).",
    '',
    'Both A/B variants keep uint32 and uint16 source buffers resident to isolate shader/data-layout performance.',
    'Potential saved bytes are a shipping-layout projection, not a claim about this A/B allocation.',
    'Screenshots and SHA-256 values are retained for visual review.'
)
[System.IO.File]::WriteAllLines((Join-Path $outputRoot 'SUMMARY.md'), $summary)

Write-Host "Completed cluster-local uint16 A/B: $outputRoot"
