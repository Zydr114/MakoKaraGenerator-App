import { useEffect, useLayoutEffect, useMemo, useRef, useState, type CSSProperties, type KeyboardEvent } from 'react';
import type { EditorLine, EditorToken } from '../model/lyrics';
import { rubyOf } from '../model/ruby-segments';
import { containsKanji } from '../model/token-semantics';
import { formatRubySegments } from '../phonetics/mora-splitter';
import { previewRomajiInput } from '../phonetics/romaji-preview';
import { Button, IconButton } from '../ui/Button';

export interface RubyPopoverAnchor {
  lineId: string;
  tokenId: string;
  /** 目标词元的 DOM 元素引用，用于紧贴词元正上方定位。 */
  element: HTMLElement | null;
}

/**
 * F2 / 双击字块 —— 原地悬浮注音框（Phase 3 §5.2）：
 * - 紧贴目标字块正上方弹出，多拍注音以 `・` 分隔展示；
 * - 实时展示 wanakana 罗马音预览徽标 `→ きょ・う`（Enter 采纳假名，Shift+Enter 原样提交拉丁字母/中文拼音）；
 * - 连续键盘流：Tab / Shift+Tab，或光标位于行首按 ← / 位于行尾按 →，自动保存并跳至相邻实义字继续注音；
 * - 提交调用后端非破坏性 `lyrics.setRubySegments`，按段序号保留已有拍时间。
 */
export function RubyInlinePopover(props: {
  anchor: RubyPopoverAnchor | null;
  line: EditorLine | undefined;
  token: EditorToken | undefined;
  mergeLongAndNasalInKanji: boolean;
  onCommit: (line: EditorLine, token: EditorToken, segments: string[]) => Promise<void>;
  onNavigateToken: (delta: -1 | 1) => void;
  onClose: () => void;
}) {
  const { anchor, line, token, mergeLongAndNasalInKanji, onCommit, onNavigateToken, onClose } = props;
  const existingRuby = line && token ? rubyOf(line, token.id) : undefined;
  const initialValue = existingRuby ? formatRubySegments(existingRuby.segments.map((s) => s.text)) : '';
  const [value, setValue] = useState(initialValue);
  const [busy, setBusy] = useState(false);
  const [coords, setCoords] = useState<{ left: number; top: number; below: boolean } | null>(null);
  const popRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  // Sync draft when target token changes (including Tab/Arrow jump!).
  useEffect(() => {
    setValue(initialValue);
    // Focus and select all text so user can immediately overtype.
    requestAnimationFrame(() => {
      const el = inputRef.current;
      if (el) {
        el.focus();
        el.select();
      }
    });
  }, [anchor?.lineId, anchor?.tokenId, initialValue]);

  // Compute anchored fixed viewport position right above target token element.
  useLayoutEffect(() => {
    if (!anchor) {
      setCoords(null);
      return;
    }
    const updatePosition = () => {
      const targetEl = anchor.element;
      if (!targetEl || !targetEl.isConnected) return;
      const rect = targetEl.getBoundingClientRect();
      const popWidth = popRef.current?.offsetWidth ?? 300;
      const popHeight = popRef.current?.offsetHeight ?? 116;
      const margin = 12;
      const centerX = rect.left + rect.width / 2;
      const clampedLeft = Math.max(margin + popWidth / 2, Math.min(window.innerWidth - margin - popWidth / 2, centerX));
      const below = rect.top < popHeight + 18;
      const top = below ? rect.bottom + 8 : rect.top - 8;
      setCoords({ left: clampedLeft, top, below });
    };
    updatePosition();
    window.addEventListener('resize', updatePosition);
    window.addEventListener('scroll', updatePosition, true);
    return () => {
      window.removeEventListener('resize', updatePosition);
      window.removeEventListener('scroll', updatePosition, true);
    };
  }, [anchor]);

  // Click outside closes popover without overwriting unsaved edits.
  useEffect(() => {
    if (!anchor) return;
    const onPointerDown = (event: MouseEvent) => {
      const target = event.target as Node | null;
      if (!target) return;
      if (popRef.current?.contains(target)) return;
      if (anchor.element?.contains(target)) return;
      onClose();
    };
    window.addEventListener('mousedown', onPointerDown, true);
    return () => window.removeEventListener('mousedown', onPointerDown, true);
  }, [anchor, onClose]);

  const preview = useMemo(
    () =>
      previewRomajiInput(value, {
        mergeLongAndNasalInKanji,
        isKanjiBase: token ? containsKanji(token.text) : true,
      }),
    [value, mergeLongAndNasalInKanji, token],
  );

  if (!anchor || !line || !token) return null;

  const isDirty = value.trim() !== initialValue.trim();

  const submitSegments = async (segments: string[], closeAfter: boolean) => {
    if (busy) return;
    setBusy(true);
    try {
      await onCommit(line, token, segments);
      if (closeAfter) onClose();
    } finally {
      setBusy(false);
    }
  };

  const commitAndJump = async (delta: -1 | 1, useRaw = false) => {
    if (busy) return;
    if (isDirty) {
      const segs = value.trim() === '' ? [] : useRaw ? preview.rawSegments : preview.kanaSegments;
      await submitSegments(segs, false);
    }
    onNavigateToken(delta);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.nativeEvent.isComposing) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      onClose();
      return;
    }
    if (event.key === 'Enter') {
      event.preventDefault();
      event.stopPropagation();
      const segs = value.trim() === '' ? [] : event.shiftKey ? preview.rawSegments : preview.kanaSegments;
      void submitSegments(segs, true);
      return;
    }
    if (event.key === 'Tab') {
      event.preventDefault();
      event.stopPropagation();
      void commitAndJump(event.shiftKey ? -1 : 1, false);
      return;
    }
    if (event.key === 'ArrowLeft' && !event.altKey && !event.ctrlKey && !event.metaKey) {
      const input = event.currentTarget;
      if (input.selectionStart === 0 && input.selectionEnd === 0) {
        event.preventDefault();
        event.stopPropagation();
        void commitAndJump(-1, false);
      }
      return;
    }
    if (event.key === 'ArrowRight' && !event.altKey && !event.ctrlKey && !event.metaKey) {
      const input = event.currentTarget;
      if (input.selectionStart === input.value.length && input.selectionEnd === input.value.length) {
        event.preventDefault();
        event.stopPropagation();
        void commitAndJump(1, false);
      }
    }
  };

  const style: CSSProperties = coords
    ? {
        left: `${coords.left}px`,
        top: `${coords.top}px`,
        transform: coords.below ? 'translate(-50%, 0)' : 'translate(-50%, -100%)',
      }
    : {
        left: '50%',
        top: '28%',
        transform: 'translate(-50%, -50%)',
      };

  return (
    <div
      ref={popRef}
      className="ruby-inline-popover"
      role="dialog"
      aria-label={`编辑「${token.text}」的注音`}
      style={style}
      data-below={coords?.below ? '1' : undefined}
    >
      <div className="rip-head">
        <span className="rip-base" lang="ja">
          {token.text}
        </span>
        <span className="rip-badge">
          {preview.kanaSegments.length > 0 ? `${preview.kanaSegments.length} 拍` : '无注音'}
        </span>
        <span className="spacer" />
        <IconButton icon="chevron_left" label="上一个实义字 (Shift+Tab)" size="sm" isDisabled={busy} onPress={() => void commitAndJump(-1)} />
        <IconButton icon="chevron_right" label="下一个实义字 (Tab)" size="sm" isDisabled={busy} onPress={() => void commitAndJump(1)} />
        <IconButton icon="close" label="关闭 (Esc)" size="sm" isDisabled={busy} onPress={onClose} />
      </div>

      <div className="rip-input-row">
        <input
          ref={inputRef}
          type="text"
          className="rip-input"
          value={value}
          disabled={busy}
          placeholder="输入假名/罗马音/拼音，・或 , 分拍，留空清除"
          aria-label={`「${token.text}」注音输入`}
          onChange={(e) => setValue(e.target.value)}
          onKeyDown={onKeyDown}
        />
      </div>

      {preview.hasLatin && preview.previewLabel ? (
        <div className="rip-preview romaji">
          <span className="rip-preview-kana" lang="ja">
            → {preview.previewLabel}
          </span>
          <span className="rip-preview-hint">Enter 转假名 · Shift+Enter 保留原文/拼音</span>
        </div>
      ) : preview.kanaSegments.length > 0 ? (
        <div className="rip-preview chips" aria-label="分拍预览">
          {preview.kanaSegments.map((seg, i) => (
            <span key={i} className="rip-chip" lang="ja">
              {seg}
            </span>
          ))}
          <span className="spacer" />
          <span className="rip-preview-hint">Tab / ←→ 连续跳字</span>
        </div>
      ) : (
        <div className="rip-preview empty">
          <span className="rip-preview-hint">留空按 Enter 清除注音 · Tab 跳至下一字</span>
        </div>
      )}

      <div className="rip-actions">
        {existingRuby && (
          <Button variant="text" size="sm" danger isDisabled={busy} onPress={() => void submitSegments([], true)}>
            清除
          </Button>
        )}
        <span className="spacer" />
        {preview.hasLatin && (
          <Button variant="outlined" size="sm" isDisabled={busy} onPress={() => void submitSegments(preview.rawSegments, true)}>
            保留原文 (Shift+↵)
          </Button>
        )}
        <Button
          variant="filled"
          size="sm"
          isDisabled={busy}
          onPress={() => void submitSegments(value.trim() === '' ? [] : preview.kanaSegments, true)}
        >
          应用 (↵)
        </Button>
      </div>
    </div>
  );
}
