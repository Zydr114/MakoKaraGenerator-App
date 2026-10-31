// 编排页各部分共用的小组件。

import { useReducer, type ReactNode } from 'react';

/** 图标按钮，悬停写明作用；不可用时写明原因。mdui 按钮禁用后不响应指针，title 放在外层。 */
export function ToolButton(props: { label: string; reason: string | undefined; disabled?: boolean; className: string; onClick: () => void; children: ReactNode }) {
  return (
    <span className="tool-button" title={props.reason ? `${props.label}：${props.reason}` : props.label}>
      <mdui-button-icon className={props.className} aria-label={props.label} disabled={!!props.reason || !!props.disabled} onClick={props.onClick}>
        {props.children}
      </mdui-button-icon>
    </span>
  );
}

/** 只读信息的一行（放在 dl.override-list 里）。 */
export function Row(props: { label: string; value: string }) {
  return (
    <div className="override-row">
      <dt>{props.label}</dt>
      <dd>{props.value}</dd>
    </div>
  );
}

/**
 * 下拉框提交没成功（被拒绝或 no-op）时，受控值没变，mdui-select 却已显示新选的项：用返回的 epoch 作 key，reset 后重建。
 * 重建推迟到下一个宏任务：mdui-select 发出 change 后还要等自己更新完再做校验，这期间被摘掉会抛异常。
 */
export function useSelectReset(): [number, () => void] {
  const [epoch, bump] = useReducer((x: number) => x + 1, 0);
  return [epoch, () => void setTimeout(bump, 0)];
}
