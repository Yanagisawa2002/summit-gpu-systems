Set-StrictMode -Version Latest

$script:Utf8NoBom = [Text.UTF8Encoding]::new($false)
$script:SealPropertyName = 'recordSha256'

function ConvertTo-GpuBenchmarkCanonicalValue {
    param([AllowNull()]$Value)

    if ($null -eq $Value -or $Value -is [string] -or
        $Value -is [char] -or $Value -is [bool] -or
        $Value.GetType().IsPrimitive -or $Value -is [decimal] -or
        $Value -is [DateTime] -or $Value -is [DateTimeOffset] -or
        $Value -is [Guid]) {
        return $Value
    }

    if ($Value -is [Collections.IDictionary]) {
        $names = [string[]]@($Value.Keys | ForEach-Object { [string]$_ })
        [Array]::Sort($names, [StringComparer]::Ordinal)
        $result = [ordered]@{}
        foreach ($name in $names) {
            $result[$name] = ConvertTo-GpuBenchmarkCanonicalValue $Value[$name]
        }
        return $result
    }

    if ($Value -is [Collections.IEnumerable]) {
        $items = [Collections.Generic.List[object]]::new()
        foreach ($item in $Value) {
            $items.Add((ConvertTo-GpuBenchmarkCanonicalValue $item))
        }
        return [object[]]$items.ToArray()
    }

    $properties = @($Value.PSObject.Properties | Where-Object {
        $_.MemberType -in @(
            [Management.Automation.PSMemberTypes]::NoteProperty,
            [Management.Automation.PSMemberTypes]::Property,
            [Management.Automation.PSMemberTypes]::AliasProperty,
            [Management.Automation.PSMemberTypes]::ScriptProperty)
    })
    if ($properties.Count -eq 0) {
        return [string]$Value
    }
    $propertyNames = [string[]]@($properties.Name)
    [Array]::Sort($propertyNames, [StringComparer]::Ordinal)
    $objectResult = [ordered]@{}
    foreach ($name in $propertyNames) {
        $objectResult[$name] = ConvertTo-GpuBenchmarkCanonicalValue (
            $Value.PSObject.Properties[$name].Value)
    }
    return $objectResult
}

function Get-GpuBenchmarkCanonicalJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowNull()]$Value,
        [ValidateRange(2, 100)][int]$Depth = 100
    )

    return ConvertTo-GpuBenchmarkCanonicalValue $Value |
        ConvertTo-Json -Depth $Depth -Compress
}

function Get-GpuBenchmarkObjectSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowNull()]$Value)

    $json = Get-GpuBenchmarkCanonicalJson $Value
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString(
            $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($json)))).
            Replace('-', '')
    }
    finally {
        $sha.Dispose()
    }
}

function ConvertTo-GpuBenchmarkRecordWithoutSeal {
    param([Parameter(Mandatory = $true)]$Record)

    $result = [ordered]@{}
    $properties = @($Record.PSObject.Properties)
    if ($Record -is [Collections.IDictionary]) {
        foreach ($key in $Record.Keys) {
            if ([string]$key -cne $script:SealPropertyName) {
                $result[[string]$key] = $Record[$key]
            }
        }
        return $result
    }
    foreach ($property in $properties) {
        if ($property.Name -cne $script:SealPropertyName) {
            $result[$property.Name] = $property.Value
        }
    }
    return $result
}

function New-GpuBenchmarkSealedRecord {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Value)

    $hasSeal = if ($Value -is [Collections.IDictionary]) {
        $Value.Contains($script:SealPropertyName)
    }
    else {
        $null -ne $Value.PSObject.Properties[$script:SealPropertyName]
    }
    if ($hasSeal) {
        throw "Benchmark record already contains '$script:SealPropertyName'."
    }

    $record = ConvertTo-GpuBenchmarkCanonicalValue $Value
    $record[$script:SealPropertyName] = Get-GpuBenchmarkObjectSha256 $record
    return [pscustomobject]$record
}

function Write-GpuBenchmarkAtomicText {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$Text,
        [switch]$CreateNew
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    $parent = Split-Path -Parent $resolved
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $temporary = Join-Path $parent (
        '.' + [IO.Path]::GetFileName($resolved) + '.' +
        [Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $stream = [IO.FileStream]::new(
            $temporary,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        try {
            $bytes = $script:Utf8NoBom.GetBytes($Text)
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }

        if ($CreateNew) {
            [IO.File]::Move($temporary, $resolved)
        }
        elseif (Test-Path -LiteralPath $resolved -PathType Leaf) {
            [IO.File]::Replace($temporary, $resolved, $null)
        }
        else {
            [IO.File]::Move($temporary, $resolved)
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
    return $resolved
}

function Write-GpuBenchmarkAtomicJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path,
        [ValidateRange(2, 100)][int]$Depth = 100,
        [switch]$CreateNew
    )

    $canonical = ConvertTo-GpuBenchmarkCanonicalValue $Value
    $json = ($canonical | ConvertTo-Json -Depth $Depth) +
        [Environment]::NewLine
    return Write-GpuBenchmarkAtomicText `
        -Path $Path `
        -Text $json `
        -CreateNew:$CreateNew
}

function Write-GpuBenchmarkSealedJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path,
        [ValidateRange(2, 100)][int]$Depth = 100,
        [switch]$CreateNew
    )

    $record = New-GpuBenchmarkSealedRecord $Value
    [void](Write-GpuBenchmarkAtomicJson `
        -Value $record `
        -Path $Path `
        -Depth $Depth `
        -CreateNew:$CreateNew)
    return $record
}

function Read-GpuBenchmarkSealedJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string]$ExpectedSuite,
        [int]$ExpectedSchemaVersion = -1
    )

    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Sealed benchmark record is missing: $resolved"
    }
    # PowerShell otherwise auto-converts ISO-8601 strings to DateTime and can
    # normalize away fractional zeroes, changing the canonical sealed payload.
    $record = Get-Content -LiteralPath $resolved -Raw |
        ConvertFrom-Json -DateKind String
    $sealProperty = $record.PSObject.Properties[$script:SealPropertyName]
    if ($null -eq $sealProperty -or
        [string]$sealProperty.Value -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Benchmark record has no valid '$script:SealPropertyName': $resolved"
    }
    $withoutSeal = ConvertTo-GpuBenchmarkRecordWithoutSeal $record
    $expectedSeal = Get-GpuBenchmarkObjectSha256 $withoutSeal
    if (-not [string]::Equals(
            [string]$sealProperty.Value,
            $expectedSeal,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Benchmark record seal is invalid: $resolved"
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSuite) -and
        [string]$record.suite -cne $ExpectedSuite) {
        throw "Benchmark record suite is invalid: $resolved"
    }
    if ($ExpectedSchemaVersion -ge 0 -and
        [int]$record.schemaVersion -ne $ExpectedSchemaVersion) {
        throw "Benchmark record schema is invalid: $resolved"
    }
    return $record
}

function Get-GpuBenchmarkFileSetReceipt {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string[]]$RelativePaths
    )

    $resolvedRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $resolvedRoot -PathType Container)) {
        throw "Benchmark file-set root is missing: $resolvedRoot"
    }
    $normalized = [string[]]@($RelativePaths | ForEach-Object {
        ([string]$_).Replace('\', '/').TrimStart('/')
    })
    if ($normalized.Count -eq 0 -or
        @($normalized | Select-Object -Unique).Count -ne $normalized.Count) {
        throw 'Benchmark file-set paths must be non-empty and unique.'
    }
    [Array]::Sort($normalized, [StringComparer]::Ordinal)
    $entries = [Collections.Generic.List[object]]::new()
    $builder = [Text.StringBuilder]::new()
    foreach ($relative in $normalized) {
        if ([string]::IsNullOrWhiteSpace($relative) -or
            $relative -match '(^|/)\.\.(/|$)') {
            throw "Benchmark file-set path is unsafe: '$relative'."
        }
        $path = [IO.Path]::GetFullPath(
            (Join-Path $resolvedRoot $relative.Replace('/', '\')))
        if (-not $path.StartsWith(
                $resolvedRoot + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase) -or
            -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Benchmark file-set member is missing or outside its root: $path"
        }
        $file = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        $entry = [pscustomobject][ordered]@{
            relativePath = $relative
            lengthBytes = [int64]$file.Length
            sha256 = $hash
        }
        $entries.Add($entry)
        [void]$builder.Append($relative)
        [void]$builder.Append("`0")
        [void]$builder.Append([int64]$file.Length)
        [void]$builder.Append("`0")
        [void]$builder.Append($hash)
        [void]$builder.Append("`n")
    }
    return [pscustomobject][ordered]@{
        schemaVersion = 1
        fileCount = $entries.Count
        combinedSha256 = Get-GpuBenchmarkObjectSha256 $builder.ToString()
        files = [object[]]$entries.ToArray()
    }
}

function Assert-GpuBenchmarkFileSetReceipt {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)]$Receipt
    )

    $paths = [string[]]@($Receipt.files | ForEach-Object {
        [string]$_.relativePath
    })
    $current = Get-GpuBenchmarkFileSetReceipt -Root $Root -RelativePaths $paths
    if ([int]$Receipt.schemaVersion -ne 1 -or
        [int]$Receipt.fileCount -ne $current.fileCount -or
        -not [string]::Equals(
            [string]$Receipt.combinedSha256,
            [string]$current.combinedSha256,
            [StringComparison]::OrdinalIgnoreCase) -or
        (Get-GpuBenchmarkObjectSha256 $Receipt.files) -cne
            (Get-GpuBenchmarkObjectSha256 $current.files)) {
        throw 'Benchmark file-set receipt does not match current files.'
    }
    return $current
}

function Test-GpuBenchmarkLockOwnerActive {
    param([Parameter(Mandatory = $true)]$Record)

    if ([string]$Record.machineName -cne [Environment]::MachineName) {
        return $true
    }
    try {
        $process = Get-Process -Id ([int]$Record.processId) -ErrorAction Stop
        $start = $process.StartTime.ToUniversalTime().ToString('O')
        return $start -ceq [string]$Record.processStartUtc
    }
    catch {
        return $false
    }
}

function Enter-GpuBenchmarkRunLock {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RunContractFingerprint,
        [switch]$RecoverInterrupted
    )

    if ($RunContractFingerprint -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'Run contract fingerprint must be a 64-hex SHA-256 value.'
    }
    $resolved = [IO.Path]::GetFullPath($Path)
    $parent = Split-Path -Parent $resolved
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $recoveredLockPath = ''
    if (Test-Path -LiteralPath $resolved -PathType Leaf) {
        if (-not $RecoverInterrupted) {
            throw "Benchmark run lock already exists: $resolved"
        }
        $prior = Read-GpuBenchmarkSealedJson `
            -Path $resolved `
            -ExpectedSuite 'summit.gpu-benchmark-run-lock' `
            -ExpectedSchemaVersion 1
        if (-not [string]::Equals(
                [string]$prior.runContractFingerprint,
                $RunContractFingerprint,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Interrupted lock belongs to a different run contract.'
        }
        if (Test-GpuBenchmarkLockOwnerActive $prior) {
            throw 'Benchmark lock owner may still be active; recovery refused.'
        }
        $archiveRoot = Join-Path $parent 'interruptions'
        New-Item -ItemType Directory -Path $archiveRoot -Force | Out-Null
        $recoveredLockPath = Join-Path $archiveRoot (
            'run-lock-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff') +
            '.json')
        Move-Item `
            -LiteralPath $resolved `
            -Destination $recoveredLockPath `
            -ErrorAction Stop
    }

    $processStartUtc = (Get-Process -Id $PID).StartTime.
        ToUniversalTime().ToString('O')
    $record = New-GpuBenchmarkSealedRecord ([ordered]@{
        schemaVersion = 1
        suite = 'summit.gpu-benchmark-run-lock'
        runContractFingerprint = $RunContractFingerprint.ToUpperInvariant()
        processId = $PID
        processStartUtc = $processStartUtc
        machineName = [Environment]::MachineName
        acquiredUtc = (Get-Date).ToUniversalTime().ToString('O')
    })
    $stream = $null
    try {
        $stream = [IO.FileStream]::new(
            $resolved,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::Read)
        $text = (ConvertTo-GpuBenchmarkCanonicalValue $record |
            ConvertTo-Json -Depth 20) + [Environment]::NewLine
        $bytes = $script:Utf8NoBom.GetBytes($text)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
        return [pscustomobject][ordered]@{
            Path = $resolved
            Stream = $stream
            Record = $record
            RecoveredLockPath = $recoveredLockPath
        }
    }
    catch {
        if ($null -ne $stream) {
            $stream.Dispose()
        }
        throw
    }
}

function Exit-GpuBenchmarkRunLock {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Handle)

    if ($null -ne $Handle.Stream) {
        $Handle.Stream.Dispose()
    }
    $current = Read-GpuBenchmarkSealedJson `
        -Path ([string]$Handle.Path) `
        -ExpectedSuite 'summit.gpu-benchmark-run-lock' `
        -ExpectedSchemaVersion 1
    if (-not [string]::Equals(
            [string]$current.recordSha256,
            [string]$Handle.Record.recordSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Benchmark run lock changed while it was held.'
    }
    Remove-Item -LiteralPath ([string]$Handle.Path) -Force
}

function Assert-GpuBenchmarkPhaseCoverage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ReceiptsDirectory,
        [Parameter(Mandatory = $true)][string[]]$ExpectedPhaseIds,
        [Parameter(Mandatory = $true)][string]$RunContractFingerprint
    )

    $root = [IO.Path]::GetFullPath($ReceiptsDirectory)
    $allFiles = if (Test-Path -LiteralPath $root -PathType Container) {
        @(Get-ChildItem -LiteralPath $root -File)
    }
    else { @() }
    $nonReceiptFiles = @($allFiles | Where-Object {
        $_.Extension -cne '.json'
    })
    if ($nonReceiptFiles.Count -ne 0) {
        throw 'Benchmark phase receipt directory contains unsealed files.'
    }
    $files = @($allFiles)
    $records = [Collections.Generic.List[object]]::new()
    foreach ($file in $files) {
        $record = Read-GpuBenchmarkSealedJson `
            -Path $file.FullName `
            -ExpectedSuite 'summit.gpu-benchmark-phase' `
            -ExpectedSchemaVersion 1
        if (-not [string]::Equals(
                [string]$record.runContractFingerprint,
                $RunContractFingerprint,
                [StringComparison]::OrdinalIgnoreCase)) {
            throw "Phase receipt belongs to another run contract: $($file.FullName)"
        }
        $records.Add($record)
    }
    $expected = [string[]]@($ExpectedPhaseIds)
    if ($expected.Count -eq 0 -or
        @($expected | Select-Object -Unique).Count -ne $expected.Count) {
        throw 'Expected phase IDs must be non-empty and unique.'
    }
    $duplicates = @($records | Group-Object phaseId | Where-Object Count -ne 1)
    $observedIds = [string[]]@($records | ForEach-Object { [string]$_.phaseId })
    $missing = @($expected | Where-Object { $_ -cnotin $observedIds })
    $unexpected = @($observedIds | Where-Object { $_ -cnotin $expected })
    if ($duplicates.Count -ne 0 -or $missing.Count -ne 0 -or
        $unexpected.Count -ne 0 -or $records.Count -ne $expected.Count) {
        throw 'Benchmark phase coverage has duplicate, missing, or unexpected receipts.'
    }
    return [object[]]$records.ToArray()
}

Export-ModuleMember -Function @(
    'Get-GpuBenchmarkCanonicalJson',
    'Get-GpuBenchmarkObjectSha256',
    'New-GpuBenchmarkSealedRecord',
    'Write-GpuBenchmarkAtomicText',
    'Write-GpuBenchmarkAtomicJson',
    'Write-GpuBenchmarkSealedJson',
    'Read-GpuBenchmarkSealedJson',
    'Get-GpuBenchmarkFileSetReceipt',
    'Assert-GpuBenchmarkFileSetReceipt',
    'Enter-GpuBenchmarkRunLock',
    'Exit-GpuBenchmarkRunLock',
    'Assert-GpuBenchmarkPhaseCoverage')
