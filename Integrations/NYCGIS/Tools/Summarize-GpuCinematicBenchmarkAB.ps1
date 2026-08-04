[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InputDirectory,

    [ValidateRange(30.0, 80.0)]
    [double]$MinimumPsnrDb = 40.0,

    [ValidateRange(0.0, 0.1)]
    [double]$MaximumFractionOver5 = 0.01
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$inputRoot = (Resolve-Path -LiteralPath $InputDirectory).Path

$imageComparerSource = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public sealed class CinematicImageComparisonResult
{
    public int Width;
    public int Height;
    public double MeanAbsoluteError;
    public double PsnrDb;
    public double FractionOver5;
}

public static class CinematicImageComparer
{
    public static CinematicImageComparisonResult Compare(
        string leftPath,
        string rightPath)
    {
        using (Bitmap leftSource = new Bitmap(leftPath))
        using (Bitmap rightSource = new Bitmap(rightPath))
        {
            if (leftSource.Width != rightSource.Width ||
                leftSource.Height != rightSource.Height)
            {
                throw new InvalidOperationException(
                    "Image dimensions differ.");
            }

            int width = leftSource.Width;
            int height = leftSource.Height;
            using (Bitmap left = ConvertToRgb(leftSource))
            using (Bitmap right = ConvertToRgb(rightSource))
            {
                Rectangle rect = new Rectangle(0, 0, width, height);
                BitmapData leftData = left.LockBits(
                    rect,
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format24bppRgb);
                BitmapData rightData = right.LockBits(
                    rect,
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format24bppRgb);
                try
                {
                    int leftStride = Math.Abs(leftData.Stride);
                    int rightStride = Math.Abs(rightData.Stride);
                    byte[] leftBytes = new byte[leftStride * height];
                    byte[] rightBytes = new byte[rightStride * height];
                    Marshal.Copy(
                        leftData.Scan0,
                        leftBytes,
                        0,
                        leftBytes.Length);
                    Marshal.Copy(
                        rightData.Scan0,
                        rightBytes,
                        0,
                        rightBytes.Length);

                    double absoluteSum = 0.0;
                    double squaredSum = 0.0;
                    long pixelsOver5 = 0;
                    for (int y = 0; y < height; y++)
                    {
                        int leftRow = y * leftStride;
                        int rightRow = y * rightStride;
                        for (int x = 0; x < width; x++)
                        {
                            int leftPixel = leftRow + x * 3;
                            int rightPixel = rightRow + x * 3;
                            int maxDifference = 0;
                            for (int channel = 0; channel < 3; channel++)
                            {
                                int difference = Math.Abs(
                                    leftBytes[leftPixel + channel] -
                                    rightBytes[rightPixel + channel]);
                                absoluteSum += difference;
                                squaredSum += difference * difference;
                                maxDifference = Math.Max(
                                    maxDifference,
                                    difference);
                            }
                            if (maxDifference > 5)
                            {
                                pixelsOver5++;
                            }
                        }
                    }

                    double channelCount = width * (double)height * 3.0;
                    double meanSquaredError = squaredSum / channelCount;
                    return new CinematicImageComparisonResult
                    {
                        Width = width,
                        Height = height,
                        MeanAbsoluteError = absoluteSum / channelCount,
                        PsnrDb = meanSquaredError <= 0.0
                            ? 99.0
                            : 20.0 * Math.Log10(
                                255.0 / Math.Sqrt(meanSquaredError)),
                        FractionOver5 = pixelsOver5 /
                            (double)(width * height)
                    };
                }
                finally
                {
                    left.UnlockBits(leftData);
                    right.UnlockBits(rightData);
                }
            }
        }
    }

    private static Bitmap ConvertToRgb(Bitmap source)
    {
        Bitmap result = new Bitmap(
            source.Width,
            source.Height,
            PixelFormat.Format24bppRgb);
        using (Graphics graphics = Graphics.FromImage(result))
        {
            graphics.DrawImageUnscaled(source, 0, 0);
        }
        return result;
    }
}
'@

Add-Type -AssemblyName System.Drawing
if (-not ('CinematicImageComparer' -as [type])) {
    Add-Type -TypeDefinition $imageComparerSource -ReferencedAssemblies System.Drawing
}

$reportItems = @(
    Get-ChildItem -LiteralPath $inputRoot -Filter '*-r*.json' -File |
    ForEach-Object {
        if ($_.BaseName -notmatch '^(baseline|optimized)-r([0-9]+)$') {
            return
        }
        [pscustomobject]@{
            File = $_
            Variant = $Matches[1]
            Round = [int]$Matches[2]
            Report = Get-Content -LiteralPath $_.FullName -Raw |
                ConvertFrom-Json
        }
    }
)
if ($reportItems.Count -lt 2) {
    throw "At least one baseline and one optimized report are required."
}
$invalidSchemas = @(
    $reportItems.Report |
    Where-Object { $_.schemaVersion -ne 5 })
if ($invalidSchemas.Count -gt 0) {
    throw "Exact cinematic report schema v5 is required; found " +
        "$($invalidSchemas.Count) incompatible report(s)."
}
$rounds = @($reportItems.Round | Select-Object -Unique | Sort-Object)
foreach ($round in $rounds) {
    $roundReports = @($reportItems | Where-Object Round -eq $round)
    $baselineCount = @($roundReports |
        Where-Object Variant -eq 'baseline').Count
    $optimizedCount = @($roundReports |
        Where-Object Variant -eq 'optimized').Count
    if ($roundReports.Count -ne 2 -or $baselineCount -ne 1 -or
        $optimizedCount -ne 1) {
        throw "Round $round must contain exactly one baseline and one optimized report."
    }
}
if ($reportItems.Count -ne (2 * $rounds.Count)) {
    throw 'Cinematic report discovery contained duplicate or unpaired runs.'
}
$cameraCountValues = @(
    $reportItems.Report |
    Select-Object -ExpandProperty cameraCount -Unique)
$supportedCameraProfile = $cameraCountValues.Count -eq 1 -and
    ($cameraCountValues[0] -eq 1 -or $cameraCountValues[0] -eq 4)
$benchmarkCameraCount = if ($supportedCameraProfile) {
    [int]$cameraCountValues[0]
} else {
    0
}
$expectedCityCullCameraMode = if ($benchmarkCameraCount -eq 1) {
    'ExplicitHeroOnly'
} else {
    'ExplicitHeroPlusPayloadUnion'
}

function Get-Median {
    param([double[]]$Values)
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return 0.0 }
    $middle = [int][Math]::Floor($sorted.Count / 2)
    if (($sorted.Count % 2) -eq 1) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2.0
}

function Get-InterpolatedPercentile {
    param([double[]]$Values, [double]$Percentile)
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return 0.0 }
    $position = [Math]::Max(0.0, [Math]::Min(1.0, $Percentile)) *
        ($sorted.Count - 1)
    $lower = [int][Math]::Floor($position)
    $upper = [int][Math]::Ceiling($position)
    if ($lower -eq $upper) { return [double]$sorted[$lower] }
    $weight = $position - $lower
    return ([double]$sorted[$lower] * (1.0 - $weight)) +
        ([double]$sorted[$upper] * $weight)
}

function Get-VariantMetric {
    param([string]$Variant, [string]$Property)
    [double[]]$values = @(
        $reportItems |
        Where-Object { $_.Variant -eq $Variant } |
        ForEach-Object { [double]($_.Report.$Property) })
    return Get-Median -Values $values
}

function Get-Improvement {
    param([double]$Baseline, [double]$Optimized)
    if ($Baseline -eq 0.0) { return 0.0 }
    return ($Baseline - $Optimized) / $Baseline * 100.0
}

$functionalProperties = @(
    'sensorOutputHash',
    'residencyOutputHash',
    'deadlineOutputHash'
)
$functionalHashesMatch = $true
foreach ($property in $functionalProperties) {
    $unique = @(
        $reportItems.Report |
        Select-Object -ExpandProperty $property -Unique)
    if ($unique.Count -ne 1) {
        $functionalHashesMatch = $false
    }
}

$qualityPassed = @(
    $reportItems.Report |
    Where-Object { -not $_.qualityPassed }).Count -eq 0
$dimensionsMatch = @(
    $reportItems.Report |
    ForEach-Object { "$($_.outputWidth)x$($_.outputHeight)" } |
    Select-Object -Unique).Count -eq 1
$residentPacksMatch = @(
    $reportItems.Report |
    Select-Object -ExpandProperty cityResidentPacks -Unique).Count -eq 1
$fullResidentPackSetValid = @(
    $reportItems.Report |
    Where-Object {
        $_.cityResidentPacks -le 0 -or
        $_.cityResidentPacks -ne $_.cityTotalPacks
    }).Count -eq 0
$residentBytesMatch = @(
    $reportItems.Report |
    Select-Object -ExpandProperty cityResidentGpuBytes -Unique).Count -eq 1
$finalLoadingClear = @(
    $reportItems.Report |
    Where-Object { $_.cityLoadingPacks -ne 0 }).Count -eq 0
$cityValidationStable = @(
    $reportItems.Report |
    Where-Object {
        -not $_.cityValidationStable -or
        -not $_.cityValidationFreshDispatches -or
        $_.cityValidationMode -ne
            'ForcedHeroFreshPrimaryPassConverged3' -or
        $_.cityValidationSamples -lt 3 -or
        $_.cityValidationSamples -gt 30 -or
        $_.cityValidationFirstPassEpoch -le 0 -or
        $_.cityValidationLastPassEpoch -le
            $_.cityValidationFirstPassEpoch -or
        $_.cityValidationPreparedCameraCount -ne 1 -or
        $_.cityValidationDispatchedPackCount -ne
            $_.cityResidentPacks -or
        -not $_.cityValidationPackSetComplete -or
        [string]::IsNullOrWhiteSpace(
            [string]$_.cityValidationCameraStateHash) -or
        [string]::IsNullOrWhiteSpace(
            [string]$_.cityValidationDispatchedPackHash)
    }).Count -eq 0
$cityOracleTopologyMatches = @(
    $reportItems.Report |
    ForEach-Object {
        "$($_.cityValidationPreparedCameraCount)|" +
        "$($_.cityValidationCameraStateHash)|" +
        "$($_.cityValidationDispatchedPackCount)|" +
        "$($_.cityValidationDispatchedPackHash)"
    } |
    Select-Object -Unique).Count -eq 1
$explicitCullCamerasValid = $supportedCameraProfile -and
    @(
    $reportItems.Report |
    Where-Object {
        $_.rendererCount -ne 1 -or
        $_.cameraCount -ne $benchmarkCameraCount -or
        $_.cityCullCameraMode -ne $expectedCityCullCameraMode
    }).Count -eq 0
$payloadCadenceValid = @(
    $reportItems.Report |
    Where-Object {
        $expectedPayload =
            ([int]$_.cameraCount - 1) * [int]$_.sampleFrames
        $_.payloadRenderMode -ne
            'QueuedExplicitCamerasAfterBfp2LateUpdateSrpVerified' -or
        -not $_.payloadRenderCountValid -or
        $_.measuredPayloadRenderCount -ne $expectedPayload -or
        $_.expectedPayloadRenderCount -ne $expectedPayload
    }).Count -eq 0
$prePayloadCitySubmissionValid = @(
    $reportItems.Report |
    Where-Object {
        -not $_.prePayloadCitySubmissionValid -or
        $_.measuredPrePayloadCitySubmissionCount -ne $_.sampleFrames -or
        $_.expectedPrePayloadCitySubmissionCount -ne $_.sampleFrames
    }).Count -eq 0
$cameraRenderOrderValid = @(
    $reportItems.Report |
    Where-Object {
        -not $_.cameraRenderOrderValid -or
        $_.measuredPayloadSrpRenderCount -ne
            $_.expectedPayloadRenderCount -or
        $_.expectedPayloadSrpRenderCount -ne
            $_.expectedPayloadRenderCount -or
        $_.measuredHeroSrpRenderCount -ne $_.sampleFrames -or
        $_.expectedHeroSrpRenderCount -ne $_.sampleFrames
    }).Count -eq 0
$workloadSubmissionCadenceValid = @(
    $reportItems.Report |
    Where-Object {
        $_.schemaVersion -lt 4 -or
        -not $_.workloadSubmissionCadenceValid -or
        $_.workloadIssueIntervalFrames -le 0 -or
        $_.expectedWorkloadSubmissions -le 0 -or
        $_.scheduledWorkloadIssueCount -ne
            $_.expectedWorkloadSubmissions -or
        $_.sensorSubmitted -ne $_.expectedWorkloadSubmissions -or
        $_.residencySubmitted -ne $_.expectedWorkloadSubmissions -or
        $_.deadlineSubmitted -ne $_.expectedWorkloadSubmissions -or
        $_.sensorDropped -ne 0 -or
        $_.residencyDropped -ne 0 -or
        $_.deadlineDropped -ne 0 -or
        -not $_.measurementWorkloadsDrained
    }).Count -eq 0
$workloadSequenceCoverageValid = @(
    $reportItems.Report |
    Where-Object {
        $expectedUnique = [Math]::Min(
            [int]$_.expectedWorkloadSubmissions,
            [int]$_.workloadLogicalStateCount)
        $_.workloadUniqueLogicalStates -ne $expectedUnique -or
        [string]::IsNullOrWhiteSpace(
            [string]$_.workloadIssueFrameSequenceHash) -or
        [string]::IsNullOrWhiteSpace(
            [string]$_.workloadLogicalStateSequenceHash)
    }).Count -eq 0
$warmupSubmissionCadenceValid = @(
    $reportItems.Report |
    Where-Object {
        $expectedWarmupUnique = [Math]::Min(
            [int]$_.warmupExpectedWorkloadSubmissions,
            [int]$_.workloadLogicalStateCount)
        -not $_.warmupSubmissionCadenceValid -or
        $_.warmupScheduledWorkloadIssueCount -ne
            $_.warmupExpectedWorkloadSubmissions -or
        $_.warmupSensorSubmitted -ne
            $_.warmupExpectedWorkloadSubmissions -or
        $_.warmupResidencySubmitted -ne
            $_.warmupExpectedWorkloadSubmissions -or
        $_.warmupDeadlineSubmitted -ne
            $_.warmupExpectedWorkloadSubmissions -or
        $_.warmupSensorDropped -ne 0 -or
        $_.warmupResidencyDropped -ne 0 -or
        $_.warmupDeadlineDropped -ne 0 -or
        $_.warmupUniqueLogicalStates -ne $expectedWarmupUnique
    }).Count -eq 0
$workloadSubmissionCadenceMatches = @(
    $reportItems.Report |
    ForEach-Object {
        "$($_.workloadIssueIntervalFrames)|" +
        "$($_.expectedWorkloadSubmissions)|" +
        "$($_.scheduledWorkloadIssueCount)|" +
        "$($_.sensorSubmitted)|$($_.residencySubmitted)|" +
        "$($_.deadlineSubmitted)|" +
        "$($_.workloadIssueFrameSequenceHash)|" +
        "$($_.workloadLogicalStateSequenceHash)|" +
        "$($_.warmupExpectedWorkloadSubmissions)|" +
        "$($_.warmupScheduledWorkloadIssueCount)|" +
        "$($_.warmupSensorSubmitted)|" +
        "$($_.warmupResidencySubmitted)|" +
        "$($_.warmupDeadlineSubmitted)|" +
        "$($_.warmupIssueFrameSequenceHash)|" +
        "$($_.warmupLogicalStateSequenceHash)"
    } |
    Select-Object -Unique).Count -eq 1
$sampleCompletenessValid = @(
    $reportItems.Report |
    Where-Object {
        -not $_.sampleCompletenessValid -or
        $_.sampleFrames -ne $_.configuredMaxSampleFrames
    }).Count -eq 0
$gpuTimingCoverageValid = @(
    $reportItems.Report |
    Where-Object {
        -not $_.gpuTimingCoverageValid -or
        -not $_.heroTimestampEvidenceComplete -or
        $_.gpuTimingMode -ne 'NativeDx12DirectQueueTimestamp' -or
        $_.gpuTimingScopeVersion -ne
            'SplitDirectQueueBegin_CityCull_ExplicitCameraGroup_End_v1' -or
        $_.gpuTimingQueue -ne 'D3D12Direct' -or
        $_.graphicsApi -ne 'Direct3D12' -or
        $_.gpuTimingPrimeFrames -ne 8 -or
        $_.gpuTimingValidSamples -ne $_.sampleFrames -or
        $_.frameTimingGpuSamples -ne 0 -or
        $_.profilerGpuSamples -ne 0 -or
        -not $_.nativeTimestampBackendAvailable -or
        $_.nativeTimestampAbiVersion -ne 2 -or
        (([uint32]$_.nativeTimestampCapabilityFlags -band 0x1F) -ne 0x1F) -or
        $_.nativeTimestampRingCapacity -le 0 -or
        $_.nativeTimestampPreparedScopes -le 0 -or
        $_.nativeTimestampPreparedScopes -gt $_.nativeTimestampRingCapacity -or
        $_.nativeTimestampObservedFrequency -le 0 -or
        $_.nativeTimestampDeviceGeneration -le 0 -or
        ([string]$_.nativeTimestampDllSha256).Length -ne 64 -or
        $_.nativeTimestampWarmupPairs -ne 8 -or
        -not $_.nativeTimestampWarmupPassed -or
        -not $_.nativeTimestampOrderingDiscriminatorPassed -or
        $_.nativeTimestampWarmupControlSubmitted -ne 8 -or
        $_.nativeTimestampWarmupControlReady -ne 8 -or
        $_.nativeTimestampWarmupControlValid -ne 8 -or
        $_.nativeTimestampWarmupHeroSubmitted -ne 8 -or
        $_.nativeTimestampWarmupHeroReady -ne 8 -or
        $_.nativeTimestampWarmupHeroValid -ne 8 -or
        $_.nativeTimestampWarmupHeroP50Ms -le
            $_.nativeTimestampWarmupControlP99Ms -or
        $_.nativeTimestampExpectedHeroSamples -ne $_.sampleFrames -or
        $_.nativeTimestampHeroSubmitted -ne $_.sampleFrames -or
        $_.nativeTimestampHeroReady -ne $_.sampleFrames -or
        $_.nativeTimestampHeroValid -ne $_.sampleFrames -or
        $_.nativeTimestampExpectedControlSamples -ne
            [int][Math]::Ceiling($_.sampleFrames / 8.0) -or
        $_.nativeTimestampControlSubmitted -ne
            $_.nativeTimestampExpectedControlSamples -or
        $_.nativeTimestampControlReady -ne
            $_.nativeTimestampExpectedControlSamples -or
        $_.nativeTimestampControlValid -ne
            $_.nativeTimestampExpectedControlSamples -or
        $_.nativeTimestampAcquireFailures -ne 0 -or
        $_.nativeTimestampPreparedScopeFailures -ne 0 -or
        $_.nativeTimestampResultFailures -ne 0 -or
        $_.nativeTimestampTimeouts -ne 0 -or
        $_.nativeTimestampFinalPendingSamples -ne 0 -or
        $_.nativeTimestampFinalActiveSamples -ne 0 -or
        $_.nativeTimestampFinalReservedSamples -ne 0 -or
        $_.nativeTimestampFinalSubmittedSamples -ne 0 -or
        $_.nativeTimestampTerminal -or
        $_.nativeTimestampInstrumentationReadbackBytes -ne
            (16 * ($_.sampleFrames +
                $_.nativeTimestampExpectedControlSamples))
    }).Count -eq 0
$nativeCompatibilitySignatures = @(
    $reportItems.Report | ForEach-Object {
        "$($_.graphicsDeviceName)|$($_.graphicsDeviceVendor)|" +
        "$($_.graphicsApi)|$($_.graphicsVersion)|$($_.unityVersion)|" +
        "$($_.gpuTimingMode)|$($_.gpuTimingScopeVersion)|" +
        "$($_.gpuTimingQueue)|$($_.nativeTimestampAbiVersion)|" +
        "$($_.nativeTimestampCapabilityFlags)|" +
        "$($_.nativeTimestampRendererType)|" +
        "$($_.nativeTimestampDeviceGeneration)|" +
        "$($_.nativeTimestampObservedFrequency)|" +
        "$($_.nativeTimestampDllSha256)"
    } | Select-Object -Unique)
$nativeTimestampCompatibilityValid =
    $nativeCompatibilitySignatures.Count -eq 1
$nativeTimestampEvidenceValid = $true
foreach ($item in $reportItems) {
    try {
        $timestampPath = Join-Path $inputRoot `
            ($item.File.BaseName + '.timestamps.csv')
        if (-not (Test-Path -LiteralPath $timestampPath -PathType Leaf) -or
            [System.IO.Path]::GetFullPath([string]$item.Report.rawTimestampsPath) -ne
                [System.IO.Path]::GetFullPath($timestampPath)) {
            throw 'missing or mismatched timestamp evidence path'
        }
        $rows = @(Import-Csv -LiteralPath $timestampPath)
        $expectedControls = [int][Math]::Ceiling(
            [int]$item.Report.sampleFrames / 8.0)
        if ($rows.Count -ne (16 + [int]$item.Report.sampleFrames +
                $expectedControls) -or
            @($rows | Where-Object {
                $_.status -ne 'Ready' -or [int]$_.valid -ne 1
            }).Count -ne 0 -or
            @($rows.token | Select-Object -Unique).Count -ne $rows.Count -or
            @($rows.userTag | Select-Object -Unique).Count -ne $rows.Count) {
            throw 'row completeness or identity validation failed'
        }
        $warmupControls = @($rows |
            Where-Object kind -eq 'WarmupControl' |
            Sort-Object { [int]$_.sampleIndex })
        $warmupHeroes = @($rows |
            Where-Object kind -eq 'WarmupHero' |
            Sort-Object { [int]$_.sampleIndex })
        $controls = @($rows |
            Where-Object kind -eq 'MeasurementControl' |
            Sort-Object { [int]$_.sampleIndex })
        $heroes = @($rows |
            Where-Object kind -eq 'MeasurementHero' |
            Sort-Object { [int]$_.sampleIndex })
        if ($warmupControls.Count -ne 8 -or $warmupHeroes.Count -ne 8 -or
            $controls.Count -ne $expectedControls -or
            $heroes.Count -ne [int]$item.Report.sampleFrames) {
            throw 'phase/kind counts failed'
        }
        for ($index = 0; $index -lt $heroes.Count; $index++) {
            if ([int]$heroes[$index].sampleIndex -ne $index -or
                [uint32]$heroes[$index].flags -ne 0 -or
                [int]$heroes[$index].srpHeroCallbacks -ne 1 -or
                [uint64]$heroes[$index].elapsedTicks -eq 0) {
                throw 'hero mapping failed'
            }
        }
        for ($index = 0; $index -lt $controls.Count; $index++) {
            if ([int]$controls[$index].sampleIndex -ne ($index * 8) -or
                [uint32]$controls[$index].flags -ne 1 -or
                [int]$controls[$index].srpHeroCallbacks -ne 0) {
                throw 'control mapping failed'
            }
        }
        foreach ($row in $rows) {
            $begin = [uint64]$row.beginTicks
            $end = [uint64]$row.endTicks
            if ($end -lt $begin -or [uint64]$row.elapsedTicks -ne
                    ($end - $begin) -or
                [uint64]$row.frequency -ne
                    [uint64]$item.Report.nativeTimestampObservedFrequency -or
                [uint32]$row.deviceGeneration -ne
                    [uint32]$item.Report.nativeTimestampDeviceGeneration -or
                [uint64]$row.fenceValue -eq 0 -or
                [int]$row.pendingFrames -ne
                    ([int]$row.resultFrame - [int]$row.sourceFrame)) {
                throw 'tick/frequency/fence validation failed'
            }
        }
        $heroMs = [double[]]@(
            $heroes | ForEach-Object { [double]$_.elapsedMs })
        $rawAverage = [double](($heroMs | Measure-Object -Average).Average)
        $rawP99 = Get-InterpolatedPercentile $heroMs 0.99
        if ([Math]::Abs($rawAverage -
                [double]$item.Report.gpuAverageMs) -gt 0.000001 -or
            [Math]::Abs($rawP99 -
                [double]$item.Report.gpuP99Ms) -gt 0.000001) {
            throw 'raw metric reconciliation failed'
        }
    }
    catch {
        $nativeTimestampEvidenceValid = $false
    }
}
$measurementResidencyStable = @(
    $reportItems.Report |
    Where-Object { -not $_.measurementResidencyStable }).Count -eq 0
$measurementResidencyMatches = @(
    $reportItems.Report |
    ForEach-Object {
        "$($_.measurementMinResidentPacks)|" +
        "$($_.measurementMaxResidentPacks)|" +
        "$($_.measurementMinResidentBytes)|" +
        "$($_.measurementMaxResidentBytes)"
    } |
    Select-Object -Unique).Count -eq 1
$measurementCullTopologyValid = @(
    $reportItems.Report |
    Where-Object {
        -not $_.measurementCullTopologyValid -or
        $_.measurementCullTopologySamples -ne $_.sampleFrames -or
        $_.measurementMinCullCameraCount -ne
            $_.cameraCount -or
        $_.measurementMaxCullCameraCount -ne
            $_.cameraCount -or
        $_.measurementMinCullPackCount -ne
            $_.cityResidentPacks -or
        $_.measurementMaxCullPackCount -ne
            $_.cityResidentPacks -or
        [string]::IsNullOrWhiteSpace(
            [string]$_.measurementCullTopologySequenceHash)
    }).Count -eq 0
$measurementCullTopologyMatches = @(
    $reportItems.Report |
    ForEach-Object {
        "$($_.measurementCullTopologySamples)|" +
        "$($_.measurementMinCullCameraCount)|" +
        "$($_.measurementMaxCullCameraCount)|" +
        "$($_.measurementMinCullPackCount)|" +
        "$($_.measurementMaxCullPackCount)|" +
        "$($_.measurementCullTopologySequenceHash)"
    } |
    Select-Object -Unique).Count -eq 1
$sourceCommits = @(
    $reportItems.Report |
    Select-Object -ExpandProperty sourceCommit -Unique)
$playerHashes = @(
    $reportItems.Report |
    Select-Object -ExpandProperty playerSha256 -Unique)
$nativeDllHashes = @(
    $reportItems.Report |
    Select-Object -ExpandProperty nativeTimestampDllSha256 -Unique)
$provenanceValid = $sourceCommits.Count -eq 1 -and
    -not [string]::IsNullOrWhiteSpace([string]$sourceCommits[0]) -and
    $playerHashes.Count -eq 1 -and
    -not [string]::IsNullOrWhiteSpace([string]$playerHashes[0]) -and
    $nativeDllHashes.Count -eq 1 -and
    ([string]$nativeDllHashes[0]).Length -eq 64 -and
    @($reportItems.Report | Where-Object { $_.sourceDirty }).Count -eq 0
$cityHashesMatch = @(
    $reportItems.Report |
    Select-Object -ExpandProperty cityOutputHash -Unique).Count -eq 1
$compositeHashesMatch = @(
    $reportItems.Report |
    Select-Object -ExpandProperty compositeOutputHash -Unique).Count -eq 1

$imageRows = [System.Collections.Generic.List[object]]::new()
foreach ($round in $rounds) {
    foreach ($shot in @('skyline', 'facade', 'texture')) {
        $left = Join-Path $inputRoot "baseline-r$round-$shot.png"
        $right = Join-Path $inputRoot "optimized-r$round-$shot.png"
        if (-not (Test-Path -LiteralPath $left -PathType Leaf) -or
            -not (Test-Path -LiteralPath $right -PathType Leaf)) {
            throw "Missing gallery pair for round $round / $shot."
        }
        $comparison = [CinematicImageComparer]::Compare($left, $right)
        $imageRows.Add([pscustomobject]@{
            Round = $round
            Shot = $shot
            Width = $comparison.Width
            Height = $comparison.Height
            MeanAbsoluteError = $comparison.MeanAbsoluteError
            PsnrDb = $comparison.PsnrDb
            FractionOver5 = $comparison.FractionOver5
            Passed = (
                $comparison.PsnrDb -ge $MinimumPsnrDb -and
                $comparison.FractionOver5 -le $MaximumFractionOver5)
        })
    }
}
$imageRows | Export-Csv -LiteralPath (
    Join-Path $inputRoot 'visual-equivalence.csv') -NoTypeInformation
$visualEquivalent = @($imageRows | Where-Object { -not $_.Passed }).Count -eq 0

$baselineFrameAverage =
    Get-VariantMetric 'baseline' 'frameAverageMs'
$optimizedFrameAverage =
    Get-VariantMetric 'optimized' 'frameAverageMs'
$baselineFrameP99 = Get-VariantMetric 'baseline' 'frameP99Ms'
$optimizedFrameP99 = Get-VariantMetric 'optimized' 'frameP99Ms'
$baselineGpuAverage = Get-VariantMetric 'baseline' 'gpuAverageMs'
$optimizedGpuAverage = Get-VariantMetric 'optimized' 'gpuAverageMs'
$baselineGpuP99 = Get-VariantMetric 'baseline' 'gpuP99Ms'
$optimizedGpuP99 = Get-VariantMetric 'optimized' 'gpuP99Ms'
$baselineLongRate = Get-VariantMetric 'baseline' 'longFrame33Rate'
$optimizedLongRate = Get-VariantMetric 'optimized' 'longFrame33Rate'
$baselineSensorUpload =
    Get-VariantMetric 'baseline' 'sensorLogicalUploadBytes'
$optimizedSensorUpload =
    Get-VariantMetric 'optimized' 'sensorLogicalUploadBytes'
$baselineResidencyUpload =
    Get-VariantMetric 'baseline' 'residencyLogicalUploadBytes'
$optimizedResidencyUpload =
    Get-VariantMetric 'optimized' 'residencyLogicalUploadBytes'

$pairedRows = [System.Collections.Generic.List[object]]::new()
foreach ($round in $rounds) {
    $baseline = ($reportItems |
        Where-Object { $_.Round -eq $round -and
            $_.Variant -eq 'baseline' }).Report
    $optimized = ($reportItems |
        Where-Object { $_.Round -eq $round -and
            $_.Variant -eq 'optimized' }).Report
    $pairedRows.Add([pscustomobject]@{
        Round = $round
        FrameAverageImprovementPct = Get-Improvement `
            ([double]$baseline.frameAverageMs) `
            ([double]$optimized.frameAverageMs)
        FrameP99ImprovementPct = Get-Improvement `
            ([double]$baseline.frameP99Ms) `
            ([double]$optimized.frameP99Ms)
        NativeScopeAverageImprovementPct = Get-Improvement `
            ([double]$baseline.gpuAverageMs) `
            ([double]$optimized.gpuAverageMs)
        NativeScopeP99ImprovementPct = Get-Improvement `
            ([double]$baseline.gpuP99Ms) `
            ([double]$optimized.gpuP99Ms)
        LongFrame33ImprovementPct = Get-Improvement `
            ([double]$baseline.longFrame33Rate) `
            ([double]$optimized.longFrame33Rate)
    })
}
$pairedRows | Export-Csv -LiteralPath (
    Join-Path $inputRoot 'paired-improvements.csv') -NoTypeInformation
$pairedFrameAverageImprovement = Get-Median `
    ([double[]]$pairedRows.FrameAverageImprovementPct)
$pairedFrameP99Improvement = Get-Median `
    ([double[]]$pairedRows.FrameP99ImprovementPct)
$pairedGpuAverageImprovement = Get-Median `
    ([double[]]$pairedRows.NativeScopeAverageImprovementPct)
$pairedGpuP99Improvement = Get-Median `
    ([double[]]$pairedRows.NativeScopeP99ImprovementPct)
$pairedLongFrameImprovement = Get-Median `
    ([double[]]$pairedRows.LongFrame33ImprovementPct)

$accepted = $qualityPassed -and
    $functionalHashesMatch -and
    $dimensionsMatch -and
    $residentPacksMatch -and
    $fullResidentPackSetValid -and
    $residentBytesMatch -and
    $finalLoadingClear -and
    $cityValidationStable -and
    $cityOracleTopologyMatches -and
    $explicitCullCamerasValid -and
    $payloadCadenceValid -and
    $prePayloadCitySubmissionValid -and
    $cameraRenderOrderValid -and
    $workloadSubmissionCadenceValid -and
    $workloadSequenceCoverageValid -and
    $warmupSubmissionCadenceValid -and
    $workloadSubmissionCadenceMatches -and
    $sampleCompletenessValid -and
    $gpuTimingCoverageValid -and
    $nativeTimestampCompatibilityValid -and
    $nativeTimestampEvidenceValid -and
    $measurementResidencyStable -and
    $measurementResidencyMatches -and
    $measurementCullTopologyValid -and
    $measurementCullTopologyMatches -and
    $provenanceValid -and
    $visualEquivalent -and
    $cityHashesMatch -and
    $compositeHashesMatch

$summary = [System.Text.StringBuilder]::new()
[void]$summary.AppendLine('# NYC GPU Cinematic Benchmark A/B')
[void]$summary.AppendLine()
[void]$summary.AppendLine("- Acceptance passed: $accepted")
[void]$summary.AppendLine("- Reports: $($reportItems.Count)")
[void]$summary.AppendLine("- GPU: $($reportItems[0].Report.graphicsDeviceName) / $($reportItems[0].Report.graphicsApi)")
[void]$summary.AppendLine("- Full resident pack set submitted: $fullResidentPackSetValid")
[void]$summary.AppendLine("- Hero output: $($reportItems[0].Report.outputWidth)x$($reportItems[0].Report.outputHeight)")
[void]$summary.AppendLine("- Functional workload hashes identical: $functionalHashesMatch")
[void]$summary.AppendLine("- City cull-set hashes identical: $cityHashesMatch")
[void]$summary.AppendLine("- Composite output hashes identical: $compositeHashesMatch")
[void]$summary.AppendLine("- Gallery visual equivalence passed: $visualEquivalent")
[void]$summary.AppendLine("- City-oracle convergence samples: $($reportItems[0].Report.cityValidationSamples)")
[void]$summary.AppendLine("- Fresh completed-pass fixed-camera oracle: $cityValidationStable")
[void]$summary.AppendLine("- A/B city-oracle topology identical: $cityOracleTopologyMatches")
[void]$summary.AppendLine("- Camera profile: $benchmarkCameraCount ($expectedCityCullCameraMode)")
[void]$summary.AppendLine("- Explicit requested-camera city cull set: $explicitCullCamerasValid")
[void]$summary.AppendLine("- Fresh requested-camera pass on every measured frame: $measurementCullTopologyValid")
[void]$summary.AppendLine("- A/B measured cull-topology sequence identical: $measurementCullTopologyMatches")
[void]$summary.AppendLine("- Exact payload render cadence: $payloadCadenceValid")
[void]$summary.AppendLine("- Fresh city submission precedes every camera render group: $prePayloadCitySubmissionValid")
[void]$summary.AppendLine("- Ordered hero + $($benchmarkCameraCount - 1) payload SRP callbacks every frame: $cameraRenderOrderValid")
[void]$summary.AppendLine("- Exact zero-drop auxiliary workload cadence: $workloadSubmissionCadenceValid")
[void]$summary.AppendLine("- Full logical-state sequence coverage: $workloadSequenceCoverageValid")
[void]$summary.AppendLine("- Exact zero-drop warmup cadence: $warmupSubmissionCadenceValid")
[void]$summary.AppendLine("- A/B auxiliary workload counts identical: $workloadSubmissionCadenceMatches")
[void]$summary.AppendLine("- Auxiliary cadence: every $($reportItems[0].Report.workloadIssueIntervalFrames) logical frames")
[void]$summary.AppendLine("- Submissions per workload/process: $($reportItems[0].Report.expectedWorkloadSubmissions)")
[void]$summary.AppendLine("- Warmup submissions per workload/process: $($reportItems[0].Report.warmupExpectedWorkloadSubmissions)")
[void]$summary.AppendLine("- Complete frame samples: $sampleCompletenessValid")
[void]$summary.AppendLine("- Complete native D3D12 direct-queue scope coverage: $gpuTimingCoverageValid")
[void]$summary.AppendLine("- Raw per-token timestamp evidence reconciled: $nativeTimestampEvidenceValid")
[void]$summary.AppendLine("- A/B native backend/device/scope compatible: $nativeTimestampCompatibilityValid")
[void]$summary.AppendLine("- Native scope: $($reportItems[0].Report.gpuTimingScopeVersion)")
[void]$summary.AppendLine("- Native timestamp DLL SHA256: $($nativeDllHashes[0])")
[void]$summary.AppendLine("- Measurement residency stable: $measurementResidencyStable")
[void]$summary.AppendLine("- A/B residency state identical: $measurementResidencyMatches")
[void]$summary.AppendLine("- Final residency bytes identical: $residentBytesMatch")
[void]$summary.AppendLine("- Clean traceable provenance: $provenanceValid")
[void]$summary.AppendLine("- Source commit: $($sourceCommits[0])")
[void]$summary.AppendLine("- Counterbalanced A/B rounds: $($rounds.Count); 3+ rounds are required for a resume-grade claim")
[void]$summary.AppendLine()
[void]$summary.AppendLine('| Metric | A baseline median | B optimized median | Median paired B improvement |')
[void]$summary.AppendLine('|---|---:|---:|---:|')
[void]$summary.AppendLine(('| Frame average | {0:F3} ms | {1:F3} ms | {2:F2}% |' -f $baselineFrameAverage, $optimizedFrameAverage, $pairedFrameAverageImprovement))
[void]$summary.AppendLine(('| Frame P99 | {0:F3} ms | {1:F3} ms | {2:F2}% |' -f $baselineFrameP99, $optimizedFrameP99, $pairedFrameP99Improvement))
[void]$summary.AppendLine(('| Native city-cull + explicit-camera-group average | {0:F3} ms | {1:F3} ms | {2:F2}% |' -f $baselineGpuAverage, $optimizedGpuAverage, $pairedGpuAverageImprovement))
[void]$summary.AppendLine(('| Native city-cull + explicit-camera-group P99 | {0:F3} ms | {1:F3} ms | {2:F2}% |' -f $baselineGpuP99, $optimizedGpuP99, $pairedGpuP99Improvement))
[void]$summary.AppendLine(('| Frames >33.3 ms | {0:P2} | {1:P2} | {2:F2}% |' -f $baselineLongRate, $optimizedLongRate, $pairedLongFrameImprovement))
[void]$summary.AppendLine(('| Sensor CPU upload | {0:N0} B | {1:N0} B | {2:F2}% |' -f $baselineSensorUpload, $optimizedSensorUpload, (Get-Improvement $baselineSensorUpload $optimizedSensorUpload)))
[void]$summary.AppendLine(('| Page upload | {0:N0} B | {1:N0} B | {2:F2}% |' -f $baselineResidencyUpload, $optimizedResidencyUpload, (Get-Improvement $baselineResidencyUpload $optimizedResidencyUpload)))
[void]$summary.AppendLine()
[void]$summary.AppendLine('| Gallery shot | PSNR | Mean absolute error | Pixels with max channel error >5 |')
[void]$summary.AppendLine('|---|---:|---:|---:|')
foreach ($row in $imageRows) {
    [void]$summary.AppendLine(('| R{0} {1} | {2:F3} dB | {3:F4} / 255 | {4:P4} |' -f $row.Round, $row.Shot, $row.PsnrDb, $row.MeanAbsoluteError, $row.FractionOver5))
}
[void]$summary.AppendLine()
[void]$summary.AppendLine('A and B use the same 1080p route, weather, time, cameras, seed, residency, and post-processing inputs.')
[void]$summary.AppendLine('Auxiliary sensor, residency, and deadline workloads use the same fixed logical-frame cadence and must complete with identical submission counts and zero drops.')
[void]$summary.AppendLine('The native interval begins before BFP2 LateUpdate and ends after the explicit camera group; sensor/residency/deadline submissions issued before BEGIN and independent async compute/copy queues are excluded.')
[void]$summary.AppendLine('Empty controls run at an identical one-per-eight-frame cadence in A and B and are reported separately; their duration is not subtracted from hero samples.')
[void]$summary.AppendLine()
[void]$summary.AppendLine('This exact-route benchmark requires identical internal city cull-set and composite hashes in addition to thresholded gallery equivalence.')
[void]$summary.AppendLine()
[void]$summary.AppendLine(('Visual threshold: PSNR >= {0:F1} dB and fraction over 5 <= {1:P2} for every fixed gallery frame.' -f $MinimumPsnrDb, $MaximumFractionOver5))
[System.IO.File]::WriteAllText(
    (Join-Path $inputRoot 'SUMMARY.md'),
    $summary.ToString())

if (-not $accepted) {
    exit 2
}
Write-Host "Cinematic summary written: $(Join-Path $inputRoot 'SUMMARY.md')"
