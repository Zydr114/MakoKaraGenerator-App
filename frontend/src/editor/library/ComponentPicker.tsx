// 模板的效果／材质页签共用组件选择器（W8 §2.10）：按详情校验用途，不合的列出原因。

import { useEffect, useState } from 'react';
import { BackendError } from '../../client';
import type { ScriptConfigurationDetailResponse, ScriptConfigurationKind } from '../../protocol';
import { errorText } from '../session/editor-session';
import { FormDialog } from '../ui/legacy/FormDialog';
import { Select } from '../ui/legacy/fields';
import type { ComponentLibraryStore } from './component-library-store';

interface Choice {
  id: string;
  name: string;
  detail?: ScriptConfigurationDetailResponse;
  problem?: string;
}

export function ComponentPicker(props: {
  components: ComponentLibraryStore;
  kind: ScriptConfigurationKind;
  problemOf: (detail: ScriptConfigurationDetailResponse) => string | undefined;
  onChoose: (detail: ScriptConfigurationDetailResponse) => boolean;
  disabled: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [loading, setLoading] = useState(false);
  const [choices, setChoices] = useState<Choice[]>([]);
  const [picked, setPicked] = useState('');
  const [failure, setFailure] = useState<string>();
  const [retry, setRetry] = useState(0);
  const { components, kind } = props;
  useEffect(() => {
    if (!open) return;
    let active = true;
    setLoading(true);
    setFailure(undefined);
    setChoices([]);
    const read = async () => {
      // 详情绑定整个组件库修订；读取期间库变动时重读一次。
      for (let attempt = 0; attempt < 2; attempt++) {
        await components.load();
        if (!active) return;
        if (components.state.status !== 'ready') throw new Error(components.state.notice?.message ?? '读取组件库失败。');
        const summaries = components.state.configurations?.filter((c) => c.kind === kind) ?? [];
        const result: Choice[] = new Array(summaries.length);
        let next = 0;
        let conflict = false;
        // 限制同时读取数量，避免一整个组件库同时发请求。
        await Promise.all(Array.from({ length: Math.min(4, summaries.length) }, async () => {
          while (active && next < summaries.length) {
            const index = next++;
            const summary = summaries[index]!;
            try {
              const detail = await components.detailOf(summary.configurationId);
              result[index] = detail
                ? { id: summary.configurationId, name: summary.name, detail }
                : { id: summary.configurationId, name: summary.name, problem: '这个效果组件已经删除。' };
            } catch (error) {
              if (error instanceof BackendError && error.status === 409) conflict = true;
              result[index] = { id: summary.configurationId, name: summary.name, problem: `读取失败：${errorText(error)}` };
            }
          }
        }));
        if (!active) return;
        if (conflict && attempt === 0) continue;
        setChoices(result);
        return;
      }
    };
    void read().catch((error: unknown) => active && setFailure(errorText(error))).finally(() => active && setLoading(false));
    return () => { active = false; };
  }, [open, components, kind, retry]);
  const options = choices.map((choice) => {
    const problem = choice.problem ?? (choice.detail && props.problemOf(choice.detail));
    return { ...choice, problem };
  });
  const current = options.find((choice) => choice.id === picked && !choice.problem) ?? options.find((choice) => !choice.problem);
  return (
    <>
      <mdui-button variant="tonal" disabled={props.disabled} onClick={() => setOpen(true)} aria-label="从效果组件选…">从效果组件选…</mdui-button>
      <FormDialog
        open={open}
        headline="从效果组件选"
        description={kind === 'Material' ? '选用一个材质组件，参数会复制到这个部位。' : '选用一个效果组件，之后可以在模板里独立修改。'}
        className="component-picker-dialog"
        submitText={kind === 'Material' ? '使用材质' : '加入效果'}
        submitDisabled={props.disabled || loading || !current?.detail}
        onSubmit={() => { if (current?.detail && props.onChoose(current.detail)) setOpen(false); }}
        onClose={() => setOpen(false)}
      >
        {loading ? <mdui-linear-progress /> : failure ? (
          <><p className="notice-error">{failure}</p><mdui-button variant="text" onClick={() => setRetry((n) => n + 1)}>重试</mdui-button></>
        ) : options.length === 0 ? <p className="hint">还没有{kind === 'Material' ? '材质' : '效果'}组件。到左栏“效果组件”中导入效果包。</p> : (
          <>
            <Select label="效果组件" value={current?.id ?? ''} options={options.map((choice) => ({ value: choice.id, label: `${choice.name}${choice.problem ? ` · ${choice.problem}` : ''}`, disabled: !!choice.problem }))} onChange={setPicked} disabled={props.disabled} />
            {current?.detail && <p className="hint" data-component-id={current.id}>{current.detail.package.name} · {current.detail.configuration.exportId}</p>}
            {!current && <p className="hint">这些组件不能用在当前{kind === 'Material' ? '材质部位' : '模板用途'}，原因已列在名称旁。</p>}
          </>
        )}
      </FormDialog>
    </>
  );
}
