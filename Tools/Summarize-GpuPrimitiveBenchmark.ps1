[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ReportDirectory,

    [string]$OutputDirectory,

    [switch]$RequireNativeIntegrity
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$reportRoot = (Resolve-Path -LiteralPath $ReportDirectory).Path
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $outputRoot = $reportRoot
}
elseif ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $outputRoot = [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    $outputRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $reportRoot $OutputDirectory))
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

function Require-File {
    param([Parameter(Mandatory = $true)][string]$Name)

    $path = Join-Path $reportRoot $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required benchmark artifact is missing: $path"
    }
    return $path
}

function Convert-ToDouble {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $parsed = 0.0
    if (-not [double]::TryParse(
            [string]$Value,
            [System.Globalization.NumberStyles]::Float,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed)) {
        throw "Metric '$Name' is not numeric: '$Value'."
    }
    if ([double]::IsNaN($parsed) -or [double]::IsInfinity($parsed)) {
        throw "Metric '$Name' must be finite, observed '$Value'."
    }
    return $parsed
}

function Get-Median {
    param([Parameter(Mandatory = $true)][double[]]$Values)

    if ($Values.Count -eq 0) {
        throw 'Median requires at least one value.'
    }
    $sorted = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) {
        return [double]$sorted[$middle]
    }
    return ([double]$sorted[$middle - 1] + [double]$sorted[$middle]) / 2.0
}

function Get-Percentile {
    param(
        [Parameter(Mandatory = $true)][double[]]$Values,
        [Parameter(Mandatory = $true)][double]$Percentile
    )

    if ($Values.Count -eq 0) {
        throw 'Percentile requires at least one value.'
    }
    if ($Percentile -lt 0.0 -or $Percentile -gt 1.0) {
        throw "Percentile must be in [0, 1], observed $Percentile."
    }
    $sorted = @($Values | Sort-Object)
    $index = [int][Math]::Ceiling($sorted.Count * $Percentile) - 1
    $index = [Math]::Max(0, [Math]::Min($sorted.Count - 1, $index))
    return [double]$sorted[$index]
}

function Assert-CsvColumns {
    param(
        [Parameter(Mandatory = $true)][object[]]$Rows,
        [Parameter(Mandatory = $true)][string[]]$Names,
        [Parameter(Mandatory = $true)][string]$Artifact
    )

    $available = @($Rows[0].PSObject.Properties.Name)
    foreach ($name in $Names) {
        if ($name -notin $available) {
            throw "$Artifact is missing required column '$name'."
        }
    }
}

function Get-RequiredSummaryValue {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Summary,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not $Summary.ContainsKey($Name)) {
        throw "run-summary.txt is missing required field '$Name'."
    }
    return $Summary[$Name]
}

function Get-ImprovementPercent {
    param(
        [Parameter(Mandatory = $true)][double]$Baseline,
        [Parameter(Mandatory = $true)][double]$Candidate
    )

    if ([Math]::Abs($Baseline) -lt 1.0e-12) {
        return 0.0
    }
    return ($Baseline - $Candidate) / $Baseline * 100.0
}

function Get-DriftPercent {
    param([Parameter(Mandatory = $true)][double[]]$Values)

    if ($Values.Count -le 1) {
        return 0.0
    }
    $median = Get-Median $Values
    if ([Math]::Abs($median) -lt 1.0e-12) {
        return 0.0
    }
    $minimum = ($Values | Measure-Object -Minimum).Minimum
    $maximum = ($Values | Measure-Object -Maximum).Maximum
    return ([double]$maximum - [double]$minimum) / $median * 100.0
}

function Get-RequiredPropertyValue {
    param(
        [Parameter(Mandatory = $true)][object]$Object,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Artifact
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "$Artifact is missing required property '$Name'."
    }
    return $property.Value
}

function Assert-Sha256 {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $text = [string]$Value
    if ($text -notmatch '^[0-9A-Fa-f]{64}$' -or $text -match '^0{64}$') {
        throw "Provenance hash '$Name' is not a non-zero SHA-256 value."
    }
}

function Test-StringSetEqual {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Actual,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Expected
    )

    $actualSorted = @($Actual | Sort-Object -Unique)
    $expectedSorted = @($Expected | Sort-Object -Unique)
    if ($actualSorted.Count -ne $expectedSorted.Count) {
        return $false
    }
    for ($index = 0; $index -lt $expectedSorted.Count; $index++) {
        if ($actualSorted[$index] -cne $expectedSorted[$index]) {
            return $false
        }
    }
    return $true
}

function Assert-StringArrayEqual {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Actual,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($Actual.Count -ne $Expected.Count) {
        throw (
            "$Name count mismatch: expected $($Expected.Count), " +
            "observed $($Actual.Count).")
    }
    for ($index = 0; $index -lt $Expected.Count; $index++) {
        if ($Actual[$index] -cne $Expected[$index]) {
            throw (
                "$Name mismatch at index ${index}: expected " +
                "'$($Expected[$index])', observed '$($Actual[$index])'.")
        }
    }
}

function Assert-GitSnapshotIdentity {
    param(
        [Parameter(Mandatory = $true)][object]$Snapshot,
        [Parameter(Mandatory = $true)][string]$ExpectedHead,
        [Parameter(Mandatory = $true)][string]$ExpectedBranch,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][bool]$RequireClean
    )

    $head = [string](Get-RequiredPropertyValue `
        $Snapshot 'head' $Name)
    $branch = [string](Get-RequiredPropertyValue `
        $Snapshot 'branch' $Name)
    $dirty = [bool](Get-RequiredPropertyValue `
        $Snapshot 'dirty' $Name)
    $statusLines = [string[]]@(
        Get-RequiredPropertyValue $Snapshot 'statusLines' $Name)
    [void](Get-RequiredPropertyValue $Snapshot 'capturedUtc' $Name)
    if ($head -notmatch '^[0-9A-Fa-f]{40}$' -or
        $head -ine $ExpectedHead) {
        throw "$Name HEAD differs from the benchmark start commit."
    }
    if ($branch -cne $ExpectedBranch) {
        throw "$Name branch differs from the benchmark start branch."
    }
    if ($dirty -ne [bool]($statusLines.Count -ne 0)) {
        throw "$Name dirty flag disagrees with its porcelain status."
    }
    if ($RequireClean -and $dirty) {
        throw "$Name must be clean."
    }
}

function Get-PlayerPayloadManifestSummary {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Entries,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($Entries.Count -eq 0) {
        throw "$Name must contain at least one file."
    }
    $builder = [System.Text.StringBuilder]::new()
    $previousPath = $null
    $totalLength = [int64]0
    $paths = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $Entries) {
        $relativePath = [string](Get-RequiredPropertyValue `
            $entry 'relativePath' $Name)
        $lengthBytes = [int64](Get-RequiredPropertyValue `
            $entry 'lengthBytes' $Name)
        $fileSha256 = Get-RequiredPropertyValue `
            $entry 'sha256' $Name
        Assert-Sha256 -Value $fileSha256 -Name "$Name.$relativePath"
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            [System.IO.Path]::IsPathRooted($relativePath) -or
            $relativePath.Contains('\') -or
            $relativePath -match '(^|/)\.\.?(/|$)') {
            throw "$Name contains a non-canonical relative path '$relativePath'."
        }
        if ($null -ne $previousPath -and
            [System.StringComparer]::Ordinal.Compare(
                $previousPath, $relativePath) -ge 0) {
            throw "$Name paths are not unique and ordinally sorted."
        }
        if ($lengthBytes -lt 0) {
            throw "$Name contains a negative file length."
        }
        $previousPath = $relativePath
        $paths.Add($relativePath)
        $totalLength += $lengthBytes
        [void]$builder.Append($relativePath)
        [void]$builder.Append('=')
        [void]$builder.Append($lengthBytes)
        [void]$builder.Append('=')
        [void]$builder.Append([string]$fileSha256)
        [void]$builder.Append("`n")
    }
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())
        $manifestSha256 = [System.BitConverter]::ToString(
            $sha256.ComputeHash($bytes)).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
    return [pscustomobject]@{
        sha256 = $manifestSha256
        fileCount = $Entries.Count
        lengthBytes = $totalLength
        paths = [string[]]@($paths)
    }
}

function Test-FilterMatchesExpected {
    param(
        [Parameter(Mandatory = $true)][object]$Value,
        [Parameter(Mandatory = $true)][string[]]$Expected
    )

    $text = ([string]$Value).Trim()
    if ($text -eq '*') {
        return $true
    }
    $actual = @(
        $text -split '[,;]' |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    return Test-StringSetEqual -Actual $actual -Expected $Expected
}

function Get-ControllerCaseOrder {
    param(
        [Parameter(Mandatory = $true)][string[]]$Cases,
        [Parameter(Mandatory = $true)][int]$Round
    )

    if ($Cases.Count -eq 0 -or $Round -lt 1) {
        throw 'Controller case order requires at least one case and a positive round.'
    }
    $roundIndex = $Round - 1
    $shift = [int]([Math]::Floor($roundIndex / 2.0)) % $Cases.Count
    $reverse = ($roundIndex -band 1) -ne 0
    $ordered = [string[]]::new($Cases.Count)
    for ($position = 0; $position -lt $Cases.Count; $position++) {
        $offset = if ($reverse) {
            $Cases.Count - 1 - $position
        }
        else {
            $position
        }
        $ordered[$position] = $Cases[($offset + $shift) % $Cases.Count]
    }
    return $ordered
}

function Get-MetricStats {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [double[]]$Values)

    if ($Values.Count -eq 0) {
        return [pscustomobject]@{
            Average = -1.0
            P50 = -1.0
            P95 = -1.0
            P99 = -1.0
        }
    }
    return [pscustomobject]@{
        Average = [double](($Values | Measure-Object -Average).Average)
        P50 = Get-Percentile -Values $Values -Percentile 0.50
        P95 = Get-Percentile -Values $Values -Percentile 0.95
        P99 = Get-Percentile -Values $Values -Percentile 0.99
    }
}

function Assert-NearlyEqual {
    param(
        [Parameter(Mandatory = $true)][double]$Actual,
        [Parameter(Mandatory = $true)][double]$Expected,
        [Parameter(Mandatory = $true)][string]$Name,
        [double]$Tolerance = 0.0000011
    )

    if ([Math]::Abs($Actual - $Expected) -gt $Tolerance) {
        throw (
            "Metric '$Name' mismatch: expected $Expected, observed $Actual, " +
            "tolerance $Tolerance.")
    }
}

function Get-NormalizedDeviceName {
    param([object]$Value)

    $text = ([string]$Value).ToLowerInvariant()
    return [System.Text.RegularExpressions.Regex]::Replace(
        $text,
        '[^a-z0-9]',
        '')
}

$rawPath = Require-File 'raw-frames.csv'
$blockPath = Require-File 'block-summary.csv'
$validationPath = Require-File 'validation.csv'
$configPath = Require-File 'config.json'
$devicePath = Require-File 'device.json'
$runSummaryPath = Require-File 'run-summary.txt'
$runnerConfigPath = Require-File 'runner-config.json'
$playerPayloadManifestPath = Require-File 'player-payload-manifest.json'

$raw = @(Import-Csv -LiteralPath $rawPath)
$blocks = @(Import-Csv -LiteralPath $blockPath)
$validation = @(Import-Csv -LiteralPath $validationPath)
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$device = Get-Content -LiteralPath $devicePath -Raw | ConvertFrom-Json
$runnerConfig =
    Get-Content -LiteralPath $runnerConfigPath -Raw | ConvertFrom-Json
$playerPayloadManifest =
    Get-Content -LiteralPath $playerPayloadManifestPath -Raw |
        ConvertFrom-Json
if ($raw.Count -eq 0 -or $blocks.Count -eq 0 -or $validation.Count -eq 0) {
    throw 'Raw frames, block summaries, and validation results must all be non-empty.'
}
if ([int]$config.schemaVersion -ne 3) {
    throw "Expected benchmark config schema v3, observed '$($config.schemaVersion)'."
}

if ([int](Get-RequiredPropertyValue $runnerConfig 'schemaVersion' 'runner-config.json') -ne 5) {
    throw (
        "Expected runner-config schema v5, observed " +
        "'$($runnerConfig.schemaVersion)'.")
}

$formalAcceptanceMode = [bool](Get-RequiredPropertyValue `
    $runnerConfig `
    'formalAcceptanceMode' `
    'runner-config.json')
$frozenUnityVersion = '6000.5.2f1'
$frozenOperations = [string[]]@(
    'exclusive-scan',
    'histogram-16',
    'stable-compaction',
    'append-compaction',
    'radix-sort-32'
)
$frozenBackends = [string[]]@('portable', 'wave-ops')
$frozenCases = [string[]]@(
    'control/empty-command-buffer',
    'append-compaction/portable',
    'append-compaction/wave-ops',
    'exclusive-scan/portable',
    'exclusive-scan/wave-ops',
    'histogram-16/portable',
    'histogram-16/wave-ops',
    'radix-sort-32/portable',
    'radix-sort-32/wave-ops',
    'stable-compaction/portable',
    'stable-compaction/wave-ops'
)

$selectedCases = [string[]]@($config.selectedCases)
if ($formalAcceptanceMode) {
    foreach ($requiredTrueName in @(
            'runnerConfigFinalized',
            'buildPerformed',
            'summaryRequested',
            'sourceHashesStableAcrossBuild',
            'playerPayloadStableThroughRun',
            'formalContractSatisfied')) {
        if (-not [bool](Get-RequiredPropertyValue `
                $runnerConfig `
                $requiredTrueName `
                'runner-config.json')) {
            throw "Formal runner gate '$requiredTrueName' is not true."
        }
    }

    $expectedGitHead = [string](Get-RequiredPropertyValue `
        $runnerConfig 'gitCommit' 'runner-config.json')
    $expectedGitBranch = [string](Get-RequiredPropertyValue `
        $runnerConfig 'gitBranch' 'runner-config.json')
    if ($expectedGitHead -notmatch '^[0-9A-Fa-f]{40}$') {
        throw 'Formal provenance requires a full 40-character Git commit.'
    }
    if ([string]::IsNullOrWhiteSpace($expectedGitBranch) -or
        $expectedGitBranch -ceq 'HEAD') {
        throw 'Formal provenance requires a named Git branch.'
    }
    $gitStart = Get-RequiredPropertyValue `
        $runnerConfig 'gitStart' 'runner-config.json'
    $gitPostBuildBeforeRestore = Get-RequiredPropertyValue `
        $runnerConfig 'gitPostBuildBeforeRestore' 'runner-config.json'
    $gitPostBuildAfterRestore = Get-RequiredPropertyValue `
        $runnerConfig 'gitPostBuildAfterRestore' 'runner-config.json'
    $gitFinalBeforeRestore = Get-RequiredPropertyValue `
        $runnerConfig 'gitFinalBeforeRestore' 'runner-config.json'
    $gitFinal = Get-RequiredPropertyValue `
        $runnerConfig 'gitFinal' 'runner-config.json'
    Assert-GitSnapshotIdentity `
        -Snapshot $gitStart `
        -ExpectedHead $expectedGitHead `
        -ExpectedBranch $expectedGitBranch `
        -Name 'runner-config.gitStart' `
        -RequireClean $true
    Assert-GitSnapshotIdentity `
        -Snapshot $gitPostBuildBeforeRestore `
        -ExpectedHead $expectedGitHead `
        -ExpectedBranch $expectedGitBranch `
        -Name 'runner-config.gitPostBuildBeforeRestore' `
        -RequireClean $false
    Assert-GitSnapshotIdentity `
        -Snapshot $gitPostBuildAfterRestore `
        -ExpectedHead $expectedGitHead `
        -ExpectedBranch $expectedGitBranch `
        -Name 'runner-config.gitPostBuildAfterRestore' `
        -RequireClean $true
    Assert-GitSnapshotIdentity `
        -Snapshot $gitFinalBeforeRestore `
        -ExpectedHead $expectedGitHead `
        -ExpectedBranch $expectedGitBranch `
        -Name 'runner-config.gitFinalBeforeRestore' `
        -RequireClean $false
    Assert-GitSnapshotIdentity `
        -Snapshot $gitFinal `
        -ExpectedHead $expectedGitHead `
        -ExpectedBranch $expectedGitBranch `
        -Name 'runner-config.gitFinal' `
        -RequireClean $true
    if ([bool]$runnerConfig.gitTreeDirtyAtStart -ne [bool]$gitStart.dirty -or
        [bool]$runnerConfig.gitTreeDirty -ne [bool]$gitFinal.dirty) {
        throw 'Legacy Git dirty fields disagree with schema-5 snapshots.'
    }

    $fishPath =
        'Packages/com.firstgeargames.fishnet/CodeGenerating/' +
        'cecil-0.11.4/Mono.Cecil.sln.meta'
    $urpPath =
        'Assets/Settings/UniversalRenderPipelineGlobalSettings.asset'
    foreach ($driftRecord in @(
            @(
                'post-build',
                $gitPostBuildBeforeRestore,
                'gitPostBuildRestoredPaths',
                'gitPostBuildDriftKinds'),
            @(
                'final',
                $gitFinalBeforeRestore,
                'gitFinalRestoredPaths',
                'gitFinalDriftKinds'))) {
        $expectedPaths = [System.Collections.Generic.List[string]]::new()
        $expectedKinds = [System.Collections.Generic.List[string]]::new()
        foreach ($line in [string[]]@($driftRecord[1].statusLines)) {
            if ($line -ceq " D $fishPath") {
                $expectedPaths.Add($fishPath)
                $expectedKinds.Add('fishnet-meta-deleted')
            }
            elseif ($line -ceq " M $urpPath") {
                $expectedPaths.Add($urpPath)
                $expectedKinds.Add('urp-runtime-setting-removed')
            }
            else {
                throw (
                    "Formal $($driftRecord[0]) provenance contains " +
                    "unrecognized Git drift '$line'.")
            }
        }
        Assert-StringArrayEqual `
            -Actual ([string[]]@(
                Get-RequiredPropertyValue `
                    $runnerConfig $driftRecord[2] 'runner-config.json')) `
            -Expected ([string[]]@($expectedPaths)) `
            -Name "runner-config.$($driftRecord[2])"
        Assert-StringArrayEqual `
            -Actual ([string[]]@(
                Get-RequiredPropertyValue `
                    $runnerConfig $driftRecord[3] 'runner-config.json')) `
            -Expected ([string[]]@($expectedKinds)) `
            -Name "runner-config.$($driftRecord[3])"
    }

    $payload = Get-RequiredPropertyValue `
        $runnerConfig 'playerPayload' 'runner-config.json'
    if ([int](Get-RequiredPropertyValue `
            $payload 'schemaVersion' 'runner-config.playerPayload') -ne 1 -or
        [int](Get-RequiredPropertyValue `
            $playerPayloadManifest 'schemaVersion' `
            'player-payload-manifest.json') -ne 1) {
        throw 'Formal Player payload requires manifest schema v1.'
    }
    $payloadSummary = Get-PlayerPayloadManifestSummary `
        -Entries ([object[]]@($payload.files)) `
        -Name 'runner-config.playerPayload.files'
    $artifactPayloadSummary = Get-PlayerPayloadManifestSummary `
        -Entries ([object[]]@($playerPayloadManifest.files)) `
        -Name 'player-payload-manifest.files'
    Assert-Sha256 -Value $payload.sha256 -Name 'playerPayload.sha256'
    if ([string]$payloadSummary.sha256 -cne [string]$payload.sha256 -or
        [string]$artifactPayloadSummary.sha256 -cne [string]$payload.sha256 -or
        [string]$playerPayloadManifest.sha256 -cne [string]$payload.sha256 -or
        [int]$payloadSummary.fileCount -ne [int]$payload.fileCount -or
        [int64]$payloadSummary.lengthBytes -ne [int64]$payload.lengthBytes -or
        [int]$artifactPayloadSummary.fileCount -ne [int]$payload.fileCount -or
        [int64]$artifactPayloadSummary.lengthBytes -ne
            [int64]$payload.lengthBytes -or
        [int]$playerPayloadManifest.fileCount -ne [int]$payload.fileCount -or
        [int64]$playerPayloadManifest.lengthBytes -ne
            [int64]$payload.lengthBytes -or
        [string]$playerPayloadManifest.playerRelativePath -cne
            [string]$payload.playerRelativePath -or
        [string]$playerPayloadManifest.dataDirectoryRelativePath -cne
            [string]$payload.dataDirectoryRelativePath -or
        [int]$playerPayloadManifest.dataFileCount -ne
            [int]$payload.dataFileCount -or
        [int64]$playerPayloadManifest.dataLengthBytes -ne
            [int64]$payload.dataLengthBytes) {
        throw 'Formal Player payload manifest hash/count/length is inconsistent.'
    }
    Assert-StringArrayEqual `
        -Actual ([string[]]@($payload.excludedRelativePaths)) `
        -Expected ([string[]]@('build-summary.txt')) `
        -Name 'playerPayload.excludedRelativePaths'
    $playerRelativePath = [string]$payload.playerRelativePath
    $dataDirectoryRelativePath = [string]$payload.dataDirectoryRelativePath
    if ($playerRelativePath -notmatch '(?i)\.exe$' -or
        $dataDirectoryRelativePath -cne
            ([System.IO.Path]::GetFileNameWithoutExtension(
                $playerRelativePath) + '_Data')) {
        throw 'Formal Player executable and matching _Data directory are invalid.'
    }
    $playerEntries = @(
        $payload.files | Where-Object {
            [string]$_.relativePath -ceq $playerRelativePath })
    $unityPlayerEntries = @(
        $payload.files | Where-Object {
            [string]$_.relativePath -ceq 'UnityPlayer.dll' })
    $dataEntries = @(
        $payload.files | Where-Object {
            ([string]$_.relativePath).StartsWith(
                "$dataDirectoryRelativePath/",
                [System.StringComparison]::Ordinal) })
    $dataLength = [int64]((
        $dataEntries | Measure-Object -Property lengthBytes -Sum).Sum)
    if ($playerEntries.Count -ne 1 -or
        [int64]$playerEntries[0].lengthBytes -le 0 -or
        $unityPlayerEntries.Count -ne 1 -or
        [int64]$unityPlayerEntries[0].lengthBytes -le 0 -or
        $dataEntries.Count -ne [int]$payload.dataFileCount -or
        $dataEntries.Count -eq 0 -or
        $dataLength -ne [int64]$payload.dataLengthBytes -or
        $dataLength -le 0 -or
        [string]$playerEntries[0].sha256 -cne
            [string]$runnerConfig.playerExecutableSha256 -or
        [int64]$playerEntries[0].lengthBytes -ne
            [int64]$runnerConfig.playerExecutableLengthBytes) {
        throw 'Formal Player payload lacks a valid EXE, UnityPlayer.dll, or _Data.'
    }

    $frozenScalarChecks = @(
        @('runner.deviceIndex', [int]$runnerConfig.deviceIndex, 0),
        @('runner.rounds', [int]$runnerConfig.rounds, 3),
        @('runner.warmupFrames', [int]$runnerConfig.warmupFrames, 60),
        @('runner.sampleFrames', [int]$runnerConfig.sampleFrames, 900),
        @('runner.cooldownFrames', [int]$runnerConfig.cooldownFrames, 15),
        @('runner.elementCount', [int]$runnerConfig.elementCount, 1048576),
        @('runner.seed', [int]$runnerConfig.seed, 20260730),
        @('runner.dispatchesPerFrame', [int]$runnerConfig.dispatchesPerFrame, 1),
        @('config.rounds', [int]$config.rounds, 3),
        @('config.localWarmupFrames', [int]$config.localWarmupFrames, 60),
        @('config.sampleFrames', [int]$config.sampleFrames, 900),
        @('config.cooldownFrames', [int]$config.cooldownFrames, 15),
        @('config.elementCount', [int]$config.elementCount, 1048576),
        @('config.seed', [int]$config.seed, 20260730),
        @('config.dispatchesPerFrame', [int]$config.dispatchesPerFrame, 1),
        @('config.nativeTimestampAbiVersion',
            [int]$config.nativeTimestampAbiVersion, 2),
        @('config.nativeTimestampCapabilityFlags',
            [int]$config.nativeTimestampCapabilityFlags, 31)
    )
    foreach ($check in $frozenScalarChecks) {
        if ($check[1] -ne $check[2]) {
            throw (
                "Frozen matrix mismatch for $($check[0]): expected " +
                "$($check[2]), observed $($check[1]).")
        }
    }
    if ([string]$config.unityVersion -cne $frozenUnityVersion) {
        throw (
            "Frozen Unity version mismatch: expected $frozenUnityVersion, " +
            "observed '$($config.unityVersion)'.")
    }
    if ([string]$device.graphicsDeviceType -cne 'Direct3D12') {
        throw (
            "Frozen graphics API mismatch: expected Direct3D12, observed " +
            "'$($device.graphicsDeviceType)'.")
    }
    if (-not [bool]$runnerConfig.requireCompleteGpuTimings -or
        -not [bool]$config.requireCompleteGpuTimings) {
        throw 'Formal acceptance requires complete native GPU timings.'
    }
    if ([string]$config.nativeTimestampBackendSelected -cne
        'native-d3d12-timestamp-query') {
        throw 'Formal acceptance requires the native Direct3D 12 timestamp backend.'
    }
    if (-not [bool]$config.supportsWaveOperations) {
        throw 'Formal acceptance requires wave-operation support.'
    }
    if (-not (Test-FilterMatchesExpected `
            $runnerConfig.operations `
            $frozenOperations) -or
        -not (Test-FilterMatchesExpected `
            $config.requestedOperations `
            $frozenOperations)) {
        throw 'Formal operation filters do not select exactly the five frozen operations.'
    }
    if (-not (Test-FilterMatchesExpected `
            $runnerConfig.backends `
            $frozenBackends) -or
        -not (Test-FilterMatchesExpected `
            $config.requestedBackends `
            $frozenBackends)) {
        throw 'Formal backend filters do not select exactly portable and wave-ops.'
    }
    Assert-StringArrayEqual `
        -Actual $selectedCases `
        -Expected $frozenCases `
        -Name 'config.selectedCases'

    if ([bool]$runnerConfig.gitTreeDirty) {
        throw 'Formal provenance requires a clean benchmark worktree.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$runnerConfig.gitBranch)) {
        throw 'Formal provenance requires a recorded Git branch.'
    }
    if ([string]$runnerConfig.gitCommit -notmatch '^[0-9A-Fa-f]{40}$') {
        throw 'Formal provenance requires a full 40-character Git commit.'
    }
    if ([string]$runnerConfig.gitCommit -cne [string]$config.buildCommit) {
        throw 'runner-config Git commit differs from the Player config build commit.'
    }

    $requiredRunnerHashes = @(
        'benchmarkHarnessSha256',
        'portableShaderSha256',
        'waveShaderSha256',
        'runtimeApiSha256',
        'timestampManagedRuntimeSha256',
        'timestampNativeDllSha256',
        'timestampNativeSourceSha256',
        'timestampNativeHeaderSha256',
        'timestampBuildScriptSha256',
        'playerBuildScriptSha256',
        'packagesManifestSha256',
        'packagesLockSha256',
        'projectVersionSha256',
        'sourceSnapshotSha256',
        'playerExecutableSha256'
    )
    foreach ($hashName in $requiredRunnerHashes) {
        Assert-Sha256 `
            -Value (Get-RequiredPropertyValue `
                $runnerConfig `
                $hashName `
                'runner-config.json') `
            -Name $hashName
    }
    if (-not [bool]$runnerConfig.timestampNativeDllPresent) {
        throw 'Formal provenance reports a missing native timestamp DLL.'
    }
    foreach ($hashName in @(
            'portableShaderSha256',
            'waveShaderSha256',
            'runtimeApiSha256')) {
        $runnerHash = Get-RequiredPropertyValue `
            $runnerConfig $hashName 'runner-config.json'
        $playerHash = Get-RequiredPropertyValue `
            $config $hashName 'config.json'
        if ([string]$runnerHash -cne [string]$playerHash) {
            throw "Runner and Player config hashes differ for '$hashName'."
        }
    }

    $editModeResult = Get-RequiredPropertyValue `
        $runnerConfig `
        'editModeResults' `
        'runner-config.json'
    Assert-Sha256 `
        -Value (Get-RequiredPropertyValue `
            $editModeResult 'sha256' 'runner-config.editModeResults') `
        -Name 'editModeResults.sha256'
    $editModeTotal = [int](Get-RequiredPropertyValue `
        $editModeResult 'total' 'runner-config.editModeResults')
    $editModePassed = [int](Get-RequiredPropertyValue `
        $editModeResult 'passed' 'runner-config.editModeResults')
    $editModeFailed = [int](Get-RequiredPropertyValue `
        $editModeResult 'failed' 'runner-config.editModeResults')
    $editModeSkipped = [int](Get-RequiredPropertyValue `
        $editModeResult 'skipped' 'runner-config.editModeResults')
    $editModeInconclusive = [int](Get-RequiredPropertyValue `
        $editModeResult 'inconclusive' 'runner-config.editModeResults')
    $editModeStatus = [string](Get-RequiredPropertyValue `
        $editModeResult 'result' 'runner-config.editModeResults')
    if ($editModeTotal -ne 48 -or
        $editModePassed -ne $editModeTotal -or
        $editModeFailed -ne 0 -or
        $editModeSkipped -ne 0 -or
        $editModeInconclusive -ne 0 -or
        $editModeStatus -ine 'Passed') {
        throw (
            "Formal EditMode gate failed: result=$editModeStatus, " +
            "total=$editModeTotal, passed=$editModePassed, " +
            "failed=$editModeFailed, skipped=$editModeSkipped, " +
            "inconclusive=$editModeInconclusive.")
    }
}

$videoControllers = @(
    Get-RequiredPropertyValue `
        $runnerConfig `
        'windowsVideoControllers' `
        'runner-config.json')
if ($formalAcceptanceMode -and
    (-not [string]::IsNullOrWhiteSpace(
        [string]$runnerConfig.windowsVideoControllerInventoryError) -or
    $videoControllers.Count -eq 0)) {
    throw 'Formal provenance requires a successful Windows video-controller inventory.'
}
$unityDeviceNameNormalized = Get-NormalizedDeviceName $device.graphicsDeviceName
$driverMatches = @(
    $videoControllers |
        Where-Object {
            (Get-NormalizedDeviceName $_.name) -eq $unityDeviceNameNormalized
        })
$driverMatchKind = 'exact-normalized'
if ($driverMatches.Count -eq 0 -and
    -not [string]::IsNullOrWhiteSpace($unityDeviceNameNormalized)) {
    $driverMatches = @(
        $videoControllers |
            Where-Object {
                $candidate = Get-NormalizedDeviceName $_.name
                -not [string]::IsNullOrWhiteSpace($candidate) -and
                ($candidate.Contains($unityDeviceNameNormalized) -or
                    $unityDeviceNameNormalized.Contains($candidate))
            })
    $driverMatchKind = 'normalized-substring'
}
if ($driverMatches.Count -eq 0) {
    $driverMatchKind = 'none'
}
elseif ($driverMatches.Count -gt 1) {
    $driverMatchKind += '-ambiguous'
}
if ($formalAcceptanceMode -and $driverMatches.Count -ne 1) {
    throw (
        "Formal driver provenance requires exactly one Unity-device-name " +
        "match, observed $($driverMatches.Count) for " +
        "'$($device.graphicsDeviceName)'.")
}


Assert-CsvColumns -Rows $raw -Artifact 'raw-frames.csv' -Names @(
    'sourceUnityFrame',
    'processId',
    'round',
    'blockIndex',
    'orderPosition',
    'order',
    'caseId',
    'operation',
    'variant',
    'marker',
    'sampleIndex',
    'elapsedSeconds',
    'enqueueCpuMs',
    'frameMs',
    'mainThreadMs',
    'renderThreadMs',
    'gpuRegionElapsedMs',
    'gpuRegionSampleBlocks',
    'gpuFrameMs',
    'gpuFrameTimingValid',
    'gpuRegionResultUnityFrame',
    'nativeTimestampToken',
    'nativeTimestampUserTag',
    'nativeTimestampFlags',
    'nativeTimestampStatus',
    'nativeTimestampBeginTicks',
    'nativeTimestampEndTicks',
    'nativeTimestampElapsedTicks',
    'nativeTimestampFrequency',
    'nativeTimestampElapsedNanoseconds',
    'nativeTimestampElapsedMs',
    'nativeTimestampFenceValue',
    'nativeTimestampDeviceGeneration',
    'gpuRegionTimingSource',
    'gpuRegionTimingValid',
    'measurementReadbackBytes',
    'timestampInstrumentationReadbackBytes'
)
Assert-CsvColumns -Rows $blocks -Artifact 'block-summary.csv' -Names @(
    'gpuRegionValidSamples',
    'processId',
    'round',
    'blockIndex',
    'orderPosition',
    'order',
    'caseId',
    'operation',
    'variant',
    'marker',
    'samples',
    'gpuFrameValidSamples',
    'dispatchesPerFrame',
    'logicalBytesPerDispatch',
    'fenceSupported',
    'fencePassed',
    'frameAverageMs',
    'frameP50Ms',
    'frameP95Ms',
    'frameP99Ms',
    'gpuFrameAverageMs',
    'gpuFrameP50Ms',
    'gpuFrameP95Ms',
    'gpuFrameP99Ms',
    'enqueueAverageMs',
    'enqueueP50Ms',
    'enqueueP95Ms',
    'enqueueP99Ms',
    'mainAverageMs',
    'mainP99Ms',
    'renderAverageMs',
    'renderP99Ms',
    'gpuRegionAverageMs',
    'gpuRegionP50Ms',
    'gpuRegionP95Ms',
    'gpuRegionP99Ms',
    'measurementReadbackBytes',
    'timestampInstrumentationReadbackBytes'
)

$runSummary = @{}
foreach ($line in Get-Content -LiteralPath $runSummaryPath) {
    $parts = $line -split '=', 2
    if ($parts.Count -eq 2) {
        $runSummary[$parts[0]] = $parts[1]
    }
}

if ([string]$config.nativeTimestampBackendSelected -ne
    'native-d3d12-timestamp-query' -and
    [bool]$config.requireCompleteGpuTimings) {
    throw (
        "Strict timing selected '$($config.nativeTimestampBackendSelected)' " +
        'instead of native-d3d12-timestamp-query.')
}

$processIds = @(
    @($raw.processId) + @($blocks.processId) |
        Sort-Object -Unique
)
if ($processIds.Count -ne 1) {
    throw "Expected one Player PID across all measured rows, observed: $($processIds -join ', ')"
}
if ([string]$processIds[0] -ne [string]$config.processId -or
    [string]$processIds[0] -ne [string]$device.processId) {
    throw 'Player PID differs between CSV, config.json, and device.json.'
}

$measurementReadbacks = @(
    @($raw.measurementReadbackBytes) + @($blocks.measurementReadbackBytes) |
        Where-Object { [int64]$_ -ne 0 }
)
if ($measurementReadbacks.Count -ne 0) {
    throw 'Measurement rows contain non-zero GPU readback bytes.'
}

$nativeRowFailures = [System.Collections.Generic.List[string]]::new()
$seenNativeTokens = [System.Collections.Generic.HashSet[uint64]]::new()
[uint64]$observedNativeFrequency = 0
[int]$previousSourceUnityFrame = -1
for ($rowIndex = 0; $rowIndex -lt $raw.Count; $rowIndex++) {
    $row = $raw[$rowIndex]
    [uint64]$token = $row.nativeTimestampToken
    [uint64]$userTag = $row.nativeTimestampUserTag
    [uint32]$flags = $row.nativeTimestampFlags
    [uint64]$beginTicks = $row.nativeTimestampBeginTicks
    [uint64]$endTicks = $row.nativeTimestampEndTicks
    [uint64]$elapsedTicks = $row.nativeTimestampElapsedTicks
    [uint64]$frequency = $row.nativeTimestampFrequency
    [uint64]$fenceValue = $row.nativeTimestampFenceValue
    [uint32]$deviceGeneration = $row.nativeTimestampDeviceGeneration
    [uint32]$expectedFlags = if ($row.operation -eq 'control') { 1 } else { 0 }
    if ($frequency -eq 0) {
        $nativeRowFailures.Add(
            "row=$rowIndex case=$($row.caseId) has zero timestamp frequency")
        continue
    }

    [int]$sourceUnityFrame = $row.sourceUnityFrame
    [int64]$elapsedNanoseconds = $row.nativeTimestampElapsedNanoseconds
    [decimal]$expectedNanosecondsDecimal =
        [decimal]$elapsedTicks * [decimal]1000000000 / [decimal]$frequency
    [int64]$expectedNanoseconds = [decimal]::ToInt64(
        [decimal]::Round(
            $expectedNanosecondsDecimal,
            0,
            [System.MidpointRounding]::AwayFromZero))
    [double]$expectedElapsedMs =
        [double]$elapsedTicks * 1000.0 / [double]$frequency
    [double]$nativeElapsedMs = Convert-ToDouble `
        $row.nativeTimestampElapsedMs 'nativeTimestampElapsedMs'
    [double]$gpuRegionElapsedMs = Convert-ToDouble `
        $row.gpuRegionElapsedMs 'gpuRegionElapsedMs'

    $valid = $row.nativeTimestampStatus -eq 'ready' -and
        [int]$row.gpuRegionTimingValid -eq 1 -and
        $row.gpuRegionTimingSource -eq 'native-d3d12-timestamp-query' -and
        $token -ne 0 -and
        $seenNativeTokens.Add($token) -and
        $userTag -eq [uint64]($rowIndex + 1) -and
        $flags -eq $expectedFlags -and
        [int]$row.gpuRegionResultUnityFrame -ge
            [int]$row.sourceUnityFrame -and
        $endTicks -ge $beginTicks -and
        [decimal]$elapsedTicks -eq
            ([decimal]$endTicks - [decimal]$beginTicks) -and
        $frequency -ne 0 -and
        $fenceValue -gt 0 -and
        $deviceGeneration -eq [uint32]$config.nativeTimestampDeviceGeneration -and
        [int64]$row.timestampInstrumentationReadbackBytes -eq 16 -and
        [int]$row.gpuRegionSampleBlocks -eq 1 -and
        $sourceUnityFrame -gt $previousSourceUnityFrame -and
        $elapsedNanoseconds -eq $expectedNanoseconds -and
        [Math]::Abs($nativeElapsedMs - $expectedElapsedMs) -le 0.0000011 -and
        [Math]::Abs($gpuRegionElapsedMs - $nativeElapsedMs) -le 0.000000000001
    if ($row.operation -ne 'control') {
        $valid = $valid -and
            $elapsedTicks -gt 0 -and
            $nativeElapsedMs -gt 0.0
    }
    if ($observedNativeFrequency -eq 0 -and $frequency -ne 0) {
        $observedNativeFrequency = $frequency
    }
    elseif ($frequency -ne $observedNativeFrequency) {
        $valid = $false
    }
    if (-not $valid) {
        $nativeRowFailures.Add(
            "row=$rowIndex case=$($row.caseId) token=$token status=$($row.nativeTimestampStatus)")
    }
    $previousSourceUnityFrame = $sourceUnityFrame
}

$requiredZeroSummaryFields = @(
    'nativeTimestampAcquireFailures',
    'nativeTimestampResultFailures',
    'nativeTimestampTimeouts',
    'nativeTimestampActiveSamples',
    'nativeTimestampReservedSamples',
    'nativeTimestampSubmittedSamples',
    'nativeTimestampPendingRows',
    'nativeTimestampTerminal',
    'nativeTimestampObservedFrequencyMismatches',
    'nativeTimestampObservedZeroFrequencies'
)
$nativeSummaryComplete =
    [int](Get-RequiredSummaryValue $runSummary 'nativeTimestampBackendAvailable') -eq 1 -and
    [bool]$config.nativeTimestampWarmupPassed -and
    [string]$config.nativeTimestampWarmupStatus -eq 'ready' -and
    [uint64]$config.nativeTimestampWarmupFrequency -ne 0 -and
    [uint64]$config.nativeTimestampWarmupFenceValue -gt 0 -and
    [uint32]$config.nativeTimestampWarmupDeviceGeneration -eq
        [uint32]$config.nativeTimestampDeviceGeneration -and
    [int]$config.nativeTimestampWarmupInstrumentationReadbackBytes -eq 16 -and
    [int](Get-RequiredSummaryValue $runSummary 'nativeTimestampWarmupPassed') -eq 1 -and
    [string](Get-RequiredSummaryValue $runSummary 'nativeTimestampWarmupStatus') -eq
        'ready' -and
    [uint64](Get-RequiredSummaryValue $runSummary 'nativeTimestampWarmupFrequency') -ne 0 -and
    [uint64](Get-RequiredSummaryValue $runSummary 'nativeTimestampWarmupFenceValue') -gt 0 -and
    [uint32](Get-RequiredSummaryValue $runSummary 'nativeTimestampWarmupDeviceGeneration') -eq
        [uint32]$config.nativeTimestampDeviceGeneration -and
    [int](Get-RequiredSummaryValue $runSummary `
        'nativeTimestampWarmupInstrumentationReadbackBytes') -eq 16 -and
    [int](Get-RequiredSummaryValue $runSummary 'gpuRegionTimingComplete') -eq 1 -and
    [int](Get-RequiredSummaryValue $runSummary 'performanceMetricsUsable') -eq 1 -and
    [int](Get-RequiredSummaryValue $runSummary 'nativeTimestampReadySamples') -eq
        $raw.Count -and
    [int](Get-RequiredSummaryValue $runSummary 'nativeTimestampObservedFrequencySamples') -eq
        $raw.Count -and
    [int](Get-RequiredSummaryValue $runSummary 'nativeTimestampObservedFrequencyConsistent') -eq 1 -and
    [uint64](Get-RequiredSummaryValue $runSummary 'nativeTimestampObservedFrequency') -eq
        $observedNativeFrequency
foreach ($field in $requiredZeroSummaryFields) {
    if ([int64](Get-RequiredSummaryValue $runSummary $field) -ne 0) {
        $nativeSummaryComplete = $false
    }
}

$invalidGpuBlocks = @(
    $blocks |
        Where-Object {
            [int]$_.gpuRegionValidSamples -ne [int]$_.samples
        }
)
$nativeTimingComplete =
    $nativeRowFailures.Count -eq 0 -and
    $nativeSummaryComplete -and
    $invalidGpuBlocks.Count -eq 0
if (-not $nativeTimingComplete -and ([bool]$config.requireCompleteGpuTimings -or $RequireNativeIntegrity)) {
    $rowDetails = @($nativeRowFailures | Select-Object -First 5) -join '; '
    throw (
        'Native timestamp completeness gate failed: ' +
        "rowFailures=$($nativeRowFailures.Count), " +
        "summaryComplete=$([int]$nativeSummaryComplete), " +
        "invalidBlocks=$($invalidGpuBlocks.Count). $rowDetails")
}
if (-not $nativeTimingComplete) {
    Write-Warning (
        'Native timestamp data is incomplete; output is correctness-only and ' +
        'must not be used for performance claims.')
}

$fenceFailures = @(
    $blocks |
        Where-Object {
            [int]$_.fenceSupported -eq 1 -and [int]$_.fencePassed -ne 1
        }
)
if ($fenceFailures.Count -ne 0) {
    throw "$($fenceFailures.Count) measured blocks failed their completion fence."
}

$validationFailures = @($validation | Where-Object { [int]$_.passed -ne 1 })
if ($validationFailures.Count -ne 0) {
    throw "$($validationFailures.Count) correctness validations failed."
}

$matrixCases = if ($formalAcceptanceMode) {
    $frozenCases
}
else {
    [string[]]@($config.selectedCases)
}
$expectedBlockCount = [int]$config.rounds * $matrixCases.Count
$expectedRawCount = $expectedBlockCount * [int]$config.sampleFrames
if ($blocks.Count -ne $expectedBlockCount) {
    throw (
        "Block count mismatch: expected $expectedBlockCount, " +
        "observed $($blocks.Count).")
}
if ($raw.Count -ne $expectedRawCount) {
    throw (
        "Raw row count mismatch: expected $expectedRawCount, " +
        "observed $($raw.Count).")
}

$observedRoundIds = [int[]]@(
    $blocks |
        ForEach-Object { [int]$_.round } |
        Sort-Object -Unique)
$expectedRoundIds = [int[]]@(1..([int]$config.rounds))
if ($formalAcceptanceMode) {
    $expectedRoundIds = [int[]]@(1, 2, 3)
}
if ($observedRoundIds.Count -ne $expectedRoundIds.Count) {
    throw 'Observed round IDs do not match the expected sequential matrix.'
}
for ($index = 0; $index -lt $expectedRoundIds.Count; $index++) {
    if ($observedRoundIds[$index] -ne $expectedRoundIds[$index]) {
        throw (
            "Round ID mismatch at index ${index}: expected " +
            "$($expectedRoundIds[$index]), observed $($observedRoundIds[$index]).")
    }
}

for ($round = 1; $round -le [int]$config.rounds; $round++) {
    $expectedOrder = [string[]]@(
        Get-ControllerCaseOrder -Cases $matrixCases -Round $round)
    $expectedOrderText = $expectedOrder -join '>'
    $roundBlocks = @(
        $blocks |
            Where-Object { [int]$_.round -eq $round } |
            Sort-Object { [int]$_.orderPosition })
    if ($roundBlocks.Count -ne $matrixCases.Count) {
        throw (
            "Round $round contains $($roundBlocks.Count) blocks; " +
            "expected $($matrixCases.Count).")
    }

    for ($position = 1; $position -le $matrixCases.Count; $position++) {
        $block = $roundBlocks[$position - 1]
        $expectedCase = $expectedOrder[$position - 1]
        $expectedBlockIndex =
            ($round - 1) * $matrixCases.Count + $position
        if ([int]$block.blockIndex -ne $expectedBlockIndex -or
            [int]$block.orderPosition -ne $position -or
            [string]$block.caseId -cne $expectedCase -or
            [string]$block.order -cne $expectedOrderText) {
            throw (
                "Counterbalance mismatch in round $round position ${position}: " +
                "expected block=$expectedBlockIndex case='$expectedCase' " +
                "order='$expectedOrderText'.")
        }
        if ([int]$block.samples -ne [int]$config.sampleFrames) {
            throw (
                "Block $expectedBlockIndex sample count is $($block.samples); " +
                "expected $($config.sampleFrames).")
        }

        $expectedOperation = if ($expectedCase -eq
            'control/empty-command-buffer') {
            'control'
        }
        else {
            ($expectedCase -split '/', 2)[0]
        }
        $expectedVariant = if ($expectedOperation -eq 'control') {
            'empty-command-buffer'
        }
        else {
            ($expectedCase -split '/', 2)[1]
        }
        if ([string]$block.operation -cne $expectedOperation -or
            [string]$block.variant -cne $expectedVariant) {
            throw "Block $expectedBlockIndex operation/variant metadata is inconsistent."
        }
        $expectedDispatches = if ($expectedOperation -eq 'control') {
            0
        }
        else {
            [int]$config.dispatchesPerFrame
        }
        if ([int]$block.dispatchesPerFrame -ne $expectedDispatches) {
            throw "Block $expectedBlockIndex dispatch count is inconsistent."
        }
        if ($formalAcceptanceMode -and
            ([int]$block.fenceSupported -ne 1 -or
                [int]$block.fencePassed -ne 1)) {
            throw "Formal block $expectedBlockIndex lacks a passed completion fence."
        }

        $rawBlock = @(
            $raw |
                Where-Object {
                    [int]$_.blockIndex -eq $expectedBlockIndex
                } |
                Sort-Object { [int]$_.sampleIndex })
        if ($rawBlock.Count -ne [int]$config.sampleFrames) {
            throw (
                "Block $expectedBlockIndex has $($rawBlock.Count) raw rows; " +
                "expected $($config.sampleFrames).")
        }
        for ($sample = 1; $sample -le $rawBlock.Count; $sample++) {
            $rawRow = $rawBlock[$sample - 1]
            if ([int]$rawRow.sampleIndex -ne $sample -or
                [int]$rawRow.round -ne $round -or
                [int]$rawRow.orderPosition -ne $position -or
                [string]$rawRow.order -cne $expectedOrderText -or
                [string]$rawRow.caseId -cne $expectedCase -or
                [string]$rawRow.operation -cne $expectedOperation -or
                [string]$rawRow.variant -cne $expectedVariant -or
                [string]$rawRow.marker -cne [string]$block.marker) {
                throw (
                    "Raw metadata mismatch in block $expectedBlockIndex " +
                    "sample $sample.")
            }
        }

        $frameValues = [double[]]@(
            $rawBlock | ForEach-Object {
                Convert-ToDouble $_.frameMs 'raw.frameMs'
            })
        $gpuRegionValues = [double[]]@(
            $rawBlock |
                Where-Object { [int]$_.gpuRegionTimingValid -eq 1 } |
                ForEach-Object {
                    Convert-ToDouble $_.gpuRegionElapsedMs 'raw.gpuRegionElapsedMs'
                })
        $gpuFrameValues = [double[]]@(
            $rawBlock |
                Where-Object { [int]$_.gpuFrameTimingValid -eq 1 } |
                ForEach-Object {
                    Convert-ToDouble $_.gpuFrameMs 'raw.gpuFrameMs'
                })
        $enqueueValues = [double[]]@(
            $rawBlock | ForEach-Object {
                Convert-ToDouble $_.enqueueCpuMs 'raw.enqueueCpuMs'
            })
        $mainValues = [double[]]@(
            $rawBlock | ForEach-Object {
                Convert-ToDouble $_.mainThreadMs 'raw.mainThreadMs'
            })
        $renderValues = [double[]]@(
            $rawBlock | ForEach-Object {
                Convert-ToDouble $_.renderThreadMs 'raw.renderThreadMs'
            })
        $frameStats = Get-MetricStats $frameValues
        $gpuRegionStats = Get-MetricStats $gpuRegionValues
        $gpuFrameStats = Get-MetricStats $gpuFrameValues
        $enqueueStats = Get-MetricStats $enqueueValues
        $mainStats = Get-MetricStats $mainValues
        $renderStats = Get-MetricStats $renderValues

        if ([int]$block.gpuRegionValidSamples -ne $gpuRegionValues.Count -or
            [int]$block.gpuFrameValidSamples -ne $gpuFrameValues.Count) {
            throw "Block $expectedBlockIndex valid-sample counts do not match raw rows."
        }
        foreach ($metric in @(
                @('frameAverageMs', $frameStats.Average),
                @('frameP50Ms', $frameStats.P50),
                @('frameP95Ms', $frameStats.P95),
                @('frameP99Ms', $frameStats.P99),
                @('gpuRegionAverageMs', $gpuRegionStats.Average),
                @('gpuRegionP50Ms', $gpuRegionStats.P50),
                @('gpuRegionP95Ms', $gpuRegionStats.P95),
                @('gpuRegionP99Ms', $gpuRegionStats.P99),
                @('gpuFrameAverageMs', $gpuFrameStats.Average),
                @('gpuFrameP50Ms', $gpuFrameStats.P50),
                @('gpuFrameP95Ms', $gpuFrameStats.P95),
                @('gpuFrameP99Ms', $gpuFrameStats.P99),
                @('enqueueAverageMs', $enqueueStats.Average),
                @('enqueueP50Ms', $enqueueStats.P50),
                @('enqueueP95Ms', $enqueueStats.P95),
                @('enqueueP99Ms', $enqueueStats.P99),
                @('mainAverageMs', $mainStats.Average),
                @('mainP99Ms', $mainStats.P99),
                @('renderAverageMs', $renderStats.Average),
                @('renderP99Ms', $renderStats.P99))) {
            $actualMetric = Convert-ToDouble `
                $block.PSObject.Properties[$metric[0]].Value `
                "block.$($metric[0])"
            Assert-NearlyEqual `
                -Actual $actualMetric `
                -Expected ([double]$metric[1]) `
                -Name "block=$expectedBlockIndex.$($metric[0])"
        }
        $rawInstrumentationBytes = [int64]((
            $rawBlock |
                Measure-Object `
                    -Property timestampInstrumentationReadbackBytes `
                    -Sum).Sum)
        if ($rawInstrumentationBytes -ne
                ([int64]$config.sampleFrames * 16) -or
            [int64]$block.timestampInstrumentationReadbackBytes -ne
                $rawInstrumentationBytes) {
            throw "Block $expectedBlockIndex timestamp-byte accounting is inconsistent."
        }
    }
}

$caseIds = @(
    $blocks |
        Where-Object { $_.operation -ne 'control' } |
        Select-Object -ExpandProperty caseId -Unique
)
foreach ($caseId in $caseIds) {
    $caseValidation = @($validation | Where-Object { $_.caseId -eq $caseId })
    $warmup = @($caseValidation | Where-Object { $_.phase -eq 'warmup' })
    $final = @($caseValidation | Where-Object { $_.phase -eq 'final' })
    if ($warmup.Count -ne 1 -or $final.Count -ne 1) {
        throw "Case '$caseId' must have exactly one warmup and one final validation."
    }
    if ($warmup[0].resultHash -ne $final[0].resultHash) {
        throw "Case '$caseId' changed correctness hash between warmup and final validation."
    }
}

$operations = @(
    $blocks |
        Where-Object { $_.operation -ne 'control' } |
        Select-Object -ExpandProperty operation -Unique |
        Sort-Object
)
foreach ($operation in $operations) {
    foreach ($phase in @('warmup', 'final')) {
        $hashes = @(
            $validation |
                Where-Object {
                    $_.operation -eq $operation -and $_.phase -eq $phase
                } |
                Select-Object -ExpandProperty resultHash -Unique
        )
        if ($hashes.Count -ne 1) {
            throw (
                "Operation '$operation' has non-equivalent backend hashes in " +
                "$phase validation: $($hashes -join ', ')")
        }
    }
}

$roundIds = @($blocks.round | Sort-Object { [int]$_ } -Unique)
if ($roundIds.Count -ne [int]$config.rounds) {
    throw (
        "Observed $($roundIds.Count) rounds, config requested " +
        "$($config.rounds).")
}
foreach ($roundId in $roundIds) {
    $roundRows = @($blocks | Where-Object { $_.round -eq $roundId })
    $controlRows = @($roundRows | Where-Object { $_.operation -eq 'control' })
    if ($controlRows.Count -ne 1) {
        throw "Round $roundId must contain exactly one empty-control block."
    }
    foreach ($caseId in $caseIds) {
        $caseRows = @($roundRows | Where-Object { $_.caseId -eq $caseId })
        if ($caseRows.Count -ne 1) {
            throw "Round $roundId must contain exactly one '$caseId' block."
        }
    }
}

$paired = [System.Collections.Generic.List[object]]::new()
foreach ($roundId in $roundIds) {
    foreach ($operation in $operations) {
        $portable = @(
            $blocks |
                Where-Object {
                    $_.round -eq $roundId -and
                    $_.operation -eq $operation -and
                    $_.variant -eq 'portable'
                })
        $wave = @(
            $blocks |
                Where-Object {
                    $_.round -eq $roundId -and
                    $_.operation -eq $operation -and
                    $_.variant -eq 'wave-ops'
                })
        if ($portable.Count -eq 0 -and $wave.Count -eq 0) {
            continue
        }
        if ($portable.Count -ne 1 -or $wave.Count -ne 1) {
            Write-Warning (
                "Skipping paired delta for '$operation' round ${roundId}: " +
                "portable=$($portable.Count), wave-ops=$($wave.Count).")
            continue
        }

        if ([int]$portable[0].gpuRegionValidSamples -ne [int]$portable[0].samples -or
            [int]$wave[0].gpuRegionValidSamples -ne [int]$wave[0].samples) {
            Write-Warning (
                "Skipping performance delta for '$operation' round ${roundId}: " +
                "GPU marker-region timings are incomplete.")
            continue
        }

        $portableGpuAverage = Convert-ToDouble $portable[0].gpuRegionAverageMs 'portable.gpuRegionAverageMs'
        $waveGpuAverage = Convert-ToDouble $wave[0].gpuRegionAverageMs 'wave.gpuRegionAverageMs'
        $portableGpuP99 = Convert-ToDouble $portable[0].gpuRegionP99Ms 'portable.gpuRegionP99Ms'
        $waveGpuP99 = Convert-ToDouble $wave[0].gpuRegionP99Ms 'wave.gpuRegionP99Ms'
        $portableFrameAverage = Convert-ToDouble $portable[0].frameAverageMs 'portable.frameAverageMs'
        $waveFrameAverage = Convert-ToDouble $wave[0].frameAverageMs 'wave.frameAverageMs'
        $portableFrameP99 = Convert-ToDouble $portable[0].frameP99Ms 'portable.frameP99Ms'
        $waveFrameP99 = Convert-ToDouble $wave[0].frameP99Ms 'wave.frameP99Ms'
        $portableEnqueue = Convert-ToDouble $portable[0].enqueueAverageMs 'portable.enqueueAverageMs'
        $waveEnqueue = Convert-ToDouble $wave[0].enqueueAverageMs 'wave.enqueueAverageMs'

        $paired.Add([pscustomobject]@{
            processId = [int]$processIds[0]
            round = [int]$roundId
            order = $portable[0].order
            operation = $operation
            portableGpuRegionAverageMs = $portableGpuAverage
            waveGpuRegionAverageMs = $waveGpuAverage
            gpuAverageImprovementPercent =
                Get-ImprovementPercent $portableGpuAverage $waveGpuAverage
            gpuAverageAbsoluteReductionMs =
                $portableGpuAverage - $waveGpuAverage
            portableGpuRegionP99Ms = $portableGpuP99
            waveGpuRegionP99Ms = $waveGpuP99
            gpuP99ImprovementPercent =
                Get-ImprovementPercent $portableGpuP99 $waveGpuP99
            portableFrameAverageMs = $portableFrameAverage
            waveFrameAverageMs = $waveFrameAverage
            frameAverageImprovementPercent =
                Get-ImprovementPercent $portableFrameAverage $waveFrameAverage
            portableFrameP99Ms = $portableFrameP99
            waveFrameP99Ms = $waveFrameP99
            frameP99ImprovementPercent =
                Get-ImprovementPercent $portableFrameP99 $waveFrameP99
            portableEnqueueAverageMs = $portableEnqueue
            waveEnqueueAverageMs = $waveEnqueue
            enqueueImprovementPercent =
                Get-ImprovementPercent $portableEnqueue $waveEnqueue
        })
    }
}

$pairedPath = Join-Path $outputRoot 'paired-deltas.csv'
$paired | Export-Csv -LiteralPath $pairedPath -NoTypeInformation -Encoding utf8

$operationSummary = [System.Collections.Generic.List[object]]::new()
foreach ($operation in $operations) {
    $rows = @($paired | Where-Object { $_.operation -eq $operation })
    if ($rows.Count -eq 0) {
        continue
    }
    $gpuAverage = [double[]]@(
        $rows | ForEach-Object { [double]$_.gpuAverageImprovementPercent })
    $gpuP99 = [double[]]@(
        $rows | ForEach-Object { [double]$_.gpuP99ImprovementPercent })
    $frameAverage = [double[]]@(
        $rows | ForEach-Object { [double]$_.frameAverageImprovementPercent })
    $frameP99 = [double[]]@(
        $rows | ForEach-Object { [double]$_.frameP99ImprovementPercent })
    $gpuAverageAbsolute = [double[]]@(
        $rows | ForEach-Object { [double]$_.gpuAverageAbsoluteReductionMs })
    $positiveGpuAveragePairs =
        @($gpuAverage | Where-Object { $_ -gt 0.0 }).Count
    $gpuAverageMedian = Get-Median $gpuAverage
    $gpuAverageAbsoluteMedian = Get-Median $gpuAverageAbsolute
    $gpuP99Median = Get-Median $gpuP99
    $operationValidation = @(
        $validation | Where-Object { $_.operation -eq $operation })
    $correctnessInvariant =
        $operationValidation.Count -eq 4 -and
        @($operationValidation | Where-Object { [int]$_.passed -ne 1 }).Count -eq 0
    foreach ($phase in @('warmup', 'final')) {
        $phaseHashes = @(
            $operationValidation |
                Where-Object { $_.phase -eq $phase } |
                Select-Object -ExpandProperty resultHash -Unique)
        if ($phaseHashes.Count -ne 1) {
            $correctnessInvariant = $false
        }
    }
    $readbackInvariant = $true
    foreach ($phase in @('warmup', 'final')) {
        $phaseReadbacks = @(
            $operationValidation |
                Where-Object { $_.phase -eq $phase } |
                Select-Object -ExpandProperty readbackBytes -Unique)
        if ($phaseReadbacks.Count -ne 1) {
            $readbackInvariant = $false
        }
    }
    $residentBytesInvariant = [int64]$config.totalResidentBytes -gt 0
    $hasExactThreePairs = $rows.Count -eq 3
    $positivePairGate = $positiveGpuAveragePairs -ge 2
    $medianPercentGate = $gpuAverageMedian -ge 3.0
    $medianAbsoluteGate = $gpuAverageAbsoluteMedian -ge 0.005
    $p99Gate = $gpuP99Median -ge -5.0
    $meetsFrozenImprovementGate =
        $hasExactThreePairs -and
        $positivePairGate -and
        $medianPercentGate -and
        $medianAbsoluteGate -and
        $p99Gate -and
        $correctnessInvariant -and
        $readbackInvariant -and
        $residentBytesInvariant
    $classification = if ($meetsFrozenImprovementGate) {
        'accepted'
    }
    elseif ($gpuAverageMedian -lt 0.0) {
        'negative'
    }
    else {
        'neutral-or-inconclusive'
    }
    $operationSummary.Add([pscustomobject]@{
        operation = $operation
        pairedRounds = $rows.Count
        hasExactThreePairs = [int]$hasExactThreePairs
        positiveGpuAveragePairCount = $positiveGpuAveragePairs
        positiveGpuAveragePairGate = [int]$positivePairGate
        gpuAverageMedianPercentGate = [int]$medianPercentGate
        gpuAverageAbsoluteReductionMedianMs = $gpuAverageAbsoluteMedian
        gpuAverageMedianAbsoluteGate = [int]$medianAbsoluteGate
        gpuP99NoWorseThanMinus5PercentGate = [int]$p99Gate
        correctnessInvariant = [int]$correctnessInvariant
        readbackInvariant = [int]$readbackInvariant
        residentBytesInvariant = [int]$residentBytesInvariant
        sharedResidentBytes = [int64]$config.totalResidentBytes
        meetsFrozenImprovementGate = [int]$meetsFrozenImprovementGate
        classification = $classification
        gpuAverageImprovementMedianPercent = Get-Median $gpuAverage
        gpuAverageImprovementMinimumPercent =
            ($gpuAverage | Measure-Object -Minimum).Minimum
        gpuAverageImprovementMaximumPercent =
            ($gpuAverage | Measure-Object -Maximum).Maximum
        gpuP99ImprovementMedianPercent = Get-Median $gpuP99
        gpuP99ImprovementMinimumPercent =
            ($gpuP99 | Measure-Object -Minimum).Minimum
        gpuP99ImprovementMaximumPercent =
            ($gpuP99 | Measure-Object -Maximum).Maximum
        frameAverageImprovementMedianPercent = Get-Median $frameAverage
        frameP99ImprovementMedianPercent = Get-Median $frameP99
        allGpuAveragePairsImproved =
            [int](@($gpuAverage | Where-Object { $_ -le 0.0 }).Count -eq 0)
        allGpuP99PairsImproved =
            [int](@($gpuP99 | Where-Object { $_ -le 0.0 }).Count -eq 0)
    })
}

$operationSummaryPath = Join-Path $outputRoot 'operation-summary.csv'
$operationSummary |
    Export-Csv -LiteralPath $operationSummaryPath -NoTypeInformation -Encoding utf8

$controls = @($blocks | Where-Object { $_.operation -eq 'control' })
$controlNativeAverage = [double[]]@(
    $controls |
        Where-Object { [int]$_.gpuRegionValidSamples -eq [int]$_.samples } |
        ForEach-Object {
        Convert-ToDouble $_.gpuRegionAverageMs 'control.gpuRegionAverageMs'
    })
$controlNativeP99 = [double[]]@(
    $controls |
        Where-Object { [int]$_.gpuRegionValidSamples -eq [int]$_.samples } |
        ForEach-Object {
        Convert-ToDouble $_.gpuRegionP99Ms 'control.gpuRegionP99Ms'
    })
$emptyScopeRawMs = [double[]]@(
    $raw |
        Where-Object {
            $_.operation -eq 'control' -and
            [int]$_.gpuRegionTimingValid -eq 1
        } |
        ForEach-Object {
        Convert-ToDouble $_.gpuRegionElapsedMs 'control.gpuRegionElapsedMs'
    })
$controlDriftNativeAverage = if ($controlNativeAverage.Count -gt 0) {
    Get-DriftPercent $controlNativeAverage
}
else { -1.0 }
$controlDriftNativeP99 = if ($controlNativeP99.Count -gt 0) {
    Get-DriftPercent $controlNativeP99
}
else { -1.0 }
$emptyScopeRawAverage = if ($emptyScopeRawMs.Count -gt 0) {
    ($emptyScopeRawMs | Measure-Object -Average).Average
}
else { -1.0 }
$emptyScopeRawP50 = if ($emptyScopeRawMs.Count -gt 0) {
    Get-Percentile $emptyScopeRawMs 0.50
}
else { -1.0 }
$emptyScopeRawP95 = if ($emptyScopeRawMs.Count -gt 0) {
    Get-Percentile $emptyScopeRawMs 0.95
}
else { -1.0 }
$emptyScopeRawP99 = if ($emptyScopeRawMs.Count -gt 0) {
    Get-Percentile $emptyScopeRawMs 0.99
}
else { -1.0 }
$expectedControlRawCount =
    [int]$config.rounds * [int]$config.sampleFrames
if ($emptyScopeRawMs.Count -ne $expectedControlRawCount) {
    throw (
        "Empty-scope raw count mismatch: expected $expectedControlRawCount, " +
        "observed $($emptyScopeRawMs.Count).")
}
$emptyScopeGatePassed = $emptyScopeRawP99 -le 0.005
if ($formalAcceptanceMode -and -not $emptyScopeGatePassed) {
    throw (
        "Formal empty-scope P99 gate failed: $emptyScopeRawP99 ms exceeds 0.005 ms.")
}

if ([int](Get-RequiredSummaryValue $runSummary 'passed') -ne 1) {
    throw 'run-summary.txt does not report a passed benchmark.'
}
$performanceMetricsUsable = $nativeTimingComplete
$expectedPairedRows = 3 * $frozenOperations.Count
$operationSummariesComplete =
    $operationSummary.Count -eq $frozenOperations.Count -and
    @($operationSummary |
        Where-Object { [int]$_.pairedRounds -ne 3 }).Count -eq 0
$aBDataUsable =
    $formalAcceptanceMode -and
    $performanceMetricsUsable -and
    $roundIds.Count -eq 3 -and
    (Test-StringSetEqual `
        -Actual ([string[]]$operations) `
        -Expected $frozenOperations) -and
    $paired.Count -eq $expectedPairedRows -and
    $operationSummariesComplete -and
    $emptyScopeGatePassed
$allOperationsMeetFrozenImprovementGate =
    $operationSummariesComplete -and
    @($operationSummary |
        Where-Object { [int]$_.meetsFrozenImprovementGate -ne 1 }).Count -eq 0
$anyOperationMeetsFrozenImprovementGate =
    @($operationSummary |
        Where-Object { [int]$_.meetsFrozenImprovementGate -eq 1 }).Count -gt 0
$aBPerformanceClaimUsable = $aBDataUsable
$matchedDriverNames = @($driverMatches | ForEach-Object { $_.name }) -join '|'
$matchedDriverVersions =
    @($driverMatches | ForEach-Object { $_.driverVersion }) -join '|'
$qualityStatus = if ($performanceMetricsUsable) {
    'performance-usable'
}
else { 'correctness-only' }
$qualityLines = [System.Collections.Generic.List[string]]::new()
$qualityLines.Add('GPU primitive benchmark quality summary')
$qualityLines.Add("status=$qualityStatus")
$qualityLines.Add("source=$reportRoot")
$qualityLines.Add("formalAcceptanceMode=$([int]$formalAcceptanceMode)")
$qualityLines.Add("runnerConfigSchemaVersion=$($runnerConfig.schemaVersion)")
$qualityLines.Add("gitCommit=$($runnerConfig.gitCommit)")
$qualityLines.Add("gitBranch=$($runnerConfig.gitBranch)")
$qualityLines.Add("gitHeadAtStart=$($runnerConfig.gitStart.head)")
$qualityLines.Add(
    "gitHeadPostBuild=$($runnerConfig.gitPostBuildBeforeRestore.head)")
$qualityLines.Add("gitHeadAtFinal=$($runnerConfig.gitFinal.head)")
$qualityLines.Add(
    "gitStatusAtStartCount=$(@($runnerConfig.gitStart.statusLines).Count)")
$qualityLines.Add(
    "gitStatusPostBuildBeforeRestoreCount=" +
    @($runnerConfig.gitPostBuildBeforeRestore.statusLines).Count)
$qualityLines.Add(
    "gitStatusAtFinalBeforeRestoreCount=" +
    @($runnerConfig.gitFinalBeforeRestore.statusLines).Count)
$qualityLines.Add("gitTreeDirtyFinal=$([int]([bool]$runnerConfig.gitTreeDirty))")
$qualityLines.Add(
    "gitPostBuildRestoredPaths=" +
    (@($runnerConfig.gitPostBuildRestoredPaths) -join '|'))
$qualityLines.Add(
    "gitFinalRestoredPaths=" +
    (@($runnerConfig.gitFinalRestoredPaths) -join '|'))
$qualityLines.Add("playerExecutableSha256=$($runnerConfig.playerExecutableSha256)")
$qualityLines.Add("playerPayloadSha256=$($runnerConfig.playerPayload.sha256)")
$qualityLines.Add("playerPayloadFileCount=$($runnerConfig.playerPayload.fileCount)")
$qualityLines.Add(
    "playerPayloadLengthBytes=$($runnerConfig.playerPayload.lengthBytes)")
$qualityLines.Add(
    "playerPayloadDataFileCount=$($runnerConfig.playerPayload.dataFileCount)")
$qualityLines.Add(
    "playerPayloadStableThroughRun=" +
    [int]([bool]$runnerConfig.playerPayloadStableThroughRun))
$qualityLines.Add("driverMatchKind=$driverMatchKind")
$qualityLines.Add("driverMatchCount=$($driverMatches.Count)")
$qualityLines.Add("driverMatchAmbiguous=$([int]($driverMatches.Count -gt 1))")
$qualityLines.Add("matchedDriverNames=$matchedDriverNames")
$qualityLines.Add("matchedDriverVersions=$matchedDriverVersions")
$qualityLines.Add("processId=$($processIds[0])")
$qualityLines.Add("device=$($device.graphicsDeviceName)")
$qualityLines.Add("graphicsApi=$($device.graphicsDeviceType)")
$qualityLines.Add("supportsGpuRecorder=$([int]([bool]$device.supportsGpuRecorder))")
$qualityLines.Add("rounds=$($roundIds.Count)")
$qualityLines.Add("operations=$($operations -join ',')")
$qualityLines.Add("caseCount=$($caseIds.Count)")
$qualityLines.Add("rawSampleCount=$($raw.Count)")
$qualityLines.Add("singlePlayerProcess=1")
$qualityLines.Add("sameProcessCounterbalanced=1")
$qualityLines.Add("gpuRegionTimingComplete=$([int]$performanceMetricsUsable)")
$qualityLines.Add("aBDataUsable=$([int]$aBDataUsable)")
$qualityLines.Add(
    "meetsFrozenImprovementGate=" +
    [int]$allOperationsMeetFrozenImprovementGate)
$qualityLines.Add(
    "anyPrimitiveMeetsFrozenImprovementGate=" +
    [int]$anyOperationMeetsFrozenImprovementGate)
$qualityLines.Add("performanceMetricsUsable=$([int]$performanceMetricsUsable)")
$qualityLines.Add("aBPerformanceClaimUsable=$([int]$aBPerformanceClaimUsable)")
$qualityLines.Add('aBMinimumCompleteCounterbalancedRounds=3')
$qualityLines.Add('measurementReadbackBytes=0')
$qualityLines.Add("nativeTimestampObservedFrequency=$observedNativeFrequency")
$qualityLines.Add("nativeTimestampRowsComplete=$([int]($nativeRowFailures.Count -eq 0))")
$qualityLines.Add("nativeTimestampWarmupPassed=$([int]([bool]$config.nativeTimestampWarmupPassed))")
$qualityLines.Add("nativeTimestampWarmupStatus=$($config.nativeTimestampWarmupStatus)")
$qualityLines.Add("nativeTimestampWarmupFrequency=$($config.nativeTimestampWarmupFrequency)")
$qualityLines.Add("nativeTimestampWarmupFenceValue=$($config.nativeTimestampWarmupFenceValue)")
$qualityLines.Add(
    "nativeTimestampWarmupDeviceGeneration=$($config.nativeTimestampWarmupDeviceGeneration)")
$qualityLines.Add(
    "nativeTimestampWarmupInstrumentationReadbackBytes=" +
    $config.nativeTimestampWarmupInstrumentationReadbackBytes)
$qualityLines.Add("validationRows=$($validation.Count)")
$qualityLines.Add('validationFailures=0')
$qualityLines.Add('emptyScopeOverheadSubtracted=0')
$qualityLines.Add("emptyScopeRawSamples=$($emptyScopeRawMs.Count)")
$qualityLines.Add(("emptyScopeRawAverageMs={0:F6}" -f $emptyScopeRawAverage))
$qualityLines.Add("emptyScopeP99GatePassed=$([int]$emptyScopeGatePassed)")
$qualityLines.Add(("emptyScopeRawP50Ms={0:F6}" -f $emptyScopeRawP50))
$qualityLines.Add(("emptyScopeRawP95Ms={0:F6}" -f $emptyScopeRawP95))
$qualityLines.Add(("emptyScopeRawP99Ms={0:F6}" -f $emptyScopeRawP99))
$qualityLines.Add(
    ("controlNativeAverageDriftPercent={0:F3}" -f $controlDriftNativeAverage))
$qualityLines.Add(
    ("controlNativeP99DriftPercent={0:F3}" -f $controlDriftNativeP99))
$qualityLines.Add("pairedDeltaRows=$($paired.Count)")
foreach ($row in $operationSummary) {
    $prefix = $row.operation.Replace('-', '_')
    $qualityLines.Add("$prefix.classification=$($row.classification)")
    $qualityLines.Add(
        "$prefix.positiveGpuAveragePairCount=$($row.positiveGpuAveragePairCount)")
    $qualityLines.Add(
        ("{0}.gpuAverageAbsoluteReductionMedianMs={1:F6}" -f
            $prefix,
            $row.gpuAverageAbsoluteReductionMedianMs))
    $qualityLines.Add(
        "$prefix.correctnessInvariant=$($row.correctnessInvariant)")
    $qualityLines.Add("$prefix.readbackInvariant=$($row.readbackInvariant)")
    $qualityLines.Add(
        "$prefix.residentBytesInvariant=$($row.residentBytesInvariant)")
    $qualityLines.Add(
        "$prefix.meetsFrozenImprovementGate=$($row.meetsFrozenImprovementGate)")
    $qualityLines.Add(
        ("{0}.gpuAverageImprovementMedianPercent={1:F3}" -f
            $prefix,
            $row.gpuAverageImprovementMedianPercent))
    $qualityLines.Add(
        ("{0}.gpuP99ImprovementMedianPercent={1:F3}" -f
            $prefix,
            $row.gpuP99ImprovementMedianPercent))
    $qualityLines.Add(
        "$prefix.allGpuAveragePairsImproved=$($row.allGpuAveragePairsImproved)")
    $qualityLines.Add(
        "$prefix.allGpuP99PairsImproved=$($row.allGpuP99PairsImproved)")
}

$qualityPath = Join-Path $outputRoot 'quality-summary.txt'
$qualityLines | Set-Content -LiteralPath $qualityPath -Encoding utf8

$qualityLines
"pairedDeltas=$pairedPath"
"operationSummary=$operationSummaryPath"
"qualitySummary=$qualityPath"
