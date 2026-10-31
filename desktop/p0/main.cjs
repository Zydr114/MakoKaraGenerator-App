'use strict';
const { app, BrowserWindow, ipcMain, sharedTexture } = require('electron');
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { importAndTransferFrame } = require('../gpu-preview/import-consumer.cjs');

const configPath = process.argv[process.argv.indexOf('--p0-config') + 1];
const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
if (process.versions.electron !== '45.2.1') throw new Error('P0 requires Electron 45.2.1');
app.setPath('userData', path.join(config.runDir, 'profile'));
app.setPath('crashDumps', path.join(config.runDir, 'crashes'));
app.commandLine.appendSwitch('disable-breakpad');
const started = process.hrtime.bigint();
const report = { config, versions: process.versions, mainPid: process.pid, status: 'running', events: [], boundaries: { cpuReadback: config.mode === 'correctness', chromiumInternalCopies: 'unmeasured', acquireSync: 'producer queue idle + foreign ownership release; correctness baseline only' } };
const elapsed = () => Number(process.hrtime.bigint() - started) / 1e6;
const record = (type, data = {}) => {
  const event = { ms: elapsed(), ...data, type };
  report.events.push(event);
  console.log(JSON.stringify(event));
};
const fdSnapshot = pid => {
  try {
    const dir = `/proc/${pid}/fd`;
    const links = fs.readdirSync(dir).flatMap(fd => { try { return [fs.readlinkSync(path.join(dir, fd))]; } catch { return []; } });
    return { pid, count: links.length, drm: links.filter(x => x.startsWith('/dev/dri/')), dmaBuf: links.filter(x => /dmabuf|dma_buf/.test(x)).length };
  } catch { return { pid, unavailable: true }; }
};
const processIdentity = pid => {
  try { const s=fs.readFileSync(`/proc/${pid}/stat`,'utf8'); const rest=s.slice(s.lastIndexOf(')')+2).split(' '); return { startTime:rest[19], state:rest[0] }; }
  catch { return null; }
};
let window, broker, connection, producer, poll, deadline, finishing = false, acknowledged = 0;
let nextSequence = 1;
const imports = new Map();
const slotEpochs = new Map();
let producerIdentity;
let scenarioTriggered = false;
let screenshotPending;
let terminalPacket, producerExit;
let stopRequested = false;
let gpuLost = false;
let consumerLost = false;
const drawn = new Set();
const released = new Set();
function maybeComplete() {
  const count = stopRequested ? terminalPacket?.frames : config.frames;
  if (terminalPacket && producerExit?.code === 0 && acknowledged === count && (stopRequested || drawn.size === count) && imports.size === 0) {
    void finish('passed', stopRequested ? 'cooperative stop: every published frame final-released; producer stopped and exited zero' : 'finite frame identity/canvas draw/final GPU release verified; producer finished and exited zero');
  }
}
function requestStop(reason) {
  if (stopRequested || !connection || finishing) return;
  stopRequested = true;
  record('stop-request', { reason });
  broker.send(connection, JSON.stringify({ type: 'stop' }));
}

async function finish(status, reason) {
  if (finishing) return;
  finishing = true;
  clearInterval(poll);
  clearTimeout(deadline);
  report.status = status;
  report.reason = reason;
  report.acknowledged = acknowledged;
  report.outstandingAtStop = [...imports.keys()];
  report.unknownGpuCompletion = consumerLost || gpuLost;
  report.fdAtStop = fdSnapshot(process.pid);
  // Unknown references cause a disconnect and producer context teardown, never a slot ACK.
  if (connection) { try { broker.close(connection); } catch {} connection = null; }
  for (const item of imports.values()) {
    if (!item.mainReleased) { item.mainReleased = true; try { item.imported.release(); } catch {} }
  }
  if (screenshotPending) {
    try { await screenshotPending; }
    catch (error) { report.status = status = 'failed'; report.reason = reason = `Correctness screenshot failed: ${error.message}`; }
  }
  if (window && !window.isDestroyed()) window.destroy();
  if (producer && producer.exitCode === null) {
    await new Promise(resolve => {
      const killTimer = setTimeout(() => { if (producer.exitCode === null) producer.kill('SIGTERM'); }, 1000);
      const stopTimer = setTimeout(() => { if (producer.exitCode === null) producer.kill('SIGKILL'); }, 3000);
      producer.once('exit', () => { clearTimeout(killTimer); clearTimeout(stopTimer); resolve(); });
    });
  }
  report.fdAfterProducerExit = fdSnapshot(process.pid);
  fs.writeFileSync(path.join(config.runDir, 'result.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify({ type: 'result', status, reason, acknowledged, runDir: config.runDir }));
  app.exit(status === 'passed' ? 0 : 1);
}
const fail = error => { record('error', { error: String(error.stack || error) }); void finish('failed', String(error.message || error)); };
process.on('uncaughtException', fail);
process.on('unhandledRejection', fail);
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => void finish('interrupted', signal));
app.on('window-all-closed', () => { if (!finishing && !stopRequested) void finish('failed', 'window closed before final release'); });
app.on('child-process-gone', (_, details) => {
  record('child-process-gone', { ...details, processType:details.type });
  if (details.type === 'GPU' && !finishing) {
    gpuLost = true;
    consumerLost = true;
    void finish('failed', 'GPU exited: completion is unknown; no final-release ACK or slot reuse is inferred');
  }
});

function validate(packet) {
  const m = packet.metadata;
  if (!m || m.type !== 'frame' || m.protocolVersion !== 1) throw new Error('Invalid frame packet');
  if (m.producerPid !== packet.peerPid || m.producerPid !== producer.pid) throw new Error('producer pid does not match owned child and SO_PEERCRED');
  if (typeof m.producerEpoch !== 'string' || !/^\d+$/.test(m.producerEpoch) || m.poolGeneration !== 1) throw new Error('Invalid producer generation');
  const chromiumDevice = report.gpu.gpuDevice.find(x => x.active && x.vendorId === m.gpu.vendorId && x.deviceId === m.gpu.deviceId);
  const gpuProcess = report.processes.find(x => x.type === 'GPU');
  const matchedNode = gpuProcess?.fd.drm.some(node => {
    const dev = fs.statSync(node, { bigint: true }).rdev;
    const major = Number(((dev >> 8n) & 0xfffn) | ((dev >> 32n) & ~0xfffn));
    const minor = Number((dev & 0xffn) | ((dev >> 12n) & ~0xffn));
    return major === m.gpu.drmRenderMajor && minor === m.gpu.drmRenderMinor;
  });
  if (!chromiumDevice || !matchedNode) throw new Error('Producer physical GPU does not match actual Chromium GPU render node and PCI identity');
  if (!report.deviceMatch) { report.deviceMatch = { vendorId:m.gpu.vendorId, deviceId:m.gpu.deviceId, renderMajor:m.gpu.drmRenderMajor, renderMinor:m.gpu.drmRenderMinor, chromiumGpuPid:gpuProcess.pid }; record('device-match', report.deviceMatch); }
  const identity = `${m.producerPid}/${m.producerEpoch}/${m.poolGeneration}`;
  if (producerIdentity && producerIdentity !== identity) throw new Error('Producer identity changed');
  producerIdentity = identity;
  if (m.frameSeq !== nextSequence || !Number.isSafeInteger(m.slotId) || m.slotId < 0 || m.slotId >= config.slots || !Number.isSafeInteger(m.slotEpoch) || m.slotEpoch !== (slotEpochs.get(m.slotId) ?? 0)+1) throw new Error('Stale frame/slot identity');
  if ([...imports.values()].some(i => i.metadata.slotId === m.slotId)) throw new Error('Producer reused an outstanding slot');
  if (m.width !== config.width || m.height !== config.height || m.format !== 'BGRA_8888' || m.fourcc !== 'AR24' || m.origin !== 'top-left' || m.alphaMode !== 'premultiplied' || m.colorSpace !== 'srgb' || m.sync !== 'queue-idle-foreign-release') throw new Error('Unsupported P0 texture contract');
  if (m.provider !== (config.drawPlugin ? 'skia-plugin' : 'vulkan-clear') || m.counters?.cpuPixelReadbacks !== 0 || m.counters?.gpuBlits !== m.frameSeq * config.gpuCopiesPerFrame) throw new Error('Producer copy/readback counters do not match explicitly selected P0 mode');
  if (typeof m.modifier !== 'string' || !/^\d+$/.test(m.modifier) || BigInt(m.modifier) > 0xffffffffffffffffn || !Number.isSafeInteger(m.allocationSize) || m.allocationSize < 1 || m.allocationSize > 1024**3 || !Array.isArray(m.planes) || m.planes.length !== 1 || packet.fds.length !== 1) throw new Error('Invalid native pixmap descriptor');
  for (const p of m.planes) {
    if (p.fdIndex !== 0 || ![p.stride,p.offset,p.size].every(Number.isSafeInteger) || p.stride < m.width*4 || p.offset < 0 || p.size < p.offset + p.stride*m.height || p.size > m.allocationSize) throw new Error('Invalid plane bounds');
  }
  slotEpochs.set(m.slotId, m.slotEpoch);
  nextSequence++;
  return m;
}

async function consume(packet) {
  return importAndTransferFrame({
    packet, broker, sharedTexture, validate,
    timestamp: metadata => metadata.frameSeq,
    targetFrame: () => window.webContents.mainFrame,
    onImported: item => {
      imports.set(item.metadata.frameSeq,item);
      record('imported', { metadata:item.metadata, outstanding:imports.size, fd:fdSnapshot(process.pid) });
    },
    onAllReferencesReleased: metadata => {
      record('all-references-released', { frameSeq:metadata.frameSeq, slotId:metadata.slotId, slotEpoch:metadata.slotEpoch });
      imports.delete(metadata.frameSeq);
      if (finishing || !connection || consumerLost) {
        if (consumerLost) record('release-ack-suppressed-unknown-completion', { frameSeq:metadata.frameSeq });
        return;
      }
      try {
        if (gpuLost) throw new Error('GPU completion unknown after GPU process loss');
        if ((!drawn.has(metadata.frameSeq) && !stopRequested) || released.has(metadata.frameSeq)) throw new Error('Missing canvas draw or duplicate final release');
        released.add(metadata.frameSeq);
        broker.send(connection,JSON.stringify({ type:'release', frameSeq:metadata.frameSeq, slotId:metadata.slotId, slotEpoch:metadata.slotEpoch }));
        acknowledged++;
        record('producer-ack', { frameSeq:metadata.frameSeq, acknowledged, fd:fdSnapshot(process.pid) });
        maybeComplete();
      } catch (error) { fail(error); }
    },
    shouldTransfer: () => {
      if (config.scenario === 'no-consumer') requestStop('no consumer attached');
      return !stopRequested;
    },
    onTransferred: metadata => record('transferred',{frameSeq:metadata.frameSeq}),
    onDrained: metadata => record('drained-without-transfer',{frameSeq:metadata.frameSeq}),
    onTransferError: (error,metadata) => {
      if (!(stopRequested && config.scenario === 'renderer-crash')) return false;
      record('transfer-cancelled-by-renderer-exit',{frameSeq:metadata.frameSeq,error:String(error.message || error)});
      return true;
    },
    onMainReleased: metadata => record('main-release',{frameSeq:metadata.frameSeq}),
  });
}

async function startProducer() {
  const native = path.join(config.root, 'native/gpu-preview/build');
  broker = require(path.join(native, 'gpu_preview_broker.node'));
  const socket = path.join(config.runDir, 'producer.sock');
  const args = ['--socket', socket, '--width', String(config.width), '--height', String(config.height), '--slots', String(config.slots), '--frames', String(config.frames)];
  if (config.drawPlugin) args.push('--draw-plugin', config.drawPlugin);
  producer = spawn(path.join(native, 'gpu-preview-producer'), args, { cwd: config.root, stdio: ['ignore', 'pipe', 'pipe'] });
  producer.stdout.on('data', data => { fs.appendFileSync(path.join(config.runDir, 'producer.log'), data); process.stdout.write(data); });
  producer.stderr.on('data', data => { fs.appendFileSync(path.join(config.runDir, 'producer.log'), data); process.stderr.write(data); });
  producer.on('error', fail);
  producer.on('exit', (code, signal) => {
    record('producer-exit', { code, signal });
    producerExit = { code, signal };
    if (!finishing && code !== 0) void finish('failed', 'producer exited before finite release completion');
    maybeComplete();
  });
  const waitStart = elapsed();
  while (!fs.existsSync(socket)) {
    if (finishing || elapsed() - waitStart > 5000) throw new Error('Producer socket not ready');
    await new Promise(resolve => setTimeout(resolve, 20));
  }
  connection = broker.connect(socket);
  report.fdConnected = fdSnapshot(process.pid);
  poll = setInterval(() => {
    if (finishing) return;
    try {
      const packet = broker.receive(connection);
      if (!packet) return;
      if (packet.metadata?.type !== 'frame') {
        for (const fd of packet.fds) broker.closeFd(fd);
        record('producer-message', { metadata: packet.metadata });
        const m = packet.metadata;
        if (m.type !== (stopRequested ? 'stopped' : 'finished') || (!stopRequested && m.frames !== config.frames) || !Number.isSafeInteger(m.frames) || m.frames !== nextSequence-1 || m.slots !== config.slots || m.cpuPixelReadbacks !== 0 || m.gpuBlits !== m.frames * config.gpuCopiesPerFrame || acknowledged !== m.frames) throw new Error('Invalid producer terminal packet');
        terminalPacket = m;
        clearInterval(poll);
        maybeComplete();
        return;
      }
      if (imports.size >= config.slots) { for (const fd of packet.fds) broker.closeFd(fd); throw new Error('Texture pool exceeds fixed slot bound'); }
      void consume(packet).catch(fail);
    } catch (error) { fail(error); }
  }, 4);
}

ipcMain.handle('mako-gpu:config', event => {
  if (event.sender !== window.webContents) throw new Error('Unexpected renderer');
  return { mode: config.mode, scenario: config.scenario, width: config.width, height: config.height, slowMs: config.slowMs };
});
ipcMain.on('mako-gpu:error', (event, error) => { if (event.sender === window.webContents) fail(new Error(error)); });
ipcMain.on('mako-gpu:renderer-release', (event, data) => { if (event.sender === window.webContents) record('renderer-release-requested', data); });
ipcMain.on('mako-gpu:drawn', async (event, data) => {
  if (event.sender !== window.webContents) return;
  if (!imports.has(data.frameSeq) || drawn.has(data.frameSeq) || data.timestamp !== data.frameSeq || data.width !== config.width || data.height !== config.height) return fail(new Error('Invalid canvas frame identity'));
  drawn.add(data.frameSeq);
  record('drawn', data);
  if (config.mode === 'correctness' && data.frameSeq === 1) {
    screenshotPending = (async () => {
      await new Promise(resolve => setTimeout(resolve, 100));
      fs.writeFileSync(path.join(config.runDir, 'visible-frame.png'), (await window.webContents.capturePage()).toPNG());
      record('correctness-screenshot');
    })();
  }
  if (scenarioTriggered || data.frameSeq !== 1 || finishing) return;
  scenarioTriggered = true;
  if (config.scenario === 'hidden') window.hide();
  if (config.scenario === 'detach') { requestStop('active detach'); window.webContents.send('mako-gpu:detach'); }
  if (config.scenario === 'close') window.close();
  if (config.scenario === 'renderer-crash') {
    requestStop('renderer crash');
    const rendererPid = window.webContents.getOSProcessId();
    if (!rendererPid) return fail(new Error('No owned renderer PID found'));
    record('renderer-kill', { pid:rendererPid });
    consumerLost = true;
    process.kill(rendererPid, 'SIGKILL');
  }
  if (config.scenario === 'gpu-crash') {
    const gpu = app.getAppMetrics().find(x => x.type === 'GPU');
    if (!gpu) fail(new Error('No owned GPU child process found'));
    else { requestStop('GPU crash'); gpuLost = true; consumerLost = true; process.kill(gpu.pid, 'SIGKILL'); }
  }
});
ipcMain.once('mako-gpu:ready', async (event, data) => {
  if (event.sender !== window.webContents) return;
  record('renderer-ready', data);
  if (!data.sandboxed || !data.contextIsolated) return fail(new Error('Sandbox/context isolation required'));
  // Wait until Chromium has initialized an actual compositor before recording identity.
  await new Promise(resolve => setTimeout(resolve, 500));
  try {
    report.gpu = await app.getGPUInfo('complete');
    report.gpuFeatures = app.getGPUFeatureStatus();
    report.processes = app.getAppMetrics().map(x => ({ pid: x.pid, type: x.type, identity:processIdentity(x.pid), fd: fdSnapshot(x.pid) }));
    record('gpu-identity', { gpu: report.gpu, features: report.gpuFeatures, processes: report.processes });
    if (config.mode === 'probe') return void finish('passed', 'visible sandbox + sharedTexture API probe only; no dma-buf imported');
    await startProducer();
  } catch (error) { fail(error); }
});

app.whenReady().then(async () => {
  report.fdBaseline = fdSnapshot(process.pid);
  window = new BrowserWindow({ width: Math.min(config.width, 1360), height: Math.min(config.height + 60, 850), show: true, backgroundColor: '#20232a', webPreferences: { preload: path.join(__dirname, '../gpu-preview/preload.cjs'), sandbox: true, contextIsolation: true, nodeIntegration: false, backgroundThrottling: false } });
  window.on('close', event => { if (!finishing && connection) { event.preventDefault(); requestStop('visible window close'); } });
  window.webContents.on('render-process-gone', (_, details) => {
    record('renderer-gone', details);
    if (!finishing) {
      consumerLost = true;
      // Electron's crash monitor can only retain the acquire token. Its final
      // callback does not prove the dead consumer's final GPU use completed.
      // Disconnect without ACK, stop the producer, and exit this whole instance.
      void finish('failed', 'expected fail-stop: renderer exited; GPU completion unknown; entire Electron instance must exit');
    }
  });
  deadline = setTimeout(() => void finish('failed', 'finite P0 deadline exceeded; no inferred release'), config.deadlineMs);
  await window.loadFile(path.join(__dirname, 'index.html'));
}).catch(fail);
