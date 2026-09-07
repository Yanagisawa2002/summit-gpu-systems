[CmdletBinding()]
param([ValidateSet('correctness', 'smoke', 'comparison')][string]$Mode = 'correctness',
    [string]$OutputPath = '', [ValidateRange(1, 100)][int]$Repetitions = 1)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'Tools/ResidencyPlannerHarness/ResidencyPlannerHarness.csproj'
if (!$OutputPath) { $OutputPath = Join-Path $root 'Reports/ResidencyPlanner/results.json' }
if ($Mode -eq 'correctness') { & dotnet run --project $project -c Release; if ($LASTEXITCODE -ne 0) { throw 'Planner correctness failed.' }; return }
for ($i = 0; $i -lt $Repetitions; $i++) {
    $target = if ($Repetitions -eq 1) { $OutputPath } else { $OutputPath.Replace('.json', "-$i.json") }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent ([IO.Path]::GetFullPath($target))) | Out-Null
    @{ sourceCommit = (& git -C $root rev-parse HEAD).Trim(); dirtyWorktree = [bool](& git -C $root status --porcelain); mode = $Mode; repetition = $i; utc = [DateTime]::UtcNow.ToString("o"); runtime = (& dotnet --version) } | ConvertTo-Json | Set-Content ($target + '.metadata.json')
    $order = if ($i % 2 -eq 0) { 'forward' } else { 'reverse' }
    & dotnet run --project $project -c Release -- $Mode $target $order
    if ($LASTEXITCODE -ne 0) { throw 'Planner comparison failed.' }
}
