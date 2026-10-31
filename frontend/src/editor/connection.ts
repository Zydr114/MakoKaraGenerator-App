// 编辑器的 MakoClient 单例。放在模块级，避免 StrictMode 双重挂载创建两个 client。
// 与控制台各自持有实例；同一标签页内两页共用 sessionStorage 中的身份，经 resume 接回。

import { useEffect, useRef, useState } from 'react';
import { MakoClient, devServerBootstrapProvider } from '../client';
import { TemplateFacts } from './appearance/template-facts';
import { FontCatalog } from './library/font-catalog';
import { TemplateTransfers } from './library/template-transfers';
import { RenderDeviceStore } from './preview/render-device';
import { useObservable } from './ui/hooks';

export const client = MakoClient.create(devServerBootstrapProvider());

/** 渲染设备偏好（渲染设备设计 §2.1）：整个应用一个。连接重置（Server 重启）后旧的检测结果作废，就绪后重新检测。 */
export const renderDevice = new RenderDeviceStore(client);
client.on('reset', () => renderDevice.forget());

/** 后端字体目录（W7 设计 §2.5）：用到时读一次，连接重置后重读。 */
export const fonts = new FontCatalog(client);
client.on('reset', () => fonts.forget());

/** 外观页用到的模板事实（W7 设计 §2.7）：能用于哪些目标、要读取哪些来源输入。按模板修订缓存，连接重置后作废。 */
export const templateFacts = new TemplateFacts(client);
client.on('reset', () => templateFacts.forget());

/** .mktpl 导入导出（W7 设计 §2.6）：记着本页拿到的传输对象何时过期；连接重置后是新的客户端身份，配额重新计。 */
export const templateTransfers = new TemplateTransfers(client);
client.on('reset', () => templateTransfers.forget());

let started = false;

/** 首次挂载时连接；Server 重启或连接失败时按 1s、2s、4s…（上限 10s）自动重新初始化。 */
export function useConnection() {
  const snapshot = useObservable(client.state$);
  const [retryTick, setRetryTick] = useState(0);
  const attempts = useRef(0);

  useEffect(() => {
    if (started) return;
    started = true;
    client.initialize().catch(() => undefined);
  }, []);

  useEffect(() => {
    if (snapshot.state !== 'hostRestarted' && snapshot.state !== 'failed') {
      attempts.current = 0;
      return;
    }
    const delay = Math.min(10_000, 1000 * 2 ** attempts.current);
    const timer = setTimeout(() => {
      attempts.current++;
      client.reinitialize('自动重新初始化').catch(() => setRetryTick((t) => t + 1));
    }, delay);
    return () => clearTimeout(timer);
  }, [snapshot.state, retryTick]);

  return snapshot;
}
