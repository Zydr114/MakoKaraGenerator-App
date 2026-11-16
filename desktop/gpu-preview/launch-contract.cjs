'use strict';

const fs = require('node:fs');
const path = require('node:path');
const privateFs = require('../runtime/private-fs.cjs');

function readLaunchConfig(file, root, mainPid, options = {}) {
  root = fs.realpathSync(root);
  const env = options.env ?? process.env;
  const runtimeDir = privateFs.defaultRuntimeDataDir('makokara-generator', env);
  const allowedDirs = [
    path.join(root, '.local', 'run'),
    runtimeDir,
    path.join(runtimeDir, 'run'),
  ];
  if (
    !path.isAbsolute(file) ||
    !allowedDirs.some((dir) => privateFs.isSamePath(path.dirname(file), dir)) ||
    !privateFs.isSamePath(fs.realpathSync(file), file)
  ) {
    throw new Error('Invalid desktop launch file');
  }
  privateFs.assertPrivateFile(file, { label: 'Desktop launch file', mode: 0o600 });
  const value = JSON.parse(fs.readFileSync(file, 'utf8'));
  if (
    typeof value.root !== 'string' ||
    !path.isAbsolute(value.root) ||
    !privateFs.isSamePath(value.root, root) ||
    !/^[a-f0-9]{32}$/.test(value.consumerId) ||
    !/^[a-f0-9]{64}$/.test(value.consumerSecret)
  ) {
    throw new Error('Invalid desktop launch identity');
  }
  const page = new URL(value.pageUrl);
  if (
    page.protocol !== 'http:' ||
    page.hostname !== '127.0.0.1' ||
    page.username ||
    page.password ||
    page.pathname !== '/' ||
    page.search ||
    page.hash ||
    !page.port
  ) {
    throw new Error('Desktop page must be a loopback origin');
  }
  if (value.ready) {
    const backend = new URL(value.baseUrl);
    if (
      backend.protocol !== 'http:' ||
      backend.hostname !== '127.0.0.1' ||
      backend.username ||
      backend.password ||
      backend.pathname !== '/' ||
      backend.search ||
      backend.hash ||
      !backend.port ||
      !value.hostInstanceId ||
      value.mainPid !== mainPid
    ) {
      throw new Error('Invalid ready desktop identity');
    }
  }
  return value;
}

module.exports = { readLaunchConfig };
