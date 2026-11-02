import type { LyricBranchSummary, PresetApplication, TemplateLibraryId, TemplateSummary } from '../../protocol';
import { KIND_TEXT } from '../library/template-library-store';
import { TemplateThumb } from '../library/TemplateThumb';
import type { TemplateThumbnails } from '../library/template-thumbnails';
import type { AppearanceTarget } from '../model/appearance-targets';
import { inputMappingText } from '../model/branch-inputs';
import { applicationsOn, countByTemplate, orphanTargetText, scopeText } from '../model/preset-applications';
import { propertySummary } from '../model/template-groups';
import { Button } from '../ui/Button';
import { cls } from '../ui/cls';
import { Icon } from '../ui/Icon';
import { ProgressBar, Tag } from '../ui/indicators';
import { Note, ReadError } from '../workspace/panel';
import type { AppearanceState } from './appearance-store';

export interface ApplicationActions {
  setEnabled: (application: PresetApplication, enabled: boolean) => void;
  remove: (applications: readonly PresetApplication[], text: string) => void;
  update: (application: PresetApplication) => void;
  /** 能用库里的当前版本更新时返回 undefined，否则返回原因。 */
  updateBlocked: (application: PresetApplication) => string | undefined;
  /** 分配的模板现在在哪个库里（缩略图、“内置／我的”用）；库还没读到或已经没有时是 undefined。 */
  entry: (application: PresetApplication) => { libraryId: TemplateLibraryId; revision: string; summary: TemplateSummary } | undefined;
}

const LIBRARY_TEXT: Record<TemplateLibraryId, string> = { builtin: '内置', personal: '我的' };

/**
 * 检查器的“模板”组（W3 设计 §2.6，G15）：选中目标上的直接分配，一个分配一张卡片（缩略图、名称、用途），停用／启用、更新、移除。
 * 工程默认另外列出目标已失效的分配和认不出的目标上的分配。
 */
export function ApplicationsPanel(props: {
  view: AppearanceState;
  targets: readonly AppearanceTarget[];
  branches: readonly LyricBranchSummary[];
  thumbnails: TemplateThumbnails;
  busy: boolean;
  actions: ApplicationActions;
  onRetry: () => void;
}) {
  const { view, targets, actions } = props;
  const applications = view.applications;
  if (!applications) return <Note>后端没有开放模板分配的读取：只能应用，不满意可以撤销。</Note>;
  if (!applications.index) {
    return applications.status === 'error' ? <ReadError error={applications.error} onRetry={props.onRetry} /> : <ProgressBar label="正在读取模板分配" />;
  }
  const { index } = applications;
  const direct = applicationsOn(index, targets);
  const isProject = targets[0]?.kind === 'project';

  return (
    <>
      {applications.status === 'error' && <ReadError error={applications.error} onRetry={props.onRetry} />}
      {targets.length > 1 ? (
        <MultiSummary targets={targets.length} applications={direct} busy={props.busy} actions={actions} />
      ) : direct.length === 0 ? (
        <Note className="no-application">没有直接分配，外观由上一层继承。</Note>
      ) : (
        <div className="acards">
          {direct.map((app) => (
            <ApplicationCard key={app.applicationId} application={app} branches={props.branches} thumbnails={props.thumbnails} busy={props.busy} actions={actions} />
          ))}
        </div>
      )}
      {direct.length > 1 && targets.length === 1 && <Note>同一目标有多个分配时，后面的覆盖前面的同类属性。</Note>}
      {isProject && (index.invalid.length > 0 || index.other.length > 0) && <Orphans invalid={index.invalid} other={index.other} busy={props.busy} actions={actions} />}
    </>
  );
}

function ApplicationCard(props: {
  application: PresetApplication;
  branches: readonly LyricBranchSummary[];
  thumbnails: TemplateThumbnails;
  busy: boolean;
  actions: ApplicationActions;
}) {
  const { application: app, actions } = props;
  const blocked = actions.updateBlocked(app);
  const entry = actions.entry(app);
  const newVersion = app.source.comparable && app.source.newVersionAvailable === true;
  const effects = app.snapshot.hasEffectRecipes || app.snapshot.hasPackageMaterials;
  const scope = scopeText(app);
  return (
    <div className={cls('acard', 'application-row', !app.enabled && 'disabled')} data-application-id={app.applicationId}>
      <span className="athumb" title={entry ? '样式库里这个模板的试样' : undefined}>
        {entry ? (
          <TemplateThumb thumbnails={props.thumbnails} libraryId={entry.libraryId} libraryRevision={entry.revision} summary={entry.summary} />
        ) : (
          <Icon name="style" />
        )}
      </span>
      <div className="ainfo">
        <b className="application-name">{app.snapshot.name}</b>
        <span className="small">
          {entry ? `${LIBRARY_TEXT[entry.libraryId]}，` : ''}
          {KIND_TEXT[app.snapshot.kind]}
          {effects ? '，含效果' : ''}
        </span>
        <span className="small faint">
          {propertySummary(app.snapshot.kind, app.snapshot.properties)}
          {scope ? ` · ${scope}` : ''}
        </span>
        {(!app.enabled || newVersion) && (
          <span className="atags">
            {!app.enabled && <Tag>已停用</Tag>}
            {newVersion && <Tag tone="accent">库里有新版本</Tag>}
          </span>
        )}
      </div>
      {Object.keys(app.branchInputs).length > 0 && <span className="small application-inputs">读取：{inputMappingText(app.branchInputs, props.branches)}</span>}
      <div className="aacts">
        <Button variant="ghost" size="sm" icon={app.enabled ? 'visibility_off' : 'visibility'} isDisabled={props.busy} onPress={() => actions.setEnabled(app, !app.enabled)}>
          {app.enabled ? '停用' : '启用'}
        </Button>
        <span className="tool-wrap" title={blocked ?? '取库里这个模板的当前版本；内容相同时不产生历史'}>
          <Button variant="ghost" size="sm" icon="update" isDisabled={props.busy || !!blocked} onPress={() => actions.update(app)}>
            更新
          </Button>
        </span>
        <Button variant="ghost" size="sm" icon="delete" isDisabled={props.busy} onPress={() => actions.remove([app], `“${app.snapshot.name}”`)}>
          移除
        </Button>
      </div>
      {blocked && <Note className="ablocked">{blocked}</Note>}
    </div>
  );
}

function MultiSummary(props: { targets: number; applications: readonly PresetApplication[]; busy: boolean; actions: ApplicationActions }) {
  const { applications } = props;
  if (applications.length === 0) return <Note>选中的 {props.targets} 个目标都没有直接分配。</Note>;
  const counts = countByTemplate(applications);
  return (
    <div className="application-summary">
      <p>
        {props.targets} 个目标上有 {applications.length} 个直接分配：{counts.map(([name, n]) => (n > 1 ? `${name} ×${n}` : name)).join('、')}
      </p>
      <Button variant="text" size="sm" icon="delete" isDisabled={props.busy} onPress={() => props.actions.remove(applications, `${applications.length} 个分配`)}>
        全部移除
      </Button>
    </div>
  );
}

function Orphans(props: { invalid: readonly PresetApplication[]; other: readonly PresetApplication[]; busy: boolean; actions: ApplicationActions }) {
  return (
    <div className="application-orphans">
      {props.invalid.length > 0 && (
        <>
          <div className="sub">
            目标已不存在的分配 · {props.invalid.length}
            <Button variant="text" size="sm" isDisabled={props.busy} onPress={() => props.actions.remove(props.invalid, `${props.invalid.length} 个失效分配`)}>
              全部移除
            </Button>
          </div>
          <ul className="orphan-list">
            {props.invalid.map((app) => (
              <li key={app.applicationId} data-application-id={app.applicationId}>
                {app.snapshot.name} <span className="faint">· {orphanTargetText(app)}</span>
              </li>
            ))}
          </ul>
          <Note>这些分配还在工程里，但已经不起作用。</Note>
        </>
      )}
      {props.other.length > 0 && (
        <>
          <div className="sub">认不出的目标上的分配 · 只读</div>
          <ul className="orphan-list">
            {props.other.map((app) => (
              <li key={app.applicationId}>
                {app.snapshot.name} <span className="faint">· {orphanTargetText(app)}</span>
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  );
}
