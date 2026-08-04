[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReportDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$CpuVariant = 'cpu-produced-uploaded'
$GpuVariant = 'gpu-produced-resident'
$ControlVariant = 'empty-main-graphics-control'
$CpuCaseId = 'sensor-pipeline/cpu-produced-uploaded-v1'
$GpuCaseId = 'sensor-pipeline/gpu-produced-resident-v1'
$ControlCaseId = 'control/empty-main-graphics-command-buffer'
$CpuMarker = 'GPU.SensorPipeline/CpuProducedUploaded/MainGraphics'
$GpuMarker = 'GPU.SensorPipeline/GpuProducedResident/MainGraphics'
$ControlMarker = 'GPU.SensorPipeline/Control/EmptyMainGraphics'
$FixedBinCount = 262144
$FixedStateCount = 64
$ExpectedSuperRounds = 4
$ExpectedPairCount = 8
$ExpectedBlockCount = 18
$ExpectedValidationRows = 10
$TimestampBytesPerSample = 16
$ValidationBytesPerComparison = 32
$MinimumWinningPairs = 6
$MinimumMedianGpuAverageImprovementPercent = 5.0
$MinimumMedianGpuAverageReductionMs = 0.005
$MinimumMedianGpuP99ImprovementPercent = -2.0
$MaximumControlP99Ms = 0.005

function Require-File {
    param([Parameter(Mandatory = $true)][string]$Path)
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Required evidence file is missing: $resolved"
    }
    return $resolved
}

function Read-Json {
    param([Parameter(Mandatory = $true)][string]$Path)
    $resolved = Require-File $Path
    return Get-Content -LiteralPath $resolved -Raw | ConvertFrom-Json
}

function Read-KeyValue {
    param([Parameter(Mandatory = $true)][string]$Path)
    $resolved = Require-File $Path
    $result = @{}
    foreach ($line in Get-Content -LiteralPath $resolved) {
        $parts = $line -split '=', 2
        if ($parts.Count -ne 2) {
            continue
        }
        if ($result.ContainsKey($parts[0])) {
            throw "Duplicate key '$($parts[0])' in $resolved"
        }
        $result[$parts[0]] = $parts[1]
    }
    return $result
}

function Require-Columns {
    param(
        [Parameter(Mandatory = $true)][object[]]$Rows,
        [Parameter(Mandatory = $true)][string[]]$Columns,
        [Parameter(Mandatory = $true)][string]$Label
    )
    if ($Rows.Count -eq 0) {
        throw "$Label contains no rows."
    }
    $observed = @($Rows[0].PSObject.Properties.Name)
    foreach ($column in $Columns) {
        if ($observed -cnotcontains $column) {
            throw "$Label is missing required column '$column'."
        }
    }
}

function Number {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )
    [double]$parsed = 0.0
    if (-not [double]::TryParse(
            [string]$Value,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed) -or
        [double]::IsNaN($parsed) -or
        [double]::IsInfinity($parsed)) {
        throw "$Label is not a finite number: '$Value'."
    }
    return $parsed
}

function Assert-Near {
    param(
        [Parameter(Mandatory = $true)][double]$Actual,
        [Parameter(Mandatory = $true)][double]$Expected,
        [Parameter(Mandatory = $true)][string]$Label,
        [double]$Tolerance = 0.000000001
    )
    if ([Math]::Abs($Actual - $Expected) -gt $Tolerance) {
        throw (
            "$Label is inconsistent: actual=$Actual expected=$Expected " +
            "tolerance=$Tolerance")
    }
}

function Median {
    param([Parameter(Mandatory = $true)][double[]]$Values)
    if ($Values.Count -eq 0) {
        throw 'Median requires at least one value.'
    }
    [double[]]$sorted = @($Values | Sort-Object)
    $middle = [int]($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return $sorted[$middle]
    }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2.0
}

function Percentile {
    param(
        [Parameter(Mandatory = $true)][double[]]$Values,
        [Parameter(Mandatory = $true)][double]$Percentile
    )
    if ($Values.Count -eq 0) {
        throw 'Percentile requires at least one value.'
    }
    [double[]]$sorted = @($Values | Sort-Object)
    $index = [Math]::Max(
        0,
        [Math]::Min(
            $sorted.Count - 1,
            [int][Math]::Ceiling($sorted.Count * $Percentile) - 1))
    return $sorted[$index]
}

function Improvement {
    param(
        [Parameter(Mandatory = $true)][double]$Baseline,
        [Parameter(Mandatory = $true)][double]$Candidate,
        [Parameter(Mandatory = $true)][string]$Label
    )
    if ($Baseline -le 0.0) {
        throw "$Label baseline must be positive; observed $Baseline."
    }
    return (($Baseline - $Candidate) / $Baseline) * 100.0
}

function Require-Hash {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )
    if ($Value -cnotmatch '^[0-9A-Fa-f]{64}$') {
        throw "$Label is not a SHA-256 digest."
    }
}

function Assert-CurrentFileHash {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $resolved = Require-File $Path
    $actual =
        (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
    if ($actual -cne $Expected) {
        throw "$Label changed after runner provenance capture."
    }
}

function Expected-Blocks {
    param([Parameter(Mandatory = $true)][int]$SuperRounds)
    if ($SuperRounds -lt 1) {
        throw 'SuperRounds must be positive.'
    }
    $result = [System.Collections.Generic.List[object]]::new()
    $blockIndex = 1
    $pairIndex = 1
    $result.Add([pscustomobject]@{
        blockIndex = $blockIndex++
        blockType = 'control-pre'
        variant = $ControlVariant
        caseId = $ControlCaseId
        marker = $ControlMarker
        superRound = 0
        sequencePosition = 0
        pairIndex = 0
        pairOrder = ''
        withinPairPosition = 0
    })
    for ($superRound = 1;
        $superRound -le $SuperRounds;
        $superRound++) {
        $variants = if (($superRound -band 1) -eq 1) {
            @($CpuVariant, $GpuVariant, $GpuVariant, $CpuVariant)
        }
        else {
            @($GpuVariant, $CpuVariant, $CpuVariant, $GpuVariant)
        }
        for ($position = 1; $position -le 4; $position++) {
            $variant = $variants[$position - 1]
            $pairStart =
                [int][Math]::Floor(($position - 1) / 2.0) * 2
            $pairOrder =
                if ($variants[$pairStart] -ceq $CpuVariant) {
                    'AB'
                }
                else {
                    'BA'
                }
            $withinPair = (($position - 1) % 2) + 1
            $caseId = if ($variant -ceq $CpuVariant) {
                $CpuCaseId
            }
            else {
                $GpuCaseId
            }
            $marker = if ($variant -ceq $CpuVariant) {
                $CpuMarker
            }
            else {
                $GpuMarker
            }
            $result.Add([pscustomobject]@{
                blockIndex = $blockIndex++
                blockType = 'measurement'
                variant = $variant
                caseId = $caseId
                marker = $marker
                superRound = $superRound
                sequencePosition = $position
                pairIndex = $pairIndex
                pairOrder = $pairOrder
                withinPairPosition = $withinPair
            })
            if ($withinPair -eq 2) {
                $pairIndex++
            }
        }
    }
    $result.Add([pscustomobject]@{
        blockIndex = $blockIndex
        blockType = 'control-post'
        variant = $ControlVariant
        caseId = $ControlCaseId
        marker = $ControlMarker
        superRound = 0
        sequencePosition = 0
        pairIndex = 0
        pairOrder = ''
        withinPairPosition = 0
    })
    return $result.ToArray()
}

function Assert-BlockMetric {
    param(
        [Parameter(Mandatory = $true)][object[]]$Rows,
        [Parameter(Mandatory = $true)][string]$RawField,
        [Parameter(Mandatory = $true)]$BlockAverage,
        [Parameter(Mandatory = $true)]$BlockP99,
        [Parameter(Mandatory = $true)][string]$Label
    )
    [double[]]$values = @(
        $Rows | ForEach-Object {
            Number $_.$RawField "$Label/$RawField"
        })
    $average = ($values | Measure-Object -Average).Average
    $p99 = Percentile $values 0.99
    Assert-Near (
        Number $BlockAverage "$Label average") $average "$Label average"
    Assert-Near (
        Number $BlockP99 "$Label p99") $p99 "$Label p99"
}

$root = [System.IO.Path]::GetFullPath($ReportDirectory)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Report directory is missing: $root"
}

$runnerPath = Require-File (Join-Path $root 'runner-config.json')
$matrixPath = Require-File (Join-Path $root 'matrix.csv')
$payloadManifestPath =
    Require-File (Join-Path $root 'player-payload-manifest.json')
$runner = Read-Json $runnerPath
$matrix = @(Import-Csv -LiteralPath $matrixPath)
$payloadManifest = Read-Json $payloadManifestPath

if ([int]$runner.schemaVersion -ne 10 -or
    [int]$runner.benchmarkSchemaVersion -ne 10 -or
    [string]$runner.suite -cne 'summit.gpu-sensor-pipeline') {
    throw 'Runner schema/suite contract does not match sensor-pipeline v10.'
}
if (-not [bool]$runner.runnerConfigFinalized -or
    [string]$runner.signedImprovementConvention -cne
        'positive-gpu-resident-faster' -or
    -not [bool]$runner.requireCompleteGpuTimings -or
    [int]$runner.binCount -ne $FixedBinCount -or
    [int]$runner.logicalStateCount -ne $FixedStateCount -or
    [int]$runner.superRounds -ne $ExpectedSuperRounds -or
    [int]$runner.stagingSlotCount -ne 4 -or
    [int]$runner.validationTimeoutSeconds -ne 60 -or
    [bool]$runner.uploadQueueCoverageVerified -or
    [bool]$runner.asyncComputeClaim -or
    [bool]$runner.copyQueueClaim -or
    [int64]$runner.measurementWorkloadReadbackBytes -ne 0 -or
    -not [bool]$runner.timestampInstrumentationReadbackDisclosedSeparately) {
    throw 'Runner fixed benchmark/claim-boundary contract failed.'
}
if (-not [bool]$runner.sourceHashesStableAcrossBuild -or
    -not [bool]$runner.playerPayloadStableThroughRun -or
    [bool]$runner.gitTreeDirty -or
    [bool]$runner.gitStart.dirty -or
    [bool]$runner.gitFinal.dirty -or
    [string]$runner.gitStart.head -cne [string]$runner.gitCommit -or
    [string]$runner.gitFinal.head -cne [string]$runner.gitCommit -or
    [string]$runner.gitStart.branch -ceq 'HEAD' -or
    [string]$runner.gitStart.branch -cne [string]$runner.gitBranch) {
    throw 'Runner clean-commit/source/payload provenance contract failed.'
}
if ([System.IO.Path]::GetFullPath([string]$runner.outputDirectory) -ine
    $root) {
    throw 'Runner outputDirectory does not match the summarized directory.'
}

foreach ($field in @(
    'sourceSnapshotSha256',
    'runtimeShaderSha256',
    'runtimeApiSha256',
    'generatorSha256',
    'runnerToolSha256',
    'summarizerToolSha256',
    'provenanceTestSha256',
    'benchmarkPlanSha256',
    'timestampNativeDllSha256')) {
    Require-Hash ([string]$runner.$field) "runner.$field"
}
if ([int]$runner.sourceFileCount -le 0) {
    throw 'Runner source file inventory is empty.'
}

$projectRoot = [System.IO.Path]::GetFullPath([string]$runner.projectRoot)
Assert-CurrentFileHash (
    Join-Path $projectRoot 'Tools\Run-GpuSensorPipelineBenchmark.ps1') (
    [string]$runner.runnerToolSha256) 'Runner tool'
Assert-CurrentFileHash $PSCommandPath (
    [string]$runner.summarizerToolSha256) 'Summarizer tool'
Assert-CurrentFileHash (
    Join-Path $projectRoot (
        'Tools\Tests\Test-GpuSensorPipelineBenchmarkProvenance.ps1')) (
    [string]$runner.provenanceTestSha256) 'Provenance test'
Assert-CurrentFileHash (
    Join-Path $projectRoot (
        'Docs\GPU_DYNAMIC_SENSOR_PIPELINE_BENCHMARK_PLAN.md')) (
    [string]$runner.benchmarkPlanSha256) 'Benchmark plan'
Assert-CurrentFileHash (
    Join-Path $projectRoot (
        'Packages\com.summit.gpu-timestamps\Runtime\Plugins\x86_64\' +
        'SummitGpuTimestamps.dll')) (
    [string]$runner.timestampNativeDllSha256) 'Native timestamp DLL'

if ([string]$payloadManifest.sha256 -cne
        [string]$runner.playerPayload.sha256 -or
    [int]$payloadManifest.fileCount -ne
        [int]$runner.playerPayload.fileCount -or
    [int64]$payloadManifest.lengthBytes -ne
        [int64]$runner.playerPayload.lengthBytes) {
    throw 'Player payload manifest differs from finalized runner evidence.'
}
Require-Hash ([string]$payloadManifest.sha256) 'player payload'

$formal = [bool]$runner.formalAcceptanceMode
$frozenMatrix =
    [string]$runner.matrixPreset -in @(
        'amd-r9700-dynamic-v1',
        'amd-r9700-dynamic-discovery-v1')
if ($formal) {
    if ([string]$runner.matrixPreset -cne 'amd-r9700-dynamic-v1' -or
        [string]$runner.matrixRole -cne 'formal-holdout' -or
        -not [bool]$runner.formalContractSatisfied -or
        -not [bool]$runner.editModeEvidenceBoundToSource -or
        -not [bool]$runner.sourceHashesStableAcrossEditMode -or
        [int]$runner.deviceIndex -ne 0 -or
        [string]$runner.projectUnityVersion -cne '6000.5.2f1' -or
        [string]$runner.unityEditorResolvedVersion -cne '6000.5.2f1' -or
        [int]$runner.sampleFrames -ne 900 -or
        [int]$runner.warmupFrames -ne 60 -or
        [int]$runner.cooldownFrames -ne 15 -or
        [int]$runner.editModeTimeoutMinutes -ne 30 -or
        [int]$runner.playerTimeoutMinutes -ne 90) {
        throw 'Formal runner contract differs from the frozen v1 holdout.'
    }
    $contract = $runner.formalContract
    if ([string]$contract.unityVersion -cne '6000.5.2f1' -or
        [int]$contract.deviceIndex -ne 0 -or
        [string]$contract.matrixPreset -cne
            'amd-r9700-dynamic-v1' -or
        [int]$contract.binCount -ne $FixedBinCount -or
        [int]$contract.stateCount -ne $FixedStateCount -or
        [int]$contract.stagingSlotCount -ne 4 -or
        [int]$contract.superRounds -ne $ExpectedSuperRounds -or
        [int]$contract.warmupFrames -ne 60 -or
        [int]$contract.sampleFrames -ne 900 -or
        [int]$contract.cooldownFrames -ne 15 -or
        [int]$contract.editModeTimeoutMinutes -ne 30 -or
        [int]$contract.validationTimeoutSeconds -ne 60 -or
        [int]$contract.playerTimeoutMinutes -ne 90) {
        throw 'Formal contract metadata is not the frozen v1 contract.'
    }
}
elseif ([string]$runner.matrixPreset -ceq
    'amd-r9700-dynamic-discovery-v1') {
    if ([string]$runner.matrixRole -cne 'discovery' -or
        [int]$runner.deviceIndex -ne 0 -or
        [string]$runner.projectUnityVersion -cne '6000.5.2f1' -or
        [string]$runner.unityEditorResolvedVersion -cne '6000.5.2f1' -or
        [int]$runner.sampleFrames -ne 240 -or
        [int]$runner.warmupFrames -ne 60 -or
        [int]$runner.cooldownFrames -ne 15 -or
        [int]$runner.validationTimeoutSeconds -ne 60) {
        throw 'Discovery runner contract differs from the frozen v1 contract.'
    }
    $contract = $runner.discoveryContract
    if ([string]$contract.unityVersion -cne '6000.5.2f1' -or
        [int]$contract.deviceIndex -ne 0 -or
        [string]$contract.matrixPreset -cne
            'amd-r9700-dynamic-discovery-v1' -or
        [int]$contract.stagingSlotCount -ne 4 -or
        [int]$contract.superRounds -ne $ExpectedSuperRounds -or
        [int]$contract.warmupFrames -ne 60 -or
        [int]$contract.sampleFrames -ne 240 -or
        [int]$contract.cooldownFrames -ne 15 -or
        [int]$contract.validationTimeoutSeconds -ne 60) {
        throw 'Discovery contract metadata is not the frozen v1 contract.'
    }
}
elseif ([string]$runner.matrixRole -cne 'custom') {
    throw 'Non-frozen runner must declare matrixRole=custom.'
}

$expectedEditModeIdentities = @(
    'Summit.GpuSensorPipeline.Tests.Editor.dll',
    'Summit.GpuSensorPipeline.Tests.GpuSensorDeterministicGeneratorTests',
    'Summit.GpuSensorPipeline.Tests.GpuSensorPipelineContractTests',
    'Summit.GpuSensorPipeline.Tests.GpuSensorPipelineIntegrationTests',
    'Summit.GpuSensorPipeline.Benchmark.Tests.Editor.dll',
    'Summit.GpuSensorPipeline.Benchmark.Tests.GpuSensorPipelineBenchmarkScheduleTests'
)
if ($formal) {
    $edit = $runner.editModeResults
    if ($null -eq $edit -or
        [string]$edit.evidenceOrigin -cne
            'runner-generated-unity-editmode' -or
        -not [bool]$edit.executedByRunner -or
        [string]$edit.gitCommit -cne [string]$runner.gitCommit -or
        [string]$edit.sourceSnapshotSha256 -cne
            [string]$runner.sourceSnapshotSha256 -or
        [string]$edit.unityEditorResolvedVersion -cne '6000.5.2f1' -or
        [string]$edit.testPlatform -cne 'EditMode' -or
        [string]$edit.graphicsApi -cne 'Direct3D12' -or
        [int]$edit.deviceIndex -ne 0 -or
        [int]$edit.timeoutMinutes -ne 30 -or
        [int]$edit.exitCode -ne 0 -or
        [string]$edit.result -cne 'Passed' -or
        [int]$edit.total -le 0 -or
        [int]$edit.passed -ne [int]$edit.total -or
        [int]$edit.failed -ne 0 -or
        [int]$edit.skipped -ne 0 -or
        [int]$edit.inconclusive -ne 0 -or
        [string]$edit.identityMatchMode -cne 'exact-nunit-node-v1' -or
        @($edit.missingIdentities).Count -ne 0) {
        throw 'Formal EditMode metadata is incomplete or not source-bound.'
    }
    foreach ($identity in $expectedEditModeIdentities) {
        if ([string[]]@($edit.expectedIdentities) -cnotcontains $identity -or
            [string[]]@($edit.observedIdentities) -cnotcontains $identity) {
            throw "Formal EditMode evidence lacks exact identity '$identity'."
        }
    }
    $editXmlPath = Require-File (Join-Path $root 'editmode-results.xml')
    $editLogPath = Require-File (Join-Path $root 'unity-editmode.log')
    Assert-CurrentFileHash $editXmlPath (
        [string]$edit.copiedSha256) 'Formal EditMode XML'
    Assert-CurrentFileHash $editLogPath (
        [string]$edit.logSha256) 'Formal EditMode log'
    if ([string]$edit.sourceSha256 -cne
        [string]$edit.copiedSha256) {
        throw 'Formal EditMode XML source/copy hashes differ.'
    }
    [xml]$editXml = Get-Content -LiteralPath $editXmlPath -Raw
    $xmlRun = $editXml.'test-run'
    if ($null -eq $xmlRun -or
        [string]$xmlRun.result -cne 'Passed' -or
        [int]$xmlRun.total -ne [int]$edit.total -or
        [int]$xmlRun.passed -ne [int]$edit.passed -or
        [int]$xmlRun.failed -ne 0 -or
        [int]$xmlRun.skipped -ne 0 -or
        [int]$xmlRun.inconclusive -ne 0) {
        throw 'Formal EditMode XML is not the fully passed captured run.'
    }
}

Require-Columns $matrix @(
    'scenarioId','elementCount','binCount','queryCount',
    'logicalStateCount','seed','processId','status','passed',
    'rawSampleCount') 'matrix.csv'
$runnerScenarios = @($runner.scenarios)
$playerRuns = @($runner.playerRuns)
if ($matrix.Count -ne $runnerScenarios.Count -or
    $matrix.Count -ne $playerRuns.Count -or
    $matrix.Count -lt 1) {
    throw 'Runner, matrix, and Player-run scenario counts differ.'
}
if ($frozenMatrix -and $matrix.Count -ne 2) {
    throw 'Frozen AMD matrices require exactly two scenarios.'
}

$expectedFrozenScenarios = [ordered]@{
    'dynamic-n262144-q64' =
        '262144|262144|64|64|20260731'
    'dynamic-n1048576-q256' =
        '1048576|262144|256|64|20260732'
}
$matrixScenarioIds = @(
    $matrix | Select-Object -ExpandProperty scenarioId)
if (($matrixScenarioIds | Select-Object -Unique).Count -ne
    $matrixScenarioIds.Count) {
    throw 'matrix.csv contains duplicate scenario IDs.'
}
if ($frozenMatrix) {
    foreach ($scenarioId in $expectedFrozenScenarios.Keys) {
        $row = @($matrix | Where-Object {
            [string]$_.scenarioId -ceq $scenarioId
        })
        if ($row.Count -ne 1) {
            throw "Frozen matrix lacks scenario '$scenarioId'."
        }
        $observed =
            "$($row[0].elementCount)|$($row[0].binCount)|" +
            "$($row[0].queryCount)|$($row[0].logicalStateCount)|" +
            "$($row[0].seed)"
        if ($observed -cne $expectedFrozenScenarios[$scenarioId]) {
            throw "Frozen scenario '$scenarioId' parameters changed."
        }
    }
}

$allPairRows = [System.Collections.Generic.List[object]]::new()
$scenarioRows = [System.Collections.Generic.List[object]]::new()
$scenarioDetails = [System.Collections.Generic.List[object]]::new()
$activeDeviceIdentity = $null

foreach ($matrixRow in $matrix) {
    $scenarioId = [string]$matrixRow.scenarioId
    $scenarioRoot = Join-Path $root $scenarioId
    if (-not (Test-Path -LiteralPath $scenarioRoot -PathType Container)) {
        throw "Scenario directory is missing: $scenarioRoot"
    }
    $configPath = Require-File (Join-Path $scenarioRoot 'config.json')
    $devicePath = Require-File (Join-Path $scenarioRoot 'device.json')
    $rawPath = Require-File (Join-Path $scenarioRoot 'raw-frames.csv')
    $blockPath = Require-File (Join-Path $scenarioRoot 'block-summary.csv')
    $validationPath =
        Require-File (Join-Path $scenarioRoot 'validation.csv')
    $runPath = Require-File (Join-Path $scenarioRoot 'run-summary.txt')
    $config = Read-Json $configPath
    $device = Read-Json $devicePath
    $raw = @(Import-Csv -LiteralPath $rawPath)
    $blocks = @(Import-Csv -LiteralPath $blockPath)
    $validation = @(Import-Csv -LiteralPath $validationPath)
    $run = Read-KeyValue $runPath

    $runnerScenario = @($runnerScenarios | Where-Object {
        [string]$_.scenarioId -ceq $scenarioId
    })
    $playerRun = @($playerRuns | Where-Object {
        [string]$_.scenarioId -ceq $scenarioId
    })
    if ($runnerScenario.Count -ne 1 -or $playerRun.Count -ne 1) {
        throw "Scenario '$scenarioId' is not uniquely bound in runner evidence."
    }
    if ([int]$matrixRow.passed -ne 1 -or
        [string]$matrixRow.status -cne 'completed' -or
        [int]$playerRun[0].exitCode -ne 0 -or
        [int]$playerRun[0].processId -ne [int]$matrixRow.processId -or
        [System.IO.Path]::GetFullPath(
            [string]$playerRun[0].reportDirectory) -ine
            [System.IO.Path]::GetFullPath($scenarioRoot)) {
        throw "Scenario '$scenarioId' runner/Player completion evidence failed."
    }

    if ([int]$config.schemaVersion -ne 10 -or
        [string]$config.suite -cne 'summit.gpu-sensor-pipeline' -or
        [string]$config.scenarioId -cne $scenarioId -or
        [int]$config.processId -ne [int]$matrixRow.processId -or
        [int]$device.processId -ne [int]$matrixRow.processId -or
        [int]$config.elementCount -ne [int]$matrixRow.elementCount -or
        [int]$config.binCount -ne [int]$matrixRow.binCount -or
        [int]$config.queryCount -ne [int]$matrixRow.queryCount -or
        [int]$config.logicalStateCount -ne
            [int]$matrixRow.logicalStateCount -or
        [int]$config.seed -ne [int]$matrixRow.seed -or
        [int]$config.superRounds -ne [int]$runner.superRounds -or
        [int]$config.warmupFrames -ne [int]$runner.warmupFrames -or
        [int]$config.sampleFrames -ne [int]$runner.sampleFrames -or
        [int]$config.cooldownFrames -ne [int]$runner.cooldownFrames -or
        [int]$config.stagingSlotCount -ne
            [int]$runner.stagingSlotCount) {
        throw "Scenario '$scenarioId' config/matrix/runner binding failed."
    }
    if ([int]$config.binCount -ne $FixedBinCount -or
        [int]$config.logicalStateCount -ne $FixedStateCount -or
        [string]$config.scanBackend -cne 'wave-ops' -or
        [bool]$config.profilerMarkers -or
        [string]$config.cpuCaseId -cne $CpuCaseId -or
        [string]$config.gpuCaseId -cne $GpuCaseId -or
        [string]$config.controlCaseId -cne $ControlCaseId -or
        -not [bool]$config.caseLocalWarmup -or
        -not [bool]$config.sameProcessPaired -or
        -not [bool]$config.informationalPlayerLogsSuppressed -or
        -not [bool]$config.mainGraphicsQueueTimestamp -or
        [bool]$config.uploadQueueCoverageVerified -or
        [bool]$config.asyncComputeClaim -or
        [bool]$config.copyQueueClaim -or
        -not [bool]$config.finalStateKeyValidation -or
        -not [bool]$config.allStateDigestComparison -or
        [int]$config.allStateDigestCount -ne $FixedStateCount -or
        [int]$config.expectedValidationRows -ne
            $ExpectedValidationRows -or
        [int]$config.measurementReadbackBytesPerFrame -ne 0 -or
        [int]$config.validationReadbackBytesPerComparison -ne
            $ValidationBytesPerComparison -or
        [int]$config.timestampInstrumentationBytesPerCompletedSample -ne
            $TimestampBytesPerSample -or
        -not [bool]$config.requireCompleteGpuTimings -or
        [string]$config.cpuPipelineRecordMetricCoverage -notmatch
            'not SetBufferData-only') {
        throw "Scenario '$scenarioId' fixed pipeline/claim contract failed."
    }
    if ([string]$config.buildCommit -cne [string]$runner.gitCommit -or
        [string]$config.runtimeShaderSha256 -cne
            [string]$runner.runtimeShaderSha256 -or
        [string]$config.runtimeApiSha256 -cne
            [string]$runner.runtimeApiSha256 -or
        [string]$config.nativeTimestampDllSha256 -cne
            [string]$runner.timestampNativeDllSha256) {
        throw "Scenario '$scenarioId' code/native hash binding failed."
    }

    [int64]$logicalProducerBytes =
        20L * [int64]$config.elementCount
    [int64]$expectedHostStagingBytes =
        [int64]$config.stagingSlotCount * $logicalProducerBytes
    [int64]$expectedOwnedBufferBytes =
        28L * [int64]$config.elementCount +
        8L * [int64]$config.binCount +
        32L * [int64]$config.queryCount +
        1060L
    [int64]$expectedBinnerInternal =
        4L * [int64]$config.binCount
    [int64]$expectedBlockDigestBytes =
        2L * $FixedStateCount * 16L
    if ([int64]$config.cpuUploadLogicalBytesPerFrame -ne
            $logicalProducerBytes -or
        [int64]$config.gpuProducerLogicalWriteBytesPerFrame -ne
            $logicalProducerBytes -or
        [int64]$config.actualBenchmarkHostStagingBytes -ne
            $expectedHostStagingBytes -or
        [int64]$config.cpuCaseRequiredHostStagingBytes -ne
            $expectedHostStagingBytes -or
        [int64]$config.gpuCaseRequiredHostStagingBytes -ne 0 -or
        [int64]$config.pipelineOwnedBufferBytes -ne
            $expectedOwnedBufferBytes -or
        [int64]$config.primitiveScratchBytes -le 0 -or
        [int64]$config.binnerInternalScratchBytes -ne
            $expectedBinnerInternal -or
        [int64]$config.pipelineResidentBytes -ne
            ([int64]$config.pipelineOwnedBufferBytes +
                [int64]$config.primitiveScratchBytes +
                [int64]$config.binnerInternalScratchBytes) -or
        [int64]$config.blockDigestBufferBytes -ne
            $expectedBlockDigestBytes -or
        [int64]$config.actualBenchmarkGpuResidentBytes -ne
            ([int64]$config.pipelineResidentBytes +
                $expectedBlockDigestBytes)) {
        throw "Scenario '$scenarioId' logical resource accounting failed."
    }

    if ([string]$device.graphicsDeviceType -cne 'Direct3D12' -or
        -not [bool]$device.supportsComputeShaders -or
        -not [bool]$device.supportsGraphicsFence -or
        -not [bool]$device.supportsAsyncGpuReadback -or
        -not [bool]$device.mainGraphicsQueueTimestamp -or
        [bool]$device.uploadQueueCoverageVerified -or
        [bool]$device.asyncComputeClaim -or
        [bool]$device.copyQueueClaim) {
        throw "Scenario '$scenarioId' graphics device/claim contract failed."
    }
    if ($frozenMatrix -and
        ([int]$device.graphicsDeviceVendorId -ne 4098 -or
            [string]$device.graphicsDeviceName -cne
                'AMD Radeon AI PRO R9700')) {
        throw (
            "Frozen AMD scenario '$scenarioId' ran on unexpected device " +
            "'$($device.graphicsDeviceName)' vendorId=" +
            "$($device.graphicsDeviceVendorId).")
    }
    $deviceIdentity =
        "$($device.graphicsDeviceType)|$($device.graphicsDeviceVendorId)|" +
        "$($device.graphicsDeviceId)|$($device.graphicsDeviceName)"
    if ($null -eq $activeDeviceIdentity) {
        $activeDeviceIdentity = $deviceIdentity
    }
    elseif ($deviceIdentity -cne $activeDeviceIdentity) {
        throw 'Matrix scenarios used different active graphics devices.'
    }
    if ([int]$config.nativeTimestampAbiVersion -ne 2 -or
        (([uint32]$config.nativeTimestampCapabilityFlags -band 0x1F) -ne
            0x1F) -or
        -not [bool]$config.nativeTimestampWarmupPassed -or
        [string]$config.nativeTimestampWarmupStatus -cne 'ready' -or
        [uint64]$config.nativeTimestampWarmupFrequency -eq 0 -or
        [uint32]$config.nativeTimestampWarmupDeviceGeneration -ne
            [uint32]$config.nativeTimestampDeviceGeneration -or
        [int]$device.nativeTimestampAbiVersion -ne
            [int]$config.nativeTimestampAbiVersion -or
        [uint32]$device.nativeTimestampCapabilityFlags -ne
            [uint32]$config.nativeTimestampCapabilityFlags) {
        throw "Scenario '$scenarioId' native timestamp contract failed."
    }

    Require-Columns $raw @(
        'processId','scenarioId','superRound','sequencePosition',
        'pairIndex','pairOrder','withinPairPosition','blockIndex',
        'blockType','caseId','variant','marker','sampleIndex',
        'logicalState','stagingSlot','stagingWaitFrames',
        'sourceUnityFrame','resultUnityFrame','elapsedSeconds',
        'cpuProducerMs','cpuPipelineRecordMs','cpuCommandRecordMs',
        'submissionCpuMs','logicalUploadBytes',
        'gpuProducerLogicalWriteBytes','nativeTimestampToken',
        'nativeTimestampUserTag','nativeTimestampFlags',
        'nativeTimestampStatus','nativeTimestampBeginTicks',
        'nativeTimestampEndTicks','nativeTimestampElapsedTicks',
        'nativeTimestampFrequency',
        'nativeTimestampElapsedNanoseconds',
        'nativeTimestampFenceValue',
        'nativeTimestampDeviceGeneration','gpuRegionElapsedMs',
        'frameMs','measurementReadbackBytes',
        'timestampInstrumentationReadbackBytes'
    ) "$scenarioId/raw-frames.csv"
    Require-Columns $blocks @(
        'processId','scenarioId','superRound','sequencePosition',
        'pairIndex','pairOrder','withinPairPosition','blockIndex',
        'blockType','caseId','variant','marker','samples',
        'gpuRegionValidSamples','stateCount','logicalUploadBytes',
        'gpuProducerLogicalWriteBytes','stagingWaitFrames',
        'gpuRegionAverageMs','gpuRegionP50Ms','gpuRegionP95Ms',
        'gpuRegionP99Ms','frameAverageMs','frameP99Ms',
        'cpuProducerAverageMs','cpuProducerP99Ms',
        'cpuPipelineRecordAverageMs','cpuPipelineRecordP99Ms',
        'cpuCommandRecordAverageMs','cpuCommandRecordP99Ms',
        'submissionAverageMs','submissionP99Ms','fenceSupported',
        'fencePassed','measurementReadbackBytes',
        'timestampInstrumentationReadbackBytes'
    ) "$scenarioId/block-summary.csv"
    Require-Columns $validation @(
        'phase','passed','message','readbackBytes','resultHash',
        'stateCount','digestMismatchCount','digestDeltaXor',
        'digestDeltaSum0','digestDeltaSum1','cpuInvalidKeyCount',
        'cpuInvalidKeyHash','gpuInvalidKeyCount','gpuInvalidKeyHash'
    ) "$scenarioId/validation.csv"

    $expectedBlocks = @(Expected-Blocks ([int]$config.superRounds))
    if ($expectedBlocks.Count -ne $ExpectedBlockCount -or
        $blocks.Count -ne $expectedBlocks.Count) {
        throw "Scenario '$scenarioId' block count/schedule is invalid."
    }
    $expectedRawCount =
        $ExpectedBlockCount * [int]$config.sampleFrames
    if ($raw.Count -ne $expectedRawCount -or
        [int]$matrixRow.rawSampleCount -ne $expectedRawCount) {
        throw "Scenario '$scenarioId' raw sample count is invalid."
    }
    $processIds = @(
        $raw | Select-Object -ExpandProperty processId -Unique)
    if ($processIds.Count -ne 1 -or
        [int]$processIds[0] -ne [int]$matrixRow.processId) {
        throw "Scenario '$scenarioId' does not have one matching Player PID."
    }

    $configSchedule = [string[]]@($config.schedule)
    if ($configSchedule.Count -ne $expectedBlocks.Count -or
        [string]$config.scheduleContract -cne
            ('control-pre;' + $config.superRounds +
                ' balanced super-rounds;ABBA/BAAB;control-post')) {
        throw "Scenario '$scenarioId' config schedule metadata is invalid."
    }
    for ($i = 0; $i -lt $expectedBlocks.Count; $i++) {
        $expectedScheduleValue =
            "$($expectedBlocks[$i].blockType):" +
            "$($expectedBlocks[$i].variant)"
        if ($configSchedule[$i] -cne $expectedScheduleValue) {
            throw "Scenario '$scenarioId' config schedule order changed."
        }
    }

    $observedTokens =
        [System.Collections.Generic.HashSet[uint64]]::new()
    $observedTags =
        [System.Collections.Generic.HashSet[uint64]]::new()
    [uint64]$previousFence =
        [uint64]$config.nativeTimestampWarmupFenceValue
    [uint64]$previousEndTicks = 0
    [uint64]$observedFrequency = 0
    [int]$previousSourceUnityFrame = -1
    [int]$globalRowIndex = 0

    for ($blockPosition = 0;
        $blockPosition -lt $expectedBlocks.Count;
        $blockPosition++) {
        $expected = $expectedBlocks[$blockPosition]
        $matchingBlocks = @($blocks | Where-Object {
            [int]$_.blockIndex -eq [int]$expected.blockIndex
        })
        if ($matchingBlocks.Count -ne 1) {
            throw "Scenario '$scenarioId' block index is not unique."
        }
        $block = $matchingBlocks[0]
        foreach ($field in @(
            'blockIndex','superRound','sequencePosition','pairIndex',
            'withinPairPosition')) {
            if ([int]$block.$field -ne [int]$expected.$field) {
                throw (
                    "Scenario '$scenarioId' block " +
                    "$($expected.blockIndex) has invalid $field.")
            }
        }
        foreach ($field in @(
            'blockType','pairOrder','variant','caseId','marker')) {
            if ([string]$block.$field -cne [string]$expected.$field) {
                throw (
                    "Scenario '$scenarioId' block " +
                    "$($expected.blockIndex) has invalid $field.")
            }
        }
        if ([int]$block.processId -ne [int]$matrixRow.processId -or
            [string]$block.scenarioId -cne $scenarioId -or
            [int]$block.samples -ne [int]$config.sampleFrames -or
            [int]$block.gpuRegionValidSamples -ne
                [int]$config.sampleFrames -or
            [int]$block.stateCount -ne $FixedStateCount -or
            [int]$block.fenceSupported -ne 1 -or
            [int]$block.fencePassed -ne 1 -or
            [int64]$block.measurementReadbackBytes -ne 0 -or
            [int64]$block.timestampInstrumentationReadbackBytes -ne
                ([int64]$config.sampleFrames *
                    $TimestampBytesPerSample)) {
            throw "Scenario '$scenarioId' block completion/readback failed."
        }

        $rows = @(
            $raw | Where-Object {
                [int]$_.blockIndex -eq [int]$expected.blockIndex
            } | Sort-Object { [int]$_.sampleIndex })
        if ($rows.Count -ne [int]$config.sampleFrames) {
            throw "Scenario '$scenarioId' has an incomplete raw block."
        }
        [int64]$expectedBlockUpload = 0
        [int64]$expectedBlockGpuWrites = 0
        if ($expected.variant -ceq $CpuVariant) {
            $expectedBlockUpload =
                $logicalProducerBytes * [int64]$config.sampleFrames
        }
        elseif ($expected.variant -ceq $GpuVariant) {
            $expectedBlockGpuWrites =
                $logicalProducerBytes * [int64]$config.sampleFrames
        }
        if ([int64]$block.logicalUploadBytes -ne
                $expectedBlockUpload -or
            [int64]$block.gpuProducerLogicalWriteBytes -ne
                $expectedBlockGpuWrites -or
            [int]$block.stagingWaitFrames -ne
                (@($rows | ForEach-Object {
                    [int]$_.stagingWaitFrames
                }) | Measure-Object -Sum).Sum) {
            throw "Scenario '$scenarioId' block byte/staging accounting failed."
        }

        [double[]]$blockGpu = @()
        for ($sample = 1; $sample -le $rows.Count; $sample++) {
            $row = $rows[$sample - 1]
            foreach ($field in @(
                'superRound','sequencePosition','pairIndex',
                'withinPairPosition','blockIndex')) {
                if ([int]$row.$field -ne [int]$expected.$field) {
                    throw (
                        "Scenario '$scenarioId' raw row has invalid $field.")
                }
            }
            foreach ($field in @(
                'blockType','pairOrder','variant','caseId','marker')) {
                if ([string]$row.$field -cne [string]$expected.$field) {
                    throw (
                        "Scenario '$scenarioId' raw row has invalid $field.")
                }
            }
            if ([int]$row.processId -ne [int]$matrixRow.processId -or
                [string]$row.scenarioId -cne $scenarioId -or
                [int]$row.sampleIndex -ne $sample -or
                [int]$row.logicalState -ne
                    (($sample - 1) % $FixedStateCount) -or
                [int]$row.stagingSlot -lt 0 -or
                [int]$row.stagingSlot -ge
                    [int]$config.stagingSlotCount -or
                [int]$row.stagingWaitFrames -lt 0 -or
                [string]$row.nativeTimestampStatus -cne 'ready' -or
                [int64]$row.measurementReadbackBytes -ne 0 -or
                [int64]$row.timestampInstrumentationReadbackBytes -ne
                    $TimestampBytesPerSample) {
                throw "Scenario '$scenarioId' raw row contract failed."
            }

            [int64]$expectedRowUpload =
                if ($expected.variant -ceq $CpuVariant) {
                    $logicalProducerBytes
                }
                else {
                    0L
                }
            [int64]$expectedRowGpuWrites =
                if ($expected.variant -ceq $GpuVariant) {
                    $logicalProducerBytes
                }
                else {
                    0L
                }
            if ([int64]$row.logicalUploadBytes -ne
                    $expectedRowUpload -or
                [int64]$row.gpuProducerLogicalWriteBytes -ne
                    $expectedRowGpuWrites) {
                throw "Scenario '$scenarioId' raw logical bytes are invalid."
            }
            $cpuProducer =
                Number $row.cpuProducerMs 'cpuProducerMs'
            if ($cpuProducer -lt 0.0 -or
                ($expected.variant -ne $CpuVariant -and
                    $cpuProducer -ne 0.0) -or
                ($expected.variant -eq $CpuVariant -and
                    $cpuProducer -le 0.0)) {
                throw "Scenario '$scenarioId' CPU producer metric is invalid."
            }
            foreach ($metric in @(
                'cpuPipelineRecordMs','cpuCommandRecordMs',
                'submissionCpuMs','frameMs')) {
                if ((Number $row.$metric $metric) -lt 0.0) {
                    throw "Scenario '$scenarioId' has negative $metric."
                }
            }

            [uint64]$token = $row.nativeTimestampToken
            [uint64]$tag = $row.nativeTimestampUserTag
            if ($token -eq 0 -or
                -not $observedTokens.Add($token) -or
                -not $observedTags.Add($tag) -or
                $tag -ne [uint64]($globalRowIndex + 1)) {
                throw "Scenario '$scenarioId' timestamp token/tag is invalid."
            }
            [uint32]$expectedFlags =
                if ($expected.variant -ceq $ControlVariant) {
                    1
                }
                else {
                    0
                }
            if ([uint32]$row.nativeTimestampFlags -ne $expectedFlags) {
                throw "Scenario '$scenarioId' timestamp flags are invalid."
            }
            [uint64]$begin = $row.nativeTimestampBeginTicks
            [uint64]$end = $row.nativeTimestampEndTicks
            [uint64]$elapsed = $row.nativeTimestampElapsedTicks
            [uint64]$frequency = $row.nativeTimestampFrequency
            [uint64]$fence = $row.nativeTimestampFenceValue
            [int]$sourceFrame = $row.sourceUnityFrame
            [int64]$elapsedNanoseconds =
                $row.nativeTimestampElapsedNanoseconds
            if ($frequency -eq 0) {
                throw "Scenario '$scenarioId' timestamp frequency is zero."
            }
            [decimal]$expectedNsDecimal =
                [decimal]$elapsed * [decimal]1000000000 /
                [decimal]$frequency
            [int64]$expectedNs = [decimal]::ToInt64(
                [decimal]::Round(
                    $expectedNsDecimal,
                    0,
                    [System.MidpointRounding]::AwayFromZero))
            if ($end -lt $begin -or
                $elapsed -ne ($end - $begin) -or
                $elapsedNanoseconds -ne $expectedNs -or
                $frequency -ne
                    [uint64]$config.nativeTimestampWarmupFrequency -or
                [uint32]$row.nativeTimestampDeviceGeneration -ne
                    [uint32]$config.nativeTimestampDeviceGeneration -or
                $fence -ne ($previousFence + [uint64]1) -or
                ($previousEndTicks -ne 0 -and
                    $begin -lt $previousEndTicks) -or
                $sourceFrame -lt $previousSourceUnityFrame -or
                [int]$row.resultUnityFrame -lt $sourceFrame) {
                throw "Scenario '$scenarioId' timestamp payload is malformed."
            }
            if ($observedFrequency -eq 0) {
                $observedFrequency = $frequency
            }
            elseif ($observedFrequency -ne $frequency) {
                throw "Scenario '$scenarioId' timestamp frequency changed."
            }
            $gpuMs =
                Number $row.gpuRegionElapsedMs 'gpuRegionElapsedMs'
            Assert-Near $gpuMs (
                [double]$elapsedNanoseconds / 1000000.0) (
                "$scenarioId timestamp ns/ms conversion") 0.000000000001
            Assert-Near $gpuMs (
                [double]$elapsed * 1000.0 / [double]$frequency) (
                "$scenarioId timestamp ticks/ms conversion") 0.0000011
            if ($expected.variant -ne $ControlVariant -and
                ($elapsed -eq 0 -or $gpuMs -le 0.0)) {
                throw "Scenario '$scenarioId' has zero-duration GPU work."
            }
            $previousFence = $fence
            $previousEndTicks = $end
            $previousSourceUnityFrame = $sourceFrame
            $globalRowIndex++
            $blockGpu += $gpuMs
        }

        Assert-BlockMetric $rows 'gpuRegionElapsedMs' (
            $block.gpuRegionAverageMs) ($block.gpuRegionP99Ms) (
            "$scenarioId block $($expected.blockIndex) GPU")
        Assert-BlockMetric $rows 'frameMs' (
            $block.frameAverageMs) ($block.frameP99Ms) (
            "$scenarioId block $($expected.blockIndex) frame")
        Assert-BlockMetric $rows 'cpuProducerMs' (
            $block.cpuProducerAverageMs) ($block.cpuProducerP99Ms) (
            "$scenarioId block $($expected.blockIndex) producer")
        Assert-BlockMetric $rows 'cpuPipelineRecordMs' (
            $block.cpuPipelineRecordAverageMs) (
            $block.cpuPipelineRecordP99Ms) (
            "$scenarioId block $($expected.blockIndex) pipeline record")
        Assert-BlockMetric $rows 'cpuCommandRecordMs' (
            $block.cpuCommandRecordAverageMs) (
            $block.cpuCommandRecordP99Ms) (
            "$scenarioId block $($expected.blockIndex) command record")
        Assert-BlockMetric $rows 'submissionCpuMs' (
            $block.submissionAverageMs) ($block.submissionP99Ms) (
            "$scenarioId block $($expected.blockIndex) submission")
    }

    $expectedPhases = [System.Collections.Generic.List[string]]::new()
    $expectedPhases.Add('before')
    for ($pair = 1; $pair -le $ExpectedPairCount; $pair++) {
        $expectedPhases.Add("post-pair-$pair")
    }
    $expectedPhases.Add('after')
    if ($validation.Count -ne $ExpectedValidationRows) {
        throw "Scenario '$scenarioId' validation row count is invalid."
    }
    for ($i = 0; $i -lt $validation.Count; $i++) {
        $row = $validation[$i]
        if ([string]$row.phase -cne $expectedPhases[$i] -or
            [int]$row.passed -ne 1 -or
            [int64]$row.readbackBytes -ne
                $ValidationBytesPerComparison -or
            [string]$row.resultHash -cne
                '00000000-00000000-00000000-00000000' -or
            [int]$row.stateCount -ne $FixedStateCount -or
            [uint32]$row.digestMismatchCount -ne 0 -or
            [uint32]$row.digestDeltaXor -ne 0 -or
            [uint32]$row.digestDeltaSum0 -ne 0 -or
            [uint32]$row.digestDeltaSum1 -ne 0 -or
            [uint32]$row.cpuInvalidKeyCount -ne 0 -or
            [uint32]$row.cpuInvalidKeyHash -ne 0 -or
            [uint32]$row.gpuInvalidKeyCount -ne 0 -or
            [uint32]$row.gpuInvalidKeyHash -ne 0) {
            throw "Scenario '$scenarioId' validation invariant failed."
        }
    }

    if ([int]$run.schemaVersion -ne 10 -or
        [string]$run.suite -cne 'summit.gpu-sensor-pipeline' -or
        [int]$run.passed -ne 1 -or
        [string]$run.status -cne 'completed' -or
        [int]$run.processId -ne [int]$matrixRow.processId -or
        [string]$run.scenarioId -cne $scenarioId -or
        [int]$run.rawSampleCount -ne $expectedRawCount -or
        [int]$run.blockCount -ne $ExpectedBlockCount -or
        [int]$run.validationRows -ne $ExpectedValidationRows -or
        [int]$run.expectedValidationRows -ne
            $ExpectedValidationRows -or
        [int]$run.validationFailures -ne 0 -or
        [int64]$run.validationReadbackBytes -ne
            ($ExpectedValidationRows *
                $ValidationBytesPerComparison) -or
        [int64]$run.measurementReadbackBytes -ne 0 -or
        [int64]$run.timestampInstrumentationReadbackBytes -ne
            ($expectedRawCount * $TimestampBytesPerSample) -or
        [int]$run.nativeTimestampReadyRows -ne $expectedRawCount -or
        [int]$run.nativeTimestampWarmupPassed -ne 1 -or
        [string]$run.nativeTimestampWarmupStatus -cne 'ready' -or
        [int]$run.nativeTimestampAbiVersion -ne 2 -or
        (([uint32]$run.nativeTimestampCapabilityFlags -band 0x1F) -ne
            0x1F) -or
        [int]$run.nativeTimestampAcquireFailures -ne 0 -or
        [int]$run.nativeTimestampResultFailures -ne 0 -or
        [int]$run.nativeTimestampTimeouts -ne 0 -or
        [int]$run.nativeTimestampPendingRows -ne 0 -or
        [int]$run.gpuRegionTimingComplete -ne 1 -or
        [int]$run.logicalStateCount -ne $FixedStateCount -or
        [int]$run.finalStateKeyValidation -ne 1 -or
        [int]$run.allStateDigestComparison -ne 1 -or
        [int]$run.uploadQueueCoverageVerified -ne 0 -or
        [int]$run.asyncComputeClaim -ne 0 -or
        [int]$run.copyQueueClaim -ne 0 -or
        [int]$run.mainGraphicsQueueTimestamp -ne 1) {
        throw "Scenario '$scenarioId' run-summary contract failed."
    }
    [int64]$expectedMeasuredCaseBytes =
        8L * [int64]$config.sampleFrames * $logicalProducerBytes
    if ([int64]$run.logicalUploadBytes -ne
            $expectedMeasuredCaseBytes -or
        [int64]$run.gpuProducerLogicalWriteBytes -ne
            $expectedMeasuredCaseBytes) {
        throw "Scenario '$scenarioId' run logical-byte totals failed."
    }

    $measurementBlocks = @(
        $blocks | Where-Object {
            [string]$_.blockType -ceq 'measurement'
        })
    if ($measurementBlocks.Count -ne 16) {
        throw "Scenario '$scenarioId' measurement block count is invalid."
    }
    $scenarioPairRows =
        [System.Collections.Generic.List[object]]::new()
    for ($pairId = 1; $pairId -le $ExpectedPairCount; $pairId++) {
        $pairBlocks = @(
            $measurementBlocks | Where-Object {
                [int]$_.pairIndex -eq $pairId
            } | Sort-Object { [int]$_.withinPairPosition })
        if ($pairBlocks.Count -ne 2 -or
            [int]$pairBlocks[0].withinPairPosition -ne 1 -or
            [int]$pairBlocks[1].withinPairPosition -ne 2 -or
            [int]$pairBlocks[1].blockIndex -ne
                ([int]$pairBlocks[0].blockIndex + 1)) {
            throw "Scenario '$scenarioId' pair $pairId is not adjacent."
        }
        $cpu = @($pairBlocks | Where-Object {
            [string]$_.variant -ceq $CpuVariant
        })
        $gpu = @($pairBlocks | Where-Object {
            [string]$_.variant -ceq $GpuVariant
        })
        if ($cpu.Count -ne 1 -or $gpu.Count -ne 1 -or
            [string]$pairBlocks[0].pairOrder -cne
                [string]$pairBlocks[1].pairOrder) {
            throw "Scenario '$scenarioId' pair $pairId lacks one A and one B."
        }
        $cpuGpuAverage =
            Number $cpu[0].gpuRegionAverageMs 'CPU path GPU average'
        $gpuGpuAverage =
            Number $gpu[0].gpuRegionAverageMs 'GPU path GPU average'
        $cpuGpuP99 =
            Number $cpu[0].gpuRegionP99Ms 'CPU path GPU p99'
        $gpuGpuP99 =
            Number $gpu[0].gpuRegionP99Ms 'GPU path GPU p99'
        $cpuProducer =
            Number $cpu[0].cpuProducerAverageMs 'CPU producer average'
        $gpuProducerHost =
            Number $gpu[0].cpuProducerAverageMs 'GPU producer host average'
        if ($cpuProducer -le 0.0 -or $gpuProducerHost -ne 0.0) {
            throw "Scenario '$scenarioId' pair host-producer contract failed."
        }
        $cpuPipeline =
            Number $cpu[0].cpuPipelineRecordAverageMs 'CPU pipeline record'
        $gpuPipeline =
            Number $gpu[0].cpuPipelineRecordAverageMs 'GPU pipeline record'
        $cpuCommand =
            Number $cpu[0].cpuCommandRecordAverageMs 'CPU command record'
        $gpuCommand =
            Number $gpu[0].cpuCommandRecordAverageMs 'GPU command record'
        $cpuSubmission =
            Number $cpu[0].submissionAverageMs 'CPU submission'
        $gpuSubmission =
            Number $gpu[0].submissionAverageMs 'GPU submission'
        $pairRow = [pscustomobject]@{
            scenarioId = $scenarioId
            processId = [int]$matrixRow.processId
            superRound = [int]$pairBlocks[0].superRound
            pairIndex = $pairId
            pairOrder = [string]$pairBlocks[0].pairOrder
            cpuBlockIndex = [int]$cpu[0].blockIndex
            gpuBlockIndex = [int]$gpu[0].blockIndex
            cpuUploadedGpuAverageMs = $cpuGpuAverage
            gpuResidentGpuAverageMs = $gpuGpuAverage
            gpuAverageImprovementPercent =
                Improvement $cpuGpuAverage $gpuGpuAverage 'GPU average'
            gpuAverageAbsoluteReductionMs =
                $cpuGpuAverage - $gpuGpuAverage
            cpuUploadedGpuP99Ms = $cpuGpuP99
            gpuResidentGpuP99Ms = $gpuGpuP99
            gpuP99ImprovementPercent =
                Improvement $cpuGpuP99 $gpuGpuP99 'GPU p99'
            cpuProducerAverageMs = $cpuProducer
            gpuResidentCpuProducerAverageMs = $gpuProducerHost
            cpuProducerImprovementPercent = 100.0
            cpuUploadedPipelineRecordAverageMs = $cpuPipeline
            gpuResidentPipelineRecordAverageMs = $gpuPipeline
            pipelineRecordImprovementPercent =
                Improvement $cpuPipeline $gpuPipeline 'pipeline record'
            cpuUploadedCommandRecordAverageMs = $cpuCommand
            gpuResidentCommandRecordAverageMs = $gpuCommand
            commandRecordImprovementPercent =
                Improvement $cpuCommand $gpuCommand 'command record'
            cpuUploadedSubmissionAverageMs = $cpuSubmission
            gpuResidentSubmissionAverageMs = $gpuSubmission
            submissionImprovementPercent =
                Improvement $cpuSubmission $gpuSubmission 'submission'
            cpuLogicalUploadBytesPerFrame = $logicalProducerBytes
            gpuLogicalUploadBytesPerFrame = 0L
            gpuProducerLogicalWriteBytesPerFrame = $logicalProducerBytes
        }
        $scenarioPairRows.Add($pairRow)
        $allPairRows.Add($pairRow)
    }

    $pairPath = Join-Path $scenarioRoot 'pair-summary.csv'
    $scenarioPairRows |
        Export-Csv -LiteralPath $pairPath -NoTypeInformation -Encoding utf8
    [double[]]$gpuAverageImprovements = @(
        $scenarioPairRows | ForEach-Object {
            [double]$_.gpuAverageImprovementPercent
        })
    [double[]]$gpuAverageReductions = @(
        $scenarioPairRows | ForEach-Object {
            [double]$_.gpuAverageAbsoluteReductionMs
        })
    [double[]]$gpuP99Improvements = @(
        $scenarioPairRows | ForEach-Object {
            [double]$_.gpuP99ImprovementPercent
        })
    [double[]]$producerImprovements = @(
        $scenarioPairRows | ForEach-Object {
            [double]$_.cpuProducerImprovementPercent
        })
    [double[]]$pipelineImprovements = @(
        $scenarioPairRows | ForEach-Object {
            [double]$_.pipelineRecordImprovementPercent
        })
    [double[]]$commandImprovements = @(
        $scenarioPairRows | ForEach-Object {
            [double]$_.commandRecordImprovementPercent
        })
    [double[]]$submissionImprovements = @(
        $scenarioPairRows | ForEach-Object {
            [double]$_.submissionImprovementPercent
        })
    [double[]]$abImprovements = @(
        $scenarioPairRows | Where-Object {
            [string]$_.pairOrder -ceq 'AB'
        } | ForEach-Object {
            [double]$_.gpuAverageImprovementPercent
        })
    [double[]]$baImprovements = @(
        $scenarioPairRows | Where-Object {
            [string]$_.pairOrder -ceq 'BA'
        } | ForEach-Object {
            [double]$_.gpuAverageImprovementPercent
        })
    $gpuAverageMedian = Median $gpuAverageImprovements
    $gpuReductionMedian = Median $gpuAverageReductions
    $gpuP99Median = Median $gpuP99Improvements
    $abMedian = Median $abImprovements
    $baMedian = Median $baImprovements
    $winningPairs = @(
        $gpuAverageImprovements | Where-Object { $_ -gt 0.0 }).Count
    $controlValues = [double[]]@(
        $raw | Where-Object {
            [string]$_.variant -ceq $ControlVariant
        } | ForEach-Object {
            Number $_.gpuRegionElapsedMs 'control GPU interval'
        })
    $controlP99 = Percentile $controlValues 0.99
    $pairCountGate =
        $scenarioPairRows.Count -eq $ExpectedPairCount -and
        $abImprovements.Count -eq 4 -and
        $baImprovements.Count -eq 4
    $winCountGate = $winningPairs -ge $MinimumWinningPairs
    $averagePercentGate =
        $gpuAverageMedian -ge
            $MinimumMedianGpuAverageImprovementPercent
    $averageAbsoluteGate =
        $gpuReductionMedian -ge $MinimumMedianGpuAverageReductionMs
    $p99Gate =
        $gpuP99Median -ge
            $MinimumMedianGpuP99ImprovementPercent
    $orderBalanceGate = $abMedian -gt 0.0 -and $baMedian -gt 0.0
    $controlGate = $controlP99 -le $MaximumControlP99Ms
    if (-not $controlGate) {
        throw (
            "Scenario '$scenarioId' control P99 $controlP99 ms exceeds " +
            "$MaximumControlP99Ms ms.")
    }
    $performanceAccepted =
        $pairCountGate -and
        $winCountGate -and
        $averagePercentGate -and
        $averageAbsoluteGate -and
        $p99Gate -and
        $orderBalanceGate -and
        $controlGate
    $classification = if ($performanceAccepted) {
        'accepted'
    }
    elseif ($gpuAverageMedian -lt 0.0) {
        'negative'
    }
    else {
        'neutral-or-inconclusive'
    }
    $scenarioSummary = [pscustomobject]@{
        scenarioId = $scenarioId
        processId = [int]$matrixRow.processId
        elementCount = [int]$config.elementCount
        binCount = [int]$config.binCount
        queryCount = [int]$config.queryCount
        logicalStateCount = [int]$config.logicalStateCount
        sampleFrames = [int]$config.sampleFrames
        pairCount = $scenarioPairRows.Count
        evidenceValid = 1
        classification = $classification
        gpuPerformanceClaimUsable = [int]$performanceAccepted
        hostUploadEliminationClaimUsable = 1
        gpuResidentWinningPairs = $winningPairs
        gpuAverageImprovementMedianPercent = $gpuAverageMedian
        gpuAverageAbsoluteReductionMedianMs = $gpuReductionMedian
        gpuP99ImprovementMedianPercent = $gpuP99Median
        abGpuAverageImprovementMedianPercent = $abMedian
        baGpuAverageImprovementMedianPercent = $baMedian
        cpuProducerImprovementMedianPercent =
            Median $producerImprovements
        pipelineRecordImprovementMedianPercent =
            Median $pipelineImprovements
        commandRecordImprovementMedianPercent =
            Median $commandImprovements
        submissionImprovementMedianPercent =
            Median $submissionImprovements
        controlP99Ms = $controlP99
        cpuLogicalUploadBytesPerFrame = $logicalProducerBytes
        gpuLogicalUploadBytesPerFrame = 0L
        gpuProducerLogicalWriteBytesPerFrame = $logicalProducerBytes
        actualBenchmarkHostStagingBytes =
            [int64]$config.actualBenchmarkHostStagingBytes
        cpuCaseRequiredHostStagingBytes =
            [int64]$config.cpuCaseRequiredHostStagingBytes
        gpuCaseRequiredHostStagingBytes =
            [int64]$config.gpuCaseRequiredHostStagingBytes
        actualBenchmarkGpuResidentBytes =
            [int64]$config.actualBenchmarkGpuResidentBytes
        measurementWorkloadReadbackBytes = 0L
        timestampInstrumentationReadbackBytes =
            [int64]$run.timestampInstrumentationReadbackBytes
        uploadQueueCoverageVerified = 0
        mainGraphicsQueueTimestamp = 1
        graphicsDeviceName = [string]$device.graphicsDeviceName
        graphicsDeviceVendorId = [int]$device.graphicsDeviceVendorId
        graphicsDeviceId = [int]$device.graphicsDeviceId
        configSha256 =
            (Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
        deviceSha256 =
            (Get-FileHash -LiteralPath $devicePath -Algorithm SHA256).Hash
        rawFramesSha256 =
            (Get-FileHash -LiteralPath $rawPath -Algorithm SHA256).Hash
        blockSummarySha256 =
            (Get-FileHash -LiteralPath $blockPath -Algorithm SHA256).Hash
        validationSha256 =
            (Get-FileHash -LiteralPath $validationPath -Algorithm SHA256).Hash
        runSummarySha256 =
            (Get-FileHash -LiteralPath $runPath -Algorithm SHA256).Hash
    }
    @($scenarioSummary) |
        Export-Csv -LiteralPath (
            Join-Path $scenarioRoot 'scenario-summary.csv') `
            -NoTypeInformation -Encoding utf8
    $scenarioRows.Add($scenarioSummary)
    $scenarioDetails.Add([pscustomobject]@{
        summary = $scenarioSummary
        gates = [pscustomobject]@{
            pairCount = [bool]$pairCountGate
            winningPairs = [bool]$winCountGate
            medianGpuAveragePercent = [bool]$averagePercentGate
            medianGpuAverageAbsolute = [bool]$averageAbsoluteGate
            medianGpuP99 = [bool]$p99Gate
            abBaBalance = [bool]$orderBalanceGate
            controlP99 = [bool]$controlGate
        }
    })
}

$allPairRows |
    Export-Csv -LiteralPath (
        Join-Path $root 'pair-summary.csv') `
        -NoTypeInformation -Encoding utf8
$scenarioRows |
    Export-Csv -LiteralPath (
        Join-Path $root 'matrix-summary.csv') `
        -NoTypeInformation -Encoding utf8

$allEvidenceValid =
    @($scenarioRows | Where-Object {
        [int]$_.evidenceValid -ne 1
    }).Count -eq 0
$allPerformanceAccepted =
    @($scenarioRows | Where-Object {
        [int]$_.gpuPerformanceClaimUsable -ne 1
    }).Count -eq 0
$allHostClaimsUsable =
    @($scenarioRows | Where-Object {
        [int]$_.hostUploadEliminationClaimUsable -ne 1
    }).Count -eq 0
$quality = [ordered]@{
    schemaVersion = 10
    suite = 'summit.gpu-sensor-pipeline'
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    reportDirectory = $root
    formalAcceptanceMode = $formal
    matrixPreset = [string]$runner.matrixPreset
    matrixRole = [string]$runner.matrixRole
    gitCommit = [string]$runner.gitCommit
    gitBranch = [string]$runner.gitBranch
    signedImprovementConvention =
        'positive-gpu-resident-faster'
    evidenceValid = [bool]$allEvidenceValid
    scenarioCount = $scenarioRows.Count
    pairCount = $allPairRows.Count
    crossWorkloadGpuPerformanceClaimUsable =
        [bool]$allPerformanceAccepted
    crossWorkloadHostUploadEliminationClaimUsable =
        [bool]$allHostClaimsUsable
    claimBoundary = [ordered]@{
        gpuMetric = 'D3D12 main-graphics-command-list interval'
        uploadQueueCoverageVerified = $false
        asyncComputeClaim = $false
        copyQueueClaim = $false
        pcieTransferDurationClaim = $false
        endToEndSensorLatencyClaim = $false
        liveSensorClaim = $false
        citySceneClaim = $false
        fpsClaim = $false
        nvidiaValidationClaim = $false
    }
    performanceGate = [ordered]@{
        expectedPairs = $ExpectedPairCount
        minimumWinningPairs = $MinimumWinningPairs
        minimumMedianGpuAverageImprovementPercent =
            $MinimumMedianGpuAverageImprovementPercent
        minimumMedianGpuAverageReductionMs =
            $MinimumMedianGpuAverageReductionMs
        minimumMedianGpuP99ImprovementPercent =
            $MinimumMedianGpuP99ImprovementPercent
        requirePositiveAbAndBaMedians = $true
        maximumControlP99Ms = $MaximumControlP99Ms
    }
    scenarios = @($scenarioDetails)
}
$qualityJsonPath = Join-Path $root 'quality-summary.json'
$quality |
    ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath $qualityJsonPath -Encoding utf8

$qualityLines = [System.Collections.Generic.List[string]]::new()
$qualityLines.Add('GPU-resident dynamic sensor pipeline quality summary')
$qualityLines.Add('schemaVersion=10')
$qualityLines.Add('suite=summit.gpu-sensor-pipeline')
$qualityLines.Add("evidenceValid=$([int]$allEvidenceValid)")
$qualityLines.Add("formalAcceptanceMode=$([int]$formal)")
$qualityLines.Add("matrixPreset=$($runner.matrixPreset)")
$qualityLines.Add("matrixRole=$($runner.matrixRole)")
$qualityLines.Add("scenarioCount=$($scenarioRows.Count)")
$qualityLines.Add("pairCount=$($allPairRows.Count)")
$qualityLines.Add(
    "crossWorkloadGpuPerformanceClaimUsable=" +
    "$([int]$allPerformanceAccepted)")
$qualityLines.Add(
    "crossWorkloadHostUploadEliminationClaimUsable=" +
    "$([int]$allHostClaimsUsable)")
$qualityLines.Add(
    'gpuMetric=D3D12 main-graphics-command-list interval')
$qualityLines.Add('uploadQueueCoverageVerified=0')
$qualityLines.Add('asyncComputeClaim=0')
$qualityLines.Add('copyQueueClaim=0')
$qualityLines.Add('pcieTransferDurationClaim=0')
$qualityLines.Add('endToEndSensorLatencyClaim=0')
$qualityLines.Add('liveSensorClaim=0')
$qualityLines.Add('citySceneClaim=0')
$qualityLines.Add('fpsClaim=0')
$qualityLines.Add('nvidiaValidationClaim=0')
foreach ($scenario in $scenarioRows) {
    $prefix =
        ([string]$scenario.scenarioId -replace '[^A-Za-z0-9_]', '_')
    $qualityLines.Add(
        "$prefix.classification=$($scenario.classification)")
    $qualityLines.Add(
        "$prefix.gpuPerformanceClaimUsable=" +
        "$($scenario.gpuPerformanceClaimUsable)")
    $qualityLines.Add(
        "$prefix.hostUploadEliminationClaimUsable=" +
        "$($scenario.hostUploadEliminationClaimUsable)")
    $qualityLines.Add(
        "$prefix.gpuResidentWinningPairs=" +
        "$($scenario.gpuResidentWinningPairs)")
    $qualityLines.Add(
        "$prefix.gpuAverageImprovementMedianPercent=" +
        "$($scenario.gpuAverageImprovementMedianPercent)")
    $qualityLines.Add(
        "$prefix.gpuAverageAbsoluteReductionMedianMs=" +
        "$($scenario.gpuAverageAbsoluteReductionMedianMs)")
    $qualityLines.Add(
        "$prefix.gpuP99ImprovementMedianPercent=" +
        "$($scenario.gpuP99ImprovementMedianPercent)")
    $qualityLines.Add(
        "$prefix.cpuProducerImprovementMedianPercent=" +
        "$($scenario.cpuProducerImprovementMedianPercent)")
    $qualityLines.Add(
        "$prefix.pipelineRecordImprovementMedianPercent=" +
        "$($scenario.pipelineRecordImprovementMedianPercent)")
    $qualityLines.Add(
        "$prefix.commandRecordImprovementMedianPercent=" +
        "$($scenario.commandRecordImprovementMedianPercent)")
    $qualityLines.Add(
        "$prefix.submissionImprovementMedianPercent=" +
        "$($scenario.submissionImprovementMedianPercent)")
}
$qualityTextPath = Join-Path $root 'quality-summary.txt'
[System.IO.File]::WriteAllLines(
    $qualityTextPath,
    $qualityLines,
    [System.Text.UTF8Encoding]::new($false))

Write-Host (
    "Validated GPU sensor-pipeline evidence: scenarios=" +
    "$($scenarioRows.Count), pairs=$($allPairRows.Count), " +
    "allGpuPerformanceAccepted=$allPerformanceAccepted")
