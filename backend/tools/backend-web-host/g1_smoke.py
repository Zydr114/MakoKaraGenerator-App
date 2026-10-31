#!/usr/bin/env python3
"""Separate real HTTP G1 quota/reclamation probe with explicit 2-second test lease."""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

from smoke import Smoke, existing
from host import ROLES, validate_dist


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dist', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    dist, output = args.dist.resolve(), args.output.resolve()
    validate_dist(dist)
    output.mkdir(parents=True, exist_ok=False)
    run = output / 'private-run'; run.mkdir(mode=0o700)
    credential = run / 'bootstrap.json'
    profile = output / 'profile'; profile.mkdir()
    env = {k: v for k, v in os.environ.items() if not k.startswith('MAKO_BACKEND_')}
    env.update(MAKO_BACKEND_CLIENT_LEASE_SECONDS='2',
               MAKO_BACKEND_STATE_ROOT=str(profile / 'state'),
               MAKO_BACKEND_RESOURCE_ROOT=str(run / 'resources'),
               MAKO_BACKEND_CREDENTIAL_FILE=str(credential),
               MAKO_BACKEND_ALLOWED_ORIGINS='http://localhost:5173',
               MAKO_BACKEND_WORKER_PATH=str(dist / 'worker' / ROLES['worker']),
               MAKO_BACKEND_FFMPEG_PATH='/usr/bin/ffmpeg', MAKO_BACKEND_FFPROBE_PATH='/usr/bin/ffprobe')
    probe = existing.Probe(output, [])
    secret_values = []
    with (run / 'server.log').open('w') as log:
        process = subprocess.Popen([str(dist / 'server' / ROLES['server']), '--urls', 'http://127.0.0.1:0'],
                                   env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            deadline = time.monotonic() + 60
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise AssertionError('G1 test Server exited before ready')
                if credential.exists() and 'BACKEND_READY ' in (run / 'server.log').read_text():
                    break
                time.sleep(.05)
            else:
                raise AssertionError('G1 test Server readiness timed out')
            boot = json.loads(credential.read_text())
            probe.host, probe.base_url, probe.bootstrap = boot['hostInstanceId'], boot['addresses'][0], boot['bootstrapToken']
            secret_values.append(probe.bootstrap)
            first = None
            # No authenticated requests/receipts/assets/projects/SSE: every candidate is truly idle.
            for batch in range(4):
                clients = []
                for index in range(16):
                    client = probe.register(f'batch{batch}-register{index}', select=False)
                    secret_values.extend((client['accessToken'], client['resumeToken']))
                    clients.append(client)
                    first = first or client
                probe.request(f'batch{batch}-live-quota', 'POST', '/api/v1/clients', client=None,
                    headers={'Authorization': 'Bearer ' + probe.bootstrap}, value={'protocolVersion': '1'},
                    status=429, code='clientQuotaExceeded')
                if batch < 3:
                    time.sleep(2.15)
            # At least 64 successful new identities in one Server versus the fixed maxClients16.
            probe.check(len(probe.tokens) == 64, '64 registrations succeeded with quota16 and expired-idle reclamation')
            probe.request('reclaimed-old-access', 'GET', '/api/v1/capabilities', client=first['clientId'],
                          status=401, code='unauthorizedClient')
            probe.request('reclaimed-old-resume', 'POST', '/api/v1/clients', client=None,
                headers={'Authorization': 'Bearer ' + probe.bootstrap}, value={'protocolVersion': '1',
                    'resumeClientId': first['clientId'], 'resumeToken': first['resumeToken']},
                status=401, code='unauthorizedClient')
        finally:
            if process.poll() is None:
                process.send_signal(signal.SIGTERM)
            code = process.wait(timeout=90)
            probe.check(code == 0, 'G1 test Server SIGTERM exit0')
            probe.check(not credential.exists(), 'G1 test private bootstrap removed')
    for path in output.rglob('*'):
        if path.is_file():
            probe.check(not any(value.encode() in path.read_bytes() for value in secret_values),
                        'credentials absent: ' + str(path.relative_to(output)))
    existing.save_json(output / 'summary.json', {'status': 'passed', 'httpRequests': probe.request_count,
        'assertions': len(probe.assertions), 'registrationCount': 64, 'maxClients': 16,
        'leaseSeconds': 2, 'leaseScope': 'explicit test configuration; production default remains300',
        'assertionLabels': probe.assertions, 'serverExitCode': code,
        'scope': 'real quota, valid-lease protection, repeated expired-idle reclaim and stale credentials; other protection/races covered by controlled tests'})
    print(json.dumps({'status': 'passed', 'httpRequests': probe.request_count, 'assertions': len(probe.assertions)}))


if __name__ == '__main__':
    main()
