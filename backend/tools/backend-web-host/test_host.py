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
        app = directory / host.role_executable(role)
        app.write_bytes(b'apphost')
        if not host.IS_WINDOWS:
            app.chmod(0o700)
        (directory / 'MakoKaraGenerator.Scripting.Abstractions.dll').write_bytes(scripting)
        if role == 'worker':
            refs = directory / 'script-refs' / 'net10.0'
            refs.mkdir(parents=True)
            (refs / 'System.Runtime.dll').write_bytes(b'reference assembly')

    def manifest(self):
        result = {'rid': host.target_rid(), 'selfContained': True, 'files': host.inventory(self.dist)}
        (self.dist / 'deployment.json').write_text(json.dumps(result))
        return result

    def publish(self, drift_at=None, mismatched=False):
        reads = 0
        lock_entries = 0

        @contextlib.contextmanager
        def fake_lock(*_args, **_kwargs):
            nonlocal lock_entries
            lock_entries += 1
            yield self.root / 'publish.lock'

        def command(arguments, **_kwargs):
            nonlocal reads
            if arguments[:3] == ['git', 'rev-parse', 'HEAD']:
                changed = drift_at is not None and reads >= drift_at
                reads += 1
                return subprocess.CompletedProcess(arguments, 0, stdout=('later-commit' if changed else 'initial-commit') + '\n')
            if arguments[:2] == ['git', 'diff'] or arguments[:2] == ['git', 'ls-files']:
                return subprocess.CompletedProcess(arguments, 0, stdout=b'')
            expected_prefix = ['dotnet', 'publish'] if host.IS_WINDOWS else ['flock', '-o', host.LINUX_LOCK, 'dotnet', 'publish']
            if arguments[:len(expected_prefix)] == expected_prefix:
                role = Path(arguments[-1]).name
                self.role_files(role, b'different scripting ABI' if mismatched and role == 'worker' else b'matched scripting ABI')
                return subprocess.CompletedProcess(arguments, 0)
            raise AssertionError(f'Unexpected subprocess: {arguments}')
        with patch.object(host, 'REPO', self.repo), patch.object(host, 'source_inputs', return_value={'source.cs': 'fixed-input'}), \
                patch.object(host, 'acquire_publish_lock', side_effect=fake_lock), \
                patch.object(host.subprocess, 'run', side_effect=command):
            result = host.publish(SimpleNamespace(dist=self.dist))
        if host.IS_WINDOWS:
            self.assertEqual(2, lock_entries)
        return result

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

class PrivateDirectoryTests(unittest.TestCase):
    def test_complete_acl_shape_rejects_foreign_explicit_grants_in_memory(self):
        sid = 'S-1-5-21-123-456-789-1001'
        rule = {'sid': sid, 'type': 'Allow', 'rights': 2032127, 'inheritance': 3, 'propagation': 0}
        host._assert_private_windows_acl({'protected': True, 'rules': [rule]}, sid)
        for acl in [
            {'protected': False, 'rules': [rule]},
            {'protected': True, 'rules': []},
            {'protected': True, 'rules': [rule, {'sid': 'S-1-1-0', 'type': 'Allow'}]},
            {'protected': True, 'rules': [{**rule, 'inheritance': 0}]},
        ]:
            with self.subTest(acl=acl), self.assertRaisesRegex(ValueError, 'current-user-only'):
                host._assert_private_windows_acl(acl, sid)

    def test_existing_child_is_not_admitted_by_ancestor_cache(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            child = root / 'existing'
            child.mkdir()
            host._PROTECTED_DIRS.clear()
            sid = 'S-1-5-21-123-456-789-1001'
            rule = {'sid': sid, 'type': 'Allow', 'rights': 2032127, 'inheritance': 3, 'propagation': 0}
            with patch.object(host, 'IS_WINDOWS', True), \
                    patch.object(host, '_assert_windows_persistent_acl_volume'), \
                    patch.object(host, '_current_user_sid', return_value=sid), \
                    patch.object(host, '_windows_directory_acl', return_value={'protected': True, 'rules': [rule]}) as setter:
                host.ensure_private_dir(root)
                host.ensure_private_dir(child)
                host.ensure_private_dir(child)
                self.assertEqual([root, child], [call.args[0] for call in setter.call_args_list])
            host._PROTECTED_DIRS.clear()

    @unittest.skipUnless(host.IS_WINDOWS, 'Windows DACL / LockFileEx integration')
    def test_own_temporary_directory_dacl_and_lock_wait_longer_than_15_seconds(self):
        # Only tighten our own temporary directory; no broad ACE is added.
        import sys
        import time
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'private'
            host.ensure_private_dir(root)
            lock = root / 'publish.lock'
            code = (
                'import importlib.util,sys,time;from pathlib import Path;'
                's=importlib.util.spec_from_file_location("lock_host",sys.argv[1]);'
                'h=importlib.util.module_from_spec(s);s.loader.exec_module(h);'
                'h.ensure_private_dir(Path(sys.argv[2]).parent);print("ready",flush=True);start=time.monotonic();'
                '\nwith h.acquire_publish_lock(Path(sys.argv[2])):\n print(time.monotonic()-start,flush=True)'
            )
            with host.acquire_publish_lock(lock):
                child = subprocess.Popen([sys.executable, '-c', code, str(Path(host.__file__).absolute()), str(lock)],
                                         stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
                try:
                    self.assertEqual('ready', child.stdout.readline().strip())
                    time.sleep(17)
                    if child.poll() is not None:
                        out, err = child.communicate()
                        self.fail(f'contender exited before release: {out} {err}')
                except BaseException:
                    child.kill()  # This test owns this Popen identity.
                    child.communicate()
                    raise
            out, err = child.communicate(timeout=20)
            self.assertEqual(0, child.returncode, err)
            self.assertGreater(float(out.strip()), 15)
            self.assertEqual(b'', lock.read_bytes())

    def test_symlink_ancestor_is_checked_before_canonicalization(self):
        target = Path('private') / 'alias' / 'child'
        # Memory-only link avoids Windows Developer Mode requirements.
        def linked(path):
            return path.name == 'alias'
        with patch.object(Path, 'is_symlink', linked):
            with self.assertRaisesRegex(ValueError, 'symlink'):
                host._assert_no_symlink_ancestors(target)


if __name__ == '__main__':
    unittest.main()
