import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { OutcomeUnknownError, formatBytes, type MakoSnapshot } from '../client';
import {
  micros,
  type AssetResponse,
  type BackendCapabilities,
  type FileHandleResponse,
  type JobResponse,
  type LyricLine,
  type PreviewFrameMetadata,
  type PreviewRuntimeInfo,
  type ProjectHistoryResponse,
  type ProjectSessionSummary,
} from '../protocol';
import { client } from './console-store';
import { describeError, formatMicros, formatTime, shortId, type DisplayError } from './format';
import { useObjectUrl } from './hooks';
import { makeSampleLrc, makeSampleWav } from './sample-assets';

interface StepError {
  step: string;
  error: DisplayError;
}

function defaultSavePath(): string {
  const d = new Date();
  const pad = (n: number) => String(n).padStart(2, '0');
  return `w0-console-${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(d.getHours())}${pad(d.getMinutes())}`;
}

/** 毫秒输入 → 微秒字符串。 */
function msToMicros(text: string) {
  const value = Number(text);
  if (!Number.isFinite(value)) throw new Error(`不是有效的毫秒数：${text}`);
  return micros(BigInt(Math.round(value * 1000)));
}

export function WorkflowPanel({ snapshot }: { snapshot: MakoSnapshot }) {
  const ready = snapshot.state === 'ready';
  const [capabilities, setCapabilities] = useState<BackendCapabilities | null>(null);
  const [runtime, setRuntime] = useState<PreviewRuntimeInfo | null>(null);

  const [audioFile, setAudioFile] = useState<File | null>(null);
  const [lyricsFile, setLyricsFile] = useState<File | null>(null);
  const [audioAsset, setAudioAsset] = useState<AssetResponse | null>(null);
  const [lyricsAsset, setLyricsAsset] = useState<AssetResponse | null>(null);
  const [projectName, setProjectName] = useState('W0 控制台演示');
  const [summary, setSummary] = useState<ProjectSessionSummary | null>(null);
  const [history, setHistory] = useState<ProjectHistoryResponse | null>(null);
  const [lines, setLines] = useState<LyricLine[]>([]);
  const [linesRevision, setLinesRevision] = useState<string | null>(null);
  const [selectedLine, setSelectedLine] = useState<string | null>(null);
  const [selectedToken, setSelectedToken] = useState('');
  const [startMs, setStartMs] = useState('500');
  const [endMs, setEndMs] = useState('1800');
  const [previewMs, setPreviewMs] = useState('1000');
  const [previewBlob, setPreviewBlob] = useState<Blob | null>(null);
  const [previewFrame, setPreviewFrame] = useState<{ frame: PreviewFrameMetadata; verified: boolean } | null>(null);
  const [savePath, setSavePath] = useState(defaultSavePath);
  const [handle, setHandle] = useState<FileHandleResponse | null>(null);
  const [lastJob, setLastJob] = useState<JobResponse | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<StepError | null>(null);
  const previewUrl = useObjectUrl(previewBlob);
  const projectId = summary?.projectSessionId;
  const projectIdRef = useRef<string | undefined>(undefined);
  projectIdRef.current = projectId;

  // 连接就绪后读取能力；连接身份作废时丢弃全部工程／预览／任务 ID。
  useEffect(() => {
    if (!ready) return;
    void client.capabilities().then(setCapabilities, () => undefined);
    void client.preview.runtimeInfo().then(setRuntime, () => undefined);
  }, [ready, snapshot.hostInstanceId]);

  useEffect(
    () =>
      client.on('reset', () => {
        setAudioAsset(null);
        setLyricsAsset(null);
        setSummary(null);
        setHistory(null);
        setLines([]);
        setLinesRevision(null);
        setSelectedLine(null);
        setPreviewBlob(null);
        setPreviewFrame(null);
        setLastJob(null);
        setCapabilities(null);
        setRuntime(null);
      }),
    [],
  );

  const refreshProject = useCallback(async (id = projectIdRef.current) => {
    if (!id) return;
    const [nextSummary, nextHistory] = await Promise.all([client.projects.get(id), client.projects.history(id)]);
    if (projectIdRef.current !== id) return;
    // 迟到的旧读取不能把显示的修订倒退。
    setSummary((current) =>
      current && BigInt(current.projectRevision) > BigInt(nextSummary.projectRevision) ? current : nextSummary,
    );
    setHistory(nextHistory);
  }, []);

  // 事件只是提示：当前工程有变化或事件流 resync 后，重新 GET 权威状态。
  useEffect(() => {
    const offEvent = client.on('event', (event) => {
      if (event.summary?.projectSessionId === projectIdRef.current && event.type !== 'project.closed') {
        void refreshProject().catch(() => undefined);
      }
    });
    const offResync = client.on('resync', () => void refreshProject().catch(() => undefined));
    return () => {
      offEvent();
      offResync();
    };
  }, [refreshProject]);

  const run = async (step: string, action: () => Promise<void>) => {
    setBusy(step);
    setError(null);
    try {
      await action();
    } catch (caught) {
      setError({ step, error: describeError(caught) });
      if (caught instanceof OutcomeUnknownError) await refreshProject().catch(() => undefined);
    } finally {
      setBusy(null);
    }
  };

  const readLyrics = async (id = projectId) => {
    if (!id) return;
    const result = await client.projects.lyrics(id, 'primary');
    setLines(result.lines);
    setLinesRevision(result.projectRevision);
    setSelectedLine((current) => current ?? result.lines[0]?.id ?? null);
  };

  const selected = lines.find((l) => l.id === selectedLine);
  const audioPrepReason = capabilities?.audioPreparation && !capabilities.audioPreparation.enabled
    ? `音频准备不可用：${capabilities.audioPreparation.disabledReason ?? 'disabled'}`
    : null;
  const previewReason = runtime && !runtime.enabled ? `预览不可用：${runtime.disabledReason?.code ?? runtime.status}` : null;
  const disabled = (condition: boolean) => !ready || busy !== null || !condition;

  return (
    <section className="panel workflow">
      <div className="panel-head">
        <h2>操作步骤</h2>
        {busy && <span className="busy">进行中：{busy}……</span>}
      </div>

      {error && (
        <div className="error-box" role="alert">
          <div>
            <span className="tag">{error.error.category}</span> <strong>{error.step}</strong>：{error.error.title}
          </div>
          <div className="detail">{error.error.detail}</div>
        </div>
      )}

      <Step n={1} title="上传素材" done={!!audioAsset && !!lyricsAsset}>
        <div className="row">
          <label className="file">
            音频 <input type="file" accept="audio/*,.wav,.mp3,.flac,.ogg,.m4a" onChange={(e) => setAudioFile(e.target.files?.[0] ?? null)} />
          </label>
          <span className="muted">{audioFile ? `${audioFile.name} · ${formatBytes(audioFile.size)}` : '未选择'}</span>
        </div>
        <div className="row">
          <label className="file">
            歌词 <input type="file" accept=".lrc,.elrc,.txt,.srt,.vtt,.ass,.ssa,.ttml,.xml,text/plain,application/ttml+xml" onChange={(e) => setLyricsFile(e.target.files?.[0] ?? null)} />
          </label>
          <span className="muted">{lyricsFile ? `${lyricsFile.name} · ${formatBytes(lyricsFile.size)}` : '未选择'}</span>
        </div>
        <div className="row">
          <button
            onClick={() => {
              setAudioFile(makeSampleWav());
              setLyricsFile(makeSampleLrc());
            }}
          >
            生成示例素材
          </button>
          <button
            className="primary"
            disabled={disabled(!!audioFile && !!lyricsFile)}
            onClick={() =>
              run('上传', async () => {
                setAudioAsset(await client.assets.upload(audioFile!, { fileName: audioFile!.name }));
                setLyricsAsset(await client.assets.upload(lyricsFile!, { fileName: lyricsFile!.name }));
              })
            }
          >
            上传两个文件
          </button>
        </div>
        {capabilities && (
          <div className="hint">单个资源上限 {capabilities.maxAssetBytes === null ? '未设' : formatBytes(capabilities.maxAssetBytes)}；文件名经 ?fileName= 传递，支持中文。</div>
        )}
        {audioAsset && <KV k="音频" v={`${shortId(audioAsset.assetId)} · ${audioAsset.contentType} · sha256 ${audioAsset.sha256.slice(0, 12)}…`} />}
        {lyricsAsset && <KV k="歌词" v={`${shortId(lyricsAsset.assetId)} · ${lyricsAsset.fileName} · sha256 ${lyricsAsset.sha256.slice(0, 12)}…`} />}
      </Step>

      <Step n={2} title="新建工程" done={!!summary}>
        <div className="row">
          <input value={projectName} onChange={(e) => setProjectName(e.target.value)} aria-label="工程名" />
          <button
            className="primary"
            disabled={disabled(!!audioAsset && !!lyricsAsset && !audioPrepReason)}
            onClick={() =>
              run('新建', async () => {
                const created = await client.projects.create({
                  name: projectName,
                  audioAssetId: audioAsset!.assetId,
                  lyricsAssetId: lyricsAsset!.assetId,
                  composition: { canvasWidth: 640, canvasHeight: 360, frameRateNumerator: 30, frameRateDenominator: 1, outputDurationUs: 4_000_000 },
                });
                projectIdRef.current = created.projectSessionId;
                setSummary(created);
                setLines([]);
                setPreviewBlob(null);
                setPreviewFrame(null);
                await refreshProject(created.projectSessionId);
              })
            }
          >
            新建
          </button>
        </div>
        {audioPrepReason && <div className="hint warn">{audioPrepReason}（新建有音频工程需要 Worker 音频准备）</div>}
        {summary && <ProjectSummary summary={summary} history={history} />}
      </Step>

      <Step n={3} title="读歌词" done={lines.length > 0}>
        <div className="row">
          <button disabled={disabled(!!projectId)} onClick={() => run('读歌词', () => readLyrics())}>
            读取 primary 来源
          </button>
          {linesRevision && (
            <span className={`muted ${summary && linesRevision !== summary.projectRevision ? 'stale' : ''}`}>
              读取于修订 {linesRevision}
              {summary && linesRevision !== summary.projectRevision && '（工程已变化，可重新读取）'}
            </span>
          )}
        </div>
        {lines.length > 0 && (
          <table className="lines">
            <thead>
              <tr>
                <th />
                <th>正文</th>
                <th>时间</th>
                <th>元素</th>
              </tr>
            </thead>
            <tbody>
              {lines.map((line) => (
                <tr key={line.id} className={line.id === selectedLine ? 'selected' : ''} onClick={() => setSelectedLine(line.id)}>
                  <td>
                    <input type="radio" checked={line.id === selectedLine} onChange={() => setSelectedLine(line.id)} aria-label={line.text} />
                  </td>
                  <td>{line.text}</td>
                  <td className="mono">{formatTime(line.time)}</td>
                  <td className="muted">{line.tokens.length}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </Step>

      <Step n={4} title="设置选中行时间" done={false}>
        <div className="row">
          <select value={selectedToken} onChange={(e) => setSelectedToken(e.target.value)} disabled={!selected} aria-label="目标">
            <option value="">整行</option>
            {selected?.tokens.map((token) => (
              <option key={token.id} value={token.id}>
                元素「{token.text}」{formatTime(token.time)}
              </option>
            ))}
          </select>
          <label>
            开始 <input className="num" value={startMs} onChange={(e) => setStartMs(e.target.value)} /> ms
          </label>
          <label>
            结束 <input className="num" value={endMs} onChange={(e) => setEndMs(e.target.value)} /> ms
          </label>
          <button
            className="primary"
            disabled={disabled(!!selected)}
            onClick={() =>
              run('设置时间', async () => {
                const result = await client.projects.setTimeRange(
                  projectId!,
                  { branchId: selected!.branchId, lineId: selected!.id, ...(selectedToken ? { tokenId: selectedToken } : {}) },
                  msToMicros(startMs),
                  msToMicros(endMs),
                );
                if (!result.changed) setError({ step: '设置时间', error: { category: '提示', title: '领域 no-op', detail: '时间没有变化，修订号不变。' } });
                await readLyrics();
              })
            }
          >
            setTimeRange
          </button>
        </div>
      </Step>

      <Step n={5} title="撤销 / 重做" done={false}>
        <div className="row">
          <button
            disabled={disabled(!!summary?.canUndo)}
            onClick={() => run('撤销', async () => void (await client.projects.undo(projectId!), await readLyrics()))}
          >
            撤销{history?.undoName ? `「${history.undoName}」` : ''}
          </button>
          <button
            disabled={disabled(!!summary?.canRedo)}
            onClick={() => run('重做', async () => void (await client.projects.redo(projectId!), await readLyrics()))}
          >
            重做{history?.redoName ? `「${history.redoName}」` : ''}
          </button>
          {history && <span className="muted">可撤销 {history.undoCount} · 可重做 {history.redoCount}</span>}
        </div>
      </Step>

      <Step n={6} title="静态预览一帧" done={!!previewFrame}>
        <div className="row">
          <label>
            时间 <input className="num" value={previewMs} onChange={(e) => setPreviewMs(e.target.value)} /> ms
          </label>
          <button
            className="primary"
            disabled={disabled(!!summary && !previewReason)}
            onClick={() =>
              run('预览', async () => {
                const still = await client.preview.renderStill({
                  projectSessionId: projectId!,
                  sourceRevision: summary!.projectRevision,
                  timeUs: msToMicros(previewMs),
                  size: { width: 640, height: 360 },
                });
                setPreviewBlob(still.blob);
                setPreviewFrame({ frame: still.frame, verified: still.verified });
              })
            }
          >
            渲染修订 {summary?.projectRevision ?? '—'}
          </button>
        </div>
        {previewReason && <div className="hint warn">{previewReason}</div>}
        {runtime?.enabled && runtime.status === 'configured-unverified' && !previewFrame && (
          <div className="hint">runtime-info 为 configured-unverified：只表示 Worker 已配置，真实 PNG 才证明渲染可用。</div>
        )}
        {previewUrl && previewFrame && (
          <figure className="preview">
            <img src={previewUrl} alt="预览帧" width={previewFrame.frame.width} height={previewFrame.frame.height} />
            <figcaption className="mono">
              修订 {previewFrame.frame.sourceRevision} · 代次 {previewFrame.frame.previewGeneration} · seq {previewFrame.frame.requestSeq} ·{' '}
              {formatMicros(previewFrame.frame.timeUs)} · {previewFrame.frame.width}×{previewFrame.frame.height} ·{' '}
              {formatBytes(previewFrame.frame.content.sizeBytes)} · SHA-256 {previewFrame.verified ? '✓ 一致' : '✗ 不一致'}
            </figcaption>
          </figure>
        )}
      </Step>

      <Step n={7} title="保存到授权目录" done={!!summary?.hasPath && !summary.isDirty}>
        <div className="row">
          <span className="muted">projects /</span>
          <input value={savePath} onChange={(e) => setSavePath(e.target.value)} aria-label="相对目录" />
          <button
            className="primary"
            disabled={disabled(!!summary && !summary.isReadOnly)}
            onClick={() =>
              run('保存', async () => {
                const nextHandle =
                  handle && handle.relativePath === savePath
                    ? handle
                    : await client.files.createHandle({ rootGrantId: 'projects', relativePath: savePath, access: 'readWrite' });
                setHandle(nextHandle);
                const job = await client.projects.save(projectId!, { targetHandleId: nextHandle.handleId });
                setLastJob(job);
                setLastJob(
                  await client.jobs.waitForTerminal(job.jobId, {
                    onProgress: (j) => setLastJob((prev) => (prev ? { ...prev, state: j.state } : prev)),
                  }),
                );
                await refreshProject();
              })
            }
          >
            保存
          </button>
        </div>
        {handle && <KV k="目录句柄" v={`${shortId(handle.handleId)} · ${handle.rootGrantId}/${handle.relativePath} · ${handle.canWrite ? '可写' : '只读'}`} />}
      </Step>

      <Step n={8} title="关闭 → 重开" done={false}>
        <div className="row">
          <button
            disabled={disabled(!!summary)}
            onClick={() =>
              run('关闭', async () => {
                const job = await client.projects.close(projectId!, { policy: 'requireClean', activeJobs: 'wait' });
                setLastJob(job);
                const done = await client.jobs.waitForTerminal(job.jobId);
                setLastJob(done);
                if (done.state === 'succeeded') {
                  projectIdRef.current = undefined;
                  setSummary(null);
                  setHistory(null);
                  setLines([]);
                  setLinesRevision(null);
                }
              })
            }
          >
            关闭（requireClean）
          </button>
          <button
            className="primary"
            disabled={disabled(!!savePath.trim() && !summary)}
            onClick={() =>
              run('重开', async () => {
                // 句柄绑定 client；刷新或 Server 重启后需要按目录重新创建。
                const reopenHandle = await client.files.createHandle({ rootGrantId: 'projects', relativePath: savePath.trim(), access: 'readWrite' });
                setHandle(reopenHandle);
                const job = await client.projects.open({ projectDirectoryHandleId: reopenHandle.handleId, access: 'readWrite' });
                setLastJob(job);
                const done = await client.jobs.waitForTerminal(job.jobId);
                setLastJob(done);
                if (done.state === 'succeeded' && done.result) {
                  projectIdRef.current = done.result.projectSessionId;
                  setSummary(done.result);
                  await refreshProject(done.result.projectSessionId);
                  await readLyrics(done.result.projectSessionId);
                }
              })
            }
          >
            从 projects/{savePath.trim() || '…'} 重开
          </button>
        </div>
        {lastJob && <JobLine job={lastJob} />}
      </Step>

      <Step n={9} title="事件流调试" done={false}>
        <div className="row">
          <button disabled={!ready} onClick={() => client.disconnectEvents()}>断开事件流</button>
          <button disabled={!ready} onClick={() => client.connectEvents()}>从游标续接</button>
          <button disabled={!ready} onClick={() => client.reconnectEvents()}>断开并立即重连</button>
          <button disabled={!ready} onClick={() => client.reconnectEvents('999999999')}>制造游标缺口</button>
          <button disabled={!projectId || !ready} onClick={() => run('刷新', () => refreshProject())}>刷新工程状态</button>
        </div>
        <div className="hint">“制造游标缺口”用超前游标重连：服务端发 resync.required，客户端重读 client state 后用新游标续接。</div>
      </Step>
    </section>
  );
}

function Step({ n, title, done, children }: { n: number; title: string; done: boolean; children: ReactNode }) {
  return (
    <div className={`step ${done ? 'done' : ''}`}>
      <h3>
        <span className="step-n">{done ? '✓' : n}</span>
        {title}
      </h3>
      <div className="step-body">{children}</div>
    </div>
  );
}

function KV({ k, v }: { k: string; v: string }) {
  return (
    <div className="kv">
      <span className="k">{k}</span>
      <span className="v">{v}</span>
    </div>
  );
}

function ProjectSummary({ summary, history }: { summary: ProjectSessionSummary; history: ProjectHistoryResponse | null }) {
  return (
    <div className="summary">
      <KV k="工程" v={`${shortId(summary.projectSessionId)} · ${summary.name}`} />
      <KV
        k="状态"
        v={[
          `修订 ${summary.projectRevision}`,
          summary.isDirty ? '未保存' : '已保存',
          summary.savedProjectRevision ? `保存于修订 ${summary.savedProjectRevision}` : null,
          summary.isReadOnly ? '只读' : null,
          `${summary.lyricsLineCount} 行`,
          summary.audioDurationUs !== undefined ? `音频 ${(summary.audioDurationUs / 1e6).toFixed(2)}s` : null,
          history ? `历史 ${history.undoCount}/${history.redoCount}` : null,
        ]
          .filter(Boolean)
          .join(' · ')}
      />
    </div>
  );
}

function JobLine({ job }: { job: JobResponse }) {
  return (
    <div className={`job job-${job.state}`}>
      任务 <span className="mono">{shortId(job.jobId)}</span> · {job.kind} · <strong>{job.state}</strong>
      {job.savedProjectRevision && ` · 保存修订 ${job.savedProjectRevision}`}
      {job.error && ` · ${job.error.code}：${job.error.message}`}
      {job.progress && ` · ${job.progress.completedFrames}/${job.progress.totalFrames}`}
      {job.diagnostics && job.diagnostics.length > 0 && ` · ${job.diagnostics.length} 条诊断`}
    </div>
  );
}
