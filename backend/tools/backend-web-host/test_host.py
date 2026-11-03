"""Deployment admission checks; no real Git mutation, publish, or backend process."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location('backend_web_host', Path(__file__).with_name('host.py'))
host = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(host)


class DeploymentTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.repo = self.root / 'repo'
        self.repo.mkdir()
        (self.repo / 'Directory.Build.props').write_text('<VersionPrefix>0.110.1</VersionPrefix>')
        self.dist = self.root / 'dist'

    def role_files(self, role, scripting=b'matched scripting ABI'):
        directory = self.dist / role
        directory.mkdir(parents=True)
        app = directory / host.ROLES[role]
        app.write_bytes(b'apphost')
        app.chmod(0o700)
        (directory / 'MakoKaraGenerator.Scripting.Abstractions.dll').write_bytes(scripting)
        if role == 'worker':
            refs = directory / 'script-refs' / 'net10.0'
            refs.mkdir(parents=True)
            (refs / 'System.Runtime.dll').write_bytes(b'reference assembly')

    def manifest(self):
        result = {'rid': 'linux-x64', 'selfContained': True, 'files': host.inventory(self.dist)}
        (self.dist / 'deployment.json').write_text(json.dumps(result))
        return result

    def publish(self, drift_at=None, mismatched=False):
        reads = 0
        def command(arguments, **_kwargs):
            nonlocal reads
            if arguments[:3] == ['git', 'rev-parse', 'HEAD']:
                changed = drift_at is not None and reads >= drift_at
                reads += 1
                return subprocess.CompletedProcess(arguments, 0, stdout=('later-commit' if changed else 'initial-commit') + '\n')
            if arguments[:2] == ['git', 'diff'] or arguments[:2] == ['git', 'ls-files']:
                return subprocess.CompletedProcess(arguments, 0, stdout=b'')
            if arguments[:5] == ['flock', '-o', host.LOCK, 'dotnet', 'publish']:
                role = Path(arguments[-1]).name
                self.role_files(role, b'different scripting ABI' if mismatched and role == 'worker' else b'matched scripting ABI')
                return subprocess.CompletedProcess(arguments, 0)
            raise AssertionError(f'Unexpected subprocess: {arguments}')
        with patch.object(host, 'REPO', self.repo), patch.object(host, 'source_inputs', return_value={'source.cs': 'fixed-input'}), \
                patch.object(host.subprocess, 'run', side_effect=command):
            return host.publish(SimpleNamespace(dist=self.dist))

    def test_head_drift_at_each_publish_boundary_is_rejected(self):
        # Initial HEAD capture, then before/after each role, before manifest and before ready.
        for checkpoint in range(1, 7):
            with self.subTest(checkpoint=checkpoint):
                self.dist = self.root / f'drift-{checkpoint}'
                output = io.StringIO()
                with contextlib.redirect_stdout(output):
                    with self.assertRaisesRegex(ValueError, 'HEAD'):
                        self.publish(drift_at=checkpoint)
                self.assertNotIn('DEPLOYMENT_READY', output.getvalue())
                if checkpoint <= 5:
                    self.assertFalse((self.dist / 'deployment.json').exists())

    def test_publish_different_scripting_assemblies_rejected_without_manifest(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            with self.assertRaisesRegex(ValueError, 'Scripting.Abstractions'):
                self.publish(mismatched=True)
        self.assertFalse((self.dist / 'deployment.json').exists())
        self.assertNotIn('DEPLOYMENT_READY', output.getvalue())

    def test_validate_dist_rejects_individually_valid_inventory_with_different_scripting(self):
        self.role_files('server', b'server ABI')
        self.role_files('worker', b'worker ABI')
        self.manifest()  # Each file matches its own recorded hash; cross-role equality is independent.
        with self.assertRaisesRegex(ValueError, 'Scripting.Abstractions'):
            host.validate_dist(self.dist)

    def test_matching_publish_and_revalidation_are_accepted(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            self.assertEqual(0, self.publish())
        self.assertIn('DEPLOYMENT_READY', output.getvalue())
        self.assertEqual('initial-commit', host.validate_dist(self.dist)['sourceCommit'])


if __name__ == '__main__':
    unittest.main()
