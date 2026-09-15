[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9-]+$')][string]$Name,
    [Parameter(Mandatory)][string]$Script,
    [double]$EstimatedAdditionalGiB = 1
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$run=Join-Path $repo 'Artifacts/whole-task-md-20260915'
$coord='D:\CodexWork\whole-task-validation-20260915\coordination'
$null=New-Item -ItemType Directory -Path "$run/stages" -Force
$receiptPath="$run/stages/$Name.json"
if(Test-Path -LiteralPath $receiptPath){throw 'Keep previous evidence; choose a new stage name.'}
$receipt=[ordered]@{schemaVersion=1;id='summit';name=$Name;startedAtUtc=[DateTime]::UtcNow.ToString('o');pid=$PID;status='preflight';mutexAcquired=$false;hardwareReleased=$false}
$gate=[Threading.Mutex]::new($false,'Local\CodexR9700VNextUnityGpu');$owned=$false
try {
    $prior=Get-Content -LiteralPath "$coord/handoff/hlsl.json" -Raw|ConvertFrom-Json
    if($prior.terminal -cne $true -or $prior.hardwareReleased -cne $true){throw 'HLSL has not reached terminal hardware release.'}
    $registry=Get-Content -LiteralPath "$coord/threads.json" -Raw|ConvertFrom-Json
    if($prior.threadId -and $prior.threadId -ne $registry.threads.hlsl.threadId){throw 'HLSL handoff identity mismatch.'}
    $receipt.predecessor=$prior
    try{$owned=$gate.WaitOne(0)}catch [Threading.AbandonedMutexException]{$owned=$true;$receipt.abandonedMutexRecovered=$true}
    if(!$owned){throw 'Shared hardware mutex is held; no child launched.'}
    $receipt.mutexAcquired=$true
    $conflicts=@(Get-CimInstance Win32_Process|Where-Object {$_.Name -match '^(Unity|UnityShaderCompiler|MSBuild|dotnet|cl|link|nmake|ninja|cmake|ArborX.*|Cabana.*|SummitExternalReplay|SummitMolecular.*|UpstreamMolecular.*|Hlsl.*|.*Benchmark.*)\.exe$'}|Select-Object ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine)
    $receipt.conflicts=$conflicts
    if($conflicts.Count){throw 'Other build/experiment processes are active; none stopped.'}
    $receipt.freeGiB=@{C=[math]::Round((Get-PSDrive C).Free/1GB,3);D=[math]::Round((Get-PSDrive D).Free/1GB,3)}
    if($receipt.freeGiB.C -lt 20 -or $receipt.freeGiB.D-$EstimatedAdditionalGiB -lt 20){throw '20 GiB disk reserve preflight failed.'}
    $os=Get-CimInstance Win32_OperatingSystem
    $receipt.availableMemoryGiB=[math]::Round($os.FreePhysicalMemory/1MB,3)
    if($receipt.availableMemoryGiB -lt 8){throw 'Insufficient free physical memory.'}
    $receipt.cpuLoadPercent=@(Get-CimInstance Win32_Processor|Select-Object -ExpandProperty LoadPercentage)
    if(($receipt.cpuLoadPercent|Measure-Object -Maximum).Maximum -gt 25){throw 'CPU background load exceeds 25 percent.'}
    $gpu=@(Get-CimInstance Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine -ErrorAction SilentlyContinue|Where-Object {$_.Name -match 'engtype_(3D|Compute)' -and $_.UtilizationPercentage -gt 0}|Select-Object Name,UtilizationPercentage)
    $receipt.gpuActivity=$gpu
    if(($gpu.UtilizationPercentage|Measure-Object -Maximum).Maximum -gt 20){throw 'GPU background engine utilization exceeds 20 percent.'}
    $receipt.script=[IO.Path]::GetFullPath($Script);$receipt.scriptSha256=(Get-FileHash -LiteralPath $Script -Algorithm SHA256).Hash.ToLowerInvariant()
    $receipt.sourceCommit=(& git -C $repo rev-parse HEAD).Trim();$receipt.status='running'
    $receipt|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $receiptPath -Encoding utf8
    & $Script *>&1 | Tee-Object -FilePath "$run/stages/$Name.log"
    if($LASTEXITCODE -and $LASTEXITCODE -ne 0){throw "Native process exit $LASTEXITCODE"}
    $receipt.status='completed'
} catch {
    $receipt.status='failed';$receipt.error=$_.Exception.Message
    throw
} finally {
    if($owned){$gate.ReleaseMutex()}
    $gate.Dispose();$receipt.hardwareReleased=$true;$receipt.finishedAtUtc=[DateTime]::UtcNow.ToString('o')
    $receipt|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $receiptPath -Encoding utf8
}
