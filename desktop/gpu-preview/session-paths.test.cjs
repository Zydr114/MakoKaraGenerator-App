'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { prepareDesktopSession } = require('./session-paths.cjs');
const privateFs = require('../runtime/private-fs.cjs');

test('named acceptance sessions isolate both profiles and reject path escape/symlinks', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'mako-session-'));
  try {
    const expectedDefault = process.platform === 'win32' ? privateFs.defaultRuntimeDataDir() : path.join(root, '.local');
    // Check the platform default without touching a user's real runtime directory.
    const ensure = privateFs.ensurePrivateDir;
    let defaultSession;
    try {
      privateFs.ensurePrivateDir = () => {};
      defaultSession = prepareDesktopSession(root);
    } finally { privateFs.ensurePrivateDir = ensure; }
    assert.equal(defaultSession.profile, path.join(expectedDefault, 'desktop-profile'));
    const options = { runtimeRoot: path.join(root, '.local') };
    const named = prepareDesktopSession(root, 'quality-repro', options);
    assert.equal(named.profile, path.join(root, '.local/acceptance/quality-repro/desktop-profile'));
    assert.equal(named.userData, path.join(root, '.local/acceptance/quality-repro/desktop-electron-profile'));
    for (const name of ['../outside', '', 'UPPER', 'x/y', null, 'a'.repeat(49)]) {
      assert.throws(() => prepareDesktopSession(root, name, options), /session name/);
    }
    fs.symlinkSync(
      root,
      path.join(root, '.local/acceptance/escape'),
      process.platform === 'win32' ? 'junction' : undefined,
    );
    assert.throws(() => prepareDesktopSession(root, 'escape', options), /symlink/);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
