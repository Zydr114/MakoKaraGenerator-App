# M11 完整作者工作流：源码核对与实施分工

日期：2026-11-04。本文落实[主计划 §5.8](script-sdk-v2-render-pipeline-plan.md#58-目标样式与作者工作流)、
[D8](script-sdk-v2-capability-ledger.md)和[验收表 M11](script-sdk-v2-render-acceptance.md#3-15-个验收节点)。
这是当前源码事实与待实施切片，不是 M11 运行通过记录；M07／M08 的有限证据仍归原节点，
M09／M10 的资源、几何、图和媒体语义必须先通过所属门禁。本文不修改生产代码、合同或版本，
不执行构建、测试、发布、浏览器或性能采样。

## 1. 已有入口与真实缺口

路径相对 App 根。现有前端保留紧凑 M3、React Aria 和 `src/editor/ui/` 控件体系。

| 范围 | 当前源码事实 | M11 必须完成 |
| --- | --- | --- |
| 作者包文件 | `backend/src/MakoKaraGenerator.Backend.Contracts/BackendAuthorPackageFilesContracts.cs`、Hosting 的 `ProjectSessionRegistry.AuthorPackageFiles.cs`、Server 的 `ServerEndpoints.AuthorPackageFiles.cs` 已有精确草稿修订下 GET／PUT `package-files`；manifest 与全部可编辑文本原子提交，服务派生文本 SHA | 在同一作者链接入 manifest、C#、SVG、shader、shaderProgram、graph，保持包闭包和 CAS；不能只加前端文本框 |
| 文本与资源限制 | `IsEditableAuthorRole` 只有 csharp／shader／shaderProgram／graph；非文本 path／role／SHA 必须完全保留，不能新增、删除或替换；文本最多 256 个，单个及 manifest 各 4 MiB，合计 16 MiB，wire 32 MiB | SVG role 依 M09 准入接入；新增／替换图片、SVG、字体等资源需要受控导入事务，现接口不能完成这些动作 |
| 前端客户端 | `frontend/src/client/mako-client.ts` 的 drafts 已封装创建、C# sources、配置、编译、采用、验证、删除；未封装 package-files、source-revisions、包下载、profile 草稿发现／claim | 补当前正式合同的类型和调用，未接线的服务端能力与必须新增的能力分别处理 |
| 编辑状态 | `frontend/src/editor/library/draft-source.ts` 管理 C# 缓冲、CodeMirror 状态、保存、固定修订编译、采用撤销；`author-draft-store.ts` 的 packages 缓存仍明确假设“仅 C# 可变、manifest 不变” | 改为按 `(draftId, sourceRevision)` 固定的包元数据，按文件 role 分派编辑器；保留未提交文本、光标和撤销历史 |
| 编辑器辅助 | `SourceEditor.tsx` 仅 C# 高亮、括号、历史、行列 reveal 和诊断，无补全、搜索或格式化 | 最小 v2 模板、可编译片段、基础补全、参数约束／能力／属性帮助和固定修订源码定位；通用 C# LSP 不作为前置 |
| 修订与恢复 | `protocol/author.ts` 有 base／latest／compiled／adopted／in-use；`DraftSourceModel.revisionRows` 只合并本页记住的修订和摘要；后端 `source-revisions` 已分页保留历史，profile 发现／claim 已有独立所有权 CAS | 接完整历史、恢复和明确的读取中／冲突状态；当前“草稿只在本页、没有恢复”文案应随真实接线更新 |
| 试样与诊断 | `template-workshop.ts` 固定 authorDraft 的 draftRevision、authorSourceRevision、sample、target；读取失败诊断只有第一页；`authorSampleFailure` 主要把运行诊断变成文本，`SampleFrame` 只保留像素 SHA／time，没有公开完整帧修订链 | 分页读取完整诊断并关联包／实例／阶段／目标／TimeUs／逻辑位置；显示真正显示帧身份，可跳到当时源码 |
| 失败旧帧 | 工坊 loading 可保留旧 Blob，但失败会替换为无 Blob 的 SampleFrame；主工程 `preview-controller.ts` 会保留 frameUrl／frameRevision | 作者和工程各自显式保存 last-success identity，失败时显示旧帧与新失败，两者不得混成新帧成功 |
| 验证与导出 | `ScriptSampleValidator` 为每输出模式 360 帧，共 720；`ProjectSessionRegistry.AuthorValidation.cs` 固定在用源码、样本、目标、设备和任务输入；后端已有作者／配置包下载 | UI 等待任务终态、读完整报告；补组件／模板／工程保存重开和包下载入口，真正从保存后的固定输入导出 |
| 成本和调试 | `PerformanceTelemetry.cs` 有进程有界聚合／帧阶段 wall time、分配和读回；`PreparedRenderFrame` 有最终几何、Playback、V2ParticleSnapshots、V2AnchorStatistics；现作者／预览 DTO 没有完整作者 inspection／成本视图 | 建有界、默认关闭、只读 inspection 投影；日志或测试中的内部对象不是现成公共 UI API |

当前 `backend/samples/ScriptEffectsV2/` 具有 WordEmphasis、RepeatEmphasis、ClusterExit、
CursorTrail、VectorTrail、EventBurst 六个目录。SpriteTrail、CustomInk、LineGlitch、GlassInk
尚未出现在该 v2 目录；ClusterExit 是附加退场样例，不占九个原样例名额。

## 2. 包编辑、资源导入与采用事务

1. 编辑器读固定 `(draftId, expectedDraftRevision, sourceRevision)` 的完整包清单和 role。
   manifest、文件树、参数 schema、能力提示、诊断与选中源码使用同一 sourceRevision；
   不能拿来源组件旧 metadata 给新 manifest 校验。历史浏览不改在用修订。
2. 保存提交 manifest 和恰好全部可编辑文本。后端检查重复键／路径、合法 Unicode、role、
   文件闭包、资源描述、预算和导出后才发布新修订；SHA 由实际字节派生。
   no-op 保持内容修订，409 保留本地缓冲并提示重读／比较，不自动覆盖。
3. 图片／字体等二进制先走受认证的现有 asset 上传，再由作者事务显式绑定 assetId、包内路径、
   role 和固定摘要；具体 DTO 由 root 冻结。新事务仍复用作者 storage lock、metadata receipt、
   expectedDraftRevision 和包存储，禁止前端写本机路径或直接改工程 Effects。
   检查完整目标闭包、M09 SVG 准入、图片尺寸和字体预算，成功才成为新 sourceRevision。
   失败／取消／过期／缺 asset 不发布半包，不删除旧修订或在用租约。
4. SVG 文本和导入都经过同一 M09 validator；编辑器不负责扩张允许的 SVG 子集。
   普通文本保存、资源添加／替换／删除／重命名是不同可审阅动作，不能借 manifest 编辑
   绕过二进制身份和引用校验。二进制资源未被引用也须符合包准入，不留下坏文件供以后执行。
5. SDK2 保存新源码、编译／资源验证成功、采用和在用是不同阶段。
   纯材质／直接图没有 C#，不能用 `canCompile=false` 禁止其文件编辑或伪造 DLL 编译；
   展示结构／shader／资源验证结果，并按正式门禁采用。

现有 `SaveAuthorPackageFilesAsync` 在 v2 保留 previous.SourceRevision，配置针对该旧包验证；
`AdoptAuthorSourceRevisionAsync` 仅切 SourceRevision 并保留旧 ExportId／参数。这能保护在用
内容，但**不能保证修改 export／参数 schema 后总能迁移采用**。例如删除旧导出、引入新导出时，
先改配置会被旧包拒绝，先采用又会被新包拒绝。实施前需冻结“已验证新修订＋完整目标配置”
的一次显式采用事务，按同一 draft CAS 原子切换；资源编辑保存本身仍不自动采用。
必须有旧导出删除／参数类型改变／仅资源修改／纯资源包的正反例，不能靠多次 PUT 暴露中间非法状态。

## 3. 修订链、参数状态与诊断定位

作者详情应能分别辨认以下值，不用一个“当前版本”混称：

| 身份 | 来源和含义 |
| --- | --- |
| 本地编辑值 | 尚未提交的文件／参数，可含本地错误；不是后端 sourceRevision |
| draftRevision／currentSourceRevision | 已提交草稿内容与最新保存包；保存源码不代表开始执行 |
| compiled sourceRevision＋artifactHash | 实际编译产物及其源；stale 仍可查看，不能套给另一源 |
| adoptedSourceRevision／configuration.sourceRevision | 最近采用标记与实际在用包；按 DTO 原样展示，二者可不同 |
| 请求输入／显示帧 | previewId／generation／requestSeq／TimeUs 和固定 source；authorDraft 的 sourceRevision 是 draftRevision，authorSourceRevision 才是执行包修订 |
| 媒体分析制品 | M10 定义的媒体源／profile／分析制品修订、状态及其依赖；不得用“最新分析完成”替代该帧实际使用身份 |

`draft-config-editor.ts` 已有 300 ms 合并、单在途、最新值和撤销快照，不应另造并行提交队列。
包编辑、采用、验证与配置提交共用草稿串行协调：动作前 settle 必要提交；收到旧回执不能
覆盖后来本地编辑。显示“本地／已提交／画面采用”的参数状态；undo／redo 形成可解释提交，
不把网络回执每一步都塞进用户撤销历史。若缩短 300 ms，单列请求数、CAS、撤销粒度和端侧延迟验收。

`PreviewPanel.tsx` 的工程静态播放控制间隔为 500 ms；`template-workshop.ts` 的试样取帧间隔
当前为 40 ms，`DRAFT_DEBOUNCE_MS` 为 300 ms。这是三个不同边界，不能因作者输入优化而
改掉 500 ms 控制节奏，也不能把试样刷新率写成该控制节奏已改变。

运行诊断读完整页并保留 occurrence epoch／sequence、包修订、artifact、instance、stage、
target、TimeUs、逻辑 source location。现 DTO 的 runtime occurrence 不含通用 target；
目标缺失要从实际执行归属链补齐公共投影，不能由当前 UI 选中目标猜测。无作者行号的宿主
预算错误应显示真实阶段与代码，不编造源码行；有位置时按诊断的包／源码修订读取并 reveal。
历史导航先保护 dirty buffer，可用只读历史视图，不为跳转覆盖未保存工作。
同样处理编译、运行、资源准入、图／材质、媒体诊断，不能只匹配 `ScriptExecutionFailed`。

最近成功帧独立保存其 Blob／显示身份。新输入失败时保留该画面，明确“显示旧修订／旧时刻”
和新失败原因；显示中的旧帧不能更新 rendered 参数、成功计数或当前 generation 的 ACK。
只有真正收到并完成该帧的展示事件才能推进显示身份；GPU 消费 ACK 继续遵循现 lease／完成边界。
HTTP 图片 fetch 完成与浏览器实际 decode／显示也分别留证据。

## 4. 默认关闭的只读 inspection 与成本视图

建议新增封闭 inspection DTO／内部投影，具体字段和预算先由 root 冻结，再让前端消费。
它钉住与帧相同的 input／generation／request／TimeUs／package／artifact／analysis identity；
不包含作者对象、回调、native handle、本机绝对路径或凭证。

- 几何从真实 shaping／PreparedRenderFrame 派生：可独立 cluster 与不可拆 cluster、
  Base／RubyId 归属、active／exit 的真实 binding／load、播放头和进度来源、实际出生锚点、
  Warp 后 mapper、效果 binding 范围。不能按字符串字符重新整形或凭 bbox 伪造 cluster。
- 只读叠加层由预览侧单独绘制，默认关闭；不改作者输出、不参与 hit-test／工程内容、缓存
  fingerprint 或正式 export。开关前后同输入正式像素／结构输出／导出摘要一致。
  额外图元数量／传输字节／保留帧数受固定预算，超限具名说明，不静默漏画一半当完整报告。
- 成本分开记录 compile、Declare／session preparation、实际帧 Prepare、final-anchor supplement、
  图 pass、粒子 births／alive／trail、分配、memo／资源缓存命中、读回与显示耗时。
  现 PerformanceTelemetry 的阶段是 inclusive host wall time，不能相加称总耗时，不能当 GPU
  硬件时间；全进程 allocation delta 不能称当前作者精确分配量。
- 成本采样和 debug inspection 分别标启用／关闭、采样率与 dropped／overflow。
  当前聚合日志不能直接充当跨 Worker 的固定帧成本 API；增补各实际计数及有界投影，
  UI 按同一输入和采样范围解释。正式性能仍以无诊断／无仪表对照为主，诊断自身成本另测。

## 5. 可分配文件与集成顺序

以下是建议所有权，不是已开始编码的分配。共享文件先指定一个 owner；其他切片新增 partial
或提交小接缝给 owner。版本、capability、Worker／HTTP 合同和组合根由 root 统一冻结发布。

| 切片 | 主要文件／独占新增候选 | 依赖与验收 |
| --- | --- | --- |
| A：作者文件与事务 | `BackendAuthorPackageFilesContracts.cs`、`ProjectSessionRegistry.AuthorPackageFiles.cs`、`ServerEndpoints.AuthorPackageFiles.cs`；新增 `ProjectSessionRegistry.AuthorResources.cs`／资源合同；`ProjectSessionRegistry.Author.cs` 采用小接缝 | M09资源role／validator；完整闭包、上传绑定、CAS／no-op、原子采用迁移、取消／配额／owner；沿用 `BackendAuthorPackageFilesTests.cs`、`BackendAuthorHistoryExportTests.cs` |
| B：客户端和多语言编辑 | `frontend/src/protocol/author.ts`、`client/mako-client.ts`、`draft-source.ts`、`author-draft-store.ts`、`DraftSourceTab.tsx`、`SourceEditor.tsx`；新增包文件／资源编辑 model、角色语言配置和 SDK2 片段目录 | A冻结合同；同修订metadata、dirty与编辑器状态、manifest／SVG／shader／graph／二进制列表、基础补全／帮助；`test/unit/author-client.test.ts`、`draft-source.test.ts`、`author-drafts.test.ts` |
| C：修订／导航／恢复 | `DraftDetail.tsx`、`DraftValidationTab.tsx`、`draft-validation.ts`、`DraftSourceTab.tsx`、`template-workshop.ts`、`template-thumbnails.ts`、`TemplateThumb.tsx`；新增 revision／diagnostic 展示组件 | B共享文件由单owner合入；完整历史与claim、诊断分页、历史源码跳转、stale画面、local/submitted/rendered；沿用作者与workshop／preview定向回归 |
| D：inspection与成本 | 新作者 inspection contracts／host store／Worker projection partial／前端只读overlay和cost组件；复用 `PreparedRenderFrame*.cs`、`PerformanceTelemetry.cs`、`GpuStreamDiagnostics.cs` 的实际数据来源 | M09 mapping／图pass与M10制品身份；root协调公共frame复制、Worker和GPU投影；默认关闭、有界、同代次、export隔离和诊断开销 |
| E：样例和真实验收 | `backend/samples/ScriptEffectsV2/` 九例和复杂组合；新增 M11 probe／browser acceptance 工具；`frontend/test/integration/author.test.ts` 及现有export／runtime回归 | M09／M10功能先闭合；九例逐项720帧、保存重开、下载／项目导出、真实UI及D8证据，不能用mock renderer替代 |
| root：共享集成 | ContractMetadata、`backend/tools/backend-api-contract/{freeze.py,validate.py}`、前端协议快照、版本、Runtime.Hosting组合根、发布和证据台账 | 源匹配合同与Server／Worker，之后fresh HTTP／浏览器／桌面分别验收；每步中文提交并推送授权App远程 |

顺序为：先固定 A／D 公开值和事务边界 → B／C 消费 → E 逐样例和跨样例验收。
资源／分析／图功能自身失败仍归 M09／M10 修复，不能在作者 UI 中透明替换绕过。

## 6. 九样例与复杂样式验收清单

| 原九例 | 必须从作者入口实际编辑并执行的内容 |
| --- | --- |
| WordEmphasis | 缺时间、真实ProgressSource、字族／Ruby变换、唱完回落、完整affine和任意t |
| RepeatEmphasis | 多token重复句、真实occurrence ordinal、branch／多次装载去重、重开／撤销 |
| CursorTrail | 固定／分段常数／线性速率、配额边界、最终锚点、寿命／随机身份和预算负例 |
| VectorTrail | Custom birth访问、解析运动／trail节点、断点与自动模式不重复输出 |
| SpriteTrail | 导入／替换实际图像、资源修订、完整affine、atlas邻接边缘与mip，不能只看manifest可读 |
| EventBurst | payload／scope／occurrence、token／line／cursor／mark四锚点、line fallback与同边界顺序 |
| CustomInk | 无DLL纯资源material、材质参数／覆盖、fill／stroke、sung／unsung和透明执行 |
| LineGlitch | direct ColorOnly图及typed controller变体；真实动态uniform／Transform node／采样框与缓存身份 |
| GlassInk | BackgroundMedia／BackdropBelow不同真实下层、blur／refraction范围、透明fallback与诊断 |

另做独立 nested group 图场景，不能因为后三例通过而省略子图→父图、Color／Erasure、
owned overlay排除、partial捕获、组alpha和预算。目标复杂样式必须同时具有 cluster退场、
Ruby、Warp后最终锚点、粒子、SVG星星扫句和媒体自适应描边，覆盖active／exit并存、
多实例合成、分析Pending→Available换代与透明fallback。ClusterExit继续作为附加样例保留。

每个样例记录一条完整链，不只导入未改的包后跑一帧：

1. 最小模板／包导入 → 实际文本或资源编辑 → 保存新sourceRevision；检查非空变更与no-op、
   stale CAS、其他owner、编译失败、资源闭包失败均不改变在用内容。
2. 编译或纯资源验证观察成功／失败／取消终态 → 显式采用固定修订 → UI显示实际新帧。
   记录包、源、artifact、草稿、preview、frame和analysis identity；保留旧帧的失败场景单验。
3. 正式作者验证 **opaque 360＋transparent 360＝720帧**，固定640×360／30fps／12s，
   读任务终态、完整报告和诊断。`202`、进度100%或截断报告均不能替代成功；source mismatch
   或改参后的旧报告明确stale，不把旧720帧归给新输入。每个原九例至少一条完整720帧链，
   总数6480帧是最低基数，不包含复杂样式／其他目标、材质槽位及负例。
4. 保存组件／模板并关闭编辑器重开，必要时新client通过显式claim恢复；再应用到工程，
   工程保存关闭重开。核对固定包、参数、种子、binding／订阅、资源闭包和分析制品，
   重开后实际预览同输入，不能只比较保存JSON。
5. 下载`.mkfx`后重新inspect／导入核对全闭包；从已保存重开工程进行真实GPU导出，
   同一固定制品、帧序／时长／alpha、诊断和完成结果可追溯。纯资源包无C#不要求伪编译。
   debug开关不进入成片。失败、取消、磁盘／预算负例无假成功，并验证资源／任务排水。
6. 同输入顺序、打乱、重复、memo冷热／淘汰、重开、undo→redo分别比较结构和真实像素。
   事件／load／cluster／出生死亡／分析就绪边界包含左右1µs；GPU为执行后端，含v2的CPU
   预览／验证／导出明确拒绝，无效果CPU仍能运行。

浏览器证据须含实际编辑、dirty保护、行列跳转、配置最新值合并／撤销、错误旧帧标识、
默认关闭overlay和成本读数；桌面共享纹理展示、消费ACK和静态HTTP PNG分开记录。
运行使用本仓库匹配构建、隔离profile和loopback；性能采样与build／test／其他GPU任务串行。

## 7. D8 与节点关闭

按C01–C10逐项登记E／A／X／D／T／P，不把样例表当能力覆盖率。特别核查独立Base／Cluster
材质、组图、完整affine Sprite、所有uniform类型、任意字段状态原用途、事件索引与音频／
确定随机；当前具名拒绝的能力只有在实现并验收后才能改为支持。

M11应交付：实际功能提交、对应源码与包摘要、合同身份、命令／环境、成功／失败／取消／
跳过数、720帧报告、保存重开和导出产物、真实显示证据，以及台账每项的缺口和归属。
M13负责整体发布候选证据汇总，不替代M11当下作者功能和HTTP／UI正确性验收。
未映射的D8能力、未通过的前序功能或正式性能门禁继续阻塞，不因本文或九例可编译而关闭。
