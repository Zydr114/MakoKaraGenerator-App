'use strict';

const path = require('node:path');

const SPLASH_WIDTH = 540;
const SPLASH_HEIGHT = 320;
const SPLASH_BACKGROUND_COLOR = '#0C1220';

const STARTUP_STAGES = Object.freeze({
  preparing: Object.freeze({ text: '正在初始化运行环境…' }),
  validating: Object.freeze({ text: '正在校验运行环境与部署文件…' }),
  'web-server': Object.freeze({ text: '正在启动本地页面服务…' }),
  backend: Object.freeze({ text: '正在启动后端服务并初始化 Worker…' }),
  'gpu-and-window': Object.freeze({ text: '正在初始化主窗口与 GPU 环境…' }),
  'loading-ui': Object.freeze({ text: '正在加载编辑器界面…' }),
  ready: Object.freeze({ text: '即将就绪…' }),
});

function resolveStagePayload(stageOrPayload, version) {
  if (typeof stageOrPayload === 'string') {
    const preset = STARTUP_STAGES[stageOrPayload] ?? STARTUP_STAGES.preparing;
    return { stage: stageOrPayload, text: preset.text, version };
  }
  const preset = stageOrPayload?.stage ? (STARTUP_STAGES[stageOrPayload.stage] ?? STARTUP_STAGES.preparing) : STARTUP_STAGES.preparing;
  return {
    stage: stageOrPayload?.stage ?? 'preparing',
    text: typeof stageOrPayload?.text === 'string' && stageOrPayload.text ? stageOrPayload.text : preset.text,
    version: stageOrPayload?.version ?? version,
  };
}

function computeCenteredBounds(workArea, width = SPLASH_WIDTH, height = SPLASH_HEIGHT) {
  const areaX = Number.isFinite(workArea?.x) ? workArea.x : 0;
  const areaY = Number.isFinite(workArea?.y) ? workArea.y : 0;
  const areaW = Number.isFinite(workArea?.width) && workArea.width > 0 ? workArea.width : width;
  const areaH = Number.isFinite(workArea?.height) && workArea.height > 0 ? workArea.height : height;
  const finalW = Math.min(width, areaW);
  const finalH = Math.min(height, areaH);
  return {
    x: Math.round(areaX + (areaW - finalW) / 2),
    y: Math.round(areaY + (areaH - finalH) / 2),
    width: finalW,
    height: finalH,
  };
}

function createSplashController(options) {
  const {
    BrowserWindow,
    screen,
    version = '',
    iconPath,
    htmlPath = path.join(__dirname, 'splash.html'),
    preloadPath = path.join(__dirname, 'preload.cjs'),
    width = SPLASH_WIDTH,
    height = SPLASH_HEIGHT,
    onUserClose,
  } = options;

  let splashWindow = null;
  let loaded = false;
  let dismissedByApp = false;
  let abortedByUser = false;
  let handoffInProgress = false;
  let latestState = resolveStagePayload('preparing', version);

  function pushState() {
    if (!splashWindow || splashWindow.isDestroyed() || !loaded) return;
    try {
      splashWindow.webContents.send('mako-splash:state', latestState);
    } catch { /* ignore send errors during teardown */ }
  }

  function show(initialStage = 'preparing') {
    if (splashWindow && !splashWindow.isDestroyed()) return splashWindow;
    latestState = resolveStagePayload(initialStage, version);
    const workArea = screen.getPrimaryDisplay().workArea;
    const bounds = computeCenteredBounds(workArea, width, height);
    splashWindow = new BrowserWindow({
      ...bounds,
      title: 'MakoKara Generator',
      icon: iconPath,
      frame: false,
      transparent: false,
      backgroundColor: SPLASH_BACKGROUND_COLOR,
      resizable: false,
      minimizable: false,
      maximizable: false,
      fullscreenable: false,
      skipTaskbar: true,
      alwaysOnTop: false,
      show: false,
      webPreferences: {
        preload: preloadPath,
        sandbox: true,
        contextIsolation: true,
        nodeIntegration: false,
        backgroundThrottling: false,
      },
    });

    const wc = splashWindow.webContents;
    wc.setWindowOpenHandler(() => ({ action: 'deny' }));
    wc.on('will-navigate', (event) => event.preventDefault());
    wc.on('will-frame-navigate', (event) => event.preventDefault());
    wc.on('before-input-event', (event, input) => {
      if (input.key === 'F5' || ((input.control || input.meta) && String(input.key).toLowerCase() === 'r')) {
        event.preventDefault();
      }
    });
    wc.once('did-finish-load', () => {
      loaded = true;
      pushState();
    });

    splashWindow.once('ready-to-show', () => {
      if (!dismissedByApp && !abortedByUser && splashWindow && !splashWindow.isDestroyed()) {
        splashWindow.show();
      }
    });

    splashWindow.on('closed', () => {
      splashWindow = null;
      loaded = false;
      if (!dismissedByApp) {
        abortedByUser = true;
        onUserClose?.();
      }
    });

    void splashWindow.loadFile(htmlPath, {
      query: {
        v: String(latestState.version ?? ''),
        text: String(latestState.text ?? ''),
      },
    }).catch(() => {});

    return splashWindow;
  }

  function update(stageOrPayload) {
    latestState = resolveStagePayload(stageOrPayload, version);
    pushState();
    return latestState;
  }

  function focus() {
    if (!splashWindow || splashWindow.isDestroyed()) return false;
    if (splashWindow.isMinimized()) splashWindow.restore();
    if (!splashWindow.isVisible()) splashWindow.show();
    splashWindow.focus();
    return true;
  }

  function close() {
    dismissedByApp = true;
    const win = splashWindow;
    splashWindow = null;
    loaded = false;
    if (win && !win.isDestroyed()) {
      win.destroy();
    }
  }

  function handoffTo(mainWindow) {
    if (abortedByUser) return false;
    handoffInProgress = true;
    dismissedByApp = true;
    try {
      if (mainWindow && !mainWindow.isDestroyed()) {
        if (!mainWindow.isVisible()) mainWindow.show();
        mainWindow.focus();
      }
      const win = splashWindow;
      splashWindow = null;
      loaded = false;
      if (win && !win.isDestroyed()) {
        win.destroy();
      }
      return true;
    } finally {
      handoffInProgress = false;
    }
  }

  return {
    show,
    update,
    focus,
    close,
    handoffTo,
    isAborted: () => abortedByUser,
    isHandingOff: () => handoffInProgress,
    isVisible: () => Boolean(splashWindow && !splashWindow.isDestroyed()),
    getState: () => latestState,
  };
}

module.exports = {
  SPLASH_BACKGROUND_COLOR,
  SPLASH_HEIGHT,
  SPLASH_WIDTH,
  STARTUP_STAGES,
  computeCenteredBounds,
  createSplashController,
  resolveStagePayload,
};
