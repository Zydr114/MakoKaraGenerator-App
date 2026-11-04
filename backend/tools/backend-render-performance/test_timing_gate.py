#!/usr/bin/env python3
import json
import math
from pathlib import Path
import tempfile
import unittest

import timing_gate


class TimingGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.folder = Path(self.temp.name)
        self.args = {'rounds': 3, 'variants': 'old,new', 'scenarios': 'graph', 'warmup': 1,
                     'frames': 2, 'mode': 'sequential', 'width': 320, 'height': 180,
                     'transparent': False, 'counterfactual': 'none'}
        self.records = []
        self.results = {}
        for round_number in (1, 2, 3):
            for backend in ('cpu', 'vulkan'):
                for variant in ('old', 'new'):
                    name = f'{round_number}-{backend}-{variant}.json'
                    self.records.append({'round': round_number, 'scenario': 'graph', 'backend': backend,
                                         'variant': variant, 'result': name, 'exited': True, 'exitCode': 0})
                    self.results[name] = {'scenario': 'graph', 'backend': backend, 'mode': 'sequential',
                        'width': 320, 'height': 180, 'transparent': False, 'counterfactual': 'none',
                        'requestedWarmupFrames': 1, 'requestedMeasuredFrames': 2, 'projectFrameCount': 3,
                        'traceAvailable': False, 'monitoringMode': 'off', 'fixtureSha256': 'fixture',
                        'fixture': {'scenario': 'graph'}, 'quality': 'Final', 'requestedTimesUs': None,
                        'deviceProbe': {'backend': backend}, 'create': {'trace': None}, 'frames': [
                            {'index': 0, 'phase': 'cold', 'timeUs': 0, 'wallMs': 100},
                            {'index': 1, 'phase': 'steady', 'timeUs': 1, 'wallMs': 10},
                            {'index': 2, 'phase': 'steady', 'timeUs': 2, 'wallMs': 20}]}
        self.manifest = {'complete': True, 'arguments': self.args, 'runs': self.records}
        self.assessment = {'valid': True, 'errors': [], 'counts': {'projectFrames': 36,
            'probeFrames': 12, 'matchedFrames': 18, 'exitedProcesses': 12}}

    def run_gate(self):
        (self.folder / 'manifest.json').write_text(json.dumps(self.manifest))
        (self.folder / 'assessment.json').write_text(json.dumps(self.assessment))
        for name, result in self.results.items():
            (self.folder / name).write_text(json.dumps(result))
        return timing_gate.evaluate(self.folder)

    def test_median_and_nearest_rank(self):
        self.assertEqual(timing_gate.statistics.median([1, 9, 2]), 2)
        self.assertEqual(timing_gate.statistics.median([1, 9, 2, 4]), 3)
        self.assertEqual(timing_gate.rank(list(range(1, 21)), .95), 19)
        result = self.run_gate()
        self.assertTrue(result['passed'])
        self.assertEqual(result['groups'][0]['median'], 15)
        self.assertEqual(result['groups'][0]['p95'], 20)
        self.assertEqual(result['groups'][0]['samples'], 6)

    def test_threshold_inclusive_and_failure(self):
        for name, data in self.results.items():
            if '-new.' in name:
                for frame in data['frames'][1:]:
                    frame['wallMs'] = 21.5
        self.assertTrue(self.run_gate()['passed'])
        for name, data in self.results.items():
            if '-new.' in name:
                for frame in data['frames'][1:]:
                    frame['wallMs'] = 21.5001
        self.assertFalse(self.run_gate()['passed'])

    def test_bad_counts_and_round_matrix(self):
        self.results[self.records[0]['result']]['frames'].pop()
        with self.assertRaises(ValueError):
            self.run_gate()
        self.setUp_fresh_results()
        self.records[0]['round'] = 4
        with self.assertRaises(ValueError):
            self.run_gate()

    def setUp_fresh_results(self):
        self.results[self.records[0]['result']]['frames'].append({'index': 2, 'phase': 'steady', 'timeUs': 2, 'wallMs': 20})

    def test_nonfinite_negative_and_instrumented(self):
        data = self.results[self.records[0]['result']]
        for value in (math.nan, math.inf, -1):
            data['frames'][1]['wallMs'] = value
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.run_gate()
        data['frames'][1]['wallMs'] = 10
        data['traceAvailable'] = True
        with self.assertRaises(ValueError):
            self.run_gate()
        data['traceAvailable'] = False
        data['frames'][1]['trace'] = {'stages': []}
        with self.assertRaises(ValueError):
            self.run_gate()
        del data['frames'][1]['trace']
        self.args['variants'] = 'old-instrumented,new-instrumented'
        with self.assertRaises(ValueError):
            self.run_gate()

    def test_assessment_and_settings_fail_closed(self):
        self.assessment['errors'] = ['invalid']
        with self.assertRaises(ValueError):
            self.run_gate()
        self.assessment['errors'] = []
        self.assessment['counts']['matchedFrames'] = 17
        with self.assertRaises(ValueError):
            self.run_gate()
        self.assessment['counts']['matchedFrames'] = 18
        self.results[self.records[0]['result']]['fixtureSha256'] = 'different'
        with self.assertRaises(ValueError):
            self.run_gate()
        self.results[self.records[0]['result']]['fixtureSha256'] = 'fixture'
        self.results[self.records[0]['result']]['frames'][1]['timeUs'] = 123
        with self.assertRaises(ValueError):
            self.run_gate()


if __name__ == '__main__':
    unittest.main()
