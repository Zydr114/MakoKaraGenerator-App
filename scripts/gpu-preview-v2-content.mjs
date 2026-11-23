// P3 GPU 流的 SDK2 内容：经生产界面在样式库建一个含 SDK2 效果的行模板，再在外观页一次应用到全部歌词行。
// 样式 P3 起可加 --v2-style（两态、硬边分层渐变、4 层描边、羽化加高亮），可以不带包单独测直绘路径。
// 只用界面操作与文件输入，不读取凭证、不直接发 HTTP；包取本仓库 backend/tests/Fixtures/ScriptEffectsV2 或
// backend/tests/Fixtures/ScriptEffectsV2Perf（M12 测速用测试包）并校验清单 SHA。
// 材质包与 M12 HTTP 探针一致：全部材质导出按顺序排开，未唱填充用第一个、已唱填充用最后一个。
import { createHash } from 'node:crypto';
import { access, appendFile, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { zipFiles } from '../frontend/scripts/effect-packages.mjs';
import { resolveBackendRepo } from '../frontend/scripts/backend-launcher.mjs';
import { shell, ui, uiJs } from '../frontend/scripts/ui-query.mjs';

/** 与 M11/M12 HTTP 探针相同的 T01 复杂样式五例。 */
export const V2_SETS = { t01: ['ClusterExit', 'WaveWarp', 'StarSweep', 'CursorTrail', 'AdaptiveStroke'] };
const EFFECT_KINDS = new Set(['controller', 'graph']);
const fail = (message) => { throw new Error(message); };

export function v2SampleNames(value) {
  const names = V2_SETS[value] ?? value.split(',');
  if (names.length < 1 || names.length > 8 || names.some((name) => !/^[A-Za-z][A-Za-z0-9]{0,63}$/.test(name))) fail('--v2-samples 须为 t01 或逗号分隔的样例目录名');
  return names;
}

/** --v2-params：逗号分隔、不重复的「参数=数值」，打包时改所选包里同名数值参数的默认值。 */
export function v2ParamOverrides(value) {
  const overrides = {};
  for (const item of value === undefined ? [] : value.split(',')) {
    const match = /^([A-Za-z][A-Za-z0-9]{0,63})=(-?\d+(?:\.\d+)?)$/.exec(item);
    if (!match || Object.hasOwn(overrides, match[1])) fail('--v2-params 须为逗号分隔、不重复的 参数=数值');
    overrides[match[1]] = Number(match[2]);
  }
  return overrides;
}

/**
 * 样式库模板编辑器里的样式预设（P3 第 8 步复测）：在“外观”页签按界面操作改经典整行，保存前生效。
 * - twoState：未唱一层描边，已唱描边单独设置为更宽的一层（两态按态绘制）；
 * - bands：未唱填充改为单个字形坐标的线性渐变，第 2、3 色标重合成硬边；
 * - strokes：未唱 4 层描边加阴影（阴影取整体轮廓）；
 * - feather：擦除羽化 0.2 倍字号，开扫字线高亮（缺省滤色）。
 */
export const V2_STYLES = {
  async twoState(page) {
    await strokeOn(page);
    await includeGroup(page, 'sungStroke');
    await ui.choose(page, '已唱描边开关', '单独设置');
    await page.waitFor(`${strokeLayers('sungStroke')} >= 1`, '已唱描边单独设置');
    await ui.fill(page, '宽度', '8', { scope: '[data-group="sungStroke"] [data-layer="0"]', enter: true });
  },
  async bands(page) {
    const scope = '[data-group="unsung"]';
    await includeGroup(page, 'unsung');
    await ui.choose(page, '未唱颜色的类型', '渐变', { scope });
    for (let i = 0; i < 2; i++) await ui.click(page, 'button', '添加色标', { scope });
    await page.waitFor(`document.querySelectorAll('${scope} .gradient-stop').length === 4`, '四个色标');
    await ui.fill(page, '色标 2', '50', { scope, enter: true });
    await ui.fill(page, '色标 3 的颜色', '#ff4040', { scope, enter: true });
    await ui.fill(page, '色标 4 的颜色', '#ff4040', { scope, enter: true });
    await ui.choose(page, '坐标', '单个字形', { scope });
  },
  async strokes(page) {
    await strokeOn(page);
    while ((await page.eval(strokeLayers('stroke'))) < 4) await ui.click(page, 'button', '加一层外圈', { scope: '[data-group="stroke"]' });
    await includeGroup(page, 'shadow');
    await ui.choose(page, '未唱阴影开关', '有阴影');
  },
  async feather(page) {
    await includeGroup(page, 'feather');
    await ui.fill(page, '羽化宽度', '0.2', { scope: '[data-group="feather"]', enter: true });
    await includeGroup(page, 'highlight');
    await ui.choose(page, '高亮开关', '有高亮');
  },
};

export function v2StyleName(value) {
  if (!Object.hasOwn(V2_STYLES, value)) fail(`--v2-style 须为 ${Object.keys(V2_STYLES).join('、')} 之一`);
  return value;
}

const strokeLayers = (group) => `document.querySelectorAll('[data-group="${group}"] .stroke-layer').length`;

/** 勾上模板属性组（右侧包含开关）。 */
async function includeGroup(page, group) {
  const selector = `[data-group="${group}"] [role="switch"]`;
  await page.waitFor(`!!document.querySelector(${JSON.stringify(selector)})`, `${group} 属性组开关`);
  if (!(await page.eval(`document.querySelector(${JSON.stringify(selector)}).checked`))) await ui.click(page, selector, null);
  await page.waitFor(`document.querySelector(${JSON.stringify(selector)})?.checked === true`, `包含 ${group}`);
}

async function strokeOn(page) {
  await includeGroup(page, 'stroke');
  if ((await page.eval(strokeLayers('stroke'))) === 0) await ui.choose(page, '未唱描边开关', '有描边');
  await page.waitFor(`${strokeLayers('stroke')} >= 1`, '未唱描边');
}

/** 高级页签里的完整定义（只读），复测记录用。 */
async function definitionStyle(page) {
  await ui.click(page, 'tab', '高级');
  await page.waitFor(`!!document.querySelector('.template-json')`, '完整定义');
  return JSON.parse(await page.eval(`document.querySelector('.template-json').textContent`)).style;
}

const PACKAGE_SOURCES = { samples: ['tests', 'Fixtures', 'ScriptEffectsV2'], 'perf-fixtures': ['tests', 'Fixtures', 'ScriptEffectsV2Perf'] };

/** 包目录须恰好出现在样例与测速夹具两处之一，同名时报错而不是择一。 */
async function packageRoot(name) {
  const found = [];
  for (const [source, parts] of Object.entries(PACKAGE_SOURCES)) {
    const root = join(resolveBackendRepo(), ...parts, name);
    if (await access(join(root, 'manifest.json')).then(() => true, () => false)) found.push({ source, root });
  }
  if (found.length !== 1) fail(`${name} 须恰好在样例或测速夹具目录之一（找到 ${found.length} 处）`);
  return found[0];
}

/**
 * 把包目录打成 .mkfx 写到输出目录；文件字节须与清单 SHA 一致。包须含控制器、图或材质导出。
 * overrides 改清单里同名数值参数的默认值（须在其范围内），每个覆盖至少要被一个包用到；清单本身不在 SHA 闭包内。
 */
export async function packV2Samples(output, names, overrides = {}) {
  const packages = [];
  const used = new Set();
  for (const name of names) {
    const { source, root } = await packageRoot(name);
    let raw = await readFile(join(root, 'manifest.json'));
    const manifest = JSON.parse(raw.toString('utf8'));
    if (manifest.packageVersion !== 3 || !String(manifest.sdkVersion).startsWith('2.')) fail(`${name} 不是 package3/SDK2`);
    const parameterOverrides = {};
    for (const [id, value] of Object.entries(overrides)) {
      const parameter = manifest.parameters.find((item) => item.id === id);
      if (!parameter) continue;
      if (parameter.type !== 'number' || value < parameter.min || value > parameter.max) fail(`${name}.${id} 不是数值参数或 ${value} 超出 ${parameter.min}～${parameter.max}`);
      parameter.default = value;
      parameterOverrides[id] = value;
      used.add(id);
    }
    if (Object.keys(parameterOverrides).length > 0) raw = Buffer.from(JSON.stringify(manifest, null, 2) + '\n', 'utf8');
    const files = [];
    for (const entry of manifest.files) {
      const data = await readFile(join(root, entry.path));
      if (createHash('sha256').update(data).digest('hex') !== entry.sha256) fail(`${name}/${entry.path} 与清单 SHA 不符`);
      files.push({ name: entry.path, data });
    }
    const effects = manifest.exports.filter((item) => EFFECT_KINDS.has(item.kind));
    const materials = manifest.exports.filter((item) => item.kind === 'material');
    if (effects.length === 0 && materials.length === 0) fail(`${name} 没有控制器、图或材质导出`);
    const archive = zipFiles([{ name: 'manifest.json', data: raw }, ...files]);
    const path = join(output, `v2-${name}.mkfx`);
    await writeFile(path, archive, { mode: 0o600 });
    packages.push({ name, source, path, packageId: manifest.packageId, packageName: manifest.name, effects: effects.map((item) => ({ id: item.id, name: item.name })),
      materials: materials.map((item) => ({ id: item.id, name: item.name })), parameterOverrides, archiveSha256: createHash('sha256').update(archive).digest('hex') });
  }
  const unused = Object.keys(overrides).filter((id) => !used.has(id));
  if (unused.length > 0) fail(`--v2-params 里的 ${unused.join('、')} 没有任何所选包声明`);
  return packages;
}

/** 真实鼠标点元素中心并带修饰键（8 是 Shift）。长列表滚动到位前坐标会变，像 ui.click 一样等坐标稳定且没被挡住。 */
async function clickWith(page, selector, modifiers = 0) {
  const measure = () => page.eval(uiJs(`const el = document.querySelector(${JSON.stringify(selector)}); if (!el) throw new Error(${JSON.stringify(`找不到 ${selector}`)}); return __ui.point(el);`));
  let last = null;
  let p = await measure();
  for (let i = 0; (p.covered || !last || last.x !== p.x || last.y !== p.y) && i < 30; i++) {
    await new Promise((done) => setTimeout(done, 100));
    last = p;
    p = await measure();
  }
  if (p.covered) throw new Error(`${selector} 被 ${p.what} 挡住了`);
  // 点下去之前再看一次落点下的行，便于排查点偏。
  const hit = await page.eval(`document.elementFromPoint(${p.x}, ${p.y})?.closest('[data-target-key]')?.dataset.targetKey ?? null`);
  for (const type of ['mouseMoved', 'mousePressed', 'mouseReleased']) {
    await page.send('Input.dispatchMouseEvent', { type, x: p.x, y: p.y, button: type === 'mouseMoved' ? 'none' : 'left', buttons: type === 'mousePressed' ? 1 : 0, clickCount: 1, modifiers });
  }
  return { x: p.x, y: p.y, hit };
}

/**
 * 窄窗（P3 桌面窗口 1024 宽为 bothOverlay）两侧栏收成竖条，同一时刻只开一侧；宽窗停靠时没有竖条，直接返回。
 * 用到某侧的控件前先点开该侧竖条，等它展开。
 */
async function side(page, label) {
  const rail = `document.querySelector(${JSON.stringify(`.work-rail[aria-label="${label}"]`)})`;
  if (!(await page.eval(`!!${rail}`)) || (await page.eval(`${rail}.getAttribute('aria-expanded') === 'true'`))) return;
  await ui.click(page, `.work-rail[aria-label="${label}"]`, null);
  await page.waitFor(`!${rail} || ${rail}.getAttribute('aria-expanded') === 'true'`, `展开「${label}」`);
}

const templateName = `document.querySelector('.template-name')?.textContent`;
const detailTags = `[...document.querySelectorAll('.template-detail .phead .tag')].map((t) => t.textContent)`;
const revision = `Number((document.querySelector('.status-bar')?.textContent.match(/修订 (\\d+)/) ?? [])[1])`;
const lineRows = `document.querySelectorAll('.appearance-targets .at.line-row')`;
const recipes = `document.querySelectorAll('.recipe-item').length`;

/** 计时前的建模板步骤失败时留一张截图和页面摘要（不在任何计时阶段内）。 */
async function withFailureEvidence(page, output, step) {
  try { return await step(); }
  catch (error) {
    try {
      // 窗口被遮挡或不在屏幕上时合成器不出新帧，截图会一直挂起；限 10 秒，超时只留页面摘要。
      const shot = await Promise.race([page.screenshot(), new Promise((done) => setTimeout(() => done(null), 10_000))]);
      if (shot) await writeFile(join(output, 'v2-content-failure.png'), shot);
      const state = await page.eval(`(() => ({ url: location.href, title: document.title,
        page: document.querySelector('.app-bar [aria-current=page], .app-bar [aria-pressed=true]')?.textContent ?? null,
        viewport: { width: innerWidth, height: innerHeight },
        dialogs: [...document.querySelectorAll('[role=dialog], [role=alertdialog]')].map((d) => d.textContent.slice(0, 200)),
        rows: [...document.querySelectorAll('[role=row]')].slice(0, 20).map((r) => ({ text: r.textContent.slice(0, 60), rects: r.getClientRects().length,
          hidden: !!r.closest('[aria-hidden=true], [inert]'), disabled: r.getAttribute('aria-disabled'), dataDisabled: !!r.closest('[data-disabled]') })) }))()`);
      await writeFile(join(output, 'v2-content-failure.json'), JSON.stringify({ error: String(error?.message ?? error), state }, null, 2) + '\n');
    } catch { /* 保留原始失败。 */ }
    throw error;
  }
}

/** 未唱填充用全部材质导出里的第一个、已唱填充用最后一个（只有一个时两处相同）；没有材质导出时不动材质。 */
export function materialSlotPlan(packages) {
  const all = packages.flatMap((item) => item.materials.map((material) => ({ ...material, packageName: item.packageName, packageId: item.packageId })));
  return all.length === 0 ? [] : [{ slot: 'unsungFill', label: '未唱填充', ...all[0] }, { slot: 'sungFill', label: '已唱填充', ...all.at(-1) }];
}

/** 样式库复制“经典整行”，导入各包、逐个加效果并填好材质部位，按需套样式预设后保存；外观页 Shift 选中全部行一次应用。 */
export async function applyV2Template(page, output, names, lineCount, overrides = {}, style = null) {
  return withFailureEvidence(page, output, () => buildAndApply(page, output, names, lineCount, overrides, style));
}

async function buildAndApply(page, output, names, lineCount, overrides, style) {
  // 每步耗时写到 v2-content-steps.jsonl，便于定位慢步骤；只在计时阶段之前。
  let mark = Date.now();
  const step = async (label) => {
    const now = Date.now();
    await appendFile(join(output, 'v2-content-steps.jsonl'), JSON.stringify({ step: label, ms: now - mark, at: new Date(now).toISOString() }) + '\n');
    mark = now;
  };
  const packages = await packV2Samples(output, names, overrides);
  const name = [names.length > 0 && `P3 SDK2 ${names.join('+')}`, style && `P3 样式 ${style}`].filter(Boolean).join(' · ');
  const started = Date.now();
  await shell.goto(page, 'library');
  const classic = '[role=row]:has([data-template-id="builtin:line-classic"])';
  await page.waitFor(`!!document.querySelector(${JSON.stringify(classic)})`, '内置经典整行', 60_000);
  await side(page, '样式库');
  await ui.click(page, classic, null);
  await page.waitFor(`${templateName} === '经典整行'`, '内置详情');
  await side(page, '模板编辑');
  await ui.click(page, 'button', '复制并编辑');
  await ui.waitDialog(page, '复制到我的模板');
  await ui.fill(page, '名称', name, { scope: '@dialog' });
  await ui.clickInDialog(page, '复制');
  await ui.waitDialogClosed(page, '复制到我的模板', { timeout: 30_000 });
  await page.waitFor(`${templateName} === ${JSON.stringify(name)} && !!document.querySelector('.template-tabs')`, `复制为 ${name}`, 30_000);
  await side(page, '模板编辑');
  if (packages.length > 0) await ui.click(page, 'tab', '效果');
  await step('复制经典整行');
  for (const item of packages) {
    await ui.setFile(page, '导入效果包…', item.path);
    await page.waitFor(`!!document.querySelector('.package-pending[data-status=ready]')`, `检查 ${item.name}`, 60_000);
    await ui.click(page, 'button', '加入模板', { scope: '.package-pending' });
    await page.waitFor(`!!document.querySelector('.package-item[data-package-id=${JSON.stringify(item.packageId)}]')`, `加入 ${item.packageId}`, 30_000);
    await step(`导入 ${item.name}`);
  }
  for (const item of packages) {
    for (const effect of item.effects) {
      const before = await page.eval(recipes);
      await ui.choose(page, '要加的效果', `${effect.name}（${item.packageName}）`, { scope: '.add-effect' });
      await ui.click(page, 'button', '加效果', { scope: '.add-effect' });
      await page.waitFor(`${recipes} === ${before + 1}`, `加效果 ${effect.id}`, 30_000);
      await step(`加效果 ${effect.id}`);
    }
  }
  const slots = materialSlotPlan(packages);
  if (slots.length > 0) {
    await ui.click(page, 'tab', '材质');
    await page.waitFor(`!!document.querySelector('.slot-material')`, '材质页签');
  }
  for (const target of slots) {
    const slotMaterial = `document.querySelector('.slot-material[data-slot=${target.slot}]')`;
    await ui.click(page, 'button', target.label, { scope: '.slot-buttons' });
    await page.waitFor(`!!${slotMaterial}`, `部位 ${target.label}`);
    if ((await page.eval(`${slotMaterial}.dataset.kind`)) !== 'package') {
      await ui.click(page, 'button', '效果包', { scope: '.slot-material .material-kind' });
      await page.waitFor(`${slotMaterial}?.dataset.kind === 'package'`, `${target.label}换成包材质`);
    }
    // 选项文字是“名称 版本（修订）”，按名称前缀选；材质导出按名称精确选。
    const chosen = (field) => `document.querySelector('.slot-material .${field} .select-value')?.textContent.trim()`;
    const pkgLabel = await page.eval(chosen('material-package'));
    if (!pkgLabel?.startsWith(`${target.packageName} `)) {
      await ui.click(page, 'select', '效果包', { scope: '.material-package' });
      await ui.click(page, 'option', `${target.packageName} `, { partial: true, what: `${target.label}的效果包「${target.packageName}」` });
    }
    await ui.choose(page, '材质', target.name, { scope: '.material-export' });
    await page.waitFor(`${chosen('material-package')}?.startsWith(${JSON.stringify(`${target.packageName} `)}) && ${chosen('material-export')} === ${JSON.stringify(target.name)}`,
      `${target.label}用「${target.packageName}／${target.name}」`);
    // 模板不包含该部位时材质不会生效，计时结果会失真，直接失败。
    if (await page.eval(`!!${slotMaterial}?.querySelector('.material-off')`)) fail(`模板不包含${target.label}，材质不会生效`);
    await step(`${target.label} ${target.packageId}/${target.id}`);
  }
  let styleDefinition = null;
  if (style) {
    await ui.click(page, 'tab', '外观', { scope: '.template-tabs' });
    await V2_STYLES[style](page);
    styleDefinition = await definitionStyle(page);
    await step(`样式 ${style}`);
  }
  await page.waitFor(`!document.querySelector('.template-problem')`, '模板没有问题提示', 30_000);
  await ui.click(page, 'button', '保存', { scope: '.template-actions' });
  await page.waitFor(`${detailTags}[0] === '我的模板' && !${detailTags}.includes('未保存')`, '模板保存完成', 60_000);
  await step('保存模板');

  await shell.goto(page, 'appearance');
  await page.waitFor(`${lineRows}.length === ${lineCount}`, `外观页 ${lineCount} 行`, 120_000);
  await step('外观页就绪');
  await side(page, '外观目标');
  const key = (index) => page.eval(`${lineRows}[${index}].dataset.targetKey`);
  const first = await key(0), last = await key(lineCount - 1);
  const firstClick = await clickWith(page, `.appearance-targets .at[data-target-key="${first}"]`);
  await page.waitFor(`document.querySelector(${JSON.stringify(`.appearance-targets .at[data-target-key="${first}"]`)})?.getAttribute('aria-selected') === 'true'
    || !!document.querySelector(${JSON.stringify(`.appearance-targets .at.on[data-target-key="${first}"]`)})`, '选中第 1 行', 10_000).catch(() => {});
  const selected = `[...${lineRows}].filter((row) => row.getAttribute('aria-selected') === 'true' || row.classList.contains('on')).length`;
  // 偶发只选中前一段（例如 24 行里选中 20 行）：等选择落定，不全就再 Shift 点一次末行，最多三次。
  let lastClick, selectedRows = 0;
  for (let attempt = 1; attempt <= 3 && selectedRows !== lineCount; attempt++) {
    lastClick = await clickWith(page, `.appearance-targets .at[data-target-key="${last}"]`, 8);
    await page.waitFor(`${selected} === ${lineCount}`, `Shift 选中 ${lineCount} 行`, 5_000).catch(() => {});
    selectedRows = await page.eval(selected);
    await appendFile(join(output, 'v2-content-steps.jsonl'), JSON.stringify({ step: 'Shift 选择诊断', attempt, first, last, firstClick, lastClick, selectedRows }) + '\n');
  }
  if (selectedRows !== lineCount) fail(`Shift 只选中 ${selectedRows}／${lineCount} 行`);
  await side(page, '模板与覆盖');
  await ui.click(page, 'button', '选择模板…', { scope: '.template-grp' });
  await ui.waitDialog(page, '选择模板');
  const card = `[...document.querySelectorAll('.template-picker .template-card')].find((c) => c.querySelector('.template-card-name')?.textContent === ${JSON.stringify(name)})`;
  await page.waitFor(`!!${card}`, `选择器里的「${name}」`, 60_000);
  const id = await page.eval(`${card}.dataset.templateId`);
  await ui.click(page, `.template-picker .template-card[data-template-id="${id}"]`, null);
  const apply = `应用到 ${lineCount} 行`;
  await step('选中全部行并打开选择器');
  await ui.wait(page, 'button', apply, { scope: '@dialog', timeout: 30_000 });
  const before = await page.eval(revision);
  await ui.clickInDialog(page, apply);
  await ui.waitDialogClosed(page, '选择模板', { timeout: 120_000 });
  await page.waitFor(`${revision} === ${before + 1}`, `应用「${name}」到 ${lineCount} 行`, 120_000);
  await step('应用到全部行');
  return { templateName: name, samples: names, style, styleDefinition, appliedLines: lineCount, projectRevision: before + 1, setupMs: Date.now() - started,
    packages: packages.map(({ name: sample, source, packageId, archiveSha256, effects, materials, parameterOverrides }) => ({ sample, source, packageId, archiveSha256,
      exports: effects.map((item) => item.id), materials: materials.map((item) => item.id), parameterOverrides })),
    materialSlots: Object.fromEntries(materialSlotPlan(packages).map((item) => [item.slot, `${item.packageId}/${item.id}`])),
    boundary: 'Template built and applied through production UI before timing; setup time is not part of any interaction metric.' };
}
