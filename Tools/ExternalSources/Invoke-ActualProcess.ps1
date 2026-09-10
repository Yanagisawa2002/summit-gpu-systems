[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$FilePath,
    [Parameter(Mandatory)][string[]]$ProcessArguments,
    [Parameter(Mandatory)][string]$ReceiptPrefix
)
# Invoke only inside Invoke-ActualStage: that caller owns the shared execution mutex.
$ErrorActionPreference='Stop'
foreach($suffix in @('.process.json','.stdout.log','.stderr.log')){
    if(Test-Path -LiteralPath ($ReceiptPrefix+$suffix)){throw "Process evidence exists: $ReceiptPrefix$suffix"}
}
if($ProcessArguments | Where-Object {$_.Contains('"') -or $_.EndsWith('\')}){throw 'Arguments must not contain quotes or end with a backslash.'}
$quotedArguments=($ProcessArguments | ForEach-Object {'"'+$_+'"'}) -join ' '
$receipt=[ordered]@{executable=[IO.Path]::GetFullPath($FilePath);sha256=(Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash.ToLowerInvariant();arguments=$ProcessArguments;startedUtc=[DateTime]::UtcNow.ToString('o');exited=$false}
$process=Start-Process -FilePath $FilePath -ArgumentList $quotedArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput ($ReceiptPrefix+'.stdout.log') -RedirectStandardError ($ReceiptPrefix+'.stderr.log')
$receipt['pid']=$process.Id
try {
    $receipt['processorAffinityHex']=$process.ProcessorAffinity.ToInt64().ToString('X')
    $receipt['priorityClass']=$process.PriorityClass.ToString()
} catch {$receipt['processPropertiesReadError']=$_.Exception.Message}
$receipt|ConvertTo-Json -Depth 5|Set-Content -LiteralPath ($ReceiptPrefix+'.process.json') -Encoding utf8
$process.WaitForExit()
$receipt['exitCode']=$process.ExitCode
$receipt['exited']=$process.HasExited
$receipt['finishedUtc']=[DateTime]::UtcNow.ToString('o')
$receipt|ConvertTo-Json -Depth 5|Set-Content -LiteralPath ($ReceiptPrefix+'.process.json') -Encoding utf8
if($process.ExitCode -ne 0){throw "Process $($process.Id) exited $($process.ExitCode); see $ReceiptPrefix"}
