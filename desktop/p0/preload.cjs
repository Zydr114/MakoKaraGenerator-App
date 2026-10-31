'use strict';
const { ipcRenderer, sharedTexture } = require('electron');
let config;
let detached = false;
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const nextPaint = () => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));

window.addEventListener('DOMContentLoaded', async () => {
  try {
    config = await ipcRenderer.invoke('p0:config');
    if (typeof sharedTexture?.setSharedTextureReceiver !== 'function') throw new Error('sandbox sharedTexture receiver unavailable');
    const canvas = document.getElementById('preview');
    canvas.width = config.width;
    canvas.height = config.height;
    const context = canvas.getContext('2d', { alpha: true });
    sharedTexture.setSharedTextureReceiver(async ({ importedSharedTexture }, metadata) => {
      let frame;
      try {
        frame = importedSharedTexture.getVideoFrame();
        if (!detached) {
          context.clearRect(0, 0, canvas.width, canvas.height);
          context.drawImage(frame, 0, 0);
          document.getElementById('state').textContent = ` frame ${metadata.frameSeq} / slot ${metadata.slotId}`;
          const observation = { frameSeq: metadata.frameSeq, timestamp: frame.timestamp, width: frame.displayWidth, height: frame.displayHeight };
          // Readback is strictly limited to the separate correctness run.
          if (config.mode === 'correctness') {
            const sample = (x, y) => [...context.getImageData(Math.floor(x*canvas.width), Math.floor(y*canvas.height), 1, 1).data];
            const checks = [
              { name: 'top-left-red', x:.05, y:.15, expected:[255,0,0,255] },
              { name: 'top-right-green', x:.95, y:.15, expected:[0,255,0,255] },
              { name: 'bottom-left-blue', x:.05, y:.95, expected:[0,0,255,255] },
              { name: 'bottom-right-white', x:.95, y:.95, expected:[255,255,255,255] },
              { name: 'premultiplied-alpha', x:.375, y:.8, expected:[255,0,255,128] },
              { name: 'transparent', x:.625, y:.8, expected:[0,0,0,0] },
              ...Array.from({length:8}, (_, i) => ({ name:`gray-${i}`, x:(i+.5)/8, y:.5, expected:[Math.round(i/7*255),Math.round(i/7*255),Math.round(i/7*255),255] })),
            ];
            observation.samples = checks.map(check => ({ ...check, rgba:sample(check.x,check.y) }));
            observation.pixelChecksPassed = observation.samples.every(check => check.rgba.every((v,i) => Math.abs(v-check.expected[i]) <= 2));
            observation.decodedSequence = Array.from({length:16},(_,i)=>sample((i+.5)/16,.02)[0]>127 ? 2**i:0).reduce((a,b)=>a+b,0);
            if (!observation.pixelChecksPassed || observation.decodedSequence !== metadata.frameSeq || frame.timestamp !== metadata.frameSeq) throw new Error(`Correctness mismatch: ${JSON.stringify(observation)}`);
          }
          ipcRenderer.send('p0:drawn', observation);
          // Hidden documents do not advance rAF. Their draw still consumes VideoFrame.
          if (config.mode === 'correctness' && config.scenario !== 'hidden') await nextPaint();
          if (config.scenario === 'slow') await delay(config.slowMs);
        }
      } catch (error) {
        ipcRenderer.send('p0:error', String(error.stack || error));
      } finally {
        frame?.close();
        importedSharedTexture.release();
        ipcRenderer.send('p0:renderer-release', { frameSeq: metadata.frameSeq });
      }
    });
    ipcRenderer.on('p0:detach', () => { detached = true; });
    ipcRenderer.send('p0:ready', { sandboxed: process.sandboxed, contextIsolated: process.contextIsolated, versions: process.versions });
  } catch (error) { ipcRenderer.send('p0:error', String(error.stack || error)); }
});
