[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReportDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [System.IO.Path]::GetFullPath($ReportDirectory)

function Require-File {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required report file is missing: $Path"
    }
    return $Path
}

function Read-KeyValues {
    param([Parameter(Mandatory = $true)][string]$Path)
    $result = @{}
    foreach ($line in Get-Content -LiteralPath (Require-File $Path)) {
        $parts = $line -split '=', 2
        if ($parts.Count -eq 2) {
            $result[$parts[0]] = $parts[1]
        }
    }
    return $result
}

function Require-Columns {
    param(
        [Parameter(Mandatory = $true)][object[]]$Rows,
        [Parameter(Mandatory = $true)][string[]]$Names,
        [Parameter(Mandatory = $true)][string]$Label
    )
    if ($Rows.Count -eq 0) {
        throw "$Label contains no rows."
    }
    $columns = @($Rows[0].PSObject.Properties.Name)
    foreach ($name in $Names) {
        if ($name -notin $columns) {
            throw "$Label is missing required column '$name'."
        }
    }
}

function Number {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string]$Label
    )
    $parsed = 0.0
    if (-not [double]::TryParse(
            [string]$Value,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        throw "Unable to parse $Label='$Value' as a finite number."
    }
    if ([double]::IsNaN($parsed) -or [double]::IsInfinity($parsed)) {
        throw "Unable to parse $Label='$Value' as a finite number."
    }
    return $parsed
}

function Percentile {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [double[]]$Values,
        [Parameter(Mandatory = $true)][double]$P
    )
    if ($Values.Count -eq 0) { return -1.0 }
    $sorted = [double[]]@($Values | Sort-Object)
    $index = [Math]::Max(
        0,
        [Math]::Min(
            $sorted.Count - 1,
            [Math]::Ceiling($sorted.Count * $P) - 1))
    return $sorted[$index]
}

function Median {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [double[]]$Values
    )
    if ($Values.Count -eq 0) { return -1.0 }
    $sorted = [double[]]@($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2.0)
    if (($sorted.Count -band 1) -ne 0) {
        return $sorted[$middle]
    }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2.0
}

function Improvement {
    param(
        [Parameter(Mandatory = $true)][double]$Baseline,
        [Parameter(Mandatory = $true)][double]$Candidate
    )
    if ([Math]::Abs($Baseline) -lt 1.0e-12) { return 0.0 }
    return ($Baseline - $Candidate) / $Baseline * 100.0
}

function Assert-Near {
    param(
        [Parameter(Mandatory = $true)][double]$Actual,
        [Parameter(Mandatory = $true)][double]$Expected,
        [Parameter(Mandatory = $true)][string]$Label,
        [double]$Tolerance = 0.000000001
    )
    if ([Math]::Abs($Actual - $Expected) -gt $Tolerance) {
        throw "$Label mismatch: actual=$Actual expected=$Expected"
    }
}

function Expected-Blocks {
    param([Parameter(Mandatory = $true)][int]$SuperRounds)
    $result = [System.Collections.Generic.List[object]]::new()
    $block = 1
    $pair = 1
    $result.Add([pscustomobject]@{
        blockIndex = $block++
        blockType = 'control-pre'
        superRound = 0
        sequencePosition = 0
        pairIndex = 0
        pairOrder = ''
        withinPairPosition = 0
        variant = 'empty-command-buffer'
    })
    for ($round = 1; $round -le $SuperRounds; $round++) {
        $variants = if (($round -band 1) -ne 0) {
            @(
                'reference-compose-portable',
                'direct-count-scan-scatter-portable',
                'direct-count-scan-scatter-portable',
                'reference-compose-portable')
        }
        else {
            @(
                'direct-count-scan-scatter-portable',
                'reference-compose-portable',
                'reference-compose-portable',
                'direct-count-scan-scatter-portable')
        }
        for ($position = 1; $position -le 4; $position++) {
            $within = (($position - 1) % 2) + 1
            $first = $variants[[int][Math]::Floor(($position - 1) / 2) * 2]
            $order = if ($first -eq 'reference-compose-portable') {
                'AB'
            }
            else { 'BA' }
            $result.Add([pscustomobject]@{
                blockIndex = $block++
                blockType = 'measurement'
                superRound = $round
                sequencePosition = $position
                pairIndex = $pair
                pairOrder = $order
                withinPairPosition = $within
                variant = $variants[$position - 1]
            })
            if ($within -eq 2) { $pair++ }
        }
    }
    $result.Add([pscustomobject]@{
        blockIndex = $block
        blockType = 'control-post'
        superRound = 0
        sequencePosition = 0
        pairIndex = 0
        pairOrder = ''
        withinPairPosition = 0
        variant = 'empty-command-buffer'
    })
    return @($result)
}

$runnerPath = Require-File (Join-Path $root 'runner-config.json')
$runner = Get-Content -LiteralPath $runnerPath -Raw | ConvertFrom-Json
if ([int]$runner.schemaVersion -ne 5 -or
    [string]$runner.suite -cne 'summit.gpu-direct-binning' -or
    [int]$runner.benchmarkSchemaVersion -ne 1) {
    throw 'Runner schema/suite contract does not match direct-binning v1.'
}
if (-not [bool]$runner.runnerConfigFinalized) {
    throw 'runner-config.json is not finalized.'
}
$formal = [bool]$runner.formalAcceptanceMode
if ($formal) {
    foreach ($gate in @(
        'formalContractSatisfied',
        'sourceHashesStableAcrossBuild',
        'playerPayloadStableThroughRun')) {
        if (-not [bool]$runner.$gate) {
            throw "Formal runner gate '$gate' is false."
        }
    }
    if ([bool]$runner.gitTreeDirty -or
        [bool]$runner.gitFinal.dirty) {
        throw 'Formal provenance reports a dirty final worktree.'
    }
    if ([string]$runner.gitCommit -notmatch '^[0-9a-fA-F]{40}$' -or
        [string]$runner.gitBranch -eq 'HEAD') {
        throw 'Formal provenance lacks a named branch/full Git commit.'
    }
    if ($null -eq $runner.editModeResults -or
        [string]$runner.editModeResults.result -cne 'Passed' -or
        [int]$runner.editModeResults.total -le 0 -or
        [int]$runner.editModeResults.passed -ne
            [int]$runner.editModeResults.total -or
        [int]$runner.editModeResults.failed -ne 0 -or
        [int]$runner.editModeResults.skipped -ne 0 -or
        [int]$runner.editModeResults.inconclusive -ne 0) {
        throw 'Formal EditMode evidence is not fully passed.'
    }
    if (@($runner.windowsVideoControllers).Count -eq 0) {
        throw 'Formal provenance lacks Windows video-controller inventory.'
    }
    $expectedFormalNumbers = [ordered]@{
        deviceIndex = 0
        superRounds = 2
        warmupFrames = 60
        sampleFrames = 900
        cooldownFrames = 15
        dispatchesPerFrame = 1
    }
    foreach ($field in $expectedFormalNumbers.Keys) {
        if ($null -eq $runner.PSObject.Properties[$field] -or
            [int64]$runner.$field -ne
                [int64]$expectedFormalNumbers[$field]) {
            throw (
                "Formal runner field '$field' is '$($runner.$field)'; " +
                "expected '$($expectedFormalNumbers[$field])'.")
        }
    }
    if ([string]$runner.matrixPreset -cne 'amd-r9700-v1') {
        throw (
            "Formal runner field 'matrixPreset' is " +
            "'$($runner.matrixPreset)'; expected 'amd-r9700-v1'.")
    }
    foreach ($field in @(
        'projectUnityVersion',
        'unityEditorResolvedVersion')) {
        if ([string]$runner.$field -cne '6000.5.2f1') {
            throw (
                "Formal runner field '$field' is '$($runner.$field)'; " +
                "expected '6000.5.2f1'.")
        }
    }
    if (-not [bool]$runner.requireCompleteGpuTimings) {
        throw 'Formal runner must require complete GPU timings.'
    }
    if (@($runner.scenarios).Count -ne 3 -or
        @($runner.playerRuns).Count -ne 3) {
        throw 'Formal runner requires exactly three scenarios/Player runs.'
    }
    $expectedEditModeIdentities = @(
        'Summit.GpuDirectBinning.Tests.Editor.dll',
        'Summit.GpuDirectBinning.Tests.CpuDirectBinningOracleTests',
        'Summit.GpuDirectBinning.Tests.GpuDirectSpatialBinnerContractTests',
        'Summit.GpuDirectBinning.Tests.GpuDirectSpatialBinnerIntegrationTests',
        'Summit.GpuDirectBinning.Benchmark.Tests.Editor.dll',
        'Summit.GpuDirectBinning.Benchmark.Tests.GpuDirectBinningBenchmarkScheduleTests',
        'Summit.GpuDirectBinning.Benchmark.Tests.GpuDirectBinningInputDistributionTests',
        'Summit.GpuDirectBinning.Benchmark.Tests.GpuDirectBinningCpuOracleTests'
    )
    $runnerExpectedIdentities =
        [string[]]@($runner.editModeResults.expectedIdentities)
    $runnerObservedIdentities =
        [string[]]@($runner.editModeResults.observedIdentities)
    if (
        [string]$runner.editModeResults.identityMatchMode -cne
            'exact-nunit-node-v1' -or
        $runnerExpectedIdentities.Count -ne
            $expectedEditModeIdentities.Count -or
        $runnerObservedIdentities.Count -lt
            $expectedEditModeIdentities.Count) {
        throw 'Formal EditMode identity metadata is inconsistent.'
    }
    foreach ($identity in $expectedEditModeIdentities) {
        if (-not ($runnerExpectedIdentities -ccontains $identity) -or
            -not ($runnerObservedIdentities -ccontains $identity)) {
            throw "Formal EditMode metadata lacks exact identity '$identity'."
        }
    }
    $copiedEditModePath =
        [System.IO.Path]::GetFullPath(
            (Join-Path $root 'editmode-results.xml'))
    if ([string]$runner.editModeResults.copiedPath -ine
        $copiedEditModePath -or
        [string]::IsNullOrWhiteSpace(
            [string]$runner.editModeResults.sourceLastWriteUtc) -or
        [string]$runner.editModeResults.sourceSha256 -cne
            [string]$runner.editModeResults.copiedSha256 -or
        @($runner.editModeResults.missingIdentities).Count -ne 0) {
        throw 'Formal EditMode evidence provenance is inconsistent.'
    }
    $copiedEditModePath = Require-File $copiedEditModePath
    $copiedEditModeSha =
        (Get-FileHash -LiteralPath $copiedEditModePath -Algorithm SHA256).Hash
    if ($copiedEditModeSha -cne
        [string]$runner.editModeResults.copiedSha256) {
        throw 'Formal copied EditMode XML hash is inconsistent.'
    }
    [xml]$editModeXml =
        Get-Content -LiteralPath $copiedEditModePath -Raw
    $xmlIdentitySet =
        [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::Ordinal)
    foreach ($assembly in @(
        $editModeXml.SelectNodes("//test-suite[@type='Assembly']"))) {
        [void]$xmlIdentitySet.Add([string]$assembly.name)
    }
    foreach ($fixture in @(
        $editModeXml.SelectNodes("//test-suite[@type='TestFixture']"))) {
        [void]$xmlIdentitySet.Add([string]$fixture.fullname)
    }
    foreach ($identity in $expectedEditModeIdentities) {
        if (-not $xmlIdentitySet.Contains($identity)) {
            throw "Formal EditMode XML lacks exact identity '$identity'."
        }
    }
}

$matrix = @(
    Import-Csv -LiteralPath (
        Require-File (Join-Path $root 'matrix.csv')))
$configuredScenarios = @($runner.scenarios)
if ($matrix.Count -ne $configuredScenarios.Count) {
    throw (
        "Matrix row count $($matrix.Count) does not match configured " +
        "scenario count $($configuredScenarios.Count).")
}
if ($formal) {
    $frozen = @(
        'uniform-c4096|1048576|4096|uniform|20260730',
        'hotset16-c4096|1048576|4096|hotset16|20260731',
        'uniform-c65536|1048576|65536|uniform|20260732')
    $actual = @(
        $matrix | ForEach-Object {
            "$($_.scenarioId)|$($_.elementCount)|$($_.binCount)|" +
            "$($_.distribution)|$($_.seed)"
        })
    if (($actual -join ';') -cne ($frozen -join ';')) {
        throw 'Formal AMD matrix differs from the frozen v1 matrix.'
    }
}

$rootSummaries = [System.Collections.Generic.List[object]]::new()
$allExploratoryUsable = $true
$allAccepted = $true
$activeDeviceIdentity = $null
foreach ($matrixRow in $matrix) {
    $scenarioId = [string]$matrixRow.scenarioId
    $scenarioRoot = Join-Path $root $scenarioId
    $config = Get-Content -LiteralPath (
        Require-File (Join-Path $scenarioRoot 'config.json')) -Raw |
        ConvertFrom-Json
    $device = Get-Content -LiteralPath (
        Require-File (Join-Path $scenarioRoot 'device.json')) -Raw |
        ConvertFrom-Json
    $run = Read-KeyValues (
        Join-Path $scenarioRoot 'run-summary.txt')
    $raw = @(
        Import-Csv -LiteralPath (
            Require-File (Join-Path $scenarioRoot 'raw-frames.csv')))
    $blocks = @(
        Import-Csv -LiteralPath (
            Require-File (Join-Path $scenarioRoot 'block-summary.csv')))
    $validation = @(
        Import-Csv -LiteralPath (
            Require-File (Join-Path $scenarioRoot 'validation.csv')))

    $runnerScenarios = @(
        $runner.scenarios | Where-Object {
            [string]$_.scenarioId -ceq $scenarioId
        })
    if ($runnerScenarios.Count -ne 1) {
        throw "Scenario '$scenarioId' lacks one runner.scenarios row."
    }
    $runnerScenario = $runnerScenarios[0]
    if ([int]$runnerScenario.elementCount -ne [int]$matrixRow.elementCount -or
        [int]$runnerScenario.binCount -ne [int]$matrixRow.binCount -or
        [string]$runnerScenario.distribution -cne
            [string]$matrixRow.distribution -or
        [int]$runnerScenario.seed -ne [int]$matrixRow.seed) {
        throw "Scenario '$scenarioId' runner workload differs from matrix.csv."
    }
    $runnerPlayerRows = @(
        $runner.playerRuns | Where-Object {
            [string]$_.scenarioId -ceq $scenarioId
        })
    if ($runnerPlayerRows.Count -ne 1) {
        throw "Scenario '$scenarioId' lacks one runner.playerRuns row."
    }
    $runnerPlayer = $runnerPlayerRows[0]
    $runnerReportDirectory =
        [System.IO.Path]::GetFullPath(
            [string]$runnerPlayer.reportDirectory)
    if ([string]$runnerPlayer.processId -cne
            [string]$matrixRow.processId -or
        $runnerReportDirectory -ine
            [System.IO.Path]::GetFullPath($scenarioRoot)) {
        throw "Scenario '$scenarioId' Player binding is inconsistent."
    }

    if ([int]$config.schemaVersion -ne 1 -or
        [string]$config.suite -cne 'summit.gpu-direct-binning' -or
        [string]$config.scenarioId -cne $scenarioId) {
        throw "Scenario '$scenarioId' config schema is inconsistent."
    }
    if ([int]$config.elementCount -ne [int]$matrixRow.elementCount -or
        [int]$config.binCount -ne [int]$matrixRow.binCount -or
        [string]$config.distribution -cne
            [string]$matrixRow.distribution -or
        [int]$config.seed -ne [int]$matrixRow.seed) {
        throw (
            "Scenario '$scenarioId' config workload differs from matrix.csv.")
    }
    if ([string]$config.processId -cne [string]$matrixRow.processId) {
        throw (
            "Scenario '$scenarioId' config PID differs from matrix.csv.")
    }
    foreach ($field in @(
        'superRounds',
        'warmupFrames',
        'sampleFrames',
        'cooldownFrames',
        'dispatchesPerFrame')) {
        if ([int]$config.$field -ne [int]$runner.$field) {
            throw (
                "Scenario '$scenarioId' config field '$field' differs " +
                "from runner-config.json.")
        }
    }
    if ([string]$config.scanBackend -cne 'portable' -or
        [string]$config.scheduleContract -cne
            'control-pre;ABBA;BAAB;control-post' -or
        [string]$config.buildCommit -cne [string]$runner.gitCommit -or
        [string]$config.runtimeShaderSha256 -cne
            [string]$runner.runtimeShaderSha256 -or
        [string]$config.referenceShaderSha256 -cne
            [string]$runner.referenceShaderSha256 -or
        [string]$config.runtimeApiSha256 -cne
            [string]$runner.runtimeApiSha256) {
        throw "Scenario '$scenarioId' config provenance is inconsistent."
    }
    if (-not [bool]$config.requireCompleteGpuTimings -or
        -not [bool]$config.caseLocalWarmup -or
        -not [bool]$config.sameProcessPaired -or
        -not [bool]$config.informationalPlayerLogsSuppressed -or
        [string]$config.baselineId -cne
            'reference-compose-portable-v1' -or
        [string]$config.directId -cne
            'direct-count-scan-scatter-portable-v1') {
        throw (
            "Scenario '$scenarioId' benchmark comparison contract is " +
            'inconsistent.')
    }
    if ([int]$run.passed -ne 1 -or
        $run.status -notin @('completed', 'completed-correctness-only')) {
        throw "Scenario '$scenarioId' did not complete successfully."
    }
    if ($formal -and $run.status -cne 'completed') {
        throw "Formal scenario '$scenarioId' lacks complete GPU timings."
    }
    if ([string]$device.graphicsDeviceType -cne 'Direct3D12') {
        throw "Scenario '$scenarioId' did not run on Direct3D12."
    }
    if ($formal -and
        ([int]$device.graphicsDeviceVendorId -ne 4098 -or
            [string]$device.graphicsDeviceName -cne
                'AMD Radeon AI PRO R9700')) {
        throw (
            "Formal scenario '$scenarioId' expected " +
            "'AMD Radeon AI PRO R9700'; observed " +
            "'$($device.graphicsDeviceName)' vendorId=" +
            "'$($device.graphicsDeviceVendorId)'.")
    }
    $matchingDriverRows = @(
        $runner.windowsVideoControllers | Where-Object {
            [string]$_.name -ceq [string]$device.graphicsDeviceName
        })
    if ($formal -and $matchingDriverRows.Count -ne 1) {
        throw (
            "Formal scenario '$scenarioId' requires exactly one matching " +
            "Windows driver inventory row.")
    }
    if ($formal -and
        ([string]::IsNullOrWhiteSpace(
                [string]$matchingDriverRows[0].driverVersion) -or
            [string]::IsNullOrWhiteSpace(
                [string]$matchingDriverRows[0].pnpDeviceId))) {
        throw (
            "Formal scenario '$scenarioId' lacks complete Windows driver " +
            'identity evidence.')
    }
    $scenarioDeviceIdentity =
        "$($device.graphicsDeviceType)|" +
        "$($device.graphicsDeviceVendorId)|" +
        "$($device.graphicsDeviceId)|" +
        "$($device.graphicsDeviceName)"
    if ($null -eq $activeDeviceIdentity) {
        $activeDeviceIdentity = $scenarioDeviceIdentity
    }
    elseif ($scenarioDeviceIdentity -cne $activeDeviceIdentity) {
        throw (
            "Scenario '$scenarioId' active graphics device differs from " +
            'the earlier matrix scenarios.')
    }
    if ([int]$config.nativeTimestampAbiVersion -ne 2 -or
        (([uint32]$config.nativeTimestampCapabilityFlags -band 0x1F) -ne 0x1F) -or
        -not [bool]$config.nativeTimestampWarmupPassed -or
        [string]$config.nativeTimestampWarmupStatus -cne 'ready') {
        throw "Scenario '$scenarioId' native timestamp contract failed."
    }
    if ([int64]$config.logicalProblemBytesPerDispatch -ne
        (12L * [int64]$config.elementCount +
            8L * [int64]$config.binCount + 12L)) {
        throw "Scenario '$scenarioId' logical-byte accounting is invalid."
    }
    if ([int64]$config.sharedInputBytes -ne
        8L * [int64]$config.elementCount -or
        [int64]$config.sharedOutputBytes -ne
        (4L * [int64]$config.elementCount +
            8L * [int64]$config.binCount + 12L)) {
        throw "Scenario '$scenarioId' shared buffer accounting is invalid."
    }
    [int64]$expectedInternalScratchBytes =
        4L * [int64]$config.binCount
    if ([int64]$config.sharedInputBytes -lt 0 -or
        [int64]$config.sharedOutputBytes -lt 0 -or
        [int64]$config.primitiveScratchBytes -lt 0 -or
        [int64]$config.directInternalScratchBytes -lt 0 -or
        [int64]$config.referenceInternalScratchBytes -lt 0 -or
        [int64]$config.directInternalScratchBytes -ne
            $expectedInternalScratchBytes -or
        [int64]$config.referenceInternalScratchBytes -ne
            $expectedInternalScratchBytes) {
        throw (
            "Scenario '$scenarioId' scratch-byte accounting is invalid.")
    }
    [int64]$directCaseResidentBytes =
        [int64]$config.sharedInputBytes +
        [int64]$config.sharedOutputBytes +
        [int64]$config.primitiveScratchBytes +
        [int64]$config.directInternalScratchBytes
    [int64]$referenceCaseResidentBytes =
        [int64]$config.sharedInputBytes +
        [int64]$config.sharedOutputBytes +
        [int64]$config.primitiveScratchBytes +
        [int64]$config.referenceInternalScratchBytes
    if ([int64]$config.directCaseResidentBytes -ne
            $directCaseResidentBytes -or
        [int64]$config.referenceCaseResidentBytes -ne
            $referenceCaseResidentBytes) {
        throw (
            "Scenario '$scenarioId' case-resident accounting is invalid.")
    }
    if ([int64]$config.actualBenchmarkBufferResidentBytes -ne
        ([int64]$config.sharedInputBytes +
            [int64]$config.sharedOutputBytes +
            [int64]$config.primitiveScratchBytes +
            [int64]$config.directInternalScratchBytes +
            [int64]$config.referenceInternalScratchBytes)) {
        throw "Scenario '$scenarioId' resident-byte accounting is invalid."
    }

    Require-Columns $raw @(
        'processId','scenarioId','superRound','sequencePosition',
        'pairIndex','pairOrder','withinPairPosition','blockIndex',
        'blockType','caseId','variant','sampleIndex','sourceUnityFrame',
        'resultUnityFrame','nativeTimestampToken','nativeTimestampUserTag',
        'nativeTimestampFlags','nativeTimestampStatus',
        'nativeTimestampBeginTicks','nativeTimestampEndTicks',
        'nativeTimestampElapsedTicks','nativeTimestampFrequency',
        'nativeTimestampElapsedNanoseconds','nativeTimestampFenceValue',
        'nativeTimestampDeviceGeneration','gpuRegionElapsedMs',
        'measurementReadbackBytes','timestampInstrumentationReadbackBytes'
    ) "$scenarioId/raw-frames.csv"
    Require-Columns $blocks @(
        'blockIndex','blockType','superRound','sequencePosition',
        'pairIndex','pairOrder','withinPairPosition','variant','samples',
        'gpuRegionValidSamples','gpuRegionAverageMs','gpuRegionP99Ms',
        'caseResidentBytes','fenceSupported','fencePassed',
        'measurementReadbackBytes'
    ) "$scenarioId/block-summary.csv"

    $expectedBlocks =
        @(Expected-Blocks -SuperRounds ([int]$config.superRounds))
    if ($blocks.Count -ne $expectedBlocks.Count) {
        throw "Scenario '$scenarioId' block count is invalid."
    }
    $expectedRawCount =
        $expectedBlocks.Count * [int]$config.sampleFrames
    if ($raw.Count -ne $expectedRawCount) {
        throw (
            "Scenario '$scenarioId' raw row count is $($raw.Count); " +
            "expected $expectedRawCount.")
    }

    $processIds = @($raw | Select-Object -ExpandProperty processId -Unique)
    if ($processIds.Count -ne 1 -or
        [string]$processIds[0] -cne [string]$matrixRow.processId) {
        throw "Scenario '$scenarioId' does not have one matching Player PID."
    }

    $observedTokens = [System.Collections.Generic.HashSet[uint64]]::new()
    $observedTags = [System.Collections.Generic.HashSet[uint64]]::new()
    [uint64]$previousFence = [uint64]$config.nativeTimestampWarmupFenceValue
    [uint64]$observedFrequency = 0
    [uint64]$previousEndTicks = 0
    [int]$previousSourceUnityFrame = -1
    [int]$globalRowIndex = 0
    $allGpuValues = [System.Collections.Generic.List[double]]::new()
    for ($blockPosition = 0;
        $blockPosition -lt $expectedBlocks.Count;
        $blockPosition++) {
        $expected = $expectedBlocks[$blockPosition]
        $block = $blocks[$blockPosition]
        foreach ($field in @(
            'blockIndex','superRound','sequencePosition','pairIndex',
            'withinPairPosition')) {
            if ([int]$block.$field -ne [int]$expected.$field) {
                throw (
                    "Scenario '$scenarioId' block $($expected.blockIndex) " +
                    "has invalid $field.")
            }
        }
        foreach ($field in @('blockType','pairOrder','variant')) {
            if ([string]$block.$field -cne [string]$expected.$field) {
                throw (
                    "Scenario '$scenarioId' block $($expected.blockIndex) " +
                    "has invalid $field.")
            }
        }
        if ([int]$block.samples -ne [int]$config.sampleFrames -or
            [int]$block.gpuRegionValidSamples -ne [int]$block.samples) {
            throw "Scenario '$scenarioId' block timing completeness failed."
        }
        if ($formal -and
            ([int]$block.fenceSupported -ne 1 -or
                [int]$block.fencePassed -ne 1)) {
            throw "Scenario '$scenarioId' block completion fence failed."
        }
        if ([int64]$block.measurementReadbackBytes -ne 0) {
            throw "Scenario '$scenarioId' performed measurement readback."
        }
        [int64]$expectedCaseResidentBytes =
            if ($expected.variant -ceq
                'direct-count-scan-scatter-portable') {
                $directCaseResidentBytes
            }
            elseif ($expected.variant -ceq 'reference-compose-portable') {
                $referenceCaseResidentBytes
            }
            else { 0L }
        if ([int64]$block.caseResidentBytes -ne
            $expectedCaseResidentBytes) {
            throw "Scenario '$scenarioId' block resident bytes are invalid."
        }

        $rows = @(
            $raw | Where-Object {
                [int]$_.blockIndex -eq [int]$expected.blockIndex
            } | Sort-Object { [int]$_.sampleIndex })
        if ($rows.Count -ne [int]$config.sampleFrames) {
            throw "Scenario '$scenarioId' has an incomplete raw block."
        }
        $blockGpu = [System.Collections.Generic.List[double]]::new()
        for ($sample = 1; $sample -le $rows.Count; $sample++) {
            $row = $rows[$sample - 1]
            if ([int]$row.sampleIndex -ne $sample -or
                [string]$row.scenarioId -cne $scenarioId -or
                [string]$row.variant -cne [string]$expected.variant -or
                [string]$row.blockType -cne [string]$expected.blockType -or
                [int]$row.superRound -ne [int]$expected.superRound -or
                [int]$row.sequencePosition -ne
                    [int]$expected.sequencePosition -or
                [int]$row.pairIndex -ne [int]$expected.pairIndex -or
                [string]$row.pairOrder -cne [string]$expected.pairOrder -or
                [int]$row.withinPairPosition -ne
                    [int]$expected.withinPairPosition) {
                throw "Scenario '$scenarioId' raw schedule metadata is invalid."
            }
            if ([string]$row.nativeTimestampStatus -cne 'ready') {
                throw "Scenario '$scenarioId' contains a non-ready timestamp."
            }
            [uint64]$token = $row.nativeTimestampToken
            [uint64]$tag = $row.nativeTimestampUserTag
            if ($token -eq 0 -or -not $observedTokens.Add($token) -or
                -not $observedTags.Add($tag) -or
                $tag -ne [uint64]($globalRowIndex + 1)) {
                throw "Scenario '$scenarioId' has duplicate/zero token evidence."
            }
            $expectedFlags =
                if ($expected.variant -eq 'empty-command-buffer') { 1 }
                else { 0 }
            if ([uint32]$row.nativeTimestampFlags -ne $expectedFlags) {
                throw "Scenario '$scenarioId' timestamp flags are invalid."
            }
            [uint64]$begin = $row.nativeTimestampBeginTicks
            [uint64]$end = $row.nativeTimestampEndTicks
            [uint64]$elapsed = $row.nativeTimestampElapsedTicks
            [uint64]$frequency = $row.nativeTimestampFrequency
            [uint64]$fence = $row.nativeTimestampFenceValue
            if ($frequency -eq 0) {
                throw "Scenario '$scenarioId' has zero timestamp frequency."
            }
            [int]$sourceUnityFrame = $row.sourceUnityFrame
            [int64]$elapsedNanoseconds =
                $row.nativeTimestampElapsedNanoseconds
            [decimal]$expectedNanosecondsDecimal =
                [decimal]$elapsed * [decimal]1000000000 /
                [decimal]$frequency
            [int64]$expectedNanoseconds = [decimal]::ToInt64(
                [decimal]::Round(
                    $expectedNanosecondsDecimal,
                    0,
                    [System.MidpointRounding]::AwayFromZero))
            if ($end -lt $begin -or $elapsed -ne ($end - $begin) -or
                $frequency -eq 0 -or $fence -le $previousFence -or
                [uint32]$row.nativeTimestampDeviceGeneration -ne
                    [uint32]$config.nativeTimestampDeviceGeneration -or
                [int]$row.resultUnityFrame -lt $sourceUnityFrame -or
                $sourceUnityFrame -le $previousSourceUnityFrame -or
                $elapsedNanoseconds -ne $expectedNanoseconds -or
                $frequency -ne
                    [uint64]$config.nativeTimestampWarmupFrequency -or
                $fence -ne ($previousFence + [uint64]1) -or
                ($previousEndTicks -ne 0 -and $begin -lt $previousEndTicks)) {
                throw "Scenario '$scenarioId' timestamp payload is malformed."
            }
            if ($observedFrequency -eq 0) { $observedFrequency = $frequency }
            elseif ($frequency -ne $observedFrequency) {
                throw "Scenario '$scenarioId' timestamp frequency changed."
            }
            $previousFence = $fence
            $ms = Number $row.gpuRegionElapsedMs 'gpuRegionElapsedMs'
            $nsMs =
                (Number $row.nativeTimestampElapsedNanoseconds 'elapsedNs') /
                1000000.0
            Assert-Near $ms $nsMs 'timestamp ns/ms conversion' 0.000000000001
            $tickMs =
                [double]$elapsed * 1000.0 / [double]$frequency
            Assert-Near $ms $tickMs 'timestamp ticks/ms conversion' 0.0000011
            if ($expected.variant -ne 'empty-command-buffer' -and
                ($elapsed -eq 0 -or $ms -le 0.0)) {
                throw "Scenario '$scenarioId' has a zero-duration workload row."
            }
            if ([int64]$row.measurementReadbackBytes -ne 0 -or
                [int64]$row.timestampInstrumentationReadbackBytes -ne 16) {
                throw "Scenario '$scenarioId' readback accounting is invalid."
            }
            $previousSourceUnityFrame = $sourceUnityFrame
            $previousEndTicks = $end
            $globalRowIndex++
            $blockGpu.Add($ms)
            $allGpuValues.Add($ms)
        }
        $average = ($blockGpu | Measure-Object -Average).Average
        $p99 = Percentile ([double[]]$blockGpu.ToArray()) 0.99
        Assert-Near (
            Number $block.gpuRegionAverageMs 'block average') $average (
            "$scenarioId block average")
        Assert-Near (
            Number $block.gpuRegionP99Ms 'block p99') $p99 (
            "$scenarioId block p99")
    }

    if ($validation.Count -ne 4 -or
        @($validation | Where-Object { [int]$_.passed -ne 1 }).Count -ne 0) {
        throw "Scenario '$scenarioId' correctness validation is incomplete."
    }
    foreach ($phase in @('warmup','final')) {
        foreach ($variant in @(
            'reference-compose-portable',
            'direct-count-scan-scatter-portable')) {
            $rows = @($validation | Where-Object {
                $_.phase -ceq $phase -and $_.variant -ceq $variant
            })
            if ($rows.Count -ne 1 -or
                [string]$rows[0].resultHash -cne
                    [string]$config.expectedResultHash -or
                [uint32]$rows[0].invalidKeyCount -ne 0 -or
                [uint32]$rows[0].diagnosticFlags -ne 0 -or
                [int]$rows[0].validCount -ne [int]$config.elementCount -or
                [int64]$rows[0].readbackBytes -ne
                    [int64]$config.sharedOutputBytes) {
                throw "Scenario '$scenarioId' validation invariant failed."
            }
        }
    }
    if ([int64]$run.validationReadbackBytes -ne
        4L * [int64]$config.sharedOutputBytes) {
        throw "Scenario '$scenarioId' validation readback total is invalid."
    }

    $paired = [System.Collections.Generic.List[object]]::new()
    $measurementBlocks = @(
        $blocks | Where-Object { $_.blockType -ceq 'measurement' })
    foreach ($pairId in @(
        $measurementBlocks |
            Select-Object -ExpandProperty pairIndex -Unique |
            ForEach-Object { [int]$_ } |
            Sort-Object)) {
        $pairRows = @(
            $measurementBlocks |
                Where-Object { [int]$_.pairIndex -eq $pairId } |
                Sort-Object { [int]$_.withinPairPosition })
        if ($pairRows.Count -ne 2 -or
            [int]$pairRows[0].withinPairPosition -ne 1 -or
            [int]$pairRows[1].withinPairPosition -ne 2) {
            throw "Scenario '$scenarioId' pair $pairId is not adjacent/complete."
        }
        $reference = @(
            $pairRows | Where-Object {
                $_.variant -ceq 'reference-compose-portable'
            })
        $direct = @(
            $pairRows | Where-Object {
                $_.variant -ceq 'direct-count-scan-scatter-portable'
            })
        if ($reference.Count -ne 1 -or $direct.Count -ne 1) {
            throw "Scenario '$scenarioId' pair $pairId lacks one A and one B."
        }
        $refAvg = Number $reference[0].gpuRegionAverageMs 'reference average'
        $directAvg = Number $direct[0].gpuRegionAverageMs 'direct average'
        $refP99 = Number $reference[0].gpuRegionP99Ms 'reference p99'
        $directP99 = Number $direct[0].gpuRegionP99Ms 'direct p99'
        $paired.Add([pscustomobject]@{
            scenarioId = $scenarioId
            pairIndex = $pairId
            pairOrder = $pairRows[0].pairOrder
            referenceGpuAverageMs = $refAvg
            directGpuAverageMs = $directAvg
            gpuAverageImprovementPercent =
                Improvement $refAvg $directAvg
            gpuAverageAbsoluteReductionMs = $refAvg - $directAvg
            referenceGpuP99Ms = $refP99
            directGpuP99Ms = $directP99
            gpuP99ImprovementPercent =
                Improvement $refP99 $directP99
        })
    }
    $pairedPath = Join-Path $scenarioRoot 'paired-deltas.csv'
    $paired |
        Export-Csv -LiteralPath $pairedPath -NoTypeInformation -Encoding utf8

    $averageImprovements = [double[]]@(
        $paired | ForEach-Object { [double]$_.gpuAverageImprovementPercent })
    $absoluteReductions = [double[]]@(
        $paired | ForEach-Object { [double]$_.gpuAverageAbsoluteReductionMs })
    $p99Improvements = [double[]]@(
        $paired | ForEach-Object { [double]$_.gpuP99ImprovementPercent })
    $positivePairs =
        @($averageImprovements | Where-Object { $_ -gt 0.0 }).Count
    $averageMedian = Median $averageImprovements
    $absoluteMedian = Median $absoluteReductions
    $p99Median = Median $p99Improvements
    $abImprovements = [double[]]@(
        $paired | Where-Object { $_.pairOrder -ceq 'AB' } |
            ForEach-Object { [double]$_.gpuAverageImprovementPercent })
    $baImprovements = [double[]]@(
        $paired | Where-Object { $_.pairOrder -ceq 'BA' } |
            ForEach-Object { [double]$_.gpuAverageImprovementPercent })
    $abMedian = Median $abImprovements
    $baMedian = Median $baImprovements
    $orderBalanceGate =
        $abImprovements.Count -eq 2 -and $baImprovements.Count -eq 2 -and
        $abMedian -ge 0.0 -and $baMedian -ge 0.0
    $pairCountGate = $paired.Count -eq 4
    $positiveGate = $positivePairs -ge 3
    $percentGate = $averageMedian -ge 5.0
    $absoluteGate = $absoluteMedian -ge 0.005
    $p99Gate = $p99Median -ge -2.0
    $residentGate =
        $directCaseResidentBytes -le
            $referenceCaseResidentBytes
    $controlRows = @(
        $raw | Where-Object { $_.variant -ceq 'empty-command-buffer' })
    $controlMs = [double[]]@(
        $controlRows | ForEach-Object {
            Number $_.gpuRegionElapsedMs 'control gpuRegionElapsedMs'
        })
    $controlP99 = Percentile $controlMs 0.99
    $emptyScopeGate = $controlP99 -le 0.005
    if ($formal -and -not $emptyScopeGate) {
        throw (
            "Scenario '$scenarioId' empty-scope P99 $controlP99 exceeds 0.005 ms.")
    }
    $exploratoryUsable =
        $pairCountGate -and
        $validation.Count -eq 4 -and
        [int64]$run.measurementReadbackBytes -eq 0 -and
        [int]$run.gpuRegionTimingComplete -eq 1 -and
        $emptyScopeGate
    $accepted =
        $exploratoryUsable -and
        $positiveGate -and
        $percentGate -and
        $absoluteGate -and
        $p99Gate -and
        $orderBalanceGate -and
        $residentGate
    $classification = if ($accepted) {
        'accepted'
    }
    elseif ($averageMedian -lt 0.0) {
        'negative'
    }
    else {
        'neutral-or-inconclusive'
    }
    $allExploratoryUsable = $allExploratoryUsable -and $exploratoryUsable
    $allAccepted = $allAccepted -and $accepted

    $scenarioSummary = [pscustomobject]@{
        scenarioId = $scenarioId
        processId = $processIds[0]
        pairedRows = $paired.Count
        exploratoryDataUsable = [int]$exploratoryUsable
        classification = $classification
        positiveGpuAveragePairCount = $positivePairs
        positivePairGate = [int]$positiveGate
        gpuAverageImprovementMedianPercent = $averageMedian
        gpuAveragePercentGate = [int]$percentGate
        gpuAverageAbsoluteReductionMedianMs = $absoluteMedian
        gpuAverageAbsoluteGate = [int]$absoluteGate
        gpuP99ImprovementMedianPercent = $p99Median
        gpuP99NoWorseThanMinus2PercentGate = [int]$p99Gate
        abOrderGpuAverageImprovementMedianPercent = $abMedian
        baOrderGpuAverageImprovementMedianPercent = $baMedian
        orderBalanceGate = [int]$orderBalanceGate
        residentBytesGate = [int]$residentGate
        emptyScopeP99Ms = $controlP99
        emptyScopeGate = [int]$emptyScopeGate
        meetsFrozenImprovementGate = [int]$accepted
    }
    @($scenarioSummary) |
        Export-Csv -LiteralPath (
            Join-Path $scenarioRoot 'scenario-summary.csv') `
            -NoTypeInformation -Encoding utf8
    @(
        'GPU direct-binning scenario quality summary'
        "scenarioId=$scenarioId"
        "classification=$classification"
        "exploratoryDataUsable=$([int]$exploratoryUsable)"
        "pairedRows=$($paired.Count)"
        "measurementReadbackBytes=0"
        "gpuAverageImprovementMedianPercent=$averageMedian"
        "gpuP99ImprovementMedianPercent=$p99Median"
        "orderBalanceGate=$([int]$orderBalanceGate)"
        "emptyScopeP99Ms=$controlP99"
        "meetsFrozenImprovementGate=$([int]$accepted)"
    ) | Set-Content -LiteralPath (
        Join-Path $scenarioRoot 'quality-summary.txt') -Encoding utf8
    $rootSummaries.Add($scenarioSummary)
}

$rootSummaries |
    Export-Csv -LiteralPath (
        Join-Path $root 'matrix-summary.csv') -NoTypeInformation -Encoding utf8
$aBDataUsable =
    $formal -and
    $allExploratoryUsable -and
    $rootSummaries.Count -eq 3
$crossWorkloadClaimUsable = $aBDataUsable -and $allAccepted
$qualityLines = @(
    'GPU direct spatial binning matrix quality summary'
    "status=$(if ($allExploratoryUsable) { 'performance-usable' } else { 'invalid' })"
    "formalAcceptanceMode=$([int]$formal)"
    "matrixPreset=$($runner.matrixPreset)"
    "scenarioCount=$($rootSummaries.Count)"
    "exploratoryDataUsable=$([int]$allExploratoryUsable)"
    "aBDataUsable=$([int]$aBDataUsable)"
    "crossWorkloadImprovementClaimUsable=$([int]$crossWorkloadClaimUsable)"
    'activeDeviceIdentityConsistent=1'
    'measurementReadbackBytes=0'
    'performanceGateDoesNotControlDataRetention=1'
)
foreach ($summary in $rootSummaries) {
    $prefix = $summary.scenarioId.Replace('-', '_')
    $qualityLines += "$prefix.classification=$($summary.classification)"
    $qualityLines += (
        "$prefix.gpuAverageImprovementMedianPercent=" +
        "$($summary.gpuAverageImprovementMedianPercent)")
    $qualityLines += (
        "$prefix.gpuP99ImprovementMedianPercent=" +
        "$($summary.gpuP99ImprovementMedianPercent)")
}
$qualityPath = Join-Path $root 'quality-summary.txt'
$qualityLines |
    Set-Content -LiteralPath $qualityPath -Encoding utf8
$qualityLines
"matrixSummary=$(Join-Path $root 'matrix-summary.csv')"
"qualitySummary=$qualityPath"
