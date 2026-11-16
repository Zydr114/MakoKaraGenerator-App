import {test} from 'node:test';
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {existsSync} from 'node:fs';
import {delimiter, isAbsolute, join, resolve} from 'node:path';

function resolvePythonCommand() {
  if (process.platform !== 'win32') return 'python3';
  const windowsApps = join('microsoft', 'windowsapps').toLowerCase();
  for (const dir of String(process.env.PATH ?? '').split(delimiter)) {
    if (!dir || !isAbsolute(dir) || dir.toLowerCase().includes(windowsApps)) continue;
    for (const name of ['python.exe', 'python3.exe']) {
      const candidate = join(dir, name);
      if (existsSync(candidate)) return candidate;
    }
  }
  return 'python';
}

test('desktop host forwards only the exact synthetic benchmark opt-in',()=>{
  const script=`import importlib.util,json,sys
s=importlib.util.spec_from_file_location('host',sys.argv[1]);m=importlib.util.module_from_spec(s);s.loader.exec_module(m)
print(json.dumps([m.sanitized_backend_environment({'PATH':'fixture','MAKO_BACKEND_GPU_PREVIEW_BENCHMARK':v,'MAKO_BACKEND_WORKER_PATH':'foreign','MAKO_BACKEND_GPU_PREVIEW_BENCHMARK_OTHER':'1'}) for v in ('1','true','0','')]))`;
  const results=JSON.parse(execFileSync(resolvePythonCommand(),['-c',script,resolve('backend/tools/backend-web-host/host.py')],{encoding:'utf8'}));
  assert.deepEqual(results,[{PATH:'fixture',MAKO_BACKEND_GPU_PREVIEW_BENCHMARK:'1'},{PATH:'fixture'},{PATH:'fixture'},{PATH:'fixture'}]);
});
