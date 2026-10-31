// Finite real-browser integration check. No playback or throughput claims.
import assert from 'node:assert/strict';
import { mkdtemp, writeFile, rm, mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { launchChrome } from '../frontend/scripts/cdp.mjs';
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
const field = (label) => `[...document.querySelectorAll('mdui-text-field')].find(f => f.label === ${JSON.stringify(label)})`;
const button = (text) => `[...document.querySelectorAll('mdui-button,mdui-button-icon')].find(b => (b.textContent.trim() === ${JSON.stringify(text)} || b.getAttribute('aria-label') === ${JSON.stringify(text)}) && !b.disabled)`;
async function click(text) { const expr = button(text); await page.waitFor(`!!(${expr})`, text); await page.eval(`(${expr}).click()`); }
async function input(label, value) { const expr = field(label); await page.waitFor(`!!(${expr})`, label); await page.eval(`(() => {const f = ${expr}; f.value = ${JSON.stringify(value)}; f.dispatchEvent(new Event('input', {bubbles:true,composed:true}));})()`); }
async function file(label, path) { const {root} = await page.send('DOM.getDocument', {depth:-1}); const {nodeId} = await page.send('DOM.querySelector', {nodeId:root.nodeId, selector:`input[type=file][aria-label="${label}"]`}); await page.send('DOM.setFileInputFiles', {nodeId, files:[path]}); }
try {
  browser.cdp.on(m => { if (m.method === 'Runtime.exceptionThrown') errors.push(m.params.exceptionDetails.exception?.description ?? 'exception'); });
  page = await browser.openPage(url, { width:1440, height:1000 });
  await page.send('DOM.enable');
  await page.waitFor(`document.querySelector('.connection')?.textContent === '已连接'`, '连接App后端');
  console.log('PASS App生产页面经本机bootstrap连接匹配Server');
  await click('新建工程');
  await page.waitFor(`!!document.querySelector('mdui-dialog.new-project-dialog[open] input[aria-label="选择歌词"]')`, '新建对话框');
  await file('选择音频', wav); await file('选择歌词', lrc);
  await page.waitFor(`!!document.querySelector('.lyrics-plan-summary')`, '歌词识别');
  await input('工程名', `App integration ${Date.now()}`);
  await click('下一步'); await click('新建');
  await page.waitFor(`document.querySelectorAll('.line-list mdui-list-item').length === 2`, '两行歌词进入工作区', 120000);
  console.log('PASS 上传WAV/LRC并新建临时工程');
  await page.eval(`document.querySelector('.token-chip').click()`);
  await page.waitFor(`document.querySelector('.time-editor-title')?.textContent.includes('所选「朝」')`, '选择元素');
  await input('起点', '0:00.600'); await input('终点', '0:00.900');
  await page.eval(`(${field('终点')}).dispatchEvent(new KeyboardEvent('keydown', {key:'Enter',bubbles:true,composed:true}))`);
  await page.waitFor(`document.querySelector('.status-bar')?.textContent.includes('修订 1')`, '修改修订1');
  assert.equal(await page.eval(`(${field('起点')}).value`), '0:00.600');
  await click('撤销');
  await page.waitFor(`document.querySelector('.status-bar')?.textContent.includes('修订 2')`, '撤销修订2');
  assert.equal(await page.eval(`(${field('起点')}).value`), '');
  console.log('PASS 时间编辑与后端撤销修订闭环');
  await page.eval(`document.querySelector('mdui-navigation-rail-item[value=appearance]').click()`);
  await page.waitFor(`!!document.querySelector('.appearance .timeline canvas')`, '外观时间轴');
  await click('全曲');
  const rect = await page.eval(`(() => {const r=document.querySelector('.appearance .timeline canvas').getBoundingClientRect();return {x:r.x,y:r.y,w:r.width};})()`);
  for (const type of ['mousePressed','mouseReleased']) await page.send('Input.dispatchMouseEvent',{type,x:rect.x+rect.w/6,y:rect.y+10,button:'left',clickCount:1});
  await page.waitFor(`document.querySelector('.transport-time span')?.textContent.startsWith('0:01.') && document.querySelector('.preview-title')?.textContent.includes(document.querySelector('.transport-time span').textContent)`, '暂停定位1秒并更新帧', 120000);
  await page.waitFor(`document.querySelector('.preview-frame img')?.complete && document.querySelector('.preview-frame img').naturalWidth > 0`, '真实PNG显示', 120000);
  const png = await page.eval(`(async () => {const img=document.querySelector('.preview-frame img');const b=await(await fetch(img.src)).arrayBuffer();const a=new Uint8Array(b);const c=document.createElement('canvas');c.width=img.naturalWidth;c.height=img.naturalHeight;const ctx=c.getContext('2d');ctx.drawImage(img,0,0);const d=ctx.getImageData(0,0,c.width,c.height).data;let nonBlackRgbPixels=0;for(let i=0;i<d.length;i+=4)if(d[i]||d[i+1]||d[i+2])nonBlackRgbPixels++;return {timeText:document.querySelector('.transport-time span').textContent,nonBlackRgbPixels,width:img.naturalWidth,height:img.naturalHeight,bytes:a.length,signature:[...a.slice(0,8)],sha256:[...new Uint8Array(await crypto.subtle.digest('SHA-256',b))].map(v=>v.toString(16).padStart(2,'0')).join('')};})()`);
  assert.deepEqual(png.signature, [137,80,78,71,13,10,26,10]);
  await writeFile(join(out, 'editor-preview.png'), await page.screenshot());
  console.log(`PASS 实际PNG显示 ${png.width}x${png.height} ${png.bytes} bytes；截图.local/integration/editor-preview.png`);
  await click('更多');
  const item = `[...document.querySelectorAll('mdui-menu-item')].find(i => i.textContent.trim() === '关闭工程' && !i.disabled)`;
  await page.waitFor(`!!(${item})`, '关闭工程菜单');
  await page.eval(`(${item}).dispatchEvent(new MouseEvent('click',{bubbles:true,composed:true}))`);
  await page.waitFor(`!!(${button('不保存')}) || !document.querySelector('mdui-navigation-rail-item[value=lyrics]')`, '关闭或放弃保存');
  if (await page.eval(`!!(${button('不保存')})`)) await click('不保存');
  await page.waitFor(`!document.querySelector('mdui-navigation-rail-item[value=lyrics]')`, '工程已关闭');
  assert.deepEqual(errors, []);
  await writeFile(join(out, 'browser-result.json'), JSON.stringify({ url, png, checks:['connect','create-wav-lrc','edit','undo','paused-seek-1s','png-displayed','close'], runtimeExceptions:errors }, null, 2)+'\n');
  console.log('PASS 临时工程关闭；页面无未处理异常');
} catch (error) {
  if (page) { await writeFile(join(out, 'failure.png'), await page.screenshot()).catch(()=>{}); console.error((await page.eval('document.body.innerText').catch(()=>'' )).slice(0,1600)); }
  throw error;
} finally { await browser.close(); await rm(assets, { recursive:true, force:true }); }
