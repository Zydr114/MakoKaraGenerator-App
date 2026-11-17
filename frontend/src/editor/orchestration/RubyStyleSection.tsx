import { useEffect, useState } from 'react';
import type { ProjectResolvedStyleResponse, ProjectStyleResponse } from '../../protocol';
import { RUBY_VALUE_MAX, rubyStyleCommand, type RubyMode, type RubyProperty } from '../model/ruby-style';
import { errorText, type EditorSession } from '../session/editor-session';
import { useObservable } from '../ui/hooks';
import { Button } from '../ui/Button';
import { NumberField, Select } from '../ui/fields';
import { Group, Note } from '../workspace/panel';

type ReadState = { local: ProjectStyleResponse; resolved?: ProjectResolvedStyleResponse; resolvedError?: string };

/** 句子直接覆盖；实际生效值由后端解析模板及所有作用域。 */
export function RubyStyleSection({ session, branchId, lineId }: { session: EditorSession; branchId: string; lineId: string }) {
  const state = useObservable(session.state$);
  const revision = state.summary.projectRevision;
  const readable = session.supports('project-sessions.style.read') && session.supports('project-sessions.resolved-style.read');
  const [read, setRead] = useState<ReadState>();
  const [error, setError] = useState<string>();
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    let active = true;
    setRead(undefined);
    setError(undefined);
    if (readable) void session.readLineStyle(branchId, lineId, revision).then((value) => {
      if (active) setRead(value);
    }, (reason: unknown) => {
      if (active) setError(errorText(reason));
    });
    return () => { active = false; };
  }, [session, branchId, lineId, revision, readable, attempt]);

  if (!readable) return <Group title="注音"><Note>当前后端未开放样式来源读取。</Note></Group>;
  if (error) return <Group title="注音"><Note error>读取注音样式失败：{error}</Note><Button variant="text" size="sm" onPress={() => setAttempt((value) => value + 1)}>重试</Button></Group>;
  if (!read) return <Group title="注音"><Note>正在读取注音样式……</Note></Group>;

  const disabled = state.summary.isReadOnly || state.syncing || state.pendingCommands > 0 || !session.supportsCommand('styles.patch') || !session.supportsCommand('styles.restore');
  const target = { kind: 'line' as const, branchId, lineId };
  const commit = (property: RubyProperty, mode: RubyMode, value: number) => {
    const command = rubyStyleCommand(target, property, mode, value);
    if (command) void session.command(command);
  };
  return <Group title="注音">
    <Note>设置仅作用于这一句；模板分配和各文本单元的覆盖可能影响实际取值。跟随歌词使用每条注音自身的设置。</Note>
    {(['ruby.relativeScale', 'ruby.gapPx'] as const).map((property) => {
      const scale = property === 'ruby.relativeScale';
      const label = scale ? '注音字号比例' : '注音与正文间距';
      const local = read.local.local.find((change) => change.property === property);
      const value = read.resolved ? (scale ? read.resolved.style.ruby.relativeScale : read.resolved.style.ruby.gapPx) : undefined;
      const mode: RubyMode = !local ? 'inherit' : local.value === null ? 'lyrics' : 'custom';
      const number = typeof local?.value === 'number' ? local.value : (value ?? (scale ? 0.5 : 2));
      const source = read.resolved?.sources.find((entry) => entry.property === property)?.sourcePath;
      const origin = source?.startsWith('presetApplications.') ? '模板分配' : source?.includes(`.lines.${lineId}.styleOverride`) ? '本句直接覆盖' : '工程或上级样式';
      return <div key={property} className="ruby-style-field">
        <Select<RubyMode> label={label} labelPlacement="inside" value={mode} options={[
          { value: 'inherit', label: '继承上级' },
          { value: 'lyrics', label: '跟随歌词中的注音设置' },
          { value: 'custom', label: '自定义' },
        ]} disabled={disabled} onChange={(next) => commit(property, next, number)} />
        {mode === 'custom' && <NumberField label={label} labelPlacement="inside" value={scale ? number * 100 : number} suffix={scale ? '%' : 'px'}
          {...(scale ? { greaterThan: 0, max: RUBY_VALUE_MAX * 100 } : { min: 0, max: RUBY_VALUE_MAX })} disabled={disabled}
          check={(input) => scale && (!Number.isFinite(input / 100) || input / 100 <= 0) ? '比例超出可用范围。' : undefined}
          onCommit={(input) => commit(property, 'custom', scale ? input / 100 : input)} />}
        {value !== undefined && <Note>当前静态结果：{value === null ? '跟随歌词中的注音设置' : scale ? `${Math.round(value * 10000) / 100}%` : `${value} px`}（{source ? origin : '来源未提供'}）</Note>}
      </div>;
    })}
    {read.resolvedError && <Note>当前句子无法读取静态结果：{read.resolvedError}</Note>}
    {state.summary.isReadOnly && <Note>工程只读，不能编辑注音设置。</Note>}
    {(!session.supportsCommand('styles.patch') || !session.supportsCommand('styles.restore')) && <Note>当前后端未开放注音样式编辑。</Note>}
  </Group>;
}
