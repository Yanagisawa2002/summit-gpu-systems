param(
    [Parameter(Mandatory=$true)][string]$FxcPath,
    [Parameter(Mandatory=$true)][string]$DxcPath,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repoPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoPath 'Artifacts/SensorQueryFunctional/shaders' }
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory
$shaderDirectory = Join-Path $repoPath 'Packages/com.summit.gpu-sensor-pipeline/Runtime/Resources/GpuSensorPipeline'
$shaderNames = @('GpuSensorCellSpanQuery.compute', 'GpuSensorCellSpanQueryWave.compute',
    'GpuSensorCompactIndexView.compute', 'GpuSensorIncrementalIndex.compute', 'GpuSensorIncrementalIndexOptimized.compute')
$compiled = 0
foreach ($shaderName in $shaderNames) {
    $shaderPath = Join-Path $shaderDirectory $shaderName
    $kernelNames = Select-String -LiteralPath $shaderPath -Pattern '^#pragma kernel (\w+)' |
        ForEach-Object { $_.Matches[0].Groups[1].Value }
    # Unity importer directives are not HLSL. Preserve all other source lines,
    # defines and includes; compile the same kernels with warnings as errors.
    $compilePath = Join-Path $OutputDirectory "$shaderName.hlsl"
    $compileSource = Get-Content -LiteralPath $shaderPath | ForEach-Object {
        if ($_ -match '^#pragma (kernel|target|use_dxc)\b') { '' } else { $_ }
    }
    Set-Content -LiteralPath $compilePath -Value $compileSource -Encoding utf8
    foreach ($kernelName in $kernelNames) {
        $binaryPath = Join-Path $OutputDirectory "$shaderName.$kernelName.bin"
        if ($shaderName -eq 'GpuSensorCellSpanQueryWave.compute') {
            & $DxcPath -T cs_6_0 -E $kernelName -Ges -WX -I $shaderDirectory -Fo $binaryPath $compilePath
        } else {
            & $FxcPath /nologo /T cs_5_0 /E $kernelName /Ges /WX /I $shaderDirectory /Fo $binaryPath $compilePath
        }
        if ($LASTEXITCODE -ne 0) { throw "Shader compilation failed: $shaderName/$kernelName" }
        $compiled++
    }
}
Write-Output "PASS: $compiled shader entry points compiled. No shader execution or GPU dispatch."
