[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$FixtureRoot,
    [string]$LockPath = (Join-Path $PSScriptRoot '..\UPSTREAM_BENCHMARK_LOCK.json')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-FixtureGitText {
    param([Parameter(Mandatory = $true)][string[]]$GitArguments)

    $output = @(& git -C $script:ResolvedFixtureRoot @GitArguments 2>&1)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($GitArguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }
    return ($output -join "`n").Trim()
}

function Get-FixtureGitBlobBytes {
    param([Parameter(Mandatory = $true)][string]$BlobId)

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'git'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $null = $startInfo.ArgumentList.Add('-C')
    $null = $startInfo.ArgumentList.Add($script:ResolvedFixtureRoot)
    $null = $startInfo.ArgumentList.Add('cat-file')
    $null = $startInfo.ArgumentList.Add('blob')
    $null = $startInfo.ArgumentList.Add($BlobId)

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stdout = [IO.MemoryStream]::new()
    $stderr = [IO.MemoryStream]::new()
    try {
        if (-not $process.Start()) {
            throw 'Failed to start git cat-file.'
        }
        $stdoutCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
        $stderrCopy = $process.StandardError.BaseStream.CopyToAsync($stderr)
        $process.WaitForExit()
        [Threading.Tasks.Task]::WaitAll(@($stdoutCopy, $stderrCopy))
        if ($process.ExitCode -ne 0) {
            $message = [Text.Encoding]::UTF8.GetString($stderr.ToArray()).Trim()
            throw "git cat-file blob $BlobId failed: $message"
        }
        return ,$stdout.ToArray()
    }
    finally {
        $stdout.Dispose()
        $stderr.Dispose()
        $process.Dispose()
    }
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return [Convert]::ToHexString($algorithm.ComputeHash($Bytes))
    }
    finally {
        $algorithm.Dispose()
    }
}

function Assert-Hex {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][int]$Length,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($Value -notmatch ('^[0-9a-fA-F]{' + $Length + '}$')) {
        throw "$Name must contain exactly $Length hexadecimal characters."
    }
}

$script:ResolvedFixtureRoot = (Resolve-Path -LiteralPath $FixtureRoot).Path
$resolvedLockPath = (Resolve-Path -LiteralPath $LockPath).Path
$actualFixtureRoot = Invoke-FixtureGitText @('rev-parse', '--show-toplevel')
if (-not [IO.Path]::GetFullPath($actualFixtureRoot).Equals(
        [IO.Path]::GetFullPath($script:ResolvedFixtureRoot),
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "FixtureRoot is not the Git worktree root: $actualFixtureRoot"
}

$lock = Get-Content -Raw -LiteralPath $resolvedLockPath | ConvertFrom-Json
if ([int]$lock.schemaVersion -ne 1 -or
    [string]$lock.suite -cne 'gpu-systems.external-brg-shooter') {
    throw 'The benchmark lock has an unsupported schema or suite.'
}

$sourceCommit = [string]$lock.upstream.commit
Assert-Hex $sourceCommit 40 'upstream.commit'
if ((Invoke-FixtureGitText @('cat-file', '-t', $sourceCommit)) -cne 'commit') {
    throw "The pinned upstream object is not a commit: $sourceCommit"
}

& git -C $script:ResolvedFixtureRoot merge-base --is-ancestor $sourceCommit HEAD
if ($LASTEXITCODE -ne 0) {
    throw 'The fixture HEAD does not descend from the pinned upstream commit.'
}

$targets = [Collections.Generic.List[object]]::new()
$targets.Add([pscustomobject][ordered]@{
    path = [string]$lock.upstream.licensePath
    expectedBlobSha1 = [string]$lock.upstream.licenseGitBlobSha1
    expectedSha256 = [string]$lock.upstream.licenseCanonicalSha256
    expectedBytes = $null
})
foreach ($entry in @($lock.upstreamFileReceipts)) {
    $targets.Add([pscustomobject][ordered]@{
        path = [string]$entry.path
        expectedBlobSha1 = [string]$entry.gitBlobSha1
        expectedSha256 = [string]$entry.canonicalSha256
        expectedBytes = [int64]$entry.repositoryBytes
    })
}

$seenPaths = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
$receipts = [Collections.Generic.List[object]]::new()
foreach ($target in $targets) {
    $path = ([string]$target.path).Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($path) -or
        $path.StartsWith('/', [StringComparison]::Ordinal) -or
        $path.Split('/') -contains '..' -or
        -not $seenPaths.Add($path)) {
        throw "Invalid or duplicate locked path: '$path'"
    }
    Assert-Hex ([string]$target.expectedBlobSha1) 40 "$path blob SHA-1"
    Assert-Hex ([string]$target.expectedSha256) 64 "$path SHA-256"

    $actualBlobSha1 = Invoke-FixtureGitText @(
        'rev-parse', '--verify', "$sourceCommit`:$path")
    if ($actualBlobSha1 -cne ([string]$target.expectedBlobSha1).ToLowerInvariant()) {
        throw "$path Git blob mismatch: expected $($target.expectedBlobSha1), got $actualBlobSha1"
    }
    if ((Invoke-FixtureGitText @('cat-file', '-t', $actualBlobSha1)) -cne 'blob') {
        throw "$path does not resolve to a Git blob."
    }

    [byte[]]$bytes = Get-FixtureGitBlobBytes $actualBlobSha1
    if ($null -ne $target.expectedBytes -and
        $bytes.LongLength -ne [int64]$target.expectedBytes) {
        throw "$path byte length mismatch: expected $($target.expectedBytes), got $($bytes.LongLength)"
    }
    $actualSha256 = Get-Sha256Hex $bytes
    if ($actualSha256 -cne ([string]$target.expectedSha256).ToUpperInvariant()) {
        throw "$path SHA-256 mismatch: expected $($target.expectedSha256), got $actualSha256"
    }

    $receipts.Add([pscustomobject][ordered]@{
        path = $path
        repositoryBytes = $bytes.LongLength
        gitBlobSha1 = $actualBlobSha1
        canonicalSha256 = $actualSha256
        accepted = $true
    })
}

[pscustomobject][ordered]@{
    schemaVersion = 1
    suite = 'gpu-systems.external-brg-shooter.upstream-lock-validation'
    accepted = $true
    fixtureRoot = $script:ResolvedFixtureRoot
    lockPath = $resolvedLockPath
    upstreamCommit = $sourceCommit
    fixtureHead = Invoke-FixtureGitText @('rev-parse', 'HEAD')
    validatedFileCount = $receipts.Count
    files = [object[]]$receipts.ToArray()
} | ConvertTo-Json -Depth 5
