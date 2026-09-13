[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Stage,
    [Parameter(Mandatory)][string]$ScriptFile,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [double]$EstimatedAdditionalGiB = 2
)
$ErrorActionPreference='Stop'
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$evidence=[IO.Path]::GetFullPath($EvidenceDirectory)
if(!$evidence.StartsWith($repoRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Evidence must be inside this worktree.'}
$null=New-Item -ItemType Directory -Path $evidence -Force
$receiptPath=Join-Path $evidence ($Stage+'.stage.json')
if(Test-Path -LiteralPath $receiptPath){throw 'Stage receipt already exists; choose a new stage name.'}
$gate=[Threading.Mutex]::new($false,'Local\CodexR9700VNextUnityGpu')
$acquired=$false
$receipt=[ordered]@{stage=$Stage;startedUtc=[DateTime]::UtcNow.ToString('o');pid=$PID;mutex='Local\CodexR9700VNextUnityGpu';estimatedAdditionalGiB=$EstimatedAdditionalGiB;minimumFreeGiB=20;status='preflight';resourcesReleased=$false}
try {
    try{$acquired=$gate.WaitOne(0)}catch [Threading.AbandonedMutexException]{$acquired=$true;$receipt['abandonedMutexRecovered']=$true}
    if(!$acquired){throw 'Shared execution mutex is held by another process.'}
    $background=@(Get-CimInstance Win32_Process | Where-Object {$_.Name -match '^(Unity|UnityShaderCompiler|MSBuild|dotnet|cl|link|nmake|ninja|cmake|devenv|Rider64|LinkedCellPerformance|CabanaCapture|CabanaReplay|ArborXNative|ArborXReplay|SummitExternalReplay|ArborX_Benchmark.*)\.exe$'} | Select-Object ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine)
    $receipt['background']=$background
    $receipt['freeGiBBefore']=[math]::Round((Get-PSDrive C).Free/1GB,4)
    if($background.Count){throw 'Conflicting editor/build/runtime processes are active; none were stopped.'}
    if($receipt.freeGiBBefore-$EstimatedAdditionalGiB -lt 20){throw 'Estimated peak would cross the 20 GiB reserve.'}
    $receipt['sourceCommit']=(& git -C $repoRoot rev-parse HEAD).Trim()
    $receipt['scriptSha256']=(Get-FileHash -LiteralPath $ScriptFile -Algorithm SHA256).Hash.ToLowerInvariant()
    $receipt['status']='running'
    $receipt|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $receiptPath -Encoding utf8
    & $ScriptFile *>&1 | Tee-Object -FilePath (Join-Path $evidence ($Stage+'.log'))
    if($LASTEXITCODE -and $LASTEXITCODE -ne 0){throw "Stage native command returned $LASTEXITCODE"}
    $receipt['status']='completed'
} catch {
    $receipt['status']='failed';$receipt['error']=$_.Exception.Message
    throw
} finally {
    if($acquired){$gate.ReleaseMutex()}
    $gate.Dispose()
    $receipt['resourcesReleased']=$true
    $receipt['freeGiBAfter']=[math]::Round((Get-PSDrive C).Free/1GB,4)
    $receipt['finishedUtc']=[DateTime]::UtcNow.ToString('o')
    $receipt|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $receiptPath -Encoding utf8
}
