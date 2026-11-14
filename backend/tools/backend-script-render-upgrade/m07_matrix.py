#!/usr/bin/env python3
"""Run the finite M07 three-round matrix serially; retain timings and pixel comparisons."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import statistics
import subprocess
import time


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n")


def stats(values):
    ordered = sorted(values)
    return {"n": len(values), "medianMs": statistics.median(values),
            "p95Ms": ordered[math.ceil(len(ordered) * .95) - 1]}


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument("--output", type=Path, required=True)
    cli.add_argument("--runner", type=Path, required=True)
    cli.add_argument("--backend", type=Path, required=True)
    cli.add_argument("--source-commit", required=True)
    cli.add_argument("--icd", type=Path, required=True)
    cli.add_argument("--v1-reference-dir", type=Path, default=None)
    args = cli.parse_args()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    runner = args.runner.resolve()
    backend = args.backend.resolve()
    v1_reference_dir = args.v1_reference_dir.resolve() if args.v1_reference_dir is not None else None
    environment = {**os.environ, "VK_DRIVER_FILES": str(args.icd.resolve()), "VK_ICD_FILENAMES": str(args.icd.resolve())}
    save(output / "inputs.json", {"sourceCommit": args.source_commit,
        "scope": "Already-built M07 binaries and unchanged M07 samples. No rebuild. Three fresh processes per v2 sample; three cycles per process. No OS or driver cache reset.",
        "runner": str(runner), "binarySha256": {p.name: sha(p) for p in sorted(runner.parent.glob('*.dll'))},
        "icdSha256": sha(args.icd), "v1ReferenceDir": str(v1_reference_dir) if v1_reference_dir is not None else None,
        "cpuLoadStart": os.getloadavg()})
    runs = []
    samples = ['WordEmphasis', 'RepeatEmphasis', 'ClusterExit']
    for round_index in range(1, 4):
        for sample in samples:
            sdk = 'v2'
            name = f"r{round_index}-{sdk}-{sample}"
            path = output / (name + '.json')
            command = ['dotnet', str(runner), str(backend), sdk, sample, str(path), '3']
            record = {"round": round_index, "sdk": sdk, "sample": sample, "file": path.name,
                      "command": command, "loadBefore": os.getloadavg(), "startUnix": time.time()}
            record['gpuBusyBefore'] = {str(p): p.read_text().strip() for p in Path('/sys/class/drm').glob('card*/device/gpu_busy_percent')}
            print('RUN ' + name, flush=True)
            with (output / (name + '.log')).open('x') as log:
                completed = subprocess.run(command, env=environment, stdout=log, stderr=subprocess.STDOUT, timeout=780)
            record.update(exitCode=completed.returncode, endUnix=time.time())
            runs.append(record)
            save(output / 'runs.json', runs)
            if completed.returncode:
                raise RuntimeError(f'{name} failed; evidence retained.')

    reports = {(r['round'], r['sdk'], r['sample']): json.loads((output / r['file']).read_text()) for r in runs}
    comparisons = []
    determinism = []
    for sample in samples:
        baseline = reports[(1, 'v2', sample)]
        for round_index in (2, 3):
            current = reports[(round_index, 'v2', sample)]
            for stage, key in [('preparation', 'sha256'), ('rendering', 'bgraSha256')]:
                expected = {r['timeUs']: r[key] for r in baseline[stage]}
                actual = {r['timeUs']: r[key] for r in current[stage]}
                determinism.append({'sdk': 'v2', 'sample': sample, 'round': round_index,
                                    'stage': stage, 'passed': actual == expected})
        if sample == 'ClusterExit' or v1_reference_dir is None:
            continue
        for round_index in range(1, 4):
            v1_report = json.loads((v1_reference_dir / f'r{round_index}-v1-{sample}.json').read_text())
            left = {r['timeUs']: r['pixelAsset'] for r in v1_report['rendering']}
            right = {r['timeUs']: r['pixelAsset'] for r in reports[(round_index, 'v2', sample)]['rendering']}
            for time_us, asset in left.items():
                a = (v1_reference_dir / f'r{round_index}-v1-{sample}.json.assets' / asset).read_bytes()
                b = (output / f'r{round_index}-v2-{sample}.json.assets' / right[time_us]).read_bytes()
                if len(a) != len(b):
                    raise RuntimeError('Pixel byte lengths differ.')
                differences = [abs(x-y) for x, y in zip(a, b)]
                maximum = max(differences)
                comparisons.append({'sample': sample, 'round': round_index, 'timeUs': time_us,
                    'maxChannelDifference': maximum, 'channelsOver2': sum(d > 2 for d in differences),
                    'passed': maximum <= 2})
    timings = []
    for sdk, sample in [('v2', 'WordEmphasis'), ('v2', 'RepeatEmphasis'), ('v2', 'ClusterExit')]:
        group = [reports[(r,sdk,sample)] for r in range(1,4)]
        item = {'sdk':sdk, 'sample':sample, 'setup':{}, 'queries':[]}
        for key in ('packageLoadMs','compileMs','prepareSessionMs','renderSessionMs'):
            item['setup'][key] = stats([r[key] for r in group])
        for stage in ('preparation','rendering'):
            for index, time_us in enumerate(group[0]['fixture']['cycle']):
                for cycle_type in ('first','repeat'):
                    records = [r for report in group for r in report[stage] if r['cycleIndex']==index
                               and (r['cycleNumber']==0 if cycle_type=='first' else r['cycleNumber']>0)]
                    row = {'stage':stage,'cycleIndex':index,'timeUs':time_us,'cycleType':cycle_type,
                           **stats([r['wallMs'] for r in records])}
                    if stage == 'preparation':
                        row['geometrySamples'] = [r['geometrySamples'] for r in records]
                    item['queries'].append(row)
        timings.append(item)
    result = {'status':'passed' if all(x['passed'] for x in comparisons+determinism) else 'failed',
              'pixelToleranceChannel8bit':2,'v1ReferenceDir':str(v1_reference_dir) if v1_reference_dir is not None else None,
              'comparisons':comparisons,'crossProcessDeterminism':determinism,
              'timings':timings,'formalPerformanceThreshold':'user confirmed 2026-11-04; run m07_thresholds.py with the explicit archived baseline',
              'scope':'Finite one-token CJK/Ruby parity. Separate setup/query metrics; no HTTP/UI timing or arbitrary v1 equivalence claim.'}
    save(output / 'summary.json', result)
    print(json.dumps({'status':result['status'],'runs':len(runs),'pixelComparisons':len(comparisons)},ensure_ascii=False))
    return 0 if result['status']=='passed' else 1


if __name__ == '__main__':
    raise SystemExit(main())
