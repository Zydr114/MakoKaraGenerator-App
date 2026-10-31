import type { LyricBranchSummary, PresetApplication } from '../../protocol';
import { KIND_TEXT } from '../library/template-library-store';
import type { AppearanceTarget } from '../model/appearance-targets';
import { inputMappingText } from '../model/branch-inputs';
import { applicationsOn, countByTemplate, orphanTargetText, scopeText } from '../model/preset-applications';
import { propertySummary } from '../model/template-groups';
import type { AppearanceState } from './appearance-store';

export interface ApplicationActions {
  setEnabled: (application: PresetApplication, enabled: boolean) => void;
  remove: (applications: readonly PresetApplication[], text: string) => void;
  update: (application: PresetApplication) => void;
  /** 能用库里的当前版本更新时返回 undefined，否则返回原因。 */
  updateBlocked: (application: PresetApplication) => string | undefined;
}

/**
 * 检查器的“模板”节（W3 设计 §2.6，G15）：选中目标上的直接分配，停用／启用、移除、按库里的当前版本更新。
 * 工程默认另外列出目标已失效的分配和认不出的目标上的分配。
 */
export function ApplicationsPanel(props: {
  view: AppearanceState;
  targets: readonly AppearanceTarget[];
  branches: readonly LyricBranchSummary[];
  busy: boolean;
  actions: ApplicationActions;
  onRetry: () => void;
}) {
  const { view, targets, actions } = props;
  const applications = view.applications;
  if (!applications) return <p className="hint">后端没有开放模板分配的读取：只能应用，不满意可以撤销。</p>;
  if (!applications.index) {
    return applications.status === 'error' ? <ReadError text={applications.error} onRetry={props.onRetry} /> : <mdui-linear-progress />;
  }
  const { index } = applications;
  const direct = applicationsOn(index, targets);
  const isProject = targets[0]?.kind === 'project';

  return (
    <>
      {applications.status === 'error' && <ReadError text={applications.error} onRetry={props.onRetry} />}
      {targets.length > 1 ? (
        <MultiSummary targets={targets.length} applications={direct} busy={props.busy} actions={actions} />
      ) : direct.length === 0 ? (
        <p className="hint">—　没有直接分配，外观由上一层继承。</p>
      ) : (
        <div className="application-list">
          {direct.map((app) => (
            <ApplicationRow key={app.applicationId} application={app} branches={props.branches} busy={props.busy} actions={actions} />
          ))}
        </div>
      )}
      {direct.length > 1 && targets.length === 1 && <p className="hint">同一目标有多个分配时，后面的覆盖前面的同类属性。</p>}
      {isProject && (index.invalid.length > 0 || index.other.length > 0) && <Orphans invalid={index.invalid} other={index.other} busy={props.busy} actions={actions} />}
    </>
  );
}

function ApplicationRow(props: { application: PresetApplication; branches: readonly LyricBranchSummary[]; busy: boolean; actions: ApplicationActions }) {
  const { application: app, actions } = props;
  const blocked = actions.updateBlocked(app);
  const newVersion = app.source.comparable && app.source.newVersionAvailable === true;
  return (
    <div className={`application-row${app.enabled ? '' : ' disabled'}`} data-application-id={app.applicationId}>
      <div className="application-head">
        <span className="application-name">{app.snapshot.name}</span>
        {!app.enabled && <span className="application-badge">已停用</span>}
        {newVersion && <span className="application-badge accent">库里有新版本</span>}
      </div>
      <span className="muted">
        {KIND_TEXT[app.snapshot.kind]} · {propertySummary(app.snapshot.kind, app.snapshot.properties)}
        {app.snapshot.hasEffectRecipes || app.snapshot.hasPackageMaterials ? ' · 含效果' : ''}
        {scopeText(app) ? ` · ${scopeText(app)}` : ''}
      </span>
      {Object.keys(app.branchInputs).length > 0 && <span className="muted application-inputs">读取：{inputMappingText(app.branchInputs, props.branches)}</span>}
      <div className="application-actions">
        <mdui-button variant="text" disabled={props.busy} onClick={() => actions.setEnabled(app, !app.enabled)}>
          {app.enabled ? '停用' : '启用'}
        </mdui-button>
        <mdui-button variant="text" disabled={props.busy || !!blocked} title={blocked ?? '取库里这个模板的当前版本；内容相同时不产生历史'} onClick={() => actions.update(app)}>
          更新
        </mdui-button>
        <mdui-button variant="text" disabled={props.busy} onClick={() => actions.remove([app], `“${app.snapshot.name}”`)}>
          移除
        </mdui-button>
      </div>
      {blocked && <span className="hint">{blocked}</span>}
    </div>
  );
}

function MultiSummary(props: { targets: number; applications: readonly PresetApplication[]; busy: boolean; actions: ApplicationActions }) {
  const { applications } = props;
  if (applications.length === 0) return <p className="hint">选中的 {props.targets} 个目标都没有直接分配。</p>;
  const counts = countByTemplate(applications);
  return (
    <div className="application-summary">
      <p>
        {props.targets} 个目标上有 {applications.length} 个直接分配：{counts.map(([name, n]) => (n > 1 ? `${name} ×${n}` : name)).join('、')}
      </p>
      <mdui-button variant="text" disabled={props.busy} onClick={() => props.actions.remove(applications, `${applications.length} 个分配`)}>
        全部移除
      </mdui-button>
    </div>
  );
}

function Orphans(props: { invalid: readonly PresetApplication[]; other: readonly PresetApplication[]; busy: boolean; actions: ApplicationActions }) {
  return (
    <div className="application-orphans">
      {props.invalid.length > 0 && (
        <>
          <h4 className="subsection-title">目标已不存在的分配 · {props.invalid.length}</h4>
          <ul className="orphan-list">
            {props.invalid.map((app) => (
              <li key={app.applicationId} data-application-id={app.applicationId}>
                {app.snapshot.name} <span className="muted">· {orphanTargetText(app)}</span>
              </li>
            ))}
          </ul>
          <p className="hint">这些分配还在工程里，但已经不起作用。</p>
          <mdui-button variant="text" disabled={props.busy} onClick={() => props.actions.remove(props.invalid, `${props.invalid.length} 个失效分配`)}>
            全部移除
          </mdui-button>
        </>
      )}
      {props.other.length > 0 && (
        <>
          <h4 className="subsection-title">认不出的目标上的分配 · 只读</h4>
          <ul className="orphan-list">
            {props.other.map((app) => (
              <li key={app.applicationId}>
                {app.snapshot.name} <span className="muted">· {orphanTargetText(app)}</span>
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  );
}

function ReadError(props: { text: string | undefined; onRetry: () => void }) {
  return (
    <p className="notice-error">
      {props.text}{' '}
      <mdui-button variant="text" onClick={props.onRetry}>
        重试
      </mdui-button>
    </p>
  );
}
