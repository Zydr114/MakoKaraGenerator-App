# 独立后端拆分审查与双端开发合同建议

日期：2026-10-16。基线：`refactor/individual-backend` / `e93e725`，产品 `0.75.1`，HTTP 协议 `"1"`。

本轮为代码、计划与前端消费方案审查，附定向复验；没有修改产品代码、协议、文件格式或版本。下文新增接口和批次都是**建议，尚未实施**，不表示恢复全部开发。当前接口仍以 [统一 API](architecture/backend-api.md) 为准，历史完成状态以 [roadmap](roadmap.md) 和 [实施记录](backend-extraction-progress.md) 为准。

前端材料：`/mnt/Data/Program/MakoKaraGenerator-Web/docs/web-client-w0-design.md`，并检查该项目 `docs/backend/` 副本与代码骨架。该设计定义的是 **W0 协议客户端／开发底座／控制台**；W1 只有方向和缺口，没有完整编辑器设计与冻结的功能清单。

## 1. 结论

1. **已经有最小可用的独立后端原型。** 原计划的首条链路“上传／授权 → 创建或打开 → 修改时间 → 撤销／重做 → 保存重开”已实现。本轮再次通过真实 HTTP 和独立 Server／Worker 复验，并走通 PNG 预览、正常退出与重启后打开持久工程。启动不依赖 Avalonia 或物理声卡。
2. **能够支持修订后的 W0。** 主要剩余工作在前端协议客户端、启动器适配和浏览器验收；无需等待正文、波形、场景或公共 package API。完整 W0 部署必须包含匹配 Worker，“无 Worker 只跳过预览”不符合当前音频准备要求。
3. **尚不能直接支持日常使用的 W1 打轴编辑器，也不是全功能独立后端。** 共享编辑用例较完整，但公共 HTTP 只有五种工程命令；完整编辑读模型和波形入口缺失。B3／B4 公共能力仍受静态输入及有限预算约束，B5 未完成，B6 部分提前落地。
4. **后续按前端可操作的业务闭环推进。** 协议产物、查询与 HTTP 编辑覆盖提前；W0 前端实施和 W1 后端合同／用例可以并行，公共 package 和完整资源输入随后继续。

测试数量、版本数量和行数不能换算为完成百分比；进度单位应是另一端能实际调用并验收的工作流。

## 2. 原计划与实际实现

依据：[原方案 §1、§6、§9](backend-extraction-plan.md)、[B3 接续计划](backend-b3-completion-plan.md)、[B4 计划](backend-b4-plan.md)。区分“共享 C# 用例”“公共 HTTP 能力”“浏览器完成”。

| 阶段 | 实际代码与交付 | 相对原计划的判断 |
|---|---|---|
| B0 基线 | Application 特征回归、Backend 依赖检查、分批输入与证据清单 | 必要有限基线存在；代表完整工程的保存／历史／渲染及性能基线未整体完成 |
| B1，0.50～0.56 | [Application/Editing](../src/MakoKaraGenerator.Application/Editing)：时间组、动作／打点、正文／注音、行操作、来源、导入、设置、拆合／重排／重切；App 已接共享用例 | 七个界定切片完成。原计划未以 HTTP 为 B1 前提，因此记录成立；多数用例未开放 HTTP，现在成为 W1 主要缺口。媒体设置／作者不在这次收口内 |
| B2，0.57～0.60及后续增强 | [准备服务](../src/MakoKaraGenerator.Backend.Hosting/NewProjectPreparationService.cs)、[工程会话](../src/MakoKaraGenerator.Backend.Hosting/ProjectSession.cs)、[持久化](../src/MakoKaraGenerator.Backend.Hosting/ProjectSessionRegistry.Persistence.cs)、Client／Resource／Receipt／Event／Job 与 Server 路由 | 原定义的首条独立链路成立；单写者、精确修订、回执、授权、保存／重开、恢复和 SSE 已实现。完整编辑查询与长期使用体验仍待补齐 |
| B3-A～D，0.61～0.64 | [协调器](../src/MakoKaraGenerator.Backend.Hosting/BackendPreviewCoordinator.cs)、[帧句柄](../src/MakoKaraGenerator.Backend.Hosting/PreviewFrameHandleStore.cs)、[静态 codec](../src/MakoKaraGenerator.Backend.Rendering/StaticProjectSnapshotCodec.cs)、[Worker 工厂](../src/MakoKaraGenerator.Backend.Hosting/WorkerRenderFrameSessionFactory.cs)、[HTTP 所有者](../src/MakoKaraGenerator.Backend.Server/PreviewBackendService.cs) | 已从接缝推进到真实静态工程 Worker／HTTP 预览；代次、最新 pending 槽、PNG 校验／租约／诊断已接通。浏览器连续播放／换输入／seek仍未整体验收 |
| B3-E～G，0.73～0.75 | [包 codec](../src/MakoKaraGenerator.Backend.Rendering/PackageProjectSnapshotCodec.cs)、[包 Worker](../src/MakoKaraGenerator.Backend.Hosting/WorkerPackageRenderFrameSessionFactory.cs)、[完整诊断](../src/MakoKaraGenerator.Backend.Rendering/PackageRenderDiagnosticsMapper.cs) | 固定包、真实 Runtime、失控回收及诊断是内部能力。Server 仍选静态工厂和 codec；公共 package、固定预设、背景媒体和可搬移字体闭包未完整交付 |
| B4-A～I，0.65～0.72 | [统一任务](../src/MakoKaraGenerator.Backend.Hosting/ProjectSessionRegistry.Jobs.cs)、scheduler／ledger／sidecar；Validation／Export／Analysis／AudioPreparation Worker 及路由 | 静态正式验证、短片导出／下载、独立分析／采用／清除、分析静态 profile、压缩音轨和受管理媒体准备的有限切片成立。完整渲染输入和完整成片出口未成立 |
| B5 场景／作者 | Core／Application 有场景和库基础；App 仍有作者草稿、编译／采用／发布流程 | 无完整无 GUI 作者用例和 HTTP 闭环。包能执行不等于前端能制作、编译、采用和发布 |
| B6 收口 | 分层项目和[匹配发布启动器](../tools/backend-web-host/README.md)已存在 | 部署基础提前交付；OpenAPI／TS 同步、浏览器客户端、旧入口收口和跨平台分发未完成。协议产物应提前到 W1 扩接口之前 |
| G1，0.75.1 | [ClientLifecycle](../src/MakoKaraGenerator.Backend.Hosting/ClientLifecycle.cs)、[回收保护](../src/MakoKaraGenerator.Backend.Hosting/ProjectSessionRegistry.ClientReclamation.cs) | HTTP新登记配额不足时回收过期且无保护依据的client；不是定时注销。工程、回执、保留期job、在途请求／SSE／读取继续保护 |

依赖方向符合主要目标：Contracts 无 ProjectReference，Application 只依赖 Core，Hosting 不依赖 ASP.NET／App，Worker 复用 Runtime／Export。Rendering codec、PNG适配、Processes各有实际职责；Host使用Skia编码PNG不等于Host执行完整脚本。故障隔离不等于不可信代码的OS安全沙箱。

当前公共工程命令准确为：`lyrics.setTimeRange`、`history.undo`、`history.redo`、`audio.analysis.apply`、`audio.analysis.clear`。依据是 `ProjectSession.ExecuteCommandAsync` 的 switch，不能按 Application 类数量推定 HTTP 覆盖。

公共预览仅双静态 profile，最大1280×720／裸帧4MiB；编辑后须PUT input，seek换代并重建会话。完整分析可随静态输入传输，不代表公开音频驱动脚本。导出仍限 **10秒／600帧**等合同；准备、分析、导出预算独立。能准备／编辑歌曲不代表能导出整首歌，也没有已验收的30／60fps浏览器预览承诺。

## 3. 发现与影响

### F1 · P2 · 已复现：无音轨工程可打开，创建静态预览却404

[打开流程](../src/MakoKaraGenerator.Backend.Hosting/ProjectSessionRegistry.Persistence.cs) 第130～158行允许无音轨，此时 `AudioAssetId` 为空；[CaptureAsync](../src/MakoKaraGenerator.Backend.Server/PreviewBackendService.cs) 第129～132行却无条件 `AcquireReference(client, audioAssetId)`，在固定静态输入前就失败。

本轮自有夹具保留当前产品／schema／歌词／场景，只将音频路径和媒体身份设空、分析状态设为规范 `noAudio`：真实open job成功，preview返回 **404 `resourceNotFound`，field=source**。静态绘制本身不要求播放音频，这是入口不一致。

建议独立修复：有音频身份时才持音频租约；增加无音轨真实路由／PNG回归，并保留有音轨路径的固定引用与排水检查。本轮只记录未修复，不阻塞W0既定“音频＋歌词”样例。

证据：`artifacts/backend-review-20261016/no-audio-preview-final2/result.json`，SHA `be572d85bfb1b378df7a4a1165854b1b89040c55948e7e6c4e8167c339fc5d48`。首次探针漏改 `noAudio` 状态，被正确拒绝；该失败保留在 `no-audio-preview/`，不算产品缺陷证据。

### F2 · P1（W0启动）· 前端启动设计已不匹配当前后端

W0 §4.1默认 `dotnet run`、Worker可缺省。本机实查仅有 `Microsoft.NETCore.App 10.0.12`，没有共享ASP.NET runtime；[AudioPreparationWorkerConfiguration](../src/MakoKaraGenerator.Backend.Server/AudioPreparationWorkerConfiguration.cs) 又在Worker缺失时禁用音频准备。HTTP新建必需音频，因此无Worker会阻塞核心流程，不能只跳过预览算W0通过。

应让 `dev-backend.mjs` 调已交付的 `host.py run`，解析 `WEB_BACKEND_READY`，由可信Node进程读取私有credentialFile。完整W0要求匹配Server＋完整Worker、`audioPreparation.enabled=true`和真实PNG。无Worker模式仅用于禁用展示测试。

设计还假定设置环境变量可提高到64MiB；稳定启动器清除继承的 `MAKO_BACKEND_*`，目前没有对应覆盖参数，实际仍16MiB。W0用生成的短WAV即可；W1大WAV若必须支持，另定显式配置和容量验收。

### F3 · P2（W0上传）· 中文文件名不能直接放进自定义头

W0 §3.8计划把原始文件名写入 `X-Mako-Asset-Name`。本机Node v26.11.1执行 `new Headers({"X-Mako-Asset-Name":"歌词.lrc"})` 实际抛出ByteString `TypeError`，请求尚未发到Server。

现有API支持原始字节＋`?fileName=...`；建议用 `URLSearchParams` 编码，或采用规范multipart文件字段。凭证仍只在头中，不自创头值编码并期待后端解码。前端应加中文／日文／空格文件名测试。本轮验证为Node Fetch Headers，完整浏览器上传待W0。

### F4 · P2（W0恢复与队列）· 需要补充客户端状态规则

- **重启不保证409。** Server先认证再检查host，旧access在新进程常先得401 `unauthorizedClient`。前端应刷新可信启动信息／health、比较host并停止旧队列，不能只等 `hostRestarted`。
- **G1描述过时。** W0 §3.4／§9仍称client永不移除；现在实际回收后access／resume均失效。未关闭工程受保护，刷新仍须优先resume，不能依赖G1自动discard。
- **保存重开必须先关闭。** save成功终态 → close成功终态 → open；202不是成功，未释放目录写租约会冲突。
- **迟到事件不能倒退revision。** W0 §3.6从SSE summary更新队列修订，§5只约定相等比较，未覆盖响应N+1之后旧事件N到达。建议SSE主要触发重读，同host／session下精确检查数值修订单调性并拒旧读取；opaque cursor／hash不做数值解释。冲突保留草稿，不无条件把旧意图换成最新expectedRevision重发。
- **internalError可能已经固定在回执。** `ClientRequestReceiptStore` 会保留可能产生副作用后的错误结果。同ID重试可读原结果，却不能期待它重新执行成功；应转权威读取／结果未知处理，不能自动换ID重做。

以上为待实现客户端的设计补项，不称为已运行前端的已复现故障。

### F5 · P1（W1）· 读模型、编辑命令、波形生命周期未闭合

`ReadLyricsAsync` 只有branchId／lineId／text／time和token id／text／time；缺来源发现、注音、时间状态、FollowPrimary编辑信息。固定 `primary` 夹具不能代替任意工程的主来源发现。

波形也不是补一条GET即可：创建第242～250行只将 `candidate.Waveform.Peaks.DurationUs` 放入ProjectSession，打开时同样只保duration。后端还需定义完整波形的缓存所有权、音频身份、容量、窗口读取与关闭释放，避免每次滚动重新解码或直接暴露Worker payload。

这些是未实施能力，不是已有五命令的回归。前端不应重写时间规则、解析工程磁盘格式或提交整份Project来绕过缺口。

### F6 · P2（W1连续预览）· 60秒回执窗口与帧请求频率有容量冲突

[帧请求](../src/MakoKaraGenerator.Backend.Server/PreviewBackendService.cs) 与编辑共用client回执，[默认1024条](../src/MakoKaraGenerator.Backend.Hosting/ProjectCommandRetentionOptions.cs)，有效条目不能提前驱逐。若沿用W0统一60秒窗口，假设实际接受30请求／秒，60秒需1800条，约34秒触顶；60请求／秒约17秒。此为**容量推导，不是实际渲染性能测量**。

W1应约定帧请求专用的较短重试窗口、发送背压和编辑／保存预留容量；例如普通写保持60秒，异步帧请求窗口另设，最多一条传输请求在途、一个最新待发时间。具体窗口／频率通过浏览器联调确定；必要时再讨论独立有界帧回执政策，不先盲目扩大总配额。

## 4. Claude方案的逐项支持判定

| W0要求 | 后端状态 | 前端接续 |
|---|---|---|
| 启动与bootstrap | 匹配发布、私有文件、随机端口、排水已有 | 接host.py；Vite bootstrap仅开发可用，限定准确Host／页面Origin、no-store、无通配CORS，不只检查loopback连接 |
| 登记／resume／心跳 | 已有，G1已交付 | 更新0.75.1基线、401先于host检查与实际回收规则；处理复制标签页可能共享身份造成的access轮换 |
| 上传／新建／读歌词／改时／历史 | W0固定样例可用 | 中文文件名、音频准备能力、实际返回ID／revision；允许明确使用生成的primary夹具 |
| SSE／重放／resync | 已有 | fetch流、单连接、恢复与client／job／preview补读；真实浏览器待验收 |
| 静态PNG | 含音轨样例可用 | 身份校验、鉴权fetch、解码、释放旧bitmap／Blob；无Worker不算完整出口 |
| 保存／关闭／重开／重启 | 已有 | 等成功终态；同client刷新先读state；重启重新授权句柄并打开持久工程 |
| TS类型／运行时校验 | 文字规范＋C# DTO有，OpenAPI没有 | W0可手写实际消费子集，不能把Contracts中的私有Worker协议一起变成公共SDK |
| W1打点／正文／注音／行操作／设置／波形 | 多数仅共享用例，HTTP缺失 | 后端成对交付查询和命令；前端可按冻结fixture做UI，mock不计真实联调 |

本轮实查前端 `dev-backend.mjs` 直接exit1，client／protocol／React页均为占位，该目录当前未初始化Git。因此实际为设计＋骨架，未完成W0。前端agent应在自己的项目建立版本记录；本轮没有修改前端。

## 5. 建议冻结的双端合同

### 5.1 唯一规范和版本化产物

后端维护公共合同、schema、脱敏fixture与配套包；前端消费**指定版本**并生成类型。禁止分别手改两份API文档，也不要求前端每次启动编译后端HEAD。

将B6相关内容提前为协议交付批次，建议产物如下；新增文件均待实施：

| 产物 | 约定 |
|---|---|
| 当前 `docs/architecture/backend-api.md` | 后端唯一行为规范；建议接口不混入已实现路由表 |
| 公共OpenAPI＋JSON Schema | 仅Server公共路由；覆盖command判别联合、错误、精确数值、null／省略、202／206／304／416等响应 |
| SSE／二进制附录与fixtures | 描述重放、缺口、背压、迟到帧、Range／ETag／TTL；这类行为不能只靠OpenAPI |
| `contract-manifest.json` | HTTP主版本、独立合同修订、产品版本、源码提交、schema SHA、所需能力、匹配部署身份；不必全部增加为HTTP字段 |
| 前端protocol生成类型＋版本锁 | 来自冻结schema；允许忽略兼容新增输出字段，未知错误有兜底，未支持命令不发送；关键边界仍做运行时校验 |
| 匹配Server＋Worker发布包 | 后端负责完整可复验；前端显式更新锁定包，profile与版本隔离 |

为现有端点先生成、核对schema，用实际序列化、路由覆盖及消费者测试防漂移。W0既定“不做CI”保持，先提供本地检查命令。

工程文件仍要求 `applicationVersion == ProductVersion.Current`：即使HTTP保v1，产品升级也可能使旧工程无法打开。双端测试应保存原始WAV／LRC和创建脚本，用当前服务重新生成夹具；旧profile留给匹配旧包，不手改工程版本，不悄悄放宽文件兼容。

### 5.2 保持现有wire，明确行为

| 方面 | 建议固定规则 |
|---|---|
| 权威状态 | 后端拥有工程／历史／dirty／保存身份／任务；前端拥有输入草稿／选择／播放时钟／显示缓存；正式作者草稿按B5独立设计 |
| 命令 | 保持现有顶层expectedRevision／requestId／hostInstanceId／validUntilUtc／command；新type明确目标与必填字段。可重构C#判别DTO，保留现有五命令JSON |
| 历史粒度 | 一次确认动作一次提交；拖动先本地显示、释放后提交，不跨HTTP悬挂BeginGroup。中间态服务端预览若必须要，另设合同。no-op零历史／零修订，undo／redo当前修订仍单调增加 |
| 读模型 | 按编辑任务提供稳定ID、修订、来源与编辑信息；与命令成对交付；分页／窗口绑定同一修订，不拼接不同版本 |
| changeSet | 返回失效范围和受影响ID；结构动作提供新增／删除ID和必要选择提示；未知影响保守full refresh，不以数组下标猜身份 |
| 错误／重试 | 按code／failureCode判断；同意图重试同ID／载荷／deadline；冲突保草稿，unknown不自动重做；取消请求和终态分开 |
| 数值 | 新微秒／64位序号继续字符串；现有composition／summary的number例外保持安全整数校验。完整analysis-result另有原canonical number语义，不能全局改写 |
| 事件 | GET权威，SSE提示失效；缺口补读后续接；revision／generation／requestSeq／eventSeq／opaque cursor分作用域 |
| 预览 | 前端PreviewController负责固定输入、请求、状态、字节和显示；换输入／seek／resize／close拒晚到结果；播放不每帧seek，帧回执容量按F6验收 |
| 音频／波形 | 浏览器播放，后端给授权字节和有界波形。W1先限定经浏览器能力检测的WAV／MP3；后端能解码不等于浏览器能播放。Blob设大小范围，转码／大文件流式另批 |
| 兼容性 | 产品版本、HTTP主版本、合同修订、能力profile、文件schema分别管理；兼容新增可留v1，删除／改类型／改含义需显式版本化 |

目前没有理由先重写为WebSocket／gRPC或开放全Project JSON Patch。HTTP／SSE／二进制足以验证W0和首批W1，传输优化须依据后续浏览器实测；本轮不恢复全量性能基准。

### 5.3 双端实施与交付流程

后端agent负责Application／Hosting／Server／Worker、合同、fixture、匹配发布与回归；前端agent负责协议消费、bootstrap适配、React、草稿／播放／显示及浏览器回归。各自在自己仓库提交，不交叉修改对方实现。

每切片状态为：**建议 → 双方接受的合同 → mock可开发 → 后端真实可用 → 前端真实联调通过**。合同必须包含路径／完整JSON、稳定ID、修订／历史、错误／重试、能力、样例、验收及非目标；记录两端提交与证据。

冻结后双方并行，变更先给合同差异和影响范围；后端验收后发布匹配包，前端显式更新锁定版本。核心必需能力不能以skipped算通过，可选禁用展示单独验收。生成类型只能防结构漂移，行为fixture／消费者测试还要覆盖重试、事件与生命周期。

## 6. 建议调整后的批次

W1完整范围尚未冻结。建议第一版为“歌词正文／逐字时间编辑、音频＋波形、静态预览、保存重开”；完整歌曲视频导出、场景工坊与脚本作者后续交付，保留其长期能力。

| 批次 | 后端 | 前端并行 | 最小验收 |
|---|---|---|---|
| S0 W0基线 | 固定部署与样例；F1独立小修；F2～F4消费修订清单 | 实现现有W0及启动器 | 浏览器登记／刷新resume／SSE恢复／中文上传／新建编辑／PNG／保存关闭重开／主机重启，不以Node代替浏览器 |
| S1 合同＋读模型 | OpenAPI子集、manifest／fixture；来源列表含primaryBranchId、完整可编辑歌词投影、设置读取 | 类型同步、来源／歌词／设置UI | 任意受支持来源可发现；未打轴与零时间可区分；注音／时间状态／FollowPrimary信息可读；窗口不混修订 |
| S2 时间＋波形 | 接既有打点、元素清除、微调、整行平移／批量时间；波形缓存和授权窗口 | 音频、波形、时间轴、打点／拖动、PreviewController | 一次动作一次undo；微秒／no-op／冲突／非法边界；波形同源；编辑后画面新修订、迟到帧不覆盖 |
| S3 正文／结构／设置 | 正文／注音、增删复制行、再次导入、标量设置；继而来源管理、拆合／重排／重切 | IME草稿、结果选择、导入报告、设置页 | 编辑→查询→undo／redo→保存重开一致，非法输入不丢、新ID正确；各子切片独立提交 |
| S4 完整渲染输入 | 公共package／诊断，再预设、媒体／字体；同步扩正式任务能力 | 按capabilities打开效果与诊断 | 固定源普通／透明结果、真实故障／失控恢复；preview支持不自动等于export支持 |
| S5 B5／B6剩余 | 作者草稿／源码修订／编译／采用／库发布、完整工程及独立分发 | 作者与高级编辑 | 原两条无GUI作者闭环及App共享成立；完整成片／平台按单独范围验收 |

S1候选路由可从 `GET /project-sessions/{id}/lyrics/branches`、带修订歌词窗口、`GET .../settings` 开始；波形使用“音频身份＋元数据／层级＋有界窗口”。**这些路由不存在**，正式命名、分页和编码在各批合同中冻结。

S2已有清除用例首先是元素时间清除，整行／多选清除需另定义；注音遵循现有模型，不顺带引入独立发音计时。S0前端可以立即开始，S1后端同步推进；无需等待全部B3／B4，也不让前端长期猜接口。

## 7. 本轮验证与边界

全部本轮证据位于 `artifacts/backend-review-20261016/`。新建自有profile／素材，无用户工程操作、无Avalonia启动、无前端改动；没有执行全量、性能、长片或人工平台矩阵。

| 执行 | 结果 |
|---|---|
| 正常solution，`-m:1 /p:UseSharedCompilation=false` | 原进程exit0，0 warnings／0 errors |
| Backend.Hosting.Tests | 711／711 |
| Backend.Rendering.Tests | 155／155 |
| Backend.Worker.Tests | 70／70 |
| Backend.Server.Tests，linux-x64 self-contained | 46／46 |
| 四TRX合计 | **982／982**，不与历史批次累加 |
| 稳定启动器真实HTTP冒烟 | **86请求／526断言**：资源／编辑／历史、PNG解码／身份／内容存在、保存关闭重开、重启恢复及排水；没有浏览器或独立Runtime像素oracle |
| 无音轨复现 | open成功后preview404；问题证据，不并入功能通过计数 |
| 中文文件名头 | Node v26.11.1 Fetch Headers抛ByteString TypeError；不是完整浏览器上传验收 |

HTTP使用既有 `artifacts/backend-g1-w0-0751/deployment-final2`：其manifest诚实记录当时 `cea473d`＋dirty输入，不冒充本轮HEAD构建。本轮比较708个生产输入SHA，全部与当前工作树一致；启动器验证完整部署SHA。四项目测试由本轮当前源码构建执行，原进程结束且TRX终态后才记数。输入／产物身份见本轮 `review-manifest.json`。

主HTTP摘要SHA：`cdd18685c259d1070e140555a6beef7efbd96007cb44d1e2f3c02f9808b332e0`。本轮自有启动器均正常退出，观察进程身份无残留；不外推其他平台。

本审查覆盖B0～B6／G1阶段合同、公共路由到用例／Worker的主要调用链、前端差异和定向复验，不是全仓逐行穷尽审计。未重跑历史B1全部桌面交互、各版本完整影片oracle、完整package Runtime像素套件或浏览器W0。982项通过不是完整后端验收；F1仍待独立修复。
