"""Compose the preselected live recordings without speed changes or timing replay.

Requires Pillow and ffmpeg/ffprobe. The white task-start marker must be preceded
by captured black frames. Every subsequent source interval retains its original
duration; only the constant start offset is removed.
"""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont


def read(p):
    return json.loads(p.read_text(encoding='utf-8-sig'))


def save(p, obj):
    p.write_text(json.dumps(obj, indent=2, ensure_ascii=False)+'\n', encoding='utf-8')


def alignment(video, ffmpeg, ffprobe):
    frame_info = json.loads(subprocess.check_output([ffprobe, '-v', 'error', '-select_streams', 'v:0',
        '-show_frames', '-show_entries', 'frame=best_effort_timestamp_time', '-of', 'json', str(video)]))
    times = [float(f['best_effort_timestamp_time']) for f in frame_info['frames']]
    pixels = subprocess.check_output([ffmpeg, '-v', 'error', '-i', str(video),
        '-vf', 'crop=16:8:1232:16,scale=1:1,format=gray', '-fps_mode', 'passthrough', '-f', 'rawvideo', '-'])
    assert len(pixels) == len(times)
    first = next(i for i, pixel in enumerate(pixels) if pixel >= 235)
    assert first >= 20 and all(p < 20 for p in pixels[:first-1])
    assert all(p >= 235 for p in pixels[first:])
    assert times[-1] - times[first] >= 50
    return dict(firstWhiteFrame=first, previousFrameSeconds=times[first-1], markerSeconds=times[first],
                captureIntervalUncertaintySeconds=times[first]-times[first-1], encodedFrames=len(times),
                sourceEndSeconds=times[-1], rule='First visible white task-start marker after captured black preroll; constant offset only')


def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--matrix', type=Path, required=True);p.add_argument('--analysis', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True);p.add_argument('--ffmpeg', default='ffmpeg');p.add_argument('--ffprobe', default='ffprobe')
    p.add_argument('--font', default='C:/Windows/Fonts/msyh.ttc');p.add_argument('--bold-font', default='C:/Windows/Fonts/msyhbd.ttc')
    p.add_argument('--diagnostic', action='store_true', help='Local audit video only; visibly label failed publication gates')
    p.add_argument('--capture-check-root', type=Path, help='Separate DXGI reliability checks, never a replacement confirmation pair')
    args=p.parse_args();out=args.output.resolve();out.mkdir(parents=True,exist_ok=False)
    analysis=read(args.analysis)
    assert (analysis['publicationEligible'] or args.diagnostic) and not analysis['fullFrameSmoothnessClaim']
    videos=[args.matrix/f'0-on-{arm}/recording.mp4' for arm in ['old-full','new-full']]
    if args.capture_check_root:
        assert args.diagnostic
        videos=[args.capture_check_root/f'dxgi-capture-check-{arm}-v1/recording.mp4' for arm in ['baseline','candidate']]
    aligns=[alignment(v,args.ffmpeg,args.ffprobe) for v in videos]
    pair=analysis['recordedPair']
    if args.capture_check_root:
        pair=[dict(finishMs=read(v.parent/'player/result.json')['finishMs']) for v in videos]
    for row,video,al in zip(pair,videos,aligns):
        result=read(video.parent/'player/result.json')
        receipt=read(video.parent/'receipt.json')
        assert hashlib.sha256(video.read_bytes()).hexdigest()==receipt['videoSha256']
        assert row['finishMs']/1000 < 50 and result['verified']
        al['hostFirstMarkerRepaintAfterStartSeconds']=(result['firstMarkerRepaintTicks']-result['startTicks'])/result['clockFrequency']
        al['sourceSha256']=receipt['videoSha256'];al['finishMs']=row['finishMs']
        assert al['hostFirstMarkerRepaintAfterStartSeconds'] < .1
        # Do not pretend visible marker alignment is exact hardware presentation alignment.
        assert al['captureIntervalUncertaintySeconds'] < .1
    save(out/'alignment.json',aligns)
    bg='#080D17';fg='#F1F5F9';muted='#B6C3D6'
    canvas=Image.new('RGB',(1920,1080),bg);draw=ImageDraw.Draw(canvas)
    def text(d,x,y,s,size=28,color=fg,bold=False):
        font=ImageFont.truetype(args.bold_font if bold else args.font,size)
        assert d.textbbox((x,y),s,font=font)[2] <= 1904,s
        d.text((x,y),s,font=font,fill=color)
    text(draw,32,18,'GPU 实际完成驱动的任务进度与积压',44,bold=True)
    text(draw,32,80,'R9700 · 固定热点压力场景 · 262,144 点 / 9 查询 · 每秒到达 60 批 · 两版分别真实运行录制',26,muted)
    text(draw,32,122,'仅供核验：统计门槛未通过，部分运行失焦；不作为稳定收益证明。' if args.diagnostic else '按任务开始标记对齐；保持原速。下方进度来自当次 GPU 结果读回与校验。',25)
    text(draw,32,714,'看这三项：完成数、积压量、结果延迟',32,bold=True)
    for x,title,lines in [
        (32,'真实完成',['青色任务块：GPU 结果已读回','并通过原始 CPU oracle 摘要校验']),
        (672,'真实积压',['橙色等待，黄色执行中','任务全部保留，不丢弃、不合并']),
        (1312,'结果延迟',['从计划到达到结果可用','包括排队、执行、读回和校验'])]:
        text(draw,x,774,title,30,bold=True)
        for i,line in enumerate(lines):text(draw,x,822+36*i,line,23,muted)
    text(draw,32,932,'两版使用相同完整索引重建、相同输入轨迹、相同单任务并发上限。',26)
    text(draw,32,980,'这里验证任务交付收益；不将它写成画面 FPS 或整帧流畅度提升。',25,muted)
    canvas.save(out/'layout.png')
    card=Image.new('RGB',(1920,376),bg);d=ImageDraw.Draw(card)
    text(d,32,14,'本段真实观察 / 独立复验未通过发布门槛' if args.diagnostic else '本段录像与独立复验结果',34,bold=True)
    text(d,32,76,f"本段全部结果可用：基线 {pair[0]['finishMs']/1000:.3f} 秒 / 候选 {pair[1]['finishMs']/1000:.3f} 秒",32)
    if args.capture_check_root:
        text(d,32,133,'本片是独立 DXGI 录屏可靠性检查，两边各 384 批结果均匹配原始 oracle。',27)
        text(d,32,181,'不替换正式样本；没有把本次数据混入此前五组配对统计。',27)
        text(d,32,245,'此前 20 个正式 Player 过程正确性通过，但基线波动与漂移未过门槛。',26)
    else:
        for i,c in enumerate(analysis['comparisons']):
            mode='开录屏' if c['capture'] else '关录屏'
            text(d,32,133+48*i,f"{mode} · 5 组独立配对：完成时间比（基线/候选）{c['finishRatio']:.3f}，95% CI [{c['ci95'][0]:.3f}, {c['ci95'][1]:.3f}]",28)
        text(d,32,245,'20 个独立 Player 过程 / 7,680 批任务：GPU 查询与元数据摘要均匹配原始 oracle。',26)
    text(d,32,299,'基线 CV、漂移超限且存在失焦记录。差异不能据此归结为已确认的稳定优化收益。' if args.diagnostic else '结论限于本固定热点任务流；包含排队与异步读回成本，不是整帧流畅度结论。',25,muted)
    card.save(out/'results-card.png')
    # Results appear only after both selected recordings have completed, without
    # trimming their work or compressing the slower path's elapsed time.
    results_at=max(row['finishMs']/1000 for row in pair)+2
    assert results_at<48
    filt=(f"[1:v]trim=start={aligns[0]['markerSeconds']:.9f},setpts=PTS-STARTPTS,scale=928:522,setsar=1[b];"
          f"[2:v]trim=start={aligns[1]['markerSeconds']:.9f},setpts=PTS-STARTPTS,scale=928:522,setsar=1[c];"
          "[0:v][b]overlay=16:170:shortest=1[t];[t][c]overlay=976:170:shortest=1[u];"
          f"[u][3:v]overlay=0:704:enable='gte(t,{results_at:.9f})'[v]")
    command=[args.ffmpeg,'-hide_banner','-nostdin','-loop','1','-framerate','60','-i',str(out/'layout.png'),
             '-i',str(videos[0]),'-i',str(videos[1]),'-loop','1','-framerate','60','-i',str(out/'results-card.png'),
             '-filter_complex',filt,'-map','[v]','-t','50','-an','-c:v','libx264','-preset','fast','-crf','18',
             '-pix_fmt','yuv420p','-movflags','+faststart',str(out/'gpu-completion-comparison.mp4')]
    save(out/'encode-command.json',command)
    with (out/'encode.log').open('w',encoding='utf-8') as f:subprocess.run(command,stdout=f,stderr=subprocess.STDOUT,check=True,timeout=300)
    video=out/'gpu-completion-comparison.mp4'
    save(out/'receipt.json',dict(video=video.name,bytes=video.stat().st_size,sha256=hashlib.sha256(video.read_bytes()).hexdigest(),
         diagnosticOnly=args.diagnostic,publicationEligible=analysis['publicationEligible'],
         durationSeconds=50,sourcePair='Separate DXGI reliability-check pair, not substituted for formal replicate 0' if args.capture_check_root else 'replicate 0 capture-on, fixed before confirmation',resultsCardAtSeconds=results_at,
         alignment=aligns,timeScale=1,interpolation=False,staticHistoricTimingReplay=False,
         caveat='Compositor holds captured frames until the next original timestamp; output encoding fps is not measured display fps. GPU completion is host-observed verified readback.'))
    print(json.dumps(dict(status='composed',video=str(video),resultsAtSeconds=results_at,alignment=aligns)))


if __name__=='__main__':main()
