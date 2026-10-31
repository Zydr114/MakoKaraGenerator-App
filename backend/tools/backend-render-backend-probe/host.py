#!/usr/bin/env python3
"""Canonical matched launcher with this task's serialized dotnet lock."""
import importlib.util
from pathlib import Path
import sys

sys.dont_write_bytecode = True
source = Path(__file__).resolve().parents[1] / 'backend-web-host/host.py'
spec = importlib.util.spec_from_file_location('render_selection_host', source)
host = importlib.util.module_from_spec(spec)
spec.loader.exec_module(host)
host.LOCK = '/tmp/mkg-gpu-render-dotnet.lock'

if __name__ == '__main__':
    sys.exit(host.main())
