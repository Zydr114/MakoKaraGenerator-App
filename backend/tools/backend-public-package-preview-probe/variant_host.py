#!/usr/bin/env python3
"""Run an explicitly declared probe-owned variant; never modify the canonical deployment."""
import argparse
import importlib.util
import json
import os
from pathlib import Path

ROOT=Path(__file__).resolve().parents[2]
spec=importlib.util.spec_from_file_location('host',Path(__file__).with_name('configured_host.py'));host=importlib.util.module_from_spec(spec);spec.loader.exec_module(host)
def validate_variant(dist):
    manifest=json.loads((dist/'deployment.json').read_text());variant=manifest['probeVariant']
    assert variant['kind'] in ('missing-refs','historical-original-version','historical-current-version')
    actual=host.inventory(dist);actual.pop('deployment.json',None)
    assert actual==manifest['files'],'Variant file inventory mismatch'
    for role,app in host.ROLES.items():assert os.access(dist/role/app,os.X_OK)
    if variant['kind']=='missing-refs':assert not (dist/'worker/script-refs').exists()
    # Deliberately skip only the standard launcher's preemptive refs guard:
    # the actual Server/Worker capability handshake is under test here.
    return manifest
if __name__=='__main__':
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('command',choices=['run']);parser.add_argument('--asset-ttl-seconds',type=int,default=2);parser.add_argument('--dist',type=Path,required=True);parser.add_argument('--profile',type=Path,required=True);parser.add_argument('--origin',action='append',required=True)
    parser.add_argument('--ffmpeg',type=Path,default=Path('/usr/bin/ffmpeg'));parser.add_argument('--ffprobe',type=Path,default=Path('/usr/bin/ffprobe'))
    args=parser.parse_args();host.validate_dist=validate_variant;raise SystemExit(host.run(args))
