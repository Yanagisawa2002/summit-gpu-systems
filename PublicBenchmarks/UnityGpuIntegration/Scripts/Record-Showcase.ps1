[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$BuildRoot,
 [Parameter(Mandatory)][string]$OutputRoot,
 [Parameter(Mandatory)][string]$Oracle,
 [string]$Ffmpeg='ffmpeg',
 [ValidateSet('old-full','new-full')][string]$Arm='new-full',
 [uint32]$Seed=920071
)
$ErrorActionPreference='Stop'
$out=[IO.Path]::GetFullPath($OutputRoot);$build=[IO.Path]::GetFullPath($BuildRoot)
if(Test-Path -LiteralPath $out){throw 'Fresh capture output required.'}
$att=Get-Content -LiteralPath (Join-Path $build 'build-attestation.json') -Raw|ConvertFrom-Json
if($att.status -ne 'built' -or $att.development -or $att.sourceDirty){throw 'Clean Release build required.'}
foreach($f in $att.files){if((Get-FileHash -LiteralPath (Join-Path $build $f.path)).Hash.ToLowerInvariant() -ne $f.sha256){throw 'Frozen Player changed.'}}
New-Item -ItemType Directory -Path $out|Out-Null
$run=Join-Path $out 'player';New-Item -ItemType Directory -Path $run|Out-Null
$config=[ordered]@{mode='validate';scenario='streaming-switch';nativeProbeMode='none';showcaseSeconds=45;frames=384;warmup=64;blocks=1;seed=$Seed;processReplicate=0;arms=@($Arm);screenshot=$false;engineTimingAudit=$false;sourceSha=$att.sourceSha;output=$run;oracle=[IO.Path]::GetFullPath($Oracle)}
$cfg=Join-Path $out 'config.json';$config|ConvertTo-Json|Set-Content -LiteralPath $cfg
$ff=(Get-Command $Ffmpeg).Source
$receipt=[ordered]@{status='prepared';sourceSha=$att.sourceSha;kind='real-time visible Player recording; presentation-paced demonstration, not performance evidence';ffmpeg=$ff;ffmpegSha256=(Get-FileHash $ff).Hash;oracleSha256=(Get-FileHash $Oracle).Hash;durationSeconds=50;fps=30}
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class SummitCaptureWindow {
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
  $player=$null;$capture=$null
  try {
   $receipt.startedUtc=[DateTime]::UtcNow.ToString('o')
   $pa=@('-force-d3d12','-screen-fullscreen','0','-screen-width','1280','-screen-height','720','-integration-config',('"'+$cfg+'"'),'-logFile',('"'+(Join-Path $run 'player.log')+'"'))
   # The user requested a real recording. This owned Player must be visible.
   $player=Start-Process -FilePath (Join-Path $build 'Player/Integration.exe') -ArgumentList $pa -PassThru
   $receipt.playerPid=$player.Id;$receipt.playerArguments=$pa
   $deadline=[DateTime]::UtcNow.AddSeconds(60)
   while(!(Test-Path -LiteralPath (Join-Path $run 'capture-ready.txt'))){if($player.HasExited -or [DateTime]::UtcNow -gt $deadline){throw 'Player did not become capture-ready.'};Start-Sleep -Milliseconds 100}
   $player.Refresh();$hwnd=$player.MainWindowHandle
   if($hwnd -eq [IntPtr]::Zero){throw 'Owned Player window unavailable.'}
   if(![SummitCaptureWindow]::SetWindowPos($hwnd,[IntPtr](-1),32,32,0,0,0x0041)){throw 'Cannot position owned capture window.'}
   [void][SummitCaptureWindow]::SetForegroundWindow($hwnd)
   Start-Sleep -Milliseconds 300
   $rect=New-Object SummitCaptureWindow+Rect;$origin=New-Object SummitCaptureWindow+Point
   if(![SummitCaptureWindow]::GetClientRect($hwnd,[ref]$rect) -or ![SummitCaptureWindow]::ClientToScreen($hwnd,[ref]$origin)){throw 'Client rectangle unavailable.'}
   $width=$rect.Right-$rect.Left;$height=$rect.Bottom-$rect.Top
   if($width -ne 1280 -or $height -ne 720 -or $origin.X -lt 0 -or $origin.Y -lt 0){throw 'Unexpected owned client rectangle.'}
   $video=Join-Path $out 'summit-streaming-point-cloud.mp4'
   $args=@('-hide_banner','-nostdin','-f','gdigrab','-framerate','30','-draw_mouse','0','-offset_x',[string]$origin.X,'-offset_y',[string]$origin.Y,'-video_size','1280x720','-i','desktop','-t','50','-an','-c:v','libx264','-preset','fast','-crf','18','-pix_fmt','yuv420p','-movflags','+faststart',('"'+$video+'"'))
   $receipt.captureArguments=$args;$receipt.rectangle=@{x=$origin.X;y=$origin.Y;width=$width;height=$height}
   $capture=Start-Process -FilePath $ff -ArgumentList $args -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $out 'ffmpeg.stdout.log') -RedirectStandardError (Join-Path $out 'ffmpeg.log')
   $receipt.capturePid=$capture.Id;$receipt.captureStartedUtc=[DateTime]::UtcNow.ToString('o')
   Set-Content -LiteralPath (Join-Path $run 'capture-start.signal') -Value $receipt.captureStartedUtc
   if(!$capture.WaitForExit(90000)){throw 'Owned recorder timeout.'}
   if($capture.ExitCode -ne 0){throw "Recorder failed: $($capture.ExitCode)"}
   if(!$player.WaitForExit(60000) -or $player.ExitCode -ne 0){throw 'Player failed or timed out.'}
   $result=Get-Content -LiteralPath (Join-Path $run 'result.json') -Raw|ConvertFrom-Json
   if($result.status -ne 'completed' -or $result.formalPerformanceEvidence -or $result.runs.Count -ne 1 -or !$result.runs[0].verified){throw 'Complete live GPU oracle validation required.'}
   $receipt.videoSha256=(Get-FileHash $video).Hash;$receipt.videoBytes=(Get-Item $video).Length;$receipt.status='recorded-and-oracle-verified';$receipt.playerExitCode=$player.ExitCode;$receipt.captureExitCode=$capture.ExitCode
  } finally {
   if($capture -and !$capture.HasExited){$capture.Kill();$capture.WaitForExit()}
   if($player -and !$player.HasExited){$player.Kill();$player.WaitForExit()}
  }
 }
}catch{$receipt.status='failed';$receipt.error=$_.Exception.ToString();throw}
finally{$receipt.endedUtc=[DateTime]::UtcNow.ToString('o');$receipt|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $out 'capture-receipt.json')}
