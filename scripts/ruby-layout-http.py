#!/usr/bin/env python3
"""Finite Ruby layout acceptance on a matched App Server/Worker deployment."""
import argparse
import base64
import copy
import importlib.util
import json
from pathlib import Path
import platform
import sys
import tempfile
import traceback
from urllib.parse import urlencode

sys.dont_write_bytecode = True
ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('ruby_acceptance', ROOT / 'scripts/export-audio-normalization-http.py')
acceptance = importlib.util.module_from_spec(spec)
spec.loader.exec_module(acceptance)
host = acceptance.g1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    parser.add_argument('--ffmpeg', default=host.default_tool('ffmpeg'))
    parser.add_argument('--ffprobe', default=host.default_tool('ffprobe'))
    parser.add_argument('--timeout', type=float, default=180)
    args = parser.parse_args()
    args.render_backend = 'cpu'
    args.dist, args.output = args.dist.resolve(), args.output.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    deployment_bytes = (args.dist / 'deployment.json').read_bytes()
    deployment = json.loads(deployment_bytes)
    summary = {'status': 'running', 'stage': 'start', 'platform': platform.system(),
               'deployment': {key: deployment.get(key) for key in ('version', 'sourceCommit', 'sourceDirty', 'rid')},
               'deploymentManifestSha256': host.sha(deployment_bytes), 'checks': [], 'images': {}}
    sessions = []

    def check(condition, label):
        host.check(condition, label)
        summary['checks'].append(label)

    try:
        check(deployment['version'] == '0.115.0' and not deployment['sourceDirty'], 'clean matched 0.115.0 deployment')
        with tempfile.TemporaryDirectory(prefix='mako-ruby-private-') as private:
            with acceptance.Session(args, Path(private) / 'profile') as api:
                sessions.append(api)
                summary['stage'] = 'import'
                xml = '''<?xml version="1.0" encoding="utf-8"?>
<tt xmlns="http://www.w3.org/ns/ttml" xmlns:tts="http://www.w3.org/ns/ttml#styling">
 <body><div><p begin="0s" end="2s"><span begin="0s" end="1s"><span tts:ruby="container"><span tts:ruby="base">代</span><span tts:ruby="text">だいしょ</span></span></span><span begin="1s" end="2s"><span tts:ruby="container"><span tts:ruby="base">償</span><span tts:ruby="text">うはら</span></span></span></p></div></body>
</tt>'''.encode()
                asset = api.upload('ruby-layout.ttml', xml, 'application/ttml+xml')
                project = api.request('POST', '/api/v1/project-sessions', status=201, value={
                    'name': 'Ruby isolated acceptance', 'lyricsAssetId': asset, 'importerId': 'ttml',
                    'composition': {'canvasWidth': 1280, 'canvasHeight': 360, 'frameRateNumerator': 24,
                                    'frameRateDenominator': 1, 'outputDurationUs': 2000000}})
                path = '/api/v1/project-sessions/' + project['projectSessionId']
                revision = project['projectRevision']

                def window():
                    return api.request('GET', path + '/lyrics/window?' + urlencode({'branchId': 'primary', 'expectedRevision': revision}))

                line = window()['lines'][0]
                original_ruby = copy.deepcopy(line['rubyAnnotations'])
                check(line['text'] == '代償' and len(original_ruby) == 2, 'Japanese adjacent Ruby imported')
                line_target = {'kind': 'line', 'branchId': 'primary', 'lineId': line['id']}
                token_target = {**line_target, 'kind': 'token', 'tokenId': line['tokens'][0]['id']}

                def send(command, changed=None, status=200):
                    nonlocal revision
                    result = api.request('POST', path + '/commands', status=status, value={
                        **api.metadata(), 'expectedRevision': revision, 'command': command}, metadata=False)
                    if status == 200:
                        if changed is not None:
                            check(result['changed'] == changed, 'command changed=' + str(changed) + ': ' + command['type'])
                        revision = result['projectRevision']
                    return result

                def patch(target, **values):
                    return send({'type': 'styles.patch', 'targets': [target],
                                 'changes': [{'property': key, 'value': value} for key, value in values.items()]})

                def style(target):
                    return api.request('GET', path + '/style?' + urlencode({'expectedRevision': revision, **target}))

                def measure(label, backend='cpu'):
                    response = api.request('POST', path + '/orchestration/measurements', value={
                        'expectedRevision': revision, 'mode': 'time', 'timeUs': '500000',
                        'width': 1280, 'height': 360, 'alphaMode': 'opaque', 'renderBackend': backend}, metadata=False)
                    measured = response['measurement']
                    png = base64.b64decode(measured['png'], validate=True)
                    check(host.sha(png) == measured['pngSha256'], label + ': PNG hash')
                    (args.output / (label + '.png')).write_bytes(png)
                    rubies = [item for item in measured['elements'] if item.get('rubyId') and item['visible']]
                    check(len(rubies) == 2, label + ': two visible Ruby elements')
                    rubies.sort(key=lambda item: min(point['x'] for point in item['worldCorners']))
                    boxes = [{'left': min(p['x'] for p in item['worldCorners']), 'right': max(p['x'] for p in item['worldCorners']),
                              'top': min(p['y'] for p in item['worldCorners']), 'bottom': max(p['y'] for p in item['worldCorners'])}
                             for item in rubies]
                    summary['images'][label] = {'sha256': host.sha(png), 'rubyBoxes': boxes,
                                               'gap': boxes[1]['left'] - boxes[0]['right'], 'backend': backend}
                    return summary['images'][label]

                summary['stage'] = 'hierarchy-and-render'
                patch({'kind': 'project'}, **{'typography.fontFamilyName': 'Noto Sans CJK JP', 'typography.fontSizePx': 96,
                                             'typography.letterSpacingPx': 3})
                initial = measure('source-default')
                patch({'kind': 'project'}, **{'ruby.relativeScale': .65, 'ruby.gapPx': 18})
                enlarged = measure('project-ruby-settings')
                check(enlarged['rubyBoxes'][0]['bottom'] - enlarged['rubyBoxes'][0]['top'] >
                      initial['rubyBoxes'][0]['bottom'] - initial['rubyBoxes'][0]['top'], 'larger setting changes actual Ruby height')
                check(enlarged['rubyBoxes'][0]['bottom'] < initial['rubyBoxes'][0]['bottom'], 'larger gap moves Ruby above body')
                patch(line_target, **{'ruby.relativeScale': None, 'ruby.gapPx': None})
                local = style(line_target)
                check(local['staticHierarchy']['ruby'] == {'relativeScale': None, 'gapPx': None} and
                      {item['property']: item['value'] for item in local['local']} == {'ruby.relativeScale': None, 'ruby.gapPx': None},
                      'explicit null remains distinct from absent local override')
                source = measure('explicit-source-values')
                check(source['sha256'] == initial['sha256'], 'explicit null restores exact source-value PNG')
                send({'type': 'styles.restore', 'targets': [line_target], 'properties': ['ruby.relativeScale', 'ruby.gapPx']}, True)
                check(measure('restored-parent')['sha256'] == enlarged['sha256'], 'restore inherits project settings')
                patch(token_target, **{'ruby.relativeScale': .8, 'ruby.gapPx': 26})
                token = measure('token-settings')
                check(token['rubyBoxes'][0]['bottom'] - token['rubyBoxes'][0]['top'] >
                      token['rubyBoxes'][1]['bottom'] - token['rubyBoxes'][1]['top'], 'token override affects only associated Ruby')
                resolved = api.request('GET', path + '/resolved-style?' + urlencode({
                    'expectedRevision': revision, 'branchId': 'primary', 'lineId': line['id'], 'tokenId': line['tokens'][0]['id']}))
                check(resolved['style']['ruby'] == {'relativeScale': .8, 'gapPx': 26}, 'contextual resolved style includes Ruby values')
                send({'type': 'history.undo'}, True)
                check(measure('undo')['sha256'] == enlarged['sha256'], 'undo restores exact previous PNG')
                noop = send({'type': 'styles.restore', 'targets': [line_target], 'properties': ['ruby.gapPx']}, False)
                check(noop['canRedo'], 'no-op preserves redo')
                send({'type': 'history.redo'}, True)
                check(measure('redo')['sha256'] == token['sha256'], 'redo restores exact token PNG')
                send({'type': 'styles.patch', 'targets': [line_target], 'changes': [{'property': 'ruby.relativeScale', 'value': 0}]}, status=422)
                send({'type': 'styles.patch', 'targets': [line_target], 'changes': [{'property': 'ruby.gapPx', 'value': -1}]}, status=422)
                send({'type': 'styles.patch', 'targets': [line_target], 'changes': [{'property': 'ruby.relativeScale', 'value': 1e38}]}, status=422)
                check(window()['lines'][0]['rubyAnnotations'] == original_ruby, 'editing and rendering preserve source Ruby IDs text timing and layout defaults')

                summary['stage'] = 'template-roundtrip'
                library = api.request('GET', '/api/v1/template-libraries/personal/templates')
                definition = {'name': 'Ruby settings', 'kind': 'line', 'style': copy.deepcopy(style(line_target)['staticHierarchy']),
                              'properties': ['ruby.relativeScale', 'ruby.gapPx']}
                created = api.request('POST', '/api/v1/template-libraries/personal/commands', value={
                    'expectedRevision': library['revision'], 'command': {'type': 'create', 'definition': definition}})
                detail = api.request('GET', '/api/v1/template-libraries/personal/templates/detail?' + urlencode({
                    'expectedRevision': created['revision'], 'templateId': created['templateId']}))
                check(detail['definition'] == definition, 'personal template Ruby definition roundtrip')

                summary['stage'] = 'save-and-reopen'
                handle = api.request('POST', '/api/v1/file-handles', status=201,
                                     value={'rootGrantId': 'projects', 'relativePath': 'ruby-settings'})['handleId']
                api.wait(api.request('POST', path + '/save-jobs', status=202,
                                     value={'expectedRevision': revision, 'targetHandleId': handle}))
                api.wait(api.request('POST', path + '/close', status=202,
                                     value={'expectedRevision': revision, 'policy': 'requireClean', 'activeJobs': 'wait'}))
                opened = api.wait(api.request('POST', '/api/v1/project-sessions/open-jobs', status=202,
                                             value={'projectDirectoryHandleId': handle}))['result']
                path, revision = '/api/v1/project-sessions/' + opened['projectSessionId'], opened['projectRevision']
                check(style(token_target)['staticHierarchy']['ruby'] == {'relativeScale': .8, 'gapPx': 26}, 'saved token Ruby style reopens')
                check(measure('reopened')['sha256'] == token['sha256'], 'save/reopen exact PNG')
                backends = api.request('GET', '/api/v1/render-backends')['backends']
                if any(item['renderBackend'] == 'gpu' and item['available'] for item in backends):
                    gpu = measure('gpu-reopened', 'gpu')
                    check(gpu['rubyBoxes'] == token['rubyBoxes'], 'CPU and GPU expose the same Ruby geometry')
                summary['stage'] = 'export'
                summary['export'] = acceptance.run_export(api, args, opened['projectSessionId'], revision,
                    'ruby-layout', 'opaqueMp4', False, False, 48)
                api.wait(api.request('POST', path + '/close', status=202,
                                     value={'expectedRevision': revision, 'policy': 'requireClean', 'activeJobs': 'wait'}))
            check(not Path(private, 'profile', 'bootstrap.json').exists(), 'owned session stopped')
        summary['status'], summary['stage'] = 'passed', 'complete'
    except BaseException as error:
        summary.update(status='failed', failure={'type': type(error).__name__,
            'message': str(error) if isinstance(error, AssertionError) else 'probe exception; inspect stage and trace',
            'trace': [{'file': Path(frame.filename).name, 'line': frame.lineno} for frame in traceback.extract_tb(error.__traceback__)]})
    summary['sessions'] = [session.evidence for session in sessions]
    raw = json.dumps(summary, ensure_ascii=False, indent=2)
    for session in sessions:
        for credential in session.credentials:
            host.check(credential not in raw, 'evidence credential scan')
    credentials = [credential.encode() for session in sessions for credential in session.credentials]
    scanned = 0
    for folder in (args.dist, args.output):
        for entry in folder.rglob('*'):
            if entry.is_file():
                payload = entry.read_bytes()
                host.check(all(credential not in payload for credential in credentials), 'deployment and evidence credential scan')
                scanned += 1
    summary['credentialScannedFiles'] = scanned
    raw = json.dumps(summary, ensure_ascii=False, indent=2)
    (args.output / 'summary.json').write_text(raw + '\n', encoding='utf-8')
    print(json.dumps({'status': summary['status'], 'stage': summary['stage'], 'checks': len(summary['checks'])}))
    return 0 if summary['status'] == 'passed' else 1


if __name__ == '__main__':
    raise SystemExit(main())
