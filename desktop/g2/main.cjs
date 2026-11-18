'use strict';

const { app, BrowserWindow, MessageChannelMain, ipcMain } = require('electron');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const readline = require('node:readline');
const v8 = require('node:v8');
const { spawn } = require('node:child_process');
const { connectLocalStream } = require('../gpu-preview/local-stream-client.cjs');
const { createShmStreamConsumer } = require('../gpu-preview/shm-consumer.cjs');
const { validateWorkerMessage } = require('../gpu-preview/wire.cjs');

const configPath = process.argv[process.argv.indexOf('--g2-config') + 1];
const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
if (process.versions.electron !== '45.2.1') {
  throw new Error('G2 requires pinned Electron 45.2.1');
}

app.setPath('userData', path.join(config.runDir, 'profile'));
app.setPath('crashDumps', path.join(config.runDir, 'crashes'));
app.commandLine.appendSwitch('disable-breakpad');
app.commandLine.appendSwitch('js-flags', '--expose-gc');

const shmBridge = require(config.shmAddon);

function readProcFdCount(pid) {
  if (!pid || process.platform !== 'linux') return null;
  try {
    return fs.readdirSync(`/proc/${pid}/fd`).length;
  } catch {
    return null;
  }
}

function readProcFdLinks(pid) {
  if (!pid || process.platform !== 'linux') return [];
  try {
    return fs.readdirSync(`/proc/${pid}/fd`).map((fd) => {
      try {
        return `${fd}->${fs.readlinkSync(`/proc/${pid}/fd/${fd}`)}`;
      } catch {
        return `${fd}->(closed)`;
      }
    });
  } catch {
    return [];
  }
}

function readProcVmRssKb(pid) {
  if (!pid || process.platform !== 'linux') return null;
  try {
    const status = fs.readFileSync(`/proc/${pid}/status`, 'utf8');
    const match = /^VmRSS:\s+(\d+)\s+kB$/m.exec(status);
    return match ? Number(match[1]) : null;
  } catch {
    return null;
  }
}

function countResidualShmFiles() {
  if (process.platform !== 'linux' || !fs.existsSync('/dev/shm')) return 0;
  return fs.readdirSync('/dev/shm').filter((name) => name.startsWith('mkg-shm-')).length;
}

const delay = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

let win = null;
let workerProc = null;
let workerPid = null;
let finishing = false;
const sockDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mkg-g2-'));
fs.chmodSync(sockDir, 0o700);
const drawnObservations = [];
let preloadReadyResolve = null;
const preloadReadyPromise = new Promise((resolve) => { preloadReadyResolve = resolve; });
let fatalRendererError = null;

let preloadModeConfig = {
  mode: config.suite === 'correctness' ? 'correctness' : 'finite',
  transport: 'shm',
  scenario: config.suite === 'slow-backpressure' ? 'slow' : (config.suite === 'correctness' ? 'hidden' : 'normal'),
  slowMs: config.slowMs ?? 55,
  width: config.width ?? 1280,
  height: config.height ?? 720,
};

ipcMain.handle('mako-gpu:config', (event) => {
  if (!win || event.sender !== win.webContents) throw new Error('Unexpected renderer');
  return preloadModeConfig;
});
ipcMain.handle('mako-gpu:clock', () => ({ mainMonotonicNs: process.hrtime.bigint().toString() }));
ipcMain.on('mako-gpu:ready', (_event, info) => preloadReadyResolve?.(info));
ipcMain.on('mako-gpu:drawn', (_event, obs) => drawnObservations.push(obs));
ipcMain.on('mako-gpu:renderer-release', () => {});
ipcMain.on('mako-gpu:error', (_event, err) => {
  fatalRendererError = String(err);
});

async function spawnWorkerProducer(backendApi) {
  const child = spawn(config.workerBinary, ['--shm-g2-producer'], {
    cwd: config.root,
    env: {
      ...process.env,
      MAKO_SHM_G2_BENCHMARK: '1',
      MAKO_RENDER_BACKEND: backendApi,
      MAKO_PREVIEW_TRANSPORT: 'shm',
      MAKO_G2_MAIN_PID: String(process.pid),
    },
    stdio: ['pipe', 'pipe', 'pipe'],
  });
  workerProc = child;
  workerPid = child.pid;

  const stderrLog = fs.createWriteStream(path.join(config.runDir, `worker-${backendApi}.stderr.log`));
  child.stderr.on('data', (chunk) => stderrLog.write(chunk));

  const events = [];
  let waiter = null;
  const rl = readline.createInterface({ input: child.stdout });
  rl.on('line', (line) => {
    if (!line.startsWith('G2_EVENT:')) return;
    const parsed = JSON.parse(line.slice('G2_EVENT:'.length));
    if (waiter && waiter.predicate(parsed)) {
      const w = waiter;
      waiter = null;
      w.resolve(parsed);
    } else {
      events.push(parsed);
    }
  });

  function waitEvent(type, timeoutMs = 15000) {
    const idx = events.findIndex((e) => e.type === type);
    if (idx >= 0) return Promise.resolve(events.splice(idx, 1)[0]);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        waiter = null;
        reject(new Error(`Timed out waiting for worker event '${type}'`));
      }, timeoutMs);
      waiter = {
        predicate: (e) => e.type === type,
        resolve: (val) => {
          clearTimeout(timer);
          resolve(val);
        },
      };
    });
  }

  function sendCmd(obj) {
    child.stdin.write(JSON.stringify(obj) + '\n');
  }

  const ready = await waitEvent('ready', 15000);
  return {
    pid: child.pid,
    ready,
    sendCmd,
    waitEvent,
    async shutdown() {
      if (child.exitCode !== null) return child.exitCode;
      try { sendCmd({ cmd: 'shutdown' }); } catch {}
      try { child.stdin.end(); } catch {}
      return await new Promise((resolve) => {
        const killTimer = setTimeout(() => {
          if (child.exitCode === null) {
            try { process.kill(child.pid, 'SIGTERM'); } catch {}
          }
        }, 3000);
        child.once('exit', (code) => {
          clearTimeout(killTimer);
          stderrLog.end();
          resolve(code);
        });
      });
    },
  };
}

async function queryRendererMemory() {
  if (!win || win.isDestroyed()) return null;
  try {
    return await win.webContents.executeJavaScript(
      '(function(){ if (typeof gc === "function") gc(); const m = performance.memory; return m ? { usedJSHeapSize: m.usedJSHeapSize, totalJSHeapSize: m.totalJSHeapSize } : null; })()',
      true
    );
  } catch {
    return null;
  }
}

async function sampleProcessMetrics(rendererPid, producerPid, label, frameIndex) {
  if (typeof global.gc === 'function') global.gc();
  const mainHeap = v8.getHeapStatistics();
  const rendererJs = await queryRendererMemory();
  const appMetrics = app.getAppMetrics();
  const mainMetric = appMetrics.find((m) => m.pid === process.pid);
  const rendererMetric = appMetrics.find((m) => m.pid === rendererPid);
  return {
    label,
    frameIndex,
    fds: {
      main: readProcFdCount(process.pid),
      renderer: readProcFdCount(rendererPid),
      worker: readProcFdCount(producerPid),
    },
    rssKb: {
      main: readProcVmRssKb(process.pid) ?? mainMetric?.memory?.workingSetSize ?? null,
      renderer: readProcVmRssKb(rendererPid) ?? rendererMetric?.memory?.workingSetSize ?? null,
      worker: readProcVmRssKb(producerPid),
    },
    mainV8ExternalBytes: mainHeap.external_memory,
    mainV8UsedHeapBytes: mainHeap.used_heap_size,
    rendererJsHeap: rendererJs,
    devShmResidualCount: countResidualShmFiles(),
  };
}

async function runSingleStreamGeneration(worker, params) {
  const {
    streamId,
    hostInstanceId = 'g2-host',
    consumerId = 'g2-consumer',
    inputGeneration,
    attachSeq,
    width,
    height,
    pixelFormat,
    strideBytes = 0,
    targetFrames,
    targetFps = 30,
    onProgressFrame,
  } = params;

  const brokerPath = path.join(sockDir, `s-${attachSeq}-${crypto.randomBytes(4).toString('hex')}.sock`);
  const handshake = crypto.randomBytes(32).toString('hex');

  worker.sendCmd({
    cmd: 'open',
    streamId,
    hostInstanceId,
    inputGeneration: String(inputGeneration),
    width,
    height,
    pixelFormat,
    strideBytes,
    brokerPath,
    handshake,
  });

  const { opened } = await worker.waitEvent('opened', 15000);
  const client = await connectLocalStream(brokerPath, { timeoutMs: 5000, retryDelayMs: 10 });

  const streamState = {
    streamId,
    producerEpoch: opened.producerEpoch,
    expectedInputGeneration: String(inputGeneration),
    poolGeneration: opened.poolGeneration,
    helloNs: process.hrtime.bigint().toString(),
    size: { width, height },
  };
  const hostCfg = { hostInstanceId, consumerId };

  client.send({
    type: 'hello',
    protocolVersion: 1,
    consumerId,
    consumerEpoch: '1',
    streamId,
    hostInstanceId,
    producerEpoch: opened.producerEpoch,
    handshake,
    senderMonotonicNs: streamState.helloNs,
  });

  let shmConsumer = null;
  let calibrated = false;
  let completedFrames = 0;
  let workerPeakOutstanding = 0;
  const timingsSummary = { count: 0, maxRenderUs: 0, maxCaptureUs: 0, totalRenderUs: 0, totalCaptureUs: 0 };

  let resolveTargetFrames;
  let rejectTargetFrames;
  const targetDonePromise = new Promise((resolve, reject) => {
    resolveTargetFrames = resolve;
    rejectTargetFrames = reject;
  });

  const { port1, port2 } = new MessageChannelMain();

  let loopActive = true;
  const pump = (async () => {
    while (loopActive) {
      const pkt = await client.receive();
      if (!pkt) break;
      const m = pkt.metadata;
      validateWorkerMessage(m, streamState, hostCfg);

      if (m.type === 'hello-accepted') {
        if (m.transport !== 'shm') throw new Error(`Expected shm transport, got ${m.transport}`);
        shmConsumer = createShmStreamConsumer({
          shmBridge,
          shmName: m.shmName,
          allocationBytes: m.allocationBytes,
          size: { width, height },
          strideBytes: m.strideBytes,
          pixelFormat: m.pixelFormat,
          streamIdentity: { streamId },
          hostConfig: {
            hostInstanceId,
            producerEpoch: opened.producerEpoch,
            inputGeneration: m.inputGeneration,
            poolGeneration: m.poolGeneration,
          },
          port: port1,
          shouldTransfer: () => !fatalRendererError && completedFrames < targetFrames,
          sendRelease: (rel) => {
            if (!client.isClosed()) client.send(rel);
          },
          onFrameCompleted: ({ metadata: meta, drawn, timings, counters }) => {
            if (fatalRendererError) {
              rejectTargetFrames(new Error(fatalRendererError));
              return;
            }
            if (counters?.cpuPixelReadbacks !== 1) {
              rejectTargetFrames(new Error(`Expected cpuPixelReadbacks===1, got ${JSON.stringify(counters)}`));
              return;
            }
            if (meta.diagnostics !== null) {
              rejectTargetFrames(new Error(`Expected pure null diagnostics on synthetic frame, got ${JSON.stringify(meta.diagnostics)}`));
              return;
            }
            if (drawn) {
              completedFrames++;
              timingsSummary.count++;
              timingsSummary.totalRenderUs += timings.renderDurationUs;
              timingsSummary.totalCaptureUs += timings.portCaptureDurationUs;
              timingsSummary.maxRenderUs = Math.max(timingsSummary.maxRenderUs, timings.renderDurationUs);
              timingsSummary.maxCaptureUs = Math.max(timingsSummary.maxCaptureUs, timings.portCaptureDurationUs);
              onProgressFrame?.(completedFrames);
              if (completedFrames >= targetFrames) {
                resolveTargetFrames();
              }
            }
          },
          onError: (err) => rejectTargetFrames(err),
        });

        // Verify immediate unlink on openReadOnly: exact shmName no longer exists on /dev/shm!
        if (fs.existsSync(m.shmName)) {
          throw new Error(`/dev/shm file ${m.shmName} still exists after openReadOnly!`);
        }

        win.webContents.postMessage('mako-gpu:shm-port', {
          protocolVersion: 1,
          streamId,
          hostInstanceId,
          producerEpoch: opened.producerEpoch,
          inputGeneration: m.inputGeneration,
          consumerEpoch: '1',
          poolGeneration: m.poolGeneration,
          attachSeq,
        }, [port2]);
      } else if (m.type === 'clock-ping') {
        const nowNs = process.hrtime.bigint().toString();
        client.send({
          type: 'clock-pong',
          echoWorkerMonotonicNs: m.workerMonotonicNs,
          senderReceiveMonotonicNs: nowNs,
          senderMonotonicNs: process.hrtime.bigint().toString(),
        });
      } else if (m.type === 'clock-calibrated') {
        calibrated = true;
        client.send({
          type: 'set-playback-state',
          protocolVersion: 1,
          streamId,
          hostInstanceId,
          producerEpoch: opened.producerEpoch,
          inputGeneration: String(inputGeneration),
          consumerEpoch: '1',
          controlSeq: 1,
          mode: 'playing',
          mediaTimeUs: 0,
          rate: 1.0,
          targetFps,
          timelineResetId: `reset-${attachSeq}`,
          clockSample: {
            mediaTimeUs: 0,
            senderMonotonicNs: process.hrtime.bigint().toString(),
          },
        });
      } else if (m.type === 'control-applied') {
        win.webContents.send('mako-gpu:barrier', {
          protocolVersion: 1,
          streamId,
          hostInstanceId,
          producerEpoch: opened.producerEpoch,
          inputGeneration: String(inputGeneration),
          consumerEpoch: '1',
          poolGeneration: opened.poolGeneration,
          timelineEpoch: m.timelineEpoch,
          timelineResetId: m.timelineResetId,
          minControlSeq: 1,
          controlSeq: m.controlSeq,
        });
      } else if (m.type === 'frame') {
        shmConsumer.consumeFrame(m);
        if (completedFrames === 2 && calibrated) {
          client.send({
            type: 'read-state',
            protocolVersion: 1,
            streamId,
            hostInstanceId,
            producerEpoch: opened.producerEpoch,
            inputGeneration: String(inputGeneration),
            consumerEpoch: '1',
          });
        }
      } else if (m.type === 'state') {
        workerPeakOutstanding = Math.max(workerPeakOutstanding, m.peaks?.outstanding ?? m.outstanding ?? 0);
      } else if (m.type === 'stopped') {
        break;
      } else if (m.type === 'failed') {
        throw new Error(`Worker stream failed: ${JSON.stringify(m)}`);
      }
    }
  })();
  pump.catch((err) => rejectTargetFrames(err));

  await targetDonePromise;
  if (fatalRendererError) throw new Error(fatalRendererError);

  client.send({
    type: 'read-state',
    protocolVersion: 1,
    streamId,
    hostInstanceId,
    producerEpoch: opened.producerEpoch,
    inputGeneration: String(inputGeneration),
    consumerEpoch: '1',
  });
  await delay(15);

  worker.sendCmd({ cmd: 'close' });
  const closedEvent = await worker.waitEvent('closed', 15000);
  loopActive = false;
  win.webContents.send('mako-gpu:detach', { attachSeq });
  const consumerPeak = shmConsumer?.getPeakOutstanding() ?? 0;
  shmConsumer?.close();
  client.close();
  await pump.catch(() => {});

  if (!closedEvent.drained) {
    throw new Error('Worker reported stream not drained on close');
  }

  return {
    opened,
    completedFrames,
    consumerPeakOutstanding: consumerPeak,
    workerPeakOutstanding: Math.max(workerPeakOutstanding, consumerPeak),
    timingsSummary: {
      count: timingsSummary.count,
      avgRenderUs: Math.round(timingsSummary.totalRenderUs / Math.max(1, timingsSummary.count)),
      avgCaptureUs: Math.round(timingsSummary.totalCaptureUs / Math.max(1, timingsSummary.count)),
      maxRenderUs: timingsSummary.maxRenderUs,
      maxCaptureUs: timingsSummary.maxCaptureUs,
    },
  };
}

async function main() {
  const report = {
    suite: config.suite,
    backendApi: config.backendApi,
    electronVersion: process.versions.electron,
    mainPid: process.pid,
    status: 'running',
    metrics: [],
    results: [],
  };

  try {
    await app.whenReady();
    win = new BrowserWindow({
      width: 960,
      height: 600,
      show: false,
      webPreferences: {
        preload: path.join(config.root, 'desktop/gpu-preview/preload.cjs'),
        sandbox: true,
        contextIsolation: true,
        nodeIntegration: false,
        backgroundThrottling: false,
      },
    });

    await win.loadFile(path.join(__dirname, 'index.html'));
    const preloadInfo = await preloadReadyPromise;
    if (!preloadInfo.sandboxed || !preloadInfo.contextIsolated) {
      throw new Error('Preload must run with sandbox: true and contextIsolation: true');
    }
    report.preload = preloadInfo;

    const rendererPid = win.webContents.getOSProcessId();
    report.rendererPid = rendererPid;

    const worker = await spawnWorkerProducer(config.backendApi);
    report.workerPid = worker.pid;
    report.device = worker.ready.device;

    if (config.suite === 'correctness') {
      const cases = [
        { pixelFormat: 'bgra8888', strideBytes: 0, width: 1280, height: 720 },
        { pixelFormat: 'rgba8888', strideBytes: 1280 * 4 + 64, width: 1280, height: 720 },
      ];
      let attachSeq = 0;
      for (const c of cases) {
        drawnObservations.length = 0;
        attachSeq++;
        const res = await runSingleStreamGeneration(worker, {
          streamId: `g2-corr-${attachSeq}`,
          inputGeneration: attachSeq,
          attachSeq,
          width: c.width,
          height: c.height,
          pixelFormat: c.pixelFormat,
          strideBytes: c.strideBytes,
          targetFrames: 12,
          targetFps: 30,
        });
        const verified = drawnObservations.filter((o) => o.pixelChecksPassed === true);
        if (verified.length < 12) {
          throw new Error(`Expected >=12 pixel-verified frames for ${c.pixelFormat}, got ${verified.length}`);
        }
        report.results.push({
          pixelFormat: c.pixelFormat,
          strideBytes: res.opened.strideBytes,
          verifiedFrames: verified.length,
          sampleObservation: verified[verified.length - 1],
          timings: res.timingsSummary,
        });
      }
    } else if (config.suite === 'stress1000') {
      const pendingCheckpointTasks = [];
      const res = await runSingleStreamGeneration(worker, {
        streamId: 'g2-stress-1440p',
        inputGeneration: 1,
        attachSeq: 1,
        width: 2560,
        height: 1440,
        pixelFormat: 'bgra8888',
        strideBytes: 0,
        targetFrames: 1000,
        targetFps: 30,
        onProgressFrame: (n) => {
          if (n === 100 || n === 250 || n === 500 || n === 750 || n === 1000) {
            pendingCheckpointTasks.push(
              sampleProcessMetrics(rendererPid, worker.pid, `frame-${n}`, n).then((m) => report.metrics.push(m))
            );
          }
        },
      });
      await Promise.all(pendingCheckpointTasks);
      report.metrics.sort((a, b) => a.frameIndex - b.frameIndex);

      const warmup = report.metrics.find((m) => m.frameIndex === 100);
      const end = report.metrics.find((m) => m.frameIndex === 1000);
      const fdDeltaMain = end.fds.main - warmup.fds.main;
      const fdDeltaRenderer = end.fds.renderer - warmup.fds.renderer;
      const fdDeltaWorker = end.fds.worker - warmup.fds.worker;
      if (fdDeltaMain > 2 || fdDeltaRenderer > 2 || fdDeltaWorker > 2) {
        throw new Error(`FD growth detected across 1000 frames: main=${fdDeltaMain}, renderer=${fdDeltaRenderer}, worker=${fdDeltaWorker}`);
      }
      // 1000 frames @ 1440p transfers 14.75 GB; an unbounded leak would grow by hundreds/thousands of MB.
      const mainRssGrowthMb = (end.rssKb.main - warmup.rssKb.main) / 1024;
      const rendererRssGrowthMb = (end.rssKb.renderer - warmup.rssKb.renderer) / 1024;
      if (mainRssGrowthMb > 96 || rendererRssGrowthMb > 160) {
        throw new Error(`Excessive RSS growth across 14.75 GB transfer: main=+${mainRssGrowthMb.toFixed(1)}MB, renderer=+${rendererRssGrowthMb.toFixed(1)}MB`);
      }
      report.results.push({
        ...res,
        fdDeltaAfterWarmup: { main: fdDeltaMain, renderer: fdDeltaRenderer, worker: fdDeltaWorker },
        rssGrowthAfterWarmupMb: { main: Number(mainRssGrowthMb.toFixed(2)), renderer: Number(rendererRssGrowthMb.toFixed(2)) },
      });
    } else if (config.suite === 'resize30') {
      drawnObservations.length = 0;
      const coldBefore = await sampleProcessMetrics(rendererPid, worker.pid, 'cold-before-warmup', 0);
      report.metrics.push(coldBefore);

      // Generation 1 (720p) warms up .NET SocketAsyncEngine / Skia / Vulkan one-time handles;
      // Generations 2..31 perform 30 consecutive switches (720p <-> 1440p, bgra8888 / rgba8888) on the same <canvas>!
      const totalGenerations = 31;
      let afterWarmup = null;
      let warmupWorkerFds = [];
      for (let gen = 1; gen <= totalGenerations; gen++) {
        const is1440p = gen % 2 === 0;
        const width = is1440p ? 2560 : 1280;
        const height = is1440p ? 1440 : 720;
        const pixelFormat = gen % 3 === 0 ? 'rgba8888' : 'bgra8888';
        await runSingleStreamGeneration(worker, {
          streamId: 'g2-resize-stream',
          inputGeneration: gen,
          attachSeq: gen,
          width,
          height,
          pixelFormat,
          strideBytes: 0,
          targetFrames: 3,
          targetFps: 30,
        });
        if (gen === 1) {
          afterWarmup = await sampleProcessMetrics(rendererPid, worker.pid, 'after-gen1-warmup', 1);
          warmupWorkerFds = readProcFdLinks(worker.pid);
          report.metrics.push(afterWarmup);
        }
      }

      const after = await sampleProcessMetrics(rendererPid, worker.pid, 'after-30-resizes', totalGenerations);
      report.metrics.push(after);
      const lastObs = drawnObservations[drawnObservations.length - 1];
      if (!lastObs || lastObs.textureRecreations !== totalGenerations) {
        throw new Error(`Expected ${totalGenerations} immutable WebGL2 texture recreations, got ${lastObs?.textureRecreations}`);
      }
      if (after.devShmResidualCount !== 0) {
        throw new Error(`Residual /dev/shm files after 30 resizes: ${after.devShmResidualCount}`);
      }
      const fdDeltaMain = after.fds.main - afterWarmup.fds.main;
      const fdDeltaRenderer = after.fds.renderer - afterWarmup.fds.renderer;
      const fdDeltaWorker = after.fds.worker - afterWarmup.fds.worker;
      if (fdDeltaMain > 2 || fdDeltaRenderer > 2 || fdDeltaWorker > 2) {
        const afterWorkerFds = readProcFdLinks(worker.pid);
        throw new Error(
          `FD leak across 30 resizes: main=${fdDeltaMain}, renderer=${fdDeltaRenderer}, worker=${fdDeltaWorker}; ` +
          `warmupWorkerFds=${JSON.stringify(warmupWorkerFds)}, afterWorkerFds=${JSON.stringify(afterWorkerFds)}`
        );
      }
      report.results.push({
        switchesCompleted: 30,
        generationsRun: totalGenerations,
        textureRecreations: lastObs.textureRecreations,
        fdDeltaAfterWarmup: { main: fdDeltaMain, renderer: fdDeltaRenderer, worker: fdDeltaWorker },
        rssDeltaAfterWarmupMb: {
          main: Number(((after.rssKb.main - afterWarmup.rssKb.main) / 1024).toFixed(2)),
          renderer: Number(((after.rssKb.renderer - afterWarmup.rssKb.renderer) / 1024).toFixed(2)),
          worker: Number(((after.rssKb.worker - afterWarmup.rssKb.worker) / 1024).toFixed(2)),
        },
      });
    } else if (config.suite === 'slow-backpressure') {
      const res = await runSingleStreamGeneration(worker, {
        streamId: 'g2-slow-stream',
        inputGeneration: 1,
        attachSeq: 1,
        width: 1280,
        height: 720,
        pixelFormat: 'bgra8888',
        strideBytes: 0,
        targetFrames: 24,
        targetFps: 30,
      });
      if (res.consumerPeakOutstanding > 3 || res.workerPeakOutstanding > 3 || res.consumerPeakOutstanding < 2) {
        throw new Error(`3-slot backpressure violated or not exercised: consumerPeak=${res.consumerPeakOutstanding}, workerPeak=${res.workerPeakOutstanding}`);
      }
      report.results.push(res);
    } else {
      throw new Error(`Unknown G2 suite: ${config.suite}`);
    }

    const exitCode = await worker.shutdown();
    if (exitCode !== 0) {
      throw new Error(`Worker producer exited with non-zero code: ${exitCode}`);
    }
    report.workerExitCode = exitCode;
    report.finalDevShmResidualCount = countResidualShmFiles();
    if (report.finalDevShmResidualCount !== 0) {
      throw new Error(`Residual mkg-shm-* files in /dev/shm: ${report.finalDevShmResidualCount}`);
    }
    report.status = 'passed';
  } catch (err) {
    report.status = 'failed';
    report.error = String(err?.stack || err);
    console.error(report.error);
  } finally {
    finishing = true;
    if (workerProc && workerProc.exitCode === null) {
      try { process.kill(workerProc.pid, 'SIGTERM'); } catch {}
    }
    if (win && !win.isDestroyed()) {
      win.destroy();
    }
    try { fs.rmSync(sockDir, { recursive: true, force: true }); } catch {}
    fs.writeFileSync(path.join(config.runDir, 'result.json'), JSON.stringify(report, null, 2));
    app.exit(report.status === 'passed' ? 0 : 1);
  }
}

void main();
