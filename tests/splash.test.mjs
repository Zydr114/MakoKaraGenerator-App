import { test } from 'node:test';
import assert from 'node:assert/strict';
import { EventEmitter } from 'node:events';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const {
  SPLASH_HEIGHT,
  SPLASH_WIDTH,
  computeCenteredBounds,
  createSplashController,
  resolveStagePayload,
} = require('../desktop/splash/controller.cjs');

class FakeWebContents extends EventEmitter {
  constructor() {
    super();
    this.sent = [];
    this.windowOpenHandler = null;
  }

  setWindowOpenHandler(handler) {
    this.windowOpenHandler = handler;
  }

  send(channel, payload) {
    this.sent.push({ channel, payload });
  }
}

class FakeBrowserWindow extends EventEmitter {
  constructor(options) {
    super();
    this.options = options;
    this.webContents = new FakeWebContents();
    this.visible = Boolean(options.show);
    this.minimized = false;
    this.focused = false;
    this.destroyed = false;
    this.loadedFiles = [];
    this.callLog = [];
    FakeBrowserWindow.instances.push(this);
  }

  loadFile(filePath, opts) {
    this.loadedFiles.push({ filePath, opts });
    return Promise.resolve();
  }

  show() {
    this.callLog.push('show');
    this.visible = true;
  }

  focus() {
    this.callLog.push('focus');
    this.focused = true;
  }

  restore() {
    this.callLog.push('restore');
    this.minimized = false;
  }

  isVisible() {
    return this.visible;
  }

  isMinimized() {
    return this.minimized;
  }

  isDestroyed() {
    return this.destroyed;
  }

  destroy() {
    if (this.destroyed) return;
    this.callLog.push('destroy');
    this.destroyed = true;
    this.visible = false;
    this.emit('closed');
  }
}
FakeBrowserWindow.instances = [];

test('computeCenteredBounds centers inside workArea and clamps on small displays', () => {
  assert.deepEqual(
    computeCenteredBounds({ x: 100, y: 40, width: 1920, height: 1040 }),
    {
      x: 100 + Math.round((1920 - SPLASH_WIDTH) / 2),
      y: 40 + Math.round((1040 - SPLASH_HEIGHT) / 2),
      width: SPLASH_WIDTH,
      height: SPLASH_HEIGHT,
    },
  );
  assert.deepEqual(
    computeCenteredBounds({ x: 0, y: 0, width: 400, height: 240 }),
    { x: 0, y: 0, width: 400, height: 240 },
  );
});

test('createSplashController enforces sandboxed borderless window options, updates stages, and hands off safely', () => {
  FakeBrowserWindow.instances.length = 0;
  const order = [];
  let userClosed = 0;
  const splash = createSplashController({
    BrowserWindow: FakeBrowserWindow,
    screen: { getPrimaryDisplay: () => ({ workArea: { x: 0, y: 0, width: 1920, height: 1080 } }) },
    version: '0.117.0',
    iconPath: '/tmp/icon.png',
    onUserClose: () => { userClosed++; },
  });

  const win = splash.show('validating');
  assert.equal(win.options.frame, false);
  assert.equal(win.options.transparent, false);
  assert.equal(win.options.alwaysOnTop, false);
  assert.equal(win.options.skipTaskbar, true);
  assert.equal(win.options.resizable, false);
  assert.equal(win.options.show, false);
  assert.equal(win.options.webPreferences.sandbox, true);
  assert.equal(win.options.webPreferences.contextIsolation, true);
  assert.equal(win.options.webPreferences.nodeIntegration, false);
  assert.deepEqual(win.webContents.windowOpenHandler(), { action: 'deny' });
  assert.equal(win.loadedFiles.length, 1);
  assert.equal(win.loadedFiles[0].opts.query.v, '0.117.0');

  // Updates before did-finish-load are retained and pushed once loaded
  splash.update('backend');
  assert.equal(win.webContents.sent.length, 0);
  win.webContents.emit('did-finish-load');
  assert.equal(win.webContents.sent.length, 1);
  assert.equal(win.webContents.sent[0].channel, 'mako-splash:state');
  assert.equal(win.webContents.sent[0].payload.stage, 'backend');

  // ready-to-show reveals splash
  assert.equal(win.isVisible(), false);
  win.emit('ready-to-show');
  assert.equal(win.isVisible(), true);

  // second-instance focus brings splash to front
  win.minimized = true;
  assert.equal(splash.focus(), true);
  assert.equal(win.minimized, false);
  assert.equal(win.focused, true);

  // handoff shows & focuses main window BEFORE destroying splash and does NOT trigger onUserClose
  const origDestroy = win.destroy.bind(win);
  win.destroy = () => {
    order.push('splash:destroy');
    origDestroy();
  };
  const mainWindow = {
    visible: false,
    isDestroyed: () => false,
    isVisible() { return this.visible; },
    show() { this.visible = true; order.push('main:show'); },
    focus() { order.push('main:focus'); },
  };
  assert.equal(splash.handoffTo(mainWindow), true);
  assert.deepEqual(order, ['main:show', 'main:focus', 'splash:destroy']);
  assert.equal(win.isDestroyed(), true);
  assert.equal(userClosed, 0);
  assert.equal(splash.isAborted(), false);
});

test('closing splash before handoff signals user abort and blocks late handoff', () => {
  FakeBrowserWindow.instances.length = 0;
  let userClosed = 0;
  const splash = createSplashController({
    BrowserWindow: FakeBrowserWindow,
    screen: { getPrimaryDisplay: () => ({ workArea: { x: 0, y: 0, width: 1280, height: 720 } }) },
    version: '0.117.0',
    onUserClose: () => { userClosed++; },
  });

  const win = splash.show('validating');
  win.emit('closed');
  assert.equal(userClosed, 1);
  assert.equal(splash.isAborted(), true);

  let mainShown = false;
  const handedOff = splash.handoffTo({
    isDestroyed: () => false,
    isVisible: () => false,
    show: () => { mainShown = true; },
    focus: () => {},
  });
  assert.equal(handedOff, false);
  assert.equal(mainShown, false);
});

test('resolveStagePayload falls back gracefully on unknown stage', () => {
  const payload = resolveStagePayload('unknown-stage', '0.117.0');
  assert.equal(payload.version, '0.117.0');
  assert.equal(typeof payload.text, 'string');
  assert.ok(payload.text.length > 0);
});
