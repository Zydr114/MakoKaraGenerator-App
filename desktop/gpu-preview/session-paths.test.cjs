'use strict';
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const {prepareDesktopSession} = require('./session-paths.cjs');
test('named acceptance sessions isolate both profiles and reject path escape/symlinks',()=>{
  const root=fs.mkdtempSync(path.join(os.tmpdir(),'mako-session-'));
  try {
    assert.equal(prepareDesktopSession(root).profile,path.join(root,'.local/desktop-profile'));
    const named=prepareDesktopSession(root,'quality-repro');
    assert.equal(named.profile,path.join(root,'.local/acceptance/quality-repro/desktop-profile'));
    assert.equal(named.userData,path.join(root,'.local/acceptance/quality-repro/desktop-electron-profile'));
    for(const name of ['../outside','','UPPER','x/y',null,'a'.repeat(49)])assert.throws(()=>prepareDesktopSession(root,name),/session name/);
    fs.symlinkSync(root,path.join(root,'.local/acceptance/escape'));
    assert.throws(()=>prepareDesktopSession(root,'escape'),/symlink/);
  } finally {fs.rmSync(root,{recursive:true,force:true});}
});
