Set-StrictMode -Version Latest

function Invoke-GitLines {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    $lines = @(& git -C $ProjectRoot @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw $FailureMessage
    }
    return [string[]]$lines
}

function Get-GpuBenchmarkGitSnapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$ProjectRoot)

    $root = [System.IO.Path]::GetFullPath($ProjectRoot)
    $headLines = @(Invoke-GitLines `
        -ProjectRoot $root `
        -Arguments @('rev-parse', '--verify', 'HEAD') `
        -FailureMessage 'Unable to resolve the benchmark Git HEAD.')
    if ($headLines.Count -ne 1 -or
        $headLines[0] -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'Benchmark Git HEAD is not one full object ID.'
    }
    $branchLines = @(Invoke-GitLines `
        -ProjectRoot $root `
        -Arguments @('rev-parse', '--abbrev-ref', 'HEAD') `
        -FailureMessage 'Unable to resolve the benchmark Git branch.')
    if ($branchLines.Count -ne 1) {
        throw 'Benchmark Git branch query returned an unexpected result.'
    }
    $statusLines = @(Invoke-GitLines `
        -ProjectRoot $root `
        -Arguments @(
            'status',
            '--porcelain=v1',
            '--untracked-files=all',
            '--no-renames') `
        -FailureMessage 'Unable to inspect the benchmark worktree state.')

    return [pscustomobject][ordered]@{
        capturedUtc = (Get-Date).ToUniversalTime().ToString('o')
        head = $headLines[0].ToLowerInvariant()
        branch = $branchLines[0]
        dirty = [bool]($statusLines.Count -ne 0)
        statusLines = [string[]]$statusLines
    }
}

function Test-StringArraysEqual {
    param([string[]]$Left, [string[]]$Right)

    if ($Left.Count -ne $Right.Count) {
        return $false
    }
    for ($index = 0; $index -lt $Left.Count; $index++) {
        if (-not [string]::Equals(
                $Left[$index],
                $Right[$index],
                [System.StringComparison]::Ordinal)) {
            return $false
        }
    }
    return $true
}

function Assert-GitSnapshotCurrent {
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)]$Snapshot
    )

    $current = Get-GpuBenchmarkGitSnapshot -ProjectRoot $ProjectRoot
    if (-not [string]::Equals(
            [string]$current.head,
            [string]$Snapshot.head,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [string]$current.branch,
            [string]$Snapshot.branch,
            [System.StringComparison]::Ordinal) -or
        -not (Test-StringArraysEqual `
            -Left ([string[]]@($current.statusLines)) `
            -Right ([string[]]@($Snapshot.statusLines)))) {
        throw 'Git state changed while benchmark provenance was being validated.'
    }
}

function Restore-KnownUnityBenchmarkDrift {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ProjectRoot,
        [Parameter(Mandatory = $true)]$Snapshot,
        [Parameter(Mandatory = $true)][string]$ExpectedHead
    )

    if ([string]$Snapshot.head -ine $ExpectedHead) {
        throw (
            "Git HEAD changed: expected $ExpectedHead, observed " +
            "$($Snapshot.head).")
    }
    Assert-GitSnapshotCurrent -ProjectRoot $ProjectRoot -Snapshot $Snapshot

    foreach ($line in [string[]]@($Snapshot.statusLines)) {
        throw (
            "Unrecognized Git drift; no files were restored: '$line'. " +
            'Staged, renamed, untracked, or non-allowlisted changes are forbidden.')
    }
    $after = Get-GpuBenchmarkGitSnapshot -ProjectRoot $ProjectRoot
    if ($after.head -ine $ExpectedHead -or
        [string]$after.branch -cne [string]$Snapshot.branch -or
        [bool]$after.dirty) {
        throw 'Worktree is not clean at the expected HEAD after drift restoration.'
    }

    return [pscustomobject][ordered]@{
        restored = $false
        restoredPaths = [string[]]@()
        driftKinds = [string[]]@()
        afterSnapshot = $after
    }
}

function Get-GpuBenchmarkPlayerPayload {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$PlayerPath)

    $resolvedPlayer = (Resolve-Path -LiteralPath $PlayerPath).Path
    $player = Get-Item -LiteralPath $resolvedPlayer
    if ($player.PSIsContainer -or [int64]$player.Length -le 0) {
        throw 'Player executable is missing or empty.'
    }
    $root = $player.Directory.FullName
    $unityPlayerPath = Join-Path $root 'UnityPlayer.dll'
    if (-not (Test-Path -LiteralPath $unityPlayerPath -PathType Leaf) -or
        [int64](Get-Item -LiteralPath $unityPlayerPath).Length -le 0) {
        throw 'Player payload requires a non-empty UnityPlayer.dll.'
    }
    $dataDirectoryName = $player.BaseName + '_Data'
    $dataDirectory = Join-Path $root $dataDirectoryName
    if (-not (Test-Path -LiteralPath $dataDirectory -PathType Container)) {
        throw "Player payload requires '$dataDirectoryName'."
    }
    $reparseItems = @(
        Get-ChildItem -LiteralPath $root -Recurse -Force |
            Where-Object {
                ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
            })
    if ($reparseItems.Count -ne 0) {
        throw 'Player payload may not contain reparse points.'
    }

    $files = @(
        Get-ChildItem -LiteralPath $root -Recurse -Force -File |
            Where-Object {
                $relative = $_.FullName.Substring($root.Length).
                    TrimStart([char[]]@('\', '/')).Replace('\', '/')
                -not [string]::Equals(
                    $relative,
                    'build-summary.txt',
                    [System.StringComparison]::OrdinalIgnoreCase)
            })
    if ($files.Count -eq 0) {
        throw 'Player payload is empty.'
    }
    $dataPrefix = $dataDirectory.TrimEnd('\', '/') +
        [System.IO.Path]::DirectorySeparatorChar
    $dataFiles = @(
        $files | Where-Object {
            $_.FullName.StartsWith(
                $dataPrefix,
                [System.StringComparison]::OrdinalIgnoreCase)
        })
    $dataBytes = [int64](($dataFiles | Measure-Object -Property Length -Sum).Sum)
    if ($dataFiles.Count -eq 0 -or $dataBytes -le 0) {
        throw "Player data directory '$dataDirectoryName' is empty."
    }

    $relativePaths = [string[]]@(
        $files | ForEach-Object {
            $_.FullName.Substring($root.Length).
                TrimStart([char[]]@('\', '/')).Replace('\', '/')
        })
    [System.Array]::Sort(
        $relativePaths,
        [System.StringComparer]::Ordinal)
    $entries = @(
        $relativePaths | ForEach-Object {
            $relativePath = $_
            $file = Get-Item -LiteralPath (
                Join-Path $root ($relativePath.Replace('/', '\')))
            [pscustomobject][ordered]@{
                relativePath = $relativePath
                lengthBytes = [int64]$file.Length
                sha256 =
                    (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            }
        }
    )
    $builder = [System.Text.StringBuilder]::new()
    foreach ($entry in $entries) {
        [void]$builder.Append($entry.relativePath)
        [void]$builder.Append('=')
        [void]$builder.Append($entry.lengthBytes)
        [void]$builder.Append('=')
        [void]$builder.Append($entry.sha256)
        [void]$builder.Append("`n")
    }
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())
        $payloadHash = [System.BitConverter]::ToString(
            $sha256.ComputeHash($bytes)).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
    }
    $executableEntry = @(
        $entries | Where-Object {
            [string]::Equals(
                $_.relativePath,
                $player.Name,
                [System.StringComparison]::OrdinalIgnoreCase)
        })
    if ($executableEntry.Count -ne 1) {
        throw 'Player executable is not represented exactly once in its payload.'
    }

    return [pscustomobject][ordered]@{
        schemaVersion = 1
        root = $root
        playerRelativePath = $player.Name
        dataDirectoryRelativePath = $dataDirectoryName
        excludedRelativePaths = [string[]]@('build-summary.txt')
        fileCount = [int]$entries.Count
        lengthBytes =
            [int64](($entries | Measure-Object -Property lengthBytes -Sum).Sum)
        dataFileCount = [int]$dataFiles.Count
        dataLengthBytes = $dataBytes
        sha256 = $payloadHash
        playerExecutableSha256 = $executableEntry[0].sha256
        playerExecutableLengthBytes = $executableEntry[0].lengthBytes
        files = $entries
    }
}

Export-ModuleMember -Function @(
    'Get-GpuBenchmarkGitSnapshot',
    'Restore-KnownUnityBenchmarkDrift',
    'Get-GpuBenchmarkPlayerPayload'
)
