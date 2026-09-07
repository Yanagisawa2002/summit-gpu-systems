param([Parameter(Mandatory)][scriptblock]$Action)
$mutex=[Threading.Mutex]::new($false,'Local\CodexR9700VNextUnityGpu')
$held=$false
try {
    try {$held=$mutex.WaitOne()} catch [Threading.AbandonedMutexException] {$held=$true}
    & $Action
} finally {if($held){$mutex.ReleaseMutex()};$mutex.Dispose()}
