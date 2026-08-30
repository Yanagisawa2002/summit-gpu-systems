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

function Test-SignChangingAcceptedWinner {
    param(
        [Parameter(Mandatory = $true)][string]$LowerWinner,
        [Parameter(Mandatory = $true)][string]$UpperWinner
    )
    $acceptedWinners = @('direct', 'radix')
    return (
        $LowerWinner -cin $acceptedWinners -and
        $UpperWinner -cin $acceptedWinners -and
        $LowerWinner -cne $UpperWinner)
}

function Test-CrossoverClaimUsable {
    param(
        [Parameter(Mandatory = $true)][bool]$ABDataUsable,
        [Parameter(Mandatory = $true)][int]$RadixAcceptedCellCount,
        [Parameter(Mandatory = $true)][int]$DirectAcceptedCellCount,
        [Parameter(Mandatory = $true)][bool]$BracketEvidenceComplete,
        [Parameter(Mandatory = $true)][bool]$SignChangingBracketObserved
    )
    return (
        $ABDataUsable -and
        $RadixAcceptedCellCount -ge 2 -and
        $DirectAcceptedCellCount -ge 2 -and
        $BracketEvidenceComplete -and
        $SignChangingBracketObserved)
}

function Test-SelectorDirectionValidated {
    param(
        [Parameter(Mandatory = $true)][string]$LowerWinner,
        [Parameter(Mandatory = $true)][string]$UpperWinner
    )
    return (
        $LowerWinner -ceq 'radix' -and
        $UpperWinner -ceq 'direct')
}

function Test-SelectorPolicyClaimUsable {
    param(
        [Parameter(Mandatory = $true)][bool]$ABDataUsable,
        [Parameter(Mandatory = $true)][int]$RadixAcceptedCellCount,
        [Parameter(Mandatory = $true)][int]$DirectAcceptedCellCount,
        [Parameter(Mandatory = $true)][bool]$BracketEvidenceComplete,
        [Parameter(Mandatory = $true)][int]$SelectorDirectionValidatedCount,
        [Parameter(Mandatory = $true)][int]$PolicyCellCount,
        [Parameter(Mandatory = $true)][int]$PredictionMatchCount
    )
    return (
        $ABDataUsable -and
        $RadixAcceptedCellCount -ge 2 -and
        $DirectAcceptedCellCount -ge 2 -and
        $BracketEvidenceComplete -and
        $SelectorDirectionValidatedCount -eq 2 -and
        $PolicyCellCount -eq 5 -and
        $PredictionMatchCount -eq 5)
}

function Test-SelectorSurfacePolicyClaimUsable {
    param(
        [Parameter(Mandatory = $true)][bool]$ABDataUsable,
        [Parameter(Mandatory = $true)][int]$RadixAcceptedCellCount,
        [Parameter(Mandatory = $true)][int]$DirectAcceptedCellCount,
        [Parameter(Mandatory = $true)][int]$RequiredRadixAcceptedCells,
        [Parameter(Mandatory = $true)][int]$RequiredDirectAcceptedCells,
        [Parameter(Mandatory = $true)][int]$PolicyCellCount,
        [Parameter(Mandatory = $true)][int]$PredictionMatchCount
    )
    return (
        $ABDataUsable -and
        $RadixAcceptedCellCount -ge $RequiredRadixAcceptedCells -and
        $DirectAcceptedCellCount -ge $RequiredDirectAcceptedCells -and
        $PolicyCellCount -eq 5 -and
        $PredictionMatchCount -eq 5)
}

function Test-SelectorTailCellValidated {
    param(
        [Parameter(Mandatory = $true)][bool]$PredictionMatchesWinner,
        [Parameter(Mandatory = $true)][bool]$WinnerAccepted,
        [Parameter(Mandatory = $true)][int]$WinnerP99WinningPairCount,
        [Parameter(Mandatory = $true)][double]$WorstPairP99ImprovementPercent
    )
    return (
        $PredictionMatchesWinner -and
        $WinnerAccepted -and
        $WinnerP99WinningPairCount -ge 7 -and
        $WorstPairP99ImprovementPercent -ge -10.0)
}

function Test-SelectorPolicyTailClaimUsable {
    param(
        [Parameter(Mandatory = $true)][bool]$SelectorPolicyClaimUsable,
        [Parameter(Mandatory = $true)][int]$PolicyCellCount,
        [Parameter(Mandatory = $true)][int]$TailValidatedCellCount
    )
    return (
        $SelectorPolicyClaimUsable -and
        $PolicyCellCount -eq 5 -and
        $TailValidatedCellCount -eq 5)
}

function Test-FormalActiveDevice {
    param(
        [Parameter(Mandatory = $true)][int]$GraphicsDeviceVendorId,
        [Parameter(Mandatory = $true)][int]$GraphicsDeviceId,
        [Parameter(Mandatory = $true)][string]$GraphicsDeviceType,
        [int]$ExpectedGraphicsDeviceVendorId = 0x1002,
        [int]$ExpectedGraphicsDeviceId = 0x7551,
        [string]$ExpectedGraphicsDeviceType = 'Direct3D12'
    )
    return (
        $GraphicsDeviceVendorId -eq $ExpectedGraphicsDeviceVendorId -and
        $GraphicsDeviceId -eq $ExpectedGraphicsDeviceId -and
        $GraphicsDeviceType -ceq $ExpectedGraphicsDeviceType)
}

function Test-RequiredProfilerMarkersDisabled {
    param(
        [Parameter(Mandatory = $true)][bool]$FieldPresent,
        [Parameter(Mandatory = $true)]
        [AllowNull()][object]$Value
    )
    return (
        $FieldPresent -and
        $Value -is [bool] -and
        -not [bool]$Value)
}

function Get-GeneratorV3SingleBinKey {
    param(
        [Parameter(Mandatory = $true)][int]$Seed,
        [Parameter(Mandatory = $true)][int]$BinCount
    )
    if ($BinCount -le 0) {
        throw 'Generator-v3 exact single-bin key requires positive C.'
    }
    $seedBytes = [BitConverter]::GetBytes([int]$Seed)
    $unsignedSeed = [BitConverter]::ToUInt32($seedBytes, 0)
    return [int]($unsignedSeed % [uint32]$BinCount)
}

function Test-ExactSingleBinKeyContract {
    param(
        [Parameter(Mandatory = $true)][string]$Distribution,
        [Parameter(Mandatory = $true)][int]$Seed,
        [Parameter(Mandatory = $true)][int]$BinCount,
        [Parameter(Mandatory = $true)]
        [AllowNull()][object]$ExactSingleBinKey
    )
    if ($Distribution -cnotin @(
            'uniform',
            'hotset4',
            'hotset16',
            'singlebin') -or
        $null -eq $ExactSingleBinKey) {
        return $false
    }
    $parsedKey = 0
    if (-not [int]::TryParse(
            [string]$ExactSingleBinKey,
            [ref]$parsedKey)) {
        return $false
    }
    $expectedKey = if ($Distribution -ceq 'singlebin') {
        Get-GeneratorV3SingleBinKey -Seed $Seed -BinCount $BinCount
    }
    else {
        -1
    }
    return $parsedKey -eq $expectedKey
}

function Get-ExactCellSelectorPrediction {
    param(
        [Parameter(Mandatory = $true)][int]$ElementCount,
        [Parameter(Mandatory = $true)][int]$BinCount,
        [Parameter(Mandatory = $true)][string]$Distribution,
        [Parameter(Mandatory = $true)][int]$ExactSingleBinKey
    )
    $exactSmallCell =
        $ElementCount -eq 262144 -and
        $BinCount -eq 16 -and
        $Distribution -ceq 'singlebin' -and
        $ExactSingleBinKey -eq 9
    $exactLargeCell =
        $ElementCount -eq 1048576 -and
        $BinCount -eq 16 -and
        $Distribution -ceq 'singlebin' -and
        $ExactSingleBinKey -eq 10
    if ($exactSmallCell -or $exactLargeCell) {
        return 'radix'
    }
    return 'direct'
}

function Get-CalibratedSurfaceSelectorPrediction {
    param(
        [Parameter(Mandatory = $true)][int]$ElementCount,
        [Parameter(Mandatory = $true)][int]$BinCount,
        [Parameter(Mandatory = $true)][string]$Distribution,
        [Parameter(Mandatory = $true)][int]$ExactSingleBinKey,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()][object[]]$RadixCandidateCells
    )
    foreach ($cell in $RadixCandidateCells) {
        if ([int]$cell.elementCount -ne $ElementCount -or
            [int]$cell.binCount -ne $BinCount -or
            [string]$cell.distribution -cne $Distribution) {
            continue
        }

        $keyMode = [string]$cell.singleBinKeyMode
        if ($Distribution -ceq 'singlebin') {
            if ($keyMode -ceq 'any-valid' -and
                $ExactSingleBinKey -ge 0 -and
                $ExactSingleBinKey -lt $BinCount) {
                return 'radix'
            }
            continue
        }

        if ($keyMode -ceq 'not-applicable' -and
            $ExactSingleBinKey -eq -1) {
            return 'radix'
        }
    }

    return 'direct'
}

function Test-ExactCellSelectorPolicyContract {
    param(
        [Parameter(Mandatory = $true)][string]$PolicyLabel,
        [Parameter(Mandatory = $true)][string]$PolicyPredicate,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()][object[]]$ExactRadixCandidateCells
    )
    if ($PolicyLabel -cne
            'radix-if-exact-calibrated-cell-else-direct' -or
        $PolicyPredicate -cne
            'full-element-count-bin-count-distribution-exact-single-bin-key-tuple' -or
        $ExactRadixCandidateCells.Count -ne 2) {
        return $false
    }
    foreach ($cell in $ExactRadixCandidateCells) {
        foreach ($field in @(
                'elementCount',
                'binCount',
                'distribution',
                'exactSingleBinKey')) {
            if ($null -eq $cell.PSObject.Properties[$field]) {
                return $false
            }
        }
    }
    $actual = @(
        $ExactRadixCandidateCells | ForEach-Object {
            "$([int]$_.elementCount)|$([int]$_.binCount)|" +
            "$([string]$_.distribution)|$([int]$_.exactSingleBinKey)"
        })
    $expected = @(
        '262144|16|singlebin|9',
        '1048576|16|singlebin|10')
    return ($actual -join ';') -ceq ($expected -join ';')
}

function Test-CalibratedSurfaceSelectorPolicyContract {
    param(
        [Parameter(Mandatory = $true)][string]$PolicyLabel,
        [Parameter(Mandatory = $true)][string]$PolicyPredicate,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()][object[]]$RadixCandidateCells
    )
    if ($PolicyLabel -cne
            'radix-if-calibrated-cell-else-direct' -or
        $PolicyPredicate -cne
            'element-count-bin-count-distribution-singlebin-key-mode' -or
        $RadixCandidateCells.Count -ne 3) {
        return $false
    }
    foreach ($cell in $RadixCandidateCells) {
        foreach ($field in @(
                'elementCount',
                'binCount',
                'distribution',
                'singleBinKeyMode')) {
            if ($null -eq $cell.PSObject.Properties[$field]) {
                return $false
            }
        }
    }
    $actual = @(
        $RadixCandidateCells | ForEach-Object {
            "$([int]$_.elementCount)|$([int]$_.binCount)|" +
            "$([string]$_.distribution)|$([string]$_.singleBinKeyMode)"
        })
    $expected = @(
        '1048576|16|singlebin|any-valid',
        '1048576|16|hotset4|not-applicable',
        '1048576|16|uniform|not-applicable')
    return ($actual -join ';') -ceq ($expected -join ';')
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
                'direct-trusted-count-scan-scatter-wave-ops',
                'radix-low-bit-wave-ops',
                'radix-low-bit-wave-ops',
                'direct-trusted-count-scan-scatter-wave-ops')
        }
        else {
            @(
                'radix-low-bit-wave-ops',
                'direct-trusted-count-scan-scatter-wave-ops',
                'direct-trusted-count-scan-scatter-wave-ops',
                'radix-low-bit-wave-ops')
        }
        for ($position = 1; $position -le 4; $position++) {
            $within = (($position - 1) % 2) + 1
            $first = $variants[[int][Math]::Floor(($position - 1) / 2) * 2]
            $order = if ($first -eq
                'direct-trusted-count-scan-scatter-wave-ops') {
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

function Expected-ScheduleContract {
    param([Parameter(Mandatory = $true)][int]$SuperRounds)
    $parts = [System.Collections.Generic.List[string]]::new()
    $parts.Add('control-pre')
    for ($round = 1; $round -le $SuperRounds; $round++) {
        $parts.Add($(if (($round -band 1) -ne 0) { 'ABBA' } else { 'BAAB' }))
    }
    $parts.Add('control-post')
    return $parts -join ';'
}

$runnerPath = Require-File (Join-Path $root 'runner-config.json')
$runner = Get-Content -LiteralPath $runnerPath -Raw | ConvertFrom-Json
$runnerSchemaVersion = [int]$runner.schemaVersion
$expectedBenchmarkSchemaVersion =
    if ($runnerSchemaVersion -eq 9) { 2 } else { 3 }
if ($runnerSchemaVersion -notin @(9, 10) -or
    [string]$runner.suite -cne 'summit.gpu-adaptive-binning' -or
    [int]$runner.benchmarkSchemaVersion -ne
        $expectedBenchmarkSchemaVersion) {
    throw 'Runner schema/suite contract does not match adaptive-binning.'
}
if (-not [bool]$runner.runnerConfigFinalized) {
    throw 'runner-config.json is not finalized.'
}
$formal = [bool]$runner.formalAcceptanceMode
$contentionFormal =
    $formal -and [string]$runner.matrixPreset -ceq
        'formal-amd-r9700-contention-v1'
$nvidiaSurfaceFormal =
    $formal -and [string]$runner.matrixPreset -ceq
        'formal-nvidia-rtx4090-surface-v1'
$selectorFormal = $contentionFormal -or $nvidiaSurfaceFormal
$legacyFormal =
    $formal -and [string]$runner.matrixPreset -ceq
        'formal-amd-r9700-v1'
$expectedFormalDevice = if ($nvidiaSurfaceFormal) {
    [pscustomobject]@{
        vendorId = 0x10DE
        deviceId = 0x2684
        deviceType = 'Direct3D12'
        deviceName = 'NVIDIA GeForce RTX 4090'
    }
}
else {
    [pscustomobject]@{
        vendorId = 0x1002
        deviceId = 0x7551
        deviceType = 'Direct3D12'
        deviceName = 'AMD Radeon AI PRO R9700'
    }
}
if ($formal) {
    foreach ($gate in @(
        'formalContractSatisfied',
        'editModeEvidenceBoundToSource',
        'sourceHashesStableAcrossEditMode',
        'sourceHashesStableAcrossBuild',
        'playerPayloadStableThroughRun')) {
        if (-not [bool]$runner.$gate) {
            throw "Formal runner gate '$gate' is false."
        }
    }
    if ([bool]$runner.gitTreeDirty -or
        [bool]$runner.gitStart.dirty -or
        [bool]$runner.gitPostEditModeAfterRestore.dirty -or
        [bool]$runner.gitFinal.dirty) {
        throw 'Formal provenance reports a dirty benchmark worktree.'
    }
    if ([string]$runner.gitCommit -notmatch '^[0-9a-fA-F]{40}$' -or
        [string]$runner.gitBranch -eq 'HEAD' -or
        [string]$runner.sourceSnapshotSha256 -notmatch
            '^[0-9A-F]{64}$') {
        throw 'Formal provenance lacks a named branch/full Git or source hash.'
    }
    $runnerProjectRoot =
        [System.IO.Path]::GetFullPath([string]$runner.projectRoot).
            TrimEnd('\', '/')
    $runnerOutputRoot =
        [System.IO.Path]::GetFullPath([string]$runner.outputDirectory).
            TrimEnd('\', '/')
    if ($runnerOutputRoot -ine $root.TrimEnd('\', '/') -or
        $runnerOutputRoot -ieq $runnerProjectRoot -or
        $runnerOutputRoot.StartsWith(
            $runnerProjectRoot +
                [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw (
            'Formal report root is inconsistent or resides inside the ' +
            'benchmark Git worktree.')
    }
    foreach ($snapshotField in @(
        'gitStart',
        'gitPostEditModeBeforeRestore',
        'gitPostEditModeAfterRestore',
        'gitPostBuildBeforeRestore',
        'gitPostBuildAfterRestore',
        'gitFinalBeforeRestore',
        'gitFinal')) {
        $snapshot = $runner.$snapshotField
        if ($null -eq $snapshot -or
            [string]$snapshot.head -cne [string]$runner.gitCommit -or
            [string]$snapshot.branch -cne [string]$runner.gitBranch) {
            throw "Formal Git snapshot '$snapshotField' is not bound to HEAD."
        }
    }
    foreach ($line in @(
        $runner.gitPostEditModeBeforeRestore.statusLines)) {
        if ([string]$line -cne
                ' D Packages/com.firstgeargames.fishnet/CodeGenerating/cecil-0.11.4/Mono.Cecil.sln.meta' -and
            [string]$line -cne
                ' M Assets/Settings/UniversalRenderPipelineGlobalSettings.asset') {
            throw (
                'Formal post-EditMode snapshot contains non-allowlisted ' +
                "Git drift: '$line'.")
        }
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
    $expectedGeneratedEditModePath =
        [System.IO.Path]::GetFullPath(
            (Join-Path $root 'editmode-results.xml'))
    $expectedEditModeLogPath =
        [System.IO.Path]::GetFullPath(
            (Join-Path $root 'unity-editmode.log'))
    if (-not [bool]$runner.editModeResults.executedByRunner -or
        [string]$runner.editModeResults.evidenceOrigin -cne
            'runner-generated-unity-editmode' -or
        [string]$runner.editModeResults.gitCommit -cne
            [string]$runner.gitCommit -or
        [string]$runner.editModeResults.sourceSnapshotSha256 -cne
            [string]$runner.sourceSnapshotSha256 -or
        [string]$runner.editModeResults.projectRoot -ine
            [string]$runner.projectRoot -or
        [string]$runner.editModeResults.unityEditorPath -ine
            [string]$runner.unityPath -or
        [string]$runner.editModeResults.unityEditorResolvedVersion -cne
            '6000.5.2f1' -or
        [string]$runner.editModeResults.testPlatform -cne 'EditMode' -or
        [string]$runner.editModeResults.graphicsApi -cne 'Direct3D12' -or
        [int]$runner.editModeResults.deviceIndex -ne 0 -or
        [int]$runner.editModeResults.timeoutMinutes -ne 30 -or
        [int]$runner.editModeResults.exitCode -ne 0 -or
        [string]$runner.editModeResults.sourcePath -ine
            $expectedGeneratedEditModePath -or
        [string]$runner.editModeResults.copiedPath -ine
            $expectedGeneratedEditModePath -or
        [string]$runner.editModeResults.logPath -ine
            $expectedEditModeLogPath) {
        throw (
            'Formal EditMode evidence was not generated by this runner ' +
            'for the bound HEAD/source snapshot.')
    }
    [DateTime]$editModeStartedUtc = [DateTime]::Parse(
        [string]$runner.editModeResults.startedUtc,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind)
    [DateTime]$editModeEndedUtc = [DateTime]::Parse(
        [string]$runner.editModeResults.endedUtc,
        [System.Globalization.CultureInfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::RoundtripKind)
    if ($editModeEndedUtc -lt $editModeStartedUtc) {
        throw 'Formal EditMode evidence timestamps are reversed.'
    }
    $resolvedEditModeLogPath = Require-File $expectedEditModeLogPath
    $editModeLog = Get-Item -LiteralPath $resolvedEditModeLogPath
    if ($editModeLog.Length -le 0 -or
        (Get-FileHash -LiteralPath $resolvedEditModeLogPath -Algorithm SHA256).Hash -cne
            [string]$runner.editModeResults.logSha256 -or
        $editModeLog.LastWriteTimeUtc.ToString('o') -cne
            [string]$runner.editModeResults.logLastWriteUtc) {
        throw 'Formal runner-generated Unity EditMode log is inconsistent.'
    }
    if (@($runner.windowsVideoControllers).Count -eq 0) {
        throw 'Formal provenance lacks Windows video-controller inventory.'
    }
    $expectedFormalNumbers = [ordered]@{
        deviceIndex = 0
        superRounds = 4
        warmupFrames = 60
        sampleFrames = 900
        cooldownFrames = 15
        dispatchesPerFrame = 1
        editModeTimeoutMinutes = 30
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
    if (-not ($selectorFormal -or $legacyFormal) -or
        [string]$runner.matrixRole -cne 'holdout') {
        throw (
            "Formal runner matrix is '$($runner.matrixPreset)'/" +
            "'$($runner.matrixRole)'; expected a registered holdout matrix.")
    }
    $expectedFormalStrings = [ordered]@{
        primitiveBackend = 'wave-ops'
        keyDomain = 'guaranteed-in-range'
        orderingContract = 'unspecified-within-bin'
        signedImprovementConvention = 'positive-radix-faster'
    }
    foreach ($field in $expectedFormalStrings.Keys) {
        if ([string]$runner.$field -cne
            [string]$expectedFormalStrings[$field]) {
            throw (
                "Formal runner field '$field' is '$($runner.$field)'; " +
                "expected '$($expectedFormalStrings[$field])'.")
        }
    }
    $contractDevice = $runner.formalContract.activeDevice
    if (-not (Test-FormalActiveDevice `
            -GraphicsDeviceVendorId (
                [int]$contractDevice.graphicsDeviceVendorId) `
            -GraphicsDeviceId ([int]$contractDevice.graphicsDeviceId) `
            -GraphicsDeviceType (
                [string]$contractDevice.graphicsDeviceType) `
            -ExpectedGraphicsDeviceVendorId (
                [int]$expectedFormalDevice.vendorId) `
            -ExpectedGraphicsDeviceId (
                [int]$expectedFormalDevice.deviceId) `
            -ExpectedGraphicsDeviceType (
                [string]$expectedFormalDevice.deviceType)) -or
        [string]$contractDevice.graphicsDeviceName -cne
            [string]$expectedFormalDevice.deviceName) {
        throw (
            'Formal active-device contract differs from the frozen ' +
            "identity for '$($runner.matrixPreset)'.")
    }
    if ($selectorFormal) {
        if ([string]$runner.formalContract.selectorPolicyClaimKind -cne
                'classification-replay-not-recordadaptive-timing') {
            throw (
                'Formal selector-policy claim must be classification ' +
                'replay, not measured RecordAdaptive timing.')
        }
        $requiredMarkersProperty =
            $runner.formalContract.PSObject.Properties[
                'requiredInnerProfilerMarkersEnabled']
        $requiredMarkersValue = if (
            $null -eq $requiredMarkersProperty) {
            $null
        }
        else {
            $requiredMarkersProperty.Value
        }
        if (-not (Test-RequiredProfilerMarkersDisabled `
                -FieldPresent ($null -ne $requiredMarkersProperty) `
                -Value $requiredMarkersValue)) {
            throw 'Formal contention contract requires inner profiler markers off.'
        }
    }
    if ($contentionFormal) {
        $bracket = $runner.formalContract.crossoverBracket
        $orderedCardinalities = @(
            $bracket.orderedDominantSetCardinalities |
                ForEach-Object { [int]$_ })
        $candidatePairs = @(
            $bracket.candidatePairs |
                ForEach-Object { [string]$_ })
        if ([string]$bracket.axis -cne 'dominant-set-cardinality' -or
            [int]$bracket.fixedElementCount -ne 1048576 -or
            [int]$bracket.fixedBinCount -ne 16 -or
            ($orderedCardinalities -join ',') -cne '1,4,16' -or
            ($candidatePairs -join ',') -cne '1-4,1-16' -or
            [int]$bracket.commonBracketSeed -ne 20261002 -or
            (Number $bracket.hotset4ColdTailFraction `
                'hotset4 cold-tail fraction') -ne 0.125 -or
            -not [bool]$bracket.requireSignChangingAcceptedWinner -or
            [string]$bracket.requiredLowerWinner -cne 'radix' -or
            [string]$bracket.requiredUpperWinner -cne 'direct' -or
            [int]$bracket.requiredValidatedPairCount -ne 2 -or
            -not [bool]$bracket.requireAllCandidatePairsValidated -or
            [int]$bracket.requiredPredictionMatchCount -ne 5) {
            throw (
                'Formal crossover-bracket contract differs from the ' +
                'frozen Radix-at-1 to Direct-at-4/16 v1 contract.')
        }
        if (-not (Test-ExactCellSelectorPolicyContract `
                -PolicyLabel ([string]$bracket.selectorPolicy) `
                -PolicyPredicate (
                    [string]$bracket.selectorPolicyPredicate) `
                -ExactRadixCandidateCells @(
                    $bracket.exactRadixCandidateCells))) {
            throw (
                'Formal selector policy must be the frozen two-cell ' +
                'full-tuple classifier: N262144/C16/singlebin/key9 and ' +
                'N1048576/C16/singlebin/key10.')
        }
        $tail = $runner.formalContract.selectorTailGuard
        if ([int]$tail.expectedPairCount -ne 8 -or
            [int]$tail.minimumWinningP99Pairs -ne 7 -or
            (Number $tail.minimumWorstPairP99ImprovementPercent `
                'selector tail worst-pair threshold') -ne -10.0 -or
            [int]$tail.requiredValidatedCellCount -ne 5 -or
            -not [bool]$tail.requireAcceptedWinner) {
            throw (
                'Formal selector tail guard must require at least 7/8 ' +
                'winner-aligned P99 pairs and worst-pair P99 improvement ' +
                'of at least -10% in all five policy cells.')
        }
    }
    if ($nvidiaSurfaceFormal) {
        $surface = $runner.formalContract.selectorSurface
        if ([string]$surface.axis -cne 'exact-workload-cell' -or
            [int]$surface.requiredPredictionMatchCount -ne 5 -or
            [int]$surface.requiredRadixAcceptedCells -ne 3 -or
            [int]$surface.requiredDirectAcceptedCells -ne 2 -or
            -not (Test-CalibratedSurfaceSelectorPolicyContract `
                -PolicyLabel ([string]$surface.selectorPolicy) `
                -PolicyPredicate (
                    [string]$surface.selectorPolicyPredicate) `
                -RadixCandidateCells @($surface.radixCandidateCells))) {
            throw (
                'Formal NVIDIA selector surface differs from the frozen ' +
                'three-Radix-cell, Direct-fallback v1 contract.')
        }
        $tail = $runner.formalContract.selectorTailGuard
        if ([int]$tail.expectedPairCount -ne 8 -or
            [int]$tail.minimumWinningP99Pairs -ne 7 -or
            (Number $tail.minimumWorstPairP99ImprovementPercent `
                'selector tail worst-pair threshold') -ne -10.0 -or
            [int]$tail.requiredValidatedCellCount -ne 5 -or
            -not [bool]$tail.requireAcceptedWinner) {
            throw (
                'Formal NVIDIA selector tail guard differs from the ' +
                'frozen five-cell v1 contract.')
        }
    }
    $gate = $runner.decisiveGate
    if ((Number $gate.minimumMedianImprovementPercent 'gate percent') -ne 5.0 -or
        (Number $gate.minimumMedianAbsoluteReductionMs 'gate absolute') -ne 0.005 -or
        [int]$gate.minimumWinningPairs -ne 6 -or
        [int]$gate.expectedPairs -ne 8 -or
        -not [bool]$gate.requireAbBaSameSign -or
        (Number $gate.minimumMedianP99ImprovementPercent 'gate p99') -ne -2.0 -or
        (Number $gate.maximumEmptyScopeP99Ms 'gate control') -ne 0.005 -or
        [int]$gate.minimumAcceptedCellsPerBackendForCrossover -ne 2) {
        throw 'Formal runner decisive gate differs from the frozen v1 gate.'
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
    $expectedFormalScenarioCount =
        if ($selectorFormal) { 5 } else { 8 }
    if (@($runner.scenarios).Count -ne $expectedFormalScenarioCount -or
        @($runner.playerRuns).Count -ne $expectedFormalScenarioCount) {
        throw (
            "Formal runner requires exactly $expectedFormalScenarioCount " +
            'scenarios/Player runs for the selected preset.')
    }
    $expectedEditModeIdentities = @(
        'Summit.GpuDirectBinning.Tests.Editor.dll',
        'Summit.GpuDirectBinning.Tests.CpuDirectBinningOracleTests',
        'Summit.GpuDirectBinning.Tests.GpuDirectSpatialBinnerContractTests',
        'Summit.GpuDirectBinning.Tests.GpuDirectSpatialBinnerIntegrationTests',
        'Summit.GpuPrimitives.Tests.Editor.dll',
        'Summit.GpuPrimitives.Tests.CpuPrimitiveOracleTests',
        'Summit.GpuPrimitives.Tests.GpuPrimitivesIntegrationTests',
        'Summit.GpuPrimitives.Tests.GpuRadixKeyBitBoundaryTests',
        'Summit.GpuAdaptiveBinning.Tests.Editor.dll',
        'Summit.GpuAdaptiveBinning.Tests.GpuAdaptiveSpatialBinnerContractTests',
        'Summit.GpuAdaptiveBinning.Tests.GpuRadixSpatialBinnerIntegrationTests',
        'Summit.GpuAdaptiveBinning.Benchmark.Tests.Editor.dll',
        'Summit.GpuAdaptiveBinning.Benchmark.Tests.GpuAdaptiveBinningBenchmarkScheduleTests',
        'Summit.GpuAdaptiveBinning.Benchmark.Tests.GpuAdaptiveBinningInputDistributionTests',
        'Summit.GpuAdaptiveBinning.Benchmark.Tests.GpuAdaptiveBinningCpuOracleTests'
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
    $copiedEditModeFile = Get-Item -LiteralPath $copiedEditModePath
    $copiedEditModeSha =
        (Get-FileHash -LiteralPath $copiedEditModePath -Algorithm SHA256).Hash
    if ($copiedEditModeSha -cne
            [string]$runner.editModeResults.copiedSha256 -or
        $copiedEditModeFile.LastWriteTimeUtc.ToString('o') -cne
            [string]$runner.editModeResults.copiedLastWriteUtc -or
        [string]$runner.editModeResults.sourceLastWriteUtc -cne
            [string]$runner.editModeResults.copiedLastWriteUtc) {
        throw 'Formal copied EditMode XML file evidence is inconsistent.'
    }
    [xml]$editModeXml =
        Get-Content -LiteralPath $copiedEditModePath -Raw
    $xmlRun = $editModeXml.'test-run'
    if ($null -eq $xmlRun -or
        [string]$xmlRun.result -cne 'Passed' -or
        [int]$xmlRun.total -le 0 -or
        [int]$xmlRun.passed -ne [int]$xmlRun.total -or
        [int]$xmlRun.failed -ne 0 -or
        [int]$xmlRun.skipped -ne 0 -or
        [int]$xmlRun.inconclusive -ne 0 -or
        [int]$xmlRun.total -ne [int]$runner.editModeResults.total -or
        [int]$xmlRun.passed -ne [int]$runner.editModeResults.passed) {
        throw 'Formal EditMode XML itself is not a fully passed NUnit run.'
    }
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
foreach ($matrixRow in $matrix) {
    if ($null -eq
            $matrixRow.PSObject.Properties['exactSingleBinKey'] -or
        -not (Test-ExactSingleBinKeyContract `
            -Distribution ([string]$matrixRow.distribution) `
            -Seed ([int]$matrixRow.seed) `
            -BinCount ([int]$matrixRow.binCount) `
            -ExactSingleBinKey $matrixRow.exactSingleBinKey)) {
        throw (
            "Matrix scenario '$($matrixRow.scenarioId)' has a missing " +
            'or generator-v3-inconsistent exactSingleBinKey.')
    }
}
if ($formal) {
    $frozen = if ($contentionFormal) {
        @(
            'singlebin-n262144-c16|262144|16|singlebin|20261001|9|1|',
            'singlebin-n1048576-c16|1048576|16|singlebin|20261002|10|1|n1048576-c16-contention',
            'hotset4-n1048576-c16|1048576|16|hotset4|20261002|-1|4|n1048576-c16-contention',
            'uniform-n1048576-c16|1048576|16|uniform|20261002|-1|16|n1048576-c16-contention',
            'uniform-n1048576-c65536|1048576|65536|uniform|20261005|-1|65536|')
    }
    elseif ($nvidiaSurfaceFormal) {
        @(
            'hold-singlebin-n1048576-c16|1048576|16|singlebin|20261221|5|1|n1048576-c16-contention',
            'hold-hotset4-n1048576-c16|1048576|16|hotset4|20261222|-1|4|n1048576-c16-contention',
            'hold-uniform-n1048576-c16|1048576|16|uniform|20261223|-1|16|n1048576-c16-contention',
            'hold-uniform-n1048576-c4096|1048576|4096|uniform|20261224|-1|4096|',
            'hold-uniform-n1048576-c65536|1048576|65536|uniform|20261225|-1|65536|')
    }
    else {
        @(
            'uniform-n1048576-c64|1048576|64|uniform|20260901|-1|64|',
            'hotset16-n1048576-c64|1048576|64|hotset16|20260902|-1|16|',
            'uniform-n1048576-c256|1048576|256|uniform|20260903|-1|256|',
            'hotset16-n1048576-c256|1048576|256|hotset16|20260904|-1|16|',
            'uniform-n1048576-c4096|1048576|4096|uniform|20260905|-1|4096|',
            'hotset16-n1048576-c4096|1048576|4096|hotset16|20260906|-1|16|',
            'uniform-n1048576-c65536|1048576|65536|uniform|20260907|-1|65536|',
            'hotset16-n1048576-c65536|1048576|65536|hotset16|20260908|-1|16|')
    }
    $actual = @(
        $matrix | ForEach-Object {
            "$($_.scenarioId)|$($_.elementCount)|$($_.binCount)|" +
            "$($_.distribution)|$($_.seed)|$($_.exactSingleBinKey)|" +
            "$($_.dominantSetCardinality)|$($_.bracketGroup)"
        })
    if (($actual -join ';') -cne ($frozen -join ';')) {
        throw (
            "Formal holdout matrix '$($runner.matrixPreset)' differs " +
            'from its frozen matrix.')
    }
}

$rootSummaries = [System.Collections.Generic.List[object]]::new()
$allExploratoryUsable = $true
$radixAcceptedCellCount = 0
$directAcceptedCellCount = 0
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
    if ($null -eq
            $runnerScenario.PSObject.Properties['exactSingleBinKey'] -or
        -not (Test-ExactSingleBinKeyContract `
            -Distribution ([string]$runnerScenario.distribution) `
            -Seed ([int]$runnerScenario.seed) `
            -BinCount ([int]$runnerScenario.binCount) `
            -ExactSingleBinKey $runnerScenario.exactSingleBinKey)) {
        throw (
            "Scenario '$scenarioId' runner workload has a missing or " +
            'generator-v3-inconsistent exactSingleBinKey.')
    }
    if ([int]$runnerScenario.elementCount -ne [int]$matrixRow.elementCount -or
        [int]$runnerScenario.binCount -ne [int]$matrixRow.binCount -or
        [string]$runnerScenario.distribution -cne
            [string]$matrixRow.distribution -or
        [int]$runnerScenario.seed -ne [int]$matrixRow.seed -or
        [int]$runnerScenario.exactSingleBinKey -ne
            [int]$matrixRow.exactSingleBinKey -or
        [int]$runnerScenario.dominantSetCardinality -ne
            [int]$matrixRow.dominantSetCardinality -or
        [string]$runnerScenario.bracketGroup -cne
            [string]$matrixRow.bracketGroup) {
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

    $expectedScenarioSchemaVersion =
        if ($runnerSchemaVersion -eq 9) { 2 } else { 3 }
    if ([int]$config.schemaVersion -ne $expectedScenarioSchemaVersion -or
        [string]$config.suite -cne 'summit.gpu-adaptive-binning' -or
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
    if ($null -eq $config.PSObject.Properties['exactSingleBinKey'] -or
        -not (Test-ExactSingleBinKeyContract `
            -Distribution ([string]$config.distribution) `
            -Seed ([int]$config.seed) `
            -BinCount ([int]$config.binCount) `
            -ExactSingleBinKey $config.exactSingleBinKey) -or
        [int]$config.exactSingleBinKey -ne
            [int]$matrixRow.exactSingleBinKey) {
        throw (
            "Scenario '$scenarioId' config has a missing, mismatched, or " +
            'generator-v3-inconsistent exactSingleBinKey.')
    }
    if ($selectorFormal) {
        $configMarkersProperty =
            $config.PSObject.Properties['innerProfilerMarkersEnabled']
        $configMarkersValue = if ($null -eq $configMarkersProperty) {
            $null
        }
        else {
            $configMarkersProperty.Value
        }
        if (-not (Test-RequiredProfilerMarkersDisabled `
                -FieldPresent ($null -ne $configMarkersProperty) `
                -Value $configMarkersValue)) {
            throw (
                "Formal contention scenario '$scenarioId' requires " +
                'config.innerProfilerMarkersEnabled=false.')
        }
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
    $expectedScheduleContract =
        Expected-ScheduleContract -SuperRounds ([int]$config.superRounds)
    if ([string]$config.primitiveBackend -cne 'wave-ops' -or
        [string]$config.keyDomain -cne 'guaranteed-in-range' -or
        [string]$config.orderingContract -cne 'unspecified-within-bin' -or
        [string]$config.inputGeneratorContract -cne
            'gpu-adaptive-binning-input-v3' -or
        [string]$config.scheduleContract -cne $expectedScheduleContract -or
        [string]$config.buildCommit -cne [string]$runner.gitCommit -or
        [string]$config.directShaderSha256 -cne
            [string]$runner.directShaderSha256 -or
        [string]$config.radixShaderSha256 -cne
            [string]$runner.radixShaderSha256 -or
        [string]$config.runtimeApiSha256 -cne
            [string]$runner.runtimeApiSha256) {
        throw "Scenario '$scenarioId' config provenance is inconsistent."
    }
    if (-not [bool]$config.requireCompleteGpuTimings -or
        -not [bool]$config.caseLocalWarmup -or
        -not [bool]$config.sameProcessPaired -or
        -not [bool]$config.informationalPlayerLogsSuppressed -or
        [string]$config.directId -cne
            'spatial-binning/direct-trusted-count-scan-scatter-wave-ops' -or
        [string]$config.radixId -cne
            'spatial-binning/radix-low-bit-wave-ops' -or
        [string]$config.expectedResultHash -notmatch
            '^summit\.gpu-adaptive-binning\.canonical-csr\.sha256\.v1:[0-9A-F]{64}$') {
        throw (
            "Scenario '$scenarioId' benchmark comparison contract is " +
            'inconsistent.')
    }
    $highestKey = [uint32]([int]$config.binCount - 1)
    $expectedKeyBitCount = 1
    while (($highestKey = $highestKey -shr 1) -ne 0) {
        $expectedKeyBitCount++
    }
    $expectedPassCount = [int][Math]::Ceiling($expectedKeyBitCount / 4.0)
    if ([int]$config.radixKeyBitCount -ne $expectedKeyBitCount -or
        [int]$config.radixPassCount -ne $expectedPassCount) {
        throw "Scenario '$scenarioId' radix pass metadata is invalid."
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
    $formalDeviceMatches = Test-FormalActiveDevice `
        -GraphicsDeviceVendorId ([int]$device.graphicsDeviceVendorId) `
        -GraphicsDeviceId ([int]$device.graphicsDeviceId) `
        -GraphicsDeviceType ([string]$device.graphicsDeviceType) `
        -ExpectedGraphicsDeviceVendorId (
            [int]$expectedFormalDevice.vendorId) `
        -ExpectedGraphicsDeviceId (
            [int]$expectedFormalDevice.deviceId) `
        -ExpectedGraphicsDeviceType (
            [string]$expectedFormalDevice.deviceType)
    if ($formal -and (
            -not $formalDeviceMatches -or
            [string]$device.graphicsDeviceName -cne
                [string]$expectedFormalDevice.deviceName)) {
        throw (
            "Formal scenario '$scenarioId' requires the frozen device " +
            "for '$($runner.matrixPreset)'; observed " +
            "'$($device.graphicsDeviceName)' vendorId=" +
            "'$($device.graphicsDeviceVendorId)' deviceId=" +
            "'$($device.graphicsDeviceId)' type=" +
            "'$($device.graphicsDeviceType)'.")
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
    [int64]$expectedDirectInternalScratchBytes =
        4L * [int64]$config.binCount
    [int64]$expectedRadixInternalScratchBytes =
        4L * [int64]$config.elementCount
    if ([int64]$config.sharedInputBytes -lt 0 -or
        [int64]$config.sharedOutputBytes -lt 0 -or
        [int64]$config.directPrimitiveScratchBytes -lt 0 -or
        [int64]$config.radixPrimitiveScratchBytes -lt 0 -or
        [int64]$config.directInternalScratchBytes -ne
            $expectedDirectInternalScratchBytes -or
        [int64]$config.radixInternalScratchBytes -ne
            $expectedRadixInternalScratchBytes) {
        throw (
            "Scenario '$scenarioId' scratch-byte accounting is invalid.")
    }
    [int64]$directCaseScratchBytes =
        [int64]$config.directPrimitiveScratchBytes +
        [int64]$config.directInternalScratchBytes
    [int64]$radixCaseScratchBytes =
        [int64]$config.radixPrimitiveScratchBytes +
        [int64]$config.radixInternalScratchBytes
    [int64]$directCaseResidentBytes =
        [int64]$config.sharedInputBytes +
        [int64]$config.sharedOutputBytes +
        $directCaseScratchBytes
    [int64]$radixCaseResidentBytes =
        [int64]$config.sharedInputBytes +
        [int64]$config.sharedOutputBytes +
        $radixCaseScratchBytes
    if ([int64]$config.directCaseScratchBytes -ne
            $directCaseScratchBytes -or
        [int64]$config.radixCaseScratchBytes -ne
            $radixCaseScratchBytes -or
        [int64]$config.directCaseResidentBytes -ne
            $directCaseResidentBytes -or
        [int64]$config.radixCaseResidentBytes -ne
            $radixCaseResidentBytes) {
        throw (
            "Scenario '$scenarioId' case-resident accounting is invalid.")
    }

    $primitiveOwnershipProperty =
        $config.PSObject.Properties['primitiveScratchOwnership']
    if ($expectedScenarioSchemaVersion -eq 3 -and
        $null -eq $primitiveOwnershipProperty) {
        throw (
            "Scenario '$scenarioId' lacks primitive scratch ownership.")
    }
    [string]$primitiveScratchOwnership =
        if ($null -eq $primitiveOwnershipProperty) {
            'independent-per-forced-backend-v0'
        }
        else {
            [string]$primitiveOwnershipProperty.Value
        }
    [int64]$expectedUnionPrimitiveScratchBytes =
        if ($primitiveScratchOwnership -ceq
            'shared-across-forced-backends-v1') {
            if ([int64]$config.directPrimitiveScratchBytes -ne
                [int64]$config.radixPrimitiveScratchBytes) {
                throw (
                    "Scenario '$scenarioId' shared primitive scratch " +
                    'must be identical for both forced backends.')
            }
            [int64]$config.directPrimitiveScratchBytes
        }
        elseif ($primitiveScratchOwnership -ceq
            'independent-per-forced-backend-v0') {
            [int64]$config.directPrimitiveScratchBytes +
                [int64]$config.radixPrimitiveScratchBytes
        }
        else {
            throw (
                "Scenario '$scenarioId' has unknown primitive scratch " +
                "ownership '$primitiveScratchOwnership'.")
        }
    if ([int64]$config.unionPrimitiveScratchBytes -ne
        $expectedUnionPrimitiveScratchBytes) {
        throw (
            "Scenario '$scenarioId' union primitive scratch accounting " +
            'is invalid.')
    }
    [int64]$expectedActualBenchmarkBufferResidentBytes =
        [int64]$config.sharedInputBytes +
        [int64]$config.sharedOutputBytes +
        [int64]$config.directInternalScratchBytes +
        [int64]$config.radixInternalScratchBytes +
        $expectedUnionPrimitiveScratchBytes
    if ([int64]$config.actualBenchmarkBufferResidentBytes -ne
        $expectedActualBenchmarkBufferResidentBytes) {
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
                'direct-trusted-count-scan-scatter-wave-ops') {
                $directCaseResidentBytes
            }
            elseif ($expected.variant -ceq 'radix-low-bit-wave-ops') {
                $radixCaseResidentBytes
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
            'direct-trusted-count-scan-scatter-wave-ops',
            'radix-low-bit-wave-ops')) {
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
        $direct = @(
            $pairRows | Where-Object {
                $_.variant -ceq 'direct-trusted-count-scan-scatter-wave-ops'
            })
        $radix = @(
            $pairRows | Where-Object {
                $_.variant -ceq 'radix-low-bit-wave-ops'
            })
        if ($direct.Count -ne 1 -or $radix.Count -ne 1) {
            throw "Scenario '$scenarioId' pair $pairId lacks one Direct and one Radix block."
        }
        $directAvg = Number $direct[0].gpuRegionAverageMs 'direct average'
        $radixAvg = Number $radix[0].gpuRegionAverageMs 'radix average'
        $directP99 = Number $direct[0].gpuRegionP99Ms 'direct p99'
        $radixP99 = Number $radix[0].gpuRegionP99Ms 'radix p99'
        $paired.Add([pscustomobject]@{
            scenarioId = $scenarioId
            pairIndex = $pairId
            pairOrder = $pairRows[0].pairOrder
            directGpuAverageMs = $directAvg
            radixGpuAverageMs = $radixAvg
            signedGpuAverageImprovementPercent =
                Improvement $directAvg $radixAvg
            signedGpuAverageAbsoluteReductionMs = $directAvg - $radixAvg
            directGpuP99Ms = $directP99
            radixGpuP99Ms = $radixP99
            signedGpuP99ImprovementPercent =
                Improvement $directP99 $radixP99
            radixGpuAverageImprovementPercent =
                Improvement $directAvg $radixAvg
            radixGpuAverageAbsoluteReductionMs = $directAvg - $radixAvg
            radixGpuP99ImprovementPercent =
                Improvement $directP99 $radixP99
            directGpuAverageImprovementPercent =
                Improvement $radixAvg $directAvg
            directGpuAverageAbsoluteReductionMs = $radixAvg - $directAvg
            directGpuP99ImprovementPercent =
                Improvement $radixP99 $directP99
        })
    }
    $pairedPath = Join-Path $scenarioRoot 'paired-deltas.csv'
    $paired |
        Export-Csv -LiteralPath $pairedPath -NoTypeInformation -Encoding utf8

    $signedAverageImprovements = [double[]]@(
        $paired | ForEach-Object {
            [double]$_.signedGpuAverageImprovementPercent
        })
    $signedAbsoluteReductions = [double[]]@(
        $paired | ForEach-Object {
            [double]$_.signedGpuAverageAbsoluteReductionMs
        })
    $signedP99Improvements = [double[]]@(
        $paired | ForEach-Object {
            [double]$_.signedGpuP99ImprovementPercent
        })
    $radixAverageImprovements = [double[]]@(
        $paired | ForEach-Object {
            [double]$_.radixGpuAverageImprovementPercent
        })
    $radixAbsoluteReductions = [double[]]@(
        $paired | ForEach-Object {
            [double]$_.radixGpuAverageAbsoluteReductionMs
        })
    $radixP99Improvements = [double[]]@(
        $paired | ForEach-Object {
            [double]$_.radixGpuP99ImprovementPercent
        })
    $directAverageImprovements = [double[]]@(
        $paired | ForEach-Object {
            [double]$_.directGpuAverageImprovementPercent
        })
    $directAbsoluteReductions = [double[]]@(
        $paired | ForEach-Object {
            [double]$_.directGpuAverageAbsoluteReductionMs
        })
    $directP99Improvements = [double[]]@(
        $paired | ForEach-Object {
            [double]$_.directGpuP99ImprovementPercent
        })

    $signedAverageMedian = Median $signedAverageImprovements
    $signedAbsoluteMedian = Median $signedAbsoluteReductions
    $signedP99Median = Median $signedP99Improvements
    $radixAverageMedian = Median $radixAverageImprovements
    $radixAbsoluteMedian = Median $radixAbsoluteReductions
    $radixP99Median = Median $radixP99Improvements
    $directAverageMedian = Median $directAverageImprovements
    $directAbsoluteMedian = Median $directAbsoluteReductions
    $directP99Median = Median $directP99Improvements
    $radixWinningPairs =
        @($signedAverageImprovements | Where-Object { $_ -gt 0.0 }).Count
    $directWinningPairs =
        @($signedAverageImprovements | Where-Object { $_ -lt 0.0 }).Count
    $radixP99WinningPairs =
        @($radixP99Improvements | Where-Object { $_ -gt 0.0 }).Count
    $directP99WinningPairs =
        @($directP99Improvements | Where-Object { $_ -gt 0.0 }).Count
    $radixWorstPairP99Improvement =
        [double](($radixP99Improvements | Measure-Object -Minimum).Minimum)
    $directWorstPairP99Improvement =
        [double](($directP99Improvements | Measure-Object -Minimum).Minimum)
    $abSignedImprovements = [double[]]@(
        $paired | Where-Object { $_.pairOrder -ceq 'AB' } |
            ForEach-Object {
                [double]$_.signedGpuAverageImprovementPercent
            })
    $baSignedImprovements = [double[]]@(
        $paired | Where-Object { $_.pairOrder -ceq 'BA' } |
            ForEach-Object {
                [double]$_.signedGpuAverageImprovementPercent
            })
    $abSignedMedian = Median $abSignedImprovements
    $baSignedMedian = Median $baSignedImprovements
    $pairCountGate = $paired.Count -eq 8
    $orderCountGate =
        $abSignedImprovements.Count -eq 4 -and
        $baSignedImprovements.Count -eq 4
    $radixWinCountGate = $radixWinningPairs -ge 6
    $radixPercentGate = $radixAverageMedian -ge 5.0
    $radixAbsoluteGate = $radixAbsoluteMedian -ge 0.005
    $radixP99Gate = $radixP99Median -ge -2.0
    $radixOrderBalanceGate =
        $orderCountGate -and $abSignedMedian -gt 0.0 -and
        $baSignedMedian -gt 0.0
    $directWinCountGate = $directWinningPairs -ge 6
    $directPercentGate = $directAverageMedian -ge 5.0
    $directAbsoluteGate = $directAbsoluteMedian -ge 0.005
    $directP99Gate = $directP99Median -ge -2.0
    $directOrderBalanceGate =
        $orderCountGate -and $abSignedMedian -lt 0.0 -and
        $baSignedMedian -lt 0.0
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
    $radixAccepted =
        $exploratoryUsable -and
        $radixWinCountGate -and
        $radixPercentGate -and
        $radixAbsoluteGate -and
        $radixP99Gate -and
        $radixOrderBalanceGate
    $directAccepted =
        $exploratoryUsable -and
        $directWinCountGate -and
        $directPercentGate -and
        $directAbsoluteGate -and
        $directP99Gate -and
        $directOrderBalanceGate
    $winner = if ($radixAccepted) {
        'radix'
    }
    elseif ($directAccepted) {
        'direct'
    }
    else {
        'none'
    }
    $classification = if ($radixAccepted) {
        'radix-accepted'
    }
    elseif ($directAccepted) {
        'direct-accepted'
    }
    else {
        'neutral-or-inconclusive'
    }
    $selectorPolicyPrediction = if ($contentionFormal) {
        Get-ExactCellSelectorPrediction `
            -ElementCount ([int]$matrixRow.elementCount) `
            -BinCount ([int]$matrixRow.binCount) `
            -Distribution ([string]$matrixRow.distribution) `
            -ExactSingleBinKey ([int]$matrixRow.exactSingleBinKey)
    }
    elseif ($nvidiaSurfaceFormal) {
        Get-CalibratedSurfaceSelectorPrediction `
            -ElementCount ([int]$matrixRow.elementCount) `
            -BinCount ([int]$matrixRow.binCount) `
            -Distribution ([string]$matrixRow.distribution) `
            -ExactSingleBinKey ([int]$matrixRow.exactSingleBinKey) `
            -RadixCandidateCells @(
                $runner.formalContract.selectorSurface.radixCandidateCells)
    }
    else {
        'not-applicable'
    }
    $selectorPolicyPredictionApplicable = $selectorFormal
    $selectorPolicyPredictionMatchesForcedWinner =
        $selectorPolicyPredictionApplicable -and
        [string]$winner -ceq [string]$selectorPolicyPrediction
    $winnerAccepted = $winner -in @('direct', 'radix')
    $winnerP99WinningPairCount = if ($winner -ceq 'radix') {
        $radixP99WinningPairs
    }
    elseif ($winner -ceq 'direct') {
        $directP99WinningPairs
    }
    else {
        0
    }
    $winnerWorstPairP99Improvement = if ($winner -ceq 'radix') {
        $radixWorstPairP99Improvement
    }
    elseif ($winner -ceq 'direct') {
        $directWorstPairP99Improvement
    }
    else {
        -999999.0
    }
    $selectorPolicyTailCellValidated =
        Test-SelectorTailCellValidated `
            -PredictionMatchesWinner (
                $selectorPolicyPredictionMatchesForcedWinner) `
            -WinnerAccepted $winnerAccepted `
            -WinnerP99WinningPairCount $winnerP99WinningPairCount `
            -WorstPairP99ImprovementPercent (
                $winnerWorstPairP99Improvement)
    $allExploratoryUsable = $allExploratoryUsable -and $exploratoryUsable
    if ($radixAccepted) { $radixAcceptedCellCount++ }
    if ($directAccepted) { $directAcceptedCellCount++ }

    $scenarioSelectorPolicyClaimKind = if ($selectorFormal) {
        'classification-replay-not-recordadaptive-timing'
    }
    else {
        'not-applicable'
    }
    $scenarioSummary = [pscustomobject]@{
        scenarioId = $scenarioId
        elementCount = [int]$matrixRow.elementCount
        binCount = [int]$matrixRow.binCount
        distribution = [string]$matrixRow.distribution
        seed = [int]$matrixRow.seed
        exactSingleBinKey = [int]$matrixRow.exactSingleBinKey
        dominantSetCardinality = [int]$matrixRow.dominantSetCardinality
        bracketGroup = [string]$matrixRow.bracketGroup
        processId = $processIds[0]
        pairedRows = $paired.Count
        exploratoryDataUsable = [int]$exploratoryUsable
        classification = $classification
        winner = $winner
        selectorPolicyClaimKind = $scenarioSelectorPolicyClaimKind
        selectorPolicy = if ($contentionFormal) {
            [string]$runner.formalContract.crossoverBracket.selectorPolicy
        }
        elseif ($nvidiaSurfaceFormal) {
            [string]$runner.formalContract.selectorSurface.selectorPolicy
        }
        else {
            'not-applicable'
        }
        selectorPolicyPrediction = $selectorPolicyPrediction
        selectorPolicyPredictionApplicable =
            [int]$selectorPolicyPredictionApplicable
        selectorPolicyPredictionMatchesForcedWinner =
            [int]$selectorPolicyPredictionMatchesForcedWinner
        winnerP99WinningPairCount = $winnerP99WinningPairCount
        winnerWorstPairP99ImprovementPercent =
            $winnerWorstPairP99Improvement
        selectorPolicyTailMinimumWinningPairs = 7
        selectorPolicyTailMinimumWorstPairP99ImprovementPercent = -10.0
        selectorPolicyTailCellValidated =
            [int]$selectorPolicyTailCellValidated
        signedMetricConvention = 'positive-means-radix-faster'
        signedGpuAverageImprovementMedianPercent = $signedAverageMedian
        signedGpuAverageAbsoluteReductionMedianMs = $signedAbsoluteMedian
        signedGpuP99ImprovementMedianPercent = $signedP99Median
        abSignedGpuAverageImprovementMedianPercent = $abSignedMedian
        baSignedGpuAverageImprovementMedianPercent = $baSignedMedian
        radixWinningPairCount = $radixWinningPairs
        radixWinCountGate = [int]$radixWinCountGate
        radixGpuAverageImprovementMedianPercent = $radixAverageMedian
        radixGpuAveragePercentGate = [int]$radixPercentGate
        radixGpuAverageAbsoluteReductionMedianMs = $radixAbsoluteMedian
        radixGpuAverageAbsoluteGate = [int]$radixAbsoluteGate
        radixGpuP99ImprovementMedianPercent = $radixP99Median
        radixGpuP99Gate = [int]$radixP99Gate
        radixOrderBalanceGate = [int]$radixOrderBalanceGate
        radixMeetsFrozenImprovementGate = [int]$radixAccepted
        directWinningPairCount = $directWinningPairs
        directWinCountGate = [int]$directWinCountGate
        directGpuAverageImprovementMedianPercent = $directAverageMedian
        directGpuAveragePercentGate = [int]$directPercentGate
        directGpuAverageAbsoluteReductionMedianMs = $directAbsoluteMedian
        directGpuAverageAbsoluteGate = [int]$directAbsoluteGate
        directGpuP99ImprovementMedianPercent = $directP99Median
        directGpuP99Gate = [int]$directP99Gate
        directOrderBalanceGate = [int]$directOrderBalanceGate
        directMeetsFrozenImprovementGate = [int]$directAccepted
        directCaseResidentBytes = $directCaseResidentBytes
        radixCaseResidentBytes = $radixCaseResidentBytes
        emptyScopeP99Ms = $controlP99
        emptyScopeGate = [int]$emptyScopeGate
        meetsFrozenImprovementGate = [int]($radixAccepted -or $directAccepted)
    }
    @($scenarioSummary) |
        Export-Csv -LiteralPath (
            Join-Path $scenarioRoot 'scenario-summary.csv') `
            -NoTypeInformation -Encoding utf8
    @(
        'GPU adaptive-binning scenario quality summary'
        "scenarioId=$scenarioId"
        "exactSingleBinKey=$([int]$matrixRow.exactSingleBinKey)"
        "classification=$classification"
        "winner=$winner"
        "selectorPolicyClaimKind=$scenarioSelectorPolicyClaimKind"
        "selectorPolicyPrediction=$selectorPolicyPrediction"
        "selectorPolicyPredictionMatchesForcedWinner=$([int]$selectorPolicyPredictionMatchesForcedWinner)"
        "winnerP99WinningPairCount=$winnerP99WinningPairCount"
        "winnerWorstPairP99ImprovementPercent=$winnerWorstPairP99Improvement"
        "selectorPolicyTailCellValidated=$([int]$selectorPolicyTailCellValidated)"
        'signedMetricConvention=positive-means-radix-faster'
        "exploratoryDataUsable=$([int]$exploratoryUsable)"
        "pairedRows=$($paired.Count)"
        'measurementReadbackBytes=0'
        "signedGpuAverageImprovementMedianPercent=$signedAverageMedian"
        "signedGpuP99ImprovementMedianPercent=$signedP99Median"
        "radixMeetsFrozenImprovementGate=$([int]$radixAccepted)"
        "directMeetsFrozenImprovementGate=$([int]$directAccepted)"
        "emptyScopeP99Ms=$controlP99"
    ) | Set-Content -LiteralPath (
        Join-Path $scenarioRoot 'quality-summary.txt') -Encoding utf8
    $rootSummaries.Add($scenarioSummary)
}

$rootSummaries |
    Export-Csv -LiteralPath (
        Join-Path $root 'matrix-summary.csv') -NoTypeInformation -Encoding utf8
$bracketEvidence = [System.Collections.Generic.List[object]]::new()
$bracketEvidencePath = ''
if ($contentionFormal) {
    $bracketCandidatePairs = @(
        [pscustomobject]@{ lower = 1; upper = 4 },
        [pscustomobject]@{ lower = 1; upper = 16 })
    $bracketCells = @(
        $rootSummaries | Where-Object {
            $_.bracketGroup -ceq 'n1048576-c16-contention' -and
            [int]$_.elementCount -eq 1048576 -and
            [int]$_.binCount -eq 16
        })
    foreach ($candidate in $bracketCandidatePairs) {
        $lowerCells = @(
            $bracketCells | Where-Object {
                [int]$_.dominantSetCardinality -eq [int]$candidate.lower
            })
        $upperCells = @(
            $bracketCells | Where-Object {
                [int]$_.dominantSetCardinality -eq [int]$candidate.upper
            })
        if ($lowerCells.Count -ne 1 -or $upperCells.Count -ne 1) {
            throw (
                'Formal contention bracket lacks exactly one cell for ' +
                "$($candidate.lower)-$($candidate.upper).")
        }
        $lower = $lowerCells[0]
        $upper = $upperCells[0]
        $selectorDirectionValidated =
            Test-SelectorDirectionValidated `
                -LowerWinner ([string]$lower.winner) `
                -UpperWinner ([string]$upper.winner)
        $signChangingAcceptedWinner =
            Test-SignChangingAcceptedWinner `
                -LowerWinner ([string]$lower.winner) `
                -UpperWinner ([string]$upper.winner)
        $bracketEvidence.Add([pscustomobject]@{
            bracketGroup = 'n1048576-c16-contention'
            axis = 'dominant-set-cardinality'
            fixedElementCount = 1048576
            fixedBinCount = 16
            lowerDominantSetCardinality = [int]$candidate.lower
            lowerScenarioId = $lower.scenarioId
            lowerDistribution = $lower.distribution
            lowerSeed = $lower.seed
            lowerExactSingleBinKey = $lower.exactSingleBinKey
            lowerWinner = $lower.winner
            lowerSignedGpuAverageImprovementMedianPercent =
                $lower.signedGpuAverageImprovementMedianPercent
            upperDominantSetCardinality = [int]$candidate.upper
            upperScenarioId = $upper.scenarioId
            upperDistribution = $upper.distribution
            upperSeed = $upper.seed
            upperExactSingleBinKey = $upper.exactSingleBinKey
            upperWinner = $upper.winner
            upperSignedGpuAverageImprovementMedianPercent =
                $upper.signedGpuAverageImprovementMedianPercent
            signChangingAcceptedWinner =
                [int]$signChangingAcceptedWinner
            selectorDirectionValidated =
                [int]$selectorDirectionValidated
        })
    }
    $bracketEvidencePath =
        Join-Path $root 'crossover-bracket-evidence.csv'
    $bracketEvidence |
        Export-Csv -LiteralPath $bracketEvidencePath `
            -NoTypeInformation -Encoding utf8
}
$bracketEvidenceComplete =
    $contentionFormal -and $bracketEvidence.Count -eq 2
$signChangingBracketCount = @(
    $bracketEvidence | Where-Object {
        [int]$_.signChangingAcceptedWinner -eq 1
    }).Count
$signChangingBracketObserved = $signChangingBracketCount -gt 0
$selectorDirectionValidatedCount = @(
    $bracketEvidence | Where-Object {
        [int]$_.selectorDirectionValidated -eq 1
    }).Count
$selectorPolicyCells = @(
    $rootSummaries | Where-Object {
        [int]$_.selectorPolicyPredictionApplicable -eq 1
    })
$selectorPolicyCellCount = $selectorPolicyCells.Count
$selectorPredictionMatchCount = @(
    $selectorPolicyCells | Where-Object {
        [int]$_.selectorPolicyPredictionMatchesForcedWinner -eq 1
    }).Count
$selectorTailValidatedCellCount = @(
    $selectorPolicyCells | Where-Object {
        [int]$_.selectorPolicyTailCellValidated -eq 1
    }).Count
$expectedFormalScenarioCount =
    if ($selectorFormal) { 5 } elseif ($legacyFormal) { 8 } else { 0 }
$aBDataUsable =
    $formal -and
    $allExploratoryUsable -and
    $rootSummaries.Count -eq $expectedFormalScenarioCount
$genericCrossoverClaimUsable = Test-CrossoverClaimUsable `
    -ABDataUsable $aBDataUsable `
    -RadixAcceptedCellCount $radixAcceptedCellCount `
    -DirectAcceptedCellCount $directAcceptedCellCount `
    -BracketEvidenceComplete $bracketEvidenceComplete `
    -SignChangingBracketObserved $signChangingBracketObserved
$selectorPolicyClaimUsable = if ($contentionFormal) {
    [bool](Test-SelectorPolicyClaimUsable `
        -ABDataUsable $aBDataUsable `
        -RadixAcceptedCellCount $radixAcceptedCellCount `
        -DirectAcceptedCellCount $directAcceptedCellCount `
        -BracketEvidenceComplete $bracketEvidenceComplete `
        -SelectorDirectionValidatedCount (
            $selectorDirectionValidatedCount) `
        -PolicyCellCount $selectorPolicyCellCount `
        -PredictionMatchCount $selectorPredictionMatchCount)
}
elseif ($nvidiaSurfaceFormal) {
    [bool](Test-SelectorSurfacePolicyClaimUsable `
        -ABDataUsable $aBDataUsable `
        -RadixAcceptedCellCount $radixAcceptedCellCount `
        -DirectAcceptedCellCount $directAcceptedCellCount `
        -RequiredRadixAcceptedCells 3 `
        -RequiredDirectAcceptedCells 2 `
        -PolicyCellCount $selectorPolicyCellCount `
        -PredictionMatchCount $selectorPredictionMatchCount)
}
else {
    $false
}
$selectorPolicyTailClaimUsable = [bool](
    $selectorFormal -and
    (Test-SelectorPolicyTailClaimUsable `
        -SelectorPolicyClaimUsable $selectorPolicyClaimUsable `
        -PolicyCellCount $selectorPolicyCellCount `
        -TailValidatedCellCount $selectorTailValidatedCellCount))
$crossoverClaimUsable = if ($selectorFormal) {
    $selectorPolicyClaimUsable
}
else {
    $genericCrossoverClaimUsable
}
$qualityLines = @(
    'GPU adaptive spatial-backend matrix quality summary'
    "status=$(if ($allExploratoryUsable) { 'performance-usable' } else { 'invalid' })"
    "formalAcceptanceMode=$([int]$formal)"
    "matrixPreset=$($runner.matrixPreset)"
    "matrixRole=$($runner.matrixRole)"
    "scenarioCount=$($rootSummaries.Count)"
    "exploratoryDataUsable=$([int]$allExploratoryUsable)"
    "aBDataUsable=$([int]$aBDataUsable)"
    'signedMetricConvention=positive-means-radix-faster'
    "radixAcceptedCellCount=$radixAcceptedCellCount"
    "directAcceptedCellCount=$directAcceptedCellCount"
    "crossoverBracketApplicable=$([int]$contentionFormal)"
    "bracketEvidenceComplete=$([int]$bracketEvidenceComplete)"
    "bracketCandidatePairCount=$($bracketEvidence.Count)"
    "signChangingBracketCount=$signChangingBracketCount"
    "signChangingBracketObserved=$([int]$signChangingBracketObserved)"
    "selectorDirectionValidatedCount=$selectorDirectionValidatedCount"
    "selectorPolicyCellCount=$selectorPolicyCellCount"
    "selectorPredictionMatchCount=$selectorPredictionMatchCount"
    "selectorTailValidatedCellCount=$selectorTailValidatedCellCount"
    "genericCrossoverClaimUsable=$([int]$genericCrossoverClaimUsable)"
    "selectorPolicyClaimUsable=$([int]$selectorPolicyClaimUsable)"
    'selectorPolicyTimingMeasured=0'
    'recordAdaptiveTimingMeasured=0'
    'selectorRuntimeOverheadMeasured=0'
    "selectorPolicyTailClaimUsable=$([int]$selectorPolicyTailClaimUsable)"
    "crossoverClaimUsable=$([int]$crossoverClaimUsable)"
    'crossoverRequiresAtLeastTwoAcceptedCellsPerWinner=1'
    'activeDeviceIdentityConsistent=1'
    'measurementReadbackBytes=0'
    'performanceGateDoesNotControlDataRetention=1'
    'antiOverclaim=No universal winner may be claimed from this matrix.'
)
if ($formal) {
    $qualityLines += @(
        "formalGraphicsDeviceVendorId=0x$(([int]$expectedFormalDevice.vendorId).ToString('X4'))"
        "formalGraphicsDeviceId=0x$(([int]$expectedFormalDevice.deviceId).ToString('X4'))"
        "formalGraphicsDeviceType=$($expectedFormalDevice.deviceType)"
        "formalGraphicsDeviceName=$($expectedFormalDevice.deviceName)")
}
if ($contentionFormal) {
    $qualityLines += @(
        'crossoverBracketAxis=dominant-set-cardinality'
        'crossoverBracketFixedElementCount=1048576'
        'crossoverBracketFixedBinCount=16'
        'crossoverBracketOrderedCardinalities=1,4,16'
        'crossoverBracketCommonSeed=20261002'
        'hotset4ColdTailFraction=0.125'
        'selectorPolicyClaimKind=classification-replay-not-recordadaptive-timing'
        'selectorRequiredInnerProfilerMarkersEnabled=0'
        'selectorPolicySupportDomain=exact-five-cell-holdout-only'
        'selectorBroaderThresholdValidated=0'
        'selectorProductionReady=0'
        'crossoverRequiresBothRadixToDirectBrackets=1'
        'selectorPolicy=radix-if-exact-calibrated-cell-else-direct'
        'selectorPolicyPredicate=full-element-count-bin-count-distribution-exact-single-bin-key-tuple'
        'selectorExactRadixCandidateCell1=N262144,C16,singlebin,key9'
        'selectorExactRadixCandidateCell2=N1048576,C16,singlebin,key10'
        'nonSingleBinExactKeySentinel=-1'
        'selectorRequiredLowerWinner=radix'
        'selectorRequiredUpperWinner=direct'
        'selectorRequiredDirectionValidatedCount=2'
        'selectorRequiredPredictionMatchCount=5'
        'selectorTailExpectedPairCount=8'
        'selectorTailMinimumWinningP99Pairs=7'
        'selectorTailMinimumWorstPairP99ImprovementPercent=-10'
        'selectorTailRequiredValidatedCellCount=5'
        'crossoverBracketReason=contention-formal-holdout')
}
elseif ($nvidiaSurfaceFormal) {
    $qualityLines += @(
        'selectorPolicyClaimKind=classification-replay-not-recordadaptive-timing'
        'selectorRequiredInnerProfilerMarkersEnabled=0'
        'selectorPolicySupportDomain=frozen-five-cell-rtx4090-holdout-only'
        'selectorBroaderThresholdValidated=0'
        'selectorProductionReady=0'
        'crossoverRequiresBothRadixToDirectBrackets=0'
        'selectorPolicy=radix-if-calibrated-cell-else-direct'
        'selectorPolicyPredicate=element-count-bin-count-distribution-singlebin-key-mode'
        'selectorRadixCandidateCell1=N1048576,C16,singlebin,any-valid-key'
        'selectorRadixCandidateCell2=N1048576,C16,hotset4'
        'selectorRadixCandidateCell3=N1048576,C16,uniform'
        'selectorRequiredRadixAcceptedCells=3'
        'selectorRequiredDirectAcceptedCells=2'
        'selectorRequiredPredictionMatchCount=5'
        'selectorTailExpectedPairCount=8'
        'selectorTailMinimumWinningP99Pairs=7'
        'selectorTailMinimumWorstPairP99ImprovementPercent=-10'
        'selectorTailRequiredValidatedCellCount=5'
        'crossoverBracketReason=not-applicable-exact-surface-holdout')
}
else {
    $qualityLines += @(
        'selectorPolicyClaimKind=not-applicable'
        'selectorPolicySupportDomain=not-applicable'
        'crossoverRequiresBothRadixToDirectBrackets=0'
        'crossoverBracketReason=not-contention-formal-preset')
}
foreach ($summary in $rootSummaries) {
    $prefix = $summary.scenarioId.Replace('-', '_')
    $qualityLines += "$prefix.classification=$($summary.classification)"
    $qualityLines += "$prefix.winner=$($summary.winner)"
    $qualityLines +=
        "$prefix.exactSingleBinKey=$($summary.exactSingleBinKey)"
    $qualityLines += (
        "$prefix.selectorPolicyPrediction=" +
        "$($summary.selectorPolicyPrediction)")
    $qualityLines += (
        "$prefix.selectorPolicyPredictionMatchesForcedWinner=" +
        "$($summary.selectorPolicyPredictionMatchesForcedWinner)")
    $qualityLines += (
        "$prefix.selectorPolicyTailCellValidated=" +
        "$($summary.selectorPolicyTailCellValidated)")
    $qualityLines += (
        "$prefix.signedGpuAverageImprovementMedianPercent=" +
        "$($summary.signedGpuAverageImprovementMedianPercent)")
    $qualityLines += (
        "$prefix.signedGpuP99ImprovementMedianPercent=" +
        "$($summary.signedGpuP99ImprovementMedianPercent)")
}
foreach ($evidence in $bracketEvidence) {
    $prefix = (
        "bracket_$($evidence.lowerDominantSetCardinality)_" +
        "$($evidence.upperDominantSetCardinality)")
    $qualityLines += "$prefix.lowerScenarioId=$($evidence.lowerScenarioId)"
    $qualityLines += "$prefix.lowerWinner=$($evidence.lowerWinner)"
    $qualityLines += "$prefix.upperScenarioId=$($evidence.upperScenarioId)"
    $qualityLines += "$prefix.upperWinner=$($evidence.upperWinner)"
    $qualityLines += (
        "$prefix.signChangingAcceptedWinner=" +
        "$($evidence.signChangingAcceptedWinner)")
    $qualityLines += (
        "$prefix.selectorDirectionValidated=" +
        "$($evidence.selectorDirectionValidated)")
}
$qualityPath = Join-Path $root 'quality-summary.txt'
$qualityLines |
    Set-Content -LiteralPath $qualityPath -Encoding utf8
$qualityLines
"matrixSummary=$(Join-Path $root 'matrix-summary.csv')"
"bracketEvidence=$bracketEvidencePath"
"qualitySummary=$qualityPath"
