#!/usr/bin/env python3
"""Finite real HTTP acceptance for static Glow; owns its project and profile."""
import argparse
import hashlib
import importlib.util
import json
import subprocess
import time
from pathlib import Path
from urllib.parse import urlencode

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('assignment_probe', ROOT / 'tools/backend-preset-assignment-http-probe/probe.py')
assignment = importlib.util.module_from_spec(spec)
spec.loader.exec_module(assignment)

PROJECT = {'kind': 'project'}
RED = {'red': 255, 'green': 0, 'blue': 0, 'alpha': 255}
BLUE = {'red': 0, 'green': 0, 'blue': 255, 'alpha': 255}
GLOW = {'color': RED, 'opacity': 1, 'blurRadiusPx': 8, 'spreadPx': 0}


class GlowProbe(assignment.AssignmentProbe):
    def __init__(self, output, dist):
        super().__init__(output, dist)
        self.frames = []

    def style(self, name, revision, target):
        value = self.request(name, 'GET', self.project_path + '/style?' + urlencode({
            'expectedRevision': revision, **target}))
        self.check(value['projectRevision'] == revision and value['target'] == target,
                   name + ': exact revision and target')
        return value

    def patch(self, name, revision, target, changes, changed=True):
        result = self.command(name, revision, {'type': 'styles.patch', 'targets': [target],
            'changes': [{'property': prop, 'value': value} for prop, value in changes]})
        self.check(result['changed'] is changed, name + ': changed state')
        self.check(result['projectRevision'] == str(int(revision) + int(changed)), name + ': revision')
        return result['projectRevision']

    def restore(self, name, revision, target, *properties):
        result = self.command(name, revision, {'type': 'styles.restore', 'targets': [target], 'properties': list(properties)})
        self.check(result['changed'], name + ': restore changed')
        return result['projectRevision']

    def capture_frame(self, name, revision, alpha):
        preview = self.request(name + '-create', 'POST', '/api/v1/preview-sessions', status=201, value={
            'source': {'kind': 'project', 'sourceId': self.project, 'sourceRevision': revision},
            'size': {'width': 320, 'height': 180}, 'alphaMode': alpha, 'purpose': 'preview',
            'metadata': self.metadata(name + '-create')})
        path = '/api/v1/preview-sessions/' + preview['previewId']
        self.request(name + '-request', 'POST', path + '/frame-requests', status=202, value={
            'expectedSourceRevision': revision, 'previewGeneration': preview['previewGeneration'],
            'requestSeq': '1', 'timeUs': '500000', 'intent': 'playback', 'metadata': self.metadata(name + '-frame')})
        deadline = time.monotonic() + 35
        while time.monotonic() < deadline:
            state = self.request(name + '-poll', 'GET', path)['session']
            frame = state.get('latestFrame')
            if state['state'] == 'ready' and frame and frame['requestSeq'] == '1':
                break
            self.check(state['state'] != 'failed', name + ': Worker did not fail')
            time.sleep(.05)
        else:
            raise AssertionError(name + ': preview timed out')
        self.check(frame['sourceRevision'] == revision and frame['previewGeneration'] == preview['previewGeneration'],
                   name + ': revision and generation')
        headers, png = self.binary(name + '-png', frame['content']['contentUrl'])
        digest = hashlib.sha256(png).hexdigest()
        self.check(headers['Content-Type'].split(';')[0] == 'image/png' and png.startswith(b'\x89PNG\r\n\x1a\n')
                   and digest == frame['content']['sha256'] and len(png) == frame['content']['sizeBytes'],
                   name + ': authenticated PNG identity')
        self.check(frame['width'] == 320 and frame['height'] == 180, name + ': dimensions')
        (self.output / (name + '.png')).write_bytes(png)
        raw = subprocess.run(['/usr/bin/ffmpeg', '-v', 'error', '-i', 'pipe:0', '-f', 'rawvideo',
            '-pix_fmt', 'rgba', 'pipe:1'], input=png, capture_output=True, check=True, timeout=30).stdout
        self.check(len(raw) == 320 * 180 * 4, name + ': complete RGBA decode')
        self.request(name + '-close', 'POST', path + '/close', value={
            'expectedPreviewGeneration': preview['previewGeneration'], 'expectedSourceRevision': revision,
            'metadata': self.metadata(name + '-close')})
        self.frames.append({'name': name, 'revision': revision, 'alphaMode': alpha, 'pngSha256': digest,
                            'rgbaSha256': hashlib.sha256(raw).hexdigest()})
        return raw

    def run(self):
        self.start('glow'); self.connect('glow')
        self.create_project()
        branches = self.request('branches', 'GET', self.project_path + '/lyrics/branches?expectedRevision=0&limit=100')['branches']
        branch = next(item for item in branches if item['lineCount'] > 0)
        branch_id = branch['id']
        window = self.request('window', 'GET', self.project_path + '/lyrics/window?' + urlencode({
            'expectedRevision': '0', 'branchId': branch_id, 'offset': 0, 'limit': 1}))
        line = window['lines'][0]
        self.check(bool(line['tokens']), 'owned enhanced lyric exposes an actual token')
        branch_target = {'kind': 'branch', 'branchId': branch_id}
        line_target = {'kind': 'line', 'branchId': branch_id, 'lineId': line['id']}
        token_target = {'kind': 'token', 'branchId': branch_id, 'lineId': line['id'], 'tokenId': line['tokens'][0]['id']}
        self.check(self.style('default', '0', PROJECT)['baseline']['glow'] is None, 'default glow is null')

        white = {'kind': 'solid', 'color': {'red': 255, 'green': 255, 'blue': 255, 'alpha': 255}}
        revision = self.patch('prepare', '0', PROJECT, [('typography.fontSizePx', 36), ('stroke', None), ('shadow', None),
            ('unsungFill.material', white), ('sungFill.material', white)])
        base_t = self.capture_frame('baseline-transparent', revision, 'transparent')
        base_o = self.capture_frame('baseline-opaque', revision, 'opaque')
        revision = self.patch('project-glow', revision, PROJECT, [('glow', GLOW)])
        project = self.style('project-glow-read', revision, PROJECT)
        self.check(project['baseline']['glow'] == GLOW, 'project complete glow round trip')
        glow_t = self.capture_frame('glow-transparent', revision, 'transparent')
        glow_o = self.capture_frame('glow-opaque', revision, 'opaque')
        self.check(glow_o != base_o and glow_t != base_t, 'glow changes both real outputs')
        outside = [i for i in range(0, len(base_t), 4) if base_t[i + 3] == 0 and glow_t[i + 3] > 0]
        self.check(bool(outside), 'glow extends alpha outside baseline glyph coverage')
        self.check(any(glow_t[i] > glow_t[i + 1] and glow_t[i] > glow_t[i + 2] for i in outside),
                   'expanded pixels contain the configured red glow')
        revision = self.patch('spread-change', revision, PROJECT, [('glow.spreadPx', 4)])
        spread_t = self.capture_frame('spread-transparent', revision, 'transparent')
        self.check(spread_t != glow_t, 'nonzero spread changes transparent pixels')
        revision = self.patch('spread-reset', revision, PROJECT, [('glow.spreadPx', 0)])

        revision = self.patch('branch-leaf', revision, branch_target, [('glow.color', BLUE)])
        read = self.style('branch-read', revision, branch_target)
        self.check(read['staticHierarchy']['glow'] == {**GLOW, 'color': BLUE}, 'branch leaf inherits other glow fields')
        self.check({'property': 'glow.color', 'value': BLUE} in read['local'], 'branch leaf raw presence')
        revision = self.patch('line-leaves', revision, line_target, [('glow.opacity', .5), ('glow.blurRadiusPx', 6), ('glow.spreadPx', 2)])
        self.check(self.style('line-read', revision, line_target)['staticHierarchy']['glow'] ==
                   {'color': BLUE, 'opacity': .5, 'blurRadiusPx': 6, 'spreadPx': 2}, 'line leaves combine with inherited color')
        revision = self.patch('token-off', revision, token_target, [('glow', None)])
        token = self.style('token-off-read', revision, token_target)
        self.check(token['staticHierarchy']['glow'] is None and {'property': 'glow', 'value': None} in token['local'],
                   'token explicit null disables inherited glow')
        revision = self.restore('token-restore', revision, token_target, 'glow')
        self.check(self.style('token-inherit', revision, token_target)['staticHierarchy']['glow'] ==
                   {'color': BLUE, 'opacity': .5, 'blurRadiusPx': 6, 'spreadPx': 2}, 'restore resumes inheritance')
        revision = self.restore('line-restore', revision, line_target, 'glow')
        revision = self.restore('branch-restore', revision, branch_target, 'glow')

        history_before = self.request('noop-history-before', 'GET', self.project_path + '/history')
        unchanged = self.patch('noop', revision, PROJECT, [('glow', GLOW)], changed=False)
        self.check(self.request('noop-history-after', 'GET', self.project_path + '/history') == history_before,
                   'no-op preserves history')
        self.check(unchanged == revision, 'no-op preserves revision')
        replay_payload = self.command_payload('glow-replay', revision,
            {'type': 'styles.patch', 'targets': [PROJECT], 'changes': [{'property': 'glow', 'value': None}]})
        replay_first = self.request('receipt-first', 'POST', self.project_path + '/commands', value=replay_payload)
        self.check(replay_first['changed'], 'receipt first request changes glow')
        self.check(self.request('receipt-replay', 'POST', self.project_path + '/commands', value=replay_payload) == replay_first,
                   'same receipt replays original result despite stale expected revision')
        revision = replay_first['projectRevision']
        revision = self.patch('receipt-reset', revision, PROJECT, [('glow', GLOW)])
        project_before_rejections = self.style('project-before-rejections', revision, PROJECT)
        line_before_rejections = self.style('line-before-rejections', revision, line_target)

        def rejected(name, targets, changes, status, code, failure=None):
            result = self.request(name, 'POST', self.project_path + '/commands', status=status, code=code,
                value=self.command_payload(name, revision, {'type': 'styles.patch', 'targets': targets,
                    'changes': [{'property': prop, 'value': value} for prop, value in changes]}))
            if failure is not None:
                self.check(result.get('failureCode') == failure, name + ': domain failure code')
            self.check(self.style(name + '-project-after', revision, PROJECT) == project_before_rejections,
                       name + ': project revision and complete style unchanged')
            self.check(self.style(name + '-line-after', revision, line_target) == line_before_rejections,
                       name + ': line revision and complete style unchanged')

        self.request('stale-write', 'POST', self.project_path + '/commands', status=409, code='revisionConflict',
            value=self.command_payload('stale-write', '0', {'type': 'styles.patch', 'targets': [PROJECT],
                'changes': [{'property': 'glow', 'value': None}]}))
        self.check(self.style('stale-write-project-after', revision, PROJECT) == project_before_rejections,
                   'stale write preserves project revision and complete style')
        rejected('invalid-opacity', [PROJECT], [('glow.opacity', -1)], 422, 'commandRejected', 'InvalidValue')
        rejected('missing-spread', [PROJECT], [('glow', {key: value for key, value in GLOW.items() if key != 'spreadPx'})],
                 400, 'invalidRequest')
        rejected('null-opacity', [PROJECT], [('glow.opacity', None)], 400, 'invalidRequest')
        rejected('parent-leaf-conflict', [PROJECT], [('glow', GLOW), ('glow.color', BLUE)],
                 422, 'commandRejected', 'InvalidProperty')
        rejected('second-target-missing', [line_target, {**line_target, 'lineId': 'glow-probe-missing-line'}],
                 [('glow.color', BLUE)], 422, 'commandRejected', 'InvalidTarget')
        other = self.register('glow-other-client', select=False)
        self.secrets.extend((other['accessToken'], other['resumeToken']))
        self.request('foreign-read', 'GET', self.project_path + '/style?' + urlencode({
            'expectedRevision': revision, **PROJECT}), client=other['clientId'], status=404, code='sessionNotFound')
        self.request('foreign-write', 'POST', self.project_path + '/commands', client=other['clientId'],
            status=404, code='sessionNotFound', value=self.command_payload('foreign-write', revision,
                {'type': 'styles.patch', 'targets': [PROJECT], 'changes': [{'property': 'glow', 'value': None}]}))
        self.check(self.style('after-foreign', revision, PROJECT)['baseline']['glow'] == GLOW, 'foreign client cannot change glow')

        revision = self.patch('disable', revision, PROJECT, [('glow', None)])
        restored = self.capture_frame('disabled-transparent', revision, 'transparent')
        self.check(restored == base_t, 'disabled glow restores baseline pixels exactly')
        undo = self.command('undo-disable', revision, {'type': 'history.undo'})
        revision = undo['projectRevision']
        self.check(self.style('undo-read', revision, PROJECT)['baseline']['glow'] == GLOW, 'undo restores glow')
        self.check(self.capture_frame('undo-transparent', revision, 'transparent') == glow_t, 'undo restores glow pixels')
        revision = self.patch('noop-after-undo', revision, PROJECT, [('glow', GLOW)], changed=False)
        redo = self.command('redo-disable', revision, {'type': 'history.redo'})
        revision = redo['projectRevision']
        self.check(self.style('redo-read', revision, PROJECT)['baseline']['glow'] is None, 'redo disables glow')
        revision = self.patch('persist-glow', revision, PROJECT, [('glow', GLOW)])
        reopened = self.save_and_reopen(revision)
        revision = reopened['projectRevision']
        self.check(self.style('reopened-read', revision, PROJECT)['baseline']['glow'] == GLOW, 'save and reopen retain glow')
        self.check(self.capture_frame('reopened-transparent', revision, 'transparent') == glow_t, 'reopened pixels match')
        self.job('final-close', self.request('final-close', 'POST', self.project_path + '/close', status=202,
            value={'expectedRevision': revision, 'policy': 'requireClean', 'activeJobs': 'wait'}))
        self.check(bool(self.worker_identities), 'managed Worker identity observed')
        self.stop('glow')
        scanned = self.scan_credentials()
        deployment = json.loads((self.dist / 'deployment.json').read_text())
        assignment.smoke.existing.save_json(self.output / 'summary.json', {'status': 'passed', 'frames': self.frames,
            'outsidePixels': len(outside), 'assertions': self.assertions, 'httpRequests': self.request_count,
            'deploymentVersion': deployment['version'], 'runs': self.runs,
            'observedIdentities': [{'pid': pid, 'startTicks': ticks} for pid, ticks in sorted(self.observed)],
            'workerIdentities': [{'pid': pid, 'startTicks': ticks} for pid, ticks in sorted(self.worker_identities)],
            'shutdownEvidence': 'glow-shutdown.json', 'credentialScannedFilesBeforeSummary': scanned,
            'deploymentManifestSha256': hashlib.sha256((self.dist / 'deployment.json').read_bytes()).hexdigest(),
            'scope': 'finite static glow HTTP and decoded PNG; no browser, performance or cross-platform claim'})
        self.scan_credentials()
        print(json.dumps({'status': 'passed', 'httpRequests': self.request_count, 'assertions': len(self.assertions)}))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    output = args.output.resolve()
    if not output.is_relative_to(ROOT / 'artifacts') or output == ROOT / 'artifacts':
        parser.error('--output must be a new directory below backend/artifacts')
    output.mkdir(parents=True, exist_ok=False)
    probe = GlowProbe(output, args.dist.resolve())
    try:
        probe.run()
    finally:
        if probe.launcher is not None:
            probe.stop('failure-cleanup')


if __name__ == '__main__':
    main()
