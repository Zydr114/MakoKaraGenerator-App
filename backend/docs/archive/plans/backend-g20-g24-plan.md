# G20-G24 基础编排切片实施计划

**2026-10-18。** 用户授权接续后端能力缺口审计并实施G20-G24。本文约束基础切片及验收边界，不承诺完整编排产品。原审计与实施后矩阵见 [能力缺口审计](../../backend-capability-gap-audit.md)；HTTP行为源为 [统一API](../../architecture/backend-api.md)，已提交合同入口为 [web/README.md](../../../web/README.md)。

## 1. 基线与交付方式

基线审计提交 `9dbc735`（历史产品`0.91.0`），本批功能交付产品版本升为`0.92.0`，最终版本源码baseline为`cc6d632`（已推送）。G20（`a7e6275`／`bdbffd6`，合同1.20.0）和G21-A（`641be21`／`c9e629b`／`1c7f61f`，合同1.21.0）已提交。G21-B（`34fe3f5`）／G22-A（`1a38f2d`）／G23基础（`13da56b`）／G24基础（`987311b`）源码均已提交；功能源码`34fe3f5`已推送。本轮最终冻结合同 **1.22.0：68 paths／75 methods／367 schemas／1390 examples（989反例），62种工程命令**。最终`cc6d632`的Debug正常solution构建0警告／0错误；最终模块回归分别通过，见第5节。每个实现批次按代码、定向验收、合同与实施证据逐步记录；不把本文计划当作已验收结果。

仓库只维护无窗口后端；保持原源仓库 `/mnt/Data/Program/NewMakoKaraGenerator`，不重新加入Avalonia/SukiUI。前端由独立仓库接入，浏览器及用户验收分别登记。

## 2. 基础切片范围

| 切片 | 有限交付 | 状态及验收入口 | 本批之外 |
|---|---|---|---|
| G20 | 六命令：`orchestration.setNode`、`reparent`、`reorderChildren`、`addSlot`、`removeNode`、`reorderSlots`；稳定身份回执，重挂保持世界外观，引用保护。 | 已提交；实施§53。Hosting925/925、Server263/263、HTTP3/3分列；构建0警告／错误。 | 任意节点创建／复制、通用节点JSON编辑。 |
| G21-A | `orchestration.setLayoutDefaultPlacement`；现有内部布局默认位置，显式null清除。 | 已提交；实施§54。Hosting2/2、HTTP1/1；构建0警告／错误。 | 单独不覆盖布局参数、外层几何或动画。 |
| G21-B | `orchestration.setNodeGeometry`、`setLayoutSettings`、`setSlotAnimation`、`setProgramAnimationPolicy`；读取canonical属性。 | 已提交`34fe3f5`；合同1.22.0；最终模块回归通过，见第5节。 | 任意动画轨道、任意属性／内容、完整动态编辑器。 |
| G22-A | 单节点固定容器参考几何GET；精确修订，节点／父参考，画布坐标矩阵与四角，输入预算超限拒绝。 | 源码已提交；合同1.22.0；最终模块回归通过，见第5节。 | Runtime文本／自动布局测量、动画效果变换、最终像素几何。 |
| G23 基础 | `orchestration.createCountdown`、`createLeadIn`、`updateLeadIn`、`deleteLeadIn`；手工前奏和倒计时容器，有限内容投影。 | 源码已提交；合同1.22.0；最终模块回归通过，见第5节。 | 自动生成策略、mark编辑、附着切换；删除前奏不自动删除提示容器。 |
| G24 基础 | 单句局部transform GET与`orchestration.setLineLocalTransform`；偏移为父参考比例、旋转为度、正倍数缩放；null恢复。 | 源码已提交；合同1.22.0；最终模块回归通过，见第5节。 | 不改源时间；不开放片段时间微调或最终帧几何。 |

## 3. 共用验收门禁

1. **代码与领域**：复用Core纯候选／校验语义，拒绝非法身份、非有限数、超限输入和坏目标；失败不部分写入。检查原持久化与渲染读取链路。
2. **Hosting**：同一Writer内精确revision，一次真实变化对应一次历史；同值／重复清除no-op不消耗修订、不破坏redo。验证undo／redo、拒绝后状态、稳定身份和保存重开。
3. **公共HTTP与合同**：严格JSON/query、完整required/null语义、401／403／404／409／413等既有错误策略、读写权限、真实回执重放与并发；同步DTO、capability、OpenAPI、manifest和例子并冻结校验。Hosting测试不替代真实HTTP。
4. **Worker与像素**：只有实际运行匹配Worker并核验输出的场景才登记Worker／像素结论。静态快照或codec测试不能代替最终Runtime文本／动画／像素证明。
5. **浏览器与用户**：独立前端完成接入及真实浏览器操作后另记提交和合同来源；用户验收单列。后端命令测试通过不代表浏览器或完整产品交付。

最终实施记录须按层列出命令、范围、版本、提交和证据；失败／过程计数不与最终成功范围混合，也不把不同测试范围相加作为产品完成度。

## 4. 剩余能力与接续

**后续G20-B入口：** [节点创建与子树复制计划](backend-g20b-node-creation-plan.md) 接续产品0.93.0，有限类型创建和同父子树复制的当前证据见该计划及[审计新增§8](../../backend-capability-gap-audit.md#8-g20-b-节点创建与复制实施更新)。本节原基础批次范围及第5节历史验收不因此扩大。

- **结构／编排**：任意节点创建与复制、其余节点内容、片段成员扩缩／拆合；G25 `wouldChange` 已登记待排期。
- **布局／动画／几何**：复杂内部布局和任意动画编辑、Runtime文本测量／最终像素几何及代表工程对照；基础属性可写不代表完整G21/G22。
- **前奏／局部编辑**：前奏自动生成、mark和附着编辑；G24仅局部布局变换，源歌词时间继续由既有时间编辑能力承担。
- **完整后端**：B3公共package、配方、脚本、图片视频与字体授权闭包及连续预览；B4对应完整正式任务；B5无GUI作者闭环；B6协议／分发／升级回滚／平台和完整部署产品收口。
- **前端证据**：Claude报告G19／G19-B浏览器通过（`f4d5e23`／`ffc007b`，合同来源`f16206a`／`9dbc735`），用户验收未做；不覆盖本计划新命令或完整W4。

完整B3/B4、性能、长片、GUI与平台矩阵均保持独立边界，不能从本批基础切片推定完成。

## 5. 最终验收证据

最终源码baseline **`cc6d632`**（产品0.92.0，功能主体`34fe3f5`）已推送；Debug正常solution构建 **0警告／0错误**。冻结合同 **1.22.0／68 paths／75 methods／367 schemas／1390 examples（989反例）／62种工程命令**，冻结及独立validate通过。

| 模块 | 最终结果 | TRX |
|---|---|---|
| Hosting | 955/955，exit0 | `artifacts/test-results/g20-g24-final-0920/hosting.trx` |
| Server | 295/295，exit0 | `artifacts/test-results/g20-g24-final-0920/server.trx` |
| Backend.Rendering | 214/214，exit0 | `artifacts/test-results/g20-g24-final-0920/rendering.trx` |
| Worker | 78/78，exit0 | `artifacts/test-results/g20-g24-final-0920/worker.trx` |

各模块独立列出，不与表内历史／切片定向计数重复累计，不折算为完整产品完成度。Server模块含其HTTP回归，不等于本批全部功能的真实Worker像素或浏览器验收。

原0.91批次Hosting **953/954** 的失败保留于`artifacts/test-results/g20-g24-final/hosting.trx`；`d225c4b`／`96f6b2a`仅修测试的合法异步观察窗口，不宣称产品BUG，也不将旧失败删除或混入最终成功计数。上述通过仅适用于本文基础范围；第4节剩余能力及浏览器／用户验收边界继续保留。
