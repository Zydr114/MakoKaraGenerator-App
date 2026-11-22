// Finite real-browser integration check. No playback or throughput claims.
import assert from 'node:assert/strict';
import { mkdtemp, writeFile, rm, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { launchChrome } from '../frontend/scripts/cdp.mjs';
import { shell, ui, uiJs } from '../frontend/scripts/ui-query.mjs';
const url = process.argv[2] ?? 'http://127.0.0.1:5173';
const out = resolve(import.meta.dirname, '../.local/integration');
await mkdir(out, { recursive: true });
const assets = await mkdtemp(join(tmpdir(), 'mako-app-smoke-'));
const lrc = join(assets, 'App整合.lrc');
const wav = join(assets, 'App整合.wav');
await writeFile(lrc, '[00:00.50]朝の光浴びて\n[00:03.00]Hello karaoke\n');
const seconds = 6, rate = 22050, samples = seconds * rate;
const b = Buffer.alloc(44 + samples * 2);
b.write('RIFF'); b.writeUInt32LE(b.length - 8, 4); b.write('WAVEfmt ', 8); b.writeUInt32LE(16, 16); b.writeUInt16LE(1, 20); b.writeUInt16LE(1, 22); b.writeUInt32LE(rate, 24); b.writeUInt32LE(rate * 2, 28); b.writeUInt16LE(2, 32); b.writeUInt16LE(16, 34); b.write('data', 36); b.writeUInt32LE(samples * 2, 40);
for (let i = 0; i < samples; i++) b.writeInt16LE(Math.round(Math.sin(i * 2 * Math.PI * 330 / rate) * 6000), 44 + i * 2);
await writeFile(wav, b);
const browser = await launchChrome();
const errors = [];
let page;
// 界面改版后的查询（frontend/scripts/ui-query.mjs）：按角色和名称找控件。
const REVISION = `Number((document.querySelector('.status-bar')?.textContent.match(/修订 (\\d+)/) ?? [])[1])`;
const timeInputs = `[...document.querySelectorAll('.stage-footer .tb-time input')]`;
const startTime = `(${timeInputs}[0]?.value ?? null)`;
const playhead = `(document.querySelector('.transport-time span')?.textContent ?? '')`;
const previewTitle = `(document.querySelector('.preview-panel .preview-title .pane-title-text')?.textContent ?? '')`;
try {
  browser.cdp.on(m => { if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.exception?.description ?? 'exception'); });
  page = await browser.openPage(url, { width:1440, height:1000 });
  await page.send('DOM.enable');
  await page.waitFor(shell.connected, '连接App后端');
  console.log('PASS App生产页面经本机bootstrap连接匹配Server');
  await shell.newProject(page, { audio: wav, lyrics: lrc, name: `App integration ${Date.now()}` });
  await page.waitFor(`document.querySelectorAll('.line-list [role=row]').length === 2`, '两行歌词进入工作区', 120000);
  console.log('PASS 上传WAV/LRC并新建临时工程');
  await page.eval(`[...document.querySelectorAll('.stage-tok.kind-content')].find((c) => c.querySelector('.st-base')?.textContent === '朝').click()`);
  await page.waitFor(`document.querySelector('.stage-footer .sf-name')?.textContent === '朝'`, '选择元素');
  await page.eval(uiJs(`const el = ${timeInputs}[0]; el.focus(); __ui.setValue(el, '0:00.600');`));
  await page.eval(uiJs(`const el = ${timeInputs}[1]; el.focus(); __ui.setValue(el, '0:00.900'); __ui.key(el, 'Enter');`));
  await page.waitFor(`${REVISION} === 1`, '修改修订1');
  assert.equal(await page.eval(startTime), '0:00.600');
  await ui.click(page, 'button', '撤销', { scope: '.app-bar' });
  await page.waitFor(`${REVISION} === 2`, '撤销修订2');
  assert.equal(await page.eval(startTime), '');
  console.log('PASS 时间编辑与后端撤销修订闭环');
  await shell.goto(page, 'appearance');
  await page.waitFor(`!!document.querySelector('canvas[aria-label="时间轴"]')`, '外观时间轴');
  await ui.click(page, 'button', '全曲');
  const rect = await page.eval(`(() => {const r=document.querySelector('canvas[aria-label="时间轴"]').getBoundingClientRect();return {x:r.x,y:r.y,w:r.width};})()`);
  for (const type of ['mousePressed','mouseReleased']) await page.send('Input.dispatchMouseEvent',{type,x:rect.x+rect.w/6,y:rect.y+10,button:'left',clickCount:1});
  await page.waitFor(`${playhead}.startsWith('0:01.') && ${previewTitle}.endsWith(${playhead})`, '暂停定位1秒并更新帧', 120000);
  await page.waitFor(`document.querySelector('.preview-frame img')?.complete && document.querySelector('.preview-frame img').naturalWidth > 0`, '真实PNG显示', 120000);
  const png = await page.eval(`(async () => {const img=document.querySelector('.preview-frame img');const b=await(await fetch(img.src)).arrayBuffer();const a=new Uint8Array(b);const c=document.createElement('canvas');c.width=img.naturalWidth;c.height=img.naturalHeight;const ctx=c.getContext('2d');ctx.drawImage(img,0,0);const d=ctx.getImageData(0,0,c.width,c.height).data;let nonBlackRgbPixels=0;for(let i=0;i<d.length;i+=4)if(d[i]||d[i+1]||d[i+2])nonBlackRgbPixels++;return {timeText:document.querySelector('.transport-time span').textContent,nonBlackRgbPixels,width:img.naturalWidth,height:img.naturalHeight,bytes:a.length,signature:[...a.slice(0,8)],sha256:[...new Uint8Array(await crypto.subtle.digest('SHA-256',b))].map(v=>v.toString(16).padStart(2,'0')).join('')};})()`);
  assert.deepEqual(png.signature, [137,80,78,71,13,10,26,10]);
  await writeFile(join(out, 'editor-preview.png'), await page.screenshot());
  console.log(`PASS 实际PNG显示 ${png.width}x${png.height} ${png.bytes} bytes；截图.local/integration/editor-preview.png`);
  await shell.closeProject(page);
  assert.deepEqual(errors, []);
  await writeFile(join(out, 'browser-result.json'), JSON.stringify({ url, png, checks:['connect','create-wav-lrc','edit','undo','paused-seek-1s','png-displayed','close'], runtimeExceptions:errors }, null, 2)+'\n');
  console.log('PASS 临时工程关闭；页面无未处理异常');
} catch (error) {
  if (page) { await writeFile(join(out, 'failure.png'), await page.screenshot()).catch(()=>{}); console.error((await page.eval('document.body.innerText').catch(()=>'' )).slice(0,1600)); }
  throw error;
} finally { await browser.close(); await rm(assets, { recursive:true, force:true }); }
