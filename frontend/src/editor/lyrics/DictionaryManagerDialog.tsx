import { useCallback, useEffect, useRef, useState } from 'react';
import {
  defaultJapaneseDictStore,
  NAIST_JDIC_INFO,
  type DictCacheStatus,
  type DictDownloadProgress,
  type LocalDictFileInput,
} from '../phonetics/dict-store';
import { defaultJapaneseFuriganaService } from '../phonetics/japanese-furigana';
import { formatRubySegments, hasExplicitRubySeparator, splitExplicitRubySegments, splitJapaneseMora } from '../phonetics/mora-splitter';
import { previewRomajiInput } from '../phonetics/romaji-preview';
import { loadTimingPreferences, updateTimingPreferences } from '../phonetics/timing-preferences';
import {
  exportUserDictionaryText,
  loadUserDictionary,
  parseExternalUserDictionary,
  removeUserDictionaryEntry,
  saveUserDictionary,
  upsertUserDictionaryEntry,
  type UserDictionaryEntry,
} from '../phonetics/user-dictionary';
import { Button, IconButton } from '../ui/Button';
import { Dialog } from '../ui/Dialog';
import { confirmAction } from '../ui/dialogs';
import { saveBlob } from '../ui/download';
import { toast } from '../ui/feedback';
import { TextField } from '../ui/fields';
import { Icon } from '../ui/Icon';
import { ProgressBar, Tag } from '../ui/indicators';
import { Tabs } from '../ui/Tabs';

type DictDialogTab = 'dictionary' | 'userDict';

function formatBytes(bytes: number | undefined): string {
  if (!bytes || bytes <= 0) return '0 B';
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
}

function phaseLabel(phase: DictDownloadProgress['phase']): string {
  switch (phase) {
    case 'manifest':
      return '正在获取词典清单…';
    case 'downloading':
      return '正在下载词典分片…';
    case 'verifying':
      return '正在校验 SHA-256…';
    case 'saving':
      return '正在写入本地持久缓存…';
    case 'importing':
      return '正在解包本地词典…';
  }
}

function backendLabel(kind: DictCacheStatus['backend']): string {
  switch (kind) {
    case 'opfs':
      return 'OPFS（本地持久文件系统）';
    case 'indexeddb':
      return 'IndexedDB（本地持久存储）';
    case 'memory':
      return '内存缓存（仅当前会话）';
    default:
      return '未缓存';
  }
}

/**
 * 日语词典管理与首次按需下载确认弹窗：
 * - 未缓存时明确告知词典名称（NAIST-jdic 0.6.3b）、开源许可（BSD-3-Clause）、预估体积（约 14~16 MB）与离线持久化策略；
 * - 提供「开始下载」、「从本地导入词典包（.zip / 目录）」与「暂不下载」三个入口；
 * - 支持下载进度、取消、镜像地址配置、重试与一键清理缓存；
 * - 第二个页签提供本地用户读音词典（最高优先级覆盖）CRUD 及 StrangeUtaGame / RL 格式导入导出。
 */
export function DictionaryManagerDialog(props: {
  open: boolean;
  onClose: () => void;
  /** 首次因点击自动注音触发时传入的引导说明。 */
  promptReason?: string | undefined;
  /** 词典状态发生变化（下载完成、导入完成、清理缓存）时通知上层刷新徽标。 */
  onStatusChange?: (status: DictCacheStatus) => void;
  /** 当因触发自动注音而弹出，且词典成功就绪后的后续动作（例如继续自动注音）。 */
  onReadyAction?: (() => void) | undefined;
  readyActionLabel?: string | undefined;
}) {
  const [tab, setTab] = useState<DictDialogTab>('dictionary');
  const [status, setStatus] = useState<DictCacheStatus>({ installed: false, backend: 'none' });
  const [loadingStatus, setLoadingStatus] = useState(false);
  const [progress, setProgress] = useState<DictDownloadProgress | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [mirrorUrl, setMirrorUrl] = useState(() => loadTimingPreferences().dictMirrorUrl);
  const [showMirror, setShowMirror] = useState(false);
  const abortRef = useRef<AbortController | null>(null);
  const zipInputRef = useRef<HTMLInputElement>(null);
  const dirInputRef = useRef<HTMLInputElement>(null);
  const userDictImportRef = useRef<HTMLInputElement>(null);

  // User dictionary state
  const [userEntries, setUserEntries] = useState<UserDictionaryEntry[]>(() => loadUserDictionary());
  const [surfaceInput, setSurfaceInput] = useState('');
  const [readingInput, setReadingInput] = useState('');
  const [noteInput, setNoteInput] = useState('');
  const [userSearch, setUserSearch] = useState('');

  const refreshStatus = useCallback(async () => {
    setLoadingStatus(true);
    try {
      const next = await defaultJapaneseDictStore.getStatus();
      setStatus(next);
      props.onStatusChange?.(next);
    } finally {
      setLoadingStatus(false);
    }
  }, [props.onStatusChange]);

  useEffect(() => {
    if (!props.open) return;
    setError(null);
    setTab('dictionary');
    setUserEntries(loadUserDictionary());
    setMirrorUrl(loadTimingPreferences().dictMirrorUrl);
    void refreshStatus();
  }, [props.open, refreshStatus]);

  const startDownload = async () => {
    if (busy) return;
    setError(null);
    setBusy(true);
    setProgress(null);
    const controller = new AbortController();
    abortRef.current = controller;
    updateTimingPreferences({ dictMirrorUrl: mirrorUrl });
    try {
      const next = await defaultJapaneseDictStore.downloadFromUrl(mirrorUrl || undefined, {
        signal: controller.signal,
        onProgress: (p) => setProgress(p),
      });
      await defaultJapaneseFuriganaService.reset();
      setStatus(next);
      props.onStatusChange?.(next);
      toast('日语词典 NAIST-jdic 0.6.3b 已就绪，可永久离线使用');
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') {
        setError('已取消词典下载。');
      } else {
        setError(err instanceof Error ? err.message : String(err));
        setShowMirror(true);
      }
    } finally {
      abortRef.current = null;
      setBusy(false);
      setProgress(null);
    }
  };

  const cancelDownload = () => {
    abortRef.current?.abort();
  };

  const importFiles = async (fileList: FileList | null) => {
    if (!fileList || fileList.length === 0 || busy) return;
    setError(null);
    setBusy(true);
    setProgress(null);
    try {
      const inputs: LocalDictFileInput[] = Array.from(fileList).map((file) => ({
        name: file.webkitRelativePath || file.name,
        bytes: async () => new Uint8Array(await file.arrayBuffer()),
      }));
      const next = await defaultJapaneseDictStore.importLocalFiles(inputs, (p) => setProgress(p));
      await defaultJapaneseFuriganaService.reset();
      setStatus(next);
      props.onStatusChange?.(next);
      toast('本地日语词典导入并校验成功');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setBusy(false);
      setProgress(null);
    }
  };

  const clearCache = async () => {
    const ok = await confirmAction({
      headline: '清理本地日语词典缓存？',
      description: '将删除缓存在本地的 NAIST-jdic 词典文件（不影响本地用户读音词典）。下次使用日语自动注音时需要重新下载或本地导入。',
      confirmText: '清理缓存',
      danger: true,
    });
    if (!ok) return;
    setBusy(true);
    try {
      await defaultJapaneseDictStore.clearCache();
      await defaultJapaneseFuriganaService.reset();
      const next = await defaultJapaneseDictStore.getStatus();
      setStatus(next);
      props.onStatusChange?.(next);
      toast('已清理本地日语词典缓存');
    } finally {
      setBusy(false);
    }
  };

  // User dictionary handlers
  const mergeOpt = loadTimingPreferences().mergeLongAndNasalInKanji;
  const readingPreview = previewRomajiInput(readingInput, { mergeLongAndNasalInKanji: mergeOpt, isKanjiBase: true });
  const resolvedNewSegments = readingInput.trim()
    ? readingPreview.hasLatin
      ? readingPreview.kanaSegments
      : hasExplicitRubySeparator(readingInput)
        ? splitExplicitRubySegments(readingInput)
        : splitJapaneseMora(readingInput, { mergeLongAndNasalInKanji: mergeOpt, isKanjiBase: true })
    : [];

  const addUserEntry = () => {
    const surface = surfaceInput.trim();
    if (!surface || resolvedNewSegments.length === 0) return;
    const next = upsertUserDictionaryEntry({
      surface,
      segments: resolvedNewSegments,
      ...(noteInput.trim() ? { note: noteInput.trim() } : {}),
    });
    setUserEntries(next);
    setSurfaceInput('');
    setReadingInput('');
    setNoteInput('');
    toast(`已保存自定义读音：${surface} → ${formatRubySegments(resolvedNewSegments)}`);
  };

  const deleteUserEntry = (surface: string) => {
    setUserEntries(removeUserDictionaryEntry(surface));
  };

  const importUserDictFile = async (file: File | null) => {
    if (!file) return;
    try {
      const text = await file.text();
      const parsed = parseExternalUserDictionary(text, { mergeLongAndNasalInKanji: mergeOpt });
      if (parsed.length === 0) {
        toast('文件里没有解析到有效的词条');
        return;
      }
      const merged = saveUserDictionary([...userEntries, ...parsed]);
      setUserEntries(merged);
      toast(`已导入 ${parsed.length} 条用户读音词条（当前共 ${merged.length} 条）`);
    } catch (err) {
      toast(`导入用户词典失败：${err instanceof Error ? err.message : String(err)}`);
    }
  };

  const exportUserDict = () => {
    const text = exportUserDictionaryText(userEntries);
    saveBlob(new Blob([text], { type: 'text/plain;charset=utf-8' }), 'mako-user-phonetics.txt');
    toast(`已导出 ${userEntries.length} 条用户读音词典`);
  };

  const filteredUserEntries = userSearch.trim()
    ? userEntries.filter(
        (e) =>
          e.surface.includes(userSearch.trim()) ||
          e.segments.join('').includes(userSearch.trim()) ||
          (e.note?.includes(userSearch.trim()) ?? false),
      )
    : userEntries;

  return (
    <Dialog
      open={props.open}
      onClose={() => {
        if (!busy) props.onClose();
      }}
      dismissable={!busy}
      title="日语注音词典管理"
      description={props.promptReason ?? '管理按需下载的离线日语分词词典（NAIST-jdic 0.6.3b）与本地用户读音词典。'}
      className="dict-manager-dialog"
      actions={
        status.installed && props.onReadyAction ? (
          <>
            <Button variant="text" onPress={props.onClose}>
              关闭
            </Button>
            <Button
              variant="filled"
              icon="wand_stars"
              onPress={() => {
                props.onClose();
                props.onReadyAction?.();
              }}
            >
              {props.readyActionLabel ?? '继续日语自动注音'}
            </Button>
          </>
        ) : (
          <Button variant="text" isDisabled={busy} onPress={props.onClose}>
            {status.installed ? '关闭' : '暂不下载'}
          </Button>
        )
      }
    >
      {/* Hidden file inputs for local dictionary import */}
      <input
        ref={zipInputRef}
        type="file"
        accept=".zip"
        hidden
        aria-label="从本地导入词典压缩包"
        onChange={(e) => {
          void importFiles(e.currentTarget.files);
          e.currentTarget.value = '';
        }}
      />
      <input
        ref={dirInputRef}
        type="file"
        multiple
        // @ts-expect-error webkitdirectory is supported in Chromium/Electron/Firefox/Safari
        webkitdirectory=""
        hidden
        aria-label="从本地导入词典目录"
        onChange={(e) => {
          void importFiles(e.currentTarget.files);
          e.currentTarget.value = '';
        }}
      />
      <input
        ref={userDictImportRef}
        type="file"
        accept=".txt,.tsv,.dict,.json"
        hidden
        aria-label="导入外部用户读音词典"
        onChange={(e) => {
          void importUserDictFile(e.currentTarget.files?.[0] ?? null);
          e.currentTarget.value = '';
        }}
      />

      <Tabs
        label="词典管理页签"
        value={tab}
        onChange={setTab}
        options={[
          {
            value: 'dictionary',
            label: '离线分词词典',
            icon: 'library_books',
            badge: status.installed ? '已就绪' : '未下载',
          },
          {
            value: 'userDict',
            label: '用户读音词典',
            icon: 'translate',
            badge: userEntries.length > 0 ? String(userEntries.length) : undefined,
          },
        ]}
      >
        {(activeTab) =>
          activeTab === 'dictionary' ? (
            <div className="dict-tab-body">
              {status.installed ? (
                <div className="dict-card installed">
                  <div className="dict-card-head">
                    <Tag tone="ok" icon="check_circle">
                      词典已就绪 · 永久离线可用
                    </Tag>
                    <span className="spacer" />
                    <span className="faint small tnum">{formatBytes(status.totalBytes)}</span>
                  </div>
                  <dl className="dict-meta">
                    <div>
                      <dt>词典名称</dt>
                      <dd>{status.name ?? NAIST_JDIC_INFO.name}</dd>
                    </div>
                    <div>
                      <dt>开源许可</dt>
                      <dd>{status.license ?? NAIST_JDIC_INFO.license}（零 IPADIC 资产）</dd>
                    </div>
                    <div>
                      <dt>缓存位置</dt>
                      <dd>{backendLabel(status.backend)}</dd>
                    </div>
                    {status.version && (
                      <div>
                        <dt>版本标识</dt>
                        <dd className="tnum">{status.version}</dd>
                      </div>
                    )}
                  </dl>
                  <div className="dict-actions-row">
                    <Button variant="outlined" size="sm" icon="upload_file" isDisabled={busy} onPress={() => zipInputRef.current?.click()}>
                      从本地 .zip 重新导入
                    </Button>
                    <Button variant="outlined" size="sm" icon="folder_open" isDisabled={busy} onPress={() => dirInputRef.current?.click()}>
                      从本地目录导入
                    </Button>
                    <span className="spacer" />
                    <Button variant="tonal" size="sm" danger icon="delete" isDisabled={busy} onPress={() => void clearCache()}>
                      清理缓存
                    </Button>
                  </div>
                </div>
              ) : (
                <div className="dict-card">
                  <div className="dict-card-head">
                    <Icon name="info" className="dict-lead-ic" />
                    <strong>首次使用需按需获取日语分词词典</strong>
                  </div>
                  <p className="dict-notice-text">
                    为保持基础安装包零体积增重，日语自动注音采用按需下载机制。下载校验完成后将永久缓存在本地（OPFS／IndexedDB），后续全程离线运行，并可随时在此清理缓存。
                  </p>
                  <dl className="dict-meta">
                    <div>
                      <dt>词典名称</dt>
                      <dd>{NAIST_JDIC_INFO.name}（裁剪版，仅保留注音必需列）</dd>
                    </div>
                    <div>
                      <dt>开源许可</dt>
                      <dd>{NAIST_JDIC_INFO.license}（兼容 AGPL v3，绝不含 IPADIC）</dd>
                    </div>
                    <div>
                      <dt>预估体积</dt>
                      <dd>{NAIST_JDIC_INFO.estimatedSizeLabel}</dd>
                    </div>
                    <div>
                      <dt>本地存储</dt>
                      <dd>{backendLabel(status.backend)}</dd>
                    </div>
                  </dl>

                  {!busy && (
                    <div className="dict-actions-row">
                      <Button variant="filled" icon="download" isDisabled={loadingStatus} onPress={() => void startDownload()}>
                        开始下载（{NAIST_JDIC_INFO.estimatedSizeLabel}）
                      </Button>
                      <Button variant="tonal" icon="upload_file" onPress={() => zipInputRef.current?.click()}>
                        导入本地词典包 (.zip)
                      </Button>
                      <Button variant="outlined" icon="folder_open" onPress={() => dirInputRef.current?.click()}>
                        选择本地词典目录…
                      </Button>
                    </div>
                  )}
                </div>
              )}

              {busy && (
                <div className="dict-progress-box">
                  <ProgressBar
                    label={progress ? phaseLabel(progress.phase) : '正在准备…'}
                    value={progress ? progress.percent : undefined}
                    showLabel
                  />
                  <div className="dict-progress-detail">
                    <span className="faint small">
                      {progress?.currentFile ? `${progress.currentFile} · ` : ''}
                      {progress && progress.totalBytes > 0
                        ? `${formatBytes(progress.loadedBytes)} / ${formatBytes(progress.totalBytes)}`
                        : '连接中…'}
                    </span>
                    <span className="spacer" />
                    {abortRef.current && (
                      <Button variant="text" size="sm" danger onPress={cancelDownload}>
                        取消下载
                      </Button>
                    )}
                  </div>
                </div>
              )}

              {error && (
                <div className="dict-error-box" role="alert">
                  <Icon name="warning" />
                  <span>{error}</span>
                  <span className="spacer" />
                  <Button variant="tonal" size="sm" icon="refresh" isDisabled={busy} onPress={() => void startDownload()}>
                    重试
                  </Button>
                </div>
              )}

              <div className="dict-mirror-row">
                <Button variant="text" size="sm" onPress={() => setShowMirror((v) => !v)}>
                  {showMirror ? '隐藏镜像源设置' : '自定义词典镜像地址（网络受限时使用）'}
                </Button>
                {showMirror && (
                  <TextField
                    label="词典静态镜像基地址（留空使用默认 Release 地址）"
                    placeholder={NAIST_JDIC_INFO.defaultBaseUrl}
                    value={mirrorUrl}
                    onChange={setMirrorUrl}
                    disabled={busy}
                    helper="目录下需包含 manifest.json 与 12 个 .dat.gz 文件，下载时自动执行 SHA-256 校验。"
                  />
                )}
              </div>
            </div>
          ) : (
            <div className="dict-tab-body">
              <p className="faint small dict-user-hint">
                用户读音词典具有最高优先级，会在日语自动注音前按最长词条优先匹配覆盖（如人名、生僻熟字训、动画专有名词）。支持使用「・」或「,」手动指定分拍，也支持罗马音输入。
              </p>
              <div className="dict-user-form">
                <TextField
                  label="表层词"
                  placeholder="例如：二人 / 刹那"
                  size="sm"
                  value={surfaceInput}
                  onChange={setSurfaceInput}
                  onEnter={addUserEntry}
                />
                <TextField
                  label="分拍读音（・或 , 分隔，支持罗马音）"
                  placeholder="例如：ふ・た・り 或 fu,ta,ri"
                  size="sm"
                  value={readingInput}
                  onChange={setReadingInput}
                  onEnter={addUserEntry}
                  {...(resolvedNewSegments.length > 0
                    ? { helper: `预览分拍：${formatRubySegments(resolvedNewSegments)}（${resolvedNewSegments.length} 拍）` }
                    : {})}
                />
                <TextField
                  label="备注（可选）"
                  placeholder="例如：歌名特读"
                  size="sm"
                  value={noteInput}
                  onChange={setNoteInput}
                  onEnter={addUserEntry}
                />
                <Button
                  variant="filled"
                  size="sm"
                  icon="add"
                  isDisabled={!surfaceInput.trim() || resolvedNewSegments.length === 0}
                  onPress={addUserEntry}
                >
                  添加
                </Button>
              </div>

              <div className="dict-user-toolbar">
                <TextField
                  label="筛选词条"
                  labelPlacement="none"
                  size="sm"
                  icon="search"
                  placeholder="搜索表层词或读音…"
                  value={userSearch}
                  onChange={setUserSearch}
                  className="dict-user-search"
                />
                <span className="spacer" />
                <Button variant="outlined" size="sm" icon="upload_file" onPress={() => userDictImportRef.current?.click()}>
                  导入（StrangeUtaGame / RL / JSON）
                </Button>
                <Button variant="outlined" size="sm" icon="download" isDisabled={userEntries.length === 0} onPress={exportUserDict}>
                  导出文本
                </Button>
              </div>

              <div className="dict-user-list" role="region" aria-label="用户读音词典列表">
                {filteredUserEntries.length === 0 ? (
                  <p className="faint small dict-user-empty">
                    {userEntries.length === 0 ? '还没有自定义读音词条。在上方添加后将自动优先覆盖词典注音。' : '没有匹配的词条。'}
                  </p>
                ) : (
                  filteredUserEntries.map((entry) => (
                    <div key={entry.surface} className="dict-user-row">
                      <span className="dict-user-surface" lang="ja">
                        {entry.surface}
                      </span>
                      <span className="dict-user-segs" lang="ja">
                        {entry.segments.map((seg, i) => (
                          <span key={i} className="dict-seg-chip">
                            {seg}
                          </span>
                        ))}
                      </span>
                      <span className="dict-user-note faint small">{entry.note ?? ''}</span>
                      <IconButton icon="delete" label={`删除 ${entry.surface}`} size="sm" onPress={() => deleteUserEntry(entry.surface)} />
                    </div>
                  ))
                )}
              </div>
            </div>
          )
        }
      </Tabs>
    </Dialog>
  );
}
