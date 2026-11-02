import { useCallback, useEffect, useState } from 'react';
import { ObservableValue } from '../client';
import { AppearancePage, type EditCopyRequest, type ReturnTrial } from './appearance/AppearancePage';
import { AppearanceStore } from './appearance/appearance-store';
import { AudioController } from './audio/audio-controller';
import { client, renderDevice, useConnection } from './connection';
import { ExportPage } from './export/ExportPage';
import { ExportStore } from './export/export-store';
import { Home } from './home/Home';
import { askSaveLocation, SaveLocationHost } from './home/SaveLocationDialog';
import { confirmDiscardTemplate, LibraryPage } from './library/LibraryPage';
import { AuthorDraftStore } from './library/author-draft-store';
import { ComponentLibraryStore } from './library/component-library-store';
import { TemplateLibraryStore } from './library/template-library-store';
import { TemplateThumbnails } from './library/template-thumbnails';
import { OrchestrationPage } from './orchestration/OrchestrationPage';
import { OrchestrationStore } from './orchestration/orchestration-store';
import { EditorSession, errorText, type EditorState } from './session/editor-session';
import { SettingsDrawer } from './settings/SettingsDrawer';
import { AboutDialog } from './shell/AboutDialog';
import { AppBar, type EditPage, type Page } from './shell/AppBar';
import { AppSettings } from './shell/AppSettings';
import type { CheckFix, SettingsGroup } from './model/export-readiness';
import { parentPath, suggestDirectoryName } from './model/project-directory';
import { closeProject, projectLocation, rememberProject, saveProject } from './session/project-files';
import { askCloseUnsaved, DialogHost } from './ui/dialogs';
import { ToastHost } from './ui/feedback';
import { useObservableSelector } from './ui/hooks';
import { Workspace } from './workspace/Workspace';

/**
 * 应用外壳（界面改版 §2.5）：顶栏与当前页面。
 * 播放器属于工程而不是某个页面（共享底部区，§3.2）：切换页面时播放头和播放状态保持。
 * 样式库是应用级资源（W3 设计 §2.1）：没有工程也能进，关闭工程或连接重置时留在原页。
 */
export function App() {
  const snapshot = useConnection();
  const [session, setSession] = useState<EditorSession | null>(null);
  const [page, setPage] = useState<Page>('home');
  const [homeMessage, setHomeMessage] = useState<string | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [settingsFocus, setSettingsFocus] = useState<SettingsGroup>();
  const [appSettingsOpen, setAppSettingsOpen] = useState(false);
  const [aboutOpen, setAboutOpen] = useState(false);
  /** 进导出页之前的编辑页，“返回编辑”回到这里。 */
  const [editPage, setEditPage] = useState<EditPage>('lyrics');
  const audio = useProjectAudio(session);
  const orchestration = useProjectStore(session, createOrchestration);
  const appearance = useProjectStore(session, createAppearance);
  const exporter = useProjectStore(session, createExport);
  const [library] = useState(() => new TemplateLibraryStore(client));
  const [components] = useState(() => new ComponentLibraryStore(client));
  const [drafts] = useState(() => new AuthorDraftStore(client, components, renderDevice));
  /** 从外观页进样式库编辑副本时的返回栏（只是界面状态，W7 设计 §2.7）；离开样式库或换工程时清掉。 */
  const [libraryReturn, setLibraryReturn] = useState<(EditCopyRequest & { projectSessionId: string }) | null>(null);
  const [returnTrial, setReturnTrial] = useState<ReturnTrial | null>(null);
  const endReturnTrial = useCallback(() => setReturnTrial(null), []);
  const [thumbnails] = useState(() => new TemplateThumbnails(client, renderDevice));

  // 连接身份作废（Server 重启等）：所有工程 ID 失效，回到首页。
  useEffect(
    () =>
      client.on('reset', ({ reason }) => {
        setSession((current) => {
          current?.dispose();
          return null;
        });
        setPage((p) => (p === 'library' ? p : 'home'));
        setLibraryReturn(null);
        setReturnTrial(null);
        setHomeMessage(`连接已重置（${reason}），请重新打开工程。`);
        components.reset();
        drafts.reset(reason);
      }),
    [components, drafts],
  );

  // 作者草稿（W8 设计 §2.4）：连接就绪后读一次本标签页的草稿（刷新后续接）；有草稿没保存到组件库时，关闭或刷新标签页前提示。
  useEffect(() => {
    const loadDrafts = () => {
      if (client.state$.get().state === 'ready' && drafts.state.status === 'idle') void drafts.load();
    };
    loadDrafts();
    const unsubscribe = client.state$.subscribe(loadDrafts);
    const onBeforeUnload = (event: BeforeUnloadEvent) => {
      if (drafts.hasUnsaved) event.preventDefault();
    };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => {
      unsubscribe();
      window.removeEventListener('beforeunload', onBeforeUnload);
    };
  }, [drafts]);

  const enter = useCallback((next: EditorSession) => {
    setHomeMessage(null);
    setSession((current) => {
      if (current && current !== next) current.dispose();
      return next;
    });
    setPage('lyrics');
    setEditPage('lyrics');
    setLibraryReturn(null);
    setReturnTrial(null);
  }, []);

  const leave = useCallback(() => {
    setSession((current) => {
      current?.dispose();
      return null;
    });
    setPage((p) => (p === 'library' ? p : 'home'));
    setSettingsOpen(false);
    setLibraryReturn(null);
    setReturnTrial(null);
  }, []);

  /** 切换页面；离开有未保存草稿的样式库先确认。返回是否切换了。 */
  const goto = async (next: Page): Promise<boolean> => {
    if (next === page) return true;
    if (page === 'library' && !(await confirmDiscardTemplate(library))) return false;
    if (page === 'library') {
      library.discardDraft();
      setLibraryReturn(null);
    }
    if (next === 'export' && (page === 'lyrics' || page === 'orchestration' || page === 'appearance')) setEditPage(page);
    setPage(next);
    return true;
  };

  /** 返回栏的“保存并返回试看”：有修改先保存（失败就留在样式库），回到外观页试看当前选中的个人模板（默认是副本）。 */
  const returnToAppearance = async () => {
    const pending = libraryReturn;
    if (!pending) return;
    if (library.isDirty && !(await library.save())) return;
    const selection = library.state.selection;
    const templateId = selection?.libraryId === 'personal' && selection.templateId ? selection.templateId : pending.templateId;
    setReturnTrial({ targets: pending.targets, templateId });
    if (!(await goto('appearance'))) setReturnTrial(null);
  };

  const openSettings = (focus?: SettingsGroup) => {
    setSettingsFocus(focus);
    setSettingsOpen(true);
  };

  /** 关闭工程：有未保存的修改先问（保存后关闭、不保存、取消）。 */
  const close = async () => {
    if (!session) return;
    let policy: 'requireClean' | 'discard' = 'requireClean';
    if (session.state.summary.isDirty) {
      const choice = await askCloseUnsaved();
      if (choice === 'cancel') return;
      if (choice === 'save' && !(await save(session))) return;
      if (choice === 'discard') policy = 'discard';
    }
    try {
      await closeProject(client, session.projectSessionId, policy);
      leave();
    } catch (error) {
      session.notify({ level: 'error', message: `关闭失败：${errorText(error)}` });
    }
  };

  /** 导出页的修复入口：设置面板的某一组，或歌词页的某一句（离开导出页，任务不停）。 */
  const fix = (target: CheckFix) => {
    if (!session) return;
    if (target.kind === 'settings') {
      openSettings(target.group);
      return;
    }
    session.selectBranch(target.branchId);
    session.select({ lineId: target.lineId, ...(target.tokenId ? { tokenId: target.tokenId } : {}) });
    setPage('lyrics');
  };

  return (
    <div className="app">
      <AppBar
        page={page}
        session={session}
        connection={snapshot.state}
        renderDevice={renderDevice}
        onPage={(next) => void goto(next)}
        onBack={() => setPage(editPage)}
        onSave={(saveAs) => (session ? save(session, saveAs) : Promise.resolve(false))}
        onClose={close}
        onProjectSettings={() => openSettings()}
        onAppSettings={() => setAppSettingsOpen(true)}
        onAbout={() => setAboutOpen(true)}
      />
      <main className="app-main">
        {page === 'lyrics' && session ? (
          <Workspace
            session={session}
            audio={audio}
            appearance={appearance}
            orchestration={orchestration}
            onOpen={(next) => void goto(next)}
            onUndo={() => void session.undo()}
            onRedo={() => void session.redo()}
            onSave={() => void save(session)}
          />
        ) : page === 'orchestration' && session && orchestration ? (
          <OrchestrationPage
            session={session}
            store={orchestration}
            audio={audio}
            onUndo={() => void session.undo()}
            onRedo={() => void session.redo()}
            onSave={() => void save(session)}
          />
        ) : page === 'appearance' && session && appearance && orchestration ? (
          <AppearancePage
            session={session}
            store={appearance}
            orchestration={orchestration}
            library={library}
            thumbnails={thumbnails}
            audio={audio}
            onUndo={() => void session.undo()}
            onRedo={() => void session.redo()}
            onSave={() => void save(session)}
            onEditCopy={(request) => {
              setLibraryReturn({ ...request, projectSessionId: session.projectSessionId });
              setPage('library');
            }}
            returnTrial={returnTrial}
            onReturnTrialDone={endReturnTrial}
          />
        ) : page === 'export' && session && exporter ? (
          <ExportPage session={session} store={exporter} onFix={fix} />
        ) : page === 'library' ? (
          <LibraryPage
            store={library}
            components={components}
            drafts={drafts}
            thumbnails={thumbnails}
            returnBar={
              libraryReturn && session?.projectSessionId === libraryReturn.projectSessionId
                ? { text: `来自 外观 · ${libraryReturn.targetText}`, onReturn: returnToAppearance, onDismiss: () => setLibraryReturn(null) }
                : undefined
            }
          />
        ) : (
          <Home ready={snapshot.state === 'ready'} current={session} notice={homeMessage} onEnter={enter} onResume={() => setPage('lyrics')} />
        )}
      </main>
      {session && (
        <SettingsDrawer key={session.projectSessionId} session={session} open={settingsOpen} focus={settingsFocus} onClose={() => setSettingsOpen(false)} />
      )}
      <AppSettings open={appSettingsOpen} onClose={() => setAppSettingsOpen(false)} onAbout={() => setAboutOpen(true)} />
      <AboutDialog open={aboutOpen} onClose={() => setAboutOpen(false)} />
      <SaveLocationHost />
      <DialogHost />
      <ToastHost />
    </div>
  );
}

const NO_SESSION = new ObservableValue<EditorState | undefined>(undefined);

/** 每个工程一个播放控制器，换音频时重建；StrictMode 的双重挂载下旧实例会被释放。 */
function useProjectAudio(session: EditorSession | null): AudioController | null {
  const assetId = useObservableSelector<EditorState | undefined, string | undefined>(session?.state$ ?? NO_SESSION, (s) => s?.summary.audioAssetId);
  const [audio, setAudio] = useState<AudioController | null>(null);
  useEffect(() => {
    if (!session || !assetId) {
      setAudio(null);
      return;
    }
    const controller = new AudioController();
    setAudio(controller);
    const backendWaveform = session.supports('project-sessions.waveform.read') && session.supports('project-sessions.waveform.peaks.read');
    void controller.load(client, { assetId, projectSessionId: session.projectSessionId, backendWaveform });
    return () => controller.dispose();
  }, [session, assetId]);
  return audio;
}

/** 每个工程一个页面状态（编排、外观、导出）：切换页面时选择和任务保留。 */
function useProjectStore<T extends { dispose(): void }>(session: EditorSession | null, create: (s: EditorSession) => T): T | null {
  const [store, setStore] = useState<T | null>(null);
  useEffect(() => {
    if (!session) {
      setStore(null);
      return;
    }
    const next = create(session);
    setStore(next);
    return () => next.dispose();
  }, [session, create]);
  return store;
}

const createOrchestration = (s: EditorSession) => new OrchestrationStore(client, s);
// 外观、导出要按渲染设备偏好渲染（渲染设备设计 §2.2）。
const createAppearance = (s: EditorSession) => new AppearanceStore(client, s, renderDevice);
const createExport = (s: EditorSession) => new ExportStore(client, s, renderDevice);

/**
 * 保存：已有保存位置直接保存；新工程第一次保存和“另存为…”打开“保存到…”对话框（W5 设计 §1）。返回是否保存成功。
 * 另存为之后，工程的保存位置就是新目录。
 */
async function save(session: EditorSession, saveAs = false): Promise<boolean> {
  const summary = session.state.summary;
  if (summary.hasPath && !saveAs) {
    try {
      await saveProject(client, session.projectSessionId);
      await session.refresh();
      return true;
    } catch (error) {
      session.notify({ level: 'error', message: `保存失败：${errorText(error)}` });
      return false;
    }
  }
  const current = projectLocation(session.projectSessionId);
  return askSaveLocation({
    headline: saveAs ? '另存为' : '保存到',
    parent: current ? parentPath(current) : '',
    name: suggestDirectoryName(summary.name),
    ...(current ? { current } : {}),
    save: async (path, progress) => {
      await saveProject(client, session.projectSessionId, path, progress);
      rememberProject(path);
      await session.refresh();
    },
  });
}
