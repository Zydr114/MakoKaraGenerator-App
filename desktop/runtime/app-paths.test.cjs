'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const os = require('node:os');
const { appRuntimePaths } = require('./app-paths.cjs');

test('Windows runtime override isolates bootstrap and profile from repository builds', { skip: process.platform !== 'win32' }, () => {
  const repo = path.join(os.tmpdir(), 'app-repo');
  const isolated = path.join(os.tmpdir(), 'isolated-runtime');
  const paths = appRuntimePaths(repo, { MAKO_RUNTIME_HOME: isolated });
  assert.deepEqual(paths, {
    runtimeRoot: path.join(isolated, 'makokara-generator'),
    run: path.join(isolated, 'makokara-generator', 'run'),
    profile: path.join(isolated, 'makokara-generator', 'profile'),
  });
  assert.equal(path.join(paths.run, 'backend.json'), path.join(isolated, 'makokara-generator', 'run', 'backend.json'));
  assert.throws(() => appRuntimePaths(repo, { MAKO_RUNTIME_HOME: 'relative' }), /absolute/);
});

test('Linux runtime retains the repository .local layout', { skip: process.platform !== 'linux' }, () => {
  const repo = path.join(os.tmpdir(), 'app-repo');
  const paths = appRuntimePaths(repo, { MAKO_RUNTIME_HOME: '/ignored-on-linux' });
  assert.equal(paths.runtimeRoot, path.join(repo, '.local'));
  assert.equal(paths.run, path.join(repo, '.local', 'run'));
  assert.equal(paths.profile, path.join(repo, '.local', 'profile'));
});
