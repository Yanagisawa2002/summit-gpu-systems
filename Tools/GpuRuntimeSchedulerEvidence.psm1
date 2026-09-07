Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RuntimeManifest([string]$Root, [switch]$Source) {
    $Root = [IO.Path]::GetFullPath($Root)
    $paths = if ($Source) {
        @(& git -C $Root ls-files --cached --others --exclude-standard -- Assets Packages ProjectSettings Tools)
        if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate source inputs.' }
    } else {
        @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | ForEach-Object { [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\','/') })
    }
    $files = @($paths | Sort-Object -Unique | ForEach-Object {
        $path = Join-Path $Root $_
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            [pscustomobject]@{ path=$_; bytes=(Get-Item -LiteralPath $path).Length; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
        } else { [pscustomobject]@{ path=$_; bytes=-1; sha256='MISSING' } }
    })
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = [Convert]::ToHexString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($files | ConvertTo-Json -Compress -Depth 5)))) }
    finally { $sha.Dispose() }
    return [pscustomobject]@{ sha256=$digest; files=$files }
}

function Assert-RuntimeFreshOutput([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        if (-not (Test-Path -LiteralPath $Path -PathType Container) -or @(Get-ChildItem -LiteralPath $Path -Force).Count -gt 0) {
            throw 'Output directory is not empty; use a fresh evidence directory.'
        }
    }
}

function Assert-RuntimeClose([double]$Actual, [double]$Expected, [string]$Label) {
    if ([double]::IsNaN($Actual) -or [double]::IsInfinity($Actual) -or
        [Math]::Abs($Actual - $Expected) -gt [Math]::Max(0.000001, [Math]::Abs($Expected) * 0.000001)) { throw "Nonfinite or mismatched $Label" }
}

function Get-RuntimeStats([double[]]$Values) {
    if ($Values.Count -eq 0) { throw 'Missing timing samples.' }
    $ordered = @($Values | Sort-Object)
    return [pscustomobject]@{ average=($Values | Measure-Object -Average).Average;
        p50=$ordered[[int][Math]::Ceiling($Values.Count * 0.50)-1];
        p95=$ordered[[int][Math]::Ceiling($Values.Count * 0.95)-1];
        p99=$ordered[[int][Math]::Ceiling($Values.Count * 0.99)-1] }
}

function Assert-RuntimeNative($Sample, [string]$Tag, [int]$Flags, $Tokens) {
    if ($null -eq $Sample -or $Sample.status -ne 1 -or $Sample.flags -ne $Flags -or $Sample.nativeFlags -ne $Flags -or
        $Sample.userTag -ne $Tag -or $Sample.sourceFrame -lt 0 -or $Sample.resultFrame -lt $Sample.sourceFrame -or
        $Sample.scopeIndex -lt 0 -or $Sample.deviceGeneration -lt 1) { throw 'Invalid/mismatched native sample metadata.' }
    foreach ($field in @('token','userTag','beginTicks','endTicks','elapsedTicks','frequency','fenceValue')) {
        if ([string]$Sample.$field -notmatch '^\d+$') { throw "Invalid raw $field" }
        $null = [UInt64]::Parse([string]$Sample.$field)
    }
    if ([decimal]$Sample.token -eq 0 -or -not $Tokens.Add([string]$Sample.token)) { throw 'Duplicate or invalid native token.' }
    if ([decimal]$Sample.frequency -le 0 -or [decimal]$Sample.fenceValue -le 0 -or
        [decimal]$Sample.endTicks -lt [decimal]$Sample.beginTicks -or
        [decimal]$Sample.endTicks - [decimal]$Sample.beginTicks -ne [decimal]$Sample.elapsedTicks) { throw 'Invalid raw tick interval/frequency.' }
    Assert-RuntimeClose $Sample.durationUs ([double]([decimal]$Sample.elapsedTicks * 1000000 / [decimal]$Sample.frequency)) 'native tick conversion'
    if ($Sample.durationUs -lt 0 -or ($Flags -eq 0 -and $Sample.durationUs -le 0)) { throw 'Nonpositive workload timing.' }
}

function Test-RuntimeMatrix([string]$Directory, [int]$Rounds, [int]$Batches) {
    $scenarios = @('bursts','changing-costs','dependencies','overload','background-progress')
    $variants = @('fifo-bounded','runtime-aging-cost-dag')
    $tokens = [Collections.Generic.HashSet[string]]::new()
    $expectedFiles = [Collections.Generic.HashSet[string]]::new()
    $summaries = @(); $controls = @()
    foreach ($scenario in $scenarios) {
        $signature = $null
        $offered = if ($scenario -eq 'bursts') { 4*$Batches + 8*[Math]::Ceiling($Batches/4) } elseif ($scenario -eq 'background-progress') { $Batches+1 } else { 4*$Batches }
        for ($round=0; $round -lt $Rounds; $round++) { foreach ($variant in $variants) { foreach ($phase in @('validation','measured')) {
            $file = "$scenario-r$round-$variant-$phase.json"; $null = $expectedFiles.Add($file)
            $r = Get-Content -LiteralPath (Join-Path $Directory $file) -Raw | ConvertFrom-Json
            if ($r.scenario -ne $scenario -or $r.round -ne $round -or $r.variant -ne $variant -or $r.phase -ne $phase -or
                $r.timingDomain -ne 'cpu-fence-observed-latency-and-native-dx12-gpu-duration' -or
                $r.offered -ne $offered -or $r.completed -ne $offered -or @($r.rows).Count -ne $offered -or
                $r.cpuTimerFrequency -le 0 -or [string]::IsNullOrWhiteSpace($r.planningGcScope)) { throw "Incomplete or mismatched case: $file" }
            Assert-RuntimeNative $r.outerSample '18446744073709551615' 0 $tokens
            Assert-RuntimeNative $r.emptyBefore '18446744073709551614' 1 $tokens
            Assert-RuntimeNative $r.emptyAfter '18446744073709551614' 1 $tokens
            Assert-RuntimeClose $r.gpuTimelineMakespanUs $r.outerSample.durationUs 'outer makespan'
            if ([decimal]$r.emptyBefore.endTicks -gt [decimal]$r.outerSample.beginTicks -or [decimal]$r.emptyAfter.beginTicks -lt [decimal]$r.outerSample.endTicks) { throw 'Empty controls are not outside the case interval.' }
            $planning = @(); $gpu = @(); $critical = @(); $misses = 0; $gc = 0L; $cpuTicks = 0L; $makespan = 0L; $wait = 0L; $starved = 0; $background = 0
            $work = @()
            for ($i=0; $i -lt $offered; $i++) {
                $row = $r.rows[$i]
                if ($row.jobId -ne $i -or $row.arrivalUs -lt 0 -or $row.admittedUs -lt $row.arrivalUs -or
                    $row.submitUs -lt $row.admittedUs -or $row.completeUs -lt $row.submitUs -or
                    $row.planningTicks -lt 0 -or $row.planningAllocatedBytes -lt 0 -or $row.estimatedCostUs -lt 1) { throw 'Invalid job/CPU timing row.' }
                Assert-RuntimeNative $row.nativeSample ([string]($i+1)) 0 $tokens
                foreach ($sample in @($row.nativeSample, $r.emptyBefore, $r.emptyAfter)) {
                    if ($sample.frequency -ne $r.outerSample.frequency -or $sample.deviceGeneration -ne $r.outerSample.deviceGeneration) { throw 'Mixed timestamp device/frequency.' }
                }
                if ([decimal]$row.nativeSample.beginTicks -lt [decimal]$r.outerSample.beginTicks -or [decimal]$row.nativeSample.endTicks -gt [decimal]$r.outerSample.endTicks) { throw 'Dispatch outside outer scope.' }
                Assert-RuntimeClose $row.gpuDurationUs $row.nativeSample.durationUs 'dispatch duration'
                $planning += [double]$row.planningTicks*1000000/$r.cpuTimerFrequency; $gpu += [double]$row.gpuDurationUs
                $gc += $row.planningAllocatedBytes; $cpuTicks += $row.planningTicks; $makespan = [Math]::Max($makespan, $row.completeUs)
                if ($row.deadlineClass -eq 0) { $latency=$row.completeUs-$row.arrivalUs; $critical += $latency; if ($latency -gt 400) { $misses++ } }
                if ($row.deadlineClass -eq 2) { $background++; $jobWait=$row.submitUs-$row.arrivalUs; $wait=[Math]::Max($wait,$jobWait); if ($jobWait -gt 1000) { $starved++ } }
                $work += "$($row.jobId):$($row.deadlineClass):$($row.actualCostUs):$($row.arrivalUs)"
            }
            $caseSignature = $work -join '|'
            if ($null -eq $signature) { $signature=$caseSignature } elseif ($signature -ne $caseSignature) { throw 'Validation/measurement/policy offered workloads differ.' }
            $p = Get-RuntimeStats $planning; $g = Get-RuntimeStats $gpu; $c = Get-RuntimeStats $critical
            Assert-RuntimeClose $r.planningAverageUs $p.average 'planning average'; Assert-RuntimeClose $r.planningP99Us $p.p99 'planning P99'
            Assert-RuntimeClose $r.gpuDispatchAverageUs $g.average 'dispatch average'; Assert-RuntimeClose $r.gpuDispatchP99Us $g.p99 'dispatch P99'
            Assert-RuntimeClose $r.criticalP99Us $c.p99 'critical P99'; Assert-RuntimeClose $r.criticalMissRate ($misses/$critical.Count) 'miss rate'
            if ($r.criticalCount -ne $critical.Count -or $r.criticalMisses -ne $misses -or $r.planningAllocatedBytes -ne $gc -or
                $r.planningTicks -ne $cpuTicks -or $r.makespanUs -ne $makespan -or $r.backgroundCompleted -ne $background -or
                $r.starvedBackground -ne $starved -or $r.maxBackgroundWaitUs -ne $wait) { throw 'Mismatched aggregate counts/times.' }
            $empty = Get-RuntimeStats @($r.emptyBefore.durationUs, $r.emptyAfter.durationUs)
            $controls += [pscustomobject]@{ scenario=$scenario; round=$round; variant=$variant; phase=$phase; samples=2; p50Us=$empty.p50; p95Us=$empty.p95; p99Us=$empty.p99 }
            $summaries += $r | Select-Object scenario,round,variant,phase,timingDomain,offered,completed,criticalP99Us,criticalMissRate,makespanUs,
                gpuTimelineMakespanUs,gpuDispatchAverageUs,gpuDispatchP99Us,planningAverageUs,planningP99Us,cpuTimerFrequency,
                planningAllocatedBytes,planningGcScope,maxBackgroundWaitUs,starvedBackground,backpressureAttempts,acceptedCostSamples
        } } }
    }
    foreach ($f in Get-ChildItem -LiteralPath $Directory -File | Where-Object Name -Match '-(measured|validation)\.json$') {
        if (-not $expectedFiles.Contains($f.Name)) { throw 'Unexpected/stale matrix case.' }
    }
    $summaries | Export-Csv -NoTypeInformation -LiteralPath (Join-Path $Directory 'summary.csv')
    $controls | Export-Csv -NoTypeInformation -LiteralPath (Join-Path $Directory 'empty-controls.csv')
    return [pscustomobject]@{ cases=$expectedFiles.Count; nativeSamples=$tokens.Count; measuredCases=($summaries | Where-Object phase -eq 'measured').Count }
}

Export-ModuleMember -Function Get-RuntimeManifest,Assert-RuntimeFreshOutput,Test-RuntimeMatrix
