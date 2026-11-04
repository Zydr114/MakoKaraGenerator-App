#!/usr/bin/env python3
"""Assess an analyzed, uninstrumented three-round old/new timing run."""
import argparse
import json
import math
from pathlib import Path
import statistics
import sys


def rank(values, p):
    if not values or not 0 < p <= 1:
        raise ValueError('nearest rank requires samples and 0 < p <= 1')
    return sorted(values)[math.ceil(p * len(values)) - 1]


def evaluate(folder):
    manifest = json.loads((folder / 'manifest.json').read_text())
    assessment = json.loads((folder / 'assessment.json').read_text())
    args = manifest['arguments']
    if manifest.get('complete') is not True or assessment.get('valid') is not True or assessment.get('errors') != []:
        raise ValueError('requires a complete run and a valid, error-free assessment')
    variants = args['variants'].split(',')
    scenarios = args['scenarios'].split(',')
    if len(variants) != 2 or set(variants) != {'old', 'new'}:
        raise ValueError('requires exactly old,new; instrumented variants are excluded')
    if not scenarios or any(not s for s in scenarios) or len(set(scenarios)) != len(scenarios):
        raise ValueError('invalid scenarios')
    if type(args['rounds']) is not int or args['rounds'] != 3:
        raise ValueError('requires rounds 1, 2 and 3')
    warmup, measured = args['warmup'], args['frames']
    if type(warmup) is not int or type(measured) is not int or warmup < 0 or measured <= 0:
        raise ValueError('invalid frame counts')
    if manifest.get('monitoringModes', ['off']) != ['off']:
        raise ValueError('timing gate requires monitoring off')
    expected = {(r, s, b, v) for r in (1, 2, 3) for s in scenarios
                for b in ('cpu', 'vulkan') for v in variants}
    records = manifest['runs']
    if any(type(r['round']) is not int for r in records):
        raise ValueError('round must be an integer')
    keys = [(r['round'], r['scenario'], r['backend'], r['variant']) for r in records]
    if len(keys) != len(expected) or set(keys) != expected:
        raise ValueError('incomplete, duplicate or unexpected run matrix')
    groups, settings, total_frames = {}, {}, 0
    for record in records:
        if record.get('monitoringMode', 'off') != 'off' or record.get('exited') is not True or record.get('exitCode') != 0:
            raise ValueError('run must exit successfully with monitoring off')
        relative = Path(record['result'])
        if relative.is_absolute() or len(relative.parts) != 1:
            raise ValueError('result must be a file in the run directory')
        data = json.loads((folder / relative).read_text())
        if (data.get('traceAvailable') is not False or data.get('monitoringMode') != 'off'
                or data['create'].get('trace') is not None):
            raise ValueError('instrumented or monitored result rejected')
        for field, value in (('scenario', record['scenario']), ('backend', record['backend']),
                             ('mode', args['mode']), ('width', args['width']), ('height', args['height']),
                             ('transparent', args['transparent']), ('counterfactual', args['counterfactual']),
                             ('requestedWarmupFrames', warmup), ('requestedMeasuredFrames', measured)):
            if data.get(field) != value:
                raise ValueError('result settings mismatch: ' + field)
        frames = data['frames']
        if len(frames) != warmup + measured or data.get('projectFrameCount') != len(frames):
            raise ValueError('result frame count mismatch')
        for index, frame in enumerate(frames):
            phase = 'cold' if index == 0 else ('seek' if args['mode'] in ('seek', 'matrix') else 'warmup' if index < warmup else 'steady')
            wall = frame['wallMs']
            if frame.get('index') != index or frame.get('phase') != phase:
                raise ValueError('frame index or phase mismatch')
            if frame.get('trace') is not None or 'timeUs' not in frame:
                raise ValueError('unexpected frame trace or missing timestamp')
            if type(wall) not in (int, float) or not math.isfinite(wall) or wall < 0:
                raise ValueError('frame timing must be finite and nonnegative')
        values = [f['wallMs'] for f in frames if f['phase'] == 'steady']
        if not values:
            values = [f['wallMs'] for f in frames if f['phase'] != 'warmup']
        if not values:
            raise ValueError('round contains no measured frames')
        total_frames += len(frames)
        identity = (record['scenario'], record['backend'])
        fields = ('fixtureSha256', 'fixture', 'quality', 'requestedTimesUs', 'deviceProbe')
        if any(field not in data for field in fields):
            raise ValueError('missing comparison settings')
        signature = tuple(json.dumps(data[field], sort_keys=True) for field in fields) + (
            tuple((frame['timeUs'], frame['phase']) for frame in frames),)
        if identity in settings and settings[identity] != signature:
            raise ValueError('old/new or round settings differ')
        settings[identity] = signature
        groups.setdefault((*identity, record['variant']), []).extend(values)
    counts = assessment.get('counts', {})
    expected_counts = {'projectFrames': total_frames, 'probeFrames': len(records),
                       'matchedFrames': total_frames // 2, 'exitedProcesses': len(records)}
    if any(type(counts.get(k)) is not int or counts[k] != v for k, v in expected_counts.items()):
        raise ValueError('assessment counts do not match the run matrix')
    rows = []
    for (scenario, backend, variant), values in sorted(groups.items()):
        rows.append({'scenario': scenario, 'backend': backend, 'variant': variant, 'samples': len(values),
                     'median': statistics.median(values), 'p95': rank(values, .95)})
    pairs = {}
    for row in rows:
        pairs.setdefault((row['scenario'], row['backend']), {})[row['variant']] = row
    for pair in pairs.values():
        old, new = pair['old'], pair['new']
        if old['samples'] != new['samples']:
            raise ValueError('old/new sample counts differ')
        old['medianLimit'], old['p95Limit'] = old['median'] * 1.10 + 5, old['p95'] * 1.10 + 5
        old['medianPass'], old['p95Pass'] = new['median'] <= old['medianLimit'], new['p95'] <= old['p95Limit']
    return {'method': 'statistics.median; nearest-rank p95', 'threshold': 'new <= old * 1.10 + 5 ms',
            'instrumentedExcluded': True,
            'passed': all(p['old']['medianPass'] and p['old']['p95Pass'] for p in pairs.values()),
            'groups': rows, 'comparisons': list(pairs.values())}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('evidence', type=Path)
    parser.add_argument('--label', default='main')
    args = parser.parse_args()
    folder = args.evidence / 'runs' / args.label
    try:
        result = evaluate(folder)
    except (ValueError, KeyError, TypeError, OSError, json.JSONDecodeError) as error:
        print('timing gate rejected: ' + str(error), file=sys.stderr)
        return 1
    (folder / 'nearest-rank-summary.json').write_text(json.dumps(result, indent=2, allow_nan=False) + '\n')
    print(json.dumps(result, indent=2, allow_nan=False))
    return 0 if result['passed'] else 1


if __name__ == '__main__':
    sys.exit(main())
