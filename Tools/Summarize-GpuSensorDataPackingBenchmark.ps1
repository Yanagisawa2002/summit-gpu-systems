[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReportDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$BaselineVariant = 'expanded-aos-quantized-view'
$PackedVariant = 'packed-soa-fused-end-cursor'
$ControlVariant = 'empty-main-graphics-control'
$BaselineCaseId =
    'sensor-data-packing/expanded-aos-quantized-view-v1'
$PackedCaseId =
    'sensor-data-packing/packed-soa-fused-end-cursor-v2'
$ControlCaseId = 'control/empty-main-graphics-command-buffer'
$BaselineMarker =
    'GPU.SensorDataPacking/ExpandedAoSQuantizedView/MainGraphics'
$PackedMarker =
    'GPU.SensorDataPacking/PackedSoAFusedEndCursor/MainGraphics'
$ControlMarker = 'GPU.SensorDataPacking/Control/EmptyMainGraphics'
$Suite = 'summit.gpu-sensor-data-packing'
$SchemaVersion = 12
$FixedBinCount = 262144
$FixedStateCount = 64
$ExpectedSuperRounds = 4
$ExpectedPairCount = 8
$ExpectedPreconditioningBlockCount = 2
$PreconditioningSampleFramesPerBlock = 240
$ExpectedScoredMeasurementBlockCount = 16
$ExpectedBlockCount = 20
$ExpectedValidationRows = 10
$TimestampBytesPerSample = 16
$ValidationBytesPerComparison = 56
$MinimumWinningPairs = 7
$MinimumMedianGpuAverageImprovementPercent = 2.0
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
    return Get-Content -LiteralPath (Require-File $Path) -Raw |
        ConvertFrom-Json
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

function Read-PlayerRendererMetadata {
    param([Parameter(Mandatory = $true)][string]$Path)
    $resolved = Require-File $Path
    $text = Get-Content -LiteralPath $resolved -Raw
    $apiMatches = [regex]::Matches(
        $text,
        '(?m)^\s*Version:\s+(Direct3D 12) \[level ([^\]]+)\]\s*$')
    $rendererMatches = [regex]::Matches(
        $text,
        '(?m)^\s*Renderer:\s+(.+?) \(ID=0x([0-9A-Fa-f]+)\)\s*$')
    $driverMatches = [regex]::Matches(
        $text,
        '(?m)^\s*Driver:\s+([^\r\n]+?)\s*$')
    $complete =
        $apiMatches.Count -eq 1 -and
        $rendererMatches.Count -eq 1 -and
        $driverMatches.Count -eq 1
    $rendererIdHex = if ($rendererMatches.Count -eq 1) {
        $rendererMatches[0].Groups[2].Value.ToUpperInvariant()
    }
    else { '' }
    return [pscustomobject]@{
        parseComplete = [bool]$complete
        graphicsApi = if ($apiMatches.Count -eq 1) {
            [string]$apiMatches[0].Groups[1].Value
        }
        else { '' }
        featureLevel = if ($apiMatches.Count -eq 1) {
            [string]$apiMatches[0].Groups[2].Value
        }
        else { '' }
        rendererName = if ($rendererMatches.Count -eq 1) {
            [string]$rendererMatches[0].Groups[1].Value
        }
        else { '' }
        rendererIdHex = $rendererIdHex
        rendererId = if ($complete) {
            [Convert]::ToInt32($rendererIdHex, 16)
        }
        else { -1 }
        driverVersion = if ($driverMatches.Count -eq 1) {
            [string]$driverMatches[0].Groups[1].Value.Trim()
        }
        else { '' }
        apiMatchCount = $apiMatches.Count
        rendererMatchCount = $rendererMatches.Count
        driverMatchCount = $driverMatches.Count
        logSha256 =
            (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
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

function Test-GpuDurationContract {
    param(
        [Parameter(Mandatory = $true)][string]$BlockType,
        [Parameter(Mandatory = $true)][int64]$ElapsedTicks,
        [Parameter(Mandatory = $true)][int64]$ElapsedNanoseconds,
        [Parameter(Mandatory = $true)][double]$ElapsedMilliseconds
    )
    if (@('measurement', 'precondition') -ccontains $BlockType) {
        return (
            $ElapsedTicks -gt 0 -and
            $ElapsedNanoseconds -gt 0 -and
            $ElapsedMilliseconds -gt 0.0)
    }
    if (@('control-pre', 'control-post') -ccontains $BlockType) {
        # An empty control command buffer may begin and end within the same
        # native GPU timestamp tick. It is valid only when all three duration
        # representations agree that the interval is nonnegative.
        return (
            $ElapsedTicks -ge 0 -and
            $ElapsedNanoseconds -ge 0 -and
            $ElapsedMilliseconds -ge 0.0)
    }
    return $false
}

function Assert-Near {
    param(
        [Parameter(Mandatory = $true)][double]$Actual,
        [Parameter(Mandatory = $true)][double]$Expected,
        [Parameter(Mandatory = $true)][string]$Label,
        [double]$Tolerance = 0.000001
    )
    if ([Math]::Abs($Actual - $Expected) -gt $Tolerance) {
        throw (
            "$Label differs: actual=$Actual expected=$Expected " +
            "tolerance=$Tolerance")
    }
}

function Assert-FalseFields {
    param(
        [Parameter(Mandatory = $true)]$Object,
        [Parameter(Mandatory = $true)][string[]]$Fields,
        [Parameter(Mandatory = $true)][string]$Label
    )
    foreach ($field in $Fields) {
        $property = $Object.PSObject.Properties[$field]
        if ($null -eq $property) {
            throw "$Label is missing conservative claim field '$field'."
        }
        if ([bool]$property.Value) {
            throw "$Label must keep unsupported claim '$field' false."
        }
    }
}

function Assert-ZeroKeys {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Values,
        [Parameter(Mandatory = $true)][string[]]$Keys,
        [Parameter(Mandatory = $true)][string]$Label
    )
    foreach ($key in $Keys) {
        if (-not $Values.ContainsKey($key)) {
            throw "$Label is missing conservative claim key '$key'."
        }
        if ([int]$Values[$key] -ne 0) {
            throw "$Label must keep unsupported claim '$key' zero."
        }
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
        [Parameter(Mandatory = $true)][double]$Fraction
    )
    if ($Values.Count -eq 0) {
        throw 'Percentile requires at least one value.'
    }
    [double[]]$sorted = @($Values | Sort-Object)
    $index = [Math]::Max(
        0,
        [Math]::Min(
            $sorted.Count - 1,
            [int][Math]::Ceiling($sorted.Count * $Fraction) - 1))
    return $sorted[$index]
}

function Average {
    param([Parameter(Mandatory = $true)][double[]]$Values)
    if ($Values.Count -eq 0) {
        throw 'Average requires at least one value.'
    }
    [double]$sum = 0.0
    foreach ($value in $Values) {
        $sum += $value
    }
    return $sum / $Values.Count
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
    param(
        [Parameter(Mandatory = $true)][int]$SuperRounds,
        [Parameter(Mandatory = $true)][int]$SampleFrames
    )
    if ($SuperRounds -lt 1) {
        throw 'SuperRounds must be positive.'
    }
    if ($SampleFrames -lt 1) {
        throw 'SampleFrames must be positive.'
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
        expectedSamples = $SampleFrames
    })
    $result.Add([pscustomobject]@{
        blockIndex = $blockIndex++
        blockType = 'precondition'
        variant = $BaselineVariant
        caseId = $BaselineCaseId
        marker = $BaselineMarker
        superRound = 0
        sequencePosition = 1
        pairIndex = 0
        pairOrder = ''
        withinPairPosition = 0
        expectedSamples = $PreconditioningSampleFramesPerBlock
    })
    $result.Add([pscustomobject]@{
        blockIndex = $blockIndex++
        blockType = 'precondition'
        variant = $PackedVariant
        caseId = $PackedCaseId
        marker = $PackedMarker
        superRound = 0
        sequencePosition = 2
        pairIndex = 0
        pairOrder = ''
        withinPairPosition = 0
        expectedSamples = $PreconditioningSampleFramesPerBlock
    })
    for ($superRound = 1;
        $superRound -le $SuperRounds;
        $superRound++) {
        $variants = if (($superRound -band 1) -eq 1) {
            @(
                $BaselineVariant,
                $PackedVariant,
                $PackedVariant,
                $BaselineVariant)
        }
        else {
            @(
                $PackedVariant,
                $BaselineVariant,
                $BaselineVariant,
                $PackedVariant)
        }
        for ($position = 1; $position -le 4; $position++) {
            $variant = $variants[$position - 1]
            $pairStart =
                [int][Math]::Floor(($position - 1) / 2.0) * 2
            $pairOrder =
                if ($variants[$pairStart] -ceq $BaselineVariant) {
                    'AB'
                }
                else {
                    'BA'
                }
            $withinPair = (($position - 1) % 2) + 1
            $caseId =
                if ($variant -ceq $BaselineVariant) {
                    $BaselineCaseId
                }
                else {
                    $PackedCaseId
                }
            $marker =
                if ($variant -ceq $BaselineVariant) {
                    $BaselineMarker
                }
                else {
                    $PackedMarker
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
                expectedSamples = $SampleFrames
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
        expectedSamples = $SampleFrames
    })
    return @($result)
}

function Expected-PerFrameAccounting {
    param(
        [Parameter(Mandatory = $true)][string]$Variant,
        [Parameter(Mandatory = $true)]$Config
    )
    if ($Variant -ceq $ControlVariant) {
        return [pscustomobject]@{
            producer = 0L
            element = 0L
            spatialRead = 0L
            countAtomics = 0L
            scatterAtomics = 0L
        }
    }
    if ($Variant -ceq $BaselineVariant) {
        return [pscustomobject]@{
            producer =
                [int64]$Config.baselineProducerLogicalWriteBytesPerFrame
            element =
                [int64]$Config.
                    baselinePipelineElementMaterializedWriteBytesPerFrame
            spatialRead =
                [int64]$Config.
                    baselineSpatialBuildAddressedReadBytesPerFrame
            countAtomics =
                [int64]$Config.baselineCountAtomicOperationsPerFrame
            scatterAtomics =
                [int64]$Config.
                    baselineScatterReservationAtomicOperationsPerFrame
        }
    }
    if ($Variant -ceq $PackedVariant) {
        return [pscustomobject]@{
            producer =
                [int64]$Config.packedProducerLogicalWriteBytesPerFrame
            element =
                [int64]$Config.
                    packedPipelineElementMaterializedWriteBytesPerFrame
            spatialRead =
                [int64]$Config.
                    packedSpatialBuildAddressedReadBytesPerFrame
            countAtomics =
                [int64]$Config.packedCountAtomicOperationsPerFrame
            scatterAtomics =
                [int64]$Config.
                    packedScatterReservationAtomicOperationsPerFrame
        }
    }
    throw "Unknown benchmark variant '$Variant'."
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

if ([int]$runner.schemaVersion -ne $SchemaVersion -or
    [int]$runner.benchmarkSchemaVersion -ne $SchemaVersion -or
    [string]$runner.suite -cne $Suite) {
    throw 'Runner schema/suite contract does not match data-packing v12.'
}
if (-not [bool]$runner.runnerConfigFinalized -or
    [string]$runner.signedImprovementConvention -cne
        'positive-packed-soa-fused-end-cursor-faster' -or
    [string]$runner.engineeringGateLabel -cne
        'predeclared-engineering-gate-not-statistical-significance' -or
    [bool]$runner.statisticalSignificanceClaim -or
    -not [bool]$runner.requireCompleteGpuTimings -or
    [int]$runner.binCount -ne $FixedBinCount -or
    [int]$runner.logicalStateCount -ne $FixedStateCount -or
    [int]$runner.superRounds -ne $ExpectedSuperRounds -or
    [int]$runner.preconditioningBlockCount -ne
        $ExpectedPreconditioningBlockCount -or
    [int]$runner.preconditioningSampleFramesPerBlock -ne
        $PreconditioningSampleFramesPerBlock -or
    -not [bool]$runner.preconditioningExcludedFromScoring -or
    -not [bool]$runner.preconditioningGpuTimingsRecorded -or
    [int]$runner.expectedBlockCount -ne $ExpectedBlockCount -or
    [int]$runner.expectedScoredMeasurementBlockCount -ne
        $ExpectedScoredMeasurementBlockCount -or
    [int]$runner.expectedScoredPairCount -ne $ExpectedPairCount -or
    [int]$runner.commandSlotCount -ne 4 -or
    [int]$runner.validationTimeoutSeconds -ne 60 -or
    [int64]$runner.measurementWorkloadReadbackBytes -ne 0 -or
    [int]$runner.validationReadbackBytesPerComparison -ne
        $ValidationBytesPerComparison -or
    -not [bool]$runner.mainGraphicsQueueTimestamp -or
    -not [bool]$runner.profilerMarkers -or
    -not [bool]$runner.timestampInstrumentationReadbackDisclosedSeparately) {
    throw 'Runner fixed benchmark/readback contract failed.'
}
Assert-FalseFields $runner @(
    'hostUploadEliminationClaim',
    'uploadQueueCoverageVerified',
    'asyncComputeClaim',
    'copyQueueClaim',
    'pcieTrafficClaim',
    'measuredDramTrafficClaim',
    'driverReportedVramClaim',
    'endToEndSensorLatencyClaim',
    'liveSensorInputClaim',
    'sensorFidelityClaim',
    'citySceneClaim',
    'fpsClaim',
    'nvidiaValidationClaim',
    'statisticalSignificanceClaim') 'runner-config.json'
if ([int]$runner.coordinateLogicalBits -ne 16 -or
    [int]$runner.baselineCoordinateStorageBits -ne 32 -or
    [int]$runner.packedCoordinateStorageBits -ne 16 -or
    [int]$runner.payloadSourceBits -ne 32 -or
    [int]$runner.payloadQuantizedBits -ne 16 -or
    [int]$runner.baselinePayloadStorageBits -ne 32 -or
    [int]$runner.packedPayloadStorageBits -ne 16 -or
    [string]$runner.payloadQuantization -cne
        'UNORM32_TO_UNORM16_RNE_DIV65537' -or
    [int]$runner.payloadRawMaxErrorBound -ne 32768 -or
    [bool]$runner.packedMaterializedKeys -or
    [bool]$runner.packedMaterializedStableIds -or
    -not [bool]$runner.packedPairwiseProducer -or
    -not [bool]$runner.packedCountFusedIntoProducer -or
    -not [bool]$runner.packedPairwiseScatter -or
    -not [bool]$runner.packedQueryDecodesInConsumer -or
    -not [bool]$runner.packedQueryLoadsIntensityOnlyForAccepted -or
    [int64]$runner.packedDecodedAosBufferBytes -ne 0) {
    throw 'Runner data-layout/quantization declaration changed.'
}
if ([string]$runner.performanceAttribution -cne
        ('combined-packed-soa-q16-producer-count-offset-cursor-' +
         'fusion-lazy-payload') -or
    [string]$runner.digestComparisonCoverage -cne
        '64 aggregate frame digests; no full-buffer readback' -or
    [string]$runner.csrValidationCoverage -cne
        ('in-place end-offset/count/membership plus ' +
         'count/xor/sum/mixed-sum invariants') -or
    [string]$runner.gpuResidentAccountingCoverage -cne
        ('pipeline-owned GraphicsBuffers plus block digests; ' +
         'excludes timestamp/command/driver allocations')) {
    throw 'Runner attribution/coverage boundary changed.'
}
Assert-Near (
    Number $runner.payloadNormalizedMaxErrorBound (
        'runner intensity normalized error')) (
    32768.0 / [double][uint32]::MaxValue) (
    'runner intensity normalized error') 0.000000000001

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
    'timestampNativeDllSha256')) {
    Require-Hash ([string]$runner.$field) "runner.$field"
}
if ([int]$runner.sourceFileCount -le 0) {
    throw 'Runner source file inventory is empty.'
}

$projectRoot = [System.IO.Path]::GetFullPath([string]$runner.projectRoot)
Assert-CurrentFileHash (
    Join-Path $projectRoot 'Tools\Run-GpuSensorDataPackingBenchmark.ps1') (
    [string]$runner.runnerToolSha256) 'Runner tool'
Assert-CurrentFileHash $PSCommandPath (
    [string]$runner.summarizerToolSha256) 'Summarizer tool'
Assert-CurrentFileHash (
    Join-Path $projectRoot (
        'Tools\Tests\Test-GpuSensorDataPackingBenchmarkProvenance.ps1')) (
    [string]$runner.provenanceTestSha256) 'Provenance test'
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
        'amd-r9700-packing-v2',
        'amd-r9700-packing-discovery-v2')
$isolation = $runner.scenarioIsolationContract
if ([string]$isolation.discoverySetId -cne
        's5-packing-discovery-v2' -or
    [string]$isolation.formalHoldoutSetId -cne
        's5-packing-formal-holdout-v2' -or
    -not [bool]$isolation.preFrozenInRunnerSource -or
    -not [bool]$isolation.dataAndQuerySeedSetsDisjoint -or
    [bool]$isolation.formalHoldoutUsedForDiscovery -or
    [string]::Join(
        ',',
        @($isolation.discoverySeeds | ForEach-Object {
            [string][int]$_
        })) -cne '20260731,20260732' -or
    [string]::Join(
        ',',
        @($isolation.formalHoldoutSeeds | ForEach-Object {
            [string][int]$_
        })) -cne '20260817,20260818') {
    throw 'Discovery/formal holdout isolation contract changed.'
}
$gpuContract = $runner.frozenGpuContract
if ([int]$gpuContract.graphicsDeviceVendorId -ne 4098 -or
    [int]$gpuContract.graphicsDeviceId -ne 30033 -or
    [string]$gpuContract.graphicsDeviceName -cne
        'AMD Radeon AI PRO R9700' -or
    [string]$gpuContract.graphicsDeviceType -cne 'Direct3D12' -or
    [string]$gpuContract.rendererIdHex -cne '0x7551' -or
    [int]$gpuContract.rendererId -ne 30033 -or
    [string]$gpuContract.driverVersion -cne '32.0.31035.1003' -or
    [bool]$gpuContract.luidAvailable -or
    [string]$gpuContract.luidBoundary -cne
        ('Unity device.json and Player log expose no adapter LUID; ' +
         'this evidence makes no LUID identity claim.')) {
    throw 'Frozen R9700/driver/Renderer/LUID boundary changed.'
}
if ($frozenMatrix) {
    [DateTime]$buildStarted = [DateTime]::MinValue
    [DateTime]$buildEnded = [DateTime]::MinValue
    $buildStartParsed = [DateTime]::TryParse(
        [string]$runner.buildStartedUtc,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$buildStarted)
    $buildEndParsed = [DateTime]::TryParse(
        [string]$runner.buildEndedUtc,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind,
        [ref]$buildEnded)
    if (-not [bool]$runner.frozenMatrixRequested -or
        -not [bool]$runner.preflightCompetingGpuProcessCheckPassed -or
        @($runner.preflightGpuProcesses).Count -ne 0 -or
        -not [string]::IsNullOrWhiteSpace(
            [string]$runner.preflightGpuProcessInventoryError) -or
        [string]::IsNullOrWhiteSpace(
            [string]$runner.preflightCapturedUtc) -or
        -not [bool]$runner.buildRequested -or
        -not [bool]$runner.buildExecuted -or
        [int]$runner.buildExitCode -ne 0 -or
        -not $buildStartParsed -or
        -not $buildEndParsed -or
        $buildEnded -lt $buildStarted -or
        -not [bool]$runner.allPlayerSourceBindingsSatisfied -or
        -not [bool]$runner.allPlayerGpuIdentityContractsSatisfied) {
        throw (
            'Frozen matrix build/preflight/Player identity provenance ' +
            'contract failed.')
    }
}
if ($formal) {
    if ([string]$runner.matrixPreset -cne 'amd-r9700-packing-v2' -or
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
        throw 'Formal runner contract differs from the frozen v2 holdout.'
    }
    $contract = $runner.formalContract
    if ([string]$contract.unityVersion -cne '6000.5.2f1' -or
        [int]$contract.deviceIndex -ne 0 -or
        [string]$contract.matrixPreset -cne
            'amd-r9700-packing-v2' -or
        [int]$contract.binCount -ne $FixedBinCount -or
        [int]$contract.stateCount -ne $FixedStateCount -or
        [int]$contract.commandSlotCount -ne 4 -or
        [int]$contract.superRounds -ne $ExpectedSuperRounds -or
        [int]$contract.warmupFrames -ne 60 -or
        [int]$contract.sampleFrames -ne 900 -or
        [int]$contract.cooldownFrames -ne 15 -or
        [int]$contract.preconditioningBlockCount -ne
            $ExpectedPreconditioningBlockCount -or
        [int]$contract.preconditioningSampleFramesPerBlock -ne
            $PreconditioningSampleFramesPerBlock -or
        [int]$contract.editModeTimeoutMinutes -ne 30 -or
        [int]$contract.validationTimeoutSeconds -ne 60 -or
        [int]$contract.playerTimeoutMinutes -ne 90) {
        throw 'Formal contract metadata is not the frozen v2 contract.'
    }
}
elseif ([string]$runner.matrixPreset -ceq
    'amd-r9700-packing-discovery-v2') {
    if ([string]$runner.matrixRole -cne 'discovery' -or
        [int]$runner.deviceIndex -ne 0 -or
        [string]$runner.projectUnityVersion -cne '6000.5.2f1' -or
        [string]$runner.unityEditorResolvedVersion -cne '6000.5.2f1' -or
        [int]$runner.sampleFrames -ne 240 -or
        [int]$runner.warmupFrames -ne 60 -or
        [int]$runner.cooldownFrames -ne 15 -or
        [int]$runner.validationTimeoutSeconds -ne 60) {
        throw 'Discovery runner contract differs from the frozen v2 contract.'
    }
    $contract = $runner.discoveryContract
    if ([string]$contract.unityVersion -cne '6000.5.2f1' -or
        [int]$contract.deviceIndex -ne 0 -or
        [string]$contract.matrixPreset -cne
            'amd-r9700-packing-discovery-v2' -or
        [int]$contract.commandSlotCount -ne 4 -or
        [int]$contract.superRounds -ne $ExpectedSuperRounds -or
        [int]$contract.warmupFrames -ne 60 -or
        [int]$contract.sampleFrames -ne 240 -or
        [int]$contract.cooldownFrames -ne 15 -or
        [int]$contract.preconditioningBlockCount -ne
            $ExpectedPreconditioningBlockCount -or
        [int]$contract.preconditioningSampleFramesPerBlock -ne
            $PreconditioningSampleFramesPerBlock -or
        [int]$contract.validationTimeoutSeconds -ne 60) {
        throw 'Discovery contract metadata is not the frozen v2 contract.'
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
    'Summit.GpuSensorPipeline.Tests.GpuSensorIntensityQuantizerTests',
    'Summit.GpuSensorPipeline.Tests.GpuSensorPackedSoaPipelineContractTests',
    'Summit.GpuSensorPipeline.Tests.GpuSensorPackedSoaPipelineIntegrationTests',
    'Summit.GpuSensorPipeline.Benchmark.Tests.Editor.dll',
    'Summit.GpuSensorPipeline.Benchmark.Tests.GpuSensorDataPackingBenchmarkScheduleTests'
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
        [string]$edit.result -cne 'Passed' -or
        [int]$edit.total -le 0 -or
        [int]$edit.passed -ne [int]$edit.total -or
        [int]$edit.failed -ne 0 -or
        [int]$edit.skipped -ne 0 -or
        [int]$edit.inconclusive -ne 0 -or
        [string]$edit.identityMatchMode -cne 'exact-nunit-node-v1') {
        throw 'Formal EditMode evidence contract failed.'
    }
    $observedExpected = @($edit.expectedIdentities)
    if ([string]::Join("`n", $observedExpected) -cne
        [string]::Join("`n", $expectedEditModeIdentities) -or
        @($edit.missingIdentities).Count -ne 0) {
        throw 'Formal EditMode identity set is incomplete or changed.'
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

$expectedFormalScenarios = [ordered]@{
    'holdout-packing-v2-n262144-q64' =
        '262144|262144|64|64|20260817'
    'holdout-packing-v2-n1048576-q256' =
        '1048576|262144|256|64|20260818'
}
$expectedDiscoveryScenarios = [ordered]@{
    'discovery-packing-v2-n262144-q64' =
        '262144|262144|64|64|20260731'
    'discovery-packing-v2-n1048576-q256' =
        '1048576|262144|256|64|20260732'
}
$expectedFrozenScenarios = if ($formal) {
    $expectedFormalScenarios
}
else {
    $expectedDiscoveryScenarios
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
    $blockPath =
        Require-File (Join-Path $scenarioRoot 'block-summary.csv')
    $validationPath =
        Require-File (Join-Path $scenarioRoot 'validation.csv')
    $runPath = Require-File (Join-Path $scenarioRoot 'run-summary.txt')
    $playerLogPath =
        Require-File (Join-Path $scenarioRoot 'player.log')
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
            [System.IO.Path]::GetFullPath($scenarioRoot) -or
        [System.IO.Path]::GetFullPath(
            [string]$playerRun[0].playerLog) -ine $playerLogPath -or
        [System.IO.Path]::GetFullPath(
            [string]$playerRun[0].configPath) -ine $configPath -or
        [System.IO.Path]::GetFullPath(
            [string]$playerRun[0].devicePath) -ine $devicePath -or
        -not [bool]$playerRun[0].sourceBindingSatisfied) {
        throw "Scenario '$scenarioId' runner/process binding failed."
    }
    $playerRenderer = Read-PlayerRendererMetadata $playerLogPath
    if ([string]$playerRun[0].playerLogSha256 -cne
            [string]$playerRenderer.logSha256 -or
        [string]$playerRun[0].configSha256 -cne
            (Get-FileHash -LiteralPath $configPath `
                -Algorithm SHA256).Hash -or
        [string]$playerRun[0].deviceSha256 -cne
            (Get-FileHash -LiteralPath $devicePath `
                -Algorithm SHA256).Hash -or
        [string]$config.buildCommit -cne
            [string]$runner.gitCommit -or
        [string]$config.runtimeShaderSha256 -cne
            [string]$runner.runtimeShaderSha256 -or
        [string]$config.runtimeApiSha256 -cne
            [string]$runner.runtimeApiSha256 -or
        [string]$config.nativeTimestampDllSha256 -cne
            [string]$runner.timestampNativeDllSha256) {
        throw (
            "Scenario '$scenarioId' Player log/config/device hashes or " +
            'commit/runtime/native source binding failed.')
    }

    if ([int]$config.schemaVersion -ne $SchemaVersion -or
        [string]$config.suite -cne $Suite -or
        [int]$config.processId -ne [int]$matrixRow.processId -or
        [string]$config.scenarioId -cne $scenarioId -or
        [int]$config.elementCount -ne [int]$matrixRow.elementCount -or
        [int]$config.binCount -ne $FixedBinCount -or
        [int]$config.queryCount -ne [int]$matrixRow.queryCount -or
        [int]$config.logicalStateCount -ne $FixedStateCount -or
        [int]$config.seed -ne [int]$matrixRow.seed -or
        [int]$config.superRounds -ne $ExpectedSuperRounds -or
        [int]$config.commandSlotCount -ne 4 -or
        [string]$config.scanBackend -cne 'wave-ops' -or
        -not [bool]$config.profilerMarkers -or
        [string]$config.baselineCaseId -cne $BaselineCaseId -or
        [string]$config.packedCaseId -cne $PackedCaseId -or
        [string]$config.controlCaseId -cne $ControlCaseId -or
        [int]$config.preconditioningBlockCount -ne
            $ExpectedPreconditioningBlockCount -or
        [int]$config.preconditioningSampleFramesPerBlock -ne
            $PreconditioningSampleFramesPerBlock -or
        -not [bool]$config.preconditioningExcludedFromScoring -or
        -not [bool]$config.preconditioningGpuTimingsRecorded -or
        [string]$config.scheduleContract -cne
            ('control-pre;unscored-precondition-A-B;' +
             "$ExpectedSuperRounds balanced scored super-rounds;" +
             'ABBA/BAAB;control-post') -or
        -not [bool]$config.caseLocalWarmup -or
        -not [bool]$config.sameProcessPaired -or
        -not [bool]$config.mainGraphicsQueueTimestamp -or
        [bool]$config.cpuProducerStagePresent -or
        -not [bool]$config.baselineFinalStateKeyValidation -or
        -not [bool]$config.packedSampleValidation -or
        -not [bool]$config.packedCsrValidation -or
        -not [bool]$config.allStateDigestComparison -or
        [int]$config.allStateDigestCount -ne $FixedStateCount -or
        [int]$config.expectedValidationRows -ne
            $ExpectedValidationRows -or
        [int64]$config.measurementReadbackBytesPerFrame -ne 0 -or
        [int]$config.validationReadbackBytesPerComparison -ne
            $ValidationBytesPerComparison -or
        [int]$config.timestampInstrumentationBytesPerCompletedSample -ne
            $TimestampBytesPerSample -or
        -not [bool]$config.requireCompleteGpuTimings) {
        throw "Scenario '$scenarioId' fixed config contract failed."
    }
    Assert-FalseFields $config @(
        'hostUploadEliminationClaim',
        'uploadQueueCoverageVerified',
        'asyncComputeClaim',
        'copyQueueClaim',
        'pcieTrafficClaim',
        'measuredDramTrafficClaim',
        'driverReportedVramClaim',
        'endToEndSensorLatencyClaim',
        'liveSensorInputClaim',
        'sensorFidelityClaim',
        'citySceneClaim',
        'fpsClaim',
        'nvidiaValidationClaim') "scenario '$scenarioId' config"
    if ([int]$config.coordinateLogicalBits -ne 16 -or
        [int]$config.baselineCoordinateStorageBits -ne 32 -or
        [int]$config.packedCoordinateStorageBits -ne 16 -or
        [int]$config.payloadSourceBits -ne 32 -or
        [int]$config.payloadQuantizedBits -ne 16 -or
        [int]$config.baselinePayloadStorageBits -ne 32 -or
        [int]$config.packedPayloadStorageBits -ne 16 -or
        [string]$config.payloadQuantization -cne
            'UNORM32_TO_UNORM16_RNE_DIV65537' -or
        [int]$config.payloadRawMaxErrorBound -ne 32768 -or
        -not [bool]$config.baselineMaterializedKeys -or
        [bool]$config.packedMaterializedKeys -or
        -not [bool]$config.baselineMaterializedStableIds -or
        [bool]$config.packedMaterializedStableIds -or
        -not [bool]$config.packedPairwiseProducer -or
        -not [bool]$config.packedCountFusedIntoProducer -or
        -not [bool]$config.packedPairwiseScatter -or
        -not [bool]$config.packedQueryDecodesInConsumer -or
        -not [bool]$config.packedQueryLoadsIntensityOnlyForAccepted -or
        [int64]$config.packedDecodedAosBufferBytes -ne 0) {
        throw "Scenario '$scenarioId' data-layout declaration failed."
    }
    if ([string]$config.performanceAttribution -cne
            ('combined-packed-soa-q16-producer-count-offset-cursor-' +
             'fusion-lazy-payload') -or
        [string]$config.digestComparisonCoverage -cne
            '64 aggregate frame digests; no full-buffer readback' -or
        [string]$config.csrValidationCoverage -cne
            ('in-place end-offset/count/membership plus ' +
             'count/xor/sum/mixed-sum invariants') -or
        [string]$config.gpuResidentAccountingCoverage -cne
            ('pipeline-owned GraphicsBuffers plus block digests; ' +
             'excludes timestamp/command/driver allocations')) {
        throw "Scenario '$scenarioId' attribution/coverage boundary failed."
    }
    Assert-Near (
        Number $config.payloadNormalizedMaxErrorBound (
            "$scenarioId normalized quantization error")) (
        32768.0 / [double][uint32]::MaxValue) (
        "$scenarioId normalized quantization error") 0.000000000001

    [int64]$elementCount = [int64]$config.elementCount
    $expectedAccounting = [ordered]@{
        baselineProducerLogicalWriteBytesPerFrame =
            20L * $elementCount
        packedProducerLogicalWriteBytesPerFrame =
            8L * $elementCount
        baselinePipelineElementMaterializedWriteBytesPerFrame =
            24L * $elementCount
        packedPipelineElementMaterializedWriteBytesPerFrame =
            12L * $elementCount
        baselineSpatialBuildAddressedReadBytesPerFrame =
            12L * $elementCount
        packedSpatialBuildAddressedReadBytesPerFrame =
            6L * $elementCount
        baselineCountAtomicOperationsPerFrame = $elementCount
        packedCountAtomicOperationsPerFrame = $elementCount
        baselineScatterReservationAtomicOperationsPerFrame =
            $elementCount
        packedScatterReservationAtomicOperationsPerFrame =
            $elementCount
        baselineTotalAtomicOperationsPerFrame = 2L * $elementCount
        packedTotalAtomicOperationsPerFrame = 2L * $elementCount
    }
    foreach ($field in $expectedAccounting.Keys) {
        if ([int64]$config.$field -ne
            [int64]$expectedAccounting[$field]) {
            throw (
                "Scenario '$scenarioId' accounting '$field' changed: " +
                "$($config.$field), expected $($expectedAccounting[$field]).")
        }
    }
    if ([int64]$config.baselinePipelineResidentBytes -le 0 -or
        [int64]$config.packedPipelineResidentBytes -le 0 -or
        [int64]$config.blockDigestBufferBytes -ne 2048L -or
        [int64]$config.baselineIsolatedGpuResidentBytes -ne
            ([int64]$config.baselinePipelineResidentBytes + 1024L) -or
        [int64]$config.packedIsolatedGpuResidentBytes -ne
            ([int64]$config.packedPipelineResidentBytes + 1024L) -or
        [int64]$config.actualBenchmarkGpuResidentBytes -ne
            ([int64]$config.baselinePipelineResidentBytes +
             [int64]$config.packedPipelineResidentBytes +
             [int64]$config.blockDigestBufferBytes)) {
        throw "Scenario '$scenarioId' GPU-resident byte accounting failed."
    }

    Assert-FalseFields $device @(
        'hostUploadEliminationClaim',
        'uploadQueueCoverageVerified',
        'asyncComputeClaim',
        'copyQueueClaim',
        'pcieTrafficClaim',
        'measuredDramTrafficClaim',
        'driverReportedVramClaim',
        'endToEndSensorLatencyClaim',
        'liveSensorInputClaim',
        'sensorFidelityClaim',
        'citySceneClaim',
        'fpsClaim',
        'nvidiaValidationClaim') "scenario '$scenarioId' device"
    if ([int]$device.processId -ne [int]$matrixRow.processId -or
        [string]$device.graphicsDeviceType -cne 'Direct3D12' -or
        -not [bool]$device.supportsComputeShaders -or
        -not [bool]$device.supportsGraphicsFence -or
        -not [bool]$device.supportsAsyncGpuReadback -or
        -not [bool]$device.mainGraphicsQueueTimestamp) {
        throw "Scenario '$scenarioId' device capability contract failed."
    }
    if ($frozenMatrix -and (
        -not [bool]$playerRenderer.parseComplete -or
        [string]$playerRenderer.graphicsApi -cne 'Direct3D 12' -or
        [string]$playerRenderer.rendererName -cne
            'AMD Radeon AI PRO R9700' -or
        [int]$playerRenderer.rendererId -ne 30033 -or
        [string]$playerRenderer.rendererIdHex -cne '7551' -or
        [string]$playerRenderer.driverVersion -cne
            '32.0.31035.1003' -or
        [int]$device.graphicsDeviceVendorId -ne 4098 -or
        [int]$device.graphicsDeviceId -ne 30033 -or
        [string]$device.graphicsDeviceName -cne
            'AMD Radeon AI PRO R9700' -or
        [string]$device.graphicsDeviceType -cne 'Direct3D12' -or
        -not [bool]$playerRun[0].gpuIdentityContractSatisfied -or
        [string]$playerRun[0].activeRenderer.logSha256 -cne
            [string]$playerRenderer.logSha256 -or
        [int]$playerRun[0].deviceVendorId -ne 4098 -or
        [int]$playerRun[0].deviceId -ne 30033 -or
        [string]$playerRun[0].deviceName -cne
            'AMD Radeon AI PRO R9700' -or
        [string]$playerRun[0].deviceType -cne 'Direct3D12' -or
        [string]$playerRun[0].driverVersion -cne
            '32.0.31035.1003' -or
        [bool]$playerRun[0].luidAvailable -or
        [string]$playerRun[0].luidBoundary -cne
            [string]$gpuContract.luidBoundary)) {
        throw (
            "Scenario '$scenarioId' active Renderer/device/driver or " +
            'explicit no-LUID boundary failed.')
    }
    $deviceIdentity =
        "$($device.graphicsDeviceVendorId)|$($device.graphicsDeviceId)|" +
        "$($device.graphicsDeviceName)|$($device.graphicsDeviceVersion)|" +
        "$($playerRenderer.rendererIdHex)|$($playerRenderer.driverVersion)"
    if ($null -eq $activeDeviceIdentity) {
        $activeDeviceIdentity = $deviceIdentity
    }
    elseif ($activeDeviceIdentity -cne $deviceIdentity) {
        throw 'Scenario matrix crossed graphics-device identities.'
    }

    Require-Columns $raw @(
        'processId','scenarioId','superRound','sequencePosition',
        'pairIndex','pairOrder','withinPairPosition','blockIndex',
        'blockType','caseId','variant','marker','sampleIndex',
        'logicalState','commandSlot','commandSlotWaitFrames',
        'sourceUnityFrame','resultUnityFrame','elapsedSeconds',
        'cpuProducerMs','cpuPipelineRecordMs','cpuCommandRecordMs',
        'submissionCpuMs','producerMaterializedWriteBytes',
        'pipelineElementMaterializedWriteBytes',
        'spatialBuildAddressedReadBytes','binCountAtomicOperations',
        'scatterAtomicOperations','nativeTimestampToken',
        'nativeTimestampUserTag','nativeTimestampFlags',
        'nativeTimestampStatus','nativeTimestampBeginTicks',
        'nativeTimestampEndTicks','nativeTimestampElapsedTicks',
        'nativeTimestampFrequency','nativeTimestampElapsedNanoseconds',
        'nativeTimestampFenceValue','nativeTimestampDeviceGeneration',
        'gpuRegionElapsedMs','frameMs','measurementReadbackBytes',
        'timestampInstrumentationReadbackBytes') (
        "$scenarioId/raw-frames.csv")
    Require-Columns $blocks @(
        'processId','scenarioId','superRound','sequencePosition',
        'pairIndex','pairOrder','withinPairPosition','blockIndex',
        'blockType','caseId','variant','marker','samples',
        'gpuRegionValidSamples','stateCount',
        'producerMaterializedWriteBytes',
        'pipelineElementMaterializedWriteBytes',
        'spatialBuildAddressedReadBytes','binCountAtomicOperations',
        'scatterAtomicOperations','commandSlotWaitFrames',
        'gpuRegionAverageMs','gpuRegionP50Ms','gpuRegionP95Ms',
        'gpuRegionP99Ms','frameAverageMs','frameP99Ms',
        'cpuProducerAverageMs','cpuProducerP99Ms',
        'cpuPipelineRecordAverageMs','cpuPipelineRecordP99Ms',
        'cpuCommandRecordAverageMs','cpuCommandRecordP99Ms',
        'submissionAverageMs','submissionP99Ms','fenceSupported',
        'fencePassed','measurementReadbackBytes',
        'timestampInstrumentationReadbackBytes') (
        "$scenarioId/block-summary.csv")
    Require-Columns $validation @(
        'phase','passed','message','readbackBytes','resultHash',
        'stateCount','digestMismatchCount','digestDeltaXor',
        'digestDeltaSum0','digestDeltaSum1',
        'baselineInvalidKeyCount','baselineInvalidKeyHash',
        'packedSampleMismatchCount','packedSampleMismatchHash',
        'packedCsrMismatchCount','packedCsrMismatchHash',
        'packedCsrElementCount','packedCsrIdXor','packedCsrIdSum',
        'packedCsrIdMixedSum','expectedPackedCsrElementCount',
        'expectedPackedCsrIdXor','expectedPackedCsrIdSum',
        'expectedPackedCsrIdMixedSum') (
        "$scenarioId/validation.csv")

    $expectedBlocks = @(
        Expected-Blocks `
            -SuperRounds ([int]$config.superRounds) `
            -SampleFrames ([int]$config.sampleFrames))
    $expectedRawCount = [int64](
        ($expectedBlocks |
            Measure-Object -Property expectedSamples -Sum).Sum)
    if ($raw.Count -ne $expectedRawCount -or
        $blocks.Count -ne $ExpectedBlockCount -or
        [int]$matrixRow.rawSampleCount -ne $expectedRawCount) {
        throw "Scenario '$scenarioId' raw/block row counts failed."
    }

    $previousSourceUnityFrame = -1
    foreach ($expected in $expectedBlocks) {
        $blockRows = @($raw | Where-Object {
            [int]$_.blockIndex -eq [int]$expected.blockIndex
        } | Sort-Object { [int]$_.sampleIndex })
        $block = @($blocks | Where-Object {
            [int]$_.blockIndex -eq [int]$expected.blockIndex
        })
        $expectedBlockSamples = [int]$expected.expectedSamples
        if ($blockRows.Count -ne $expectedBlockSamples -or
            $block.Count -ne 1) {
            throw (
                "Scenario '$scenarioId' block " +
                "$($expected.blockIndex) row count failed.")
        }
        $block = $block[0]
        foreach ($identity in @(
            @('blockType', [string]$expected.blockType),
            @('variant', [string]$expected.variant),
            @('caseId', [string]$expected.caseId),
            @('marker', [string]$expected.marker),
            @('pairOrder', [string]$expected.pairOrder))) {
            if ([string]$block.($identity[0]) -cne $identity[1]) {
                throw (
                    "Scenario '$scenarioId' block $($expected.blockIndex) " +
                    "identity '$($identity[0])' changed.")
            }
        }
        foreach ($numericIdentity in @(
            @('superRound', [int]$expected.superRound),
            @('sequencePosition', [int]$expected.sequencePosition),
            @('pairIndex', [int]$expected.pairIndex),
            @('withinPairPosition', [int]$expected.withinPairPosition))) {
            if ([int]$block.($numericIdentity[0]) -ne
                $numericIdentity[1]) {
                throw (
                    "Scenario '$scenarioId' block $($expected.blockIndex) " +
                    "numeric identity '$($numericIdentity[0])' changed.")
            }
        }
        $accounting =
            Expected-PerFrameAccounting $expected.variant $config
        [double[]]$gpuValues = @()
        [double[]]$frameValues = @()
        [double[]]$cpuProducerValues = @()
        for ($rowIndex = 0;
            $rowIndex -lt $blockRows.Count;
            $rowIndex++) {
            $row = $blockRows[$rowIndex]
            $expectedSample = $rowIndex + 1
            if ([int]$row.processId -ne [int]$matrixRow.processId -or
                [string]$row.scenarioId -cne $scenarioId -or
                [int]$row.sampleIndex -ne $expectedSample -or
                [int]$row.logicalState -ne
                    (($expectedSample - 1) % $FixedStateCount) -or
                [int]$row.commandSlot -lt 0 -or
                [int]$row.commandSlot -ge [int]$config.commandSlotCount -or
                [int]$row.commandSlotWaitFrames -lt 0 -or
                [string]$row.nativeTimestampStatus -cne 'ready' -or
                [int64]$row.nativeTimestampToken -le 0 -or
                [int64]$row.nativeTimestampFrequency -le 0 -or
                [int64]$row.measurementReadbackBytes -ne 0 -or
                [int64]$row.timestampInstrumentationReadbackBytes -ne
                    $TimestampBytesPerSample -or
                [int64]$row.producerMaterializedWriteBytes -ne
                    [int64]$accounting.producer -or
                [int64]$row.pipelineElementMaterializedWriteBytes -ne
                    [int64]$accounting.element -or
                [int64]$row.spatialBuildAddressedReadBytes -ne
                    [int64]$accounting.spatialRead -or
                [int64]$row.binCountAtomicOperations -ne
                    [int64]$accounting.countAtomics -or
                [int64]$row.scatterAtomicOperations -ne
                    [int64]$accounting.scatterAtomics) {
                throw (
                    "Scenario '$scenarioId' block $($expected.blockIndex) " +
                    "sample $expectedSample contract failed.")
            }
            foreach ($identity in @(
                @('blockType', [string]$expected.blockType),
                @('variant', [string]$expected.variant),
                @('caseId', [string]$expected.caseId),
                @('marker', [string]$expected.marker),
                @('pairOrder', [string]$expected.pairOrder))) {
                if ([string]$row.($identity[0]) -cne $identity[1]) {
                    throw (
                        "Scenario '$scenarioId' sample identity " +
                        "'$($identity[0])' changed.")
                }
            }
            $sourceFrame = [int]$row.sourceUnityFrame
            if ($sourceFrame -lt $previousSourceUnityFrame -or
                [int]$row.resultUnityFrame -lt $sourceFrame) {
                throw (
                    "Scenario '$scenarioId' Unity-frame ordering failed.")
            }
            $previousSourceUnityFrame = $sourceFrame
            [double]$gpuMs =
                Number $row.gpuRegionElapsedMs 'raw GPU interval'
            if (-not (Test-GpuDurationContract `
                    -BlockType ([string]$expected.blockType) `
                    -ElapsedTicks (
                        [int64]$row.nativeTimestampElapsedTicks) `
                    -ElapsedNanoseconds (
                        [int64]$row.nativeTimestampElapsedNanoseconds) `
                    -ElapsedMilliseconds $gpuMs)) {
                throw (
                    "Scenario '$scenarioId' block " +
                    "$($expected.blockIndex) sample $expectedSample " +
                    "GPU duration contract failed.")
            }
            $gpuValues += $gpuMs
            $frameValues += Number $row.frameMs 'raw frame time'
            $cpuProducerValues +=
                Number $row.cpuProducerMs 'raw CPU producer time'
        }
        if ([int]$block.samples -ne $expectedBlockSamples -or
            [int]$block.gpuRegionValidSamples -ne
                $expectedBlockSamples -or
            [int]$block.stateCount -ne $FixedStateCount -or
            [int]$block.fenceSupported -ne 1 -or
            [int]$block.fencePassed -ne 1 -or
            [int64]$block.measurementReadbackBytes -ne 0 -or
            [int64]$block.timestampInstrumentationReadbackBytes -ne
                ([int64]$expectedBlockSamples *
                    $TimestampBytesPerSample) -or
            [int64]$block.producerMaterializedWriteBytes -ne
                ([int64]$expectedBlockSamples *
                    [int64]$accounting.producer) -or
            [int64]$block.pipelineElementMaterializedWriteBytes -ne
                ([int64]$expectedBlockSamples *
                    [int64]$accounting.element) -or
            [int64]$block.spatialBuildAddressedReadBytes -ne
                ([int64]$expectedBlockSamples *
                    [int64]$accounting.spatialRead) -or
            [int64]$block.binCountAtomicOperations -ne
                ([int64]$expectedBlockSamples *
                    [int64]$accounting.countAtomics) -or
            [int64]$block.scatterAtomicOperations -ne
                ([int64]$expectedBlockSamples *
                    [int64]$accounting.scatterAtomics)) {
            throw (
                "Scenario '$scenarioId' block $($expected.blockIndex) " +
                "aggregate contract failed.")
        }
        Assert-Near (
            Number $block.gpuRegionAverageMs 'block GPU average') (
            Average $gpuValues) 'block GPU average'
        Assert-Near (
            Number $block.gpuRegionP99Ms 'block GPU p99') (
            Percentile $gpuValues 0.99) 'block GPU p99'
        Assert-Near (
            Number $block.frameAverageMs 'block frame average') (
            Average $frameValues) 'block frame average'
        Assert-Near (
            Number $block.frameP99Ms 'block frame p99') (
            Percentile $frameValues 0.99) 'block frame p99'
        if (@($cpuProducerValues | Where-Object {
                [Math]::Abs($_) -gt 0.000000001
            }).Count -ne 0 -or
            [Math]::Abs(
                (Number $block.cpuProducerAverageMs (
                    'block CPU producer average'))) -gt 0.000000001 -or
            [Math]::Abs(
                (Number $block.cpuProducerP99Ms (
                    'block CPU producer p99'))) -gt 0.000000001) {
            throw "Scenario '$scenarioId' unexpectedly has a CPU producer."
        }
    }

    if ($validation.Count -ne $ExpectedValidationRows) {
        throw "Scenario '$scenarioId' validation row count failed."
    }
    foreach ($row in $validation) {
        if ([int]$row.passed -ne 1 -or
            [int64]$row.readbackBytes -ne
                $ValidationBytesPerComparison -or
            [int]$row.stateCount -ne $FixedStateCount -or
            [uint32]$row.digestMismatchCount -ne 0 -or
            [uint32]$row.baselineInvalidKeyCount -ne 0 -or
            [uint32]$row.packedSampleMismatchCount -ne 0 -or
            [uint32]$row.packedCsrMismatchCount -ne 0 -or
            [uint32]$row.packedCsrElementCount -ne
                [uint32]$row.expectedPackedCsrElementCount -or
            [uint32]$row.packedCsrIdXor -ne
                [uint32]$row.expectedPackedCsrIdXor -or
            [uint32]$row.packedCsrIdSum -ne
                [uint32]$row.expectedPackedCsrIdSum -or
            [uint32]$row.packedCsrIdMixedSum -ne
                [uint32]$row.expectedPackedCsrIdMixedSum -or
            [uint32]$row.expectedPackedCsrElementCount -ne
                [uint32]$config.elementCount -or
            [string]$row.resultHash -cnotmatch
                '^(?:[0-9A-F]{8}-){13}[0-9A-F]{8}$') {
            throw "Scenario '$scenarioId' validation content failed."
        }
    }

    $preconditioningBlocks = @(
        $blocks | Where-Object {
            [string]$_.blockType -ceq 'precondition'
        } | Sort-Object { [int]$_.blockIndex })
    if ($preconditioningBlocks.Count -ne
            $ExpectedPreconditioningBlockCount -or
        [int]$preconditioningBlocks[0].blockIndex -ne 2 -or
        [string]$preconditioningBlocks[0].variant -cne
            $BaselineVariant -or
        [int]$preconditioningBlocks[1].blockIndex -ne 3 -or
        [string]$preconditioningBlocks[1].variant -cne
            $PackedVariant -or
        @($preconditioningBlocks | Where-Object {
            [int]$_.pairIndex -ne 0 -or
            [int]$_.withinPairPosition -ne 0
        }).Count -ne 0) {
        throw "Scenario '$scenarioId' preconditioning contract failed."
    }

    $caseFrameCount =
        8L * [int64]$config.sampleFrames +
        [int64]$PreconditioningSampleFramesPerBlock
    $expectedProducerBytes =
        $caseFrameCount *
        ([int64]$config.baselineProducerLogicalWriteBytesPerFrame +
         [int64]$config.packedProducerLogicalWriteBytesPerFrame)
    $expectedElementBytes =
        $caseFrameCount *
        ([int64]$config.
            baselinePipelineElementMaterializedWriteBytesPerFrame +
         [int64]$config.
            packedPipelineElementMaterializedWriteBytesPerFrame)
    $expectedSpatialReadBytes =
        $caseFrameCount *
        ([int64]$config.baselineSpatialBuildAddressedReadBytesPerFrame +
         [int64]$config.packedSpatialBuildAddressedReadBytesPerFrame)
    $expectedCountAtomics =
        $caseFrameCount *
        ([int64]$config.baselineCountAtomicOperationsPerFrame +
         [int64]$config.packedCountAtomicOperationsPerFrame)
    $expectedScatterAtomics =
        $caseFrameCount *
        ([int64]$config.
            baselineScatterReservationAtomicOperationsPerFrame +
         [int64]$config.
            packedScatterReservationAtomicOperationsPerFrame)

    if ([int]$run.schemaVersion -ne $SchemaVersion -or
        [string]$run.suite -cne $Suite -or
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
        [int64]$run.producerMaterializedWriteBytes -ne
            $expectedProducerBytes -or
        [int64]$run.pipelineElementMaterializedWriteBytes -ne
            $expectedElementBytes -or
        [int64]$run.spatialBuildAddressedReadBytes -ne
            $expectedSpatialReadBytes -or
        [int64]$run.binCountAtomicOperations -ne
            $expectedCountAtomics -or
        [int64]$run.scatterAtomicOperations -ne
            $expectedScatterAtomics -or
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
        [int]$run.baselineFinalStateKeyValidation -ne 1 -or
        [int]$run.packedSampleValidation -ne 1 -or
        [int]$run.packedCsrValidation -ne 1 -or
        [int]$run.allStateDigestComparison -ne 1 -or
        [int]$run.profilerMarkers -ne 1 -or
        [string]$run.performanceAttribution -cne
            ('combined-packed-soa-q16-producer-count-offset-cursor-' +
             'fusion-lazy-payload') -or
        [string]$run.digestComparisonCoverage -cne
            '64 aggregate frame digests; no full-buffer readback' -or
        [string]$run.csrValidationCoverage -cne
            ('in-place end-offset/count/membership plus ' +
             'count/xor/sum/mixed-sum invariants') -or
        [string]$run.gpuResidentAccountingCoverage -cne
            ('pipeline-owned GraphicsBuffers plus block digests; ' +
             'excludes timestamp/command/driver allocations') -or
        [int]$run.mainGraphicsQueueTimestamp -ne 1) {
        throw "Scenario '$scenarioId' run-summary contract failed."
    }
    Assert-ZeroKeys $run @(
        'hostUploadEliminationClaim',
        'uploadQueueCoverageVerified',
        'asyncComputeClaim',
        'copyQueueClaim',
        'pcieTrafficClaim',
        'measuredDramTrafficClaim',
        'driverReportedVramClaim',
        'endToEndSensorLatencyClaim',
        'liveSensorInputClaim',
        'sensorFidelityClaim',
        'citySceneClaim',
        'fpsClaim',
        'nvidiaValidationClaim') "scenario '$scenarioId' run-summary"

    $measurementBlocks = @(
        $blocks | Where-Object {
            [string]$_.blockType -ceq 'measurement'
        })
    if ($measurementBlocks.Count -ne $ExpectedScoredMeasurementBlockCount) {
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
        $baseline = @($pairBlocks | Where-Object {
            [string]$_.variant -ceq $BaselineVariant
        })
        $packed = @($pairBlocks | Where-Object {
            [string]$_.variant -ceq $PackedVariant
        })
        if ($baseline.Count -ne 1 -or $packed.Count -ne 1 -or
            [string]$pairBlocks[0].pairOrder -cne
                [string]$pairBlocks[1].pairOrder) {
            throw "Scenario '$scenarioId' pair $pairId lacks one A and one B."
        }
        $baselineGpuAverage =
            Number $baseline[0].gpuRegionAverageMs 'baseline GPU average'
        $packedGpuAverage =
            Number $packed[0].gpuRegionAverageMs 'packed GPU average'
        $baselineGpuP99 =
            Number $baseline[0].gpuRegionP99Ms 'baseline GPU p99'
        $packedGpuP99 =
            Number $packed[0].gpuRegionP99Ms 'packed GPU p99'
        $baselineFrameAverage =
            Number $baseline[0].frameAverageMs 'baseline frame average'
        $packedFrameAverage =
            Number $packed[0].frameAverageMs 'packed frame average'
        $baselineFrameP99 =
            Number $baseline[0].frameP99Ms 'baseline frame p99'
        $packedFrameP99 =
            Number $packed[0].frameP99Ms 'packed frame p99'
        $pairRow = [pscustomobject]@{
            scenarioId = $scenarioId
            processId = [int]$matrixRow.processId
            superRound = [int]$pairBlocks[0].superRound
            pairIndex = $pairId
            pairOrder = [string]$pairBlocks[0].pairOrder
            baselineBlockIndex = [int]$baseline[0].blockIndex
            packedBlockIndex = [int]$packed[0].blockIndex
            baselineGpuAverageMs = $baselineGpuAverage
            packedGpuAverageMs = $packedGpuAverage
            gpuAverageImprovementPercent =
                Improvement $baselineGpuAverage $packedGpuAverage (
                    'GPU average')
            gpuAverageAbsoluteReductionMs =
                $baselineGpuAverage - $packedGpuAverage
            baselineGpuP99Ms = $baselineGpuP99
            packedGpuP99Ms = $packedGpuP99
            gpuP99ImprovementPercent =
                Improvement $baselineGpuP99 $packedGpuP99 'GPU p99'
            baselineFrameAverageMs = $baselineFrameAverage
            packedFrameAverageMs = $packedFrameAverage
            frameAverageImprovementPercent =
                Improvement $baselineFrameAverage $packedFrameAverage (
                    'frame average')
            baselineFrameP99Ms = $baselineFrameP99
            packedFrameP99Ms = $packedFrameP99
            frameP99ImprovementPercent =
                Improvement $baselineFrameP99 $packedFrameP99 'frame p99'
        }
        $scenarioPairRows.Add($pairRow)
        $allPairRows.Add($pairRow)
    }

    $scenarioPairRows |
        Export-Csv -LiteralPath (
            Join-Path $scenarioRoot 'pair-summary.csv') `
            -NoTypeInformation -Encoding utf8
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
    [double[]]$frameAverageImprovements = @(
        $scenarioPairRows | ForEach-Object {
            [double]$_.frameAverageImprovementPercent
        })
    [double[]]$frameP99Improvements = @(
        $scenarioPairRows | ForEach-Object {
            [double]$_.frameP99ImprovementPercent
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
    $engineeringGatePassed =
        $pairCountGate -and
        $winCountGate -and
        $averagePercentGate -and
        $averageAbsoluteGate -and
        $p99Gate -and
        $orderBalanceGate -and
        $controlGate
    $classification = if ($engineeringGatePassed -and $formal) {
        'formal-engineering-gate-passed'
    }
    elseif ($engineeringGatePassed) {
        'exploratory-engineering-gate-passed'
    }
    elseif ($gpuAverageMedian -lt 0.0) {
        if ($formal) { 'formal-negative' } else { 'exploratory-negative' }
    }
    else {
        if ($formal) {
            'formal-neutral-or-inconclusive'
        }
        else {
            'exploratory-neutral-or-inconclusive'
        }
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
        formalHoldoutEvidence = [int]$formal
        engineeringGateLabel =
            'predeclared-engineering-gate-not-statistical-significance'
        engineeringGatePassed = [int]$engineeringGatePassed
        statisticalSignificanceClaim = 0
        gpuPerformanceClaimUsable =
            [int]($formal -and $engineeringGatePassed)
        gpuAverageWinningPairs = $winningPairs
        gpuAverageImprovementMedianPercent = $gpuAverageMedian
        gpuAverageAbsoluteReductionMedianMs = $gpuReductionMedian
        gpuP99ImprovementMedianPercent = $gpuP99Median
        frameAverageImprovementMedianPercent =
            Median $frameAverageImprovements
        frameP99ImprovementMedianPercent =
            Median $frameP99Improvements
        abGpuAverageImprovementMedianPercent = $abMedian
        baGpuAverageImprovementMedianPercent = $baMedian
        controlP99Ms = $controlP99
        baselineProducerLogicalWriteBytesPerFrame =
            [int64]$config.baselineProducerLogicalWriteBytesPerFrame
        packedProducerLogicalWriteBytesPerFrame =
            [int64]$config.packedProducerLogicalWriteBytesPerFrame
        producerLogicalWriteReductionPercent =
            Improvement (
                [double]$config.
                    baselineProducerLogicalWriteBytesPerFrame) (
                [double]$config.
                    packedProducerLogicalWriteBytesPerFrame) (
                'producer logical writes')
        baselinePipelineElementMaterializedWriteBytesPerFrame =
            [int64]$config.
                baselinePipelineElementMaterializedWriteBytesPerFrame
        packedPipelineElementMaterializedWriteBytesPerFrame =
            [int64]$config.
                packedPipelineElementMaterializedWriteBytesPerFrame
        pipelineElementMaterializedWriteReductionPercent =
            Improvement (
                [double]$config.
                    baselinePipelineElementMaterializedWriteBytesPerFrame) (
                [double]$config.
                    packedPipelineElementMaterializedWriteBytesPerFrame) (
                'pipeline element materialized writes')
        baselineSpatialBuildAddressedReadBytesPerFrame =
            [int64]$config.
                baselineSpatialBuildAddressedReadBytesPerFrame
        packedSpatialBuildAddressedReadBytesPerFrame =
            [int64]$config.
                packedSpatialBuildAddressedReadBytesPerFrame
        spatialBuildAddressedReadReductionPercent =
            Improvement (
                [double]$config.
                    baselineSpatialBuildAddressedReadBytesPerFrame) (
                [double]$config.
                    packedSpatialBuildAddressedReadBytesPerFrame) (
                'spatial-build addressed reads')
        baselineTotalAtomicOperationsPerFrame =
            [int64]$config.baselineTotalAtomicOperationsPerFrame
        packedTotalAtomicOperationsPerFrame =
            [int64]$config.packedTotalAtomicOperationsPerFrame
        baselinePipelineResidentBytes =
            [int64]$config.baselinePipelineResidentBytes
        packedPipelineResidentBytes =
            [int64]$config.packedPipelineResidentBytes
        baselineIsolatedGpuResidentBytes =
            [int64]$config.baselineIsolatedGpuResidentBytes
        packedIsolatedGpuResidentBytes =
            [int64]$config.packedIsolatedGpuResidentBytes
        blockDigestBufferBytes =
            [int64]$config.blockDigestBufferBytes
        actualBenchmarkGpuResidentBytes =
            [int64]$config.actualBenchmarkGpuResidentBytes
        measurementWorkloadReadbackBytes = 0L
        validationReadbackBytesPerComparison =
            $ValidationBytesPerComparison
        timestampInstrumentationReadbackBytes =
            [int64]$run.timestampInstrumentationReadbackBytes
        graphicsDeviceName = [string]$device.graphicsDeviceName
        graphicsDeviceVendorId = [int]$device.graphicsDeviceVendorId
        graphicsDeviceId = [int]$device.graphicsDeviceId
        activeRendererName = [string]$playerRenderer.rendererName
        activeRendererIdHex = [string]$playerRenderer.rendererIdHex
        activeRendererId = [int]$playerRenderer.rendererId
        activeDriverVersion = [string]$playerRenderer.driverVersion
        activeGraphicsApi = [string]$playerRenderer.graphicsApi
        playerLogSha256 = [string]$playerRenderer.logSha256
        luidAvailable = 0
        luidBoundary = [string]$gpuContract.luidBoundary
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
            label =
                'predeclared-engineering-gate-not-statistical-significance'
            statisticalSignificanceClaim = $false
            passed = [bool]$engineeringGatePassed
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
$allEngineeringGatesPassed =
    @($scenarioRows | Where-Object {
        [int]$_.engineeringGatePassed -ne 1
    }).Count -eq 0
$crossWorkloadGpuPerformanceClaimUsable = [bool](
    $formal -and
    $allEvidenceValid -and
    $allEngineeringGatesPassed)
$quality = [ordered]@{
    schemaVersion = $SchemaVersion
    suite = $Suite
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    reportDirectory = $root
    formalAcceptanceMode = $formal
    matrixPreset = [string]$runner.matrixPreset
    matrixRole = [string]$runner.matrixRole
    gitCommit = [string]$runner.gitCommit
    gitBranch = [string]$runner.gitBranch
    signedImprovementConvention =
        'positive-packed-soa-fused-end-cursor-faster'
    preconditioning = [ordered]@{
        blockCount = $ExpectedPreconditioningBlockCount
        sampleFramesPerBlock = $PreconditioningSampleFramesPerBlock
        order = 'expanded-aos-quantized-view,packed-soa-fused-end-cursor'
        excludedFromScoring = $true
        gpuTimingsRecorded = $true
    }
    evidenceValid = [bool]$allEvidenceValid
    scenarioCount = $scenarioRows.Count
    pairCount = $allPairRows.Count
    crossWorkloadGpuPerformanceClaimUsable =
        $crossWorkloadGpuPerformanceClaimUsable
    discoveryPerformanceClaimUsable = $false
    allEngineeringGatesPassed =
        [bool]$allEngineeringGatesPassed
    engineeringGateLabel =
        'predeclared-engineering-gate-not-statistical-significance'
    statisticalSignificanceClaim = $false
    logicalAccountingEvidenceUsable =
        [bool]$allEvidenceValid
    claimBoundary = [ordered]@{
        gpuMetric =
            'D3D12 main-graphics-command-list timestamp interval'
        logicalAccounting =
            'Algorithmic shader-visible typed buffer operations; not ' +
            'measured DRAM transactions.'
        performanceAttribution =
            'combined-packed-soa-q16-producer-count-offset-cursor-' +
            'fusion-lazy-payload'
        digestComparisonCoverage =
            '64 aggregate frame digests; no full-buffer readback'
        csrValidationCoverage =
            'in-place end-offset/count/membership plus ' +
            'count/xor/sum/mixed-sum invariants'
        gpuResidentAccountingCoverage =
            'pipeline-owned GraphicsBuffers plus block digests; ' +
            'excludes timestamp/command/driver allocations'
        residentByteAccounting =
            'Explicit GraphicsBuffer capacities; not driver allocation.'
        workloadReadbackBytesPerFrame = 0
        validationReadbackBytesPerComparison =
            $ValidationBytesPerComparison
        hostUploadEliminationClaim = $false
        uploadQueueCoverageVerified = $false
        asyncComputeClaim = $false
        copyQueueClaim = $false
        pcieTrafficClaim = $false
        measuredDramTrafficClaim = $false
        driverReportedVramClaim = $false
        endToEndSensorLatencyClaim = $false
        liveSensorInputClaim = $false
        sensorFidelityClaim = $false
        citySceneClaim = $false
        fpsClaim = $false
        nvidiaValidationClaim = $false
        statisticalSignificanceClaim = $false
        luidAvailable = $false
        luidBoundary = [string]$gpuContract.luidBoundary
    }
    engineeringGate = [ordered]@{
        label =
            'predeclared-engineering-gate-not-statistical-significance'
        statisticalSignificanceClaim = $false
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
$qualityLines.Add('GPU sensor data packing and fusion quality summary')
$qualityLines.Add("schemaVersion=$SchemaVersion")
$qualityLines.Add("suite=$Suite")
$qualityLines.Add("evidenceValid=$([int]$allEvidenceValid)")
$qualityLines.Add("formalAcceptanceMode=$([int]$formal)")
$qualityLines.Add("matrixPreset=$($runner.matrixPreset)")
$qualityLines.Add("matrixRole=$($runner.matrixRole)")
$qualityLines.Add("scenarioCount=$($scenarioRows.Count)")
$qualityLines.Add("pairCount=$($allPairRows.Count)")
$qualityLines.Add(
    "crossWorkloadGpuPerformanceClaimUsable=" +
    "$([int]$crossWorkloadGpuPerformanceClaimUsable)")
$qualityLines.Add('discoveryPerformanceClaimUsable=0')
$qualityLines.Add(
    "allEngineeringGatesPassed=$([int]$allEngineeringGatesPassed)")
$qualityLines.Add(
    'engineeringGateLabel=' +
    'predeclared-engineering-gate-not-statistical-significance')
$qualityLines.Add('statisticalSignificanceClaim=0')
$qualityLines.Add('luidAvailable=0')
$qualityLines.Add("luidBoundary=$($gpuContract.luidBoundary)")
$qualityLines.Add(
    "logicalAccountingEvidenceUsable=$([int]$allEvidenceValid)")
$qualityLines.Add(
    'gpuMetric=D3D12 main-graphics-command-list timestamp interval')
$qualityLines.Add(
    'logicalAccounting=algorithmic-typed-operations-not-measured-dram')
$qualityLines.Add(
    'residentByteAccounting=graphics-buffer-capacity-not-driver-allocation')
$qualityLines.Add('workloadReadbackBytesPerFrame=0')
$qualityLines.Add(
    "validationReadbackBytesPerComparison=$ValidationBytesPerComparison")
foreach ($claim in @(
    'hostUploadEliminationClaim',
    'uploadQueueCoverageVerified',
    'asyncComputeClaim',
    'copyQueueClaim',
    'pcieTrafficClaim',
    'measuredDramTrafficClaim',
    'driverReportedVramClaim',
    'endToEndSensorLatencyClaim',
    'liveSensorInputClaim',
    'sensorFidelityClaim',
    'citySceneClaim',
    'fpsClaim',
    'nvidiaValidationClaim',
    'statisticalSignificanceClaim')) {
    $qualityLines.Add("$claim=0")
}
foreach ($scenario in $scenarioRows) {
    $prefix =
        ([string]$scenario.scenarioId -replace '[^A-Za-z0-9_]', '_')
    foreach ($field in @(
        'classification',
        'formalHoldoutEvidence',
        'engineeringGatePassed',
        'statisticalSignificanceClaim',
        'gpuPerformanceClaimUsable',
        'gpuAverageWinningPairs',
        'gpuAverageImprovementMedianPercent',
        'gpuAverageAbsoluteReductionMedianMs',
        'gpuP99ImprovementMedianPercent',
        'frameAverageImprovementMedianPercent',
        'frameP99ImprovementMedianPercent',
        'producerLogicalWriteReductionPercent',
        'pipelineElementMaterializedWriteReductionPercent',
        'spatialBuildAddressedReadReductionPercent')) {
        $qualityLines.Add(
            "$prefix.$field=$($scenario.$field)")
    }
}
$qualityTextPath = Join-Path $root 'quality-summary.txt'
[System.IO.File]::WriteAllLines(
    $qualityTextPath,
    $qualityLines,
    [System.Text.UTF8Encoding]::new($false))

Write-Host (
    "Validated GPU sensor data-packing evidence: scenarios=" +
    "$($scenarioRows.Count), pairs=$($allPairRows.Count), " +
    "formalClaimUsable=$crossWorkloadGpuPerformanceClaimUsable, " +
    "allEngineeringGatesPassed=$allEngineeringGatesPassed")
