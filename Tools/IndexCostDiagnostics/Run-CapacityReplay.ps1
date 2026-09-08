[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$OutputDirectory,
 [Parameter(Mandatory)][string]$SceneEvidence,
 [Parameter(Mandatory)][string]$FocusedEvidence,
 [Parameter(Mandatory)][string]$LockScript,
 [Parameter(Mandatory)][string]$StatusPath
)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(Test-Path -LiteralPath $OutputDirectory){throw 'Fresh output required'}
if(@(& git -C $root status --porcelain).Count){throw 'Commit frozen sources first'}
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$paths=@('GpuSensorContracts.cs','GpuSensorDeterministicGenerator.cs','GpuSensorBenchmarkFixtures.cs','GpuSensorBenchmarkOracle.cs','GpuSensorIndexUpdateTrace.cs') | ForEach-Object {Join-Path $root ('Packages/com.summit.gpu-sensor-pipeline/Runtime/'+$_)}
$paths+=Join-Path $root 'PublicBenchmarks/UnityGpuIntegration/Assets/Runtime/IntegrationFixture.cs'
$paths+=Join-Path $PSScriptRoot 'CapacityReplay.cs'
$inputs=@($paths)+@((Join-Path $PSScriptRoot 'Run-CapacityReplay.ps1'),(Join-Path $PSScriptRoot 'CausalPlan.md'),(Join-Path $root 'PublicBenchmarks/UnityGpuIntegration/Assets/Runtime/IntegrationContent.cs'))
foreach($scene in @('hotspot-dynamic','streaming-switch')){
 $inputs+=Join-Path $SceneEvidence ('oracles-v1/0-'+$scene+'/expected.bin')
 $inputs+=Join-Path $FocusedEvidence ('diagnostic-v4/'+$scene+'-phases-off/history.bin')
}
$hashes=@($inputs|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}})
$hashes | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'inputs-sha256.json')
$receipt=[ordered]@{status='running';kind='deterministic software mechanism audit; no performance measurement';sourceSha=(& git -C $root rev-parse HEAD).Trim();pid=$PID;runtime=[Runtime.InteropServices.RuntimeInformation]::FrameworkDescription;powerShell=$PSVersionTable.PSVersion.ToString();startUtc=[DateTime]::UtcNow.ToString('o');results=@();failure=$null}
function Save { $receipt|ConvertTo-Json -Depth 10|Set-Content (Join-Path $OutputDirectory 'receipt.json') }
Save
try {
 & $LockScript -Action {
  $s=Get-Content -LiteralPath $StatusPath -Raw|ConvertFrom-Json -Depth 20
  $s.phase='Compiling and executing frozen deterministic capacity replay under shared lock'
  $s.currentProcess=@{pid=$PID;label='capacity-replay';startUtc=$receipt.startUtc}
  $s.updatedUtc=[DateTime]::UtcNow.ToString('o');$s|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $StatusPath
  $dll=Join-Path $OutputDirectory 'CapacityReplay.dll'
  Add-Type -Path $paths -OutputAssembly $dll -CompilerOptions '/optimize+'
  Add-Type -Path $dll
  $receipt.assemblySha256=(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
  foreach($scene in @('hotspot-dynamic','streaming-switch')){
   $oracle=Join-Path $SceneEvidence ('oracles-v1/0-'+$scene+'/expected.bin')
   $gpu=Join-Path $FocusedEvidence ('diagnostic-v4/'+$scene+'-phases-off/history.bin')
   $receipt.results+= [Summit.IndexCostDiagnostics.CapacityReplay]::Run($scene,$oracle,$gpu,(Join-Path $OutputDirectory ($scene+'.csv')))
   Save
  }
  foreach($item in $hashes){if((Get-FileHash -LiteralPath $item.path -Algorithm SHA256).Hash -ne $item.sha256){throw 'Input changed during replay'}}
 }
 $receipt.status='complete'
 $receipt.screeningPassed=@($receipt.results | Where-Object {!$_.screeningPassed}).Count -eq 0
}catch{$receipt.status='failed';$receipt.failure=$_.ToString();throw}
finally{
 $receipt.endUtc=[DateTime]::UtcNow.ToString('o');Save
 $s=Get-Content -LiteralPath $StatusPath -Raw|ConvertFrom-Json -Depth 20
 $s.currentProcess=$null;$s.phase='Capacity mechanism replay '+$receipt.status;$s.updatedUtc=[DateTime]::UtcNow.ToString('o')
 $s|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $StatusPath
}
