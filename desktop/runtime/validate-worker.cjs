'use strict';

const { parentPort, workerData } = require('node:worker_threads');
const fs = require('node:fs');
const path = require('node:path');
const { validateDesktopDeployment, validateDist } = require('./backend-host.cjs');

try {
  if (workerData?.mode === 'desktop') {
    const validated = validateDesktopDeployment(workerData.distDir, workerData.expectedElectron);
    parentPort.postMessage({
      ok: true,
      result: {
        dist: validated.dist,
        deployment: validated.deployment,
        gpuManifest: validated.gpuManifest,
      },
    });
  } else if (workerData?.mode === 'dist') {
    const dist = fs.realpathSync(path.resolve(workerData.distDir));
    const manifest = validateDist(dist);
    parentPort.postMessage({
      ok: true,
      result: { dist, manifest },
    });
  } else {
    throw new Error(`Unsupported validation worker mode: ${workerData?.mode}`);
  }
} catch (error) {
  parentPort.postMessage({
    ok: false,
    message: error?.message ?? String(error),
    code: error?.code,
  });
}
