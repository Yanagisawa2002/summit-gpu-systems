"""Minimal English edit of the same real-time DXGI task-delivery recordings.

No new performance samples: preserve captured metrics, task tiles and source
intervals. Crop explanatory paragraphs; replace only the static tile legend.
Requires Pillow and the adjacent compose_queue_video alignment auditor.
"""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont
from compose_queue_video import alignment, read, save


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--capture-root', required=True, type=Path)
    p.add_argument('--analysis', required=True, type=Path)
    p.add_argument('--output', required=True, type=Path)
    p.add_argument('--ffmpeg', default='ffmpeg')
    p.add_argument('--ffprobe', default='ffprobe')
    p.add_argument('--font', default='C:/Windows/Fonts/segoeui.ttf')
    p.add_argument('--bold-font', default='C:/Windows/Fonts/segoeuib.ttf')
    a = p.parse_args()
    out = a.output.resolve()
    out.mkdir(parents=True, exist_ok=False)
    analysis = read(a.analysis)
    assert not analysis['publicationEligible'] and not analysis['fullFrameSmoothnessClaim']
    videos = [a.capture_root/f'dxgi-capture-check-{arm}-v1/recording.mp4'
              for arm in ['baseline', 'candidate']]
    aligns = []
    for video in videos:
        r, receipt = read(video.parent/'player/result.json'), read(video.parent/'receipt.json')
        digest = hashlib.sha256(video.read_bytes()).hexdigest()
        assert digest == receipt['videoSha256']
        assert r['verified'] and r['completed'] == 384 and r['finishMs'] < 50000
        assert r['config']['sourceSha'] == analysis['sourceSha']
        assert hashlib.sha256((video.parent/'player/actual.history.bin').read_bytes()).hexdigest() == r['oracleSha256']
        al = alignment(video, a.ffmpeg, a.ffprobe)
        assert al['captureIntervalUncertaintySeconds'] < .1
        al.update(sourceSha256=digest, finishMs=r['finishMs'])
        aligns.append(al)
    canvas = Image.new('RGB', (1920, 720), '#080D17')
    d = ImageDraw.Draw(canvas)
    def text(x, y, value, size=24, color='#B6C3D6', bold=False):
        font = ImageFont.truetype(a.bold_font if bold else a.font, size)
        assert d.textbbox((x, y), value, font=font)[2] < 1904
        d.text((x, y), value, font=font, fill=color)
    text(32, 12, 'GPU Task Delivery', 44, '#F1F5F9', True)
    text(32, 74, 'AMD Radeon AI PRO R9700  |  262,144 points  |  384 jobs  |  60 arrivals/s')
    text(32, 130, 'BASELINE  /  CellSerial', 29, '#59A8FF', True)
    text(992, 130, 'CANDIDATE  /  BatchedPointScanWave', 29, '#FAA647', True)
    text(32, 576, 'Tasks: cyan = verified   orange = waiting   yellow = in flight   dark = not yet due', 23)
    text(32, 618, 'Same workload. Separate recordings. Original speed. Progress follows verified GPU readback.', 24, '#F1F5F9')
    text(32, 666, 'Observed task delivery; stable speedup not confirmed.', 23)
    canvas.save(out/'layout.png')
    filters = []
    for i, al in enumerate(aligns, 1):
        # Hide the old static legend only. Keep every live number and all 384
        # task tiles from the recording; do not synthesize completion progress.
        filters.append(f'[{i}:v]trim=start={al["markerSeconds"]:.9f},setpts=PTS-STARTPTS,'
                       'crop=1280:500:0:140,drawbox=x=0:y=285:w=1280:h=40:color=0x050910:t=fill,'
                       f'scale=928:364,setsar=1[s{i}]')
    filters += ['[0:v][s1]overlay=16:188:shortest=1[t]',
                '[t][s2]overlay=976:188:shortest=1[v]']
    video = out/'gpu-task-delivery-english.mp4'
    command = [a.ffmpeg, '-hide_banner', '-nostdin', '-loop', '1', '-framerate', '60',
               '-i', str(out/'layout.png'), '-i', str(videos[0]), '-i', str(videos[1]),
               '-filter_complex', ';'.join(filters), '-map', '[v]', '-t', '50', '-an',
               '-c:v', 'libx264', '-preset', 'fast', '-crf', '18', '-pix_fmt', 'yuv420p',
               '-movflags', '+faststart', str(video)]
    save(out/'encode-command.json', command)
    with (out/'encode.log').open('w', encoding='utf-8') as log:
        subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=300)
    save(out/'receipt.json', dict(video=video.name, bytes=video.stat().st_size,
         sha256=hashlib.sha256(video.read_bytes()).hexdigest(), durationSeconds=50,
         alignment=aligns, timeScale=1, interpolation=False, liveMetricsUnchanged=True,
         sourcePair='Same separate DXGI capture-reliability pair as the prior local video',
         performanceConfirmation=analysis['status'], stableSpeedupConfirmed=False,
         publicDiagnosticEditRequested=True, newPerformanceSamples=False,
         edits='English layout; spatial crop; static legend removed; constant start offset only',
         caveat='Host-observed verified GPU readback, not display FPS. Source frames held until next original timestamp.'))
    print(json.dumps(dict(video=str(video), status='composed')))


if __name__ == '__main__':
    main()
