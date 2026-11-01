// 模板里的效果包（W7 设计 §2.3）：材质和效果页签顶部共用。列出模板用到的包和这份草稿里导入过的包，
// 导入 .mkfx 时先上传、检查，看过包的信息和诊断再加入；同一包的别的修订在用时改为“替换为新版本”。

import { useState } from 'react';
import type { EffectPackageInspectResponse, EffectPackageReference, FullTemplateDefinition } from '../../protocol';
import { EXPORT_KIND_TEXT, packageKey, packageUsers, referenceKey, shortRevision, usedPackageKeys } from '../model/full-template';
import { toast } from '../ui/legacy/feedback';
import { FileButton } from '../ui/legacy/fields';
import { errorText } from '../session/editor-session';
import type { TemplateDraft, TemplateLibraryStore } from './template-library-store';

type Pending = { status: 'inspecting'; fileName: string } | { status: 'ready'; pkg: EffectPackageInspectResponse } | { status: 'failed'; fileName: string; message: string };

export function PackagesPanel(props: {
  store: TemplateLibraryStore;
  definition: FullTemplateDefinition;
  /** 没有草稿（内置模板）时只读。 */
  draft: TemplateDraft | undefined;
  catalog: ReadonlyMap<string, EffectPackageInspectResponse>;
  disabled: boolean;
}) {
  const { store, definition, draft, catalog } = props;
  const [pending, setPending] = useState<Pending | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const editable = !!draft && !props.disabled;
  const references = listed(definition, draft);
  const used = new Set(usedPackageKeys(definition));

  const pick = async (file: File) => {
    setNotice(null);
    setPending({ status: 'inspecting', fileName: file.name });
    try {
      const pkg = await store.inspectPackageFile(file);
      setPending({ status: 'ready', pkg });
    } catch (error) {
      setPending({ status: 'failed', fileName: file.name, message: errorText(error) });
    }
  };
  const add = (pkg: EffectPackageInspectResponse) => {
    const replaced = store.addPackage(pkg);
    setPending(null);
    toast(replaced.length > 0 ? `已把“${pkg.name}”换成新版本 ${pkg.version}` : `已加入“${pkg.name}”，在材质或效果里选用它`);
  };
  const remove = (reference: EffectPackageReference) => {
    const users = store.removePackage(referenceKey(reference));
    setNotice(users.length > 0 ? `不能移除：${users.join('、')}在用它。` : null);
  };

  return (
    <section className="package-panel" aria-label="效果包">
      <div className="package-panel-header">
        <h3 className="template-advanced-title">效果包 · {references.length} 个</h3>
        {draft && <FileButton label="导入效果包…" accept=".mkfx" variant="text" onPick={(file) => void pick(file)} disabled={!editable || pending?.status === 'inspecting'} />}
      </div>
      {references.length === 0 && <p className="hint">模板里还没有效果包。{draft ? '导入 .mkfx 后可以用作材质或效果。' : ''}</p>}
      {references.length > 0 && (
        <ul className="package-list">
          {references.map((reference) => {
            const key = referenceKey(reference);
            const pkg = catalog.get(key);
            const users = packageUsers(definition, key);
            return (
              <li key={key} className="package-item" data-package-id={reference.packageId} data-revision={reference.packageRevision}>
                <span className="package-item-text">
                  <span className="package-name">{pkg ? `${pkg.name} ${pkg.version}` : reference.packageId}</span>
                  <span className="muted">
                    修订 {shortRevision(reference.packageRevision)}
                    {pkg ? ` · ${pkg.exports.length} 个导出` : ' · 没有读到包信息'}
                    {' · '}
                    {used.has(key) ? `用于 ${users.join('、')}` : '未使用（保存时不带上）'}
                  </span>
                </span>
                {draft && (
                  <mdui-button-icon disabled={!editable} onClick={() => remove(reference)} aria-label={`移除 ${pkg?.name ?? reference.packageId}`}>
                    <mdui-icon-close />
                  </mdui-button-icon>
                )}
              </li>
            );
          })}
        </ul>
      )}
      {notice && <p className="notice-error package-notice">{notice}</p>}
      {pending && <PendingCard pending={pending} definition={definition} references={references} disabled={!editable} onAdd={add} onClose={() => setPending(null)} />}
    </section>
  );
}

/** 检查结果：包的信息和诊断，确认后再加入。 */
function PendingCard(props: {
  pending: Pending;
  definition: FullTemplateDefinition;
  references: readonly EffectPackageReference[];
  disabled: boolean;
  onAdd: (pkg: EffectPackageInspectResponse) => void;
  onClose: () => void;
}) {
  const { pending } = props;
  if (pending.status === 'inspecting') {
    return (
      <div className="package-pending" data-status="inspecting">
        <mdui-linear-progress />
        <span className="muted">正在上传并检查 {pending.fileName}…</span>
      </div>
    );
  }
  if (pending.status === 'failed') {
    return (
      <div className="package-pending" data-status="failed">
        <p className="notice-error">读不出 {pending.fileName}：{pending.message}</p>
        <mdui-button variant="text" onClick={props.onClose}>
          关闭
        </mdui-button>
      </div>
    );
  }
  const { pkg } = pending;
  const key = packageKey(pkg.packageId, pkg.packageRevision);
  const present = props.references.some((r) => referenceKey(r) === key);
  const older = usedPackageKeys(props.definition).some((k) => k.startsWith(`${pkg.packageId}\u0000`) && k !== key);
  return (
    <div className="package-pending" data-status="ready" data-valid={pkg.structureValid}>
      <div className="package-pending-title">
        <strong>{pkg.name}</strong> {pkg.version} · 修订 {shortRevision(pkg.packageRevision)} · {pkg.files.length} 个文件
      </div>
      <ul className="package-exports">
        {pkg.exports.map((e) => (
          <li key={e.id}>
            {e.name}（{EXPORT_KIND_TEXT[e.kind]}，{e.parameterIds.length} 个参数）
          </li>
        ))}
      </ul>
      {pkg.diagnostics.length > 0 && (
        <ul className="package-diagnostics">
          {pkg.diagnostics.map((d, i) => (
            <li key={i}>
              <code>{d.code}</code> {d.path && <span className="muted">{d.path}</span>} {d.message}
            </li>
          ))}
        </ul>
      )}
      {!pkg.structureValid && <p className="notice-error">包的结构无效，不能加入。</p>}
      {pkg.structureValid && older && !present && <p className="hint">模板正在用这个包的另一个修订。替换后，用到它的材质和效果都改用新修订；新版本没有的参数会标出来。</p>}
      <div className="package-pending-actions">
        <mdui-button variant="text" onClick={props.onClose}>
          {present || !pkg.structureValid ? '关闭' : '取消'}
        </mdui-button>
        {pkg.structureValid && (
          <mdui-button variant="tonal" disabled={props.disabled || present} onClick={() => props.onAdd(pkg)}>
            {present ? '已在模板里' : older ? '替换为新版本' : '加入模板'}
          </mdui-button>
        )}
      </div>
    </div>
  );
}

/** 列出的包：模板 packages 里的，加上草稿里导入过、还没用到的。 */
function listed(definition: FullTemplateDefinition, draft: TemplateDraft | undefined): EffectPackageReference[] {
  const seen = new Set<string>();
  return [...definition.packages, ...(draft?.imported ?? [])].filter((r) => {
    const key = referenceKey(r);
    if (seen.has(key)) return false;
    seen.add(key);
    return true;
  });
}
