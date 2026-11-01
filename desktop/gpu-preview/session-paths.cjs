'use strict';
const fs = require('node:fs');
const path = require('node:path');

function prepareDesktopSession(root, name) {
  if (name !== undefined && (typeof name !== 'string' || !/^[a-z0-9][a-z0-9-]{0,47}$/.test(name))) throw new Error('Invalid desktop session name');
  const local = path.join(root, '.local');
  const base = name === undefined ? local : path.join(local, 'acceptance', name);
  const directories = name === undefined ? [local] : [local, path.join(local, 'acceptance'), base];
  for (const directory of directories) {
    fs.mkdirSync(directory, {recursive:true,mode:0o700});
    if (fs.realpathSync(directory) !== directory) throw new Error('Desktop session directory cannot be a symlink');
  }
  return {profile:path.join(base, 'desktop-profile'),userData:path.join(base, 'desktop-electron-profile'),crashDumps:path.join(base, 'desktop-electron-crashes')};
}
module.exports = {prepareDesktopSession};
