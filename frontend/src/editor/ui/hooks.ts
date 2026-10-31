import { useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type RefObject } from 'react';
import type { ObservableValue } from '../../client';

export function useObservable<T>(source: ObservableValue<T>): T {
  return useSyncExternalStore(source.subscribe, source.get);
}

/** 只订阅状态里的一个值；select 要返回原始值或稳定引用，否则每次通知都会重新渲染。 */
export function useObservableSelector<T, S>(source: Pick<ObservableValue<T>, 'subscribe' | 'get'>, select: (value: T) => S): S {
  return useSyncExternalStore(source.subscribe, () => select(source.get()));
}

/**
 * 给自定义元素挂 DOM 事件。mdui 组件的事件（change、input、close…）不一定冒泡，
 * React 的合成事件接不到，统一用 addEventListener，避免各处猜 React 的映射规则。
 */
export function useDomEvent<E extends Event = Event>(
  ref: RefObject<HTMLElement | null>,
  type: string,
  handler: (event: E) => void,
): void {
  const latest = useRef(handler);
  // 提交后立即换成最新的处理函数：React 改了 mdui 属性后，Lit 在微任务里发出的事件（例如受控关闭时的 close）
  // 会早于普通 effect 执行，用旧闭包就会读到旧的 props。
  useLayoutEffect(() => {
    latest.current = handler;
  });
  useEffect(() => {
    const element = ref.current;
    if (!element) return;
    const listener = (event: Event) => latest.current(event as E);
    element.addEventListener(type, listener);
    return () => element.removeEventListener(type, listener);
  }, [ref, type]);
}

/** 为一段字节建 object URL，字节换了或组件卸载时释放。 */
export function useObjectUrl(blob: Blob | undefined): string | undefined {
  const [url, setUrl] = useState<string>();
  useEffect(() => {
    if (!blob) {
      setUrl(undefined);
      return;
    }
    const next = URL.createObjectURL(blob);
    setUrl(next);
    return () => URL.revokeObjectURL(next);
  }, [blob]);
  return url;
}
