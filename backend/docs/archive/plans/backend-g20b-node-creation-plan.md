# G20-B 节点创建与子树复制实施计划

**2026-10-18，G20-B有限后端范围已交付并验收，合同1.23.0冻结校验通过。** 本文接续 [G20-G24基础切片](backend-g20-g24-plan.md) 与 [能力缺口实施后矩阵](../../backend-capability-gap-audit.md#8-g20-b-节点创建与复制实施更新)，记录本批有限范围、命令字段、复用原则和验收边界。实际验收见第7节；源码存在不表示浏览器或完整编排完成。

## 1. 基线与目标

实施前源码基线为 `7fe43b1`，产品 **0.92.0**，HTTP合同 **1.22.0**；既有G20六种结构命令、G21基础属性与动画、G22-A参考几何、G23手工前奏及G24局部变换保留原证据归属。本批产品版本为 **0.93.0**，冻结合同 **1.23.0**；最终冻结和验收数据见第7节。

本仓库只维护无窗口后端；原 `/mnt/Data/Program/NewMakoKaraGenerator` 保持保护。Claude负责独立Web前端，浏览器与用户验收分别登记。本批不把后端完成外推为完整编排、W4或完整B3/B4/B5/B6完成。

## 2. 业务范围

本批实施以下两项：

1. **创建有限类型的节点**：普通纯组、Auto歌词容器与SlotSequence歌词容器。类型及初始结构须明确，后端产生真实稳定身份；以已有父节点或明确画布归属为目标。槽位增加和倒计时创建继续复用现有专用能力，创建后的几何、内部布局和动画通过既有命令配置。
2. **复制已有节点子树**：复制根及全部后代，重建身份和子树内部引用，遵循Core既有同父复制语义；不复制源歌词、来源程序、默认归属、片段或前奏绑定。副本可通过现有结构、属性和分配命令继续编辑。

创建目标、类型组合、初始槽位结构与复制结果按第4节实施。本批不开放通用节点JSON写入，不包含任意节点内容、mark编辑、附着切换、自动前奏生成、片段成员扩缩拆合或任意动画轨道。

## 3. 复用语义与实现原则

- 创建复用 `SceneTreeEditor.TryAddNodes` 与统一 `PresentationEditor` 校验；禁止HTTP层绕过领域约束直接写树。
- 复制复用 `SceneTreeEditor.TryCopySubtree`：全部节点获得新ID；父引用、`Layout.SlotIds`、倒计时 `MarkNodeIds` 在子树内部重新映射；`name:null` 清除复制根名称，后代名称保留。
- Core复制单个槽位或mark时，会把副本加入原父的槽位或mark引用序列；复制子树内部owner的引用则映射到副本。本批明确保留并验证这些语义。
- 保持现有单Writer、精确修订、一次真实变化对应一次历史、失败无部分提交、回执重放及撤销重做语义。后端生成身份应由同一已接受结果及回执固定，重放不得再次生成副本。
- 完整候选结构、编排读投影和命令回执均须在提交前通过预算；不只限制客户端输入或副本根节点。
- 公开结果复用 `structureResult`，新增可选 `createdNodeIds`：根ID在首位，其余按新增节点顺序排列，不返回旧新ID映射。旧六种结构命令不返回该字段。

## 4. 已决定的命令、预算与风险

以下对象是现有命令信封中的命令体；每个列出的字段均须显式出现，`null` 不等于省略。

### 4.1 创建命令

`orchestration.createNode` 命令体：

```json
{"type":"orchestration.createNode","kind":"group","parentId":null,"name":null,"slotCount":null}
```

- `kind` 仅接受大小写精确的 `group`、`auto`、`slotSequence`。
- `group` 与 `auto` 的 `slotCount` 必须为 `null`；`slotSequence` 必须为1..64整数。
- `parentId:null` 表示画布；非null必须指向已有普通组。
- `name` 为字符串或显式null，最长256个UTF-16代码单元。
- Server生成唯一节点ID；使用默认几何与Canvas参考矩形。`group` 无布局，`auto` 使用默认 `LyricContainerLayout`。
- `slotSequence` 在同一次候选中生成根及全部槽位，配置 `SlotAnimation`，`maxVisible = min(2, slotCount)`。

### 4.2 复制命令与回执

`orchestration.copySubtree` 命令体：

```json
{"type":"orchestration.copySubtree","nodeId":"existing-node-id","name":null}
```

`nodeId` 指向已有节点；同父复制，不接受另一个父目标。`name` 为字符串或显式null，最长256个UTF-16代码单元，null清除复制根名称。复制不增加或复制Lyrics、Programs、Clips、LeadIns或Preset绑定；内部parent/slot/mark引用重映射，单槽位/mark复制按第3节更新外部owner。

两命令的 `structureResult.createdNodeIds` 固定为根优先、其余新增顺序；不提供旧新映射。回执重放返回相同已接受ID；旧六命令继续省略该字段。

### 4.3 预算与错误

- `MaxIntent` 为16KiB；源树与完整候选树各最多2000节点，深度最多64。
- 复制子树最多128节点；创建槽位最多64；名称最多256个UTF-16代码单元。
- 源与完整候选分别限制验证遍历输入20000项、累计身份/名称/标记文本1048576个UTF-16代码单元。遍历项包含节点、程序、引用、覆盖以及歌词branch/line/token，因此即使新增节点很少，长歌词也可能触发413。
- 完整编排输出另受1MiB JSON字节/20000输出项预算约束；验证输入预算与输出预算分开计量。完整回执受当前配置预算约束，全部检查先于提交。
- wire非法返回400 `invalidRequest`；语义失败返回422，主错误 `code=commandRejected`，`failureCode` 为 `TargetStale`、`ContainerInvalid` 或 `PresentationInvalid`；超限返回413 `resourceTooLarge`。精确修订、鉴权及其他共同错误沿现有合同。

### 4.4 实施风险

| 项目 | 决策及验证要求 |
|---|---|
| 创建判别类型 | 固定支持的节点种类及初始结构，拒绝未知种类、不适用字段和非法组合；不隐式制造业务绑定。 |
| 父归属及顺序 | 明确画布/父目标、同父复制、节点显示顺序与槽位轮换顺序的区别；坏目标不改变工程。 |
| 身份与引用 | 全部新身份唯一、内部引用完整；原程序、歌词、片段、前奏及默认容器绑定保持原有含义。 |
| 槽位与mark复制 | 覆盖父引用序列追加及容量约束；Core拒绝的候选不能部分写入。 |
| 放大与预算 | 复制输入虽小，可能放大节点、深度、累计ID及读投影；按第4.3节检查源树、完整候选与回执，超限明确失败。 |
| 历史与重放 | 一次复制或创建一次撤销；重放返回已接受身份；并发旧修订拒绝，失败不消耗历史或破坏redo。 |
| 静态渲染范围 | 新结构可持久化及进入既有静态输入，不据此宣称文本测量、动画像素、包脚本或完整工程等价。 |

DTO、capability、统一API及冻结生成物按合同1.23.0同步；当前字段和错误行为以统一API及冻结合同为准。最终校验状态见第7节。

## 5. 验收范围

1. **Core与Hosting**：覆盖各有限创建类型、普通/布局/槽位/倒计时子树复制、内部引用重建、源绑定保持、无效父/类型/身份、容量与预算拒绝。验证完整状态、一次历史、undo/redo、回执重放、精确修订并发和失败无部分提交。
2. **公共HTTP与合同**：严格JSON、未知/重复/缺失字段及null语义、kind精确大小写和不适用字段组合；读写能力、鉴权、错误映射及各预算边界。通过真实HTTP完成创建/复制→读取定位→配置或分配→撤销/重做→保存→关闭→重开，并验证原对象与副本独立；并发命令、旧修订拒绝及同一回执重放必须核对新增ID和一次提交。冻结校验与反例覆盖另列。
3. **构建与回归**：正常solution构建0警告/0错误；先执行切片定向，再按实际受影响范围执行模块回归，保留最终exit code及证据。历史计数、失败过程、切片和模块回归分别记录，不重复累计。
4. **Worker与像素**：若登记真实预览或像素结论，必须运行匹配Worker并核验实际输出；codec或静态快照回归仅证明各自范围。
5. **前端与用户**：前端接入提交、合同来源、浏览器操作和用户验收分别记录。后端测试不能代替这些证据。

交付时同步实施记录、当前状态入口及审计新增第8节；审计第1～7节保持历史快照，不回写旧结论或计数。

## 6. 后续优先顺序

1. **G20-B**：先形成创建、复用、配置和分配节点的基础链，复用当前G21/G22读写能力。
2. **G23-B**：前奏生成策略、mark与附着编辑。它们并非全部硬依赖G20-B，Core已有 `LeadInGeneration.Analyze/TryApply` 与 `LeadInEditor`；先补结构是为了稳定可复用目标、身份和预算。
3. **片段成员扩缩、拆分、合并**：独立冻结连续成员、时间和历史语义。
4. **复杂布局/动画与Runtime几何**：补代表工程参考及真实预览对照，不把固定容器参考几何当作最终像素。
5. **B3公共package及资源闭包，随后B4完整输入任务**：逐步接入包、脚本、媒体、字体、授权和实际故障恢复；再接续B5作者与B6分发收口。

G25 `wouldChange` 作为独立P3待排期，不作为G20-B或G23-B的前置。以上是建议顺序，不是后续批次已实现或已验收的声明。

## 7. 实施与验收证据

最终源码baseline **`d045eae`**（产品0.93.0）已提交并推送。正常solution构建 **0警告/0错误**，日志 `artifacts/g20b-build-acceptance.log`。冻结合同 **1.23.0／68 paths／75 methods／369 schemas／1522 examples（1092反例）／64命令**；统计见 `artifacts/backend-g20b-contract/validation.json`。Release metadata、freeze write/check与独立validate均exit0；manifest包含20个显式Core真值文件，其中包括 `LyricPresentationDefinition.Tree.cs` 的兼容默认程序物化实现。隔离缺失该文件的mock明确失败，真实路径check通过；日志见 `artifacts/backend-g20b-contract/freeze-check.log`、`validation.json` 与 `missing-materializer-guard.log`。

| 模块 | 最终结果 | TRX |
|---|---|---|
| Hosting | 979/979 | `artifacts/test-results/g20b-acceptance/hosting.trx` |
| Server | 321/321 | `artifacts/test-results/g20b-acceptance/server.trx` |
| Backend.Rendering | 214/214 | `artifacts/test-results/g20b-acceptance/rendering.trx` |
| Worker | 78/78 | `artifacts/test-results/g20b-acceptance/worker.trx` |

全部exit0、无失败或跳过。Hosting最终结果包含24项新例；Server最终结果包含26项本批真实HTTP，覆盖保存/关闭/重开、并发回执及cue原子拒绝断言。Hosting切片定向67/67单列保留，不与979/979重复累计；HTTP初轮26/26属于过程证据，不作为另一组最终数量相加。

本批尚无新的匹配发布、真实Worker像素、浏览器或用户验收结论。第2节的有限类型、同父复制和绑定边界保持；完整节点内容、完整编排与B3/B4/B5/B6仍独立。
