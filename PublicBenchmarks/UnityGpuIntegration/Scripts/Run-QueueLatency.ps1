[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$BuildRoot,
 [Parameter(Mandatory)][string]$OutputRoot,
 [Parameter(Mandatory)][string]$Oracle,
 [ValidateSet('old-full','new-full')][string]$Arm,
 [uint32]$Seed=928201,[int]$Replicate=0,
 [switch]$Capture,[string]$Ffmpeg='ffmpeg'
)
$ErrorActionPreference='Stop'
$out=[IO.Path]::GetFullPath($OutputRoot);$build=[IO.Path]::GetFullPath($BuildRoot)
if(Test-Path -LiteralPath $out){throw 'Fresh output required.'}
$att=Get-Content -LiteralPath (Join-Path $build 'build-attestation.json') -Raw|ConvertFrom-Json
if($att.status -ne 'built' -or $att.development -or $att.sourceDirty){throw 'Clean Release build required.'}
foreach($f in $att.files){if((Get-FileHash -LiteralPath (Join-Path $build $f.path)).Hash.ToLowerInvariant() -ne $f.sha256){throw 'Frozen Player changed.'}}
New-Item -ItemType Directory -Path $out|Out-Null
$run=Join-Path $out 'player';New-Item -ItemType Directory -Path $run|Out-Null
$config=[ordered]@{arm=$Arm;seed=$Seed;replicate=$Replicate;sourceSha=$att.sourceSha;output=$run;oracle=[IO.Path]::GetFullPath($Oracle)}
$cfg=Join-Path $out 'config.json';$config|ConvertTo-Json|Set-Content -LiteralPath $cfg
$receipt=[ordered]@{status='prepared';sourceSha=$att.sourceSha;capture=[bool]$Capture;oracleSha256=(Get-FileHash $Oracle).Hash.ToLowerInvariant();arm=$Arm;seed=$Seed;replicate=$Replicate}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class QueueCaptureWindow {
 [StructLayout(LayoutKind.Sequential)]public struct Rect {public int Left,Top,Right,Bottom;}
 [StructLayout(LayoutKind.Sequential)]public struct Point {public int X,Y;}
 [DllImport("user32.dll")]public static extern bool GetClientRect(IntPtr w,out Rect r);
 [DllImport("user32.dll")]public static extern bool ClientToScreen(IntPtr w,ref Point p);
 [DllImport("user32.dll")]public static extern bool SetWindowPos(IntPtr w,IntPtr after,int x,int y,int width,int height,uint flags);
 [DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr w);
}
'@
try {
 & (Join-Path $PSScriptRoot 'Invoke-Serialized.ps1') -Action {
  $player=$null;$recorder=$null
  try {
   $receipt.startedUtc=[DateTime]::UtcNow.ToString('o')
   $pa=@('-force-d3d12','-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-queue-config',('"'+$cfg+'"'),'-logFile',('"'+(Join-Path $run 'player.log')+'"'))
   # The requested real-run video and its no-recorder controls use the same visible owned window.
   $player=Start-Process -FilePath (Join-Path $build 'Player/Integration.exe') -ArgumentList $pa -PassThru
   $receipt.playerPid=$player.Id;$receipt.playerArguments=$pa
   $deadline=[DateTime]::UtcNow.AddSeconds(90)
   while(!(Test-Path -LiteralPath (Join-Path $run 'capture-ready.txt'))){if($player.HasExited -or [DateTime]::UtcNow -gt $deadline){throw 'Player not ready.'};Start-Sleep -Milliseconds 100}
   $player.Refresh();$hwnd=$player.MainWindowHandle
   if($hwnd -eq [IntPtr]::Zero){throw 'Owned window unavailable.'}
   if(![QueueCaptureWindow]::SetWindowPos($hwnd,[IntPtr](-1),32,32,0,0,0x0041)){throw 'Position failed.'}
   [void][QueueCaptureWindow]::SetForegroundWindow($hwnd)
   Start-Sleep -Milliseconds 300
   $rect=New-Object QueueCaptureWindow+Rect;$origin=New-Object QueueCaptureWindow+Point
   if(![QueueCaptureWindow]::GetClientRect($hwnd,[ref]$rect) -or ![QueueCaptureWindow]::ClientToScreen($hwnd,[ref]$origin)){throw 'Rectangle unavailable.'}
   if($rect.Right-$rect.Left -ne 1280 -or $rect.Bottom-$rect.Top -ne 720 -or $origin.X -lt 0 -or $origin.Y -lt 0){throw 'Unexpected client area.'}
   $receipt.rectangle=@{x=$origin.X;y=$origin.Y;width=1280;height=720}
   if($Capture){
    $ff=(Get-Command $Ffmpeg).Source;$receipt.ffmpeg=$ff;$receipt.ffmpegSha256=(Get-FileHash $ff).Hash.ToLowerInvariant()
    $video=Join-Path $out 'recording.mp4';$progress=Join-Path $out 'encode-progress.txt'
    $args=@('-hide_banner','-nostdin','-f','gdigrab','-framerate','60','-draw_mouse','0','-offset_x',[string]$origin.X,'-offset_y',[string]$origin.Y,'-video_size','1280x720','-i','desktop','-t','35','-an','-c:v','libx264','-preset','fast','-crf','18','-pix_fmt','yuv420p','-fps_mode','vfr','-movflags','+faststart','-stats_period','0.1','-progress',('"'+$progress+'"'),('"'+$video+'"'))
    $receipt.captureArguments=$args
    $recorder=Start-Process -FilePath $ff -ArgumentList $args -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $out 'ffmpeg.stdout.log') -RedirectStandardError (Join-Path $out 'ffmpeg.log')
    $receipt.recorderPid=$recorder.Id
   }
   # Pre-start only, equal in both modes. No artificial waits in the active task interval.
   Start-Sleep -Milliseconds 2000
   if($Capture -and ($recorder.HasExited -or !(Test-Path -LiteralPath $progress))){throw 'Recorder did not capture pre-start frames.'}
   $receipt.signalUtc=[DateTime]::UtcNow.ToString('o')
   Set-Content -LiteralPath (Join-Path $run 'capture-start.signal') -Value $receipt.signalUtc
   $deadline=[DateTime]::UtcNow.AddSeconds(95)
   while(!(Test-Path -LiteralPath (Join-Path $run 'work-completed.signal'))){if($player.HasExited -or [DateTime]::UtcNow -gt $deadline){throw 'Workload incomplete or failed.'};Start-Sleep -Milliseconds 100}
   if($Capture){
    if(!$recorder.WaitForExit(45000) -or $recorder.ExitCode -ne 0){throw 'Recorder failed.'}
    $receipt.videoSha256=(Get-FileHash $video).Hash.ToLowerInvariant();$receipt.videoBytes=(Get-Item $video).Length
   }
   Set-Content -LiteralPath (Join-Path $run 'capture-stop.signal') -Value 'owned runner finished'
   if(!$player.WaitForExit(15000) -or $player.ExitCode -ne 0){throw 'Player failed to exit.'}
   $result=Get-Content -LiteralPath (Join-Path $run 'result.json') -Raw|ConvertFrom-Json
   if($result.status -ne 'completed' -or !$result.verified -or $result.completed -ne 384){throw 'Full correctness required.'}
   if((Get-FileHash -LiteralPath (Join-Path $run 'actual.history.bin')).Hash.ToLowerInvariant() -ne $receipt.oracleSha256){throw 'History/oracle hash mismatch.'}
   $receipt.status='completed';$receipt.finishMs=$result.finishMs;$receipt.playerExitCode=$player.ExitCode
  } finally {
   if($recorder -and !$recorder.HasExited){$recorder.Kill();$recorder.WaitForExit()}
   if($player -and !$player.HasExited){$player.Kill();$player.WaitForExit()}
  }
 }
}catch{$receipt.status='failed';$receipt.error=$_.Exception.ToString();throw}
finally{$receipt.endedUtc=[DateTime]::UtcNow.ToString('o');$receipt|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $out 'receipt.json')}
