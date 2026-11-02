// 冒烟脚本用的控件查询（界面改版 §4）：新控件基于 React Aria，按 ARIA 角色和可访问名称找元素，不依赖类名。
// 打开模态对话框时 React Aria 会给外面的内容加 aria-hidden，查询自动跳过它们，只在对话框里找。
//
//   import { ui } from './ui-query.mjs';
//   await ui.click(page, 'button', '保存');
//   await ui.fill(page, '工程名', '夜空', { enter: true });
//   await ui.choose(page, '字体', '思源宋体');
//   await ui.menu(page, '更多', '另存为…');
//   await ui.waitDialog(page, '新建工程');
//
// 名称按可访问名称（aria-labelledby、aria-label、关联的 label、文字）精确匹配；{ partial: true } 改为包含。
// 点击走 CDP 的真实鼠标事件：先滚到可见处，再确认这个点上最上层的元素就是它，避免点到盖在上面的东西。

/** 注入页面的查询函数，作为表达式前缀。 */
export const UI_PRELUDE = `
const __ui = (() => {
  const SELECTORS = {
    button: 'button, [role=button]',
    textbox: 'input:not([type]), input[type=text], input[type=search], input[type=number], textarea, [role=textbox]',
    checkbox: 'input[type=checkbox]:not([role=switch])',
    switch: 'input[role=switch]',
    radio: 'input[type=radio]',
    tab: '[role=tab]',
    menuitem: '[role=menuitem], [role=menuitemradio], [role=menuitemcheckbox]',
    option: '[role=option]',
    dialog: '[role=dialog], [role=alertdialog]',
    row: '[role=grid] [role=row]',
    treeitem: '[role=treegrid] [role=row]',
    select: 'button[aria-haspopup=listbox]',
    slider: 'input[type=range]',
  };
  const SKIP = '.badge, .menu-desc, .menu-kbd, .radio-desc, [aria-hidden=true], [slot=description]';
  const squash = (text) => (text ?? '').replace(/\\s+/g, ' ').trim();
  const text = (el) => {
    const copy = el.cloneNode(true);
    copy.querySelectorAll(SKIP).forEach((n) => n.remove());
    return squash(copy.textContent);
  };
  const byIds = (ids) => (ids ?? '').split(/\\s+/).filter(Boolean).map((id) => document.getElementById(id)).filter(Boolean).map(text);
  // 候选名称：标题元素（对话框）、aria-labelledby 的整体与各段、aria-label、label 元素、文字。
  const names = (el) => {
    const list = [];
    const by = byIds(el.getAttribute('aria-labelledby'));
    if (by.length) list.push(by.join(' '), ...by);
    if (el.getAttribute('aria-label')) list.push(squash(el.getAttribute('aria-label')));
    if (el.labels) for (const label of el.labels) list.push(text(label));
    if (el.matches('input, textarea')) return list;
    list.push(text(el));
    return list;
  };
  const hidden = (el) => !el.isConnected || !!el.closest('[aria-hidden=true], [inert]') || el.getClientRects().length === 0;
  const disabled = (el) => !!el.disabled || el.getAttribute('aria-disabled') === 'true' || !!el.closest('[data-disabled]');
  // scope：CSS 选择器；'@dialog'、'@menu' 是最上层（最后打开）的对话框、菜单。
  const top = (selector) => [...document.querySelectorAll(selector)].filter((el) => !hidden(el)).at(-1);
  const resolve = (scope) =>
    !scope ? document : scope === '@dialog' ? top(SELECTORS.dialog) : scope === '@menu' ? top('[role=menu]') : document.querySelector(scope);
  const all = (role, name, partial, scope) => {
    const root = resolve(scope);
    if (!root) return [];
    return [...root.querySelectorAll(SELECTORS[role] ?? role)].filter(
      (el) => !hidden(el) && (name == null || names(el).some((n) => (partial ? n.includes(name) : n === name))),
    );
  };
  const find = (role, name, partial, scope) => all(role, name, partial, scope)[0];
  // 复选框、开关、单选的 input 是视觉隐藏的，点外面的 label。
  const target = (el) => (el.matches('input[type=checkbox], input[type=radio]') ? el.closest('label') ?? el : el);
  const point = (el) => {
    const t = target(el);
    t.scrollIntoView({ block: 'center', inline: 'center' });
    const r = t.getBoundingClientRect();
    const x = r.left + r.width / 2;
    const y = r.top + r.height / 2;
    const hit = document.elementFromPoint(x, y);
    return { x, y, covered: !(hit && (t === hit || t.contains(hit))), what: hit ? hit.tagName.toLowerCase() + (hit.className ? '.' + String(hit.className).split(' ').join('.') : '') : 'null' };
  };
  // React 受控输入框：要用原生 setter 改值，再发 input 事件，React 才认。
  const setValue = (el, value) => {
    const proto = el instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, value);
    el.dispatchEvent(new Event('input', { bubbles: true }));
  };
  const key = (el, key) => {
    el.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
    el.dispatchEvent(new KeyboardEvent('keyup', { key, bubbles: true, cancelable: true }));
  };
  return { all, find, names, text, disabled, point, setValue, key };
})();
`;

/** 包一段页面脚本：可以用 __ui。 */
export const uiJs = (body) => `(() => { ${UI_PRELUDE} ${body} })()`;

const arg = (value) => JSON.stringify(value ?? null);
const describe = (role, name) => `${role}「${name}」`;
/** 选项里的 scope 是 CSS 选择器，或 '@dialog'、'@menu'（最上层的对话框、菜单），只在这个元素里找。 */
const findExpr = (role, name, opts = {}) => `__ui.find(${arg(role)}, ${arg(name)}, ${!!opts.partial}, ${arg(opts.scope)})`;

async function mouseClick(page, x, y, clickCount = 1) {
  for (const type of ['mouseMoved', 'mousePressed', 'mouseReleased']) {
    await page.send('Input.dispatchMouseEvent', { type, x, y, button: type === 'mouseMoved' ? 'none' : 'left', buttons: type === 'mousePressed' ? 1 : 0, clickCount });
  }
}

export const ui = {
  /** 等元素出现（enabled 时还要可用），返回 true。 */
  async wait(page, role, name, opts = {}) {
    const cond = opts.enabled === false ? `!!el` : `!!el && !__ui.disabled(el)`;
    await page.waitFor(uiJs(`const el = ${findExpr(role, name, opts)}; return ${cond};`), opts.what ?? `${describe(role, name)}${opts.enabled === false ? '' : '可用'}`, opts.timeout);
    return true;
  },

  async exists(page, role, name, opts = {}) {
    return page.eval(uiJs(`return !!${findExpr(role, name, opts)};`));
  },

  /** 在页面里对找到的元素求值：body 里用 el。 */
  async read(page, role, name, body, opts = {}) {
    return page.eval(uiJs(`const el = ${findExpr(role, name, opts)}; if (!el) throw new Error(${arg(`找不到 ${describe(role, name)}`)}); ${body}`));
  },

  async click(page, role, name, opts = {}) {
    await ui.wait(page, role, name, opts);
    // 被挡住时等一会儿：可能是正在关闭的面板、对话框还在做退场动画
    let p = await page.eval(uiJs(`return __ui.point(${findExpr(role, name, opts)});`));
    for (let i = 0; p.covered && i < 20; i++) {
      await new Promise((r) => setTimeout(r, 100));
      p = await page.eval(uiJs(`return __ui.point(${findExpr(role, name, opts)});`));
    }
    if (p.covered) throw new Error(`${describe(role, name)}被 ${p.what} 挡住了`);
    await mouseClick(page, p.x, p.y, 1);
    if (opts.double) await mouseClick(page, p.x, p.y, 2);
  },

  /** 改输入框的值；enter 时再按回车（提交式输入框）。label 是可见标签或 aria-label。 */
  async fill(page, label, value, opts = {}) {
    await ui.wait(page, 'textbox', label, opts);
    await page.eval(
      uiJs(`const el = ${findExpr('textbox', label, opts)}; el.focus(); __ui.setValue(el, ${arg(value)}); ${opts.enter ? `__ui.key(el, 'Enter');` : ''}`),
    );
  },

  async value(page, label, opts = {}) {
    return ui.read(page, 'textbox', label, 'return el.value;', opts);
  },

  /** 下拉选择：点开 label 对应的选择框，再点选项。 */
  async choose(page, label, option, opts = {}) {
    await ui.click(page, 'select', label, opts);
    await ui.click(page, 'option', option, { what: `下拉「${label}」的选项「${option}」` });
    await page.waitFor(uiJs(`return !document.querySelector('[role=listbox]');`), `下拉「${label}」关闭`);
  },

  /** 菜单：点触发按钮，再点菜单项（submenu 给出时先进子菜单）。 */
  async menu(page, trigger, item, opts = {}) {
    await ui.click(page, 'button', trigger, opts);
    for (const step of opts.submenu ? [opts.submenu, item] : [item]) await ui.click(page, 'menuitem', step, { scope: '@menu' });
  },

  /** 开关、复选框：设成 checked，已经是就不点。 */
  async setChecked(page, role, name, checked, opts = {}) {
    await ui.wait(page, role, name, opts);
    if ((await ui.read(page, role, name, 'return el.checked;', opts)) !== checked) await ui.click(page, role, name, opts);
    await page.waitFor(uiJs(`return ${findExpr(role, name, opts)}?.checked === ${!!checked};`), `${describe(role, name)}${checked ? '打开' : '关闭'}`);
  },

  async isSelected(page, role, name, opts = {}) {
    return ui.read(page, role, name, `return el.getAttribute('aria-selected') === 'true' || el.getAttribute('aria-checked') === 'true' || el.getAttribute('aria-pressed') === 'true' || !!el.checked;`, opts);
  },

  async waitDialog(page, title, opts = {}) {
    await page.waitFor(uiJs(`return !!${findExpr('dialog', title, opts)};`), `对话框「${title}」`, opts.timeout);
  },

  async waitDialogClosed(page, title, opts = {}) {
    await page.waitFor(uiJs(`return !${findExpr('dialog', title, opts)};`), `对话框「${title}」关闭`, opts.timeout);
  },

  /** 当前最上层对话框里的按钮。 */
  async clickInDialog(page, button, opts = {}) {
    await ui.click(page, 'button', button, { ...opts, scope: '@dialog' });
  },

  /** 消息条的文字（没有时是空串）。 */
  async toastText(page) {
    return page.eval(`document.querySelector('.toast-region .toast-text')?.textContent ?? ''`);
  },
};

const PAGE_LABEL = { home: '首页', lyrics: '歌词', orchestration: '编排', appearance: '外观', library: '样式库' };

/** 外壳（界面改版 §2.5）：连接就绪的判断、顶栏切页面、工程菜单。 */
export const shell = {
  /** 页面表达式：状态胶囊写着已连接。 */
  connected: `document.querySelector('.status-chip')?.dataset.connection === 'ready'`,
  /** 页面表达式：有工程打开（顶栏有歌词、编排、外观页签）。 */
  projectOpen: `!!document.querySelector('.app-bar .pages')`,

  /** 顶栏切页面：value 是 home、lyrics、orchestration、appearance、library，或者按钮名。 */
  async goto(page, value) {
    await ui.click(page, 'button', PAGE_LABEL[value] ?? value, { scope: '.app-bar' });
    // 真实点击把焦点留在顶栏按钮上，之后关对话框时焦点会回到它，抢走脚本刚聚焦的输入框；切完页面放回 body
    await page.eval(`document.activeElement?.blur()`);
  },

  /** 工程名菜单（另存为、导出歌词文件、关闭工程、协议控制台）。 */
  async projectMenu(page, item) {
    await ui.menu(page, '工程菜单', item, { partial: true, scope: '.app-bar' });
  },
};
