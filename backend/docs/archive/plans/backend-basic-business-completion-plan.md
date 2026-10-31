# 后端基础业务补齐计划

**日期：2026-10-18。规划基线：`54cf920`；原产品0.94.0／合同1.24.0；纠错基线0.94.1／1.24.1。** 用户已授权执行最近审计建议1/2。功能实现目标0.95.0／合同1.25.0；下文B～G已按实际DTO、路由和预算回填。验收结果以§12与实施记录为准，不能以实现或计划声明通过。

## 1. 目标与边界

本轮完成两件事：先消除已确认的合同/错误字段偏差与陈旧入口，再补齐基础工程业务的公共HTTP闭环。原G20～G24后端关闭结论保持，G19成员扩缩与显式拆合按本轮新增范围实施。

纳入范围：

1. `ExportJobRequest.format`透明格式枚举及R8附着失败的`failureCode`/`field`一致性；整理现行入口中仍冒充当前状态的旧说明。
2. G12授权目录浏览。
3. 普通/增强LRC的损失评估、显式执行和认证下载。
4. 工程音频替换/清除。
5. 完整模板`.mktpl`保真导入/导出（惰性传输，不执行高级对象）。
6. 包含静态preset、实际Node父链及clip分配的最终静态样式与逐属性来源解释。
7. 既有clip成员扩缩、显式拆分与合并。
8. 工程改名及工程资源清单。

高级包执行、包/配方作者、媒体背景与字体授权闭包、整曲正式任务、完整B3/B4、B5/B6、G25 `wouldChange`、关键帧/任意轨道、浏览器与用户验收不由本批扩大。`.mktpl`保留当前StyleTemplateTransfer支持的全部合法对象；传输不执行高级对象，不借传输功能开启包执行。

## 2. 已有内核与复用点

| 业务 | 已有基础 | 本轮需要补的公共出口 |
|---|---|---|
| 目录 | `ProjectDirectoryHandles`、`ProjectWriteLease`已有root grant、client归属、读写句柄、链接拒绝、保存/打开授权。 | 可发现已授权根及其有界子项；当前调用者预先知道相对路径不等于目录浏览。 |
| LRC | Core `ILyricsExporter.Plan/Export`、`PlainLrcExporter`、`EnhancedLrcExporter`、`LyricsExportOptions/Plan/Report`已有。 | 精确修订损失评估、评估与执行绑定、固定输出、下载与回执。 |
| 音频 | `ProjectMediaEditor.SetAudioPath`、固定上传、受管理Worker准备、首音轨策略、音频/分析/波形租约已有。 | 对已打开工程替换/清除、准备与提交分离、undo/redo及持久化一致性。 |
| 模板 | 个人库CAS与静态定义/试样已有；Application `StyleTemplateTransfer`已有完整ZIP读写、路径/链接/重复/未声明文件及预算检查。 | `.mktpl`上传完整解析、全归档新ID、一次库CAS及固定归档下载。 |
| 最终样式 | `PresetStyleResolver`已解析静态应用与逐属性持久来源；`StaticStyleProjection`及真实Runtime已有消费路径。 | 在精确修订与真实展示归属下返回有限最终style/来源，不能以现有四层`staticHierarchy`代替。 |
| clip | `SceneTreeEditor.TrySetClip/TryDeleteClip`、G19/B命令、`BasicLayoutOperation`、`ClipSplitReferences`已有范围校验/部分拆片引用迁移。 | 显式成员更新及拆合事务、身份/引用/模板冲突规则与读写闭环。 |
| 改名/资源 | `Project.Name`、资源存储/读租约、工程保存闭包及音频/预设快照声明已有。 | 改名命令、受授权且有界的工程资源投影及重开核验。 |

场景编辑会话/场景草稿基础、静态模板定义草稿及固定试样已存在，不记为“完全没有草稿或模板”。它们不等同于完整B5源码修订→编译→采用→发布作者闭环。本轮沿用现有静态能力，不另造第二份工程权威。

## 3. 共同合同与执行规则

### 3.1 有限DTO和修订

- 只公开操作所需字段、严格union和稳定身份，不返回任意`Project` JSON或允许客户端提交文件系统绝对路径。
- 工程读取/评估携带`expectedRevision`；Writer内捕获不可变快照。读取期间后端可以释放Writer，但响应标明捕获修订；修改提交前重新核对修订和工程身份。
- 所有工程写入共用既有commands/Writer/history与requestId/hostInstanceId/validUntilUtc回执。一次用户确认至多一次工程历史；重复请求返回同一结果和Server生成身份。
- no-op返回原工程，不推进修订、不新增历史、不打断redo、不误报dirty；改变后undo/redo必须恢复内容和有效资源租约。
- 库写入沿用库revision/CAS与单独回执；纯导出、评估、浏览、下载不制造工程历史。

### 3.2 原子性、预算与资源

- Prepare→完整候选校验→完整读投影/结果回执预算预检→一次提交。解析、decode、归档生成和Worker工作在Writer外完成，不得先提交再发现输出超限。
- 沿用已有预算来源；新集合给出独立条数、累计文本/身份字节、输出字节和扫描上限。候选值见各批次，最终常量必须进入合同、capability和边界测试。超限明确拒绝，不静默截断。
- 大读模型使用有界分页或明确完整结果上限；续页绑定固定目录/修订与筛选条件，不能无界递归。排序和缺失项规则稳定。
- 沿用完整回执64KiB保护及changeSet超过16KiB的完整刷新规则；二进制内容只返回有界metadata/认证content引用，不塞入工程命令回执。
- 上传、准备、工程当前态、历史、固定导出和流式下载分别持有正确租约。失败/取消/关闭/到期释放候选，不能删除旧工程或仍被历史、预览、任务引用的资源。
- 不把凭证放URL，不公开Host绝对路径。权限、跨client、root/句柄归属、资源存在性错误沿用既有安全边界。

## 4. 批次A：合同和现行入口纠错

### A1 透明格式

`tools/backend-api-contract/freeze.py`对`ExportJobRequest.format`仍生成`transparentMov`；实际`ProjectSessionRegistry.Export`和`WorkerExportContract`接受`transparentProRes4444`。修复生成源、正反例与冻结物，保留实际wire，拒绝把错误名称再加为产品别名。核对capability/runtime-info、任务参数、实际透明导出及下载所用名称，合同例必须与真实HTTP JSON匹配。

### A2 R8附着错误

逐个核对附着目标无效、引用失效、真实参考缺失/不可逆、Worker测量失败、测量后revision变化等路径。当前`CommitAttachmentAsync`至少存在真实参考为空时缺稳定字段、核心拒绝时只有`failureCode`的分支。统一稳定`failureCode`与精确`field`（例如目标字段与参考失败不得混指`expectedRevision`）；保持422/409/503及既有真实Worker错误的语义，不把所有异常合并为同一种拒绝。R8是审计项标识，不新增wire值。先锁失败矩阵，再修最小实现与合同反例。

### A3 入口整理

检查`AGENTS.md`、`web/README.md`、统一API导言/能力限制与各历史实施导航。现行入口中“无几何命中”“完整最终静态样式未公开”等表述按真实已交付/本批新交付范围更新，历史版本数字与失败证据保留原归属。启动示例目录使用当前版本或明确占位符；不把旧示例当匹配0.94部署。此计划阶段不修改上述文件，实施时按实证更新。

验收：生成metadata/check/validate通过；真实HTTP普通/透明格式正例和错误旧枚举反例；R8每个拒绝断言状态、code、failureCode、field、修订/历史不变。需要测量的分支以真实Worker补证，模拟旧能力明确标识模拟。

## 5. 批次B：授权目录、改名和资源清单（实际合同）

- 既有 `GET /api/v1/file-roots` 发现启动器授权根；新增 `GET /api/v1/file-roots/{rootGrantId}/entries`，relativePath空值表示根，非空≤2048；offset/limit分页，limit1..100。Ordinal排序、只列直接子目录、isProject只检查普通工程入口。扫描完整单层≤4096（含文件），链接/越界/不可访问/超限整体拒绝。不保留目录快照，目录变化后从第一页刷新；不能拼成快照一致性承诺。
- `project.rename`：name非空白，输入≤256 UTF-16 units，Trim后提交；同名no-op。只改显示名，沿原summary/settings、历史和保存重开。
- `GET /api/v1/project-sessions/{id}/resources`：expectedRevision/offset/limit（≤100），完整闭包先受ReadModelBudget的20000项/1MiB字符及1MiB响应约束，再分页。音频/背景声明/已采用分析/效果包/模板快照按逻辑身份去重并合并roles；只公布已知事实、明确状态、适用的认证内容引用，不暴露路径或无关上传，不执行包。

详细DTO与状态见[统一API§17.1～2](../../architecture/backend-api.md#basic-business-0950)。验收需授权浏览→句柄打开/保存、非法路径/链接/跨client、改名undo/redo/no-op保存重开及资源闭包核对。

## 6. 批次C：普通/增强LRC（实际合同）

`POST .../{id}/lyrics-export-plans` 接收expectedRevision/branchId/format/options/metadata；format为lrc.plain、lrc.enhanced、ttml.project或ttml.amll，options包含title/artist/album/newLine（LF或CRLF）/writeByteOrderMark/millisecondPrecision。单来源快照绑定owner/会话/revision，返回Core完整评估、额外损失、omittedBranchCount、requiresLossAcceptance与300秒计划身份，不合成FollowPrimary时间。0.95基础业务的 LRC/TTML 计划仍按单来源 branch scope；当前完整 `ttml.project` document scope 由合同1.27与[实施§59](../history/backend-extraction-progress.md#ttml-ruby-timeline-0960)提供，`ttml.amll`为选定来源交换。Core `ttml.project` 与导入器的多分支、branch role/timingMode、PrimaryLineId、权威行正文、raw 时间/status、ruby 参数和领域 ID 往返已在TTML批次完成并以独立合同/HTTP证据登记；完整项目样式未承载，仍由损失报告和显式确认覆盖。

`POST .../{id}/lyrics-exports` 接收expectedRevision/planId/acceptLosses/metadata，按同一固定快照执行；损失或issue须显式确认，旧修订/过期/跨client拒绝。成功返回固定bytes的身份、SHA、长度、Core实际report及认证 `GET|HEAD /api/v1/lyrics-exports/{id}/content`。不新增公共job，不要求Worker，不改工程history。

实际预算：请求16KiB、来源10000行/100000元素及注音检查/4MiB累计文本、输出4MiB、完整报告64KiB并受receipt预算；计划与产物合计client8/全局32，准备并发全局2/client1，300秒并计在途预留，不截断。验收需Core字节oracle、选项/多来源损失确认、过期/修订/跨client、下载SHA与工程状态不变。

## 7. 批次D：工程音频（实际合同）

`audio.replaceAsset` 接收当前client授权assetId，`audio.clear` 无其他command字段。Writer捕获→Writer外资源固定和受管理Worker准备→Writer复核身份/revision→完整候选/回执预检→一次history。沿原首音轨和波形预算，失败不动原工程。同一当前asset与重复清空no-op。

替换更新媒体身份/准备/波形并清除已采用分析；clear清除所有上述绑定。历史保留源与准备结果租约，undo/redo同步运行时音频和波形字段，历史不再需要时释放；保存重开沿原资源闭包。不改歌词/输出时长，null仍为未确定。验收需真实Worker不同音频、双轨/坏源、历史/波形/分析/资源清单/保存重开、并发拒绝与排水。

## 8. 批次E：完整.mktpl传输（实际合同）

原“静态-only”限制已调整：复用StyleTemplateTransfer保真传输其支持的全部合法高级对象，不执行/编译/安装，definitionAvailability仅说明当前公共详情能力。不能剥离包/配方或把归档传输当B5完成。

`POST /api/v1/template-libraries/{id}/imports/preflight` 接收assetId；`POST .../{id}/imports` 接收expectedRevision/preflightId，一次CAS导入完整归档，所有模板生成新ID（返回完整映射），不按名字覆盖，builtin只读。`POST .../{id}/exports` 接收expectedRevision/templateIds，固定所选快照；内置模板只在归档捕获对象中将保留身份确定映射为 `exported:<原builtinID>`，响应templateIds仍原选择ID，个人模板ID原样；定义/包资源不变，不修改库或放松Core保留身份保护，导入仍全部新ID。认证 `GET|HEAD /api/v1/template-transfers/{id}/archive` 下载固定归档。

实际预算：请求16KiB、压缩16MiB、展开32MiB、单文件8MiB、归档项128、模板1..100、完整报告64KiB；client2/全局8/总保留128MiB、300秒，计在途预留并保护流式读取。沿原ZIP路径/链接/重复/未声明文件检查，整批失败不能污染库。验收需完整高级对象往返等价、库CAS/重放、恶意ZIP、固定SHA及静态试样。

## 9. 批次F：真实上下文静态样式（实际合同）

`GET .../{id}/resolved-style` 必须expectedRevision/branchId/lineId，可选tokenId/clipId/nodeId，单目标line/token。真实程序、成员和display schedule推导container/slot/父链，包含Runtime动态text叶；Node includeDescendants依真实祖先语义。FollowPrimary按真实容器规则，不假造独立Karaoke槽位。可选clip/node仅验证归属，不接受自定义路径。

返回context、canonical style与Core固定21属性来源sourcePath；resolution=staticPresetHierarchy、finalPixelAppearance=false，不运行动画/脚本/字体测量，不表示最终像素。不提供mark/批量新出口。节点≤2000/深64和原ReadModelBudget完整计费。验收需Core来源oracle、真实Node/clip/文字叶、FollowPrimary与Worker静态帧对照、history/持久化及错归属拒绝。

## 10. 批次G：clip完整成员事务（实际合同）

- `orchestration.setClipMembers`：branchId/clipId/lineIds，完整连续成员替换，保留ID/名称/容器/原scheduleContext；新成员不能越过已有上下文，重叠/非法范围拒绝；含相对效果配方的隐式范围改变拒绝。
- `orchestration.splitClip`：branchId/clipId/splitBeforeLineId/newClipId；client显式新ID，原ID留左，一分为二共享原scheduleContext。ClipSplitReferences迁移引用，并核所有原成员槽位/reveal/hide不变，不做Server身份或多分区拆分。
- `orchestration.mergeClips`：branchId/clipIds/targetClipId；同来源/容器/上下文、连续唯一并集，保留target名称/配置。直接效果clip绑定完整重映射；无应用或全部相同纯静态snapshot/enabled/branchInputs可合并，后者保留target应用；部分/冲突/高级固定身份引用原子拒绝。

只支持独立计时Karaoke来源。lineIds/clipIds≤1000、意图64KiB、节点2000/深64、完整读模型20000项/1MiB和完整回执64KiB。feedback为clipMembersResult，返回完整clips、removedClipIds、changed及application身份变化。全部候选/引用/预设读投影验证后一次history；no-op保redo。验收需Core/HTTP扩缩拆合、拒绝矩阵、重放/undo/redo/重开和代表静态Worker外观。

## 11. 推荐实施顺序与验收门禁

| 顺序 | 交付 | 完成门禁 |
|---|---|---|
| A | 合同纠错与现行入口收敛 | 真实wire/错误矩阵、生成校验；单独修复提交，便于追溯。 |
| B | 目录、改名、资源清单 | 授权浏览→打开/保存闭环、精确读写与重开。 |
| C | LRC评估/执行/下载 | 同plan固定快照、Core完整字节oracle、HTTP下载。 |
| D | 音频替换/清除 | 真实Worker准备、历史租约、分析/波形/保存重开一致。 |
| E | 完整`.mktpl`惰性传输 | 库CAS/固定归档、HTTP往返、静态试样。 |
| F | 最终静态样式解释 | Node/clip/preset真实归属、逐属性来源与Runtime一致。 |
| G | clip成员及拆合 | 引用事务、历史/持久化及真实样式帧。 |
| H | 集成及冻结 | 正常solution零警告/错误；相关模块回归、匹配发布、真实HTTP/Worker及冻结物独立验证。 |

实施代理可按文件所有权并行B/C/E及D/F/G独立准备，公共Contracts/Server注册/冻结生成保持单一整合者，提交按可验收业务步骤管理并推送远程。任何候选命名/预算调整须回填本计划，不能靠规划文本宣告完成。

每批须保留：修改与拒绝矩阵、相关定向结果、实际HTTP JSON/二进制核对、持久化/历史/资源租约证据；涉及运行消费时加真实Worker/PNG/媒体对照。最终以同一生产输入匹配发布Server/Worker，记录源码与部署SHA、所有成功/失败job终态、观察进程身份与正常排水。正式测试计数去重，过程失败单独保留；仅compile成功不记测试通过。

冻结时同步DTO/capability、统一API、OpenAPI/例子/manifest和入口，实际HTTP JSON必须全匹配；版本以`Directory.Build.props`单一来源，只有完成实际功能批次时按仓库约定提升。原规划阶段未跑build/test；实施阶段由集成验收记录实际结果，文档整合不自行宣告通过。

后端验收、浏览器验收与用户验收分层记录；本轮未授权恢复的性能/长片/GUI/平台矩阵继续独立。完整产品不能由新增命令数或本轮通过测试数推导。


## 12. 实施与验收状态

A项纠错已单独交付0.94.1／合同1.24.1，见[修复记录](../acceptance/backend-basic-business-bugfix-0941.md)；本轮不重写其历史数字。B～G已按实现回填，完整行为见[统一API§17](../../architecture/backend-api.md#basic-business-0950)。代码与定向门禁已通过：build-final4 exit0、零警告/错误；六组正式回归去重1832/1832、零失败/零跳过，合同precommit check exit0且指纹一致，机器清单见[实施§58](../history/backend-extraction-progress.md#basic-business-0950)。0.95/1.25 final2 的独立匹配发布、真实HTTP/Worker、内容oracle和进程排水已登记；本次验收生产源码基线 `38f2fa9` 的 0.96/1.27 B～G fresh replay另见 `artifacts/basic-business-current-38f2fa9/assessment.json`。TTML document scope/ruby segment API 不由本基础业务重放覆盖，见[TTML实施§59](../history/backend-extraction-progress.md#ttml-ruby-timeline-0960)。浏览器/用户及完整B3/B4/B5/B6、G25保持独立。
