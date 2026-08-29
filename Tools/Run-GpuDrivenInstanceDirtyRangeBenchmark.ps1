[CmdletBinding()]
param(
    [string]$UnityPath =
        'C:\Program Files\Unity\Hub\Editor\6000.5.2f1\Editor\Unity.exe',
    [string]$OutputDirectory,
    [string]$PlayerPath,
    [ValidateRange(0, 16)]
    [int]$DeviceIndex = 0,
    [ValidateRange(5, 1800)]
    [int]$WarmupFrames = 30,
    [ValidateRange(30, 7200)]
    [int]$SampleFrames = 240,
    [ValidateRange(5, 240)]
    [int]$PlayerTimeoutMinutes = 60,
    [switch]$FormalAcceptanceMode
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$suite = 'summit.gpu-driven-instance-upload'
$fullVariant = 'full'
$dirtyVariant = 'dirty'
$seed = 20260830
$stateStride = 48
$maximumDirtyUploadCalls = 16
$viewCount = 1
$drawGroupCount = 1
$visibility = 'visible25'
$formalSampleFrames = 900

function Quote-ProcessArgument {
    param([Parameter(Mandatory = $true)][string]$Value)
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Get-CombinedSha256 {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo[]]$Files,
        [Parameter(Mandatory = $true)][string]$RelativeTo
    )

    $builder = [Text.StringBuilder]::new()
    foreach ($file in @($Files | Sort-Object FullName -Unique)) {
        $relative = $file.FullName.Substring($RelativeTo.Length).
            TrimStart([char[]]@('\', '/')).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file.FullName `
            -Algorithm SHA256).Hash
        [void]$builder.Append($relative)
        [void]$builder.Append("`0")
        [void]$builder.Append($hash)
        [void]$builder.Append("`n")
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $sha.ComputeHash(
                [Text.Encoding]::UTF8.GetBytes($builder.ToString())))).
            Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
}

function Read-KeyValueFile {
    param([Parameter(Mandatory = $true)][string]$Path)

    $result = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        $parts = $line -split '=', 2
        if ($parts.Count -eq 2) {
            $result[$parts[0]] = $parts[1]
        }
    }
    return $result
}

function Require-MapValue {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Map,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Context
    )

    if (-not $Map.ContainsKey($Name)) {
        throw "$Context is missing '$Name'."
    }
    return $Map[$Name]
}

function Require-CsvColumns {
    param(
        [Parameter(Mandatory = $true)][object[]]$Rows,
        [Parameter(Mandatory = $true)][string[]]$Names,
        [Parameter(Mandatory = $true)][string]$Context
    )

    if ($Rows.Count -eq 0) {
        throw "$Context contains no rows."
    }
    $observed = [string[]]@($Rows[0].PSObject.Properties.Name)
    foreach ($name in $Names) {
        if ($observed -cnotcontains $name) {
            throw "$Context is missing CSV column '$name'."
        }
    }
}

function Number {
    param([Parameter(Mandatory = $true)]$Value)

    return [double]::Parse(
        [string]$Value,
        [Globalization.CultureInfo]::InvariantCulture)
}

function Percentile {
    param(
        [Parameter(Mandatory = $true)][double[]]$Values,
        [Parameter(Mandatory = $true)][ValidateRange(0.0, 1.0)]
        [double]$Quantile
    )

    if ($Values.Count -eq 0) {
        throw 'Cannot compute a percentile of an empty array.'
    }
    $sorted = [double[]]@($Values | Sort-Object)
    if ($sorted.Count -eq 1) {
        return $sorted[0]
    }
    $position = ($sorted.Count - 1) * $Quantile
    $lower = [Math]::Floor($position)
    $upper = [Math]::Ceiling($position)
    if ($lower -eq $upper) {
        return $sorted[[int]$lower]
    }
    $weight = $position - $lower
    return [double](
        $sorted[[int]$lower] * (1.0 - $weight) +
        $sorted[[int]$upper] * $weight)
}

function Get-ChangedInstanceCount {
    param(
        [Parameter(Mandatory = $true)][int]$InstanceCount,
        [Parameter(Mandatory = $true)][int]$MovingPercent
    )

    return [int](
        ([int64]$InstanceCount * [int64]$MovingPercent) / 100L)
}

function Get-RequiredCpuSubmissionValues {
    param(
        [Parameter(Mandatory = $true)][object[]]$Rows,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $values = [Collections.Generic.List[double]]::new()
    foreach ($row in $Rows) {
        $value = Number $row.totalCpuSubmissionMs
        if ([double]::IsNaN($value) -or
            [double]::IsInfinity($value) -or
            $value -le 0.0) {
            throw "$Context has invalid totalCpuSubmissionMs '$value'."
        }
        $values.Add($value)
    }
    return [double[]]$values.ToArray()
}

function Assert-UploadRows {
    param(
        [Parameter(Mandatory = $true)][object[]]$Rows,
        [Parameter(Mandatory = $true)][int]$InstanceCount,
        [Parameter(Mandatory = $true)][int]$MovingPercent,
        [Parameter(Mandatory = $true)][int]$SampleFrames,
        [Parameter(Mandatory = $true)][string]$ExpectedProcessId,
        [Parameter(Mandatory = $true)][string]$ScenarioId
    )

    $requiredColumns = @(
        'processId',
        'scenarioId',
        'blockIndex',
        'pairIndex',
        'variant',
        'sampleIndex',
        'logicalOrdinal',
        'totalCpuSubmissionMs',
        'mainThreadAllocatedBytes',
        'slotWaitFrames',
        'changedInstanceCount',
        'planRangeCount',
        'inputRangeCount',
        'dirtyRecordCount',
        'dirtyRecordCountExact',
        'uploadedRecordCount',
        'logicalUploadBytes',
        'uploadCallCount',
        'rangePlanHash',
        'updateHash',
        'frameTimingValid',
        'cpuFrameMs',
        'cpuMainThreadFrameMs',
        'measurementReadbackBytes')
    Require-CsvColumns -Rows $Rows -Names $requiredColumns `
        -Context "$ScenarioId raw frames"

    $fullRows = @($Rows | Where-Object {
        $_.variant -ceq $fullVariant
    })
    $dirtyRows = @($Rows | Where-Object {
        $_.variant -ceq $dirtyVariant
    })
    $rowsPerVariant = 4 * $SampleFrames
    if ($Rows.Count -ne 8 * $SampleFrames -or
        $fullRows.Count -ne $rowsPerVariant -or
        $dirtyRows.Count -ne $rowsPerVariant) {
        throw "$ScenarioId has incorrect full/dirty sample cardinality."
    }

    $changedCount = Get-ChangedInstanceCount `
        -InstanceCount $InstanceCount `
        -MovingPercent $MovingPercent
    $fullBytes = [int64]$InstanceCount * $stateStride
    $dirtyBytes = [int64]$changedCount * $stateStride
    foreach ($row in $Rows) {
        if ([string]$row.processId -cne $ExpectedProcessId -or
            [string]$row.scenarioId -cne $ScenarioId -or
            [int64]$row.mainThreadAllocatedBytes -ne 0 -or
            [int]$row.slotWaitFrames -ne 0 -or
            [int]$row.changedInstanceCount -ne $changedCount -or
            [int]$row.dirtyRecordCountExact -ne 1 -or
            [int]$row.frameTimingValid -ne 1 -or
            (Number $row.cpuFrameMs) -le 0.0 -or
            (Number $row.cpuMainThreadFrameMs) -le 0.0 -or
            [int64]$row.measurementReadbackBytes -ne 0 -or
            [string]::IsNullOrWhiteSpace([string]$row.rangePlanHash) -or
            [string]::IsNullOrWhiteSpace([string]$row.updateHash)) {
            throw "$ScenarioId contains an invalid allocation/wait/timing row."
        }
    }

    foreach ($row in $fullRows) {
        if ([int]$row.inputRangeCount -ne 0 -or
            [int]$row.dirtyRecordCount -ne $InstanceCount -or
            [int]$row.uploadedRecordCount -ne $InstanceCount -or
            [int64]$row.logicalUploadBytes -ne $fullBytes -or
            [int]$row.uploadCallCount -ne 1) {
            throw "$ScenarioId full-upload accounting is not exact."
        }
    }
    foreach ($row in $dirtyRows) {
        $calls = [int]$row.uploadCallCount
        $inputRanges = [int]$row.inputRangeCount
        $planRanges = [int]$row.planRangeCount
        $expectedNoOp = $MovingPercent -eq 0
        $expectedFull = $MovingPercent -eq 100
        if ([int]$row.dirtyRecordCount -ne $changedCount -or
            [int]$row.uploadedRecordCount -ne $changedCount -or
            [int64]$row.logicalUploadBytes -ne $dirtyBytes -or
            ($expectedNoOp -and
                ($calls -ne 0 -or
                 $inputRanges -ne 0 -or
                 $planRanges -ne 0)) -or
            (-not $expectedNoOp -and
                ($calls -lt 1 -or
                 $calls -gt $maximumDirtyUploadCalls -or
                 $inputRanges -lt 1 -or
                 $inputRanges -gt $maximumDirtyUploadCalls -or
                 $planRanges -lt 1 -or
                 $planRanges -gt $maximumDirtyUploadCalls)) -or
            ($expectedFull -and
                ($calls -ne 1 -or
                 $inputRanges -ne 1 -or
                 $planRanges -ne 1))) {
            throw "$ScenarioId dirty-upload accounting is not exact."
        }
    }

    $expectedVariants = @(
        $fullVariant,
        $dirtyVariant,
        $dirtyVariant,
        $fullVariant,
        $dirtyVariant,
        $fullVariant,
        $fullVariant,
        $dirtyVariant)
    $blocks = @($Rows | Group-Object blockIndex | Sort-Object {
        [int]$_.Name
    })
    if ($blocks.Count -ne 8) {
        throw "$ScenarioId does not contain the frozen eight blocks."
    }
    for ($index = 0; $index -lt $blocks.Count; $index++) {
        if ([int]$blocks[$index].Name -ne $index + 1 -or
            $blocks[$index].Count -ne $SampleFrames -or
            @($blocks[$index].Group |
                Select-Object -ExpandProperty variant -Unique).Count -ne 1 -or
            [string]$blocks[$index].Group[0].variant -cne
                $expectedVariants[$index]) {
            throw "$ScenarioId ABBA/BAAB upload schedule is invalid."
        }
    }

    $pairs = @($Rows | Group-Object {
        ([string]$_.pairIndex) + ':' + ([string]$_.sampleIndex)
    })
    if ($pairs.Count -ne 4 * $SampleFrames) {
        throw "$ScenarioId does not contain one row pair per pair/sample."
    }
    foreach ($pair in $pairs) {
        $pairFull = @($pair.Group | Where-Object {
            $_.variant -ceq $fullVariant
        })
        $pairDirty = @($pair.Group | Where-Object {
            $_.variant -ceq $dirtyVariant
        })
        if ($pair.Count -ne 2 -or
            $pairFull.Count -ne 1 -or
            $pairDirty.Count -ne 1 -or
            [string]$pairFull[0].logicalOrdinal -cne
                [string]$pairDirty[0].logicalOrdinal -or
            [string]$pairFull[0].updateHash -cne
                [string]$pairDirty[0].updateHash -or
            [string]$pairFull[0].rangePlanHash -cne
                [string]$pairDirty[0].rangePlanHash) {
            throw "$ScenarioId full/dirty update-hash parity failed."
        }
    }

    return [pscustomobject][ordered]@{
        FullRows = $fullRows
        DirtyRows = $dirtyRows
        ChangedCount = $changedCount
        FullBytes = $fullBytes
        DirtyBytes = $dirtyBytes
    }
}

function Get-OptionalCsvValue {
    param(
        [Parameter(Mandatory = $true)]$Row,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $property = $Row.PSObject.Properties[$Name]
    if ($null -eq $property -or
        [string]::IsNullOrWhiteSpace([string]$property.Value)) {
        return 'unavailable'
    }
    return [string]$property.Value
}

function Test-ArgumentPair {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Value
    )

    for ($index = 0; $index + 1 -lt $Arguments.Count; $index++) {
        if ([string]$Arguments[$index] -ceq $Name -and
            [string]$Arguments[$index + 1] -ceq $Value) {
            return $true
        }
    }
    return $false
}

function Assert-ValidationParity {
    param(
        [Parameter(Mandatory = $true)][object[]]$Rows,
        [Parameter(Mandatory = $true)][string]$ScenarioId,
        [Parameter(Mandatory = $true)][int]$InstanceCount
    )

    Require-CsvColumns -Rows $Rows `
        -Names @(
            'blockIndex',
            'pairIndex',
            'variant',
            'completionFencePassed',
            'expectedStateHash',
            'actualStateHash',
            'passed',
            'readbackBytes') `
        -Context "$ScenarioId validation"
    $expectedReadbackBytes = [int64]$InstanceCount * $stateStride
    if ($Rows.Count -ne 8 -or
        @($Rows | Where-Object {
            [int]$_.passed -ne 1 -or
            [int]$_.completionFencePassed -ne 1 -or
            [int64]$_.readbackBytes -ne $expectedReadbackBytes -or
            [string]::IsNullOrWhiteSpace(
                [string]$_.expectedStateHash) -or
            [string]$_.expectedStateHash -cne
                [string]$_.actualStateHash
        }).Count -ne 0) {
        throw "$ScenarioId contains a failed validation row."
    }
    $pairs = @($Rows | Group-Object pairIndex)
    if ($pairs.Count -ne 4) {
        throw "$ScenarioId validation does not contain four pairs."
    }
    foreach ($pair in $pairs) {
        $full = @($pair.Group | Where-Object {
            $_.variant -ceq $fullVariant
        })
        $dirty = @($pair.Group | Where-Object {
            $_.variant -ceq $dirtyVariant
        })
        if ($pair.Count -ne 2 -or
            $full.Count -ne 1 -or
            $dirty.Count -ne 1 -or
            [string]$full[0].actualStateHash -cne
                [string]$dirty[0].actualStateHash) {
            throw "$ScenarioId state-hash parity failed in pair $($pair.Name)."
        }
    }
}

if ($FormalAcceptanceMode) {
    if ($SampleFrames -ne $formalSampleFrames) {
        throw "Formal mode requires SampleFrames=$formalSampleFrames."
    }
    if ($WarmupFrames -ne 30) {
        throw 'Formal mode requires WarmupFrames=30.'
    }
}

$projectRoot = [IO.Path]::GetFullPath(
    (Split-Path -Parent $PSScriptRoot))
$provenanceModulePath =
    Join-Path $PSScriptRoot 'GpuBenchmarkProvenance.psm1'
Import-Module -Name $provenanceModulePath -Force
$gitStart = Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
if ([bool]$gitStart.dirty) {
    throw "Benchmark requires a clean worktree:`n" +
        (@($gitStart.statusLines) -join "`n")
}
if ([string]$gitStart.branch -ceq 'HEAD') {
    throw 'Benchmark requires a named Git branch.'
}
$gitCommit = [string]$gitStart.head

$projectVersionPath =
    Join-Path $projectRoot 'ProjectSettings\ProjectVersion.txt'
$versionLine = Get-Content -LiteralPath $projectVersionPath |
    Where-Object { $_ -like 'm_EditorVersion:*' } |
    Select-Object -First 1
$projectUnityVersion = ($versionLine -split ':', 2)[1].Trim()
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Unity editor is missing: $UnityPath"
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $OutputDirectory = Join-Path $projectRoot (
        "Reports\GpuDrivenInstanceDirtyRangeBenchmark\matrix-$stamp")
}
elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot $OutputDirectory
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) {
    if ($null -ne (Get-ChildItem -LiteralPath $outputRoot -Force |
            Select-Object -First 1)) {
        throw 'Benchmark report directory must be new or empty.'
    }
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($PlayerPath)) {
    $PlayerPath = Join-Path $projectRoot (
        'Builds\GpuDrivenInstanceDirtyRangeBenchmark\' +
        'GpuDrivenInstanceDirtyRangeBenchmark.exe')
}
elseif (-not [IO.Path]::IsPathRooted($PlayerPath)) {
    $PlayerPath = Join-Path $projectRoot $PlayerPath
}
$resolvedPlayerPath = [IO.Path]::GetFullPath($PlayerPath)
$playerPayloadRoot = Split-Path -Parent $resolvedPlayerPath
if ($outputRoot.Equals(
        $playerPayloadRoot,
        [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith(
        $playerPayloadRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Report output may not be inside the Player payload.'
}

$benchmarkRoot =
    Join-Path $projectRoot 'Assets\GpuDrivenInstanceBenchmark'
$benchmarkRuntime = Join-Path $benchmarkRoot 'Runtime'
$instanceRuntime =
    Join-Path $projectRoot 'Packages\com.summit.gpu-driven-instances\Runtime'
$binningRuntime =
    Join-Path $projectRoot 'Packages\com.summit.gpu-direct-binning\Runtime'
$primitiveRuntime =
    Join-Path $projectRoot 'Packages\com.summit.gpu-primitives\Runtime'
$buildScriptPath = Join-Path $benchmarkRoot (
    'Editor\GpuDrivenInstanceMacrobenchmarkBuild.cs')
$runnerTestPath = Join-Path $PSScriptRoot (
    'Tests\Test-GpuDrivenInstanceDirtyRangeBenchmarkProvenance.ps1')
$runtimeShaderPath = Join-Path $instanceRuntime (
    'Resources\GpuDrivenInstances\GpuDrivenInstances.compute')
$macroShaderPath = Join-Path $benchmarkRuntime (
    'Resources\GpuDrivenInstanceBenchmark\GpuDrivenInstanceMacro.shader')
foreach ($path in @(
        $provenanceModulePath,
        $buildScriptPath,
        $runnerTestPath,
        $runtimeShaderPath,
        $macroShaderPath,
        $projectVersionPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required benchmark input is missing: $path"
    }
}

function Get-BenchmarkSourceFiles {
    $files = @(
        Get-ChildItem -LiteralPath $benchmarkRoot -Recurse -File |
            Where-Object {
                $_.Extension -in @('.cs', '.compute', '.shader', '.asmdef')
            }
        Get-ChildItem -LiteralPath $instanceRuntime -Recurse -File |
            Where-Object {
                $_.Extension -in @('.cs', '.compute', '.asmdef')
            }
        Get-ChildItem -LiteralPath $binningRuntime -Recurse -File |
            Where-Object {
                $_.Extension -in @('.cs', '.compute', '.asmdef')
            }
        Get-ChildItem -LiteralPath $primitiveRuntime -Recurse -File |
            Where-Object {
                $_.Extension -in @('.cs', '.compute', '.asmdef')
            }
        Get-Item -LiteralPath $PSCommandPath
        Get-Item -LiteralPath $runnerTestPath
        Get-Item -LiteralPath $provenanceModulePath
        Get-Item -LiteralPath $projectVersionPath
        Get-Item -LiteralPath (
            Join-Path $projectRoot 'Packages\manifest.json')
        Get-Item -LiteralPath (
            Join-Path $projectRoot 'Packages\packages-lock.json'))
    return [IO.FileInfo[]]$files
}

$sourceSnapshotSha256 = Get-CombinedSha256 `
    -Files (Get-BenchmarkSourceFiles) `
    -RelativeTo $projectRoot
$runtimeShaderSha256 =
    (Get-FileHash -LiteralPath $runtimeShaderPath -Algorithm SHA256).Hash
$macroShaderSha256 =
    (Get-FileHash -LiteralPath $macroShaderPath -Algorithm SHA256).Hash

$scenarios = @()
foreach ($instanceCount in @(10000, 100000)) {
    foreach ($movingPercent in @(0, 1, 10, 100)) {
        $scenarios += [pscustomobject][ordered]@{
            scenarioId =
                "n$instanceCount-moving$movingPercent-v1-g1-visible25"
            instanceCount = $instanceCount
            movingPercent = $movingPercent
            viewCount = $viewCount
            drawGroupCount = $drawGroupCount
            visibility = $visibility
            seed = $seed
        }
    }
}

$runnerConfig = [ordered]@{
    schemaVersion = 1
    suite = $suite
    formalAcceptanceMode = [bool]$FormalAcceptanceMode
    scenarios = $scenarios
    projectRoot = $projectRoot
    projectUnityVersion = $projectUnityVersion
    unityPath = [IO.Path]::GetFullPath($UnityPath)
    playerPath = $resolvedPlayerPath
    outputDirectory = $outputRoot
    deviceIndex = $DeviceIndex
    warmupFrames = $WarmupFrames
    sampleFrames = $SampleFrames
    seed = $seed
    gitCommit = $gitCommit
    gitBranch = [string]$gitStart.branch
    gitStart = $gitStart
    sourceSnapshotSha256 = $sourceSnapshotSha256
    runtimeShaderSha256 = $runtimeShaderSha256
    macroShaderSha256 = $macroShaderSha256
    sourceHashesStableAcrossBuild = $false
    playerPayload = $null
    playerPayloadStableThroughRun = $false
    playerRuns = @()
    evidenceValid = $false
    performanceGateApplied = $false
    hundredPercentNonInferiority = 'reported-only'
    startedUtc = (Get-Date).ToUniversalTime().ToString('o')
    finalizedUtc = ''
}
$runnerConfigPath = Join-Path $outputRoot 'runner-config.json'
$runnerConfig | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath $runnerConfigPath -Encoding utf8

$buildLog = Join-Path $outputRoot 'unity-build.log'
New-Item -ItemType Directory -Path $playerPayloadRoot -Force |
    Out-Null
$buildArguments = @(
    '-batchmode',
    '-nographics',
    '-quit',
    '-projectPath', (Quote-ProcessArgument $projectRoot),
    '-executeMethod',
        'GpuDrivenInstanceMacrobenchmarkBuild.PerformBuild',
    '-gpu-driven-instance-macro-player-path',
        (Quote-ProcessArgument $resolvedPlayerPath),
    '-logFile', (Quote-ProcessArgument $buildLog))
$build = Start-Process `
    -FilePath $UnityPath `
    -ArgumentList $buildArguments `
    -WorkingDirectory $projectRoot `
    -WindowStyle Hidden `
    -Wait `
    -PassThru
if ($build.ExitCode -ne 0) {
    throw "Unity Player build failed; see $buildLog"
}
if (-not (Test-Path -LiteralPath $resolvedPlayerPath -PathType Leaf)) {
    throw "Benchmark Player is missing: $resolvedPlayerPath"
}

$postBuildGit = Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
if ($postBuildGit.head -ine $gitCommit -or [bool]$postBuildGit.dirty) {
    throw 'Git state changed during the Player build.'
}
$postBuildSourceHash = Get-CombinedSha256 `
    -Files (Get-BenchmarkSourceFiles) `
    -RelativeTo $projectRoot
if ($postBuildSourceHash -cne $sourceSnapshotSha256) {
    throw 'Benchmark source hashes changed during the Player build.'
}
$initialPayload =
    Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
$runnerConfig['sourceHashesStableAcrossBuild'] = $true
$runnerConfig['playerPayload'] = $initialPayload
$initialPayload | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath (
        Join-Path $outputRoot 'player-payload-manifest.json') `
        -Encoding utf8

$matrixRows = [Collections.Generic.List[object]]::new()
$playerRuns = [Collections.Generic.List[object]]::new()
foreach ($scenario in $scenarios) {
    $scenarioRoot = Join-Path $outputRoot $scenario.scenarioId
    New-Item -ItemType Directory -Path $scenarioRoot -Force | Out-Null
    $playerLog = Join-Path $scenarioRoot 'player.log'
    $playerArguments = @(
        '-force-d3d12',
        '-force-device-index', [string]$DeviceIndex,
        '-screen-fullscreen', '0',
        '-screen-width', '640',
        '-screen-height', '360',
        '-gpu-driven-instance-upload-benchmark',
        '-gpu-driven-instance-upload-output',
            (Quote-ProcessArgument $scenarioRoot),
        '-gpu-driven-instance-upload-scenario-id',
            (Quote-ProcessArgument $scenario.scenarioId),
        '-gpu-driven-instance-upload-instance-count',
            [string]$scenario.instanceCount,
        '-gpu-driven-instance-upload-moving-percent',
            [string]$scenario.movingPercent,
        '-gpu-driven-instance-upload-seed', [string]$scenario.seed,
        '-gpu-driven-instance-upload-warmup', [string]$WarmupFrames,
        '-gpu-driven-instance-upload-sample', [string]$SampleFrames,
        '-gpu-driven-instance-upload-build-commit', $gitCommit,
        '-gpu-driven-instance-upload-runtime-shader-sha256',
            $runtimeShaderSha256,
        '-gpu-driven-instance-upload-render-shader-sha256',
            $macroShaderSha256,
        '-logFile', (Quote-ProcessArgument $playerLog))

    Write-Host (
        "Running $($scenario.scenarioId): N=$($scenario.instanceCount), " +
        "moving=$($scenario.movingPercent)%")
    $player = Start-Process `
        -FilePath $resolvedPlayerPath `
        -ArgumentList $playerArguments `
        -WorkingDirectory $projectRoot `
        -PassThru
    if (-not $player.WaitForExit(
            $PlayerTimeoutMinutes * 60 * 1000)) {
        $player.Kill()
        $player.WaitForExit()
        throw "Scenario '$($scenario.scenarioId)' timed out."
    }
    if ($player.ExitCode -ne 0) {
        throw "Scenario '$($scenario.scenarioId)' failed; see $playerLog"
    }

    $runSummaryPath = Join-Path $scenarioRoot 'run-summary.txt'
    $configPath = Join-Path $scenarioRoot 'config.json'
    $configurationPath = Join-Path $scenarioRoot 'configuration.json'
    $devicePath = Join-Path $scenarioRoot 'device.json'
    $rawPath = Join-Path $scenarioRoot 'raw-frames.csv'
    $blockPath = Join-Path $scenarioRoot 'block-summary.csv'
    $validationPath = Join-Path $scenarioRoot 'validation.csv'
    foreach ($path in @(
            $runSummaryPath,
            $configPath,
            $configurationPath,
            $devicePath,
            $rawPath,
            $blockPath,
            $validationPath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Scenario output is incomplete: $path"
        }
    }

    $run = Read-KeyValueFile -Path $runSummaryPath
    $processId = [string](Require-MapValue $run 'processId' `
        $scenario.scenarioId)
    $expectedRawRows = 8 * $SampleFrames
    if ([int](Require-MapValue $run 'passed' $scenario.scenarioId) -ne 1 -or
        [string](Require-MapValue $run 'status' $scenario.scenarioId) -cne
            'passed' -or
        [int](Require-MapValue $run 'rawFrameCount' `
            $scenario.scenarioId) -ne $expectedRawRows -or
        [int](Require-MapValue $run 'measurementBlockCount' `
            $scenario.scenarioId) -ne 8 -or
        [int](Require-MapValue $run 'mainThreadAllocationRows' `
            $scenario.scenarioId) -ne 0 -or
        [int64](Require-MapValue $run 'mainThreadAllocatedBytes' `
            $scenario.scenarioId) -ne 0 -or
        [int](Require-MapValue $run 'timedAllocationFree' `
            $scenario.scenarioId) -ne 1 -or
        [int64](Require-MapValue $run 'slotWaitFrames' `
            $scenario.scenarioId) -ne 0 -or
        [int](Require-MapValue $run 'frameTimingReadyRows' `
            $scenario.scenarioId) -ne $expectedRawRows -or
        [int](Require-MapValue $run 'validationCount' `
            $scenario.scenarioId) -ne 8 -or
        [int](Require-MapValue $run 'validationFailures' `
            $scenario.scenarioId) -ne 0 -or
        [int](Require-MapValue $run 'stateValidationPassed' `
            $scenario.scenarioId) -ne 1 -or
        [int](Require-MapValue $run 'completionFencesComplete' `
            $scenario.scenarioId) -ne 1 -or
        [int64](Require-MapValue $run 'measurementReadbackBytes' `
            $scenario.scenarioId) -ne 0) {
        throw "Scenario quality gate failed: $($scenario.scenarioId)"
    }

    $config = Get-Content -LiteralPath $configPath -Raw |
        ConvertFrom-Json
    if ([string]$config.suite -cne $suite -or
        [string]$config.processId -cne $processId -or
        [string]$config.scenarioId -cne $scenario.scenarioId -or
        [int]$config.instanceCount -ne $scenario.instanceCount -or
        [int]$config.movingPercent -ne $scenario.movingPercent -or
        [int]$config.seed -ne $seed -or
        [int]$config.warmupFramesPerBlock -ne $WarmupFrames -or
        [int]$config.sampleFramesPerBlock -ne $SampleFrames -or
        [int]$config.measurementBlocks -ne 8 -or
        [int]$config.superRounds -ne 2 -or
        [string]$config.scheduleContract -cne 'ABBA;BAAB' -or
        [int]$config.frameTimingResultLatencyFrames -ne 4 -or
        [int]$config.viewCount -ne $viewCount -or
        [int]$config.drawGroupCount -ne $drawGroupCount -or
        [string]$config.inputLayout -cne
            'seeded-striped-contiguous-16-v1' -or
        [int64]$config.measurementReadbackBytes -ne 0 -or
        [string]$config.nativeGpuTimestamps -cne 'not-collected' -or
        [string]$config.imageValidation -cne 'not-collected' -or
        -not (Test-ArgumentPair `
            -Arguments ([string[]]@($config.commandLineArguments)) `
            -Name '-gpu-driven-instance-upload-build-commit' `
            -Value $gitCommit) -or
        -not (Test-ArgumentPair `
            -Arguments ([string[]]@($config.commandLineArguments)) `
            -Name '-gpu-driven-instance-upload-runtime-shader-sha256' `
            -Value $runtimeShaderSha256) -or
        -not (Test-ArgumentPair `
            -Arguments ([string[]]@($config.commandLineArguments)) `
            -Name '-gpu-driven-instance-upload-render-shader-sha256' `
            -Value $macroShaderSha256)) {
        throw "Scenario provenance mismatch: $($scenario.scenarioId)"
    }
    $configurationHash =
        (Get-FileHash -LiteralPath $configurationPath -Algorithm SHA256).Hash
    $configHash =
        (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
    if ($configurationHash -cne $configHash) {
        throw "Configuration aliases differ: $($scenario.scenarioId)"
    }

    $device = Get-Content -LiteralPath $devicePath -Raw |
        ConvertFrom-Json
    if ([string]$device.suite -cne $suite -or
        [string]$device.graphicsDeviceType -cne 'Direct3D12' -or
        -not [bool]$device.supportsGraphicsFence -or
        -not [bool]$device.frameTimingFeatureEnabled) {
        throw "Scenario device contract failed: $($scenario.scenarioId)"
    }

    $raw = @(Import-Csv -LiteralPath $rawPath)
    $accounting = Assert-UploadRows `
        -Rows $raw `
        -InstanceCount $scenario.instanceCount `
        -MovingPercent $scenario.movingPercent `
        -SampleFrames $SampleFrames `
        -ExpectedProcessId $processId `
        -ScenarioId $scenario.scenarioId
    $validation = @(Import-Csv -LiteralPath $validationPath)
    Assert-ValidationParity -Rows $validation `
        -ScenarioId $scenario.scenarioId `
        -InstanceCount $scenario.instanceCount

    $blocks = @(Import-Csv -LiteralPath $blockPath)
    Require-CsvColumns -Rows $blocks `
        -Names @(
            'variant',
            'sampleCount',
            'mainThreadAllocationRows',
            'mainThreadAllocatedBytes',
            'slotWaitFrames',
            'frameTimingReadyRows',
            'completionFencePassed',
            'stateValidationPassed') `
        -Context "$($scenario.scenarioId) blocks"
    if ($blocks.Count -ne 8 -or
        @($blocks | Where-Object {
            [int]$_.sampleCount -ne $SampleFrames -or
            [int]$_.mainThreadAllocationRows -ne 0 -or
            [int64]$_.mainThreadAllocatedBytes -ne 0 -or
            [int]$_.slotWaitFrames -ne 0 -or
            [int]$_.frameTimingReadyRows -ne $SampleFrames -or
            [int]$_.completionFencePassed -ne 1 -or
            [int]$_.stateValidationPassed -ne 1
        }).Count -ne 0) {
        throw "Scenario block evidence failed: $($scenario.scenarioId)"
    }

    $fullSubmissionP95 = Percentile `
        -Values (Get-RequiredCpuSubmissionValues `
            -Rows $accounting.FullRows `
            -Context "$($scenario.scenarioId) full") `
        -Quantile 0.95
    $dirtySubmissionP95 = Percentile `
        -Values (Get-RequiredCpuSubmissionValues `
            -Rows $accounting.DirtyRows `
            -Context "$($scenario.scenarioId) dirty") `
        -Quantile 0.95
    $deltaMs = $fullSubmissionP95 - $dirtySubmissionP95
    $improvementPercent = if ($fullSubmissionP95 -gt 0.0) {
        100.0 * $deltaMs / $fullSubmissionP95
    }
    else {
        [double]::NaN
    }
    $matrixRows.Add([pscustomobject][ordered]@{
        scenarioId = $scenario.scenarioId
        instanceCount = $scenario.instanceCount
        movingPercent = $scenario.movingPercent
        changedInstanceCount = $accounting.ChangedCount
        fullLogicalUploadBytes = $accounting.FullBytes
        dirtyLogicalUploadBytes = $accounting.DirtyBytes
        resultHashParity = 'unavailable'
        imageHashParity = 'unavailable'
        fullCpuSubmissionP95Ms = $fullSubmissionP95
        dirtyCpuSubmissionP95Ms = $dirtySubmissionP95
        cpuSubmissionP95DeltaMs = $deltaMs
        cpuSubmissionP95ImprovementPercent = $improvementPercent
        hundredPercentNonInferiority = if (
            $scenario.movingPercent -eq 100) {
            'reported-only'
        }
        else {
            'not-applicable'
        }
        evidencePassed = 1
    })
    $playerRuns.Add([pscustomobject][ordered]@{
        scenarioId = $scenario.scenarioId
        processId = $processId
        exitCode = $player.ExitCode
        playerLog = $playerLog
        scenarioDirectory = $scenarioRoot
    })
}

$matrixPath = Join-Path $outputRoot 'matrix-summary.csv'
$matrixRows | Export-Csv -LiteralPath $matrixPath `
    -NoTypeInformation -Encoding utf8

$finalGit = Get-GpuBenchmarkGitSnapshot -ProjectRoot $projectRoot
if ($finalGit.head -ine $gitCommit -or [bool]$finalGit.dirty) {
    throw 'Git state changed during benchmark execution.'
}
$finalSourceHash = Get-CombinedSha256 `
    -Files (Get-BenchmarkSourceFiles) `
    -RelativeTo $projectRoot
if ($finalSourceHash -cne $sourceSnapshotSha256) {
    throw 'Benchmark source hashes changed during execution.'
}
$finalPayload =
    Get-GpuBenchmarkPlayerPayload -PlayerPath $resolvedPlayerPath
$payloadStable =
    [string]$finalPayload.sha256 -ceq [string]$initialPayload.sha256 -and
    [int]$finalPayload.fileCount -eq [int]$initialPayload.fileCount -and
    [int64]$finalPayload.lengthBytes -eq [int64]$initialPayload.lengthBytes
if (-not $payloadStable) {
    throw 'Player payload changed during benchmark execution.'
}

$reportPath = Join-Path $outputRoot 'BENCHMARK_REPORT.md'
$report = [Collections.Generic.List[string]]::new()
$report.Add('# GPU-driven instance dirty-range upload benchmark')
$report.Add('')
$report.Add(
    'All correctness, D3D12, zero-allocation, zero-slot-wait, upload-' +
    'accounting, frame-tail, provenance, and payload gates passed.')
$report.Add(
    'Full/dirty GPU state hashes passed. Result-hash and image-hash ' +
    'evidence are unavailable for this focused upload benchmark.')
$report.Add('CPU submission P95 is descriptive device evidence. No hardware-' +
    'specific performance threshold is used as a pass/fail gate.')
$report.Add('')
$report.Add('| N | Moving | Full bytes | Dirty bytes | Full CPU P95 ms | ' +
    'Dirty CPU P95 ms | Improvement |')
$report.Add('|---:|---:|---:|---:|---:|---:|---:|')
foreach ($row in $matrixRows) {
    $report.Add(
        "| $($row.instanceCount) | $($row.movingPercent)% | " +
        "$($row.fullLogicalUploadBytes) | " +
        "$($row.dirtyLogicalUploadBytes) | " +
        ('{0:F6}' -f $row.fullCpuSubmissionP95Ms) + ' | ' +
        ('{0:F6}' -f $row.dirtyCpuSubmissionP95Ms) + ' | ' +
        ('{0:F2}%' -f $row.cpuSubmissionP95ImprovementPercent) + ' |')
}
$report.Add('')
$report.Add(
    'The 100% moving cells are retained as reported-only non-inferiority ' +
    'evidence; they do not fail the run on an uncalibrated device.')
$report | Set-Content -LiteralPath $reportPath -Encoding utf8

$runnerConfig['playerRuns'] = $playerRuns.ToArray()
$runnerConfig['gitFinal'] = $finalGit
$runnerConfig['playerPayloadStableThroughRun'] = $payloadStable
$runnerConfig['evidenceValid'] = $true
$runnerConfig['finalizedUtc'] =
    (Get-Date).ToUniversalTime().ToString('o')
$runnerConfig | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath $runnerConfigPath -Encoding utf8

Write-Host "Dirty-range benchmark evidence passed: $outputRoot"
Write-Host 'CPU performance deltas are reported only; no device threshold applied.'
