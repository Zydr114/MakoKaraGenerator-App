import {test} from 'node:test';
import assert from 'node:assert/strict';
import {execFileSync} from 'node:child_process';
import {resolve} from 'node:path';
test('desktop host forwards only the exact synthetic benchmark opt-in',()=>{
  const script=`import importlib.util,json,sys
s=importlib.util.spec_from_file_location('host',sys.argv[1]);m=importlib.util.module_from_spec(s);s.loader.exec_module(m)
print(json.dumps([m.sanitized_backend_environment({'PATH':'fixture','MAKO_BACKEND_GPU_PREVIEW_BENCHMARK':v,'MAKO_BACKEND_WORKER_PATH':'foreign','MAKO_BACKEND_GPU_PREVIEW_BENCHMARK_OTHER':'1'}) for v in ('1','true','0','')]))`;
  const results=JSON.parse(execFileSync('python3',['-c',script,resolve('backend/tools/backend-web-host/host.py')],{encoding:'utf8'}));
  assert.deepEqual(results,[{PATH:'fixture',MAKO_BACKEND_GPU_PREVIEW_BENCHMARK:'1'},{PATH:'fixture'},{PATH:'fixture'},{PATH:'fixture'}]);
});
