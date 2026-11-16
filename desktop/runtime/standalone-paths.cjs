'use strict';

const path = require('node:path');
const privateFs = require('./private-fs.cjs');

function prepareStandaloneSession(appName = 'makokara-generator', env = process.env) {
  const base = privateFs.defaultRuntimeDataDir(appName, env);
  const paths = {
    base,
    profile: path.join(base, 'profile'),
    userData: path.join(base, 'electron-profile'),
    crashDumps: path.join(base, 'electron-crashes'),
  };
  privateFs.ensurePrivateDir(base, { label: 'Standalone app directory' });
  privateFs.ensurePrivateDir(paths.profile, { label: 'Standalone profile directory' });
  privateFs.ensurePrivateDir(paths.userData, { label: 'Standalone userData directory' });
  privateFs.ensurePrivateDir(paths.crashDumps, { label: 'Standalone crashDumps directory' });
  return paths;
}

module.exports = { prepareStandaloneSession };
