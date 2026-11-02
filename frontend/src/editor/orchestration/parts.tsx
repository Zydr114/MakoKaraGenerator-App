// 编排页各部分共用的小组件：检查器的标题与分组、只读信息、小图标按钮、读取失败。

import type { ReactNode } from 'react';
import { Button, IconButton } from '../ui/Button';
import { cls } from '../ui/cls';
import { Icon, type IconName } from '../ui/Icon';
import { Tag } from '../ui/indicators';

export const NO_PROGRAM = '这个来源还没有编排程序（后端缺陷 R4），暂时不能编排。';

/**
 * 小图标按钮（槽位、标记的上移、下移、删除）：可用时悬停提示写作用；不可用时写明原因。
 * 停用的按钮收不到指针事件，原因写在外层的 title 上。
 */
export function ToolButton(props: { icon: IconName; label: string; reason: string | undefined; disabled?: boolean; onPress: () => void }) {
  const button = (
    <IconButton icon={props.icon} size="sm" label={props.label} isDisabled={!!props.reason || !!props.disabled} onPress={props.onPress} {...(props.reason ? { tooltip: false } : {})} />
  );
  return props.reason ? (
    <span className="tool-wrap" title={`${props.label}：${props.reason}`}>
      {button}
    </span>
  ) : (
    button
  );
}

/** 检查器的标题：上面一行是所在位置，下面是名称和种类。data-path 是完整路径（冒烟脚本用）。 */
export function InspectorHead(props: { crumbs: readonly string[]; title: string; kind: string; id?: string }) {
  return (
    <header className="ihead" data-path={[...props.crumbs, props.title].join(' / ')}>
      {props.crumbs.length > 0 && (
        <div className="crumbs">
          {props.crumbs.map((crumb, i) => (
            <span key={i}>
              {i > 0 && <Icon name="chevron_right" />}
              {crumb}
            </span>
          ))}
        </div>
      )}
      <h2 {...(props.id ? { title: `ID：${props.id}` } : {})}>
        <span className="ihead-name">{props.title}</span>
        <Tag>{props.kind}</Tag>
      </h2>
    </header>
  );
}

/** 检查器的一组：标题（主色）、标题右边的控件、内容。 */
export function Group(props: { title: string; extra?: ReactNode; className?: string; children?: ReactNode }) {
  return (
    <section className={cls('grp', props.className)} aria-label={props.title}>
      <h4>
        <span className="grp-title">{props.title}</span>
        {props.extra && <span className="grp-extra">{props.extra}</span>}
      </h4>
      {props.children}
    </section>
  );
}

/** 只读信息的一行（放在 dl.info-list 里）。 */
export function Row(props: { label: string; value: string }) {
  return (
    <div>
      <dt>{props.label}</dt>
      <dd>{props.value}</dd>
    </div>
  );
}

/** 灰色的说明；error 时是错误色，读屏会念出来。 */
export function Note(props: { children: ReactNode; error?: boolean; className?: string }) {
  return (
    <p className={cls('note', props.error && 'err', props.className)} {...(props.error ? { role: 'alert' } : {})}>
      {props.children}
    </p>
  );
}

export function ReadError(props: { error: string | undefined; onRetry: () => void }) {
  return (
    <div className="read-error" role="alert">
      <span>{props.error}</span>
      <Button variant="text" size="sm" onPress={props.onRetry}>
        重试
      </Button>
    </div>
  );
}
