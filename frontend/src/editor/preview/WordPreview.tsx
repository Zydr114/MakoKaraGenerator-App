import { memo, useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { AudioController } from '../audio/audio-controller';
import { isTimed, type EditorLine } from '../model/lyrics';
import { formatTime } from '../model/time';
import { fillRatio, previewSegments, previewWindow, windowKey, type TimedLine } from '../model/word-preview';

/**
 * 逐字预览（W2 设计 §2.4，整体设计 §4.3）：当前时刻的上一行、当前行、关联翻译和下一行。
 * 纯文字，不含任何样式。换行时才重新渲染；每帧只改各段的 --fill 和时间标签。
 */
export const WordPreview = memo(function WordPreview(props: {
  audio: AudioController | null;
  lines: readonly EditorLine[];
  translations: ReadonlyMap<string, readonly EditorLine[]>;
  onCollapse: () => void;
}) {
  const { audio } = props;
  const root = useRef<HTMLDivElement>(null);
  const clock = useRef<HTMLSpanElement>(null);
  const nowUs = () => audio?.playheadUs() ?? 0;
  const view = previewWindow(props.lines, nowUs());
  const [, setKey] = useState(() => windowKey(view));
  const lines = useRef(props.lines);
  lines.current = props.lines;

  // 渲染后立即按当前播放头上色，避免换行时闪一帧空白。
  useLayoutEffect(() => paint(root.current, nowUs()));

  useEffect(() => {
    let frame = 0;
    let lastUs = Number.NaN;
    let lastLines = lines.current;
    const loop = () => {
      frame = requestAnimationFrame(loop);
      const us = audio?.playheadUs() ?? 0;
      if (us === lastUs && lines.current === lastLines) return;
      lastUs = us;
      lastLines = lines.current;
      if (clock.current) clock.current.textContent = formatTime(us);
      setKey(windowKey(previewWindow(lines.current, us)));
      paint(root.current, us);
    };
    frame = requestAnimationFrame(loop);
    return () => cancelAnimationFrame(frame);
  }, [audio]);

  const hasTimed = props.lines.some(isTimed);
  return (
    <div className="word-preview">
      <header className="pane-header">
        <span className="pane-title-text">
          逐字预览 · <span ref={clock}>{formatTime(nowUs())}</span>
        </span>
        <mdui-button-icon aria-label="收起逐字预览" onClick={props.onCollapse}>
          <mdui-icon-chevron-right />
        </mdui-button-icon>
      </header>
      <div className="word-preview-body" ref={root}>
        {!hasTimed && <p className="muted">还没有打轴的行。打轴后，这里会按播放头逐字显示。</p>}
        {view.previous && <PreviewLine line={view.previous} role="previous" />}
        {view.current && <PreviewLine line={view.current} role={view.active ? 'current' : 'upcoming'} translations={props.translations.get(view.current.id)} />}
        {view.next && <PreviewLine line={view.next} role="next" />}
      </div>
    </div>
  );
});

function PreviewLine(props: { line: TimedLine; role: 'previous' | 'current' | 'upcoming' | 'next'; translations?: readonly EditorLine[] | undefined }) {
  const { line } = props;
  return (
    <div className={`wp-line wp-${props.role}`} data-line-id={line.id}>
      <div className="wp-text">
        {previewSegments(line).map((segment, index) => (
          <span
            key={segment.token?.id ?? `gap-${index}`}
            className={`wp-seg${segment.token && !isTimed(segment.token) ? ' untimed' : ''}`}
            data-s={segment.fillStartUs}
            data-e={segment.fillEndUs}
          >
            {segment.ruby && (
              // 注音按片段各自填充（W6 设计 §2.5）。
              <span className="wp-ruby">
                {segment.ruby.map((r, i) => (
                  <span key={i} className={`wp-rseg${r.timed ? ' timed' : ''}`} data-s={r.fillStartUs} data-e={r.fillEndUs}>
                    {r.text}
                  </span>
                ))}
              </span>
            )}
            <span className="wp-base">{segment.text}</span>
          </span>
        ))}
      </div>
      {props.translations?.map((t) => (
        <div key={t.id} className="wp-translation">
          {t.text}
        </div>
      ))}
    </div>
  );
}

function paint(root: HTMLElement | null, us: number): void {
  if (!root) return;
  for (const el of root.querySelectorAll<HTMLElement>('.wp-seg, .wp-rseg')) {
    el.style.setProperty('--fill', String(fillRatio(us, Number(el.dataset.s), Number(el.dataset.e))));
  }
}
