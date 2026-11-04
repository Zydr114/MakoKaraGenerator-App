#!/usr/bin/env python3
"""Fail closed when an Oracle or its immutable deployment changed."""
import argparse
import hashlib
import json
from pathlib import Path

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

parser = argparse.ArgumentParser()
parser.add_argument('--dist', required=True, type=Path)
parser.add_argument('--oracle-dir', required=True, type=Path)
parser.add_argument('--kind', choices=('video',), required=True)
args = parser.parse_args()
dist, root = args.dist.resolve(), args.oracle_dir.resolve()
deployment = json.loads((dist / 'deployment.json').read_text())
manifest = json.loads((root / 'input-manifest.json').read_text())
if deployment['sourceDirty'] or manifest['kind'] != args.kind or manifest['deploymentManifestSha256'] != digest(dist / 'deployment.json'):
    raise SystemExit('deployment/oracle provenance mismatch')
for name, expected in deployment['files'].items():
    if digest(dist / name) != expected:
        raise SystemExit('deployment mismatch: ' + name)
for name, expected in manifest['files'].items():
    if digest(root / 'publish' / name) != expected:
        raise SystemExit('oracle mismatch: ' + name)
for name, expected in manifest['references'].items():
    if digest(dist / name) != expected or digest(root / 'publish' / Path(name).name) != expected:
        raise SystemExit('oracle dependency mismatch: ' + name)
print(json.dumps({'status': 'matched', 'kind': args.kind, 'sourceCommit': deployment['sourceCommit'],
                  'deploymentSha256': manifest['deploymentManifestSha256']}))
