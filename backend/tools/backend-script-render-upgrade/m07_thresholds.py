#!/usr/bin/env python3
"""Check the user-approved finite M07 gates against an explicit archived baseline."""
import argparse
import hashlib
import json
import math
from pathlib import Path


def assess(current, baseline):
    expected = {('v1', 'WordEmphasis'), ('v2', 'WordEmphasis'), ('v1', 'RepeatEmphasis'),
                ('v2', 'RepeatEmphasis'), ('v2', 'ClusterExit')}
    def index(report):
        rows = report['timings']
        result = {(row['sdk'], row['sample']): row for row in rows}
        if set(result) != expected or len(rows) != len(expected):
            raise ValueError('Incomplete or duplicate finite matrix.')
        if len(report['comparisons']) != 24 or len(report['crossProcessDeterminism']) != 20:
            raise ValueError('Missing correctness comparisons.')
        for (_, sample), row in result.items():
            cycle = ([999999, 1000000, 1300000, 1599999, 1600000, 180750000, 360750000, 1300000, 999999]
                     if sample == 'ClusterExit' else [750000, 180750000, 1300000, 360750000, 1300000, 750000])
            coordinates = {(stage, position, kind): (at, 3 if kind == 'first' else 6)
                           for stage in ('preparation', 'rendering') for position, at in enumerate(cycle)
                           for kind in ('first', 'repeat')}
            queries = {(q['stage'], q['cycleIndex'], q['cycleType']): (q['timeUs'], q['n']) for q in row['queries']}
            if len(row['queries']) != len(coordinates) or queries != coordinates:
                raise ValueError('Incomplete, duplicate or altered query-position matrix.')
        return result
    actual, before = index(current), index(baseline)
    checks = []
    def add(sample, stage, metric, value, limit, **identity):
        if not all(isinstance(x, (int, float)) and math.isfinite(x) and x >= 0 for x in (value, limit)):
            raise ValueError('Invalid finite timing.')
        checks.append(dict(sample=sample, stage=stage, metric=metric, actual=value, limit=limit,
                           passed=value <= limit, **identity))
    for sample in ('WordEmphasis', 'RepeatEmphasis'):
        candidate, original = actual[('v2', sample)], before[('v1', sample)]
        for stage in ('packageLoadMs', 'compileMs', 'prepareSessionMs', 'renderSessionMs'):
            for metric in ('medianMs', 'p95Ms'):
                add(sample, stage, metric, candidate['setup'][stage][metric], original['setup'][stage][metric] * 1.25 + 50)
        prior = {(q['stage'], q['cycleIndex'], q['cycleType']): q for q in original['queries']}
        if len(candidate['queries']) != 24 or len(prior) != 24:
            raise ValueError('Incomplete query-position matrix.')
        for query in candidate['queries']:
            reference = prior[(query['stage'], query['cycleIndex'], query['cycleType'])]
            if query['timeUs'] != reference['timeUs'] or query['n'] != reference['n']:
                raise ValueError('Query coordinate or sample count differs from baseline.')
            for metric in ('medianMs', 'p95Ms'):
                add(sample, query['stage'], metric, query[metric], reference[metric] * 1.1 + 5,
                    index=query['cycleIndex'], cycle=query['cycleType'])
    for sample in ('WordEmphasis', 'RepeatEmphasis', 'ClusterExit'):
        rows = [q for q in actual[('v2', sample)]['queries'] if q['cycleType'] == 'repeat']
        if len(rows) != (18 if sample == 'ClusterExit' else 12) or any(q['n'] != 6 for q in rows):
            raise ValueError('Expected two repeated cycles in each of three processes.')
        for query in rows:
            for metric in ('medianMs', 'p95Ms'):
                add(sample, query['stage'], metric, query[metric], 5 if query['stage'] == 'preparation' else 10,
                    index=query['cycleIndex'])
    correct = all(row['passed'] for row in current['comparisons'] + current['crossProcessDeterminism'])
    if len(checks) != 196:
        raise ValueError('Approved finite matrix has exactly 196 timing checks.')
    return {'approval': 'user-confirmed-2026-11-04', 'allChecksPassed': correct and all(row['passed'] for row in checks),
            'correctnessPassed': correct, 'checks': checks,
            'scope': 'Radeon 880M, 640x360 CJK/Ruby finite M07 fixture, three processes and three cycles. '
                     'No heavy-particle, HTTP/UI or general throughput performance acceptance.'}


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument('--current', type=Path, required=True)
    cli.add_argument('--baseline', type=Path, required=True)
    cli.add_argument('--output', type=Path, required=True)
    args = cli.parse_args()
    report = assess(json.loads(args.current.read_text()), json.loads(args.baseline.read_text()))
    report['inputs'] = {name: {'path': str(path.resolve()), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest()}
                        for name, path in [('current', args.current), ('baseline', args.baseline)]}
    with args.output.open('x') as output:
        json.dump(report, output, ensure_ascii=False, indent=2)
        output.write('\n')
    print(json.dumps({'passed': report['allChecksPassed'], 'checks': len(report['checks']),
                      'failed': sum(not row['passed'] for row in report['checks'])}))
    return 0 if report['allChecksPassed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
