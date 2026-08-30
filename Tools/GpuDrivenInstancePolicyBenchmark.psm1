Set-StrictMode -Version Latest

$script:Invariant = [Globalization.CultureInfo]::InvariantCulture
$script:ExpectedSchedule = [string[]]@('A', 'B', 'B', 'A', 'B', 'A', 'A', 'B')
$script:ExpectedPairOrders = [string[]]@('AB', 'AB', 'BA', 'BA', 'BA', 'BA', 'AB', 'AB')
$script:ExpectedPairIndices = [int[]]@(1, 1, 2, 2, 3, 3, 4, 4)
$script:GpuFrameBlockMinimumCoveragePercent = 95.0
$script:GpuFramePairedMinimumCoveragePercent = 90.0

function Get-PolicyTextSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Text)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text)))).
            Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
}

function Test-PolicySha256Equal {
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string]$Left,
        [AllowEmptyString()][string]$Right
    )

    if ($Left -notmatch '\A[0-9a-fA-F]{64}\z' -or
        $Right -notmatch '\A[0-9a-fA-F]{64}\z') {
        return $false
    }
    return [string]::Equals(
        $Left,
        $Right,
        [StringComparison]::OrdinalIgnoreCase)
}

function Get-PolicyCombinedSha256 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo[]]$Files,
        [Parameter(Mandatory = $true)][string]$RelativeTo
    )

    $root = [IO.Path]::GetFullPath($RelativeTo).TrimEnd('\', '/')
    $builder = [Text.StringBuilder]::new()
    foreach ($file in @($Files | Sort-Object FullName -Unique)) {
        $full = [IO.Path]::GetFullPath($file.FullName)
        if (-not $full.StartsWith(
                $root + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Hashed file is outside the provenance root: $full"
        }
        $relative = $full.Substring($root.Length).
            TrimStart([char[]]@('\', '/')).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
        [void]$builder.Append($relative)
        [void]$builder.Append("`0")
        [void]$builder.Append($hash)
        [void]$builder.Append("`n")
    }
    return Get-PolicyTextSha256 -Text $builder.ToString()
}

function Read-PolicyKeyValueFile {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required key/value evidence is missing: $Path"
    }
    $result = @{}
    foreach ($line in Get-Content -LiteralPath $Path) {
        $parts = $line -split '=', 2
        if ($parts.Count -eq 2) {
            $result[$parts[0]] = $parts[1]
        }
    }
    return $result
}

function Get-RequiredPolicyMapValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][hashtable]$Map,
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Context
    )

    if (-not $Map.ContainsKey($Name)) {
        throw "$Context is missing '$Name'."
    }
    return [string]$Map[$Name]
}

function ConvertTo-PolicyBoolean {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Value)

    $text = ([string]$Value).Trim()
    if ($text -ceq '1' -or $text -ceq 'True' -or $text -ceq 'true') {
        return $true
    }
    if ($text -ceq '0' -or $text -ceq 'False' -or $text -ceq 'false') {
        return $false
    }
    throw "Value '$text' is not a canonical benchmark Boolean."
}

function ConvertTo-PolicyDouble {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Value)

    $number = [double]::Parse(
        [string]$Value,
        [Globalization.NumberStyles]::Float,
        $script:Invariant)
    if ([double]::IsNaN($number) -or [double]::IsInfinity($number)) {
        throw "Metric '$Value' is not finite."
    }
    return $number
}

function Assert-PolicyCsvColumns {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object[]]$Rows,
        [Parameter(Mandatory = $true)][string[]]$Names,
        [Parameter(Mandatory = $true)][string]$Context
    )

    if ($Rows.Count -eq 0) {
        throw "$Context has no rows."
    }
    $observed = [string[]]@($Rows[0].PSObject.Properties.Name)
    foreach ($name in $Names) {
        if ($observed -cnotcontains $name) {
            throw "$Context is missing CSV column '$name'."
        }
    }
}

function Get-PolicyPercentile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][double[]]$Values,
        [Parameter(Mandatory = $true)][ValidateRange(0.0, 1.0)]
        [double]$Quantile
    )

    if ($Values.Count -eq 0) {
        throw 'Cannot compute a percentile of an empty metric.'
    }
    $sorted = [double[]]@($Values | Sort-Object)
    if ($sorted.Count -eq 1) {
        return $sorted[0]
    }
    $position = ($sorted.Count - 1) * $Quantile
    $lower = [int][Math]::Floor($position)
    $upper = [int][Math]::Ceiling($position)
    if ($lower -eq $upper) {
        return $sorted[$lower]
    }
    $weight = $position - $lower
    return [double](
        $sorted[$lower] * (1.0 - $weight) +
        $sorted[$upper] * $weight)
}

function Get-PolicyMetricSummary {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][double[]]$Values)

    if ($Values.Count -eq 0) {
        throw 'Metric summary requires at least one value.'
    }
    return [pscustomobject][ordered]@{
        count = $Values.Count
        mean = [double](($Values | Measure-Object -Average).Average)
        p50 = Get-PolicyPercentile $Values 0.50
        p95 = Get-PolicyPercentile $Values 0.95
        p99 = Get-PolicyPercentile $Values 0.99
    }
}

function Get-PolicyRegressionPercent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][double]$Baseline,
        [Parameter(Mandatory = $true)][double]$Candidate
    )

    if ($Baseline -le 0.0) {
        throw 'Regression denominator must be positive.'
    }
    return 100.0 * ($Candidate - $Baseline) / $Baseline
}

function Get-PolicyImprovementPercent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][double]$Baseline,
        [Parameter(Mandatory = $true)][double]$Candidate
    )

    if ($Baseline -le 0.0) {
        throw 'Improvement denominator must be positive.'
    }
    return 100.0 * ($Baseline - $Candidate) / $Baseline
}

function Get-PolicyMetricValues {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object[]]$Rows,
        [Parameter(Mandatory = $true)][string]$Property,
        [switch]$RequirePositive
    )

    $values = [Collections.Generic.List[double]]::new()
    foreach ($row in $Rows) {
        $propertyValue = $row.PSObject.Properties[$Property]
        if ($null -eq $propertyValue) {
            throw "Metric column '$Property' is missing."
        }
        $value = ConvertTo-PolicyDouble $propertyValue.Value
        if ($RequirePositive -and $value -le 0.0) {
            throw "Metric '$Property' contains non-positive value '$value'."
        }
        $values.Add($value)
    }
    return [double[]]$values.ToArray()
}

function Get-PolicyPairedComparison {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object[]]$Rows,
        [Parameter(Mandatory = $true)][string]$BaselineCaseId,
        [Parameter(Mandatory = $true)][string]$CandidateCaseId,
        [Parameter(Mandatory = $true)][string]$Metric
    )

    $gpuFrameMetric = $Metric -ceq 'gpuFrameMs'
    $baselineRows = @($Rows | Where-Object {
        [string]$_.caseId -ceq $BaselineCaseId
    })
    $candidateRows = @($Rows | Where-Object {
        [string]$_.caseId -ceq $CandidateCaseId
    })
    if ($baselineRows.Count -eq 0 -or
        $candidateRows.Count -ne $baselineRows.Count) {
        throw "Metric '$Metric' has unbalanced case rows."
    }

    $baselineByKey = @{}
    $candidateByKey = @{}
    foreach ($row in $baselineRows) {
        $key = ([string]$row.pairIndex) + ':' + ([string]$row.sampleIndex)
        if ($baselineByKey.ContainsKey($key)) {
            throw "Baseline has duplicate paired key '$key'."
        }
        $baselineByKey[$key] = $row
    }
    foreach ($row in $candidateRows) {
        $key = ([string]$row.pairIndex) + ':' + ([string]$row.sampleIndex)
        if ($candidateByKey.ContainsKey($key)) {
            throw "Candidate has duplicate paired key '$key'."
        }
        $candidateByKey[$key] = $row
    }
    if ($baselineByKey.Count -ne $candidateByKey.Count) {
        throw "Metric '$Metric' paired key cardinality differs."
    }
    foreach ($key in $baselineByKey.Keys) {
        if (-not $candidateByKey.ContainsKey($key)) {
            throw "Candidate is missing paired key '$key'."
        }
    }
    foreach ($key in $candidateByKey.Keys) {
        if (-not $baselineByKey.ContainsKey($key)) {
            throw "Baseline is missing paired key '$key'."
        }
    }

    $baselineValues = [Collections.Generic.List[double]]::new()
    $candidateValues = [Collections.Generic.List[double]]::new()
    $pairedDeltaValues = [Collections.Generic.List[double]]::new()
    $pairedImprovementValues = [Collections.Generic.List[double]]::new()
    $positiveWins = 0
    $consumed = 0
    $unavailablePairs = 0
    foreach ($key in @($baselineByKey.Keys | Sort-Object)) {
        $baselineRow = $baselineByKey[$key]
        $candidateRow = $candidateByKey[$key]
        if ([string]$baselineRow.logicalOrdinal -cne
                [string]$candidateRow.logicalOrdinal -or
            [string]$baselineRow.updateHash -cne
                [string]$candidateRow.updateHash -or
            [string]$baselineRow.expectedStateHash -cne
                [string]$candidateRow.expectedStateHash) {
            throw "Paired logical input parity failed at '$key'."
        }
        if ($gpuFrameMetric) {
            $baselineValidity =
                $baselineRow.PSObject.Properties['gpuFrameValid']
            $candidateValidity =
                $candidateRow.PSObject.Properties['gpuFrameValid']
            if ($null -eq $baselineValidity -or
                $null -eq $candidateValidity) {
                throw "GPU-frame validity is missing at paired key '$key'."
            }
            $baselineValid = ConvertTo-PolicyBoolean `
                $baselineValidity.Value
            $candidateValid = ConvertTo-PolicyBoolean `
                $candidateValidity.Value
            $baselineText = [string](
                $baselineRow.PSObject.Properties[$Metric].Value)
            $candidateText = [string](
                $candidateRow.PSObject.Properties[$Metric].Value)
            if ((-not $baselineValid -and
                    $baselineText -cne 'unavailable') -or
                (-not $candidateValid -and
                    $candidateText -cne 'unavailable')) {
                throw "Unavailable GPU-frame data is not literal 'unavailable' at '$key'."
            }
            if (($baselineValid -and
                    (ConvertTo-PolicyDouble $baselineText) -le 0.0) -or
                ($candidateValid -and
                    (ConvertTo-PolicyDouble $candidateText) -le 0.0)) {
                throw "Available GPU-frame data is not positive at '$key'."
            }
            if (-not $baselineValid -or -not $candidateValid) {
                $unavailablePairs++
                continue
            }
        }
        $baseline = ConvertTo-PolicyDouble (
            $baselineRow.PSObject.Properties[$Metric].Value)
        $candidate = ConvertTo-PolicyDouble (
            $candidateRow.PSObject.Properties[$Metric].Value)
        if ($baseline -le 0.0 -or $candidate -le 0.0) {
            throw "Metric '$Metric' must be positive at '$key'."
        }
        $delta = $baseline - $candidate
        $improvement = Get-PolicyImprovementPercent $baseline $candidate
        $baselineValues.Add($baseline)
        $candidateValues.Add($candidate)
        $pairedDeltaValues.Add($delta)
        $pairedImprovementValues.Add($improvement)
        if ($delta -gt 0.0) {
            $positiveWins++
        }
        $consumed++
    }
    $totalPairCount = $baselineByKey.Count
    $pairCoveragePercent =
        100.0 * [double]$consumed / [double]$totalPairCount
    if (-not $gpuFrameMetric -and
        ($consumed -ne $totalPairCount -or
         $consumed -ne $candidateByKey.Count)) {
        throw "Metric '$Metric' did not consume every paired key exactly once."
    }
    if ($gpuFrameMetric -and
        $pairCoveragePercent -lt
            $script:GpuFramePairedMinimumCoveragePercent) {
        throw "Metric '$Metric' paired coverage is $pairCoveragePercent%; expected at least $($script:GpuFramePairedMinimumCoveragePercent)%."
    }

    $baselineSummary = Get-PolicyMetricSummary $baselineValues.ToArray()
    $candidateSummary = Get-PolicyMetricSummary $candidateValues.ToArray()
    return [pscustomobject][ordered]@{
        metric = $Metric
        totalPairCount = $totalPairCount
        pairCount = $consumed
        unavailablePairCount = $unavailablePairs
        pairCoveragePercent = $pairCoveragePercent
        minimumPairCoveragePercent = if ($gpuFrameMetric) {
            $script:GpuFramePairedMinimumCoveragePercent
        }
        else { 100.0 }
        positiveWins = $positiveWins
        positiveWinPercent = 100.0 * $positiveWins / $consumed
        baseline = $baselineSummary
        candidate = $candidateSummary
        meanImprovementPercent = Get-PolicyImprovementPercent `
            $baselineSummary.mean $candidateSummary.mean
        p95RegressionPercent = Get-PolicyRegressionPercent `
            $baselineSummary.p95 $candidateSummary.p95
        p99RegressionPercent = Get-PolicyRegressionPercent `
            $baselineSummary.p99 $candidateSummary.p99
        pairedDelta = Get-PolicyMetricSummary $pairedDeltaValues.ToArray()
        pairedImprovementPercent =
            Get-PolicyMetricSummary $pairedImprovementValues.ToArray()
    }
}

function Get-PolicyNUnitReceipt {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string[]]$ExpectedIdentities
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "EditMode results are missing: $resolved"
    }
    [xml]$xml = Get-Content -LiteralPath $resolved -Raw
    $run = $xml.'test-run'
    if ($null -eq $run) {
        throw "EditMode result is not NUnit XML: $resolved"
    }
    $identities = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $nodes = @($xml.SelectNodes("//test-suite[@type='Assembly']")) +
        @($xml.SelectNodes("//test-suite[@type='TestFixture']")) +
        @($xml.SelectNodes('//test-case'))
    foreach ($node in $nodes) {
        $isAssembly =
            [string]$node.GetAttribute('type') -ceq 'Assembly'
        $identity = if ($isAssembly) {
            [string]$node.GetAttribute('name')
        }
        else {
            [string]$node.GetAttribute('fullname')
        }
        if (-not [string]::IsNullOrWhiteSpace($identity)) {
            [void]$identities.Add($identity)
        }
    }
    $missing = @($ExpectedIdentities | Where-Object {
        -not $identities.Contains($_)
    })
    $receipt = [pscustomobject][ordered]@{
        path = $resolved
        sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
        result = [string]$run.result
        total = [int]$run.total
        passed = [int]$run.passed
        failed = [int]$run.failed
        skipped = [int]$run.skipped
        inconclusive = [int]$run.inconclusive
        expectedIdentities = $ExpectedIdentities
        observedIdentities = @($identities | Sort-Object)
        missingIdentities = $missing
    }
    if ($receipt.result -cne 'Passed' -or
        $receipt.total -le 0 -or
        $receipt.passed -ne $receipt.total -or
        $receipt.failed -ne 0 -or
        $receipt.skipped -ne 0 -or
        $receipt.inconclusive -ne 0 -or
        @($receipt.missingIdentities).Count -ne 0) {
        throw 'EditMode receipt is incomplete or contains non-passing tests.'
    }
    return $receipt
}

function Assert-PolicyPreflightBinding {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Provenance,
        [Parameter(Mandatory = $true)][string]$ExpectedCommit,
        [Parameter(Mandatory = $true)][string]$ExpectedSourceSha256,
        [Parameter(Mandatory = $true)][string]$ExpectedXmlSha256,
        [Parameter(Mandatory = $true)][string]$ExpectedUnityVersion
    )
    if ([int]$Provenance.schemaVersion -ne 1 -or
        -not [bool]$Provenance.generatedByRunner -or
        [bool]$Provenance.externalReceiptAccepted -or
        [string]$Provenance.sourceCommit -cne $ExpectedCommit -or
        [string]$Provenance.sourceSnapshotSha256 -cne
            $ExpectedSourceSha256 -or
        [string]$Provenance.sourceSnapshotSha256After -cne
            $ExpectedSourceSha256 -or
        [string]$Provenance.resultSha256 -cne $ExpectedXmlSha256 -or
        [string]$Provenance.unityVersion -cne $ExpectedUnityVersion -or
        [string]$Provenance.graphicsApiArgument -cne '-force-d3d12') {
        throw 'EditMode receipt is stale or not bound to this runner, commit, source hash, Unity version, and D3D12 contract.'
    }
    return $true
}

function Assert-PolicySchedule {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][object[]]$Rows,
        [Parameter(Mandatory = $true)][int]$SampleFrames,
        [Parameter(Mandatory = $true)][string]$LeftCaseId,
        [Parameter(Mandatory = $true)][string]$RightCaseId,
        [Parameter(Mandatory = $true)][string]$ExpectedProcessId
    )

    $blocks = @($Rows | Group-Object blockIndex | Sort-Object {
        [int]$_.Name
    })
    if ($blocks.Count -ne 8 -or $Rows.Count -ne 8 * $SampleFrames) {
        throw 'Raw policy evidence does not contain exactly eight full blocks.'
    }
    for ($index = 0; $index -lt 8; $index++) {
        $block = $blocks[$index]
        $expectedCase = if ($script:ExpectedSchedule[$index] -ceq 'A') {
            $LeftCaseId
        }
        else {
            $RightCaseId
        }
        $caseIds = @($block.Group | Select-Object -ExpandProperty caseId -Unique)
        if ([int]$block.Name -ne $index + 1 -or
            $block.Count -ne $SampleFrames -or
            $caseIds.Count -ne 1 -or
            [string]$caseIds[0] -cne $expectedCase) {
            throw "Raw policy schedule is invalid at block $($index + 1)."
        }
        $sampleIndices = [int[]]@($block.Group | ForEach-Object {
            [int]$_.sampleIndex
        } | Sort-Object -Unique)
        if ($sampleIndices.Count -ne $SampleFrames) {
            throw "Block $($index + 1) does not have unique samples 1..N."
        }
        for ($sampleOffset = 0;
             $sampleOffset -lt $SampleFrames;
             $sampleOffset++) {
            if ($sampleIndices[$sampleOffset] -ne $sampleOffset + 1) {
                throw "Block $($index + 1) sample coverage is not 1..N."
            }
        }
        foreach ($row in $block.Group) {
            $expectedOrdinal =
                ([int64]$script:ExpectedPairIndices[$index] - 1L) *
                [int64]$SampleFrames + [int64]$row.sampleIndex
            if ([string]$row.side -cne $script:ExpectedSchedule[$index] -or
                [string]$row.pairOrder -cne
                    $script:ExpectedPairOrders[$index] -or
                [int]$row.pairIndex -ne
                    $script:ExpectedPairIndices[$index] -or
                [int]$row.withinPairPosition -ne (($index % 2) + 1) -or
                [string]$row.processId -cne $ExpectedProcessId -or
                [int64]$row.logicalOrdinal -ne $expectedOrdinal) {
                throw "Raw policy row identity is invalid in block $($index + 1)."
            }
        }
    }
}

function Assert-PolicyBenchmarkEvidence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][int]$ExpectedSampleFrames,
        [Parameter(Mandatory = $true)][int]$ExpectedSeed,
        [Parameter(Mandatory = $true)][string]$ExpectedBuildCommit,
        [Parameter(Mandatory = $true)][string]$ExpectedUnityVersion,
        [Parameter(Mandatory = $true)][string]$ExpectedPipelineFingerprint,
        [Parameter(Mandatory = $true)][string]$ExpectedShaderFingerprint,
        [Parameter(Mandatory = $true)][string]$ExpectedMeasurementFingerprint,
        [Parameter(Mandatory = $true)][string]$ExpectedCalibrationProtocol,
        [string]$ExpectedLeftCaseId,
        [string]$ExpectedRightCaseId,
        [switch]$RequireAcceptedProfile
    )

    if ($ExpectedBuildCommit -notmatch '^[0-9a-fA-F]{40}$' -or
        $ExpectedUnityVersion -cne '6000.5.2f1' -or
        $ExpectedPipelineFingerprint -notmatch '^[0-9a-fA-F]{64}$' -or
        $ExpectedShaderFingerprint -notmatch '^[0-9a-fA-F]{64}$' -or
        $ExpectedMeasurementFingerprint -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'Expected evidence provenance must use Unity 6000.5.2f1, a 40-hex commit, and 64-hex contract fingerprints.'
    }

    $root = [IO.Path]::GetFullPath($Directory)
    $paths = @{
        summary = Join-Path $root 'run-summary.txt'
        config = Join-Path $root 'config.json'
        device = Join-Path $root 'device.json'
        raw = Join-Path $root 'raw-frames.csv'
        blocks = Join-Path $root 'block-summary.csv'
        validation = Join-Path $root 'validation.csv'
        selector = Join-Path $root 'selector-overhead.csv'
    }
    foreach ($path in $paths.Values) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Policy benchmark evidence is missing: $path"
        }
    }

    $summary = Read-PolicyKeyValueFile $paths.summary
    foreach ($entry in @(
            @('passed', '1'),
            @('status', 'passed'),
            @('evidenceGatePassed', '1'),
            @('scheduleContract', 'ABBA;BAAB'),
            @('expectedMeasurementBlockCount', '8'),
            @('measurementBlockCount', '8'),
            @('timedAllocationFree', '1'),
            @('mainThreadAllocationRows', '0'),
            @('mainThreadAllocatedBytes', '0'),
            @('slotWaitFrames', '0'),
            @('measurementReadbackBytes', '0'),
            @('validationFailures', '0'),
            @('completionFencesComplete', '1'),
            @('decisionIssueCount', '0'),
            @('frameAlignmentIssueCount', '0'),
            @('pairedInputIssueCount', '0'),
            @('firstMeasuredResidentIssueCount', '0'),
            @('validationLifecycleDrainIssueCount', '0'),
            @('validationTimeoutCount', '0'),
            @('validationDrainTimeoutCount', '0'),
            @('presentationValidationTimeoutCount', '0'),
            @('validationLifecycleComplete', '1'),
            @('validationLifecycleDrainStatus', 'not-required'),
            @('validationLifecycleDrainIncomplete', '0'),
            @('measuredLogicalOrdinalContract', 'pair-local-v1'),
            @('unmeasuredLogicalOrdinalContract', 'independent-high-bit-v1'),
            @('nativeTimestampWarmupPassed', '1'),
            @('nativeTimestampWarmupStatus', 'ready'),
            @('nativeTimestampAcquireFailures', '0'),
            @('nativeTimestampResultFailures', '0'),
            @('nativeTimestampTimeouts', '0'),
            @('buildCommit', $ExpectedBuildCommit),
            @('buildCommitValid', '1'),
            @('selectorOverheadIterations', '100000'),
            @('selectorOverheadAllocatedBytes', '0'),
            @('selectorOverheadUnstableDecisionCount', '0'))) {
        $actual = Get-RequiredPolicyMapValue $summary $entry[0] $root
        if ($actual -cne $entry[1]) {
            throw "$root has $($entry[0])='$actual'; expected '$($entry[1])'."
        }
    }
    $expectedRawCount = 8 * $ExpectedSampleFrames
    foreach ($entry in @(
            @('rawFrameCount', [string]$expectedRawCount),
            @('expectedRawFrameCount', [string]$expectedRawCount),
            @('frameTimingReadyRows', [string]$expectedRawCount),
            @('submissionWindowReadyRows', [string]$expectedRawCount),
            @('nativeTimestampReadyRows', [string]$expectedRawCount),
            @('stableDecisionRows', [string]$expectedRawCount),
            @('timestampInstrumentationReadbackBytes',
                [string](16L * $expectedRawCount)),
            @('validationCount', '4'),
            @('expectedValidationCount', '4'),
            @('presentationValidationCount', '4'),
            @('expectedPresentationValidationCount', '4'),
            @('presentationValidationFailures', '0'))) {
        $actual = Get-RequiredPolicyMapValue $summary $entry[0] $root
        if ($actual -cne $entry[1]) {
            throw "$root has incomplete $($entry[0]) evidence."
        }
    }
    $summaryGpuFrameReadyRows = [int](Get-RequiredPolicyMapValue `
        $summary 'gpuFrameReadyRows' $root)
    $summaryGpuFrameUnavailableRows = [int](Get-RequiredPolicyMapValue `
        $summary 'gpuFrameUnavailableRows' $root)
    if ($summaryGpuFrameReadyRows -lt 0 -or
        $summaryGpuFrameUnavailableRows -lt 0 -or
        $summaryGpuFrameReadyRows + $summaryGpuFrameUnavailableRows -ne
            $expectedRawCount) {
        throw "$root has inconsistent GPU-frame availability totals."
    }
    foreach ($name in @(
            'presentationValidationReadbackBytes',
            'minimumRenderTargetNonBlackPixels')) {
        $value = [int64](Get-RequiredPolicyMapValue $summary $name $root)
        if ($value -le 0L) {
            throw "$root requires positive $name evidence."
        }
    }
    $deterministicRenderTargetHash = Get-RequiredPolicyMapValue `
        $summary 'deterministicRenderTargetHash' $root
    if ([string]::IsNullOrWhiteSpace($deterministicRenderTargetHash) -or
        $deterministicRenderTargetHash -ceq 'unavailable') {
        throw "$root has no deterministic render-target hash."
    }
    foreach ($entry in @(
            @('pipelineContractFingerprint', $ExpectedPipelineFingerprint),
            @('shaderContractFingerprint', $ExpectedShaderFingerprint),
            @('measurementContractFingerprint', $ExpectedMeasurementFingerprint),
            @('calibrationProtocol', $ExpectedCalibrationProtocol))) {
        if ((Get-RequiredPolicyMapValue $summary $entry[0] $root) -cne
                $entry[1]) {
            throw "$root has a mismatched $($entry[0])."
        }
    }
    if ($RequireAcceptedProfile -and
        -not (ConvertTo-PolicyBoolean (
            Get-RequiredPolicyMapValue $summary 'profileAccepted' $root))) {
        throw "$root did not accept its exact policy profile."
    }

    $config = Get-Content -LiteralPath $paths.config -Raw | ConvertFrom-Json
    $device = Get-Content -LiteralPath $paths.device -Raw | ConvertFrom-Json
    if ([int]$config.schemaVersion -ne 1 -or
        [string]$config.suite -cne 'summit.gpu-driven-instance-policy' -or
        [string]$config.unityVersion -cne $ExpectedUnityVersion -or
        [int]$config.seed -ne $ExpectedSeed -or
        [int]$config.sampleFramesPerBlock -ne $ExpectedSampleFrames -or
        [int]$config.measurementBlocks -ne 8 -or
        [string]$config.scheduleContract -cne 'ABBA;BAAB' -or
        [int]$config.frameTimingResultLatencyFrames -ne 4 -or
        [string]$config.gpuFrameUnavailableLiteral -cne 'unavailable' -or
        [int]$config.gpuFrameBlockMinimumCoveragePercent -ne 95 -or
        [int]$config.gpuFramePairedMinimumCoveragePercent -ne 90 -or
        [int]$config.otherTimedMetricCoveragePercent -ne 100 -or
        -not [bool]$config.stateResetOutsideMeasuredWindow -or
        -not [bool]$config.selectorResetOutsideMeasuredWindow -or
        -not [bool]$config.caseLocalConvergenceOutsideMeasuredWindow -or
        [int64]$config.measurementReadbackBytesPerFrame -ne 0 -or
        [int]$config.timestampInstrumentationBytesPerCompletedSample -ne 16 -or
        [int]$config.selectorOverheadIterations -ne 100000 -or
        [string]$config.buildCommit -cne $ExpectedBuildCommit -or
        [string]$config.pipelineContractFingerprint -cne
            $ExpectedPipelineFingerprint -or
        [string]$config.shaderContractFingerprint -cne
            $ExpectedShaderFingerprint -or
        [string]$config.measurementContractFingerprint -cne
            $ExpectedMeasurementFingerprint -or
        [string]$config.calibrationProtocol -cne
            $ExpectedCalibrationProtocol -or
        [string]$config.nativeTimestampAvailability -cne 'Available' -or
        [uint32]$config.nativeTimestampAbiVersion -ne 2 -or
        ([uint32]$config.nativeTimestampCapabilityFlags -band 0x1f) -ne 0x1f -or
        -not [bool]$config.nativeTimestampWarmupPassed) {
        throw "$root configuration contract is not exact."
    }
    if ([string]$device.graphicsDeviceType -cne 'Direct3D12' -or
        [string]$device.unityVersion -cne $ExpectedUnityVersion -or
        -not [bool]$device.supportsComputeShaders -or
        -not [bool]$device.supportsGraphicsFence -or
        -not [bool]$device.supportsAsyncGpuReadback -or
        -not [bool]$device.frameTimingFeatureEnabled) {
        throw "$root did not run on the required D3D12 feature contract."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedLeftCaseId) -and
        [string]$config.leftCase -cne $ExpectedLeftCaseId) {
        throw "$root left case differs from the frozen manifest."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedRightCaseId) -and
        [string]$config.rightCase -cne $ExpectedRightCaseId) {
        throw "$root right case differs from the frozen manifest."
    }

    $raw = @(Import-Csv -LiteralPath $paths.raw)
    Assert-PolicyCsvColumns $raw @(
        'sourceRowIndex', 'processId', 'scenarioId', 'blockIndex',
        'pairIndex', 'pairOrder', 'withinPairPosition', 'side', 'caseId',
        'sampleIndex', 'logicalOrdinal', 'instanceCount', 'viewCount',
        'visibilityBasisPoints', 'dirtyBasisPoints', 'clusterCount',
        'hierarchyCandidateBp', 'totalCpuMs',
        'selectorCpuMs', 'selectorInvoked',
        'gpuRegionElapsedMs', 'cpuFrameMs', 'cpuMainThreadFrameMs',
        'cpuRenderThreadFrameMs', 'gpuFrameMs', 'cpuSubmissionWindowMs',
        'mainThreadAllocatedBytes', 'slotWaitFrames', 'updateHash',
        'changedInstanceCount', 'plannedUploadedRecordCount',
        'plannedUploadCallCount', 'expectedStateHash',
        'decisionUploadMode', 'decisionOutputMode',
        'decisionCullingMode', 'decisionPrimitiveBackend',
        'decisionProfileRuleIndex', 'decisionRuleId', 'decisionFlags',
        'decisionSource', 'decisionAccepted', 'decisionStableExpected',
        'uploadAmplificationBp',
        'completionFenceAppended', 'nativeTimestampStatus',
        'nativeTimestampElapsedNanoseconds',
        'frameTimingValid', 'frameTimingCaptureLatencyFrames',
        'cpuRenderThreadFrameValid', 'gpuFrameValid', 'submissionWindowValid',
        'measurementReadbackBytes', 'timestampInstrumentationReadbackBytes') `
        "$root raw frames"
    Assert-PolicySchedule $raw $ExpectedSampleFrames `
        ([string]$config.leftCase) ([string]$config.rightCase) `
        ([string]$config.processId)

    $rawGpuFrameReadyRows = 0
    for ($index = 0; $index -lt $raw.Count; $index++) {
        $row = $raw[$index]
        $actualAuto =
            [string]$row.caseId -ceq 'gpu-driven-policy/actual-auto'
        $gpuFrameValid = ConvertTo-PolicyBoolean $row.gpuFrameValid
        if ([int]$row.sourceRowIndex -ne $index -or
            [string]$row.scenarioId -cne [string]$config.scenarioId -or
            [int64]$row.mainThreadAllocatedBytes -ne 0 -or
            [int]$row.slotWaitFrames -ne 0 -or
            [string]$row.nativeTimestampStatus -cne 'ready' -or
            [int64]$row.nativeTimestampElapsedNanoseconds -le 0 -or
            -not (ConvertTo-PolicyBoolean $row.frameTimingValid) -or
            [int]$row.frameTimingCaptureLatencyFrames -ne 4 -or
            -not (ConvertTo-PolicyBoolean $row.cpuRenderThreadFrameValid) -or
            -not (ConvertTo-PolicyBoolean $row.submissionWindowValid) -or
            [int64]$row.measurementReadbackBytes -ne 0 -or
            [int64]$row.timestampInstrumentationReadbackBytes -ne 16 -or
            (ConvertTo-PolicyBoolean $row.selectorInvoked) -ne $actualAuto -or
            (ConvertTo-PolicyDouble $row.selectorCpuMs) -lt 0.0 -or
            ($actualAuto -and
             [string]$row.decisionSource -cne 'ActualAuto') -or
            (-not $actualAuto -and
             [string]$row.decisionSource -ceq 'ActualAuto') -or
            -not (ConvertTo-PolicyBoolean $row.completionFenceAppended) -or
            -not (ConvertTo-PolicyBoolean $row.decisionAccepted) -or
            -not (ConvertTo-PolicyBoolean $row.decisionStableExpected) -or
            [uint32]$row.decisionFlags -ne 0) {
            throw "$root has an incomplete or fallback-bearing raw row $index."
        }
        foreach ($metric in @(
                'totalCpuMs', 'gpuRegionElapsedMs', 'cpuFrameMs',
                'cpuMainThreadFrameMs', 'cpuRenderThreadFrameMs',
                'cpuSubmissionWindowMs')) {
            if ((ConvertTo-PolicyDouble $row.PSObject.Properties[$metric].Value) `
                    -le 0.0) {
                throw "$root row $index has invalid $metric."
            }
        }
        if ($gpuFrameValid) {
            if ((ConvertTo-PolicyDouble $row.gpuFrameMs) -le 0.0) {
                throw "$root row $index has invalid gpuFrameMs."
            }
            $rawGpuFrameReadyRows++
        }
        elseif ([string]$row.gpuFrameMs -cne 'unavailable') {
            throw "$root row $index must encode unavailable GPU frame time as literal 'unavailable'."
        }
    }
    if ($rawGpuFrameReadyRows -ne $summaryGpuFrameReadyRows -or
        $raw.Count - $rawGpuFrameReadyRows -ne
            $summaryGpuFrameUnavailableRows) {
        throw "$root GPU-frame availability totals do not match raw rows."
    }

    $blocks = @(Import-Csv -LiteralPath $paths.blocks)
    Assert-PolicyCsvColumns $blocks @(
        'blockIndex', 'pairIndex', 'pairOrder', 'withinPairPosition',
        'side', 'caseId', 'sampleCount', 'timestampReadyRows',
        'frameTimingReadyRows', 'gpuFrameValidCount',
        'submissionWindowReadyRows',
        'stableDecisionRows', 'mainThreadAllocationRows',
        'mainThreadAllocatedBytes', 'slotWaitFrames',
        'residentStateHashBeforeMeasured',
        'firstMeasuredExpectedStateHash',
        'firstMeasuredResidentDifferenceRequired',
        'firstMeasuredStateDiffersFromResident',
        'completionFencesPassed') "$root block summaries"
    $minimumGpuFrameValidRows = [int][Math]::Ceiling(
        $ExpectedSampleFrames *
            ($script:GpuFrameBlockMinimumCoveragePercent / 100.0))
    if ($blocks.Count -ne 8 -or @($blocks | Where-Object {
            [int]$_.sampleCount -ne $ExpectedSampleFrames -or
            [int]$_.timestampReadyRows -ne $ExpectedSampleFrames -or
            [int]$_.frameTimingReadyRows -ne $ExpectedSampleFrames -or
            [int]$_.gpuFrameValidCount -lt $minimumGpuFrameValidRows -or
            [int]$_.gpuFrameValidCount -gt $ExpectedSampleFrames -or
            [int]$_.submissionWindowReadyRows -ne $ExpectedSampleFrames -or
            [int]$_.stableDecisionRows -ne $ExpectedSampleFrames -or
            [int]$_.mainThreadAllocationRows -ne 0 -or
            [int64]$_.mainThreadAllocatedBytes -ne 0 -or
            [int64]$_.slotWaitFrames -ne 0 -or
            -not (ConvertTo-PolicyBoolean $_.completionFencesPassed)
        }).Count -ne 0) {
        throw "$root block summaries are incomplete."
    }
    for ($blockOffset = 0; $blockOffset -lt $blocks.Count; $blockOffset++) {
        $block = $blocks[$blockOffset]
        $expectedSide = $script:ExpectedSchedule[$blockOffset]
        $expectedCase = if ($expectedSide -ceq 'A') {
            [string]$config.leftCase
        }
        else { [string]$config.rightCase }
        $blockRawRows = @($raw | Where-Object {
            [int]$_.blockIndex -eq $blockOffset + 1
        })
        $firstRawRows = @($blockRawRows | Where-Object {
            [int]$_.blockIndex -eq $blockOffset + 1 -and
            [int]$_.sampleIndex -eq 1
        })
        $actualGpuFrameValidCount = @($blockRawRows | Where-Object {
            ConvertTo-PolicyBoolean $_.gpuFrameValid
        }).Count
        if ([int]$block.blockIndex -ne $blockOffset + 1 -or
            [int]$block.pairIndex -ne
                $script:ExpectedPairIndices[$blockOffset] -or
            [string]$block.pairOrder -cne
                $script:ExpectedPairOrders[$blockOffset] -or
            [int]$block.withinPairPosition -ne
                (($blockOffset % 2) + 1) -or
            [string]$block.side -cne $expectedSide -or
            [string]$block.caseId -cne $expectedCase -or
            $blockRawRows.Count -ne $ExpectedSampleFrames -or
            [int]$block.gpuFrameValidCount -ne
                $actualGpuFrameValidCount -or
            $firstRawRows.Count -ne 1) {
            throw "$root block summary identity is invalid at block $($blockOffset + 1)."
        }
        $firstRaw = $firstRawRows[0]
        $residentHash = [string]$block.residentStateHashBeforeMeasured
        $firstMeasuredHash = [string]$block.firstMeasuredExpectedStateHash
        $differenceRequired = ConvertTo-PolicyBoolean `
            $block.firstMeasuredResidentDifferenceRequired
        if ([string]::IsNullOrWhiteSpace($residentHash) -or
            $residentHash -ceq 'unavailable' -or
            [string]::IsNullOrWhiteSpace($firstMeasuredHash) -or
            $firstMeasuredHash -ceq 'unavailable' -or
            $firstMeasuredHash -cne [string]$firstRaw.expectedStateHash -or
            $differenceRequired -ne
                ([int64]$firstRaw.changedInstanceCount -gt 0L) -or
            -not (ConvertTo-PolicyBoolean `
                $block.firstMeasuredStateDiffersFromResident) -or
            ($differenceRequired -and
             $residentHash -ceq $firstMeasuredHash)) {
            throw "$root block $($block.blockIndex) lacks exact first-measured resident-state evidence."
        }
    }

    $validation = @(Import-Csv -LiteralPath $paths.validation)
    Assert-PolicyCsvColumns $validation @(
        'phase', 'caseId', 'passed', 'message', 'readbackBytes',
        'expectedOutputHash', 'actualOutputHash', 'expectedStateHash',
        'actualStateHash', 'invalidKeyCount', 'diagnosticFlags',
        'hierarchyStatisticsAvailable',
        'expectedCoarseVisibleClusterViewCount',
        'expectedCandidateInstanceViewCount',
        'expectedHierarchicalVisiblePairCount',
        'coarseVisibleClusterViewCount', 'candidateInstanceViewCount',
        'hierarchicalVisiblePairCount', 'clusterCount',
        'hierarchyCandidateBp', 'engineIndirectWordCount',
        'engineIndirectReadbackBytes', 'engineIndirectExpectedHash',
        'engineIndirectActualHash', 'engineIndirectMismatchCount',
        'engineIndirectExact', 'renderTargetFormat',
        'renderTargetWidth', 'renderTargetHeight',
        'renderTargetReadbackBytes', 'renderTargetHash',
        'renderTargetBlackReferenceHash',
        'renderTargetNonBlackPixelCount',
        'renderTargetHashConsistent', 'presentationValidationPassed',
        'presentationValidationMessage') "$root validation"
    if ($validation.Count -ne 4) {
        throw "$root must contain four sequential validation receipts."
    }
    $hierarchyCaseIds = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    foreach ($row in @($raw | Where-Object {
            [string]$_.decisionCullingMode -ceq 'Hierarchy'
        })) {
        [void]$hierarchyCaseIds.Add([string]$row.caseId)
    }
    $expectedValidationPhases = @(
        'warmup-left', 'warmup-right', 'final-left', 'final-right')
    $expectedValidationCases = @(
        [string]$config.leftCase, [string]$config.rightCase,
        [string]$config.leftCase, [string]$config.rightCase)
    $validationReadbackBytes = 0L
    $presentationReadbackBytes = 0L
    $minimumNonBlackPixels = [int64]::MaxValue
    $observedRenderTargetHash = $null
    for ($validationIndex = 0;
         $validationIndex -lt $validation.Count;
         $validationIndex++) {
        $row = $validation[$validationIndex]
        if ([string]$row.phase -cne
                $expectedValidationPhases[$validationIndex] -or
            [string]$row.caseId -cne
                $expectedValidationCases[$validationIndex]) {
            throw "$root validation order or case identity is not exact at row $validationIndex."
        }
        if (-not (ConvertTo-PolicyBoolean $row.passed) -or
            [int64]$row.readbackBytes -le 0 -or
            [string]::IsNullOrWhiteSpace([string]$row.message) -or
            [string]::IsNullOrWhiteSpace([string]$row.expectedOutputHash) -or
            [string]$row.expectedOutputHash -ceq 'unavailable' -or
            [string]$row.expectedOutputHash -cne [string]$row.actualOutputHash -or
            [string]::IsNullOrWhiteSpace([string]$row.expectedStateHash) -or
            [string]$row.expectedStateHash -ceq 'unavailable' -or
            [string]$row.expectedStateHash -cne [string]$row.actualStateHash -or
            [uint32]$row.invalidKeyCount -ne 0 -or
            [uint32]$row.diagnosticFlags -ne 0) {
            throw "$root has failed exact validation in phase '$($row.phase)'."
        }
        $statisticsAvailable =
            ConvertTo-PolicyBoolean $row.hierarchyStatisticsAvailable
        if ($hierarchyCaseIds.Contains([string]$row.caseId) -and
            -not $statisticsAvailable) {
            throw "$root omitted hierarchy statistics for '$($row.caseId)'."
        }
        if ($statisticsAvailable) {
            if (
                [uint32]$row.expectedCoarseVisibleClusterViewCount -ne
                    [uint32]$row.coarseVisibleClusterViewCount -or
                [uint32]$row.expectedCandidateInstanceViewCount -ne
                    [uint32]$row.candidateInstanceViewCount -or
                [uint32]$row.expectedHierarchicalVisiblePairCount -ne
                    [uint32]$row.hierarchicalVisiblePairCount) {
                throw "$root hierarchy statistics are not exact."
            }
        }

        $engineWordCount = [int64]$row.engineIndirectWordCount
        $engineReadbackBytes = [int64]$row.engineIndirectReadbackBytes
        $renderTargetWidth = [int64]$row.renderTargetWidth
        $renderTargetHeight = [int64]$row.renderTargetHeight
        $renderTargetReadbackBytes = [int64]$row.renderTargetReadbackBytes
        $nonBlackPixels = [int64]$row.renderTargetNonBlackPixelCount
        $engineExpectedHash = [string]$row.engineIndirectExpectedHash
        $engineActualHash = [string]$row.engineIndirectActualHash
        $renderTargetHash = [string]$row.renderTargetHash
        $blackReferenceHash = [string]$row.renderTargetBlackReferenceHash
        $rowPresentationBytes = [int64](
            $engineReadbackBytes + $renderTargetReadbackBytes)
        if ($engineWordCount -le 0L -or
            $engineReadbackBytes -ne ($engineWordCount * 8L) -or
            [string]::IsNullOrWhiteSpace($engineExpectedHash) -or
            $engineExpectedHash -ceq 'unavailable' -or
            $engineExpectedHash -cne $engineActualHash -or
            [int64]$row.engineIndirectMismatchCount -ne 0L -or
            -not (ConvertTo-PolicyBoolean $row.engineIndirectExact) -or
            [string]$row.renderTargetFormat -cne 'RGBA32' -or
            $renderTargetWidth -ne 512L -or
            $renderTargetHeight -ne 512L -or
            $renderTargetReadbackBytes -ne 1048576L -or
            [string]::IsNullOrWhiteSpace($renderTargetHash) -or
            $renderTargetHash -ceq 'unavailable' -or
            [string]::IsNullOrWhiteSpace($blackReferenceHash) -or
            $blackReferenceHash -ceq 'unavailable' -or
            $renderTargetHash -ceq $blackReferenceHash -or
            $nonBlackPixels -le 0L -or
            $nonBlackPixels -gt ($renderTargetWidth * $renderTargetHeight) -or
            -not (ConvertTo-PolicyBoolean $row.renderTargetHashConsistent) -or
            -not (ConvertTo-PolicyBoolean $row.presentationValidationPassed) -or
            [string]::IsNullOrWhiteSpace(
                [string]$row.presentationValidationMessage) -or
            [int64]$row.readbackBytes -le $rowPresentationBytes) {
            throw "$root has incomplete engine-presentation validation in phase '$($row.phase)'."
        }
        if ($null -eq $observedRenderTargetHash) {
            $observedRenderTargetHash = $renderTargetHash
        }
        elseif ($renderTargetHash -cne $observedRenderTargetHash) {
            throw "$root render-target hashes are not deterministic."
        }
        $validationReadbackBytes = [int64](
            $validationReadbackBytes + [int64]$row.readbackBytes)
        $presentationReadbackBytes = [int64](
            $presentationReadbackBytes + $rowPresentationBytes)
        $minimumNonBlackPixels = [Math]::Min(
            $minimumNonBlackPixels, $nonBlackPixels)
    }
    if ([int64](Get-RequiredPolicyMapValue `
            $summary 'validationReadbackBytes' $root) -ne
            $validationReadbackBytes -or
        [int64](Get-RequiredPolicyMapValue `
            $summary 'presentationValidationReadbackBytes' $root) -ne
            $presentationReadbackBytes -or
        [int64](Get-RequiredPolicyMapValue `
            $summary 'minimumRenderTargetNonBlackPixels' $root) -ne
            $minimumNonBlackPixels -or
        $deterministicRenderTargetHash -cne $observedRenderTargetHash) {
        throw "$root validation aggregate receipts do not match validation.csv."
    }

    $selectorRows = @(Import-Csv -LiteralPath $paths.selector)
    if ($selectorRows.Count -ne 1 -or
        [int]$selectorRows[0].iterations -ne 100000 -or
        [int64]$selectorRows[0].allocatedBytes -ne 0 -or
        [int]$selectorRows[0].unstableDecisionCount -ne 0) {
        throw "$root selector microbenchmark is incomplete."
    }
    if ($RequireAcceptedProfile -and
        ([uint32]$selectorRows[0].flags -ne 0 -or
         -not (ConvertTo-PolicyBoolean $selectorRows[0].profileAccepted))) {
        throw "$root selector microbenchmark used fallback flags."
    }

    $evidenceFiles = [IO.FileInfo[]]@(
        $paths.Values | ForEach-Object { Get-Item -LiteralPath $_ })
    $evidenceHash = Get-PolicyCombinedSha256 $evidenceFiles $root
    return [pscustomobject][ordered]@{
        directory = $root
        evidenceSha256 = $evidenceHash
        summary = $summary
        config = $config
        device = $device
        raw = $raw
        blocks = $blocks
        validation = $validation
        selectorOverhead = $selectorRows[0]
    }
}

function Test-PolicyCandidateGate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Evidence,
        [Parameter(Mandatory = $true)][string]$BaselineCaseId,
        [Parameter(Mandatory = $true)][string]$CandidateCaseId,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Upload', 'Hierarchy')][string]$CandidateKind
    )

    $comparisons = [ordered]@{}
    foreach ($metric in @(
            'totalCpuMs', 'gpuRegionElapsedMs', 'cpuFrameMs',
            'cpuMainThreadFrameMs', 'cpuRenderThreadFrameMs',
            'gpuFrameMs', 'cpuSubmissionWindowMs')) {
        $comparisons[$metric] = Get-PolicyPairedComparison `
            -Rows $Evidence.raw `
            -BaselineCaseId $BaselineCaseId `
            -CandidateCaseId $CandidateCaseId `
            -Metric $metric
    }
    $primaryName = if ($CandidateKind -ceq 'Hierarchy') {
        'gpuRegionElapsedMs'
    }
    else {
        'totalCpuMs'
    }
    $primary = $comparisons[$primaryName]
    $failures = [Collections.Generic.List[string]]::new()
    if ($primary.meanImprovementPercent -lt 2.0) {
        $failures.Add('primary-mean-improvement-below-2-percent')
    }
    if ($primary.positiveWinPercent -lt 55.0) {
        $failures.Add('primary-positive-win-rate-below-55-percent')
    }
    if ($primary.p95RegressionPercent -gt 5.0) {
        $failures.Add('primary-p95-regression-above-5-percent')
    }
    if ($primary.p99RegressionPercent -gt 10.0) {
        $failures.Add('primary-p99-regression-above-10-percent')
    }

    foreach ($guard in @(
            @('totalCpuMs', 10.0, 0.05),
            @('gpuRegionElapsedMs', 5.0, 0.05),
            @('cpuFrameMs', 5.0, 0.25),
            @('cpuMainThreadFrameMs', 5.0, 0.25),
            @('cpuRenderThreadFrameMs', 10.0, 0.25),
            @('gpuFrameMs', 5.0, 0.25),
            @('cpuSubmissionWindowMs', 10.0, 0.10))) {
        $comparison = $comparisons[$guard[0]]
        $absoluteP99 = $comparison.candidate.p99 - $comparison.baseline.p99
        if ($comparison.p99RegressionPercent -gt [double]$guard[1] -and
            $absoluteP99 -gt [double]$guard[2]) {
            $failures.Add($guard[0] + '-material-p99-regression')
        }
    }

    return [pscustomobject][ordered]@{
        accepted = $failures.Count -eq 0
        candidateKind = $CandidateKind
        baselineCaseId = $BaselineCaseId
        candidateCaseId = $CandidateCaseId
        primaryMetric = $primaryName
        thresholds = [ordered]@{
            minimumMeanImprovementPercent = 2.0
            minimumPositiveWinPercent = 55.0
            maximumPrimaryP95RegressionPercent = 5.0
            maximumPrimaryP99RegressionPercent = 10.0
            tailGuardRequiresBothRelativeAndAbsoluteBreach = $true
        }
        failures = [string[]]$failures.ToArray()
        comparisons = $comparisons
    }
}

function Test-PolicyReplayEquivalence {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Evidence,
        [string]$ForcedCaseId = 'gpu-driven-policy/forced-selected',
        [string]$AutoCaseId = 'gpu-driven-policy/actual-auto'
    )

    $rows = [object[]]$Evidence.raw
    $forcedByKey = @{}
    foreach ($row in @($rows | Where-Object {
            [string]$_.caseId -ceq $ForcedCaseId
        })) {
        $key = ([string]$row.pairIndex) + ':' + ([string]$row.sampleIndex)
        $forcedByKey[$key] = $row
    }
    $mismatchCount = 0
    $autoRows = @($rows | Where-Object {
        [string]$_.caseId -ceq $AutoCaseId
    })
    foreach ($auto in $autoRows) {
        $key = ([string]$auto.pairIndex) + ':' + ([string]$auto.sampleIndex)
        if (-not $forcedByKey.ContainsKey($key)) {
            $mismatchCount++
            continue
        }
        $forced = $forcedByKey[$key]
        if ([string]$forced.decisionSource -cne 'ForcedSelected' -or
            (ConvertTo-PolicyBoolean $forced.selectorInvoked) -or
            [string]$auto.decisionSource -cne 'ActualAuto' -or
            -not (ConvertTo-PolicyBoolean $auto.selectorInvoked)) {
            $mismatchCount++
            continue
        }
        foreach ($property in @(
                'decisionUploadMode', 'decisionOutputMode',
                'decisionCullingMode', 'decisionPrimitiveBackend',
                'decisionProfileRuleIndex', 'decisionRuleId',
                'decisionFlags', 'decisionAccepted')) {
            if ([string]$auto.PSObject.Properties[$property].Value -cne
                [string]$forced.PSObject.Properties[$property].Value) {
                $mismatchCount++
                break
            }
        }
    }
    if ($autoRows.Count -eq 0 -or $autoRows.Count -ne $forcedByKey.Count) {
        $mismatchCount++
    }

    $comparisons = [ordered]@{}
    $materialTailFailures = [Collections.Generic.List[string]]::new()
    foreach ($guard in @(
            @('totalCpuMs', 10.0, 0.05),
            @('gpuRegionElapsedMs', 5.0, 0.05),
            @('cpuFrameMs', 5.0, 0.25),
            @('cpuMainThreadFrameMs', 5.0, 0.25),
            @('cpuRenderThreadFrameMs', 10.0, 0.25),
            @('gpuFrameMs', 5.0, 0.25),
            @('cpuSubmissionWindowMs', 10.0, 0.10))) {
        $comparison = Get-PolicyPairedComparison $rows `
            $ForcedCaseId $AutoCaseId $guard[0]
        $comparisons[$guard[0]] = $comparison
        $absoluteP99 = $comparison.candidate.p99 - $comparison.baseline.p99
        if ($comparison.p99RegressionPercent -gt [double]$guard[1] -and
            $absoluteP99 -gt [double]$guard[2]) {
            $materialTailFailures.Add($guard[0] + '-material-p99-regression')
        }
    }
    return [pscustomobject][ordered]@{
        accepted = $mismatchCount -eq 0 -and
            $materialTailFailures.Count -eq 0
        decisionMismatchCount = $mismatchCount
        materialTailFailures = [string[]]$materialTailFailures.ToArray()
        selectorCpuMs = [ordered]@{
            forcedSelected = Get-PolicyMetricSummary (
                Get-PolicyMetricValues `
                    -Rows @($rows | Where-Object {
                        [string]$_.caseId -ceq $ForcedCaseId
                    }) `
                    -Property selectorCpuMs)
            actualAuto = Get-PolicyMetricSummary (
                Get-PolicyMetricValues `
                    -Rows $autoRows `
                    -Property selectorCpuMs)
        }
        comparisons = $comparisons
    }
}

function Test-PolicyEndToEndReplay {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Evidence,
        [string]$BaselineCaseId =
            'gpu-driven-policy/calibration/full-flat',
        [string]$AutoCaseId = 'gpu-driven-policy/actual-auto',
        [Parameter(Mandatory = $true)]
        [ValidateSet('Upload', 'Hierarchy')][string]$CandidateKind,
        [Parameter(Mandatory = $true)][bool]$CandidateAccepted,
        [Parameter(Mandatory = $true)][string]$ExpectedRuleId,
        [Parameter(Mandatory = $true)][string]$ExpectedUploadMode,
        [Parameter(Mandatory = $true)][string]$ExpectedOutputMode,
        [Parameter(Mandatory = $true)][string]$ExpectedCullingMode,
        [string]$ExpectedPrimitiveBackend = 'Portable'
    )

    $rows = [object[]]$Evidence.raw
    $baselineRows = @($rows | Where-Object {
        [string]$_.caseId -ceq $BaselineCaseId
    })
    $autoRows = @($rows | Where-Object {
        [string]$_.caseId -ceq $AutoCaseId
    })
    $decisionMismatchCount = 0
    if ($baselineRows.Count -eq 0 -or
        $baselineRows.Count -ne $autoRows.Count) {
        $decisionMismatchCount++
    }
    foreach ($baseline in $baselineRows) {
        if ([string]$baseline.decisionUploadMode -cne 'Full' -or
            [string]$baseline.decisionOutputMode -cne $ExpectedOutputMode -or
            [string]$baseline.decisionCullingMode -cne 'Flat' -or
            [string]$baseline.decisionPrimitiveBackend -cne 'Portable' -or
            [string]$baseline.decisionSource -cne 'ForcedCalibration' -or
            (ConvertTo-PolicyBoolean $baseline.selectorInvoked) -or
            [uint32]$baseline.decisionFlags -ne 0 -or
            -not (ConvertTo-PolicyBoolean $baseline.decisionAccepted) -or
            -not (ConvertTo-PolicyBoolean `
                $baseline.decisionStableExpected)) {
            $decisionMismatchCount++
        }
    }
    foreach ($auto in $autoRows) {
        if ([string]$auto.decisionUploadMode -cne $ExpectedUploadMode -or
            [string]$auto.decisionOutputMode -cne $ExpectedOutputMode -or
            [string]$auto.decisionCullingMode -cne $ExpectedCullingMode -or
            [string]$auto.decisionPrimitiveBackend -cne
                $ExpectedPrimitiveBackend -or
            [string]$auto.decisionRuleId -cne $ExpectedRuleId -or
            [int]$auto.decisionProfileRuleIndex -lt 0 -or
            [string]$auto.decisionSource -cne 'ActualAuto' -or
            -not (ConvertTo-PolicyBoolean $auto.selectorInvoked) -or
            [uint32]$auto.decisionFlags -ne 0 -or
            -not (ConvertTo-PolicyBoolean $auto.decisionAccepted) -or
            -not (ConvertTo-PolicyBoolean $auto.decisionStableExpected)) {
            $decisionMismatchCount++
        }
    }

    $performanceGate = Test-PolicyCandidateGate `
        -Evidence $Evidence `
        -BaselineCaseId $BaselineCaseId `
        -CandidateCaseId $AutoCaseId `
        -CandidateKind $CandidateKind
    $materialTailFailures = @($performanceGate.failures | Where-Object {
        $_ -like '*-material-p99-regression'
    })
    $safeRejectedDecision =
        $ExpectedUploadMode -ceq 'Full' -and
        $ExpectedCullingMode -ceq 'Flat' -and
        $ExpectedPrimitiveBackend -ceq 'Portable'
    $performanceAccepted = if ($CandidateAccepted) {
        [bool]$performanceGate.accepted
    }
    else {
        $safeRejectedDecision -and $materialTailFailures.Count -eq 0
    }
    return [pscustomobject][ordered]@{
        accepted = $decisionMismatchCount -eq 0 -and $performanceAccepted
        candidateAccepted = $CandidateAccepted
        decisionMismatchCount = $decisionMismatchCount
        safeRejectedDecision = $safeRejectedDecision
        materialTailFailures = [string[]]$materialTailFailures
        requiredGate = if ($CandidateAccepted) {
            'candidate-performance-and-material-tail'
        }
        else { 'safe-full-flat-portable-and-material-tail' }
        performanceGate = $performanceGate
    }
}

Export-ModuleMember -Function @(
    'Get-PolicyTextSha256',
    'Test-PolicySha256Equal',
    'Get-PolicyCombinedSha256',
    'Read-PolicyKeyValueFile',
    'Get-RequiredPolicyMapValue',
    'ConvertTo-PolicyBoolean',
    'ConvertTo-PolicyDouble',
    'Assert-PolicyCsvColumns',
    'Get-PolicyPercentile',
    'Get-PolicyMetricSummary',
    'Get-PolicyRegressionPercent',
    'Get-PolicyImprovementPercent',
    'Get-PolicyMetricValues',
    'Get-PolicyPairedComparison',
    'Get-PolicyNUnitReceipt',
    'Assert-PolicyPreflightBinding',
    'Assert-PolicySchedule',
    'Assert-PolicyBenchmarkEvidence',
    'Test-PolicyCandidateGate',
    'Test-PolicyReplayEquivalence',
    'Test-PolicyEndToEndReplay')
