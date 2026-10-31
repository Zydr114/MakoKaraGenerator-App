import { useEffect, useMemo, useRef, useState } from 'react';
import type { LyricsImportIssue, LyricsImportIssueSeverity } from '../../protocol';
import {
  ISSUE_KIND_TEXT,
  SEVERITY_TEXT,
  decoderLabel,
  filterIssues,
  issueCounts,
  reportFacts,
  sourceWindow,
  splitSourceLines,
  worstSeverity,
} from '../model/lyrics-import';
import { useDomEvent } from '../ui/hooks';
import type { ImportedReport } from './AddLyricsDialogs';

/** 问题清单一次最多显示的条数；“显示更多”每次再加这么多。 */
const ISSUE_PAGE = 300;
/** 源文件超过这么多行时只显示焦点行附近的一段。 */
const SOURCE_WINDOW = 1000;

type Filter = LyricsImportIssueSeverity | 'all';

/**
 * 导入报告（整体设计 §5.2）：识别的格式、编码、行数与跳过的行，下面是问题清单。
 * 点一条问题在右侧源文件里定位到它的行号；源文件按报告里的实际编码在浏览器本地解码。
 */
export function ImportReportDialog(props: { imported: ImportedReport | null; open: boolean; onClose: () => void }) {
  const ref = useRef<HTMLElement>(null);
  useDomEvent(ref, 'close', (event) => event.target === ref.current && props.open && props.onClose());
  const { imported } = props;
  return (
    <mdui-dialog
      ref={ref}
      open={props.open && !!imported}
      headline={imported ? `导入报告：${imported.file.name}` : '导入报告'}
      description={imported ? `已导入到「${imported.branchName}」。` : ''}
      close-on-esc
      close-on-overlay-click
      className="report-dialog"
    >
      {props.open && imported && <ReportBody imported={imported} />}
      <mdui-button slot="action" variant="tonal" onClick={props.onClose}>
        关闭
      </mdui-button>
    </mdui-dialog>
  );
}

function ReportBody({ imported }: { imported: ImportedReport }) {
  const { report } = imported;
  const counts = useMemo(() => issueCounts(report.issues), [report]);
  const [filter, setFilter] = useState<Filter>('all');
  const [limit, setLimit] = useState(ISSUE_PAGE);
  const [focus, setFocus] = useState<number | undefined>(() => report.issues.find((i) => i.sourceLineNumber > 0)?.sourceLineNumber);
  const issues = useMemo(() => filterIssues(report.issues, filter), [report, filter]);
  const source = useSourceText(imported);

  const filters: { value: Filter; label: string; count: number }[] = [
    { value: 'all', label: '全部', count: report.issues.length },
    { value: 'error', label: SEVERITY_TEXT.error, count: counts.error },
    { value: 'warning', label: SEVERITY_TEXT.warning, count: counts.warning },
    { value: 'info', label: SEVERITY_TEXT.info, count: counts.info },
  ];

  return (
    <div className="report-body">
      <dl className="report-facts">
        {reportFacts(report).map((fact) => (
          <div key={fact.label} className="report-fact">
            <dt>{fact.label}</dt>
            <dd>{fact.value}</dd>
          </div>
        ))}
      </dl>

      {report.issues.length === 0 ? (
        <p className="report-clean">没有发现问题。</p>
      ) : (
        <>
          <div className="report-filters" role="group" aria-label="按严重程度筛选">
            <span className="section-title">问题 {report.issues.length} 条</span>
            <span className="spacer" />
            {filters
              .filter((f) => f.value === 'all' || f.count > 0)
              .map((f) => (
                <mdui-button
                  key={f.value}
                  variant={filter === f.value ? 'tonal' : 'text'}
                  onClick={() => {
                    setFilter(f.value);
                    setLimit(ISSUE_PAGE);
                  }}
                >
                  {f.label} {f.count}
                </mdui-button>
              ))}
          </div>
          <div className="report-columns">
            <IssueList issues={issues.slice(0, limit)} focus={focus} onFocus={setFocus} />
            <SourceView source={source} issues={report.issues} focus={focus} />
          </div>
          {issues.length > limit && (
            <mdui-button variant="text" onClick={() => setLimit(limit + ISSUE_PAGE)}>
              还有 {issues.length - limit} 条，再显示 {Math.min(ISSUE_PAGE, issues.length - limit)} 条
            </mdui-button>
          )}
        </>
      )}
    </div>
  );
}

function IssueList(props: { issues: LyricsImportIssue[]; focus: number | undefined; onFocus: (line: number) => void }) {
  return (
    <ol className="issue-list" aria-label="问题清单">
      {props.issues.map((issue, index) => {
        const located = issue.sourceLineNumber > 0;
        const focused = located && issue.sourceLineNumber === props.focus;
        return (
          <li
            key={index}
            className={`issue-item issue-${issue.severity}${focused ? ' focused' : ''}${located ? ' located' : ''}`}
            tabIndex={located ? 0 : -1}
            onClick={() => located && props.onFocus(issue.sourceLineNumber)}
            onKeyDown={(event) => {
              if (located && (event.key === 'Enter' || event.key === ' ')) {
                event.preventDefault();
                props.onFocus(issue.sourceLineNumber);
              }
            }}
          >
            <SeverityIcon severity={issue.severity} />
            <div className="issue-text">
              <div className="issue-head">
                <span className="issue-line">{located ? `第 ${issue.sourceLineNumber} 行` : '整个文件'}</span>
                <span className="issue-kind">{ISSUE_KIND_TEXT[issue.kind] ?? issue.kind}</span>
              </div>
              <div className="issue-message">{issue.message}</div>
              {!located && issue.rawText && <code className="issue-raw">{issue.rawText}</code>}
            </div>
          </li>
        );
      })}
    </ol>
  );
}

function SeverityIcon({ severity }: { severity: LyricsImportIssueSeverity }) {
  const icon =
    severity === 'error' ? <mdui-icon-error /> : severity === 'warning' ? <mdui-icon-warning /> : <mdui-icon-info />;
  return (
    <span className="severity-icon" title={SEVERITY_TEXT[severity]} aria-label={SEVERITY_TEXT[severity]}>
      {icon}
    </span>
  );
}

type SourceText = { lines: string[] } | { error: string } | undefined;

/** 按报告里的实际编码在本地解码源文件；浏览器不支持该编码时说明原因。 */
function useSourceText(imported: ImportedReport): SourceText {
  const [source, setSource] = useState<SourceText>();
  useEffect(() => {
    let active = true;
    const label = decoderLabel(imported.report.encodingName);
    if (!label) {
      setSource({ error: `浏览器不能按 ${imported.report.encodingName} 显示源文件。` });
      return;
    }
    imported.file
      .arrayBuffer()
      .then((buffer) => active && setSource({ lines: splitSourceLines(new TextDecoder(label).decode(buffer)) }))
      .catch(() => active && setSource({ error: '读不到源文件。' }));
    return () => {
      active = false;
    };
  }, [imported]);
  return source;
}

function SourceView(props: { source: SourceText; issues: readonly LyricsImportIssue[]; focus: number | undefined }) {
  const containerRef = useRef<HTMLDivElement>(null);
  const focusedRef = useRef<HTMLDivElement>(null);
  const marks = useMemo(() => {
    const map = new Map<number, LyricsImportIssueSeverity>();
    for (const issue of props.issues) {
      if (issue.sourceLineNumber > 0) map.set(issue.sourceLineNumber, worstSeverity(map.get(issue.sourceLineNumber), issue.severity));
    }
    return map;
  }, [props.issues]);
  const lines = props.source && 'lines' in props.source ? props.source.lines : undefined;
  const [start, end] = sourceWindow(lines?.length ?? 0, props.focus, SOURCE_WINDOW);

  // 只滚动源文件框（scrollIntoView 会连对话框一起滚）。.source-view 是 position: relative，offsetTop 相对它。
  useEffect(() => {
    const container = containerRef.current;
    const line = focusedRef.current;
    if (container && line) container.scrollTop = line.offsetTop - (container.clientHeight - line.offsetHeight) / 2;
  }, [props.focus, lines]);

  return (
    <div ref={containerRef} className="source-view" aria-label="源文件">
      {!props.source && <p className="muted">正在读取源文件……</p>}
      {props.source && 'error' in props.source && <p className="muted">{props.source.error}</p>}
      {lines && (
        <>
          {start > 0 && <div className="source-gap">第 1–{start} 行未显示</div>}
          {lines.slice(start, end).map((text, i) => {
            const number = start + i + 1;
            const mark = marks.get(number);
            const focused = number === props.focus;
            return (
              <div
                key={number}
                ref={focused ? focusedRef : undefined}
                className={`source-line${mark ? ` mark-${mark}` : ''}${focused ? ' focused' : ''}`}
                data-line={number}
              >
                <span className="source-number">{number}</span>
                <span className="source-text">{text || ' '}</span>
              </div>
            );
          })}
          {end < lines.length && <div className="source-gap">第 {end + 1}–{lines.length} 行未显示</div>}
        </>
      )}
    </div>
  );
}
