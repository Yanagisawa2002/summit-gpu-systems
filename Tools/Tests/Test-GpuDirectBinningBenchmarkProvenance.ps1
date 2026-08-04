[CmdletBinding()]
param(
    [string]$ProjectRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot '..\..'))
}
else {
    $ProjectRoot = [System.IO.Path]::GetFullPath($ProjectRoot)
}

$assertions = 0
function Assert-True {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) {
        throw "ASSERTION FAILED: $Message"
    }
    $script:assertions++
}

function Assert-Parses {
    param([Parameter(Mandatory = $true)][string]$Path)
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $Path,
        [ref]$tokens,
        [ref]$errors)
    $errorText = @(
        $errors | ForEach-Object { $_.Message }) -join '; '
    Assert-True ($errors.Count -eq 0) (
        "$Path must parse without PowerShell errors: " +
        $errorText)
}

$runnerPath =
    Join-Path $ProjectRoot 'Tools\Run-GpuDirectBinningBenchmark.ps1'
$summarizerPath =
    Join-Path $ProjectRoot 'Tools\Summarize-GpuDirectBinningBenchmark.ps1'
$provenancePath =
    Join-Path $ProjectRoot 'Tools\GpuBenchmarkProvenance.psm1'
$schedulePath = Join-Path $ProjectRoot (
    'Assets\GpuDirectBinningBenchmark\Runtime\' +
    'GpuDirectBinningBenchmarkSchedule.cs')
$controllerPath = Join-Path $ProjectRoot (
    'Assets\GpuDirectBinningBenchmark\Runtime\' +
    'GpuDirectBinningBenchmarkController.cs')
$referencePath = Join-Path $ProjectRoot (
    'Assets\GpuDirectBinningBenchmark\Runtime\' +
    'GpuDirectBinningReferencePipeline.cs')

foreach ($path in @(
    $runnerPath,
    $summarizerPath,
    $provenancePath,
    $schedulePath,
    $controllerPath,
    $referencePath)) {
    Assert-True (Test-Path -LiteralPath $path -PathType Leaf) (
        "Required harness file must exist: $path")
}
Assert-Parses $runnerPath
Assert-Parses $summarizerPath
Assert-Parses $provenancePath

$runner = Get-Content -LiteralPath $runnerPath -Raw
$summarizer = Get-Content -LiteralPath $summarizerPath -Raw
$schedule = Get-Content -LiteralPath $schedulePath -Raw
$controller = Get-Content -LiteralPath $controllerPath -Raw
$reference = Get-Content -LiteralPath $referencePath -Raw

foreach ($requiredSource in @(
    'com.summit.gpu-direct-binning',
    'com.summit.gpu-primitives',
    'com.summit.gpu-timestamps',
    'Packages\manifest.json',
    'Packages\packages-lock.json',
    'GpuBenchmarkProvenance.psm1',
    'Summarize-GpuDirectBinningBenchmark.ps1')) {
    Assert-True ($runner.Contains($requiredSource)) (
        "Runner source snapshot must include '$requiredSource'.")
}
foreach ($requiredGate in @(
    'Get-GpuBenchmarkGitSnapshot',
    'Restore-KnownUnityBenchmarkDrift',
    'Get-GpuBenchmarkPlayerPayload',
    'playerPayloadStableThroughRun',
    'sourceHashesStableAcrossBuild',
    'runnerConfigFinalized',
    'formalContractSatisfied')) {
    Assert-True ($runner.Contains($requiredGate)) (
        "Runner must preserve provenance gate '$requiredGate'.")
}
$powerShellHarness = $runner + "`n" + $summarizer
Assert-True (
    -not [regex]::IsMatch(
        $powerShellHarness,
        '\b\d+(?:UL|LU|U)\b')) (
    'PowerShell harness must not contain unsigned C# numeric suffix literals.')
Assert-True (
    $runner.Contains("'uniform-c4096'") -and
    $runner.Contains("'hotset16-c4096'") -and
    $runner.Contains("'uniform-c65536'")) (
    'Runner must contain the three frozen AMD scenarios.')
Assert-True (
    $runner.Contains("sampleFrames = 900") -and
    $runner.Contains("superRounds = 2") -and
    $runner.Contains("matrixPreset = 'amd-r9700-v1'")) (
    'Runner must freeze the formal 2-super-round/900-frame contract.')
Assert-True (
    $runner.Contains("test-suite[@type='Assembly']") -and
    $runner.Contains("test-suite[@type='TestFixture']") -and
    $runner.Contains('observedIdentities.Contains') -and
    -not $runner.Contains('$rawXml.IndexOf(')) (
    'EditMode identities must use exact parsed NUnit-node matching.')
Assert-True (
    $summarizer.Contains("test-suite[@type='Assembly']") -and
    $summarizer.Contains("test-suite[@type='TestFixture']") -and
    $summarizer.Contains('identityMatchMode') -and
    $summarizer.Contains('runnerObservedIdentities') -and
    -not $summarizer.Contains('$editModeXml.IndexOf(')) (
    'Summarizer must independently require exact parsed NUnit identities.')
foreach ($requiredIdentity in @(
    'Summit.GpuDirectBinning.Tests.Editor.dll',
    'Summit.GpuDirectBinning.Tests.CpuDirectBinningOracleTests',
    'Summit.GpuDirectBinning.Tests.GpuDirectSpatialBinnerContractTests',
    'Summit.GpuDirectBinning.Tests.GpuDirectSpatialBinnerIntegrationTests',
    'Summit.GpuDirectBinning.Benchmark.Tests.Editor.dll',
    'Summit.GpuDirectBinning.Benchmark.Tests.GpuDirectBinningBenchmarkScheduleTests',
    'Summit.GpuDirectBinning.Benchmark.Tests.GpuDirectBinningInputDistributionTests',
    'Summit.GpuDirectBinning.Benchmark.Tests.GpuDirectBinningCpuOracleTests')) {
    Assert-True ($runner.Contains("'$requiredIdentity'")) (
        "Runner must require exact NUnit identity '$requiredIdentity'.")
}
Assert-True (
    $schedule.Contains('GpuDirectBinningBenchmarkVariant.Reference') -and
    $schedule.Contains('GpuDirectBinningBenchmarkVariant.Direct') -and
    $schedule.Contains('bool abba = (superRound & 1) != 0')) (
    'Schedule must encode alternating ABBA/BAAB super-rounds.')
Assert-True (
    $controller.Contains('MeasurementReadbackBytes = 0') -and
    $controller.Contains('TimestampInstrumentationReadbackBytes = 16') -and
    $controller.Contains('MarkSubmitted(token)')) (
    'Controller must preserve zero measurement readback and ABI2 submission.')
Assert-True (
    $controller.IndexOf(
        'BeginValidation',
        [System.StringComparison]::Ordinal) -ge 0 -and
    -not [regex]::IsMatch(
        $controller,
        '\bAsyncGPUReadback\s*\.|\bAsyncGPUReadbackRequest\b')) (
    'Controller may coordinate validation but must not own measurement readback.')
Assert-True (
    $controller.Contains('RuntimeInitializeLoadType.BeforeSceneLoad') -and
    $controller.Contains(
        'Debug.unityLogger.filterLogType = LogType.Warning')) (
    'Benchmark mode must suppress unrelated informational logs before scene load.')
Assert-True (
    $reference.Contains('GpuPrimitiveBackend.Portable') -and
    $reference.Contains('RecordHistogram') -and
    $reference.Contains('RecordExclusiveScan')) (
    'Reference path must be the frozen portable primitive composition.')
$benchmarkSources = Get-ChildItem -LiteralPath (
    Join-Path $ProjectRoot 'Assets\GpuDirectBinningBenchmark') -Recurse -File |
    Where-Object { $_.Extension -in @('.cs', '.compute') } |
    ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
$benchmarkText = $benchmarkSources -join "`n"
Assert-True (-not [regex]::IsMatch(
    $benchmarkText,
    '\b(radix|adaptive)\b',
    [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)) (
    'S2 benchmark must not silently add radix or adaptive backends.')
foreach ($requiredSummaryGate in @(
    'nativeTimestampElapsedNanoseconds',
    'MidpointRounding]::AwayFromZero',
    'measurementReadbackBytes',
    'performanceGateDoesNotControlDataRetention',
    'crossWorkloadImprovementClaimUsable',
    'neutral-or-inconclusive',
    'informationalPlayerLogsSuppressed',
    'negative')) {
    Assert-True ($summarizer.Contains($requiredSummaryGate)) (
        "Summarizer must preserve '$requiredSummaryGate'.")
}

$fixtureRoot = Join-Path (
    [System.IO.Path]::GetTempPath()) (
    'summit-direct-binning-tamper-' + [Guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($fixtureRoot)
try {
    $tamperedRunner = [ordered]@{
        schemaVersion = 5
        suite = 'summit.gpu-direct-binning'
        benchmarkSchemaVersion = 1
        runnerConfigFinalized = $true
        formalAcceptanceMode = $true
        formalContractSatisfied = $true
        sourceHashesStableAcrossBuild = $true
        playerPayloadStableThroughRun = $true
        gitTreeDirty = $false
        gitFinal = [ordered]@{ dirty = $false }
        gitCommit = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
        gitBranch = 'codex/tamper-fixture'
        editModeResults = [ordered]@{
            result = 'Passed'
            total = 1
            passed = 1
            failed = 0
            skipped = 0
            inconclusive = 0
        }
        windowsVideoControllers = @(
            [ordered]@{ name = 'fixture'; driverVersion = 'fixture' })
        deviceIndex = 0
        matrixPreset = 'amd-r9700-v1'
        superRounds = 2
        warmupFrames = 60
        sampleFrames = 899
        cooldownFrames = 15
        dispatchesPerFrame = 1
        projectUnityVersion = '6000.5.2f1'
        unityEditorResolvedVersion = '6000.5.2f1'
        requireCompleteGpuTimings = $true
        scenarios = @(1, 2, 3)
        playerRuns = @(1, 2, 3)
    }
    $tamperedRunner |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (
            Join-Path $fixtureRoot 'runner-config.json') -Encoding utf8
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $tamperOutput = & powershell.exe `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $summarizerPath `
        -ReportDirectory $fixtureRoot 2>&1
    $tamperExitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousPreference
    $tamperText =
        @($tamperOutput | ForEach-Object { [string]$_ }) -join "`n"
    Assert-True ($tamperExitCode -ne 0) (
        'Summarizer must reject a tampered formal sampleFrames field.')
    Assert-True (
        $tamperText.Contains("Formal runner field 'sampleFrames'")) (
        'Tampered formal-field rejection must identify sampleFrames.')
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot -PathType Container) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}

$deviceFixtureRoot = Join-Path (
    [System.IO.Path]::GetTempPath()) (
    'summit-direct-binning-device-' + [Guid]::NewGuid().ToString('N'))
[void][System.IO.Directory]::CreateDirectory($deviceFixtureRoot)
try {
    $wrongDeviceName = 'AMD Radeon RX 7900 XTX'
    $fixtureCommit = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
    $fixtureRuntimeHash = 'runtime-fixture-hash'
    $fixtureReferenceHash = 'reference-fixture-hash'
    $fixtureApiHash = 'api-fixture-hash'
    $fixtureIdentities = @(
        'Summit.GpuDirectBinning.Tests.Editor.dll',
        'Summit.GpuDirectBinning.Tests.CpuDirectBinningOracleTests',
        'Summit.GpuDirectBinning.Tests.GpuDirectSpatialBinnerContractTests',
        'Summit.GpuDirectBinning.Tests.GpuDirectSpatialBinnerIntegrationTests',
        'Summit.GpuDirectBinning.Benchmark.Tests.Editor.dll',
        'Summit.GpuDirectBinning.Benchmark.Tests.GpuDirectBinningBenchmarkScheduleTests',
        'Summit.GpuDirectBinning.Benchmark.Tests.GpuDirectBinningInputDistributionTests',
        'Summit.GpuDirectBinning.Benchmark.Tests.GpuDirectBinningCpuOracleTests'
    )
    $fixtureEditModePath =
        Join-Path $deviceFixtureRoot 'editmode-results.xml'
    $fixtureIdentityXml =
        "<test-suite type=`"Assembly`" " +
            "name=`"$($fixtureIdentities[0])`" />" +
        "<test-suite type=`"Assembly`" " +
            "name=`"$($fixtureIdentities[4])`" />" +
        (@($fixtureIdentities |
            Where-Object { $_ -notlike '*.dll' } |
            ForEach-Object {
                "<test-suite type=`"TestFixture`" fullname=`"$_`" />"
            }) -join '')
    (
        "<test-run result=`"Passed`" total=`"1`" passed=`"1`" " +
        "failed=`"0`" skipped=`"0`" inconclusive=`"0`">" +
        $fixtureIdentityXml +
        '</test-run>'
    ) | Set-Content -LiteralPath $fixtureEditModePath -Encoding utf8
    $fixtureEditModeSha =
        (Get-FileHash -LiteralPath $fixtureEditModePath -Algorithm SHA256).Hash
    $fixtureEditModeLastWrite =
        (Get-Item -LiteralPath $fixtureEditModePath).
            LastWriteTimeUtc.ToString('o')
    $deviceRunner = [ordered]@{
        schemaVersion = 5
        suite = 'summit.gpu-direct-binning'
        benchmarkSchemaVersion = 1
        runnerConfigFinalized = $true
        formalAcceptanceMode = $true
        formalContractSatisfied = $true
        sourceHashesStableAcrossBuild = $true
        playerPayloadStableThroughRun = $true
        gitTreeDirty = $false
        gitFinal = [ordered]@{ dirty = $false }
        gitCommit = $fixtureCommit
        gitBranch = 'codex/device-fixture'
        editModeResults = [ordered]@{
            result = 'Passed'
            total = 1
            passed = 1
            failed = 0
            skipped = 0
            inconclusive = 0
            identityMatchMode = 'exact-nunit-node-v1'
            expectedIdentities = @($fixtureIdentities)
            observedIdentities = @($fixtureIdentities)
            missingIdentities = @()
            sourcePath = $fixtureEditModePath
            sourceSha256 = $fixtureEditModeSha
            sourceLastWriteUtc = $fixtureEditModeLastWrite
            copiedPath = $fixtureEditModePath
            copiedSha256 = $fixtureEditModeSha
            copiedLastWriteUtc = $fixtureEditModeLastWrite
        }
        windowsVideoControllers = @(
            [ordered]@{
                name = $wrongDeviceName
                driverVersion = 'fixture'
                pnpDeviceId = 'PCI\VEN_1002&DEV_FIXTURE'
            })
        deviceIndex = 0
        matrixPreset = 'amd-r9700-v1'
        superRounds = 2
        warmupFrames = 60
        sampleFrames = 900
        cooldownFrames = 15
        dispatchesPerFrame = 1
        projectUnityVersion = '6000.5.2f1'
        unityEditorResolvedVersion = '6000.5.2f1'
        requireCompleteGpuTimings = $true
        runtimeShaderSha256 = $fixtureRuntimeHash
        referenceShaderSha256 = $fixtureReferenceHash
        runtimeApiSha256 = $fixtureApiHash
        scenarios = @(
            [ordered]@{
                scenarioId = 'uniform-c4096'
                elementCount = 1048576
                binCount = 4096
                distribution = 'uniform'
                seed = 20260730
            },
            [ordered]@{
                scenarioId = 'hotset16-c4096'
                elementCount = 1048576
                binCount = 4096
                distribution = 'hotset16'
                seed = 20260731
            },
            [ordered]@{
                scenarioId = 'uniform-c65536'
                elementCount = 1048576
                binCount = 65536
                distribution = 'uniform'
                seed = 20260732
            })
        playerRuns = @(
            [ordered]@{
                scenarioId = 'uniform-c4096'
                processId = 1234
                reportDirectory =
                    (Join-Path $deviceFixtureRoot 'uniform-c4096')
            },
            [ordered]@{
                scenarioId = 'hotset16-c4096'
                processId = 1235
                reportDirectory =
                    (Join-Path $deviceFixtureRoot 'hotset16-c4096')
            },
            [ordered]@{
                scenarioId = 'uniform-c65536'
                processId = 1236
                reportDirectory =
                    (Join-Path $deviceFixtureRoot 'uniform-c65536')
            })
    }
    $deviceRunner |
        ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (
            Join-Path $deviceFixtureRoot 'runner-config.json') -Encoding utf8

    @(
        'scenarioId,elementCount,binCount,distribution,seed,processId'
        'uniform-c4096,1048576,4096,uniform,20260730,1234'
        'hotset16-c4096,1048576,4096,hotset16,20260731,1235'
        'uniform-c65536,1048576,65536,uniform,20260732,1236'
    ) | Set-Content -LiteralPath (
        Join-Path $deviceFixtureRoot 'matrix.csv') -Encoding utf8

    $scenarioRoot = Join-Path $deviceFixtureRoot 'uniform-c4096'
    [void][System.IO.Directory]::CreateDirectory($scenarioRoot)
    [ordered]@{
        schemaVersion = 1
        suite = 'summit.gpu-direct-binning'
        scenarioId = 'uniform-c4096'
        processId = 1234
        elementCount = 1048576
        binCount = 4096
        distribution = 'uniform'
        seed = 20260730
        superRounds = 2
        warmupFrames = 60
        sampleFrames = 900
        cooldownFrames = 15
        dispatchesPerFrame = 1
        scanBackend = 'portable'
        baselineId = 'reference-compose-portable-v1'
        directId = 'direct-count-scan-scatter-portable-v1'
        caseLocalWarmup = $true
        sameProcessPaired = $true
        informationalPlayerLogsSuppressed = $true
        requireCompleteGpuTimings = $true
        scheduleContract = 'control-pre;ABBA;BAAB;control-post'
        buildCommit = $fixtureCommit
        runtimeShaderSha256 = $fixtureRuntimeHash
        referenceShaderSha256 = $fixtureReferenceHash
        runtimeApiSha256 = $fixtureApiHash
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (
        Join-Path $scenarioRoot 'config.json') -Encoding utf8
    [ordered]@{
        graphicsDeviceType = 'Direct3D12'
        graphicsDeviceVendorId = 4098
        graphicsDeviceId = 9999
        graphicsDeviceName = $wrongDeviceName
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (
        Join-Path $scenarioRoot 'device.json') -Encoding utf8
    @('passed=1', 'status=completed') | Set-Content -LiteralPath (
        Join-Path $scenarioRoot 'run-summary.txt') -Encoding utf8
    foreach ($csvName in @(
        'raw-frames.csv',
        'block-summary.csv',
        'validation.csv')) {
        'dummy' | Set-Content -LiteralPath (
            Join-Path $scenarioRoot $csvName) -Encoding utf8
    }

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $deviceOutput = & powershell.exe `
        -NoProfile `
        -ExecutionPolicy Bypass `
        -File $summarizerPath `
        -ReportDirectory $deviceFixtureRoot 2>&1
    $deviceExitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousPreference
    $deviceText =
        @($deviceOutput | ForEach-Object { [string]$_ }) -join "`n"
    Assert-True ($deviceExitCode -ne 0) (
        'Summarizer must reject a non-R9700 formal device.')
    Assert-True ($deviceText.Contains('AMD Radeon AI PRO R9700')) (
        'Non-R9700 rejection must identify the exact required GPU.')
}
finally {
    if (Test-Path -LiteralPath $deviceFixtureRoot -PathType Container) {
        Remove-Item -LiteralPath $deviceFixtureRoot -Recurse -Force
    }
}

$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
$formalOutput = & powershell.exe `
    -NoProfile `
    -ExecutionPolicy Bypass `
    -File $runnerPath `
    -MatrixPreset 'amd-r9700-v1' `
    -SuperRounds 2 `
    -WarmupFrames 60 `
    -SampleFrames 900 `
    -CooldownFrames 15 `
    -DispatchesPerFrame 1 `
    -FormalAcceptanceMode `
    -EditModeResultsPath 'intentionally-missing.xml' `
    -SkipSummary 2>&1
$formalExitCode = $LASTEXITCODE
$ErrorActionPreference = $previousPreference
$formalText = @($formalOutput | ForEach-Object { [string]$_ }) -join "`n"
Assert-True ($formalExitCode -ne 0) (
    'Formal runner must reject SkipSummary before starting Unity.')
Assert-True ($formalText.Contains('SkipSummary is forbidden.')) (
    'Formal SkipSummary rejection must be explicit.')

Write-Host (
    "PASS: GPU direct-binning benchmark provenance tests; " +
    "assertions=$assertions")
