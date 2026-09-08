"""Compose real, separately recorded Players with explicitly historical statistics.

Requires Python 3.10+, Pillow, matplotlib, ffmpeg and the published focused-costs
scene evidence. No timings are taken from the paced recordings. No frames are
independent statistical replicates. Run with --help for portable input paths.
"""
import argparse
import csv
import hashlib
import json
import math
import statistics as st
import subprocess
from pathlib import Path

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt
from matplotlib.font_manager import FontProperties
from PIL import Image, ImageDraw, ImageFont
from analyze import align_engine, lifecycle_valid

ARMS = ['old-full', 'new-full']
METRICS = ['sceneGpu', 'engineCadenceMs', 'queryGpu', 'indexGpu', 'recordCpuMs']
WORK = ['frame', 'activeCount', 'changedSlots', 'queryCount', 'drawVertices',
        'overlayVertices', 'drawCalls', 'uploadedBytes']
COLORS = ['#60A5FA', '#F5A34A']
BG, FG, MUTED = '#080D17', '#F1F5F9', '#B3C0D3'


def read(p):
    return json.loads(p.read_text(encoding='utf-8-sig'))


def sha(p):
    return hashlib.sha256(p.read_bytes()).hexdigest()


def save(p, obj):
    p.write_text(json.dumps(obj, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')


def value(f, metric):
    v = f[metric]
    return v['milliseconds'] if isinstance(v, dict) else v


def p95(v):
    v = sorted(v)
    x = .95 * (len(v) - 1)
    i = int(x)
    return v[i] + (v[min(i + 1, len(v) - 1)] - v[i]) * (x - i)


def gm(v):
    return math.exp(st.mean(math.log(x) for x in v))


def statistics(traces, metric, protocol):
    # Indexing is process -> paired block -> arm; retain the independent unit.
    means = [[[st.mean(value(f, metric) for f in traces[r, b, a])
               for a in ARMS] for b in range(4)] for r in range(5)]
    ratios = [gm([a / b for a, b in process]) for process in means]
    logs = [math.log(r) for r in ratios]
    half = 2.7764451051977987 * st.stdev(logs) / math.sqrt(5)
    ci = [math.exp(st.mean(logs) - half), math.exp(st.mean(logs) + half)]
    av = [st.mean(x[0] for x in p) for p in means]
    bv = [st.mean(x[1] for x in p) for p in means]
    cvs = [100 * st.stdev(v) / st.mean(v) for v in (av, bv)]
    drift = max(abs(p[-1][0] / p[0][0] - 1) * 100 for p in means)
    tail = gm([gm([p95([value(f, metric) for f in traces[r, b, ARMS[0]]]) /
                   p95([value(f, metric) for f in traces[r, b, ARMS[1]]])
                   for b in range(4)]) for r in range(5)])
    gates = dict(meanCiLowerAboveOne=ci[0] > 1,
                 baselineCv=cvs[0] <= protocol['cvLimitPercent'],
                 candidateCv=cvs[1] <= protocol['cvLimitPercent'],
                 baselineDrift=drift <= protocol['baselineDriftLimitPercent'],
                 p95Ratio=tail >= protocol['p95SpeedRatioMinimum'])
    return dict(metric=metric, baselineMeanMs=st.mean(av), candidateMeanMs=st.mean(bv),
                baselineP95Ms=st.mean(p95([value(f, metric) for f in traces[r, b, ARMS[0]]]) for r in range(5) for b in range(4)),
                candidateP95Ms=st.mean(p95([value(f, metric) for f in traces[r, b, ARMS[1]]]) for r in range(5) for b in range(4)),
                speedRatio=gm(ratios), ci95=ci, baselineCvPercent=cvs[0],
                candidateCvPercent=cvs[1], baselineDriftPercent=drift,
                p95SpeedRatio=tail, processPairedRatios=ratios, gates=gates,
                status='passes-frozen-metric-gates' if all(gates.values()) else 'inconclusive')


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--evidence-root', required=True, type=Path)
    p.add_argument('--capture-root', required=True, type=Path)
    p.add_argument('--output', required=True, type=Path)
    p.add_argument('--ffmpeg', default='ffmpeg')
    p.add_argument('--ffprobe', default='ffprobe')
    p.add_argument('--font', default='C:/Windows/Fonts/msyh.ttc')
    p.add_argument('--bold-font', default='C:/Windows/Fonts/msyhbd.ttc')
    p.add_argument('--render-video', action='store_true')
    args = p.parse_args()
    out, root, cap = args.output.resolve(), args.evidence_root.resolve(), args.capture_root.resolve()
    out.mkdir(parents=True, exist_ok=True)
    inputs = []

    def source(path):
        inputs.append(dict(path=str(path), bytes=path.stat().st_size, sha256=sha(path)))
        return path

    protocol = read(source(root / 'formal-v1/protocol.json'))
    published = read(source(root / 'analysis-v1/analysis.json'))
    assert not published['failures']
    assert protocol['frames'] == 384 and protocol['warmup'] == 64 and protocol['blocks'] == 4
    traces, raw_rows, coverage, workload = {}, [], {a: [] for a in ARMS}, None
    formal_sources, formal_histories = set(), 0
    for rep in range(5):
        folder = root / f'formal-v1/{rep}-streaming-switch'
        result = read(source(folder / 'result.json'))
        cfg = result['config']
        formal_sources.add(cfg['sourceSha'])
        assert cfg['seed'] == protocol['processSeeds'][rep]
        assert cfg['frames'] == 384 and cfg['blocks'] == 4 and cfg['warmup'] == 64
        assert cfg['scenario'] == 'streaming-switch' and cfg['arms'] == protocol['arms']
        assert result['status'] == 'completed' and result['formalPerformanceEvidence'] and not result['development']
        assert result['device'] == 'AMD Radeon AI PRO R9700' and result['graphicsApi'] == 'Direct3D12'
        assert len(result['runs']) == 16
        oracle = source(root / f'oracles-v1/{rep}-streaming-switch/expected.bin')
        engine, alignment = align_engine(result)
        assert alignment['status'] == 'aligned' and alignment['ambiguousTimingRecords'] == 0
        for run in result['runs']:
            if run['arm'] not in ARMS:
                continue
            arm, block = run['arm'], run['block']
            assert run['verified'] and run['verifiedDigestWords'] == 3840 and len(run['frames']) == 384
            assert arm == protocol['arms'][protocol['orders'][(block + rep) % 4][run['position']]]
            assert run['oracleSha256'] == sha(oracle)
            history = source(folder / f"{block}-{run['position']}-{arm}.history.bin")
            assert history.read_bytes() == oracle.read_bytes()
            assert lifecycle_valid(run['contentEvents'])
            formal_histories += 1
            identity = [[f[k] for k in WORK] for f in run['frames']]
            if workload is None:
                workload = identity
            assert workload == identity
            for f in run['frames']:
                for metric in ('sceneGpu', 'indexGpu', 'queryGpu'):
                    v = f[metric]
                    assert v['status'] == 'Ready' and v['sourceFrame'] == f['unityFrame']
                    assert math.isclose(v['milliseconds'], (v['endTicks'] - v['beginTicks']) * 1000 / v['frequency'], rel_tol=1e-10)
                raw_rows.append(dict(replicate=rep, seed=cfg['seed'], block=block, arm=arm,
                                     measured=f['frame'] >= 64, **{k: f[k] for k in WORK},
                                     **{m: value(f, m) for m in METRICS}))
            selected = run['frames'][64:]
            traces[rep, block, arm] = selected
            coverage[arm].append(sum(engine.get(f['unityFrame'], {}).get('gpuFrameMs', 0) > 0 for f in selected) / 320)
    assert formal_histories == 40 and len(traces) == 40 and len(formal_sources) == 1
    metrics = {m: statistics(traces, m, protocol) for m in METRICS}
    for m, actual in metrics.items():
        expected = next(x for x in published['comparisons'] if x['scene'] == 'streaming-switch' and
                        x['baseline'] == ARMS[0] and x['candidate'] == ARMS[1] and x['metric'] == m)
        for k in ('speedRatio', 'baselineCvPercent', 'candidateCvPercent', 'baselineDriftPercent', 'p95SpeedRatio'):
            assert math.isclose(actual[k], expected[k], rel_tol=1e-10), (m, k)
        assert all(math.isclose(a, b, rel_tol=1e-10) for a, b in zip(actual['ci95'], expected['ci95']))
        assert actual['gates'] == expected['gates'] and actual['status'] == expected['status']
    assert metrics['engineCadenceMs']['status'] == 'inconclusive'
    assert min(coverage['new-full']) < .95
    capture_sources, videos, capture_audits = set(), [], []
    for arm, name in zip(ARMS, ('baseline-v1', 'candidate-v1')):
        folder = cap / name
        result = read(source(folder / 'player/result.json'))
        receipt = read(source(folder / 'capture-receipt.json'))
        cfg = result['config']
        assert cfg['arms'] == [arm] and cfg['seed'] == protocol['processSeeds'][0]
        assert cfg['showcaseSeconds'] == 45 and cfg['nativeProbeMode'] == 'none' and cfg['mode'] == 'validate'
        assert result['status'] == 'completed' and not result['formalPerformanceEvidence'] and not result['development']
        assert len(result['runs']) == 1 and result['runs'][0]['verified']
        assert lifecycle_valid(result['runs'][0]['contentEvents'])
        assert [[f[k] for k in WORK] for f in result['runs'][0]['frames']] == workload
        assert source(folder / f'player/0-0-{arm}.history.bin').read_bytes() == (root / 'oracles-v1/0-streaming-switch/expected.bin').read_bytes()
        assert all(result[k] == 0 for k in ('nativeFrequencyEvents', 'nativeBeginEvents', 'nativeEndEvents', 'nativeCompletionEvents'))
        capture_sources.add(cfg['sourceSha'])
        video = source(folder / 'summit-streaming-point-cloud.mp4')
        assert sha(video) == receipt['videoSha256'].lower()
        info = json.loads(subprocess.check_output([args.ffprobe, '-v', 'error', '-show_entries',
            'format=duration:stream=width,height,codec_name', '-of', 'json', str(video)]))
        assert abs(float(info['format']['duration']) - 50) <= 1 / 30 + 1e-6 and info['streams'][0]['width'] == 1280
        videos.append(video)
        capture_audits.append(dict(arm=arm, sha256=sha(video), durationSeconds=float(info['format']['duration']), verifiedLogicalFrames=384,
                                  oracleSha256=receipt['oracleSha256'].lower(), sourceSha=cfg['sourceSha']))
    assert len(capture_sources) == 1
    summary = dict(status='verified', scene='streaming-switch', device='AMD Radeon AI PRO R9700',
                   measurementSourceSha=next(iter(formal_sources)), captureSourceSha=next(iter(capture_sources)),
                   independentProcesses=5, blocksPerProcess=4, steadyFramesPerBlock=320,
                   correctFormalFrames=40 * 384, correctCaptureFrames=2 * 384,
                   correctnessScope='Full query and metadata digest history equals CPU oracle; digest comparison is not per-point exact output comparison.',
                   metrics=metrics, engineGpuPositiveCoverage=coverage, captures=capture_audits,
                   timingScope=protocol['scope'], statistics=protocol['statistics'],
                   warning='Stable full-frame benefit is unconfirmed. Charts use historical unpaced measurements, not the live video. Native probes affect execution; OS presentation unavailable.')
    save(out / 'comparison-statistics.json', summary)
    save(out / 'input-manifest.json', inputs)
    with (out / 'chart-data.csv').open('w', newline='', encoding='utf-8') as f:
        writer = csv.DictWriter(f, fieldnames=list(raw_rows[0]))
        writer.writeheader()
        writer.writerows(raw_rows)
    save(out / 'chart-contract.json', dict(surface='1920x1080 MP4 and standalone PNG',
        question='How do the two equal-work query paths differ in instrumented scene GPU time and engine cadence?',
        takeaway='Narrow instrumented interval passes prior gates; cadence does not establish stable full-frame gains.',
        family='line', variant='all 20 raw traces per arm plus per-logical-frame median, log-ms axis',
        temporalPoints=320, rowCount=len(raw_rows), independentStatisticalUnits=5,
        palettePolicy='hard two-root cap', colors=COLORS, nonColor='baseline solid; candidate dashed',
        delivery='static Matplotlib charts in video; full unsmoothed trajectories, no clipped spikes',
        sourceDate='2026-09-07 UTC / 2026-09-08 Singapore', footage='separately captured on 2026-09-08; no time stretching; display-paced'))

    font = FontProperties(fname=args.font)
    plt.rcParams.update({'font.family': font.get_name(), 'font.size': 12, 'text.color': FG,
                         'axes.labelcolor': MUTED, 'xtick.color': MUTED, 'ytick.color': MUTED,
                         'axes.edgecolor': '#536175', 'axes.facecolor': BG, 'figure.facecolor': BG})
    for metric, title in [('sceneGpu', 'GPU：清屏 + 索引 + 查询 + 摘要 + 绘制（带探针）'), ('engineCadenceMs', '引擎逻辑帧间隔 / 含等待（非 OS 呈现）')]:
        fig, ax = plt.subplots(figsize=(6.2, 2.3), dpi=100)
        fig.subplots_adjust(left=.12, right=.95, top=.83, bottom=.25)
        for arm, color, style, label in zip(ARMS, COLORS, ['-', '--'], ['基线', '候选']):
            series = [[value(f, metric) for f in traces[r, b, arm]] for r in range(5) for b in range(4)]
            for values in series:
                ax.plot(range(64, 384), values, color=color, lw=.5, alpha=.23, linestyle=style)
            ax.plot(range(64, 384), [st.median(v[i] for v in series) for i in range(320)],
                    color=color, lw=1.6, linestyle=style, label=label)
        ax.set_yscale('log')
        ax.set_ylim((.1, 10) if metric == 'sceneGpu' else (.1, 100))
        ax.set_xlim(64, 383)
        ax.set_xticks([64, 128, 192, 256, 320, 383])
        ax.set_yticks([.1, 1, 10] if metric == 'sceneGpu' else [.1, 1, 10, 100])
        ax.set_yticklabels(['0.1', '1', '10'] if metric == 'sceneGpu' else ['0.1', '1', '10', '100'])
        ax.minorticks_off()
        ax.grid(axis='y', color='#314056', lw=.5)
        ax.set_ylabel('毫秒 · 对数轴', fontproperties=font)
        ax.set_xlabel('逻辑帧序号（64–383；前 64 帧预热排除）', fontproperties=font)
        ax.set_title(title, loc='left', fontproperties=font, fontsize=12, pad=11)
        ax.spines[['top', 'right']].set_visible(False)
        ax.legend(loc='upper right', frameon=False, ncol=2, prop=font)
        # Assert every retained point is inside the visible axis range.
        lo, hi = ax.get_ylim()
        assert all(lo <= value(f, metric) <= hi for frames in traces.values() for f in frames)
        fig.savefig(out / f'{metric}.png', dpi=100)
        plt.close(fig)

    canvas = Image.new('RGB', (1920, 1080), BG)
    d = ImageDraw.Draw(canvas)

    def text(x, y, s, size=24, color=FG, bold=False):
        ft = ImageFont.truetype(args.bold_font if bold else args.font, size)
        assert d.textbbox((x, y), s, font=ft)[2] <= 1900, s
        d.text((x, y), s, fill=color, font=ft)

    text(32, 15, 'SUMMIT  |  同一工作负载：基线 / 候选对照', 37, bold=True)
    text(32, 65, 'AMD Radeon AI PRO R9700 · Unity 6000.5.2f1 · D3D12 · 262,144 槽位 · 9 个空间查询 · 流式加载', 23, MUTED)
    text(32, 104, '整帧稳定收益尚未确认：这段视频不证明“优化后更流畅”。', 29, '#F5A34A', True)
    text(32, 150, '基线  CellSerial + 全量索引重建', 26, COLORS[0], True)
    text(976, 150, '候选  BatchedPointScanWave + 全量索引重建（opt-in）', 25, COLORS[1], True)
    # Real 1280x720 recordings are uniformly scaled to 912x513, with no crop.
    d.rectangle((32, 191, 944, 704), fill='#05080E')
    d.rectangle((976, 191, 1888, 704), fill='#05080E')
    text(32, 710, '上方两段均为真实运行录制；同种子 928201、同 384 帧轨迹。分别录制后并排，45 秒演示节奏不用于计时。', 22, MUTED)
    text(32, 742, '左上 9 条带 = 各查询命中数（对数高度）；不是 FPS、GPU 利用率或耗时。', 22, MUTED)
    text(32, 784, '历史测量 2026-09-08 SGT（非实时）：5 进程 × 4 区组 × 320 稳态帧 / 版本；细线原始、粗线中位数', 23, bold=True)
    canvas.paste(Image.open(out / 'sceneGpu.png'), (22, 832))
    canvas.paste(Image.open(out / 'engineCadenceMs.png'), (651, 832))
    # Compact statistical panel is separate from the two trace panels.
    g, c = metrics['sceneGpu'], metrics['engineCadenceMs']
    text(1300, 830, f"GPU 区间均值：{g['baselineMeanMs']:.3f} → {g['candidateMeanMs']:.3f} ms", 23)
    text(1300, 866, f"基线/候选 {g['speedRatio']:.2f}  [95% CI {g['ci95'][0]:.2f}, {g['ci95'][1]:.2f}]", 21, MUTED)
    text(1300, 900, f"帧间隔均值：{c['baselineMeanMs']:.3f} → {c['candidateMeanMs']:.3f} ms", 23)
    text(1300, 936, f"基线/候选 {c['speedRatio']:.2f}  [95% CI {c['ci95'][0]:.2f}, {c['ci95'][1]:.2f}]", 21, MUTED)
    text(1300, 972, '帧间隔未过：候选 CV 10.58%；基线漂移 18.11%', 20, '#F5A34A')
    text(1300, 1004, 'GPU 区间非完整引擎 GPU；完整 GPU 计时缺测', 20, MUTED)
    text(1300, 1036, '录制 768 帧 + 历史 15,360 帧：摘要均匹配 oracle', 20, MUTED)
    canvas.save(out / 'comparison-layout.png')

    # Optional composition keeps both recordings at their original elapsed rate.
    # Curves stay static: they must never masquerade as live video-frame timings.
    if args.render_video:
        dest = out / 'summit-streaming-comparison.mp4'
        if dest.exists():
            raise FileExistsError('Preserve prior videos; choose a fresh output directory')
        command = [args.ffmpeg, '-hide_banner', '-nostdin', '-loop', '1', '-framerate', '30',
                   '-i', str(out / 'comparison-layout.png'), '-i', str(videos[0]), '-i', str(videos[1]),
                   '-filter_complex', '[1:v]scale=912:513,setsar=1[b];[2:v]scale=912:513,setsar=1[c];[0:v][b]overlay=32:191:shortest=1[t];[t][c]overlay=976:191:shortest=1[v]',
                   '-map', '[v]', '-t', '49.9', '-r', '30', '-an', '-c:v', 'libx264', '-preset', 'fast',
                   '-crf', '18', '-pix_fmt', 'yuv420p', '-movflags', '+faststart', str(dest)]
        save(out / 'encode-command.json', command)
        with (out / 'encode.log').open('w', encoding='utf-8') as log:
            subprocess.run(command, stdout=log, stderr=subprocess.STDOUT, check=True, timeout=300)
        save(out / 'video-receipt.json', dict(video=dest.name, bytes=dest.stat().st_size,
             sha256=sha(dest), durationSeconds=49.9, sourceRecordings=capture_audits,
             caveat=summary['warning']))
    print(json.dumps(dict(status='verified-and-rendered', formalFrames=40 * 384, captureFrames=768,
                         sceneGpuRatio=metrics['sceneGpu']['speedRatio'], cadenceStatus=metrics['engineCadenceMs']['status'])))


if __name__ == '__main__':
    main()
