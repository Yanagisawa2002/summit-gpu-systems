[CmdletBinding()]
param([ValidateRange(2, 10000)][int]$BatchCount = 64, [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $taskRoot 'Reports/GpuDeadlineScheduler/runtime-simulation' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$sources = @(
    'Packages/com.summit.gpu-deadline-scheduler/Runtime/GpuDeadlineContracts.cs',
    'Packages/com.summit.gpu-deadline-scheduler/Runtime/GpuRuntimeCostEstimator.cs',
    'Packages/com.summit.gpu-deadline-scheduler/Runtime/GpuRuntimeScheduler.cs',
    'Assets/GpuDeadlineSchedulerBenchmark/Runtime/GpuRuntimeSchedulerTrace.cs'
) | ForEach-Object { Join-Path $taskRoot $_ }
Add-Type -Path $sources
$results = foreach ($scenario in [GpuRuntimeSchedulerTrace]::Scenarios) {
    foreach ($fifo in @($true, $false)) {
        $result = [GpuRuntimeSchedulerTrace]::Simulate($scenario, $BatchCount, $fifo)
        if ($result.completed -ne $result.offered) { throw 'Incomplete replay' }
        $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory "$scenario-$($result.variant).json")
        $result | Select-Object scenario, variant, timingDomain, offered, completed, criticalP99Us, criticalMissRate, makespanUs, maxBackgroundWaitUs, starvedBackground, backpressureAttempts, acceptedCostSamples, planningAllocatedBytes
    }
}
$results | Export-Csv -NoTypeInformation -LiteralPath (Join-Path $OutputDirectory 'summary.csv')
$results | Format-Table scenario, variant, completed, criticalP99Us, maxBackgroundWaitUs, acceptedCostSamples
Write-Output 'Synthetic timing only; these results do not measure R9700 performance or admit async.'
