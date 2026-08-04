[CmdletBinding()]
param(
    [string]$DataRoot = $env:NYCGIS_DATA_ROOT,
    [ValidateRange(0, 16)][int]$DeviceIndex = 0,
    [ValidateRange(1, 4)][int]$CameraCount = 1,
    [ValidateRange(0, 300)][int]$SceneWarmupSeconds = 60,
    [ValidateRange(0, 600)][int]$WorkloadWarmupFrames = 120,
    [ValidateRange(1, 60)][int]$WorkloadIssueIntervalFrames = 12,
    [ValidateRange(60, 3600)][int]$SampleFrames = 900,
    [ValidateRange(1, 6)][int]$Rounds = 1,
    [ValidateRange(15, 120)][int]$RouteDurationSeconds = 45,
    [ValidateRange(1280, 7680)][int]$Width = 1920,
    [ValidateRange(720, 4320)][int]$Height = 1080,
    [ValidateRange(65536, 1048576)][int]$SensorElementCount = 1048576,
    [ValidateRange(256, 2048)][int]$ResidencyPointsPerPage = 2048,
    [int]$Seed = 1731,
    [string]$UnityPath,
    [string]$OutputDirectory,
    [ValidateRange(60, 7200)][int]$PlayerTimeoutSeconds = 1800,
    [switch]$SkipBuild,
    [switch]$AllowDirtySource
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = Split-Path -Parent $PSScriptRoot
$playerPath = Join-Path $projectRoot 'Builds\Validation\FullCityWeather\NYCGISFullCityWeatherQA.exe'

function Resolve-UnityEditor {
    param([string]$RequestedPath)
    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = (Resolve-Path -LiteralPath $RequestedPath).Path
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Unity Editor is missing: $resolved"
        }
        return $resolved
    }
    $versionLine = Get-Content -LiteralPath (Join-Path $projectRoot 'ProjectSettings\ProjectVersion.txt') | Select-Object -First 1
    $version = ($versionLine -split ':', 2)[1].Trim()
    $candidate = Join-Path 'C:\Program Files\Unity\Hub\Editor' "$version\Editor\Unity.exe"
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "Unity $version was not found at $candidate"
    }
    return $candidate
}

function Get-ScheduledSubmissionCount {
    param(
        [int]$FirstLogicalFrame,
        [int]$FrameCount,
        [int]$IntervalFrames
    )
    if ($FrameCount -le 0) {
        return 0
    }
    $remainder = $FirstLogicalFrame % $IntervalFrames
    $firstOffset = if ($remainder -eq 0) {
        0
    } else {
        $IntervalFrames - $remainder
    }
    if ($firstOffset -ge $FrameCount) {
        return 0
    }
    return 1 + [int][Math]::Floor(
        ($FrameCount - 1 - $firstOffset) / [double]$IntervalFrames)
}

function Get-InterpolatedPercentile {
    param(
        [double[]]$Values,
        [ValidateRange(0.0, 1.0)][double]$Percentile
    )
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) {
        throw 'Cannot calculate a percentile for an empty sample set.'
    }
    $position = $Percentile * ($sorted.Count - 1)
    $lower = [int][Math]::Floor($position)
    $upper = [int][Math]::Ceiling($position)
    if ($lower -eq $upper) {
        return [double]$sorted[$lower]
    }
    $weight = $position - $lower
    return ([double]$sorted[$lower] * (1.0 - $weight)) +
        ([double]$sorted[$upper] * $weight)
}

function Assert-CloseMetric {
    param(
        [double]$Actual,
        [double]$Expected,
        [string]$Name,
        [double]$Tolerance = 0.000001
    )
    if ([Math]::Abs($Actual - $Expected) -gt $Tolerance) {
        throw "$Name mismatch: report=$Actual raw=$Expected"
    }
}

function Assert-NativeTimestampEvidence {
    param(
        [string]$Stem,
        [string]$TimestampPath,
        [object]$Report,
        [int]$ExpectedHeroSamples,
        [int]$ControlIntervalFrames
    )
    if (-not (Test-Path -LiteralPath $TimestampPath -PathType Leaf)) {
        throw "$Stem did not produce $TimestampPath"
    }
    if ([System.IO.Path]::GetFullPath([string]$Report.rawTimestampsPath) -ne
        [System.IO.Path]::GetFullPath($TimestampPath)) {
        throw "$Stem reported an unexpected raw timestamp path."
    }
    $timestampRows = @(Import-Csv -LiteralPath $TimestampPath)
    $expectedControls = [int][Math]::Ceiling(
        $ExpectedHeroSamples / [double]$ControlIntervalFrames)
    $expectedRows = 16 + $ExpectedHeroSamples + $expectedControls
    if ($timestampRows.Count -ne $expectedRows) {
        throw "$Stem timestamp row count was $($timestampRows.Count); expected $expectedRows."
    }
    if (@($timestampRows | Where-Object {
        $_.status -ne 'Ready' -or [int]$_.valid -ne 1
    }).Count -ne 0) {
        throw "$Stem contains a non-ready or invalid timestamp row."
    }
    $tokens = @($timestampRows | ForEach-Object { [string]$_.token })
    $tags = @($timestampRows | ForEach-Object { [string]$_.userTag })
    if (@($tokens | Where-Object { $_ -eq '0' } | Select-Object -Unique).Count -ne 0 -or
        @($tokens | Select-Object -Unique).Count -ne $timestampRows.Count -or
        @($tags | Where-Object { $_ -eq '0' } | Select-Object -Unique).Count -ne 0 -or
        @($tags | Select-Object -Unique).Count -ne $timestampRows.Count) {
        throw "$Stem timestamp token/user-tag identities were not unique and nonzero."
    }
    foreach ($row in $timestampRows) {
        $begin = [uint64]$row.beginTicks
        $end = [uint64]$row.endTicks
        $elapsed = [uint64]$row.elapsedTicks
        if ($end -lt $begin -or $elapsed -ne ($end - $begin) -or
            [uint64]$row.frequency -eq 0 -or
            [uint64]$row.fenceValue -eq 0 -or
            [int]$row.resultFrame -lt [int]$row.sourceFrame -or
            [int]$row.pendingFrames -ne
                ([int]$row.resultFrame - [int]$row.sourceFrame)) {
            throw "$Stem contains internally inconsistent native timestamp evidence."
        }
    }
    $frequencies = @($timestampRows.frequency | Select-Object -Unique)
    $generations = @($timestampRows.deviceGeneration | Select-Object -Unique)
    if ($frequencies.Count -ne 1 -or
        [uint64]$frequencies[0] -ne
            [uint64]$Report.nativeTimestampObservedFrequency -or
        $generations.Count -ne 1 -or
        [uint32]$generations[0] -ne
            [uint32]$Report.nativeTimestampDeviceGeneration) {
        throw "$Stem timestamp frequency or device generation was inconsistent."
    }
    $warmupControls = @($timestampRows |
        Where-Object kind -eq 'WarmupControl' |
        Sort-Object { [int]$_.sampleIndex })
    $warmupHeroes = @($timestampRows |
        Where-Object kind -eq 'WarmupHero' |
        Sort-Object { [int]$_.sampleIndex })
    $controls = @($timestampRows |
        Where-Object kind -eq 'MeasurementControl' |
        Sort-Object { [int]$_.sampleIndex })
    $heroes = @($timestampRows |
        Where-Object kind -eq 'MeasurementHero' |
        Sort-Object { [int]$_.sampleIndex })
    if ($warmupControls.Count -ne 8 -or $warmupHeroes.Count -ne 8 -or
        $controls.Count -ne $expectedControls -or
        $heroes.Count -ne $ExpectedHeroSamples) {
        throw "$Stem timestamp phase/kind counts were incomplete."
    }
    for ($index = 0; $index -lt 8; $index++) {
        if ([int]$warmupControls[$index].sampleIndex -ne $index -or
            [int]$warmupHeroes[$index].sampleIndex -ne $index) {
            throw "$Stem warmup timestamp indices were not exact."
        }
    }
    for ($index = 0; $index -lt $heroes.Count; $index++) {
        if ([int]$heroes[$index].sampleIndex -ne $index -or
            [uint32]$heroes[$index].flags -ne 0 -or
            [int]$heroes[$index].srpHeroCallbacks -ne 1 -or
            [uint64]$heroes[$index].elapsedTicks -eq 0 -or
            [double]$heroes[$index].elapsedMs -le 0.0) {
            throw "$Stem hero timestamp mapping failed at sample $index."
        }
    }
    for ($index = 0; $index -lt $controls.Count; $index++) {
        if ([int]$controls[$index].sampleIndex -ne
                ($index * $ControlIntervalFrames) -or
            [uint32]$controls[$index].flags -ne 1 -or
            [int]$controls[$index].srpHeroCallbacks -ne 0) {
            throw "$Stem control timestamp mapping failed at control $index."
        }
    }
    if (@($warmupControls | Where-Object {
            [uint32]$_.flags -ne 1 -or [int]$_.srpHeroCallbacks -ne 0
        }).Count -ne 0 -or
        @($warmupHeroes | Where-Object {
            [uint32]$_.flags -ne 0 -or
            [int]$_.srpHeroCallbacks -ne 1 -or
            [uint64]$_.elapsedTicks -eq 0
        }).Count -ne 0) {
        throw "$Stem warmup timestamp flags or callbacks were invalid."
    }
    $heroMs = [double[]]@($heroes | ForEach-Object { [double]$_.elapsedMs })
    $controlMs = [double[]]@($controls | ForEach-Object { [double]$_.elapsedMs })
    $warmupControlMs = [double[]]@(
        $warmupControls | ForEach-Object { [double]$_.elapsedMs })
    $warmupHeroMs = [double[]]@(
        $warmupHeroes | ForEach-Object { [double]$_.elapsedMs })
    $heroAverage = [double](($heroMs | Measure-Object -Average).Average)
    $heroP95 = Get-InterpolatedPercentile $heroMs 0.95
    $heroP99 = Get-InterpolatedPercentile $heroMs 0.99
    $controlP50 = Get-InterpolatedPercentile $controlMs 0.50
    $controlP99 = Get-InterpolatedPercentile $controlMs 0.99
    $warmupControlP99 = Get-InterpolatedPercentile $warmupControlMs 0.99
    $warmupHeroP50 = Get-InterpolatedPercentile $warmupHeroMs 0.50
    Assert-CloseMetric ([double]$Report.gpuAverageMs) $heroAverage "$Stem GPU average"
    Assert-CloseMetric ([double]$Report.gpuP95Ms) $heroP95 "$Stem GPU P95"
    Assert-CloseMetric ([double]$Report.gpuP99Ms) $heroP99 "$Stem GPU P99"
    Assert-CloseMetric ([double]$Report.nativeTimestampControlP50Ms) $controlP50 "$Stem control P50"
    Assert-CloseMetric ([double]$Report.nativeTimestampControlP99Ms) $controlP99 "$Stem control P99"
    if ($warmupHeroP50 -le $warmupControlP99) {
        throw "$Stem split-command ordering discriminator failed."
    }
    $measurementRows = @($heroes + $controls)
    $maxPendingFrames = ($measurementRows |
        Measure-Object -Property pendingFrames -Maximum).Maximum
    if ([int]$Report.nativeTimestampMaximumResultPendingFrames -ne
        [int]$maxPendingFrames) {
        throw "$Stem result-latency aggregate did not match raw evidence."
    }
    return [pscustomobject]@{
        HeroAverageMs = $heroAverage
        HeroP95Ms = $heroP95
        HeroP99Ms = $heroP99
        ControlP50Ms = $controlP50
        ControlP99Ms = $controlP99
        WarmupControlP99Ms = $warmupControlP99
        WarmupHeroP50Ms = $warmupHeroP50
        Rows = $timestampRows.Count
    }
}

if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $env:USERPROFILE 'Downloads\NYCGISData'
}
if ($CameraCount -ne 1 -and $CameraCount -ne 4) {
    throw 'The cinematic benchmark requires one or four cameras.'
}
$expectedCityCullCameraMode = if ($CameraCount -eq 1) {
    'ExplicitHeroOnly'
} else {
    'ExplicitHeroPlusPayloadUnion'
}
$expectedPayloadCameraCount = $CameraCount - 1
$expectedPayloadRenderCount =
    $SampleFrames * $expectedPayloadCameraCount
$resolvedDataRoot = (Resolve-Path -LiteralPath $DataRoot).Path
if (-not (Test-Path -LiteralPath $resolvedDataRoot -PathType Container)) {
    throw "NYC GIS data root is missing: $resolvedDataRoot"
}
$env:NYCGIS_DATA_ROOT = $resolvedDataRoot

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $projectRoot "Reports\GpuCinematicBenchmark\local-$stamp"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}
$outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot -PathType Container) {
    $existingArtifacts = @(Get-ChildItem -LiteralPath $outputRoot -Force)
    if ($existingArtifacts.Count -ne 0) {
        throw "Output directory must be empty: $outputRoot"
    }
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$sourceCommit = (& git -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceCommit)) {
    throw 'Could not resolve the benchmark source commit.'
}
$sourceStatus = @(
    & git -C $projectRoot status --porcelain --untracked-files=normal
)
if ($LASTEXITCODE -ne 0) {
    throw 'Could not inspect the benchmark source status.'
}
$sourceDirty = $sourceStatus.Count -gt 0
if ($sourceDirty -and -not $AllowDirtySource) {
    throw "The benchmark source is dirty. Commit/stash it first, or use -AllowDirtySource for an explicitly non-formal run."
}

if (-not $SkipBuild) {
    $resolvedUnity = Resolve-UnityEditor -RequestedPath $UnityPath
    $buildLog = Join-Path $outputRoot 'build.log'
    $buildArgs = @(
        '-batchmode',
        '-projectPath', $projectRoot,
        '-executeMethod', 'GpuStressShowcaseBuild.BuildBatch',
        '-logFile', $buildLog
    )
    $buildOptions = @{
        FilePath = $resolvedUnity
        ArgumentList = $buildArgs
        WorkingDirectory = $projectRoot
        WindowStyle = 'Hidden'
        Wait = $true
        PassThru = $true
    }
    $build = Start-Process @buildOptions
    if ($build.ExitCode -ne 0) {
        throw "Cinematic benchmark Player build failed with exit code $($build.ExitCode). See $buildLog"
    }
}

if (-not (Test-Path -LiteralPath $playerPath -PathType Leaf)) {
    throw "Cinematic benchmark Player is missing: $playerPath"
}
$playerSha256 = (Get-FileHash -LiteralPath $playerPath -Algorithm SHA256).Hash
$playerDirectory = Split-Path -Parent $playerPath
$playerDataDirectory = Join-Path $playerDirectory `
    (([System.IO.Path]::GetFileNameWithoutExtension($playerPath)) + '_Data')
$pluginDirectory = Join-Path $playerDataDirectory 'Plugins'
$nativeDllMatches = @(if (Test-Path -LiteralPath $pluginDirectory) {
    Get-ChildItem -LiteralPath $pluginDirectory -Recurse -File |
        Where-Object Name -eq 'SummitGpuTimestamps.dll'
})
if ($nativeDllMatches.Count -ne 1) {
    throw "Expected exactly one SummitGpuTimestamps.dll below $pluginDirectory; found $($nativeDllMatches.Count)."
}
$nativeTimestampDllPath = $nativeDllMatches[0].FullName
$nativeTimestampDllSha256 = (Get-FileHash -LiteralPath `
    $nativeTimestampDllPath -Algorithm SHA256).Hash
$expectedMeasuredSubmissions = Get-ScheduledSubmissionCount `
    -FirstLogicalFrame $WorkloadWarmupFrames `
    -FrameCount $SampleFrames `
    -IntervalFrames $WorkloadIssueIntervalFrames
$expectedWarmupSubmissions = Get-ScheduledSubmissionCount `
    -FirstLogicalFrame 0 `
    -FrameCount $WorkloadWarmupFrames `
    -IntervalFrames $WorkloadIssueIntervalFrames
$nativeControlIntervalFrames = 8
$expectedNativeControls = [int][Math]::Ceiling(
    $SampleFrames / [double]$nativeControlIntervalFrames)
$expectedNativeScope =
    'SplitDirectQueueBegin_CityCull_ExplicitCameraGroup_End_v1'

$rows = [System.Collections.Generic.List[object]]::new()
for ($round = 1; $round -le $Rounds; $round++) {
    $variants = if (($round % 2) -eq 1) { @('baseline', 'optimized') } else { @('optimized', 'baseline') }
    foreach ($variant in $variants) {
        $stem = "$variant-r$round"
        $reportPath = Join-Path $outputRoot "$stem.json"
        $screenshotPath = Join-Path $outputRoot "$stem.png"
        $logPath = Join-Path $outputRoot "$stem.log"
        $arguments = @(
            '-force-d3d12',
            '-force-device-index', [string]$DeviceIndex,
            '-screen-fullscreen', '0',
            '-screen-width', [string]$Width,
            '-screen-height', [string]$Height,
            '-gpu-stress-showcase',
            '-gpu-stress-cinematic',
            '-gpu-stress-cinematic-fullscreen',
            '-gpu-stress-output-width', [string]$Width,
            '-gpu-stress-output-height', [string]$Height,
            '-gpu-stress-auto-exit',
            '-gpu-stress-variant', $variant,
            '-gpu-stress-cameras', [string]$CameraCount,
            '-gpu-stress-cinematic-duration-seconds', [string]$RouteDurationSeconds,
            '-gpu-stress-cinematic-time-of-day', '17.1',
            '-gpu-stress-scene-warmup-seconds', [string]$SceneWarmupSeconds,
            '-gpu-stress-workload-warmup-frames', [string]$WorkloadWarmupFrames,
            '-gpu-stress-workload-issue-interval-frames',
                [string]$WorkloadIssueIntervalFrames,
            '-gpu-stress-sample-frames', [string]$SampleFrames,
            '-gpu-stress-sensor-elements', [string]$SensorElementCount,
            '-gpu-stress-points-per-page', [string]$ResidencyPointsPerPage,
            '-gpu-stress-seed', [string]$Seed,
            '-gpu-stress-source-commit', $sourceCommit,
            '-gpu-stress-player-sha256', $playerSha256,
            '-gpu-stress-report', $reportPath,
            '-gpu-stress-screenshot', $screenshotPath,
            '-logFile', $logPath
        )
        if ($sourceDirty) {
            $arguments += '-gpu-stress-source-dirty'
        }
        $runOptions = @{
            FilePath = $playerPath
            ArgumentList = $arguments
            WorkingDirectory = $projectRoot
            WindowStyle = 'Normal'
            PassThru = $true
        }
        $process = Start-Process @runOptions
        if (-not $process.WaitForExit($PlayerTimeoutSeconds * 1000)) {
            try {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            }
            catch {
            }
            throw "$stem exceeded the $PlayerTimeoutSeconds second Player timeout. See $logPath"
        }
        if ($process.ExitCode -ne 0) {
            throw "$stem failed with Player exit code $($process.ExitCode). See $logPath"
        }
        if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) {
            throw "$stem did not produce $reportPath"
        }

        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        if ($report.schemaVersion -ne 5) {
            throw "$stem must use exact report schema v5; got $($report.schemaVersion)."
        }
        if (-not $report.qualityPassed) {
            throw "$stem failed output validation: $($report.qualityMessage)"
        }
        if (-not $report.cinematic) {
            throw "$stem did not report cinematic mode."
        }
        if ($report.variant -ne $variant) {
            throw "$stem reported variant '$($report.variant)'."
        }
        if ($variant -eq 'optimized' -and $report.rendererAlgorithm -ne 'WaveTile32') {
            throw "$stem did not activate WaveTile32; got $($report.rendererAlgorithm)."
        }
        if ($report.rendererCount -ne 1 -or
            $report.cameraCount -ne $CameraCount) {
            throw "$stem did not execute the requested camera shape."
        }
        if ($report.cityResidentPacks -le 0 -or
            $report.cityResidentPacks -ne $report.cityTotalPacks -or
            $report.cityCullCameraMode -ne $expectedCityCullCameraMode) {
            throw "$stem did not use the requested explicit city cull mode; " +
                "got '$($report.cityCullCameraMode)'."
        }
        if (-not $report.cityValidationStable -or
            -not $report.cityValidationFreshDispatches -or
            $report.cityValidationMode -ne
                'ForcedHeroFreshPrimaryPassConverged3' -or
            $report.cityValidationSamples -lt 3 -or
            $report.cityValidationSamples -gt 30 -or
            $report.cityValidationFirstPassEpoch -le 0 -or
            $report.cityValidationLastPassEpoch -le
                $report.cityValidationFirstPassEpoch -or
            $report.cityValidationPreparedCameraCount -ne 1 -or
            $report.cityValidationDispatchedPackCount -ne
                $report.cityResidentPacks -or
            -not $report.cityValidationPackSetComplete -or
            [string]::IsNullOrWhiteSpace(
                [string]$report.cityValidationCameraStateHash) -or
            [string]::IsNullOrWhiteSpace(
                [string]$report.cityValidationDispatchedPackHash)) {
            throw "$stem did not produce a fresh completed-primary-pass " +
                "fixed-camera city oracle."
        }
        if ($report.payloadRenderMode -ne
                'QueuedExplicitCamerasAfterBfp2LateUpdateSrpVerified' -or
            -not $report.payloadRenderCountValid -or
            $report.measuredPayloadRenderCount -ne
                $expectedPayloadRenderCount -or
            $report.expectedPayloadRenderCount -ne
                $expectedPayloadRenderCount) {
            throw "$stem did not render the exact requested payload cadence."
        }
        if (-not $report.prePayloadCitySubmissionValid -or
            $report.measuredPrePayloadCitySubmissionCount -ne $SampleFrames -or
            $report.expectedPrePayloadCitySubmissionCount -ne $SampleFrames) {
            throw "$stem did not prove a fresh full-city submission before " +
                "every payload-camera render group."
        }
        if (-not $report.cameraRenderOrderValid -or
            $report.measuredPayloadSrpRenderCount -ne
                $expectedPayloadRenderCount -or
            $report.expectedPayloadSrpRenderCount -ne
                $expectedPayloadRenderCount -or
            $report.measuredHeroSrpRenderCount -ne $SampleFrames -or
            $report.expectedHeroSrpRenderCount -ne $SampleFrames) {
            throw "$stem did not prove exactly one ordered SRP render for " +
                "the hero and all three payload cameras per measured frame."
        }
        if ($report.workloadIssueIntervalFrames -ne
            $WorkloadIssueIntervalFrames -or
            $report.expectedWorkloadSubmissions -ne
                $expectedMeasuredSubmissions -or
            -not $report.workloadSubmissionCadenceValid -or
            $report.scheduledWorkloadIssueCount -ne
                $report.expectedWorkloadSubmissions -or
            $report.sensorSubmitted -ne $report.expectedWorkloadSubmissions -or
            $report.residencySubmitted -ne $report.expectedWorkloadSubmissions -or
            $report.deadlineSubmitted -ne $report.expectedWorkloadSubmissions -or
            $report.sensorDropped -ne 0 -or
            $report.residencyDropped -ne 0 -or
            $report.deadlineDropped -ne 0 -or
            -not $report.measurementWorkloadsDrained) {
            throw "$stem did not execute the exact zero-drop auxiliary workload cadence."
        }
        $expectedUniqueStates = [Math]::Min(
            [int]$report.expectedWorkloadSubmissions,
            [int]$report.workloadLogicalStateCount)
        if ($report.workloadUniqueLogicalStates -ne $expectedUniqueStates -or
            [string]::IsNullOrWhiteSpace(
                [string]$report.workloadIssueFrameSequenceHash) -or
            [string]::IsNullOrWhiteSpace(
                [string]$report.workloadLogicalStateSequenceHash)) {
            throw "$stem did not cover or record the expected logical-state sequence."
        }
        if (-not $report.warmupSubmissionCadenceValid -or
            $report.warmupExpectedWorkloadSubmissions -ne
                $expectedWarmupSubmissions -or
            $report.warmupScheduledWorkloadIssueCount -ne
                $report.warmupExpectedWorkloadSubmissions -or
            $report.warmupSensorSubmitted -ne
                $report.warmupExpectedWorkloadSubmissions -or
            $report.warmupResidencySubmitted -ne
                $report.warmupExpectedWorkloadSubmissions -or
            $report.warmupDeadlineSubmitted -ne
                $report.warmupExpectedWorkloadSubmissions -or
            $report.warmupSensorDropped -ne 0 -or
            $report.warmupResidencyDropped -ne 0 -or
            $report.warmupDeadlineDropped -ne 0) {
            throw "$stem did not start from an exact zero-drop warmup cadence."
        }
        if (-not $report.sampleCompletenessValid -or
            $report.sampleFrames -ne $SampleFrames -or
            $report.configuredMaxSampleFrames -ne $SampleFrames -or
            -not $report.gpuTimingCoverageValid -or
            -not $report.heroTimestampEvidenceComplete -or
            $report.gpuTimingMode -ne 'NativeDx12DirectQueueTimestamp' -or
            $report.gpuTimingScopeVersion -ne $expectedNativeScope -or
            $report.gpuTimingQueue -ne 'D3D12Direct' -or
            $report.graphicsApi -ne 'Direct3D12' -or
            $report.gpuTimingPrimeFrames -ne 8 -or
            $report.gpuTimingValidSamples -ne $SampleFrames -or
            $report.frameTimingGpuSamples -ne 0 -or
            $report.profilerGpuSamples -ne 0 -or
            -not $report.nativeTimestampBackendAvailable -or
            $report.nativeTimestampAbiVersion -ne 2 -or
            (([uint32]$report.nativeTimestampCapabilityFlags -band 0x1F) -ne 0x1F) -or
            $report.nativeTimestampRingCapacity -le 0 -or
            $report.nativeTimestampPreparedScopes -le 0 -or
            $report.nativeTimestampPreparedScopes -gt
                $report.nativeTimestampRingCapacity -or
            $report.nativeTimestampObservedFrequency -le 0 -or
            $report.nativeTimestampDeviceGeneration -le 0 -or
            $report.nativeTimestampDllSha256 -ne
                $nativeTimestampDllSha256 -or
            $report.nativeTimestampWarmupPairs -ne 8 -or
            -not $report.nativeTimestampWarmupPassed -or
            -not $report.nativeTimestampOrderingDiscriminatorPassed -or
            $report.nativeTimestampWarmupControlSubmitted -ne 8 -or
            $report.nativeTimestampWarmupControlReady -ne 8 -or
            $report.nativeTimestampWarmupControlValid -ne 8 -or
            $report.nativeTimestampWarmupHeroSubmitted -ne 8 -or
            $report.nativeTimestampWarmupHeroReady -ne 8 -or
            $report.nativeTimestampWarmupHeroValid -ne 8 -or
            $report.nativeTimestampWarmupHeroP50Ms -le
                $report.nativeTimestampWarmupControlP99Ms -or
            $report.nativeTimestampExpectedHeroSamples -ne $SampleFrames -or
            $report.nativeTimestampHeroSubmitted -ne $SampleFrames -or
            $report.nativeTimestampHeroReady -ne $SampleFrames -or
            $report.nativeTimestampHeroValid -ne $SampleFrames -or
            $report.nativeTimestampExpectedControlSamples -ne
                $expectedNativeControls -or
            $report.nativeTimestampControlSubmitted -ne
                $expectedNativeControls -or
            $report.nativeTimestampControlReady -ne
                $expectedNativeControls -or
            $report.nativeTimestampControlValid -ne
                $expectedNativeControls -or
            $report.nativeTimestampAcquireFailures -ne 0 -or
            $report.nativeTimestampPreparedScopeFailures -ne 0 -or
            $report.nativeTimestampResultFailures -ne 0 -or
            $report.nativeTimestampTimeouts -ne 0 -or
            $report.nativeTimestampFinalPendingSamples -ne 0 -or
            $report.nativeTimestampFinalActiveSamples -ne 0 -or
            $report.nativeTimestampFinalReservedSamples -ne 0 -or
            $report.nativeTimestampFinalSubmittedSamples -ne 0 -or
            $report.nativeTimestampTerminal -or
            $report.nativeTimestampInstrumentationReadbackBytes -ne
                (16 * ($SampleFrames + $expectedNativeControls))) {
            throw "$stem did not satisfy the complete native DX12 timestamp contract."
        }
        $timestampPath = Join-Path $outputRoot "$stem.timestamps.csv"
        $timestampEvidence = Assert-NativeTimestampEvidence `
            -Stem $stem `
            -TimestampPath $timestampPath `
            -Report $report `
            -ExpectedHeroSamples $SampleFrames `
            -ControlIntervalFrames $nativeControlIntervalFrames
        if (-not $report.measurementResidencyStable) {
            throw "$stem changed BFP2 residency during the measured interval."
        }
        if (-not $report.measurementCullTopologyValid -or
            $report.measurementCullTopologySamples -ne $SampleFrames -or
            $report.measurementMinCullCameraCount -ne $CameraCount -or
            $report.measurementMaxCullCameraCount -ne $CameraCount -or
            $report.measurementMinCullPackCount -ne
                $report.cityResidentPacks -or
            $report.measurementMaxCullPackCount -ne
                $report.cityResidentPacks -or
            [string]::IsNullOrWhiteSpace(
                [string]$report.measurementCullTopologySequenceHash)) {
            throw "$stem did not prove a fresh requested-camera cull pass for " +
                "every measured frame."
        }
        if ($report.sourceCommit -ne $sourceCommit -or $report.sourceDirty -ne $sourceDirty) {
            throw "$stem reported incorrect source provenance."
        }
        if ($report.playerSha256 -ne $playerSha256) {
            throw "$stem reported an incorrect Player SHA256."
        }

        $galleryPrefix = Join-Path $outputRoot $stem
        foreach ($shot in @('skyline', 'facade', 'texture')) {
            $shotPath = "$galleryPrefix-$shot.png"
            if (-not (Test-Path -LiteralPath $shotPath -PathType Leaf)) {
                throw "$stem did not produce gallery frame $shotPath"
            }
        }

        $rows.Add([pscustomobject]@{
            Round = $round
            Variant = $variant
            Route = [string]$report.cinematicRouteId
            Width = [int]$report.outputWidth
            Height = [int]$report.outputHeight
            FrameAverageMs = [double]$report.frameAverageMs
            FrameP99Ms = [double]$report.frameP99Ms
            GpuAverageMs = [double]$report.gpuAverageMs
            GpuP95Ms = [double]$report.gpuP95Ms
            GpuP99Ms = [double]$report.gpuP99Ms
            GpuTimingMode = [string]$report.gpuTimingMode
            GpuTimingScope = [string]$report.gpuTimingScopeVersion
            GpuTimingQueue = [string]$report.gpuTimingQueue
            NativeFrequency = [uint64]$report.nativeTimestampObservedFrequency
            NativeDllSha256 = [string]$report.nativeTimestampDllSha256
            NativeControlP50Ms = [double]$timestampEvidence.ControlP50Ms
            NativeControlP99Ms = [double]$timestampEvidence.ControlP99Ms
            NativeReadbackBytes = [int]$report.nativeTimestampInstrumentationReadbackBytes
            NativeMaxPendingFrames = [int]$report.nativeTimestampMaximumResultPendingFrames
            NativeEvidenceRows = [int]$timestampEvidence.Rows
            FpsAverage = [double]$report.fpsAverage
            LongFrame33Rate = [double]$report.longFrame33Rate
            Loaded2KPages = [int]$report.orthophotoLoadedLod0Pages
            CityResidentPacks = [int]$report.cityResidentPacks
            CityGpuBytes = [long]$report.cityResidentGpuBytes
            OutputHash = [string]$report.compositeOutputHash
            SourceCommit = [string]$report.sourceCommit
            SourceDirty = [bool]$report.sourceDirty
            PlayerSha256 = [string]$report.playerSha256
            PayloadRenders = [int]$report.measuredPayloadRenderCount
            WorkloadInterval = [int]$report.workloadIssueIntervalFrames
            ExpectedWorkloads = [int]$report.expectedWorkloadSubmissions
            ScheduledWorkloads = [int]$report.scheduledWorkloadIssueCount
            SensorSubmissions = [int]$report.sensorSubmitted
            ResidencySubmissions = [int]$report.residencySubmitted
            DeadlineSubmissions = [int]$report.deadlineSubmitted
            UniqueLogicalStates = [int]$report.workloadUniqueLogicalStates
            IssueFrameHash = [string]$report.workloadIssueFrameSequenceHash
            LogicalStateHash = [string]$report.workloadLogicalStateSequenceHash
            WarmupExpected = [int]$report.warmupExpectedWorkloadSubmissions
            WarmupScheduled = [int]$report.warmupScheduledWorkloadIssueCount
            WarmupSensorSubmissions = [int]$report.warmupSensorSubmitted
            WarmupResidencySubmissions = [int]$report.warmupResidencySubmitted
            WarmupDeadlineSubmissions = [int]$report.warmupDeadlineSubmitted
            GpuTimingSamples = [int]$report.gpuTimingValidSamples
            NativeTimestamps = $timestampPath
            Report = $reportPath
        })
    }
}

$rows | Export-Csv -LiteralPath (Join-Path $outputRoot 'cinematic-runs.csv') -NoTypeInformation
$summaryScript = Join-Path $PSScriptRoot 'Summarize-GpuCinematicBenchmarkAB.ps1'
$summaryArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $summaryScript, '-InputDirectory', $outputRoot)
$summaryOptions = @{
    FilePath = 'powershell.exe'
    ArgumentList = $summaryArgs
    WorkingDirectory = $projectRoot
    WindowStyle = 'Hidden'
    Wait = $true
    PassThru = $true
}
$summaryProcess = Start-Process @summaryOptions
if ($summaryProcess.ExitCode -ne 0) {
    throw "Cinematic benchmark summary failed with exit code $($summaryProcess.ExitCode)"
}
Write-Host "GPU cinematic A/B complete: $outputRoot"
