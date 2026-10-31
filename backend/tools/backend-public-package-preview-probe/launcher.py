"""Probe-specific launcher selection; start lifecycle copied from backend-web-host/smoke.py."""
import json,queue,subprocess,sys,threading,time
from pathlib import Path
def save_json(path,value):path.write_text(json.dumps(value,indent=2))
class ConfiguredLauncher:
    def start(self, name):
        self.current_ready = None
        self.monitor = None
        self.launcher = subprocess.Popen([sys.executable, str(Path(__file__).with_name(getattr(self,'launcher_name','configured_host.py'))), 'run', '--dist', str(self.dist),
            '--profile', str(self.output / 'profile'), '--origin', 'http://localhost:5173',
            '--origin', 'http://127.0.0.1:5173'], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        # Observe from launch, including startup capability Workers that can exit
        # before WEB_BACKEND_READY. The same identity monitor continues to drain.
        launcher_pid=self.launcher.pid
        def monitor():
            while not self.monitor_stop.wait(.01):self.observe(launcher_pid)
        self.monitor_stop.clear()
        self.monitor=threading.Thread(target=monitor,daemon=True);self.monitor.start()
        lines = queue.Queue()
        def read():
            for line in self.launcher.stdout:
                lines.put(line)
            lines.put(None)
        threading.Thread(target=read, daemon=True).start()
        deadline = time.monotonic() + 90
        logged = []
        while time.monotonic() < deadline:
            try:
                line = lines.get(timeout=0.2)
            except queue.Empty:
                continue
            if line is None:
                break
            logged.append(line)
            if line.startswith('WEB_BACKEND_READY '):
                ready = json.loads(line.split(' ', 1)[1])
                credential = Path(ready['credentialFile'])
                self.check(credential.stat().st_mode & 0o777 == 0o600, name + ': credential0600')
                self.check(credential.parent.stat().st_mode & 0o777 == 0o700, name + ': run0700')
                boot = json.loads(credential.read_text())
                self.bootstrap = boot['bootstrapToken']
                self.secrets.append(self.bootstrap)
                self.host, self.base_url = ready['hostInstanceId'], ready['addresses'][0]
                self.client = None
                self.check(self.base_url.startswith('http://127.0.0.1:'), name + ': random loopback')
                self.current_ready = ready
                self.runs.append({'ready': ready, 'launcherPid': self.launcher.pid})
                save_json(self.output / (name + '-launcher.json'), {'stdout': logged, **ready})
                return
        raise AssertionError(name + ': launcher failed to become ready: ' + ''.join(logged))
