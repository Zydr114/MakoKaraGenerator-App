'use strict';
const fs = require('node:fs');
const path = require('node:path');

function readLaunchConfig(file, root, mainPid) {
  root = fs.realpathSync(root);
  const run = path.join(root, '.local/run');
  if (!path.isAbsolute(file) || path.dirname(file) !== run || fs.realpathSync(file) !== file) throw new Error('Invalid desktop launch file');
  const info = fs.lstatSync(file);
  if (!info.isFile() || info.uid !== process.getuid() || (info.mode & 0o777) !== 0o600) throw new Error('Desktop launch file must be private');
  const value = JSON.parse(fs.readFileSync(file, 'utf8'));
  if (value.root !== root || !/^[a-f0-9]{32}$/.test(value.consumerId) || !/^[a-f0-9]{64}$/.test(value.consumerSecret)) throw new Error('Invalid desktop launch identity');
  const page = new URL(value.pageUrl);
  if (page.protocol !== 'http:' || page.hostname !== '127.0.0.1' || page.username || page.password || page.pathname !== '/' || page.search || page.hash || !page.port) throw new Error('Desktop page must be a loopback origin');
  if (value.ready) {
    const backend = new URL(value.baseUrl);
    if (backend.protocol !== 'http:' || backend.hostname !== '127.0.0.1' || backend.username || backend.password || backend.pathname !== '/' || backend.search || backend.hash || !backend.port || !value.hostInstanceId || value.mainPid !== mainPid) throw new Error('Invalid ready desktop identity');
  }
  return value;
}
module.exports = { readLaunchConfig };
