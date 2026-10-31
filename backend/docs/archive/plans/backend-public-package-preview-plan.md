# P2-A：直接效果包的公共项目预览与完整诊断

2026-10-19。施工基线：`refact/ttml-first-support` / `cf25cf587b1a61b026768a69bb52d66399b9989a`，产品0.97.0、合同1.28.0。**本计划已由主代理验收；当前S1/S2及合同已通过，S3真实HTTP尚未执行，本批未关闭。** 目标产品 **0.98.0**、HTTP合同 **1.29.0**，HTTP protocol保持`"1"`。

## 1. 来源、目标与范围

用户要求继续执行下一步，并授权通过Agent Mail向Claude同步变更。TTML/ruby时间轴及spacing分别已在0.96、0.97关闭；现行路线见[Web交接§4](../../../web/README.md#4-调整后的开发步调)：P2先公共package HTTP/诊断，再接预设、受授权媒体与字体闭包，随后扩大正式任务。本批只交付P2-A。

目标是让**既有合法项目中的直接效果包**，通过当前项目预览HTTP入口进入真实受管理Worker，并完整读取源码编译、图效果、材质及runtime诊断。沿用当前上传/项目open、精确修订、preview create/input/frame/state/diagnostics/close、PNG资源认证、回执、SSE和所有者机制；不新增接收任意源码的执行路由，不新增作者写入命令。

本批允许：

- 已由项目加载器接受并固定的包manifest/files、直接实例、包材质、效果预算、原effect-input fingerprint及完整已接受分析。
- 原歌词、ruby独立时间轴、共享spacing、场景及普通/透明输出。
- 原包codec支持的内存包资源；资源字节随固定snapshot一次open，不在逐帧时重新读取库或工程目录。
- 本机现有字体环境下，与相同固定输入的原Runtime等价；不声称跨机器字体确定性。

继续拒绝：

- 包profile里的非空`PresetApplications`，包括同时存在静态preset和直接包的项目；不得剥离preset后成功。
- 非Color背景、背景路径/媒体、外部媒体及可搬移字体闭包；不提供新的文件访问权限。
- 作者草稿/源码编译采用发布HTTP、B5/B6、完整B3、完整B4、浏览器/用户验收及原性能/长片/GUI/平台暂停矩阵。

**B4正式验证、导出及场景测量保留现有静态profile范围。** 包预览成功不自动开放这些任务。静态模板固定试样与preset候选预览也保留现有范围。

## 2. 当前代码证据与必须修复的断点

| 位置 | 当前事实 | 本批处理 |
|---|---|---|
| `Backend.Rendering/PackageProjectSnapshotCodec.cs`，`StaticProjectSnapshotCodec.cs:135–209` | 私有包codec已有完整源、预算与领域校验，拒preset/非Color背景 | 复用，不放宽输入范围 |
| `Backend.Hosting/WorkerPackageRenderFrameSessionFactory.cs:26–56` | 固定包一次open、真实Worker、失败映射已有 | 接到预览专用路由并提供实际能力握手 |
| `Backend.Contracts/BackendSnapshotProfiles.cs:6–10` | 三个静态profile及静态allowlist | 保留静态门禁；package不能因公共预览开放而被静态codec接受 |
| `Backend.Server/PreviewBackendService.cs:265–307` | 精确修订capture后仍固定选择/编码静态profile | 根据完整快照选择预览profile、能力校验和对应工厂 |
| `Backend.Server/PreviewWorkerConfiguration.cs:34–50`，`Program.cs:95–97` | 仅静态capability/工厂 | 增加独立package协商与组合工厂，不以配置路径存在当能力成功 |
| `Backend.Hosting/ProjectSession.Preview.cs:16–30` | Writer内核对所有者、closing、精确修订并捕获不可变snapshot | 保持，不在Writer内等待Worker/执行脚本 |
| `Backend.Server/PreviewBackendService.cs:276–283`及Hosting项目capture | 当前在Writer外读取`AudioAssetId`、预留资源，再在Writer内捕获snapshot，存在不一致竞态 | 本批修复：快照、音频身份及快resource reservation在同一Writer内原子捕获，closure一致性属于本批必要范围 |
| `Backend.Hosting/BackendPreviewCoordinator.cs:253–260` | 失败只保留runtime snapshot，丢`SourceDiagnostics` | 完整保留compile source failure |
| `Rendering.Abstractions/RenderSessionFailureException.cs`、`Backend.Worker/PackageRenderSession.cs`及Hosting包工厂 | runtime Error失败路径还需保留实际image/material旁报 | 复用已有私有PackageDiagnostics传输，不预计改变私有wire；异常新增可选init属性并由工厂完整还原 |
| 同文件`:54–84` | 帧已有image/material诊断 | 通过Registry公开，不静默丢弃 |
| `Backend.Hosting/BackendPreviewSessionRegistry.cs:362,420–458` | 公共投影只处理runtime，旧总预算64KiB | 实现不同origin的完整投影及组合预算 |
| `Backend.Contracts/BackendLifecycleContracts.cs:81–87` | 原诊断只有通用字段/runtime occurrence | 兼容扩展独立公共payload，保旧静态输出 |

表中路径均相对于`src/MakoKaraGenerator.`，行号属于施工基线，后续以调用链和提交diff为准。

## 3. 预览profile与固定输入合同

1. 预览专用选择器根据完整snapshot判断是否有包源（包括包/实例/材质/预算/fingerprint）；不能只按实例数量判断，更不能通过静态编码失败后剥字段重试。无包输入保持原三静态profile。
2. 包输入选`package-lyrics-scene-v1`。静态`BackendSnapshotProfiles.IsSupported`、`StaticProjectSnapshotCodec.SelectProfile`及B4调用链不承担公共包路由，不因新增preview capability而扩大decode能力。
3. 保持4MiB codec总字节、200000源值及原深度门禁；进入Worker前再按原包IPC扣除24字节头部。空、坏身份、超预算及不支持的preset/背景返回稳定field/failureCode，不编译、不发布像素。
4. 能力必须由实际Worker确认EngineIdentity、包profile、PackageVersion/SDK/shader、完整实际compiler refs身份和`PackageDiagnosticsVersion=1`。旧/缺refs/不匹配Worker在发送包open之前拒绝；不得回退静态或Host执行。能力绑定实际进程与原pipe，真正open仍复核。
5. 不改变公共preview请求形状，由服务端选择profile；`runtime-info`公开实际可用来源，能力失败准确说明。静态能力仍可独立可用，包探测失败不能禁用所有静态预览。
6. Writer内capture唯一精确revision，并在同一Writer临界区原子捕获snapshot、对应音频asset身份及快resource reservation，禁止在Writer外读取`AudioAssetId`后再捕获snapshot；允许Writer内快速取得resource引用，不允许在其中等待Worker、编译或编码。运行工厂必须使用捕获的固定asset身份，不能重新读取当前工程音频。必要调整`ProjectSession.Preview.cs`及`ProjectSessionRegistry.Preview.cs`，候选/模板路径保持兼容。包bytes在snapshot生命周期内不可变；音频retention与runtime引用分别持有，所有失败/取消/换输入/close出口归还引用。
7. 一个固定输入一次Worker open，逐帧只传现有会话/来源hash/序列/time/target/quality/alpha；保原latest pending、代次淘汰及来源修订关联。

## 4. 公共诊断、错误与预算

### 4.1 无损类型

保留`BackendDiagnosticDto`原六字段及旧静态输出，新增可选`Source`、`ImageEffect`、`Material`公共payload。`OriginKind`采用`compileSource`、`imageEffect`、`material`；每项恰有对应payload。`runtime`仍使用原Occurrence；`transportFailure`不能冒充作者实际发生。公共类型不直接复用私有Worker DTO，新增nullable payload在不适用时省略。

- **Source**完整保留`PackageId/Revision/InstanceId/ExportId/Code/Message/IsError/Path/Line/Column`十字段；nullable身份保留，0行/列合法。编译失败没有runtime occurrence，不能补造epoch/sequence/time/artifact。
- **ImageEffect**保留真实instance/artifact、graph的package/revision/export、input及code。
- **Material**保留material资源身份/input/code及完整owner union：script的instance/artifact/package identity；source的path/region/slot/sourceMaterial/parametersJson。不得把源样式伪装成script实例。
- 资源路径只使用已有包内/源样式诊断语义，不加入服务端文件位置；不得用一段格式化message替代结构字段。顶层severity/message按明确稳定映射生成，真实payload值保持。

成功帧和失败路径都传完整数据。Coordinator必须携带`RenderSessionFailureException.SourceDiagnostics`，Registry必须消费帧的image/material诊断。runtime Error失败也必须保留同时发生的image/material旁报：Worker使用已有PackageDiagnostics失败容器传递，工厂解码后通过异常新增可选init属性携带；Coordinator/Registry不能只缓存runtime snapshot。预计不改变私有wire。新payload须有严格shape、origin匹配和身份校验。

### 4.2 Error禁止发布像素

任何实际runtime Error或source IsError都进入failed并清空LatestFrame。编译失败、Worker transport failure与图/材质合法旁路诊断区分；后两类无领域Error字段，仅映射warning，本身不得被误判为脚本Error；非法owner/handle拒帧。新帧只有在完整诊断shape、身份、Error与预算全部验证通过后才能发布，禁止先放入可读取frame store再因诊断失败回滚LatestFrame。失败不得保留看似成功的新帧或发布不完整诊断；**之前成功发布的frame lease按原TTL保留**，失败状态无LatestFrame，不要求旧成功frame content立即404。

### 4.3 有界完整分页

- 私有runtime保原256项/64KiB，私有三类包诊断保各256项/64KiB、合计512项/128KiB。
- 公共组合上限为768项、**完整UTF-8诊断DTO数组512KiB**，为公共wrapper保留空间；不能沿用旧64KiB合并预算误拒合法私有结果。Host公共投影再次检查runtime最多256项、包各类最多256项/合计512项，以及总768项/512KiB；私有字节预算仍由对应Worker合同单独保证。该方案已与诊断负责人核对，实现前由主代理最终验收常量及公开说明。
- 沿现有每页上限128项、最多32页；实际configured较小页数也必须在发布前校验。页含相同previewId/generation/sourceRevision，cursor只属于当前完整快照。
- 项数、字节或页数超限明确`PreviewDiagnosticsBudgetExceeded`/公共`diagnosticLimitExceeded`，不截断、不覆盖尾页、不把实际诊断改写为空成功。
- 一次发布完整页链；后续输入/seek/resize换代、close和租约清理使旧cursor失效。投影/编码在锁外完成，锁内只核对仍当前并提交，避免阻塞健康状态/关闭。

## 5. 文件边界与协作

主代理负责设计/验收、版本合同最终冻结、Git提交推送；主要编码与验证交子代理。每位执行者编辑前检查Agent Mail reservation并精确预留，未经协调不改其他负责人的文件。

| 工作 | 预计文件边界 |
|---|---|
| 预览路由/能力 | Server `PreviewBackendService.cs`、`PreviewWorkerConfiguration.cs`、`Program.cs`；Hosting包工厂、实际能力接口/预览专用选择器；必要Contracts预览能力声明 |
| 原子输入闭包 | Hosting `ProjectSession.Preview.cs`、必要的`ProjectSessionRegistry.Preview.cs`与Server调用方；snapshot/音频身份/快速reservation同Writer，握手及编码Writer外 |
| 诊断链 | Contracts `BackendLifecycleContracts.cs`、新增公共包诊断类型、`PreviewTransportValidation.cs`；Rendering.Abstractions `RenderSessionFailureException.cs`、Worker `PackageRenderSession.cs`、Hosting包工厂失败旁报；Hosting `BackendPreviewCoordinator.cs`、`BackendPreviewSessionRegistry.cs`、`PreviewDiagnosticsStore.cs`及独立mapper |
| 代码回归 | 对应Hosting/Server/Contracts测试；Runtime.Hosting真实Worker定向测试；必要Rendering codec兼容负例 |
| 发布/HTTP验收 | 独立探针/fixtures及ignored artifacts；匹配self-contained Server/Worker发布；合同真实exchange验证 |
| 收口 | `Directory.Build.props`；`docs/architecture/backend-api.md`；`web/`冻结生成物及README；实施记录/roadmap/AGENTS/本计划 |

原则上不改Core/schema/包profile/SDK/依赖、不新增程序集、不改B4生产任务。实际必须跨边界时先向主代理说明源码影响，再调整所有权与验收scope。

## 6. 执行步骤与验收门槛

### S0 计划冻结

主代理验收本计划、DTO union、来源选择及预算，独立提交推送；随后批准编码。计划提交本身不增加产品能力。

### S1 诊断与路由实施

完成无损诊断DTO/Coordinator/Registry/Store，以及预览专用profile路由/实际capability/包工厂接入。新增正反例：十字段source含null/0位置、image完整graph、material两类owner、非法origin/payload组合、预算/分页边界、compile failure和runtime Error禁pixels。静态三profile、preset候选/模板试看、B4包拒绝必须回归。

### S2 源码验收与相关回归

- 主代理沿真实capture→selector→capability→open→render→diagnostic→PNG/分页→release调用链验收，检查cache/租约与关闭异步路径。
- 正常solution零warning/error；Hosting/Server/Backend.Rendering/Worker及受影响Contracts/Runtime.Hosting定向scope按源码影响冻结，明确filter、不重复计数。
- 有意义变异：关闭包route仍应红、丢source或material字段应红、Error放行像素应红；安全恢复后重跑相关范围。变异与恢复不并正式通过数字。
- dotnet使用`flock -o /tmp/mkg-ttml-dotnet.lock dotnet ... -m:1`串行，最终验证期间固定源码输入，不边改边验。

### S3 fresh发布与真实HTTP

- 发布匹配Server/完整Worker及refs，记录source HEAD/dirty、源码输入及部署文件SHA；只能把clean输入称clean发布。
- 通过授权上传/open真实包项目→精确修订create→帧请求→state/SSE→认证PNG→完整诊断页→update/seek/resize→close。普通/透明、多时间点及回退，和相同完整snapshot的直接Runtime PNG/BGRA对照；需有反事实证明包贡献，不以文字可见代替。
- 编译失败、runtime Error、图效果/材质旁路分别真实触发，逐字段核对完整源；compile failure无伪造occurrence。跨页、预算拒绝与旧cursor明确验证。
- 实际Worker缺refs/旧诊断版本/身份不匹配拒绝。缺refs使用canonical发布的专属部署副本，由专属启动器直接启动Server，避免标准host预检先行拦截；不修改canonical发布。真实历史0.74原版Worker优先因EngineIdentity拒绝，准确记录实际原因；若仅改VersionPrefix后重新编译历史源码以隔离diagnosticsVersion门禁，必须记录完整diff/源码SHA并标为版本变体，不能称历史原二进制。codec或模拟协议负例另外列出。
- init/evaluate失控脚本覆盖cancel/deadline/close，核Host并发健康、下一新会话成功，以及实际观察Worker/Library.Worker/子进程PID+startTicks全部退出。沿既有本机Linux组隔离，不宣称任意恶意脚本OS sandbox。
- ACL/跨client、过期revision、回执、晚帧不覆盖新代次、close后释放、工程关闭后的固定输入/租约和静态兼容完整验证。
- Server结束exit0，运行/部署/source/evidence SHA复核。敏感凭证不进入URL、提交、日志或截图；202仅accepted，等待实际终态。

### S4 合同与交接

产品主体稳定后统一0.98.0，合同1.29.0/protocol1；重导OpenAPI/index/examples/manifest，check/validate及实际HTTP exchange全部匹配。记录新增路径/方法/schema/例/命令实际计数，不预写数字。补统一API、实施记录、roadmap/AGENTS和本计划实际状态，独立提交并同步远程，核HEAD/remote/工作区。

通过Agent Mail发送Claude可执行交接：固定提交、产品/合同、Server/Worker匹配要求、profile/DTO、能力禁用原因、完整诊断读取、错误恢复、范围及运行证据。前端独立实现并验收浏览器行为，后端不代记完成。

## 7. 状态、证据归属与Agent Mail

计划冻结时状态（历史基线）：**设计/施工前**，当时尚无本批生产实现、测试通过计数、fresh HTTP或浏览器验收；当前实际状态见§8。0.97的1330/1330、179请求/2689断言及更早B3-G/B4/TTML计数只保留各自历史范围，不并本批。

本批每轮保留command/exit/log/TRX/source与deploy manifest/HTTP exchanges/进程身份/evidence索引。作者局部、mutation、失败首轮、修复重跑、正式scope及独立发布运行分别记账；0tests构建失败不是测试通过。若最终源漂移，重新冻结和执行受影响scope，不能用旧green覆盖新输入。

Agent Mail：主代理已发送**消息#97**，目前登记为**persisted，未收到ack**；不得写成Claude已阅读、已接受或已接入。实施完成后更新实际提交和合同，向对端发送交付消息并记录实际delivery receipt。双方代码归属按AGENTS前后端协作约定，Claude负责独立Web仓库。

本批关闭必须同时具备：有限公共包预览实现、完整诊断、真实匹配Worker/HTTP证据、合同冻结、文档和远程提交。文件存在、内部包测试通过或消息persisted均不单独构成关闭。


## 8. 实施与验收状态（2026-10-19，最终验收）

计划`b9f02af`已提交推送；生产主体、相关测试及合同已经主代理验收，有限HTTP验收已完成，最终证据见§8.1及[实施§62](../history/backend-extraction-progress.md#public-package-preview-0980)。实现范围、合同、正式scope及运行证据统一登记于[实施§62](../history/backend-extraction-progress.md#public-package-preview-0980)，架构裁决见[ADR-0235](../decisions/decisions.md#adr-0235)。

| 阶段 | 当前状态 | 完成证据 |
|---|---|---|
| S0计划 | 已提交推送 | `b9f02af` |
| S1代码 | 主代理源码验收已通过 | 包专用路由/实际capability、原子capture、三类诊断及runtime Error旁报guard；VerifiedProfile非wire与Registry实际闭包门禁 |
| S2回归 | 1920/1920，9commands exit0 | 1386输入稳定；772观察后代含构建/测试基础设施全gone；summary/SHA见实施§62，失败、修复与mutation分列 |
| 合同 | 1.29最终重导/write/check/validate通过 | 80/89/463/91/2790/2149；原2648例保持，`artifacts/public-package-preview/contract-final/` |
| S3发布/HTTP | 已完成有限HTTP验收 | final6：1513请求/14090断言，独立失败目录另存 |
| S4交付 | 已完成文档收口 | 工具/证据/边界已登记；Agent Mail #97/#98仍persisted未ack |

实际实现补齐了原计划中的失败旁报guard：复用私有wire已有PackageDiagnostics字段，基础shape/包帧/proxy三个位置接受实际render HasError、source为空的image/material旁报，仍拒编译伪装、像素与geometry；open编译失败继续严格仅source。失败状态无latest，新失败PNG不进入可读store，旧成功lease保TTL。公共HTTP不返回source SHA，完整源hash与Runtime像素对照为独立oracle/内部证据。

当前HTTP组合根始终生成非null SourceProfiles，资源capture包装ReferencedFactory始终提供能力接口且缺inner能力时明确拒绝，因而实际accepted包必经Ensure；并未删除可信手工组合null SourceProfiles旧兼容。VerifiedProfile只能由可信组合根设置且不在wire；Registry还核对project来源和实际包闭包。快照/音频asset/快速reservation同Writer原子捕获，Worker与编码仍Writer外。以上以最终源码/测试验收为准；尚无完整公共B3/B4、B5/B6或浏览器/用户完成结论。


### 8.1 最终验收（2026-10-19）

工具最终提交 `ff6fff1` 已推送。真实HTTP使用不可变 `artifacts/public-package-preview/final2-6f4293d/dist`（manifest sourceCommit `6f4293d`、生产输入匹配）及Release Oracle；单次 `final6-ff6fff1` 通过 **1513请求／14090断言**，59组PNG/像素比较、47诊断场景、6组失控、84个Worker/子进程身份全部排水，3656个证据文件完成凭证扫描。evidence manifest SHA-256 为 `bfb411ddabf94a5036d521710923fdb2e5167edecd98a60893cce6597099957f`。

final2 Debug Oracle的artifactHash差异、final3项目会话配额耗尽、final5测量拒绝码预期错误均独立保留，不并最终计数；Release配置Oracle与Worker的SDK、script-refs、Roslyn字节SHA相同后重跑。历史current-version变体的静态旧schema实际返回 `SnapshotVersionMismatch`，只证明拒绝边界。
