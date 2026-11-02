import { useState } from 'react';
import { CONTRACT_VERSION } from '../../protocol';
import { client, renderDevice } from '../connection';
import { logoWide } from '../theme/logo/logo';
import { Button } from '../ui/Button';
import { Dialog } from '../ui/Dialog';
import { toast } from '../ui/feedback';
import { useObservable } from '../ui/hooks';
import { CONNECTION_TEXT, renderDeviceText } from './StatusChip';
import './shell.css';

/** 主要第三方组件及其许可（静态清单，界面改版 §2.5）。 */
const THIRD_PARTY: readonly (readonly [name: string, license: string])[] = [
  ['React', 'MIT'],
  ['React Aria Components', 'Apache-2.0'],
  ['Material Color Utilities', 'Apache-2.0'],
  ['Material Symbols', 'Apache-2.0'],
  ['Roboto Flex', 'OFL-1.1'],
  ['CodeMirror', 'MIT'],
];

/** 桌面外壳里运行时是 Electron 的版本。 */
function runtimeText(): string {
  const electron = /Electron\/([\d.]+)/.exec(navigator.userAgent)?.[1];
  return electron ? `Electron ${electron}` : '浏览器';
}

/**
 * 关于（界面改版 §2.5）：横版标志、产品版本（后端能力信息的 productVersion）、前端消费的合同版本、渲染设备、运行环境，
 * 以及主要第三方组件的许可。仓库加入许可证、公开源代码之后再加“许可证”一行和“源代码”链接。
 */
export function AboutDialog(props: { open: boolean; onClose: () => void }) {
  const snapshot = useObservable(client.state$);
  const device = useObservable(renderDevice.state$);
  const [licenses, setLicenses] = useState(false);
  const product = snapshot.productVersion ?? '未知';
  const deviceText = `${renderDeviceText(renderDevice) ?? '未检测'}${device.preference === 'default' ? '（跟随后端默认）' : '（偏好）'}`;
  const facts: readonly (readonly [string, string])[] = [
    ['后端', `${product}，${CONNECTION_TEXT[snapshot.state]}`],
    ['合同', CONTRACT_VERSION],
    ['渲染设备', deviceText],
    ['运行环境', runtimeText()],
  ];

  const copy = async () => {
    const text = ['MakoKara Generator', ...facts.map(([k, v]) => `${k}：${v}`), `浏览器标识：${navigator.userAgent}`].join('\n');
    try {
      await navigator.clipboard.writeText(text);
      toast('已复制版本信息');
    } catch {
      toast('复制失败：浏览器没有允许写剪贴板');
    }
  };

  return (
    <Dialog
      open={props.open}
      onClose={props.onClose}
      title="关于 MakoKara Generator"
      className="about"
      actions={
        <Button variant="tonal" onPress={props.onClose}>
          关闭
        </Button>
      }
    >
      <img className="wordmark" src={logoWide} alt="MakoKara Generator マコカラ" />
      <p className="ver">版本 {product}</p>
      <p className="muted">把歌词、音频和样式做成卡拉 OK 字幕视频。</p>
      <dl className="about-facts">
        {facts.map(([k, v]) => (
          <div key={k}>
            <dt>{k}</dt>
            <dd>{v}</dd>
          </div>
        ))}
      </dl>
      {licenses && (
        <dl className="about-facts licenses" aria-label="第三方许可">
          {THIRD_PARTY.map(([name, license]) => (
            <div key={name}>
              <dt>{name}</dt>
              <dd>{license}</dd>
            </div>
          ))}
        </dl>
      )}
      <div className="about-links">
        <Button variant="ghost" size="sm" icon="license" aria-expanded={licenses} onPress={() => setLicenses((v) => !v)}>
          第三方许可
        </Button>
        <Button variant="ghost" size="sm" icon="content_copy" onPress={() => void copy()}>
          复制版本信息
        </Button>
      </div>
    </Dialog>
  );
}
