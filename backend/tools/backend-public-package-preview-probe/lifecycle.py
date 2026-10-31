"""Lifecycle phases shared by the finite public package preview runner."""
import hashlib
import json
from pathlib import Path
import time
from urllib.parse import urlencode
import urllib.request


def update(probe, label, preview, source, transparent=False, width=320, height=180):
    result = probe.request(label + '-input', 'PUT', '/api/v1/preview-sessions/' + preview['previewId'] + '/input', value={
        'expectedPreviewGeneration': preview['previewGeneration'], 'expectedSourceRevision': preview['source']['sourceRevision'],
        'source': source, 'size': {'width': width, 'height': height}, 'alphaMode': 'straight' if transparent else 'opaque',
        'metadata': probe.metadata(label + '-input')})
    current = result.get('session', result)
    probe.check(current['previewGeneration'] != preview['previewGeneration'], label + ': input activates new generation')
    probe.active_previews[current['previewId']] = current
    return current


def package_resource_closure(probe, source):
    """Validate the public package resource projection used by continuous preview."""
    query = urlencode({'expectedRevision': source['sourceRevision']})
    response = probe.request('lifecycle-package-resources', 'GET',
                             '/api/v1/project-sessions/' + source['sourceId'] + '/resources?' + query)
    resources = response.get('resources', [])
    packages = [item for item in resources if item.get('kind') == 'effectPackageManifest']
    files = [item for item in resources if item.get('kind') == 'effectPackageFile']
    probe.check(bool(packages), 'package resource projection exposes manifest')
    probe.check(bool(files), 'package resource projection exposes files')
    for manifest in packages:
        closure = manifest.get('packageClosure')
        probe.check(isinstance(closure, dict), 'package manifest exposes closure summary')
        if not isinstance(closure, dict):
            continue
        package_id, revision = manifest.get('packageId'), manifest.get('packageRevision')
        probe.check(package_id == closure.get('packageId') and revision == closure.get('packageRevision'),
                    'package manifest and closure identities agree')
        probe.check(manifest.get('packagePath') == 'manifest.json' and manifest.get('packageRole') == 'manifest',
                    'package manifest path and role are stable')
        probe.check(manifest.get('status') in ('validated', 'unsupported')
                    and closure.get('status') == manifest.get('status'),
                    'package closure status is explicit')
        package_files = [item for item in files if item.get('packageRevision') == revision]
        probe.check(len(package_files) == closure.get('fileCount'),
                    'package closure file count matches resource projection')
        probe.check(closure.get('validatedFileCount', -1) + closure.get('unsupportedFileCount', -1) == len(package_files),
                    'package closure validated and unsupported counts are exhaustive')
        probe.check(str(closure.get('manifestSizeBytes', '')).isdigit()
                    and str(closure.get('totalSizeBytes', '')).isdigit()
                    and len(closure.get('manifestSha256', '')) == 64,
                    'package closure sizes and manifest SHA are canonical')
        for item in package_files:
            probe.check(item.get('packageId') == package_id and item.get('packagePath')
                        and item.get('packageRole') and len(item.get('sha256', '')) == 64,
                        'package file exposes logical identity role size and SHA')
            probe.check(item.get('resourceId') == 'packageFile:' + revision + ':' + item['packagePath'],
                        'package file resource id binds revision and path')
    return response


def exercise_package_assets(probe):
    """Materialize every validated package resource and exercise its public asset transport."""
    source = probe.projects.get('scale') or probe.open_fixture('scale')
    resources = package_resource_closure(probe, source)
    route = '/api/v1/project-sessions/' + source['sourceId'] + '/resources/package-asset'
    validated = [item for item in resources.get('resources', [])
                 if item.get('kind') in ('effectPackageManifest', 'effectPackageFile')
                 and item.get('status') == 'validated']
    probe.check(bool(validated), 'package asset closure has validated resources')
    materialized = []
    for index, item in enumerate(validated):
        receipt = probe.request('package-asset-' + str(index), 'POST', route, status=201, value={
            'expectedRevision': source['sourceRevision'], 'resourceId': item['resourceId'],
            'metadata': probe.metadata('package-asset-' + str(index))})
        probe.check(receipt['projectSessionId'] == source['sourceId'], 'package asset binds project session')
        probe.check(receipt['projectRevision'] == source['sourceRevision'], 'package asset binds exact revision')
        probe.check(receipt['resourceId'] == item['resourceId'], 'package asset preserves resource identity')
        probe.check(receipt['packagePath'] == item['packagePath'] and receipt['packageRole'] == item['packageRole'],
                    'package asset preserves logical path and role')
        asset = receipt['asset']; content_url = receipt['contentUrl']
        headers, data, _ = probe.fetch('package-asset-' + str(index) + '-get', content_url)
        probe.check(len(data) == asset['size'], 'package asset GET size matches receipt')
        probe.check(hashlib.sha256(data).hexdigest() == asset['sha256'], 'package asset GET SHA matches receipt')
        probe.check(asset['size'] == int(item['sizeBytes']) and asset['sha256'] == item['sha256'],
                    'package asset receipt matches resource projection')
        head_headers, head_data, _ = probe.fetch('package-asset-' + str(index) + '-head', content_url,
                                                  method='HEAD', expected=b'')
        probe.check(head_headers.get('ETag') == headers.get('ETag'), 'package asset HEAD ETag matches GET')
        probe.check(head_headers.get('Content-Length') == str(len(data)), 'package asset HEAD size matches GET')
        probe.check(headers.get('Content-Type') == asset['contentType'], 'package asset content type matches receipt')
        if data:
            end = min(7, len(data) - 1)
            _, partial, _ = probe.fetch('package-asset-' + str(index) + '-range', content_url,
                                        headers={'Range': 'bytes=0-' + str(end)}, status=206,
                                        expected=data[:end + 1])
            probe.check(partial == data[:end + 1], 'package asset valid Range bytes match GET')
            probe.fetch('package-asset-' + str(index) + '-range-invalid', content_url,
                        headers={'Range': 'bytes=' + str(len(data)) + '-'}, status=416,
                        expected=b'', require_error_body=False)
        probe.fetch('package-asset-' + str(index) + '-cached', content_url,
                    headers={'If-None-Match': headers['ETag']}, status=304, expected=b'')
        materialized.append(receipt)

    # Authorization and revision/resource identity are checked independently of content bytes.
    if materialized:
        content_url = materialized[0]['contentUrl']
        probe.fetch('package-asset-anonymous', content_url, status=401, anonymous=True)
        other = probe.register('package-asset-other-client', select=False)['clientId']
        _, _, foreign_error = probe.fetch('package-asset-foreign', content_url, status=401, client=other)
        probe.check(foreign_error.get('code') == 'resourceUnauthorized',
                    'package asset foreign client stable resourceUnauthorized')
    probe.request('package-asset-stale-revision', 'POST', route, status=409, code='revisionConflict', value={
        'expectedRevision': '999', 'resourceId': validated[0]['resourceId'],
        'metadata': probe.metadata('package-asset-stale-revision')})
    probe.request('package-asset-unknown-resource', 'POST', route, status=404, code='resourceNotFound', value={
        'expectedRevision': source['sourceRevision'], 'resourceId': 'packageFile:missing:path.txt',
        'metadata': probe.metadata('package-asset-unknown-resource')})

    # Keep a preview lease alive while the asset is materialized, then verify both
    # the continuous preview path and final asset release after project close.
    preview = probe.create_preview('package-assets-preview', source, False)
    for sequence, time_us in enumerate((0, 100000, 300000, 500000), 1):
        preview = probe.frame_request('package-assets-preview-' + str(sequence), preview, sequence, time_us)
        probe.terminal('package-assets-preview-' + str(sequence), preview, sequence)
    previous_generation = preview['previewGeneration']
    preview = probe.frame_request('package-assets-preview-seek', preview, 5, 100000, 'seek')
    probe.check(preview['previewGeneration'] != previous_generation, 'package asset preview seek changes generation')
    probe.terminal('package-assets-preview-seek', preview, 5)
    probe.close_preview('package-assets-preview', preview)
    accepted = probe.request('package-assets-project-close', 'POST',
                             '/api/v1/project-sessions/' + source['sourceId'] + '/close', status=202,
                             value={'expectedRevision': source['sourceRevision'], 'policy': 'discard', 'activeJobs': 'wait'})
    probe.job('package-assets-project-close', accepted)
    if materialized:
        time.sleep(3.2)
        probe.fetch('package-asset-expired', materialized[0]['contentUrl'], status=410)


def exercise_sprite_assets_and_matrix(probe):
    """Exercise image-role package bytes and an isolated opaque/alpha frame matrix."""
    source = probe.open_fixture('sprite')
    resources = package_resource_closure(probe, source)
    route = '/api/v1/project-sessions/' + source['sourceId'] + '/resources/package-asset'
    images = [item for item in resources.get('resources', [])
              if item.get('kind') == 'effectPackageFile' and item.get('status') == 'validated'
              and item.get('packageRole') == 'image']
    probe.check(bool(images), 'sprite package exposes validated image resources')
    assets = []
    for index, item in enumerate(images):
        receipt = probe.request('sprite-package-asset-' + str(index), 'POST', route, status=201, value={
            'expectedRevision': source['sourceRevision'], 'resourceId': item['resourceId'],
            'metadata': probe.metadata('sprite-package-asset-' + str(index))})
        asset = receipt['asset']; content_url = receipt['contentUrl']
        probe.check(asset['size'] == int(item['sizeBytes']) and asset['sha256'] == item['sha256'],
                    'sprite image asset receipt matches projection')
        headers, data, _ = probe.fetch('sprite-package-asset-' + str(index) + '-get', content_url)
        probe.check(headers.get('Content-Type') == 'image/png', 'sprite image asset GET is image/png')
        probe.check(len(data) == asset['size'] and hashlib.sha256(data).hexdigest() == asset['sha256'],
                    'sprite image asset bytes match size and SHA')
        head, _, _ = probe.fetch('sprite-package-asset-' + str(index) + '-head', content_url, method='HEAD', expected=b'')
        probe.check(head.get('ETag') == headers.get('ETag'), 'sprite image HEAD ETag matches GET')
        end = min(7, len(data) - 1)
        probe.fetch('sprite-package-asset-' + str(index) + '-range', content_url,
                    headers={'Range': 'bytes=0-' + str(end)}, status=206, expected=data[:end + 1])
        probe.fetch('sprite-package-asset-' + str(index) + '-cached', content_url,
                    headers={'If-None-Match': headers['ETag']}, status=304, expected=b'')
        assets.append(receipt)
    if assets:
        other = probe.register('sprite-package-asset-other-client', select=False)['clientId']
        _, _, error = probe.fetch('sprite-package-asset-foreign', assets[0]['contentUrl'], status=401, client=other)
        probe.check(error.get('code') == 'resourceUnauthorized', 'sprite image foreign client unauthorized')
    accepted = probe.request('sprite-package-assets-project-close', 'POST',
                             '/api/v1/project-sessions/' + source['sourceId'] + '/close', status=202,
                             value={'expectedRevision': source['sourceRevision'], 'policy': 'discard', 'activeJobs': 'wait'})
    probe.job('sprite-package-assets-project-close', accepted)
    if assets:
        time.sleep(3.2)
        probe.fetch('sprite-package-asset-expired', assets[0]['contentUrl'], status=410)

    matrix_source = probe.open_fixture('scale')
    for transparent in (False, True):
        label = 'package-assets-matrix-' + ('transparent' if transparent else 'opaque')
        preview = probe.create_preview(label, matrix_source, transparent)
        for sequence, time_us in enumerate((0, 100000, 300000, 500000), 1):
            preview = probe.frame_request(label + '-' + str(sequence), preview, sequence, time_us)
            probe.terminal(label + '-' + str(sequence), preview, sequence)
        if transparent:
            previous = preview['previewGeneration']
            preview = probe.frame_request(label + '-seek', preview, 5, 100000, 'seek')
            probe.check(preview['previewGeneration'] != previous, label + ': seek changes generation')
            probe.terminal(label + '-seek', preview, 5)
        probe.close_preview(label, preview)
    accepted = probe.request('package-assets-matrix-project-close', 'POST',
                             '/api/v1/project-sessions/' + matrix_source['sourceId'] + '/close', status=202,
                             value={'expectedRevision': matrix_source['sourceRevision'], 'policy': 'discard', 'activeJobs': 'wait'})
    probe.job('package-assets-matrix-project-close', accepted)


def exercise_lifecycle(probe):
    source = probe.projects.get('scale') or probe.open_fixture('scale')
    package_resource_closure(probe, source)
    payload={'source':source,'size':{'width':320,'height':180},'alphaMode':'opaque','purpose':'preview','metadata':probe.metadata('lifecycle-idempotent-create')}
    preview=probe.request('lifecycle-create','POST','/api/v1/preview-sessions',status=201,value=payload)
    replay=probe.request('lifecycle-create-replay','POST','/api/v1/preview-sessions',status=201,value=payload)
    probe.check(replay==preview,'preview create exact idempotent receipt replay')
    probe.active_previews[preview['previewId']]=preview
    cursor=probe.request('lifecycle-before-frame-client','GET','/api/v1/clients/'+probe.client+'/state')['eventCursor']
    preview = probe.frame_request('lifecycle-first', preview, 1, 100000)
    first = probe.terminal('lifecycle-first', preview, 1)
    old_frame = first['session']['latestFrame']
    preview_events(probe,cursor,old_frame)
    before, pages = probe.diagnostics('lifecycle-before', preview)
    old_cursor = pages[0].get('cursor') or first['session'].get('diagnosticsCursor')
    other = probe.register('lifecycle-other-client', select=False)['clientId']
    path = '/api/v1/preview-sessions/' + preview['previewId']
    probe.request('lifecycle-other-state', 'GET', path, client=other, status=404, code='previewSessionNotFound')
    probe.request('lifecycle-other-diagnostics', 'GET', path + '/diagnostics', client=other, status=401, code='resourceUnauthorized')
    probe.fetch('lifecycle-other-frame', old_frame['content']['contentUrl'], client=other, status=401)
    # Stale project revision must fail before switching the current preview input.
    probe.request('lifecycle-stale-create', 'POST', '/api/v1/preview-sessions', status=409, code='revisionConflict',
        value={'source': {**source, 'sourceRevision': '999'}, 'size': {'width':320,'height':180}, 'alphaMode':'opaque'})
    # Resize/alpha use the same immutable project; direct oracle independently renders its target.
    preview = update(probe, 'lifecycle-resize', preview, source, True, 480, 270)
    # A generation switch closes the previous frame handle immediately. An already
    # completed download is independent, but a later read must not cross generations.
    probe.fetch('lifecycle-old-frame-after-resize', old_frame['content']['contentUrl'], status=404)
    if old_cursor:
        probe.request('lifecycle-old-cursor', 'GET', path + '/diagnostics?' + urlencode({'cursor':old_cursor}), status=409, code='diagnosticCursorExpired')
    preview = probe.frame_request('lifecycle-resize', preview, 2, 300000)
    resized = probe.terminal('lifecycle-resize', preview, 2)
    direct = probe.direct('lifecycle-resize', 'scale', 480,270,True,[300000])[0]
    probe.compare_frame('lifecycle-resize',resized,direct,'scale')
    # A real project command advances revision. Existing fixed preview remains usable until explicitly updated.
    probe.project, probe.revision = source['sourceId'],source['sourceRevision']
    summary=probe.request('lifecycle-project-before','GET',probe.project_path)
    result=probe.send('lifecycle-project-rename',{'type':'project.rename','name':'Package public preview revision'})
    changed={**source,'sourceRevision':result['projectRevision']}
    probe.projects['scale']=changed
    probe.request('lifecycle-old-revision-input','PUT',path+'/input',status=409,code='revisionConflict',value={
        'expectedPreviewGeneration':preview['previewGeneration'],'expectedSourceRevision':source['sourceRevision'],
        'source':source,'size':{'width':480,'height':270},'alphaMode':'straight','metadata':probe.metadata('lifecycle-old-revision-input')})
    preview=update(probe,'lifecycle-current-revision',preview,changed,True,480,270)
    preview=probe.frame_request('lifecycle-current-revision',preview,3,300000)
    probe.terminal('lifecycle-current-revision',preview,3)
    probe.close_preview('lifecycle',preview)
    probe.request('lifecycle-closed-diagnostics','GET',path+'/diagnostics',status=404,code='previewSessionNotFound')

    # Closing the package session releases its Worker/runtime lease. A fresh
    # session for the same exact revision must be creatable immediately and must
    # render from the package again instead of inheriting the closed generation.
    recreated = probe.create_preview('lifecycle-recreate', changed, True, 480, 270)
    recreated = probe.frame_request('lifecycle-recreate', recreated, 1, 300000)
    probe.terminal('lifecycle-recreate', recreated, 1)
    probe.close_preview('lifecycle-recreate', recreated)


def exercise_error(probe):
    kind='runtime-error';source=probe.open_fixture(kind)
    preview=probe.create_preview(kind,source)
    direct=probe.direct(kind,kind,320,180,False,[100000,500000])
    expected=[json.loads(p.with_suffix('.json').read_text()) for p in direct]
    probe.check(not expected[0]['hasError'] and expected[1]['hasError'],'conditional actual runtime fixture healthy then Error')
    preview=probe.frame_request(kind+'-healthy',preview,1,100000)
    first=probe.terminal(kind+'-healthy',preview,1)
    probe.compare_frame(kind+'-healthy',first,direct[0],kind)
    old=first['session']['latestFrame']; _,old_png,_=probe.fetch(kind+'-retained-first',old['content']['contentUrl'])
    preview=probe.frame_request(kind+'-error',preview,2,500000)
    failure=probe.terminal(kind+'-error',preview,2,'failed')
    probe.check(failure['session'].get('latestFrame') is None,'runtime Error cannot reuse old frame as latest')
    actual,_=probe.diagnostics(kind+'-error',preview)
    probe.compare_diagnostics(kind+'-error',actual,expected[1])
    # The prior successful frame remains leased under the existing TTL contract.
    probe.fetch(kind+'-old-lease-retained',old['content']['contentUrl'],expected=old_png)
    probe.close_preview(kind,preview)


def identity(pid):
    try:
        stat=Path(f'/proc/{pid}/stat').read_text().rsplit(')',1)[1].split()
        return {'pid':pid,'startTicks':stat[19],'processGroupId':int(stat[2])}
    except (OSError,IndexError):return None


def exercise_runaway(probe,stage,action,healthy_kind='static',recovery_kind='scale'):
    kind='runaway-'+stage+'-'+action
    source=probe.open_fixture(kind); preview=probe.create_preview(kind,source)
    marker=probe.output/'profile/projects/markers'/kind
    preview=probe.frame_request(kind,preview,1,100000)
    deadline=time.monotonic()+15
    while not marker.exists() and time.monotonic()<deadline:time.sleep(.05)
    probe.check(marker.exists(),kind+': actual author entered marker')
    pid,child,actual_stage,nonce=marker.read_text().split('|')
    probe.check(actual_stage==stage and nonce==kind,kind+': actual author stage/nonce')
    worker,owned_child=identity(int(pid)),identity(int(child))
    probe.check(worker is not None and owned_child is not None and worker['processGroupId']==owned_child['processGroupId'],kind+': actual Worker/child same private group')
    probe.observe(int(pid))
    probe.check(Path(f'/proc/{pid}').exists(),kind+': author still live before lifecycle action')
    # Useful unrelated HTTP/Worker rendering while the author remains stuck.
    healthy_source=probe.projects.get(healthy_kind) or probe.open_fixture(healthy_kind)
    healthy=probe.create_preview(kind+'-concurrent',healthy_source)
    healthy=probe.frame_request(kind+'-concurrent',healthy,1,100000)
    probe.terminal(kind+'-concurrent',healthy,1)
    probe.close_preview(kind+'-concurrent',healthy)
    if action=='cancel':
        preview=update(probe,kind+'-cancel',preview,healthy_source)
        preview=probe.frame_request(kind+'-recovered',preview,2,100000)
        probe.terminal(kind+'-recovered',preview,2)
        probe.close_preview(kind,preview)
    elif action=='deadline':
        failed=probe.terminal(kind+'-deadline',preview,1,'failed',35)
        probe.check(failed['session'].get('latestFrame') is None,kind+': deadline never publishes pixels')
        probe.check(failed.get('renderFailureCode')=='WorkerPackageTimedOut',kind+': stable actual Worker deadline')
        preview=update(probe,kind+'-recovery',preview,healthy_source)
        preview=probe.frame_request(kind+'-recovery',preview,2,100000)
        probe.terminal(kind+'-recovery',preview,2);probe.close_preview(kind,preview)
    else:probe.close_preview(kind,preview)
    deadline=time.monotonic()+6
    while time.monotonic()<deadline and (identity(worker['pid'])==worker or identity(owned_child['pid'])==owned_child):time.sleep(.05)
    probe.check(identity(worker['pid'])!=worker and identity(owned_child['pid'])!=owned_child,kind+': original observed Worker and child identities drained')
    closed=probe.request(kind+'-project-close','POST','/api/v1/project-sessions/'+source['sourceId']+'/close',status=202,value={'expectedRevision':source['sourceRevision'],'policy':'discard','activeJobs':'wait'})
    probe.job(kind+'-project-close',closed)
    if action=='close':
        # A new package session after the stuck session and child have drained.
        recovery_source=probe.projects.get(recovery_kind) or probe.open_fixture(recovery_kind)
        recovery=probe.create_preview(kind+'-after-close',recovery_source)
        recovery=probe.frame_request(kind+'-after-close',recovery,1,100000)
        actual=probe.terminal(kind+'-after-close',recovery,1)
        direct=probe.direct(kind+'-after-close',recovery_kind,320,180,False,[100000])[0]
        probe.compare_frame(kind+'-after-close',actual,direct,recovery_kind)
        probe.close_preview(kind+'-after-close',recovery)
    probe.runaway_results.append({'stage':stage,'action':action,'worker':worker,'child':owned_child,'markerSha256':hashlib.sha256(marker.read_bytes()).hexdigest(),'drained':True})


def exercise_b4_rejections(probe):
    source=probe.projects.get('graph') or probe.open_fixture('graph')
    path='/api/v1/project-sessions/'+source['sourceId']
    current=probe.request('b4-current-project','GET',path)
    revision=current['projectRevision']
    probe.request('b4-validation-rejects-package','POST',path+'/validation-jobs',status=422,code='commandNotSupported',value={
        'expectedRevision':revision,'framesPerOutput':1})
    probe.request('b4-export-rejects-package','POST',path+'/export-jobs',status=422,code='commandNotSupported',value={
        'expectedRevision':revision,'format':'opaqueMp4','includeAudio':False})
    measurement=probe.request('b4-measurement-rejects-package','POST',path+'/orchestration/measurements',status=422,code='commandRejected',value={
        'expectedRevision':revision,'mode':'time','timeUs':'100000','width':320,'height':180,'alphaMode':'opaque'})
    probe.check(measurement.get('failureCode')=='SnapshotEffectsUnsupported','package measurement stable failureCode')


def exercise_audio(probe):
    source=probe.open_fixture('audio-scale')
    path='/api/v1/project-sessions/'+source['sourceId']
    summary=probe.request('audio-project-summary','GET',path)
    asset=summary['audioAssetId']; content='/api/v1/assets/'+asset+'/content'
    expected=(probe.output/'profile/projects/owned.wav').read_bytes()
    probe.fetch('audio-before-close',content,expected=expected)
    preview=probe.create_preview('audio-fixed',source)
    accepted=probe.request('audio-project-close','POST',path+'/close',status=202,value={
        'expectedRevision':source['sourceRevision'],'policy':'discard','activeJobs':'wait'})
    probe.job('audio-project-close',accepted)
    # The configured existing Server asset TTL is two seconds. The first render
    # deliberately occurs only after project close and more than one TTL.
    time.sleep(3.2)
    probe.fetch('audio-held-after-project-close',content,expected=expected)
    preview=probe.frame_request('audio-first-after-project-close',preview,1,100000)
    actual=probe.terminal('audio-first-after-project-close',preview,1)
    direct=probe.direct('audio-first-after-project-close','audio-scale',320,180,False,[100000])[0]
    probe.compare_frame('audio-first-after-project-close',actual,direct,'audio-scale')
    probe.close_preview('audio-fixed',preview)
    time.sleep(3.2)
    # Filesystem observation is supplementary evidence of actual owned resource
    # cleanup, not a fabricated public reference-count endpoint.
    probe.fetch('audio-expired-after-preview-close',content,status=410)
    resource_root=Path(probe.current_ready['runDirectory'])/'resources'
    probe.check(not any(p.is_file() and hashlib.sha256(p.read_bytes()).hexdigest()==hashlib.sha256(expected).hexdigest()
        for p in resource_root.rglob('*')),'audio owned bytes cleaned after final preview reference release and configured TTL')


def exercise_budget(probe):
    source=probe.open_fixture('over-budget');preview=probe.create_preview('over-budget',source)
    preview=probe.frame_request('over-budget',preview,1,100000)
    state=probe.terminal('over-budget',preview,1,'failed')
    probe.check(state.get('renderFailureCode')=='WorkerDiagnosticsBudgetExceeded','actual private runtime diagnostic budget stable code')
    probe.check(state['session'].get('latestFrame') is None,'private diagnostic overflow never publishes pixels')
    probe.close_preview('over-budget',preview)


def preview_events(probe,cursor,frame):
    probe.request_count+=1
    path='/api/v1/events'
    request=urllib.request.Request(probe.base_url+path,headers={'X-Mako-Client-Id':probe.client,'X-Mako-Host-Instance-Id':probe.host,
        'Authorization':'Bearer '+probe.tokens[probe.client],'Last-Event-ID':cursor})
    messages=[];current=[]
    with probe.http.open(request,timeout=8) as response:
        probe.check(response.status==200,'actual authenticated package preview SSE replay')
        while len(messages)<32:
            line=response.readline().decode().rstrip('\r\n')
            if line:current.append(line);continue
            if not current:continue
            message='\n'.join(current);messages.append(message);current=[]
            if 'event: preview.frameReady' in message and frame['frameId'] in message:
                probe.check(frame['previewGeneration'] in message and frame['sourceRevision'] in message,'SSE binds exact real package frame generation/revision')
                (probe.output/'lifecycle-actual-sse.json').write_text(json.dumps(messages,indent=2));return
    raise AssertionError('SSE did not replay exact actual package frame')
