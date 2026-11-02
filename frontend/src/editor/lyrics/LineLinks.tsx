import { Button as AriaButton } from 'react-aria-components';
import type { AppearanceState, AppearanceStore } from '../appearance/appearance-store';
import { lineTarget } from '../model/appearance-targets';
import { lineAppearance, linePlacement } from '../model/line-placement';
import type { EditorLine } from '../model/lyrics';
import type { OrchestrationModel } from '../model/orchestration';
import type { ApplicationIndex } from '../model/preset-applications';
import type { OrchestrationState, OrchestrationStore } from '../orchestration/orchestration-store';
import type { EditorState } from '../session/editor-session';
import { useObservableSelector } from '../ui/hooks';
import { Icon } from '../ui/Icon';

/** 没有对应状态时的占位：订阅了也不会通知。 */
const NONE = { subscribe: () => () => undefined, get: () => undefined };

/**
 * 行编辑区的“外观”和“显示在”（界面改版 §2.7）：只读，点一下跳到外观页或编排页并选中对应的对象。
 * 数据都是内存里已有的（分配列表、编排结构）；还没读到或后端没开放时整项不显示。
 */
export function LineLinks(props: {
  appearance: AppearanceStore | null;
  orchestration: OrchestrationStore | null;
  state: EditorState;
  line: EditorLine;
  onOpen: (page: 'appearance' | 'orchestration') => void;
}) {
  const { appearance, orchestration, line } = props;
  const model = useObservableSelector<OrchestrationState | undefined, OrchestrationModel | undefined>(orchestration?.state$ ?? NONE, (s) => s?.model);
  const index = useObservableSelector<AppearanceState | undefined, ApplicationIndex | undefined>(appearance?.state$ ?? NONE, (s) => s?.applications?.index);
  const lines = props.state.linesByBranch[line.branchId] ?? props.state.lines;
  const branchName = props.state.branches?.find((b) => b.id === line.branchId)?.name ?? '主歌词';
  const look = index ? lineAppearance(index, model, line, lines, branchName) : undefined;
  const placement = model ? linePlacement(model, line, lines) : undefined;
  if (!look && !placement) return null;

  return (
    <div className="pair">
      {look && appearance && (
        <section className="fs">
          <header>
            <h4>外观</h4>
          </header>
          <AriaButton
            className="ro"
            aria-label={`外观：${look.name ?? '默认样式'}，${look.source}。到外观页`}
            onPress={() => {
              appearance.setTargets([lineTarget(line)]);
              props.onOpen('appearance');
            }}
          >
            <Icon name="palette" className="ro-lead" />
            <span className="ro-name">{look.name ?? '默认样式'}</span>
            <span className="faint">{look.source}</span>
            <Icon name="chevron_right" className="ro-go" />
          </AriaButton>
        </section>
      )}
      {placement && orchestration && (
        <section className="fs">
          <header>
            <h4>显示在</h4>
          </header>
          <AriaButton
            className="ro"
            aria-label={`显示在：${placement.title}，${placement.detail}。到编排页`}
            onPress={() => {
              orchestration.setBranch(line.branchId);
              if (placement.clipId) orchestration.selectClip(placement.clipId);
              else orchestration.select(placement.containerId);
              props.onOpen('orchestration');
            }}
          >
            <Icon name="dashboard" className="ro-lead" />
            <span className="ro-name">{placement.title}</span>
            <span className="faint">{placement.detail}</span>
            <Icon name="chevron_right" className="ro-go" />
          </AriaButton>
        </section>
      )}
    </div>
  );
}
