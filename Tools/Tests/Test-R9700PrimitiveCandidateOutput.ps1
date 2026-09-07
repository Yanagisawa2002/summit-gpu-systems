[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ReportDirectory)
$ErrorActionPreference = 'Stop'
$config = Get-Content (Join-Path $ReportDirectory 'config.json') -Raw | ConvertFrom-Json
$validation = @(Import-Csv (Join-Path $ReportDirectory 'validation.csv'))
$resources = @(Import-Csv (Join-Path $ReportDirectory 'candidate-resources.csv'))
$capabilities = @(Import-Csv (Join-Path $ReportDirectory 'candidate-capabilities.csv'))
if ($resources.Count -eq 0) { throw 'No algorithm cases were executed.' }
foreach ($case in $resources) {
    $checks = @($validation | Where-Object caseId -eq $case.caseId)
    if ($checks.Count -ne 2 -or @($checks | Where-Object passed -ne '1').Count -ne 0 -or
        @($checks.phase | Select-Object -Unique).Count -ne 2) {
        throw "Missing/failed warmup and final oracle validation: $($case.caseId)"
    }
    if ([int]$case.tileSize -ne [int]$case.threads * [int]$case.elementsPerThread) { throw 'Geometry mismatch.' }
    if ([int]$case.keyBits -ne [int]$config.keyBitCount) { throw 'Key-bit workload mismatch.' }
    if ([long]$case.instanceScratchBytes -lt [long]$case.candidateScratchBytes) { throw 'Scratch accounting mismatch.' }
}
foreach ($op in @($validation | Group-Object operation)) {
    if (@($op.Group.resultHash | Select-Object -Unique).Count -ne 1) {
        throw "Cross-candidate or phase hash mismatch for $($op.Name)"
    }
}
foreach ($candidate in $capabilities) {
    $rows = @($resources | Where-Object candidateId -eq $candidate.candidateId)
    if ($candidate.supported -eq 'False' -and $rows.Count -ne 0) { throw 'Unsupported candidate executed.' }
    if ($candidate.supported -eq 'True' -and $rows.Count -eq 0) { throw 'Supported candidate was not exercised.' }
}
Write-Output "Candidate output verified: $($resources.Count) algorithm cases; $($validation.Count) exact oracle validations; identical per-operation hashes."
