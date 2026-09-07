[CmdletBinding()]
param(
    [ValidateSet('Smoke','Primitives','AdaptiveDiscovery','AdaptiveFreeze','AdaptiveEvaluation','Query','Index','ResidencyCpu','ResidencyGpu','Scheduler')]
    [string]$Stage = 'Smoke',
    [Parameter(Mandatory=$true)][string]$ValidationLockScript,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [string]$PythonPath = 'python'
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$stageOutput = Join-Path $OutputRoot $Stage
if (Test-Path -LiteralPath $stageOutput) { throw "Use a fresh stage output: $stageOutput" }
if (-not (Test-Path -LiteralPath $ValidationLockScript -PathType Leaf)) { throw 'Pass the shared Invoke-SerializedValidation.ps1 path.' }
if (@(& git -C $repositoryRoot status --porcelain=v1 --untracked-files=all).Count) {
    throw 'Commit or resolve source changes before starting a staged comparison.'
}
New-Item -ItemType Directory -Force -Path $stageOutput | Out-Null
& (Join-Path $PSScriptRoot 'Get-R9700VNextEnvironment.ps1') -OutputPath (Join-Path $stageOutput 'environment-before.json')
$adaptiveRoot = Join-Path $OutputRoot 'AdaptiveDiscovery'
$matrixPath = Join-Path $OutputRoot 'AdaptiveFreeze/frozen-matrix.json'
$pythonBytecode = $env:PYTHONDONTWRITEBYTECODE
$env:PYTHONDONTWRITEBYTECODE = '1'
try {
    switch ($Stage) {
        'Smoke' {
            & (Join-Path $PSScriptRoot 'Run-R9700PrimitiveCandidates.ps1') -SerializationScript $ValidationLockScript -OutputDirectory (Join-Path $stageOutput 'primitives')
            & (Join-Path $PSScriptRoot 'Run-GpuSensorQueryBenchmark.ps1') -ValidationLockScript $ValidationLockScript -Mode Smoke -OutputDirectory (Join-Path $stageOutput 'query')
            & $ValidationLockScript -Action {
                & (Join-Path $PSScriptRoot 'Run-GpuSensorIndexComparison.ps1') -Mode smoke -SampleFrames 3 -WarmupFrames 1 -Rounds 2 -OutputDirectory (Join-Path $stageOutput 'index')
            }
            & (Join-Path $PSScriptRoot 'Run-GpuAdaptiveRuntimeBenchmark.ps1') -Phase smoke -ValidationLockScript $ValidationLockScript -OutputDirectory (Join-Path $stageOutput 'adaptive')
            & $ValidationLockScript -Action {
                & (Join-Path $PSScriptRoot 'Run-GpuResidencyBenchmark.ps1') -MatrixPreset smoke -CompareLruPolicies -SkipTests -OutputDirectory (Join-Path $stageOutput 'residency')
                & (Join-Path $PSScriptRoot 'Run-GpuRuntimeSchedulerComparison.ps1') -Preset smoke -OutputDirectory (Join-Path $stageOutput 'scheduler')
            }
        }
        'Primitives' {
            & (Join-Path $PSScriptRoot 'Run-R9700PrimitiveCandidates.ps1') -SerializationScript $ValidationLockScript -Comparison -OutputDirectory $stageOutput
        }
        'AdaptiveDiscovery' {
            $first = $true
            foreach ($n in @(262144,1048576)) { foreach ($c in @(16,256,4096)) {
                $run = @{Phase='discovery'; ValidationLockScript=$ValidationLockScript; ElementCount=$n; BinCount=$c;
                    FramesPerSegment=30; Repeats=2; Seed=9701; OutputDirectory=(Join-Path $stageOutput "n$n-c$c"); SkipBuild=(!$first)}
                & (Join-Path $PSScriptRoot 'Run-GpuAdaptiveRuntimeBenchmark.ps1') @run
                $first = $false
            } }
        }
        'AdaptiveFreeze' {
            $reports = @(foreach ($n in @(262144,1048576)) { foreach ($c in @(16,256,4096)) {
                Join-Path $adaptiveRoot "n$n-c$c/runtime-report.json"
            } })
            & $PythonPath (Join-Path $PSScriptRoot 'AdaptiveRuntimeCalibration.py') freeze --reports @reports --output $matrixPath
            if ($LASTEXITCODE) { throw 'Adaptive freeze failed.' }
        }
        'AdaptiveEvaluation' {
            foreach ($n in @(262144,1048576)) { foreach ($c in @(16,256,4096)) {
                $cellOutput = Join-Path $stageOutput "n$n-c$c"
                & (Join-Path $PSScriptRoot 'Run-GpuAdaptiveRuntimeBenchmark.ps1') -Phase evaluation -SkipBuild -ValidationLockScript $ValidationLockScript `
                    -ElementCount $n -BinCount $c -FramesPerSegment 60 -Repeats 2 -Seed 9702 -MatrixPath $matrixPath -OutputDirectory $cellOutput
                & $PythonPath (Join-Path $PSScriptRoot 'AdaptiveRuntimeCalibration.py') summarize --report (Join-Path $cellOutput 'runtime-report.json') --matrix $matrixPath --output (Join-Path $cellOutput 'comparison.json')
                if ($LASTEXITCODE) { throw 'Adaptive evaluation summary failed.' }
            } }
        }
        'Query' {
            & (Join-Path $PSScriptRoot 'Run-GpuSensorQueryBenchmark.ps1') -Mode Compare -ValidationLockScript $ValidationLockScript -ElementCounts 4097,65541,262145 -Warmup 10 -Samples 120 -OutputDirectory $stageOutput
        }
        'Index' {
            & $ValidationLockScript -Action { & (Join-Path $PSScriptRoot 'Run-GpuSensorIndexComparison.ps1') -Mode matrix -SampleFrames 60 -WarmupFrames 12 -Rounds 4 -OutputDirectory $stageOutput }
        }
        'ResidencyCpu' {
            & $ValidationLockScript -Action {
                & (Join-Path $PSScriptRoot 'Run-ResidencyPlannerComparison.ps1') -Mode comparison -Repetitions 4 -OutputPath (Join-Path $stageOutput 'results.json')
            }
        }
        'ResidencyGpu' {
            & $ValidationLockScript -Action {
                foreach ($pair in @(@(384,4096),@(4096,16384),@(32768,65536))) {
                    & (Join-Path $PSScriptRoot 'Run-GpuResidencyBenchmark.ps1') -MatrixPreset formal -CompareLruPolicies -PhysicalSlots $pair[0] -VirtualPages $pair[1] -OutputDirectory (Join-Path $stageOutput "slots-$($pair[0])")
                }
            }
        }
        'Scheduler' {
            & $ValidationLockScript -Action { & (Join-Path $PSScriptRoot 'Run-GpuRuntimeSchedulerComparison.ps1') -Preset formal -OutputDirectory (Join-Path $stageOutput 'runs') }
        }
    }
    & (Join-Path $PSScriptRoot 'Get-R9700VNextEnvironment.ps1') -OutputPath (Join-Path $stageOutput 'environment-after.json')
    if (@(& git -C $repositoryRoot status --porcelain=v1 --untracked-files=all).Count) {
        throw 'Stage produced source changes; inspect environment-after.json before continuing. No source changes were restored automatically.'
    }
    [ordered]@{stage=$Stage;status='complete';completedUtc=[DateTime]::UtcNow.ToString('o');
        qualification=$(if($Stage -eq 'Smoke'){'Correctness/instrumentation only; no performance acceptance.'}else{'Read each harness scope and integrity gates before interpreting results.'})} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stageOutput 'stage.json')
}
finally { $env:PYTHONDONTWRITEBYTECODE = $pythonBytecode }
