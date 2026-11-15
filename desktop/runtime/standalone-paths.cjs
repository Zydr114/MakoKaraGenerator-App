'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

function ensurePrivateDir(dirPath) {
  fs.mkdirSync(dirPath, { recursive: true, mode: 0o700 });
  const info = fs.lstatSync(dirPath);
  if (info.isSymbolicLink() || !info.isDirectory()) {
    throw new Error(`Standalone app directory cannot be a symlink: ${dirPath}`);
  }
}

function prepareStandaloneSession(appName = 'makokara-generator', env = process.env) {
  const xdgDataHome = env.XDG_DATA_HOME && path.isAbsolute(env.XDG_DATA_HOME)
    ? env.XDG_DATA_HOME
    : path.join(os.homedir(), '.local', 'share');
  const base = path.join(xdgDataHome, appName);
  const paths = {
    base,
    profile: path.join(base, 'profile'),
    userData: path.join(base, 'electron-profile'),
    crashDumps: path.join(base, 'electron-crashes'),
  };
  ensurePrivateDir(base);
  ensurePrivateDir(paths.profile);
  ensurePrivateDir(paths.userData);
  ensurePrivateDir(paths.crashDumps);
  return paths;
}

module.exports = { prepareStandaloneSession };
