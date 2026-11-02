// 字体选择器与字体检查（W7 设计 §2.5）：在后端字体目录里搜索选择；目录里没有的字体照常显示并标出。
// 字体检查用试样文字加一段自己的文字调用 fonts/inspect，逐段列出实际字体，回退的标黄，缺字的标红。

import { useEffect, useState } from 'react';
import type { FontInspectResponse, StyleTypography } from '../../protocol';
import { catalogName, fontCheckText, fontRuns, hasFamily, markText, MAX_FALLBACKS, MAX_FAMILY_LENGTH, searchFamilies } from '../model/fonts';
import { client, fonts } from '../connection';
import { errorText } from '../session/editor-session';
import { Button, IconButton } from '../ui/Button';
import { cls } from '../ui/cls';
import { TextField } from '../ui/fields';
import { Icon } from '../ui/Icon';
import { useObservable } from '../ui/hooks';
import { Note } from '../workspace/panel';

function useFontCatalog() {
  const state = useObservable(fonts.state$);
  useEffect(() => fonts.ensure(), [state.status]);
  return state;
}

/**
 * 可以搜索的字体输入：聚焦时在下面列出目录里的字体，输入时筛选；点一项或按 Enter 提交，Esc 撤回。
 * clearOnCommit 时提交后清空（“添加回退字体”）。标签只作可访问名称，框很紧凑（放在外观页签的一行里）。
 */
export function FontPicker(props: {
  label: string;
  value: string | null;
  onCommit: (family: string | null) => void;
  disabled: boolean;
  placeholder?: string;
  helper?: string;
  clearOnCommit?: boolean;
  className?: string;
}) {
  const catalog = useFontCatalog();
  const shown = props.value ?? '';
  const [text, setText] = useState(shown);
  const [open, setOpen] = useState(false);
  useEffect(() => setText(shown), [shown]);
  const error = text.trim().length > MAX_FAMILY_LENGTH ? `最多 ${MAX_FAMILY_LENGTH} 个字符。` : undefined;
  const commit = (value: string) => {
    setOpen(false);
    if (value.trim().length > MAX_FAMILY_LENGTH) return;
    const name = value.trim() ? catalogName(catalog.families, value) : '';
    if (props.clearOnCommit) {
      setText('');
      if (name) props.onCommit(name);
      return;
    }
    setText(name);
    if ((name || null) !== props.value) props.onCommit(name || null);
  };
  // 显示的是当前值时列出整个目录，改了文字再按文字筛选。
  const { items, total } = searchFamilies(catalog.families, text === shown ? '' : text);
  const missing = !props.clearOnCommit && !!props.value && catalog.status === 'ready' && !hasFamily(catalog.families, props.value);

  return (
    <div
      className={cls('font-picker', props.className)}
      onFocus={() => setOpen(true)}
      onBlur={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) commit(text);
      }}
    >
      <TextField
        label={props.label}
        labelPlacement="none"
        size="sm"
        value={text}
        onChange={(v) => {
          setText(v);
          setOpen(true);
        }}
        onEnter={() => commit(text)}
        onEscape={() => {
          setText(shown);
          setOpen(false);
        }}
        placeholder={props.placeholder ?? ''}
        {...(props.helper ? { helper: props.helper } : {})}
        error={error}
        disabled={props.disabled}
      />
      {missing && <p className="font-missing">后端没有这个字体，会用回退字体或默认字体。</p>}
      {open && !props.disabled && (
        <div className="font-options" role="listbox" aria-label={`${props.label}的候选`}>
          {catalog.status === 'loading' && <p className="font-options-note">正在读取字体目录……</p>}
          {catalog.status === 'failed' && (
            <p className="font-options-note err">
              读不到字体目录：{catalog.error}
              <Button variant="text" size="sm" onPress={() => fonts.retry()}>
                重试
              </Button>
            </p>
          )}
          {catalog.status === 'ready' && items.length === 0 && <p className="font-options-note">目录里没有“{text.trim()}”。按 Enter 仍然使用它。</p>}
          {items.map((family) => (
            <div
              key={family}
              role="option"
              aria-selected={family === props.value}
              className={family === props.value ? 'font-option selected' : 'font-option'}
              data-family={family}
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => commit(family)}
            >
              {family}
            </div>
          ))}
          {total > items.length && <p className="font-options-note">还有 {total - items.length} 个，继续输入以筛选。</p>}
        </div>
      )}
    </div>
  );
}

/** 回退字体：按顺序排成一排小块，可以移除；最后一块是“回退字体”输入框，选一个或输入名称后按 Enter 添加。目录里没有的标出。 */
export function FallbackFonts(props: { names: readonly string[]; onChange: (names: string[]) => void; disabled: boolean }) {
  const catalog = useFontCatalog();
  const { names } = props;
  const full = names.length >= MAX_FALLBACKS;
  return (
    <div className="font-fallbacks chips" role="group" aria-label="回退字体（按顺序尝试）">
      {names.map((name) => {
        const missing = catalog.status === 'ready' && !hasFamily(catalog.families, name);
        return (
          <span key={name} className={cls('chip', 'font-chip', missing && 'missing')} data-family={name} title={missing ? '后端没有这个字体' : '回退字体'}>
            {missing && <Icon name="warning" />}
            {name}
            <IconButton icon="close" size="sm" label={`移除回退字体 ${name}`} isDisabled={props.disabled} onPress={() => props.onChange(names.filter((n) => n !== name))} tooltip={false} />
          </span>
        );
      })}
      <FontPicker
        label="添加回退字体"
        className="font-fallback-add"
        value={null}
        clearOnCommit
        placeholder={full ? `最多 ${MAX_FALLBACKS} 个回退字体` : '＋ 回退字体'}
        onCommit={(family) => {
          if (family && !names.some((n) => n.toLowerCase() === family.toLowerCase())) props.onChange([...names, family]);
        }}
        disabled={props.disabled || full}
      />
    </div>
  );
}

type CheckState =
  | { status: 'idle' }
  | { status: 'checking'; key: string }
  | { status: 'done'; key: string; text: string; result: FontInspectResponse }
  | { status: 'failed'; key: string; message: string };

/** 字体检查：试样文字加上自己输入的一段，按当前字体设置检查。字体设置改了以后旧结果标为过期。 */
export function FontCheck(props: { typography: StyleTypography }) {
  const t = props.typography;
  const [extra, setExtra] = useState('');
  const [state, setState] = useState<CheckState>({ status: 'idle' });
  const input = {
    text: fontCheckText(extra),
    family: t.fontFamilyName,
    fontSizePx: t.fontSizePx,
    fontWeight: t.fontWeight,
    italic: t.isItalic,
    fallbackFamilies: t.fallbackFamilyNames.length > 0 ? t.fallbackFamilyNames : null,
    sessionId: null,
    expectedRevision: null,
  };
  const key = JSON.stringify(input);
  const run = async () => {
    setState({ status: 'checking', key });
    try {
      const result = await client.fonts.inspect(input);
      setState((s) => (s.status === 'checking' && s.key === key ? { status: 'done', key, text: input.text, result } : s));
    } catch (error) {
      setState((s) => (s.status === 'checking' && s.key === key ? { status: 'failed', key, message: errorText(error) } : s));
    }
  };
  const stale = state.status !== 'idle' && state.key !== key;

  return (
    <div className="font-check" data-status={state.status} data-stale={stale}>
      <div className="font-check-input">
        <TextField
          label="再检查一段文字"
          labelPlacement="none"
          size="sm"
          value={extra}
          onChange={setExtra}
          onEnter={() => void run()}
          placeholder="再加一段文字，例如歌词里的生僻字"
        />
        <Button variant="tonal" size="sm" icon="font_download" isPending={state.status === 'checking'} onPress={() => void run()}>
          检查
        </Button>
      </div>
      <p className="font-check-help">用试样文字加上这一段，按当前字体设置检查实际用到的字体。</p>
      {stale && <Note>字体设置或文字改过了，结果可能已经不对，再检查一次。</Note>}
      {state.status === 'failed' && <Note error>字体检查失败：{state.message}</Note>}
      {state.status === 'done' && <FontCheckResult text={state.text} result={state.result} family={t.fontFamilyName} weight={t.fontWeight} />}
    </div>
  );
}

function FontCheckResult(props: { text: string; result: FontInspectResponse; family: string | null; weight: number }) {
  const { result } = props;
  const runs = fontRuns(result.elements);
  const marked = markText(props.text, result.elements);
  const fallback = runs.filter((r) => r.isFallback && !r.hasMissingGlyph).length;
  const missing = runs.filter((r) => r.hasMissingGlyph).length;
  return (
    <div className="font-check-result">
      <p className={cls('font-check-summary', !result.requestedAvailable && 'err')}>
        {result.requestedAvailable
          ? `主字体：${result.resolvedFamily}`
          : `后端没有字体“${props.family ?? ''}”，实际用的是 ${result.resolvedFamily}`}
        {' · '}可用字重 {result.availableWeights.join('、') || '无'}
        {result.availableWeights.length > 0 && !result.availableWeights.includes(props.weight) ? `（没有 ${props.weight}，会用最接近的）` : ''}
        {' · '}
        {result.supportsItalic ? '有斜体字形' : '没有斜体字形'}
      </p>
      <p className="font-marked" aria-label="逐字结果">
        {marked.map((m, i) => (
          <span key={i} className={m.status === 'main' ? undefined : m.status} title={m.family}>
            {m.text}
          </span>
        ))}
      </p>
      {runs.length > 0 && (
        <table className="font-runs">
          <tbody>
            {runs.map((r, i) => (
              <tr key={i} className={r.hasMissingGlyph ? 'missing' : r.isFallback ? 'fallback' : undefined}>
                <td className="font-run-text">{r.text}</td>
                <td>{r.family}</td>
                <td>{r.hasMissingGlyph ? '缺字' : r.isFallback ? '回退' : ''}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <p className="font-check-help">
        {missing > 0 ? `${missing} 段缺字（红），` : ''}
        {fallback > 0 ? `${fallback} 段用了回退字体（黄），` : ''}
        {missing === 0 && fallback === 0 ? `所有文字都用 ${result.resolvedFamily} 显示。` : `其余用 ${result.resolvedFamily}。`}环境：{result.environment}
      </p>
    </div>
  );
}
