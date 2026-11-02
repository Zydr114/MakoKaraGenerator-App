import { useEffect, useState, useSyncExternalStore } from 'react';
import type { ObservableValue } from '../../client';

export function useObservable<T>(source: ObservableValue<T>): T {
  return useSyncExternalStore(source.subscribe, source.get);
}

/** 只订阅状态里的一个值；select 要返回原始值或稳定引用，否则每次通知都会重新渲染。 */
export function useObservableSelector<T, S>(source: Pick<ObservableValue<T>, 'subscribe' | 'get'>, select: (value: T) => S): S {
  return useSyncExternalStore(source.subscribe, () => select(source.get()));
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
