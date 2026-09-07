[CmdletBinding()]
param(
    [string]$UnityEditorPath = 'C:/Program Files/Unity/Hub/Editor/6000.5.2f1/Editor/Unity.exe',
    [string]$OutputPath = ''
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$commit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source commit.' }
$status = @(& git -C $repositoryRoot status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect source changes.' }
$compilerPath = Join-Path (Split-Path -Parent $UnityEditorPath) 'Data/Tools/UnityShaderCompiler.exe'
$inputs = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'Packages'), (Join-Path $repositoryRoot 'Assets') -Recurse -File |
    Where-Object { $_.Extension -in '.compute', '.hlsl', '.shader' })
$shaders = @($inputs | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($repositoryRoot, $_.FullName).Replace('\', '/');
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
$manifestText = ($shaders | ForEach-Object { $_.path + '=' + $_.sha256 }) -join "`n"
$hasher = [Security.Cryptography.SHA256]::Create()
try { $shaderIdentity = [Convert]::ToHexString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($manifestText))) }
finally { $hasher.Dispose() }
$adapters = @(Get-CimInstance Win32_VideoController | Select-Object Name, PNPDeviceID, DriverVersion, DriverDate)
$result = [ordered]@{
    schemaVersion = 1; capturedUtc = [DateTime]::UtcNow.ToString('o'); repositoryRoot = $repositoryRoot
    commit = $commit; dirty = $status.Count -ne 0; gitStatus = $status
    projectVersion = @(Get-Content -LiteralPath (Join-Path $repositoryRoot 'ProjectSettings/ProjectVersion.txt'))
    unityExecutable = $UnityEditorPath
    unityExecutableSha256 = (Get-FileHash -LiteralPath $UnityEditorPath -Algorithm SHA256).Hash
    shaderCompiler = $compilerPath
    shaderCompilerSha256 = (Get-FileHash -LiteralPath $compilerPath -Algorithm SHA256).Hash
    sourceShaderIdentity = $shaderIdentity; shaders = $shaders; adapters = $adapters
    timestampPluginSha256 = (Get-FileHash -LiteralPath (Join-Path $repositoryRoot 'Packages/com.summit.gpu-timestamps/Runtime/Plugins/x86_64/SummitGpuTimestamps.dll') -Algorithm SHA256).Hash
    measurementNote = 'Environment inventory only. Actual API/device, compiled Player identity, cold/warm state, raw timings and correctness must come from the selected benchmark run.'
}
$json = $result | ConvertTo-Json -Depth 8
if ($OutputPath) {
    $OutputPath = [IO.Path]::GetFullPath($OutputPath)
    if (Test-Path -LiteralPath $OutputPath) { throw "Refusing to overwrite environment evidence: $OutputPath" }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
    [IO.File]::WriteAllText($OutputPath, $json, [Text.UTF8Encoding]::new($false))
}
else { $json }
