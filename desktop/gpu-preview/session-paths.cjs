'use strict';

const path = require('node:path');
const privateFs = require('../runtime/private-fs.cjs');

function prepareDesktopSession(root, name, options = {}) {
  if (name !== undefined && (typeof name !== 'string' || !/^[a-z0-9][a-z0-9-]{0,47}$/.test(name))) {
    throw new Error('Invalid desktop session name');
  }
  const local = options.runtimeRoot ?? (process.platform === 'win32'
    ? privateFs.defaultRuntimeDataDir()
    : path.join(root, '.local'));
  const base = name === undefined ? local : path.join(local, 'acceptance', name);
  const directories = name === undefined ? [local] : [local, path.join(local, 'acceptance'), base];
  for (const directory of directories) {
    privateFs.ensurePrivateDir(directory, { label: 'Desktop session directory' });
  }
  return {
    profile: path.join(base, 'desktop-profile'),
    userData: path.join(base, 'desktop-electron-profile'),
    crashDumps: path.join(base, 'desktop-electron-crashes'),
  };
}

module.exports = { prepareDesktopSession };
