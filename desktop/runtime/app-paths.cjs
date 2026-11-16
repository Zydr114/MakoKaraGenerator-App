'use strict';

const path = require('node:path');
const privateFs = require('./private-fs.cjs');

function appRuntimePaths(root, env = process.env) {
  const runtimeRoot = process.platform === 'win32'
    ? privateFs.defaultRuntimeDataDir('makokara-generator', env)
    : path.join(root, '.local');
  return {
    runtimeRoot,
    run: path.join(runtimeRoot, 'run'),
    profile: path.join(runtimeRoot, 'profile'),
  };
}

module.exports = { appRuntimePaths };
