// MakoKara 界面风格设计稿共用脚本：示例数据、图标和会重复出现的片段（行列表、文本单元、时间轴、画面）。
// 只服务于静态设计稿，不是应用代码。示例歌词为原创。
// 用法：页面里放 <div data-r="片段名" data-o='{"选项":…}'></div>，加载本脚本后自动填充。
(() => {
  const RAW = [
    ['朝の光が窓を染めて', '晨光染上了窗', 2.1, 6.4, 'ok', '朝:あさ|の|光:ひかり|が|窓:まど|を|染:そ|め|て'],
    ['君の声がまだ耳に残る', '你的声音还留在耳边', 6.8, 11.6, 'ok', '君:きみ|の|声:こえ|が|ま|だ|耳:みみ|に|残:のこ|る'],
    ['夜明けのホームで手を振った', '在黎明的站台挥手', 12.3, 15.8, 'edited', '夜明:よあ|け|の|ホ|ー|ム|で|手:て|を|振:ふ|っ|た'],
    ['言えなかった言葉を抱いて', '抱着没能说出口的话', 16.4, 20.9, 'ok', '言:い|え|な|か|っ|た|言葉:ことば|を|抱:だ|い|て'],
    ['走り出す電車の窓から', '从开动的电车窗口', 21.5, 25.7, 'ok', '走:はし|り|出:だ|す|電車:でんしゃ|の|窓:まど|か|ら'],
    ['遠ざかる街を見ていた', '望着渐渐远去的街', 26.2, 30.8, 'ok', '遠:とお|ざ|か|る|街:まち|を|見:み|て|い|た'],
    ['いつかまた会えると信じて', '相信总有一天会再见', 31.6, 36.2, 'est', 'い|つ|か|ま|た|会:あ|え|る|と|信:しん|じ|て'],
    ['歌うよ この空の下で', '在这片天空下歌唱', 36.8, 41.5, 'est', '歌:うた|う|よ|こ|の|空:そら|の|下:した|で'],
    ['改札を抜けた先に', '穿过检票口的前方', 44.0, 47.6, 'none', '改札:かいさつ|を|抜:ぬ|け|た|先:さき|に'],
    ['新しい季節が待ってる', '新的季节正在等待', 48.1, 52.4, 'none', '新:あたら|し|い|季節:きせつ|が|待:ま|っ|て|る'],
    ['振り返らずに行くよ', '不回头地走下去', 53.0, 57.2, 'none', '振:ふ|り|返:かえ|ら|ず|に|行:い|く|よ'],
    ['夜明けのホームから', '从黎明的站台出发', 57.8, 62.6, 'none', '夜明:よあ|け|の|ホ|ー|ム|か|ら'],
  ];

  const LINES = RAW.map(([text, zh, s, e, state, tk], i) => {
    const parts = tk.split('|').map((p) => {
      const [t, r] = p.split(':');
      return { t, r: r ?? null };
    });
    const weight = parts.map((p) => Math.max(1, [...p.t].length));
    const total = weight.reduce((a, b) => a + b, 0);
    let at = s;
    const span = e - s - 0.12;
    const tokens = parts.map((p, k) => {
      const d = (span * weight[k]) / total;
      const tok = { ...p, s: at, e: at + d - 0.03 };
      at += d;
      return tok;
    });
    return { n: i + 1, text, zh, s, e, state, tokens };
  });

  const STATE = {
    ok: { icon: 'check', label: '已确认' },
    edited: { icon: 'check', label: '已修改' },
    est: { icon: 'half', label: '估算' },
    none: { icon: 'ring', label: '未打轴' },
  };

  const SONG = { title: '黎明站台', duration: 225, playhead: 14.218, current: 3, token: 0 };

  // ———— 工具 ————
  const pad = (n, w = 2) => String(n).padStart(w, '0');
  const fmt = (t, ms = true) => {
    const m = Math.floor(t / 60);
    const s = t - m * 60;
    return ms ? `${m}:${pad(Math.floor(s))}.${pad(Math.round((s % 1) * 1000) % 1000, 3)}` : `${m}:${pad(Math.floor(s))}`;
  };
  const esc = (s) => String(s).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
  const rng = (seed) => () => {
    seed |= 0;
    seed = (seed + 0x6d2b79f5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
  const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
  const pct = (t, from, to) => `${(((t - from) / (to - from)) * 100).toFixed(3)}%`;

  // ———— 图标（24 栅格，描边风格） ————
  const ICONS = {
    play: '<path d="M8 5.8v12.4a.6.6 0 0 0 .9.5l9.6-6.2a.6.6 0 0 0 0-1L8.9 5.3a.6.6 0 0 0-.9.5Z" fill="currentColor" stroke="none"/>',
    pause: '<rect x="7" y="5.5" width="3.4" height="13" rx=".8" fill="currentColor" stroke="none"/><rect x="13.6" y="5.5" width="3.4" height="13" rx=".8" fill="currentColor" stroke="none"/>',
    undo: '<path d="M9 13.5 4.5 9 9 4.5"/><path d="M4.5 9h10a5 5 0 0 1 0 10H11"/>',
    redo: '<path d="M15 13.5 19.5 9 15 4.5"/><path d="M19.5 9h-10a5 5 0 0 0 0 10H13"/>',
    plus: '<path d="M12 5v14M5 12h14"/>',
    minus: '<path d="M5 12h14"/>',
    x: '<path d="m6.5 6.5 11 11M17.5 6.5l-11 11"/>',
    check: '<path d="m5 12.5 4.5 4.5L19 7.5"/>',
    half: '<circle cx="12" cy="12" r="7"/><path d="M12 5a7 7 0 0 1 0 14Z" fill="currentColor"/>',
    ring: '<circle cx="12" cy="12" r="7"/>',
    warn: '<path d="M12 4.5 21 19.5H3Z"/><path d="M12 10v4.5M12 17v.1"/>',
    info: '<circle cx="12" cy="12" r="8.5"/><path d="M12 11v5.5M12 7.8v.1"/>',
    down: '<path d="m7 10 5 5 5-5"/>',
    right: '<path d="m10 7 5 5-5 5"/>',
    left: '<path d="m14 7-5 5 5 5"/>',
    more: '<circle cx="6" cy="12" r="1.3" fill="currentColor" stroke="none"/><circle cx="12" cy="12" r="1.3" fill="currentColor" stroke="none"/><circle cx="18" cy="12" r="1.3" fill="currentColor" stroke="none"/>',
    search: '<circle cx="11" cy="11" r="6"/><path d="m15.5 15.5 4 4"/>',
    folder: '<path d="M3.5 7.5a2 2 0 0 1 2-2h4l2 2.2h7a2 2 0 0 1 2 2v7.8a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2Z"/>',
    file: '<path d="M6.5 3.5h7l4 4v13h-11Z"/><path d="M13.5 3.5v4h4"/>',
    music: '<path d="M9 18V6l10-2v12"/><circle cx="6.5" cy="18" r="2.5"/><circle cx="16.5" cy="16" r="2.5"/>',
    lyrics: '<path d="M4 6.5h16M4 11h10M4 15.5h13M4 20h8"/>',
    layout: '<rect x="3.5" y="4.5" width="17" height="15" rx="1.5"/><path d="M3.5 14h17M12 14v5.5"/>',
    swatch: '<circle cx="12" cy="12" r="8"/><path d="M12 4a8 8 0 0 0 0 16Z" fill="currentColor"/>',
    library: '<rect x="4" y="4" width="4" height="16" rx="1"/><rect x="10" y="4" width="4" height="16" rx="1"/><path d="m16.2 5 3.8 14.5"/>',
    home: '<path d="M4 10.5 12 4l8 6.5V20h-5v-6H9v6H4Z"/>',
    sliders: '<path d="M5 6h9M18 6h1M5 12h3M12 12h7M5 18h11M20 18h-1"/><circle cx="16" cy="6" r="2"/><circle cx="10" cy="12" r="2"/><circle cx="18" cy="18" r="2"/>',
    film: '<rect x="3.5" y="5" width="17" height="14" rx="1.5"/><path d="M7.5 5v14M16.5 5v14M3.5 9.5h4M3.5 14.5h4M16.5 9.5h4M16.5 14.5h4"/>',
    download: '<path d="M12 4v11M7.5 10.5 12 15l4.5-4.5M5 19.5h14"/>',
    upload: '<path d="M12 15V4M7.5 8.5 12 4l4.5 4.5M5 19.5h14"/>',
    target: '<circle cx="12" cy="12" r="6.5"/><circle cx="12" cy="12" r="1.5" fill="currentColor"/><path d="M12 2.5v3M12 18.5v3M2.5 12h3M18.5 12h3"/>',
    headphones: '<path d="M4.5 16v-3.5a7.5 7.5 0 0 1 15 0V16"/><rect x="3.5" y="14" width="4" height="6" rx="1.2"/><rect x="16.5" y="14" width="4" height="6" rx="1.2"/>',
    split: '<path d="M12 4v16"/><path d="m7 9-3 3 3 3M17 9l3 3-3 3"/>',
    merge: '<path d="M4 12h6M14 12h6"/><path d="m7 9 3 3-3 3M17 9l-3 3 3 3"/>',
    link: '<path d="M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1"/><path d="M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1"/>',
    loop: '<path d="M17 4.5 19.5 7 17 9.5"/><path d="M4.5 12V10a3 3 0 0 1 3-3h12"/><path d="M7 19.5 4.5 17 7 14.5"/><path d="M19.5 12v2a3 3 0 0 1-3 3h-12"/>',
    stamp: '<circle cx="12" cy="12" r="7.5"/><circle cx="12" cy="12" r="3" fill="currentColor"/>',
    magnet: '<path d="M6 4v7a6 6 0 0 0 12 0V4"/><path d="M6 8h3M15 8h3"/><path d="M9 4v7a3 3 0 0 0 6 0V4"/>',
    zoomin: '<circle cx="11" cy="11" r="6"/><path d="m15.5 15.5 4 4M8.5 11h5M11 8.5v5"/>',
    zoomout: '<circle cx="11" cy="11" r="6"/><path d="m15.5 15.5 4 4M8.5 11h5"/>',
    fit: '<path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/>',
    eye: '<path d="M2.5 12S6 5.5 12 5.5 21.5 12 21.5 12 18 18.5 12 18.5 2.5 12 2.5 12Z"/><circle cx="12" cy="12" r="2.8"/>',
    gpu: '<rect x="3.5" y="7" width="17" height="10" rx="1.5"/><circle cx="9" cy="12" r="2.3"/><path d="M14 10.5h3.5M14 13.5h3.5M7 17v2.5M12 17v2.5M17 17v2.5"/>',
    restore: '<path d="M4.5 12a7.5 7.5 0 1 0 2.2-5.3L4.5 9"/><path d="M4.5 4.5V9H9"/><path d="M12 8.5V12l2.5 2"/>',
    group: '<rect x="3.5" y="3.5" width="17" height="17" rx="2" stroke-dasharray="2.5 2.5"/>',
    box: '<rect x="4.5" y="6.5" width="15" height="11" rx="1.5"/><path d="M8 10.5h8M8 13.5h5"/>',
    slot: '<rect x="4.5" y="8" width="15" height="8" rx="1.5"/>',
    rotate: '<rect x="4.5" y="5.5" width="15" height="6" rx="1.2"/><rect x="4.5" y="13" width="15" height="6" rx="1.2" opacity=".45"/>',
    leadin: '<circle cx="6" cy="12" r="1.8" fill="currentColor"/><circle cx="12" cy="12" r="1.8" fill="currentColor"/><circle cx="18" cy="12" r="1.8"/>',
    image: '<rect x="3.5" y="5" width="17" height="14" rx="1.5"/><circle cx="9" cy="10" r="1.8"/><path d="m4 17.5 5-4.5 3.5 3 3-2.5 4.5 4"/>',
    clock: '<circle cx="12" cy="12" r="8"/><path d="M12 7.5V12l3 2"/>',
    drag: '<circle cx="9" cy="7" r="1.1" fill="currentColor"/><circle cx="15" cy="7" r="1.1" fill="currentColor"/><circle cx="9" cy="12" r="1.1" fill="currentColor"/><circle cx="15" cy="12" r="1.1" fill="currentColor"/><circle cx="9" cy="17" r="1.1" fill="currentColor"/><circle cx="15" cy="17" r="1.1" fill="currentColor"/>',
    back: '<path d="M10 6 4 12l6 6M4.5 12H20"/>',
    copy: '<rect x="8" y="8" width="11.5" height="11.5" rx="1.5"/><path d="M5 15.5V5.5A1 1 0 0 1 6 4.5h9.5"/>',
    trash: '<path d="M5 7h14M9.5 7V4.5h5V7M7 7l.8 12.5h8.4L17 7"/>',
    wand: '<path d="m4 20 11-11M14 4v3M19 9h-3M18 5l-2 2"/>',
    grid: '<path d="M3.5 9h17M3.5 15h17M9 3.5v17M15 3.5v17"/>',
    lock: '<rect x="5" y="10.5" width="14" height="9.5" rx="1.5"/><path d="M8 10.5V8a4 4 0 0 1 8 0v2.5"/>',
    translate: '<path d="M4 6h9M8.5 4v2M6 6c.5 3 2.5 6 6 7.5M11 6c-.6 3.4-2.8 6.6-6.5 8.5"/><path d="m13 20 3.5-8.5L20 20M14.2 17h4.6"/>',
    spark: '<path d="M12 3.5 13.8 10l6.7 2-6.7 2L12 20.5 10.2 14l-6.7-2 6.7-2Z"/>',
    cpu: '<rect x="6.5" y="6.5" width="11" height="11" rx="1.5"/><path d="M9.5 3.5v3M14.5 3.5v3M9.5 17.5v3M14.5 17.5v3M3.5 9.5h3M3.5 14.5h3M17.5 9.5h3M17.5 14.5h3"/>',
  };
  const icon = (name, cls = '') => `<svg class="ic ${cls}" aria-hidden="true"><use href="#i-${name}"/></svg>`;
  const sprite = () =>
    `<svg xmlns="http://www.w3.org/2000/svg" style="display:none">${Object.entries(ICONS)
      .map(([k, v]) => `<symbol id="i-${k}" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round">${v}</symbol>`)
      .join('')}</svg>`;

  // ———— 片段 ————
  const rubyText = (line) =>
    line.tokens.map((k) => (k.r ? `<ruby>${esc(k.t)}<rt>${esc(k.r)}</rt></ruby>` : esc(k.t))).join('');

  /** 当前行逐字进度（0～1）。 */
  const lineProgress = (line, t) => clamp((t - line.s) / (line.e - line.s), 0, 1);
  const tokenProgress = (k, t) => clamp((t - k.s) / (k.e - k.s), 0, 1);

  const R = {};

  /** 行列表。o: { ruby, zh, active, selected:[], numbers, times, multi, from, count } */
  R.lineList = (o) => {
    const from = o.from ?? 1;
    const count = o.count ?? LINES.length;
    const sel = new Set(o.selected ?? [SONG.current]);
    return LINES.slice(from - 1, from - 1 + count)
      .map((l) => {
        const st = STATE[l.state];
        const active = l.n === (o.active ?? SONG.current);
        const p = active ? lineProgress(l, SONG.playhead) : 0;
        const text = o.ruby ? rubyText(l) : esc(l.text);
        const time = l.state === 'none' ? '未打轴' : `${fmt(l.s)}`;
        return `<div class="ll-row${sel.has(l.n) ? ' is-sel' : ''}${active ? ' is-active' : ''} st-${l.state}" role="option" aria-selected="${sel.has(l.n)}">
          ${o.multi ? `<span class="ll-check${sel.has(l.n) ? ' on' : ''}">${sel.has(l.n) ? icon('check') : ''}</span>` : ''}
          <span class="ll-n">${o.numbers === 'pad' ? pad(l.n) : l.n}</span>
          <span class="ll-body">
            <span class="ll-text${active ? ' wipe' : ''}" lang="ja" style="--p:${(p * 100).toFixed(1)}%">${text}</span>
            <span class="ll-sub">${o.zh === false ? '' : `<span class="ll-zh">${esc(l.zh)}</span>`}${o.times === false ? '' : `<span class="ll-time">${time}</span>`}</span>
          </span>
          <span class="ll-st" title="${st.label}">${icon(st.icon)}</span>
        </div>`;
      })
      .join('');
  };

  /** 文本单元编辑行：注音在上。o: { line, selected } */
  R.tokens = (o) => {
    const l = LINES[(o.line ?? SONG.current) - 1];
    const sel = o.selected ?? SONG.token;
    return `<div class="tk-row" lang="ja">${l.tokens
      .map((k, i) => {
        const p = tokenProgress(k, SONG.playhead);
        return `<span class="tk${i === sel ? ' is-sel' : ''}${p >= 1 ? ' is-sung' : p > 0 ? ' is-singing' : ''}" style="--p:${(p * 100).toFixed(0)}%"><span class="tk-r">${k.r ? esc(k.r) : '&nbsp;'}</span><span class="tk-t">${esc(k.t)}</span><span class="tk-d">${(k.e - k.s).toFixed(2)}</span></span>`;
      })
      .join('')}</div>`;
  };

  /** 逐字预览：上一行、当前行（逐字填充）、翻译、下一行。 */
  R.wordPreview = (o) => {
    const t = SONG.playhead;
    const cur = LINES[SONG.current - 1];
    const prev = LINES[SONG.current - 2];
    const next = LINES[SONG.current];
    const cur2 = cur.tokens
      .map((k) => {
        const p = tokenProgress(k, t);
        const inner = k.r ? `<ruby>${esc(k.t)}<rt>${esc(k.r)}</rt></ruby>` : esc(k.t);
        return `<span class="wp-k wipe" style="--p:${(p * 100).toFixed(0)}%">${inner}</span>`;
      })
      .join('');
    return `<div class="wp" lang="ja">
      <div class="wp-line is-prev">${rubyText(prev)}</div>
      <div class="wp-line is-cur">${cur2}</div>
      ${o?.zh === false ? '' : `<div class="wp-zh" lang="zh">${esc(cur.zh)}</div>`}
      <div class="wp-line is-next">${rubyText(next)}</div>
      <div class="wp-line is-next2">${rubyText(LINES[SONG.current + 1])}</div>
    </div>`;
  };

  /** 波形：确定性的伪随机振幅，歌唱段更强；播放头之前的部分单独一条路径。 */
  const wave = (from, to, ph, bars = 360) => {
    const r = rng(7);
    let a = '';
    let b = '';
    const w = 1000 / bars;
    for (let i = 0; i < bars; i++) {
      const t = from + ((to - from) * (i + 0.5)) / bars;
      const singing = LINES.some((l) => l.state !== 'none' && t >= l.s && t <= l.e) ? 1 : 0.45;
      const beat = 0.55 + 0.45 * Math.abs(Math.sin(t * Math.PI * 2.1));
      const v = clamp((0.18 + r() * 0.62) * singing * beat + r() * 0.08, 0.04, 0.96);
      const h = v * 46;
      const d = `M${(i * w + w * 0.18).toFixed(2)} ${(50 - h).toFixed(2)}h${(w * 0.64).toFixed(2)}v${(h * 2).toFixed(2)}h-${(w * 0.64).toFixed(2)}z`;
      if (ph != null && t < ph) b += d;
      else a += d;
    }
    return `<svg class="wv" viewBox="0 0 1000 100" preserveAspectRatio="none"><path class="wv-a" d="${a}"/><path class="wv-b" d="${b}"/></svg>`;
  };
  R.wave = (o) => wave(o.from ?? 0, o.to ?? 20, o.playhead ?? null, o.bars ?? 160);

  const CLIPS = [
    { name: '前段', from: 1, to: 6, c: 'c1' },
    { name: '副歌', from: 7, to: 8, c: 'c2' },
    { name: '前段', from: 9, to: 12, c: 'c1' },
  ];
  const LEADINS = [
    { s: 0, e: 2.1, label: '三点 · 第 1 句' },
    { s: 41.9, e: 44.0, label: '三点 · 第 9 句' },
  ];

  /** 时间轴。o: { from, to, playhead, tracks:[…], heads:true, labels:{}, step, sel:{line,token} } */
  R.timeline = (o) => {
    const from = o.from ?? 10;
    const to = o.to ?? 26;
    const ph = o.playhead ?? SONG.playhead;
    const p = (t) => pct(t, from, to);
    const w = (a, b) => pct(from + (b - a), from, to);
    const inView = (s, e) => e > from && s < to;
    const selLine = o.sel?.line ?? SONG.current;
    const selTok = o.sel?.token ?? SONG.token;
    const names = { ruler: '', wave: '波形', tokens: '文本单元', ruby: '注音', lines: '行', trans: '翻译', clips: '片段', leadins: '前奏', ...(o.labels ?? {}) };
    const step = o.step ?? (to - from > 30 ? 5 : to - from > 12 ? 1 : 0.5);
    const lane = (kind) => {
      switch (kind) {
        case 'ruler': {
          let h = '';
          const first = Math.ceil(from / step) * step;
          for (let t = first; t <= to + 1e-6; t += step) {
            const major = Math.abs(t / (step * 2) - Math.round(t / (step * 2))) < 1e-6;
            h += `<span class="tl-tick${major ? ' is-major' : ''}" style="left:${p(t)}">${major ? `<b>${fmt(t, false)}</b>` : ''}</span>`;
          }
          return h;
        }
        case 'wave':
          return wave(from, to, ph, o.bars ?? 420);
        case 'tokens':
          return LINES.filter((l) => l.state !== 'none' && inView(l.s, l.e))
            .flatMap((l) =>
              l.tokens.map((k, i) => {
                const pr = tokenProgress(k, ph);
                const s = l.n === selLine && i === selTok;
                return `<span class="tl-item tl-tok${s ? ' is-sel' : ''}${l.n === selLine ? ' in-line' : ''}${l.state === 'est' ? ' is-est' : ''}" style="left:${p(k.s)};width:${w(k.s, k.e)};--p:${(pr * 100).toFixed(0)}%" lang="ja">${esc(k.t)}</span>`;
              }),
            )
            .join('');
        case 'ruby':
          return LINES.filter((l) => l.state !== 'none' && inView(l.s, l.e))
            .flatMap((l) => l.tokens.filter((k) => k.r).map((k) => `<span class="tl-item tl-rb" style="left:${p(k.s)};width:${w(k.s, k.e)}" lang="ja">${esc(k.r)}</span>`))
            .join('');
        case 'lines':
          return LINES.filter((l) => inView(l.s, l.e))
            .map((l) => `<span class="tl-item tl-line${l.n === selLine ? ' is-sel' : ''} st-${l.state}" style="left:${p(l.s)};width:${w(l.s, l.e)};--p:${(lineProgress(l, ph) * 100).toFixed(0)}%" lang="ja"><i>${l.n}</i>${esc(l.text)}</span>`)
            .join('');
        case 'trans':
          return LINES.filter((l) => inView(l.s, l.e))
            .map((l) => `<span class="tl-item tl-tr" style="left:${p(l.s)};width:${w(l.s, l.e)}">${esc(l.zh)}</span>`)
            .join('');
        case 'clips':
          return CLIPS.map((c) => {
            const s = LINES[c.from - 1].s;
            const e = LINES[c.to - 1].e;
            return inView(s, e) ? `<span class="tl-item tl-clip ${c.c}${c.name === '副歌' ? ' is-sel' : ''}" style="left:${p(s)};width:${w(s, e)}"><i>${esc(c.name)}</i>${c.from}–${c.to} 句</span>` : '';
          }).join('');
        case 'leadins':
          return LEADINS.filter((x) => inView(x.s, x.e))
            .map((x) => `<span class="tl-item tl-lead" style="left:${p(x.s)};width:${w(x.s, x.e)}">${icon('leadin')}${esc(x.label)}</span>`)
            .join('');
        default:
          return '';
      }
    };
    const tracks = o.tracks ?? ['ruler', 'wave', 'tokens', 'ruby', 'lines'];
    const phOn = ph > from && ph < to;
    return `<div class="tl${o.heads === false ? ' no-heads' : ''}">${tracks
      .map(
        (k) => `<div class="tl-row tl-${k}">${o.heads === false ? '' : `<div class="tl-head">${names[k] ?? ''}</div>`}<div class="tl-lane">${lane(k)}${phOn ? `<span class="tl-ph${k === 'ruler' ? ' is-cap' : ''}" style="left:${p(ph)}"></span>` : ''}</div></div>`,
      )
      .join('')}</div>`;
  };

  /** 渲染画面（工程内容，不随界面主题变）。o: { mode:'final'|'layout', small, guides } */
  R.frame = (o) => {
    const t = SONG.playhead;
    const cur = LINES[SONG.current - 1];
    const next = LINES[SONG.current];
    const k = (l, live) =>
      l.tokens
        .map((x) => {
          const pr = live ? tokenProgress(x, t) : 0;
          const inner = x.r ? `<ruby>${esc(x.t)}<rt>${esc(x.r)}</rt></ruby>` : esc(x.t);
          return `<span class="fk" style="--p:${(pr * 100).toFixed(0)}%">${inner}</span>`;
        })
        .join('');
    if (o.mode === 'layout') {
      return `<div class="frame is-layout${o.small ? ' is-small' : ''}" lang="ja"><div class="fbg"></div>
        <div class="fbox b-left is-sel"><span class="fbox-tag">前段 · 槽位 1 左侧</span><span class="fbox-txt">朝の光が窓を染めて</span><i class="h tl"></i><i class="h tr"></i><i class="h bl"></i><i class="h br"></i></div>
        <div class="fbox b-right"><span class="fbox-tag">前段 · 槽位 2 右侧</span><span class="fbox-txt">君の声がまだ耳に残る</span></div>
        <div class="fbox b-center is-dim"><span class="fbox-tag">副歌 · 自由</span><span class="fbox-txt">いつかまた会えると信じて</span></div>
        <div class="fbox b-trans"><span class="fbox-tag">翻译</span><span class="fbox-txt" lang="zh">晨光染上了窗</span></div>
        <div class="fbox b-lead"><span class="fbox-tag">三点提示</span><span class="fbox-txt">● ● ●</span></div>
        <div class="fsafe"></div>
      </div>`;
    }
    if (o.mode === 'end') return `<div class="frame${o.small ? ' is-small' : ''}"><div class="fbg"></div></div>`;
    if (o.mode === 'first') {
      return `<div class="frame${o.small ? ' is-small' : ''}" lang="ja"><div class="fbg"></div>
        <div class="fl fl-lead">● ● ●</div>
        <div class="fl fl-1">${k(LINES[0], false)}</div>
        <div class="fl fl-2">${k(LINES[1], false)}</div>
      </div>`;
    }
    return `<div class="frame${o.small ? ' is-small' : ''}" lang="ja"><div class="fbg"></div>
      <div class="fl fl-1">${k(cur, true)}</div>
      <div class="fl fl-2">${k(next, false)}</div>
      <div class="fl fl-zh" lang="zh">${esc(cur.zh)}</div>
    </div>`;
  };

  /** 工程目录与会话（首页）。 */
  const PROJECTS = [
    { dir: true, name: 'archive', note: '3 个工程' },
    { name: '黎明站台', path: 'dawn-platform', time: '今天 19:42', ver: '0.109.0', open: true },
    { name: '夏祭りの夜', path: 'summer-night', time: '10-30 22:10', ver: '0.109.0' },
    { name: '春雨（翻唱）', path: 'spring-rain-cover', time: '10-21 14:03', ver: '0.108.0' },
    { name: '星屑ライン', path: 'stardust-line', time: '10-12 23:37', ver: '0.106.2' },
    { name: '测试工程', path: 'scratch', time: '10-18 09:51', ver: '0.108.0' },
    { name: '旧工程', path: 'old-song', time: '09-01 10:00', ver: '0.75.1', bad: '版本不兼容' },
  ];
  R.projectRows = (o) =>
    PROJECTS.map((p) =>
      p.dir
        ? `<div class="pr-row is-dir"><span class="pr-ic">${icon('folder')}</span><span class="pr-name">${p.name}</span><span class="pr-path"></span><span class="pr-time">${p.note}</span><span class="pr-ver"></span><span class="pr-act">${icon('right')}</span></div>`
        : `<div class="pr-row${p.bad ? ' is-bad' : ''}${p.open && o?.highlight !== false ? ' is-open' : ''}"><span class="pr-ic">${icon('music')}</span><span class="pr-name" lang="ja">${esc(p.name)}</span><span class="pr-path">${p.path}</span><span class="pr-time">${p.time}</span><span class="pr-ver">${p.bad ? `<em>${p.bad}</em>` : p.ver}</span><span class="pr-act">${p.bad ? '' : `<button class="btn ghost sm">打开</button>`}<button class="btn icon sm" aria-label="更多">${icon('more')}</button></span></div>`,
    ).join('');

  // ———— 页面切换（只为浏览设计稿） ————
  const SCREENS = [
    ['home', '开屏'],
    ['new', '新建工程'],
    ['lyrics', '歌词'],
    ['orchestration', '编排'],
    ['export', '导出'],
  ];
  const DRAFTS = [
    ['a-dark-stage.html', 'A 暗场'],
    ['b-songbook.html', 'B 歌本'],
    ['c-desk-stage.html', 'C 台前幕后'],
  ];
  const show = () => {
    const id = location.hash.slice(1) || 'home';
    document.querySelectorAll('.screen').forEach((s) => {
      const under = document.getElementById(id)?.dataset.under;
      s.classList.toggle('is-on', s.id === id || s.id === under);
    });
    document.querySelectorAll('.draft-nav a[data-s]').forEach((a) => a.classList.toggle('on', a.dataset.s === id));
  };

  const render = () => {
    document.body.insertAdjacentHTML('afterbegin', sprite());
    document.querySelectorAll('[data-r]').forEach((el) => {
      const o = el.dataset.o ? JSON.parse(el.dataset.o) : {};
      el.innerHTML = R[el.dataset.r](o);
    });
    document.querySelectorAll('[data-i]').forEach((el) => el.insertAdjacentHTML('afterbegin', icon(el.dataset.i)));
    if (!new URLSearchParams(location.search).has('shot')) {
      const here = location.pathname.split('/').pop();
      document.body.insertAdjacentHTML(
        'beforeend',
        `<nav class="draft-nav">${DRAFTS.map(([f, n]) => `<a href="${f}${location.hash}" class="d${f === here ? ' cur' : ''}">${n}</a>`).join('')}<span class="sep"></span>${SCREENS.map(([id, n]) => `<a href="#${id}" data-s="${id}">${n}</a>`).join('')}<span class="sep"></span><a href="index.html" class="d">总览</a></nav>`,
      );
    }
    show();
  };
  addEventListener('hashchange', show);
  document.readyState === 'loading' ? addEventListener('DOMContentLoaded', render) : render();

  window.Mock = { LINES, SONG, fmt, icon, R };
})();
