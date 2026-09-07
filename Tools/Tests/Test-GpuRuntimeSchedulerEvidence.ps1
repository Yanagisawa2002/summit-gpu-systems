#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Import-Module (Join-Path $taskRoot 'Tools/GpuRuntimeSchedulerEvidence.psm1') -Force
$fixture=Join-Path $taskRoot ('Reports/Tests/RuntimeEvidence-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$sources=@('Packages/com.summit.gpu-deadline-scheduler/Runtime/GpuDeadlineContracts.cs',
    'Packages/com.summit.gpu-deadline-scheduler/Runtime/GpuRuntimeCostEstimator.cs',
    'Packages/com.summit.gpu-deadline-scheduler/Runtime/GpuRuntimeScheduler.cs',
    'Assets/GpuDeadlineSchedulerBenchmark/Runtime/GpuRuntimeSchedulerTrace.cs') | ForEach-Object { Join-Path $taskRoot $_ }
Add-Type -Path $sources
$script:token=0
function Sample([string]$Tag,[int]$Flags,[long]$Begin,[long]$End) {
    $script:token++
    [pscustomobject]@{ token=[string]$script:token; userTag=$Tag; beginTicks=[string]$Begin; endTicks=[string]$End;
        elapsedTicks=[string]($End-$Begin); frequency='1000000'; fenceValue='1'; status=1; sourceFrame=0; resultFrame=2;
        scopeIndex=0; flags=$Flags; nativeFlags=$Flags; deviceGeneration=1; durationUs=[double]($End-$Begin) }
}
foreach ($scenario in [GpuRuntimeSchedulerTrace]::Scenarios) { foreach ($fifo in @($true,$false)) { foreach ($phase in @('validation','measured')) {
    $r=[GpuRuntimeSchedulerTrace]::Simulate($scenario,2,$fifo) | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $r.timingDomain='cpu-fence-observed-latency-and-native-dx12-gpu-duration'; $r.phase=$phase; $r.round=0
    $r.emptyBefore=Sample '18446744073709551614' 1 0 1
    $end=100+$r.offered*100+1
    $r.outerSample=Sample '18446744073709551615' 0 100 $end; $r.gpuTimelineMakespanUs=$end-100
    foreach ($row in $r.rows) {
        $row.nativeSample=Sample ([string]($row.jobId+1)) 0 (100+$row.jobId*100) (150+$row.jobId*100)
        $row.gpuDurationUs=50
    }
    $r.emptyAfter=Sample '18446744073709551614' 1 ($end+1) ($end+2)
    $r.gpuDispatchAverageUs=50; $r.gpuDispatchP99Us=50
    $r | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $fixture "$scenario-r0-$($r.variant)-$phase.json")
} } }
$result=Test-RuntimeMatrix $fixture 1 2
if ($result.cases -ne 20 -or $result.measuredCases -ne 10) { throw 'Valid matrix fixture rejected.' }
$script:negative=0
function Reject([scriptblock]$Action,[string]$Name) {
    $caught=$false
    try { & $Action | Out-Null } catch { $caught=$true }
    if (-not $caught) { throw "Expected rejection missing: $Name" }; $script:negative++
}
$file=Join-Path $fixture 'bursts-r0-fifo-bounded-measured.json'; $original=[IO.File]::ReadAllText($file)
foreach ($mutation in @('ticks','token','duration','round','phase','count','average','gc','workload','frequency','status')) {
    $r=$original | ConvertFrom-Json
    switch ($mutation) {
        ticks { $r.rows[0].nativeSample.endTicks='999999' }
        token { $r.rows[0].nativeSample.token=$r.outerSample.token }
        duration { $r.rows[0].gpuDurationUs='NaN' }
        round { $r.round=1 }
        phase { $r.phase='validation' }
        count { $r.completed-- }
        average { $r.planningAverageUs+=1 }
        gc { $r.planningAllocatedBytes+=1 }
        workload { $r.rows[0].actualCostUs+=1 }
        frequency { $r.rows[0].nativeSample.frequency='0' }
        status { $r.rows[0].nativeSample.status=0 }
    }
    $r | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $file
    Reject { Test-RuntimeMatrix $fixture 1 2 } $mutation
}
[IO.File]::WriteAllText($file,$original)
Move-Item -LiteralPath $file -Destination ($file+'.missing')
Reject { Test-RuntimeMatrix $fixture 1 2 } 'missing measured case'
Move-Item -LiteralPath ($file+'.missing') -Destination $file
$validationFile=Join-Path $fixture 'bursts-r0-fifo-bounded-validation.json'
Move-Item -LiteralPath $validationFile -Destination ($validationFile+'.missing')
Reject { Test-RuntimeMatrix $fixture 1 2 } 'missing validation case'
Move-Item -LiteralPath ($validationFile+'.missing') -Destination $validationFile
Copy-Item -LiteralPath $file -Destination (Join-Path $fixture 'stale-measured.json')
Reject { Test-RuntimeMatrix $fixture 1 2 } 'extra stale case'
Reject { Assert-RuntimeFreshOutput $fixture } 'stale output directory'
$payload=Join-Path $fixture 'fake-player'; New-Item -ItemType Directory -Path $payload | Out-Null
[IO.File]::WriteAllText((Join-Path $payload 'player.exe'),'fixed exe')
[IO.File]::WriteAllText((Join-Path $payload 'managed.dll'),'before')
$before=Get-RuntimeManifest $payload
[IO.File]::WriteAllText((Join-Path $payload 'managed.dll'),'after')
$after=Get-RuntimeManifest $payload
if ($before.sha256 -eq $after.sha256) { throw 'Managed DLL mutation missed by payload manifest.' }
[IO.File]::WriteAllText((Join-Path $payload 'native.dll'),'added native')
if ($after.sha256 -eq (Get-RuntimeManifest $payload).sha256) { throw 'Native addition missed by payload manifest.' }
Write-Output "Evidence checks passed: valid 20-case matrix; $script:negative negative gates; entire payload DLL change/addition detection. Fixture: $fixture"
