# B4：正式验证、导出与分析任务实施计划

更新：2026-10-15。起点为0.64.0 B3-D静态工程Worker与HTTP有限出口。**0.65.0 B4-A固定静态正式验证的有限出口已完成**，终态332项限定测试／真正HTTP389请求及SHA见[实施§19](../history/backend-extraction-progress.md#b4-static-validation-0650)，当前实际路由只维护于[统一规范](../../architecture/backend-api.md)。**0.66.0 B4-B独立静态导出有限出口已完成，已决合同及验收判据见§6**；**0.67.0 B4-C独立离线音频分析有限出口已完成，见§7**，**0.68.0 B4-D显式采用／清除有限出口已完成，见§8**；**0.69.0 B4-E完整分析静态profile有限出口已完成，见§9**，**0.70.0 B4-F固定MP3／FLAC独立分析有限出口已完成，见§10**；**0.70.1 B4-G自包含音频准备卫生／未发布候选释放有限出口已完成，见§11与[实施§25](../history/backend-extraction-progress.md#b4-self-contained-preparation-0701)**；**0.71.0 B4-H固定MP3／native FLAC带音轨导出有限出口已完成，见§12与[实施§26](../history/backend-extraction-progress.md#b4-compressed-export-0710)**；完整导出输入与包脚本音频驱动视觉仍未完成。**完整B3的包／脚本／资源／字体／几何剩余不是B4的整体前置条件**，每个切片自行声明能力与门禁。

当前0.72.0 B4-I受管理媒体准备／deadline隔离有限出口已完成：`1c70fae`最小修复open错误wire兼容回归后，原八scope repair2统一775／775及保持原断言的严格真正HTTP699请求／4239断言通过。repair1 753与HTTP final1失败137／987独立保留；冻结合同见[§13](#b4-managed-audio-preparation-plan)，完整终态／部署／排水／SHA见[实施§27](../history/backend-extraction-progress.md#b4-managed-audio-preparation-0720)。版本切换不改变上述已验收批次的历史数字。

## 1. 当前决定：B4-A固定静态工程正式验证

首批选择真实 `RuntimeFrameValidator` 的固定帧计划任务，复用已有受管理Worker、专用静态snapshot及唯一的 `_jobs`／持久账本。不是新增第二个job注册表，也不把现有保存job改成虚构渲染进度。音频分析和服务导出放后续独立切片；不增加export／analysis路由，不扩大B3静态输入profile。

最小真实链为：客户端上传／创建或授权打开工程→提交精确工程修订及固定帧计划→主服务固定不可变输入和资源租约→受任务配额控制的Worker调用原正式验证器→实际普通／透明帧→固定报告→原子发布报告sidecar及job终态→认证HTTP读取报告／状态和SSE任务提示。随后编辑不改变已接受任务的输入，成功不能由空背景探针、HTTP202或预设进度代替。

### 1.1 沿用的业务入口

| 既有代码 | 本批用途 |
|---|---|
| `Runtime.Hosting/RuntimeFrameValidator.cs` | `ValidateAsync(ProjectSnapshot, RuntimeValidationPlan, IProgress<RuntimeValidationProgress>?, CancellationToken)`；两个独立普通／透明会话，每帧Final质量，按整数帧起点逐帧实际渲染 |
| `Runtime.Hosting/RuntimeValidationResult.cs` | 现有计划、真实完成帧数、输出模式／时刻、汇总issues与完整diagnostic occurrences；不能把汇总去重当作完整发生去重 |
| `Backend.Rendering/StaticProjectSnapshotCodec` | 固定15个源字段的snapshot-v1 static profile；保留音频声明、零IO，不以ProjectStore保存格式充IPC |
| `Backend.Hosting/ProjectSessionRegistry.Persistence.cs`及0.64的 `ProjectPersistenceJobs.cs`／`ProjectPersistenceJobLedger.cs` | 同一client回执、job所有权／保留／取消／close／shutdown与持久账本；B4通用化迁为 `BackendJob.cs`／`BackendJobLedger.cs` 仍用唯一注册表，新增正式验证kind和受控执行策略 |
| `Backend.Hosting/WorkerProcessProxy` 与 `Backend.Worker` | 有界IPC、输入／输出身份、deadline、协作取消和拥有进程树回收；正式验证只回报告，不逐帧搬裸pixels到主服务 |
| `Backend.Contracts`、`Backend.Server/ServerEndpoints.*` | BCL-only具名请求／报告DTO，精确修订、权限和读取；最终字段／路由以实际代码冻结后写入统一API，本文不提前另立一套当前DTO |

完整Runtime业务仍复用。Hosting继续不得引Runtime.Hosting／Skia；执行真实验证位于Worker组合根。Contracts不引Core／Runtime／GUI。公开格式28／16／6、配置1、profile2、SDK与固定依赖保持。

### 1.2 已定预算与能力门禁

| 范围 | B4-A计划合同 |
|---|---|
| 输入profile | 仅现有static-lyrics-scene-v1，所有unsupported非空字段明确拒绝；不能剥掉effects／preset／analysis／背景媒体再运行 |
| 帧计划 | 每输出最多600帧，两输出最多1200帧；帧时刻按Composition帧率整数除法计算，最后时刻不得超过固定OutputDurationUs；缺时长／溢出／非法计划拒绝 |
| 私有任务封包 | `task-validation-v1`的32字节KTV1头＋snapshot-v1净字节；净快照预算4,194,272字节，区别于preview open扣24字节的4,194,280字节预算。进度与最终报告独立校验task／源SHA／计划／序号，不回裸pixels |
| 本地验证尺寸 | 最多1920×1080且裸帧≤8MiB；在Worker内本地渲染，**不传裸pixels**。这不扩大HTTP预览的1280×720／4MiB合同，也不表示已有preview frame IPC支持FullHD |
| 运行时限 | deadline默认60秒，可信host可配置1～60并由实际capabilities公布，探针8秒仅为夹具；协作取消／超时回收宽限2秒，待实际进程排水／退出再给终态，不以释放Process对象冒充已停止 |
| 并发 | running全局2／每client1／每project1；queued全局16／每client4；计数和占位须在受控闸门内原子取得／释放 |
| 完整报告 | 最多256KiB、汇总issues256、实际occurrences512；超过预算明确失败，不截断／伪造完整结果。与B3逐帧256／64KiB诊断合同分别定义 |
| 持久化 | 完整报告原子sidecar；ledger只保留有界身份、报告hash／长度／引用，单ledger记录32KiB上限不能被完整报告绕过 |
| job保留 | 沿既有job／账本保留策略，正式验证与持久化job用同一权威注册表；新report租约／淘汰／删除出口须同时明确 |
| 权限与重启读取 | 当前live job／报告／诊断严格owner；旧host已在唯一ledger登记report引用的显式recovery读取沿既有B2本机profile-wide恢复权限，当前凭bootstrap登记的profile client可读，GET仍用accessToken。旧client／resume不跨host恢复，不回退live ACL，不自动重跑或把unknown升成功 |
| 断连／休眠 | 已接受正式job不因HTTP断连或client lease到期取消；工程与任务权限仍绑定client，读报告必须认证 |
| 服务关闭 | 停止接收，取消可取消正式任务并等待排水，保存committing不能被强制撤回；运行输入引用与Worker／报告暂存都归属可审计 |

只读工程可以进行不修改内容的正式验证；实际允许策略以新实现门禁确认。任务输入修订、snapshotSHA、帧计划、执行代次和报告身份要可核对，不能把当前工程修订当成已验证修订。queue后的任务仍持固定输入，不重新从正在编辑的工程抓“最新版”。

## 2. 随切片修复的真实失败边界

原 `RuntimeFrameValidator` 的await-using释放可能覆盖原渲染失败或取消，通用catch也不能保真识别中立 `RenderSessionFailureException` 的stable code／actual snapshot。B4-A同步保留primary失败／OCE、收集cleanup失败，并正确传输实际存在的occurrence；无真实Runtime报告时仅transport failure，不创造作者发生。不要只让成功路径通过。

另有已有 `VideoExporter` 的窄兼容修复：静态输入的通用渲染异常fallback不得吞掉中立会话失败、伪造成成功像素／成片。该修复是现有业务的失败合同，**不表示本批已经开放HTTP export**；保留原导出事务、取消和最后提交边界，受影响回归单独归档。

进度只来自实际验证器完成帧数／输出模式／时刻，不能以timer推百分比；汇总issue保留来源／输出语义，完整occurrence保留真实epoch／sequence及逻辑位置／包身份。公开错误和报告必须脱敏，不能输出私有路径、栈或启动／恢复凭证。

## 3. 可逐步提交的实施顺序

1. **先统一当前API文档。** 完成0.64实际规范及路由／DTO核对，草案只保留未来目标，README／AGENTS／方案导航指向唯一规范；独立文档提交，不升版本。
2. **固定验证请求与Worker报告接缝。** 新合同仍BCL-only，验证固定snapshot、计划／预算、输入哈希、真实报告身份和报告预算；Worker实际调用原验证器，补primary／取消／cleanup失败回归。新增类型／kind不复用40字节背景探针，也不把原preview raw限制悄悄放宽。
3. **接入唯一job生命周期。** 捕获精确修订／资源租约、受控queue／running配额、取消与deadline、report sidecar发布及ledger引用、close／shutdown／重启结果核对；任务结束与淘汰有独立释放出口。
4. **接生产HTTP与报告读取。** 唯一client级receipt，状态／SSE投影、认证读取和报告预算；只公布实际静态正式验证能力。沿原job查询／取消，具体新增路由以最终Server映射为准。
5. **有限验收后版本与文档收口。** 接口主体稳定后升0.65.0；若新增项目再挂solution，不凭计划注册空项目。正常构建及窄回归、真实HTTP／Worker证据终态后同步ADR／roadmap／AGENTS／实施记录和统一API，再按步骤提交推送。

代码归属由本批协作任务划分；文档代理不并行改Worker／Server／job生产代码。每步必须给具体差异、正常编译／回归及源／执行体／证据SHA，不能用施工版清单宣称终态。

## 4. B4-A有限验收判据

- 真实同一静态工程的正式计划覆盖普通／透明两个独立会话，帧数与整数时刻正确；原Runtime oracle／已存在验证器结果一致。只有真实完成全部计划才成功。
- 接受任务后编辑工程、撤销、关闭工程或client休眠，不改变固定来源；资源引用保留至实际Worker排水，终态释放。每client／project／全局queue与running配额在竞争下不超限，不驱逐现有job。
- HTTP创建／回执重试／异载荷拒绝、读job／读报告、跨client拒绝、SSE任务终态与报告身份一致；默认Server环境缺ASP.NET共享runtime时采用正常self-contained运行，不把testhost中止计为通过。
- 显式取消／deadline／真实Worker失效和Runtime／cleanup错误保留稳定原因与实际报告，primary失败不被Dispose覆盖；没有假作者occurrence或成功占位报告。shutdown等待拥有Worker全部退出。
- 完整报告尺寸／条数、未知／非法字段、错误身份／hash与sidecar发布失败均明确拒绝；ledger≤32KiB，重启能核对已发布报告，结果不明不得自动重放副作用。
- 重启后current live job路径404，previousHost已登记报告只从显式recovery结果路由读取；权限沿B2当前profile客户端恢复机制，不宣称已恢复旧owner凭证。未知／未登记／缺失／畸形／篡改sidecar明确拒绝。
- 原保存／恢复／预览／导出受影响窄回归与依赖图保持，正常分析器／警告即错误；只跑授权相关范围，不自动恢复全量、性能、GUI、正式长片或平台人工矩阵。

本节B4-A判据已按有限范围收口；最终执行范围、通过数、进程退出与manifest在[实施§19](../history/backend-extraction-progress.md#b4-static-validation-0650)归档，不将0.64的315／112或局部／失败过程倒记为本批统一通过。完整B4出口仍由下节后续切片继续。

## 5. 后续B4切片

| 切片 | 可复用入口 | 仍须独立解决／验收 |
|---|---|---|
| 服务导出（B4-B按§6有限完成） | `Export/VideoExporter.ExportAsync(Project, VideoExportOptions, progress, token)`、现有encoder／artifact事务 | 固定完整输入、可下载artifact、编码／mux／verification／commit阶段、音轨和普通／透明短片、失败／取消旧片保护；完整包媒体支持按能力协商，不由静态验证成功推导 |
| 离线音频分析（0.67 B4-C按§7有限完成，不含apply） | `Media/AudioFeatureAnalyzer.AnalyzeAsync(固定音频路径, token)`，注入受控Probe／PCM reader，保持原48k单声道20ms hop算法及source／PCM hash | 独立固定结果、asset pin、真实Worker／FFmpeg归属及预算；本批不含apply，后续应用须另核精确来源／修订并走领域命令，不自动写最新工程 |
| 完整脚本／资源正式任务 | 原Runtime／Export与后续B3资源合同 | 包／预设／分析／图片视频闭包、字体能力、实际脚本发生与失控恢复；分别声明能力，不暂停其他可独立交付的B4任务 |

原音频分析并非精准节拍识别器，服务迁移不能改变其算法／时间语义。完整B4出口仍包括真实短片帧数／音轨／透明输出、任务配额／取消／产物下载和最终提交保护；B4-A正式验证有限完成也不等于完整B4、B3或Web产品完成。

<a id="b4-static-export-plan"></a>
## 6. B4-B：固定静态工程独立导出（0.66.0有限完成）

2026-10-13用户授权继续独立导出批次；起点为已推送的0.65.0／`be4d677`，既有B4-A产品端点为`6944c00`。**本节已批准的静态合同与有限验收判据已落实，不表示完整B4完成。** 公开合同主体稳定后切0.66.0 MINOR并注册实际Processes库／测试项目；最终正常solution0警告／错误、7组8TRX共436/436，真正HTTP764请求／5682断言及独立stereo29附证已归档，见[实施§20](../history/backend-extraction-progress.md#b4-static-export-0660)。B4-A的报告证明不能替代本批真实影片、完整帧数或音轨证明。

### 6.1 输入、输出和音频授权

每个job只选择一种输出：普通H.264／MP4或透明ProRes 4444／MOV，编码质量固定Standard、渲染质量固定Final。首批仅生成不可变、可认证下载的artifact，不接受HTTP任意输出路径，也不覆盖用户文件。画布、帧率和显式输出时长沿固定工程Composition；原`ProjectValidator.Validate(..., ValidationContext.Export)`门禁保持，不能因为正式验证曾通过而跳过导出领域校验。歌词／注音的合法timing夹具不得靠删除未定时元素或更改原数据语义获得通过。

输入仍为`static-lyrics-scene-v1`，非空包／脚本／预设／分析／图片视频背景等明确拒绝；不能删掉unsupported字段后运行。`sourceSHA`是**原接受快照的专用codec字节SHA**。音频授权是另外固定的hash／size／访问grant；执行时受信接缝把音频访问路径绑定至grant指向的固定自包含资源，**本批直接pin不可变ResourceStore文件，不默认复制音频**。不得将访问路径重绑定后的描述冒充原sourceSHA，也不得因快照含AudioPath就授予任意文件IO。若后续确需副本，须独立计入暂存预算，不能混入媒体＋报告bundle预算。静态codec继续只无损表达源字段、零IO，不改用工程保存格式作为IPC。

`includeAudio`默认true：必须有固定且已授权的PCM16 WAV，单声道或双声道，Worker按WAV内容强制解码，直接读取受租约保护的自包含固定资源，并复核grant hash／size。源PCM采样帧派生时长必须严格不短于计划视频时长；实际产物音轨时长允许50ms容器／编码量化容差，HasAudio、正确codec及正时长仍必需，不宣称AAC逐样本无损。缺失、畸形、被替换或超出音频能力的来源明确失败，不能沿桌面Exporter的警告策略退成静音成功。首批其他音频编码／容器不支持，**不纳AAC复制矩阵**。`includeAudio:false`是显式纯视频，不读取描述路径；0.66批次当时音频分析HTTP尚未开放，后续0.67见§7。

### 6.2 已定预算、并发和能力边界

| 范围 | B4-B已决合同 |
|---|---|
| 工程时长／帧率／帧数 | 显式正时长≤10秒、帧率≤60fps、计划≤600帧；总帧数沿原Exporter的`max(1, Round(durationUs / 1e6 × fps, AwayFromZero))`，不是ceil；第index帧时刻沿整数除法`floor(index × 1_000_000 × denominator / numerator)` |
| 派生视频时长 | 由实际计划帧数及有理帧率推导，也须正且≤10秒；音频最短时长、产物帧数与时长校验采用该计划，不能把Composition时长与帧量化后时长混为一项 |
| 几何／本地pixels | 最多1920×1080且单裸帧≤8MiB；普通MP4沿原yuv420p偶数尺寸门禁，透明格式保留原格式约束；本地渲染／编码，不向Host逐帧搬裸pixels，不扩大预览IPC限额 |
| 执行时限 | deadline默认120秒，可信host可配置1～120秒并由实际capabilities公布；取消／超时回收宽限2秒，终态等拥有Worker及native／FFmpeg实际排水，不由客户端延长 |
| 媒体单文件增长 | 最大64MiB；首批Linux-x64 Worker使用`RLIMIT_FSIZE`硬单文件限制，配合早期监测及回收；guard不可用则export disabled。它不宣称所有平台已支持，也不代表整个磁盘或所有子系统总写入量均被内核限制 |
| 完整报告 | 最多256KiB，另于媒体预算计算；超限明确失败，不截断后宣称完整报告 |
| 存储占位 | 每个接受job先预留64MiB媒体＋256KiB报告；全局512MiB包含queued、running、retained及活动read lease占用，原子取得／释放，不把正在下载的产物当作已回收容量 |
| 统一执行配额 | validation／export泛化为同一个scheduler：running全局2／每client1／每project1，queued全局16／每client4；同时受全局字节占位约束，不保证可以同时占满所有queue名额。持久化控制job不占该执行slot，不另建export job注册表或ledger |
| 保留和清理 | typed artifact store位于可信StateRoot；默认沿job保留1小时，启动及admission物理prune，流式读取持read lease，任务／淘汰／关闭各有归属明确的释放出口 |
| 读取和恢复 | live job／报告／artifact严格owner，认证GET／HEAD及Range／ETag／TTL；previousHost且唯一ledger登记的显式recovery读取沿B2当前bootstrap-authenticated profile客户端授权，旧client／resume不跨host恢复、不回退live ACL、不自动重跑或把unknown升成功 |

只读工程可以接受不修改内容的导出；接受后编辑、撤销、关闭工程、HTTP断流或client休眠不能改变已固定输入或撤销已接受任务。显式cancel、工程关闭政策和服务shutdown沿原job合同；可取消任务排水，公开committing不可撤回。

### 6.3 两层提交、严格验证和必要失败修复

复用原`VideoExporter.ExportAsync`，但其OutputPath仅指向每job私有candidate。原Exporter的编码暂存→验证／资源释放→最后取消检查→`VideoEncodingArtifact.Publish`仍保留；该次`ExportStage.Committing`只表示**Worker私有candidate提交**，不能让Host job提前进入不可取消状态，也不是公开artifact或账本成功。

Host公开事务必须按以下次序完成：Worker／Runtime／FFmpeg排水→严格验证实际帧数、大小、尺寸、有理帧率、音轨或显式无音轨、透明Alpha、媒体hash及完整报告身份→最后取消检查→唯一`BackendJob.EnterCommitting`→同文件系统一次`Directory.Move`公开媒体与report的单个不可变包→唯一ledger登记终态。媒体与报告不能分别移动而制造半个可读取结果；提交前失败不公开，新失败不能改写已成功的旧artifact。公开提交后的迟到取消不能改写成功；ledger终态落盘失败沿unknown合同，不假称取消或自动重做。

原桌面Exporter的缺音频／探测警告、静态普通绘制fallback等兼容语义保持；本批严格服务门禁必须另外拒绝`FailedFrames`／`DroppedFrames`非零、计划计数不符或实际媒体缺失，不能只凭`ExportReport.IsVerified`或FFmpeg退出0公开成功。实际进度来自渲染／编码／验证阶段，不以timer伪造百分比。

报告`isVerified`保原Exporter对nominal时长的验证值，新`strictlyVerified`才表示派生计划严格门禁；低fps至少一帧可使前者false、后者true，不能伪改旧值。源`audioDurationUs`与产物`actualAudioDurationUs`分别记录。实际透明NTSC试样曾暴露受限WAV的`-shortest`裁掉末帧；仅这条强WAV路径跳过`-shortest`并保留**原已有**`-t`上限，默认桌面路径保持，实际decoded帧数必须精确匹配，不能靠放宽计数过关。

异常仅在携带真实非空Runtime snapshot时，可生成有界`kind:exportFailure`的report-only包；job只存FailureReportRef，不含ArtifactRef／媒体，既定export-result／diagnostics读同一登记报告。transport-only失败只保stable code，不创造报告或occurrence。失败报告也经同一公开取消闸门，取消先赢不发布；完整报告超预算或准备失败保原primary并附内部原因，不截断伪完整，不把注入snapshot回归宣传为真实脚本验收。

随切片修复的范围限定为：新增BCL-only进程拥有基础及其测试，处理owned native进程组、Worker先退出时仍能回收其FFmpeg／ffprobe子进程、FSIZE硬限制与有界文本；窄修FFmpeg／Probe归属和取消／释放；修原Exporter早期primary失败或OCE被cleanup覆盖的边界。实际全Hosting回归还暴露subreaper对原普通Worker后代回收的影响，追加按固定PID／startTicks归属回收，以及取消／shutdown控制写入与回执共用deadline的窄修；不调用waitpid(-1)抢其他.NET Process退出权，也不以忽略zombie放宽原断言。失败保留中立stable code和真实存在的Runtime snapshot；没有真实发生则只给transport failure，不创造作者occurrence。所有公开错误／报告脱敏，不暴露私有路径、栈或启动／恢复凭证。

### 6.4 实施所有权和顺序

| 归属 | 本批负责范围 |
|---|---|
| Worker／Runtime接缝 | 专用export IPC、executor／adapter、固定snapshot到原Exporter的受信转换、有理帧数helper、音频grant复核；Worker组合根及项目引用。Hosting不反向引Runtime／Skia |
| Hosting／Server | BCL Contracts DTO、唯一job／ledger和共享scheduler、typed artifact store与容量／read leases、音频pin与registry、真实HTTP映射／能力／配置／恢复授权、Hosting项目引用 |
| 进程／Export独立审查 | 新增`src/MakoKaraGenerator.Processes`及`tests/MakoKaraGenerator.Processes.Tests`，BCL-only基础处理native group／FSIZE／有界输出；FFmpeg／Probe窄修、Exporter早期primary；独立原Runtime oracle、真实HTTP成片／失败取消探针和完整解码证据 |
| 统一文档／验收 | 仅实际源存在后注册新项目；公开合同主体稳定并通知作者后升0.66.0；唯一当前API逐条对照实际代码，再同步ADR／roadmap／AGENTS／实施记录及入口导航，保留0.65历史证据 |

先冻结请求／授权／预算和执行接缝，再落Worker与统一job／artifact事务，随后接真实Server、独立产物验收。所有作者源码freeze后才执行一次统一正常build和必要回归；使用`flock -o /tmp/mkg-backend-b4-dotnet.lock`串行dotnet，正常restore／分析器，`-m:1 /p:UseSharedCompilation=false`。每个原进程等待真实exit和终态TRX，不用另起替代套件掩盖未结束或失败范围。提交由root按可审计步骤验收；不动既有screenshots。

### 6.5 独立有限验收矩阵

- **真实成片与内容：** 同一固定合法歌词／注音／场景工程、3秒PCM16 WAV、320×180／30fps，普通MP4和透明MOV各独立job、各90编码及实际完整解码帧；解码画面确有非空正文／注音／场景，与独立原Runtime oracle按有损RGB容差及透明Alpha分别比对。两片均须实际完整解码音轨，检查编码／采样／时长；不能只看报告或扩展名。
- **窄有理帧率与纯视频：** PCM16 WAV＋30000/1001的短样例核对原RoundAway计划、整数帧时刻、实际完整帧数及派生时长；另验证显式`includeAudio:false`确无音轨。首批AAC等unsupported明确拒绝，不宣称复制分支已验收。
- **固定来源与容量：** 精确revision／回执、接受后编辑／undo／close／休眠仍绑定原codec sourceSHA及audio grant；validation／export共享配额竞争、queued／running／retained／read占位、输出增长硬限及报告预算均实际验证。guard不支持时能力disabled，不能以探针配置冒充产品默认。
- **失败与真实排水：** queued／active cancel、可信host缩短deadline、缺失／篡改音频、Worker首开失败及运行中SIGTERM、FFmpeg／probe／验证／释放失败、媒体或报告超限均有明确终态；未提交产物不可读、旧成功产物SHA保持，拥有Worker／FFmpeg／ffprobe全退出，不以父PID消失推导子进程回收。
- **公开提交边界与读取：** privateCommitting仍可public cancel；最后检查到Host.EnterCommitting闸门竞争、不可取消公开提交、迟到cancel／shutdown、Directory.Move或ledger失败均保留真实结果。认证下载／HEAD／Range／ETag／TTL、跨client拒绝、流式read lease和restart显式profile恢复读取核对同一登记hash／size；unknown不升成功、不重跑。
- **受影响原合同：** 正常solution；Hosting／Worker／self-contained Server各全本项目；原ExportCommitBoundaryTests整类52个短片定向、中立失败6项及新增早期primary回归；Media／Processes有界相关定向。Runtime只纳实际Export／native接缝及依赖相关范围，最终filter随实际修改冻结；不启动App／GUI，不跑全量、性能、长片、分析HTTP或人工平台矩阵。通过数只在原TRX终态后记录。

最终归档需要源码／执行体／影片／完整解码／音轨／日志／TRX及manifest SHA，区分受控执行器接线测试与真正HTTP→Worker→Runtime→FFmpeg链。0.65的332测试／389请求及其正式报告比对保持原范围，不倒记为0.66成片证据。完成本切片仍不表示完整B3、B4、脚本／媒体闭包或Web产品已完成。

<a id="b4-audio-analysis-plan"></a>
## 7. B4-C：独立离线音频分析结果（0.67.0有限完成）

2026-10-13用户授权接续B4-C，起点为已推送的`1df53c1 / 0.66.0`。**本节合同及有限验收已落实：正常solution0警告／错误、五TRX460／460，真正HTTP789请求／5943断言及全部source／部署／证据SHA已核验，不表示完整B3／B4完成。** 公开合同主体落地后已切0.67.0 MINOR，产品已完成本批有限出口；统一API只记录实际合同，完整证据见[实施§21](../history/backend-extraction-progress.md#b4-audio-analysis-0670)。本批决策／权威有限出口分别见ADR-0211／roadmap§18.189／实施§21及终态证据。

### 7.1 固定音频任务与明确不包含的写入

本批仅运行独立结果任务：工程精确revision／已授权固定audio→真实独立Worker调用原分析算法→完整有界分析结果→唯一job／ledger登记→认证读取及显式previousHost恢复。**接受任务和任务完成都不调用SetAudioAnalysisCommand，不改Project.AudioAnalysis、修订、dirty或历史。** 不新增apply命令；将来应用必须另有明确请求、精确修订和当前音频身份门禁，走既有一次领域历史，不能分析结束后写“最新版工程”。

输入只捕获当前client拥有工程的exactRevision与固定audio asset／hash／size／受信访问grant，直接pin ResourceStore文件，不默认复制、不接受HTTP任意路径。无需ProjectSnapshot或staticcodec，因此含effects／已有AudioAnalysis的工程也可分析其授权音频；不能因纯音频任务去剥离项目其他字段或要求静态渲染profile。分析来源profile为`pcm16-wave-analysis-v1`，私有执行profile为`analysis-pcmwav-v1`；不与静态snapshot身份混用。**static-lyrics-scene-v1保持不变**，其非空分析／包脚本／图片视频等渲染输入仍unsupported；分析结果独立存在不表示已经可渲染或应用。

复用`AudioFeatureAnalyzer`、`AudioFeatureAnalysis.ToSourceData`及Core已有`AudioAnalysisRevision`／`AudioAnalysisJson`。算法仍为bass-pulse-rbj-v1，48kHz mono、960样本／20ms hop，半开时长按真实样本向上取整，hop数量为ceil(sampleCount／960)、起点index×20000us。最后短hop用真实长度，不补零。整曲最大能量／onset归一化完成后才生成最终points；不边解码发布“最终hop”，不截取前300秒冒充完整结果，不宣称精准BPM或新节拍识别能力。

### 7.2 已决预算与真实执行能力

| 范围 | B4-C已决合同 |
|---|---|
| 授权音频 | 自包含RIFF PCM16 WAV、mono／stereo、采样率≤192kHz且正数、单资源≤16MiB、源时长≤300秒；强WAV demuxer及file／pipe协议，复核实际hash／size，不由文件名或MIME证明自包含 |
| 实际解码 | 固定48kHz mono，最多14,400,000真实样本／15,000完整hops；samples guard在builder.Append之前执行。超过即明确失败并回收，不裁切、不降采样分析密度或伪造部分available |
| 完整结果 | 原canonical AudioAnalysisJson，最多4MiB；完整points、double值／可空phase、source／PCM hash、真实sampleCount、duration、算法profile及revision均保留；非法值、缺hop、超限或非canonical明确拒绝 |
| 身份与精度 | canonical完整字节hash即Core ContentHash；语义Revision另由算法／sourceHash／pcmHash／sampleCount定义；私有wire inputSHA另绑定任务封包，三者不混。原canonical的sampleCount／durationUs／timeUs保持JSON number，上限内均JS安全；新job input／progress的64位计数和时间按实际DTO字符串合同维护，不改原文件格式 |
| 结果存储占位 | 独立analysis store全局64MiB，每接受任务先预约4MiB；包含queued／running预留、retained及expired active-read实际占用。字节预算可先于queue名额耗尽，不驱逐未过期或正在读的结果 |
| 统一执行配额 | 同一validation／export／analysis scheduler，running全局2／每client1／每project1，queued全局16／每client4；唯一jobs／ledger／client回执，持久化控制job不占执行slot，不建第二份分析任务注册表 |
| 时限与能力 | deadline默认60秒，可信host环境配置1～60秒、capabilities公布实际值；取消宽限2秒。真实Worker private group＋subreaper及工具有界probe独立验证，不依赖export编码器或RLIMIT_FSIZE，guard不可用则analysis disabled；ToolStartupVerified只证明有界启动，不替代实际解码验收 |
| 保留与读取 | job默认1h授权TTL，启动及admission物理prune，read lease持有到读取结束，过期活动读仍占预算；current live严格owner，previousHost唯一登记结果显式recovery沿既有B2 profile权限，旧client／resume不跨host恢复 |

Probe／PCM读取需要明确受信工具路径、强WAV／协议策略、有界stdout元数据和stderr；原PCM reader的无界StringBuilder不能直接进入服务任务。原Analyzer的NoStream／Unavailable／AnalysisFailed在服务中为失败，不创建可用空points；OperationCanceledException保留取消，不包装成分析不可用。失败消息脱敏，不含私有路径、启动参数或凭证，也不创造Runtime作者occurrence。

### 7.3 接线、结果事务及四个规划模板

唯一JobResponse／摘要新增AudioAnalysisInput／Progress／Result等尾DTO，表示固定音频、真实samples／time与登记结果引用；原frame input／progress保持null，不把采样数叫帧或用timer伪造百分比。进度来自实际hash／probe／decode／finalize阶段和完成样本，最终结果只在整曲分析完整通过后发布。

规划仅四个路径模板，准确DTO／错误／配置名在实现冻结后写入唯一[当前API](../../architecture/backend-api.md)，本表不表示已映射：

| 方法 | 规划路径 | 语义 |
|---|---|---|
| GET | `/api/v1/analysis-capabilities` | 独立真实Worker／工具／group能力及实际预算，需client认证 |
| POST | `/api/v1/project-sessions/{id}/analysis-jobs` | exact expectedRevision＋metadata，捕获授权固定音频并接受独立任务 |
| GET／HEAD | `/api/v1/jobs/{id}/analysis-result` | 当前owner读取唯一登记canonical结果，完整性／租约／条件读取沿有界合同 |
| GET／HEAD | `/api/v1/recovery-jobs/{id}/analysis-result` | previousHost已登记引用的profile授权恢复读取，不回退live ACL／重放或猜孤立文件 |

正常查询／取消／SSE沿原job入口。接受后编辑、undo、关闭工程、HTTP断流或client休眠不改变固定音频或自动取消；显式close政策、cancel和shutdown按既有任务合同等待实际排水。Worker／native资源全部释放→Host有界prepare、flush与hash核对→lastcancel→唯一EnterCommitting→原子发布结果→ledger终态；迟到cancel不改成功，ledger失败unknown不重放。存储或验证失败不公开部分结果，旧成功结果保持。

结果IO从validation sidecar抽取BCL基础，但原validation目录、IDs、原始bytes、预算／配额及权限合同保持；canonical完整结果独立存储，ledger单条≤32KiB只留有界input／引用，不把4MiB数据塞入账本。Host校验原Core规范网格、revision、ContentHash与音频来源，重启读取同样复核，不触发重新解码／分析或工程写入。

### 7.4 实施所有权、提交顺序与验收

Worker作者负责私有任务／代理／executor和原算法桥接、采样早期预算、独立private-group能力及native任务；Hosting／Server作者负责BCL公开DTO、唯一job／scheduler／ledger、typed结果store和共用BCL IO抽取、音频pin／HTTP／恢复权限；Media／Processes作者负责强PCM策略、有界管道与实际子进程排水。统一文档／验收代理只在公共合同主体稳定后切0.67及按实际项目注册solution，源码全部freeze后才执行一次正常solution和已冻结有限scope。依赖边界保持：Contracts仅BCL，Hosting不反向引Runtime／Skia／GUI，真实分析在Worker组合根。

按计划／公开合同→受控Media及Worker真实分析→唯一job与结果发布→实际HTTP接线→有限终态／版本文档的步骤提交推送。使用`flock -o /tmp/mkg-backend-b4-dotnet.lock`、正常restore／分析器、`-m:1 /p:UseSharedCompilation=false`；逐个原session等待实际exit／TRX，不重复已绿作者过程或开替代任务掩盖失败。

- **真实完整分析：** 合法短WAV覆盖不同源采样率、mono／stereo、真实静音、低频／脉冲及非整hop短尾。HTTP→真实Worker的完整canonical bytes与独立原Analyzer＋ToSourceData＋AudioAnalysisJson oracle逐字节核对，分别核sourceHash／pcmHash／sampleCount／ceil时长／全部points／Revision／ContentHash；路径移动同字节身份保持。静音available且零特征／null phase，不能与无流／失败混同。
- **固定输入与零工程写入：** 精确修订／单层回执、接受后编辑／关闭／休眠及恢复仍绑定原audio grant；完成后工程revision／dirty／undo／已接受分析均不因任务改变。含effects／已有分析的工程可只分析其音频，不调用staticcodec，不扩大渲染能力。
- **预算、失败与排水：** queue／running／字节预约、连续PCM超样本预算、超hops／结果字节、无换行持续stderr、源hash变化、未知字段／坏canonical／时间网格／非法值、准备／发布／完整性失败均明确拒绝，无partial available。queued／active cancel、可信host缩短deadline、真实Worker及FFmpeg故障按稳定原因结束，所有拥有进程实际退出；主因／OCE不被cleanup覆盖。
- **读取、恢复与提交：** live owner、跨client拒绝、GET／HEAD／条件读取、read lease和startup／admission prune、previousHost profile读取同登记bytes／hash；default1h不靠缩短产品TTL宣称真实HTTP过期，时间／reader记账用受控TimeProvider单列。lastcancel／committing／迟到cancel／shutdown及ledger unknown不重放保持。
- **有限回归：** 正常solution，Hosting／Worker／self-contained Server各全本项目；Media限定AudioFeatureAnalyzerTests、AudioAnalysisSourceDataTests、BoundedPcmReaderTests与原FfmpegAudioTests的两项解码回归，Core仅MediaAnalysisStorageTests。不另跑Processes旧FSIZE、Runtime／App／Application项目或新增独立渲染／影片验收；Hosting全范围保留既有RenderFrame／WorkerExportLifetime等兼容测试，不据此宣称本次完全未渲染或编码。本批独立真实oracle仅验收音频，原0.66影片与0.65报告证据不倒记。源码全freeze且root确认后执行，仍不恢复GUI、全量、性能、长片或screenshots。

本节0.67独立任务有限出口已完成；后续显式采用／清除已按§8交付0.68有限出口。其他音频容器、完整B3资源／脚本／分析渲染输入、B4整体和Web／作者闭环仍须独立推进；原暂停范围不自动恢复。


<a id="b4-analysis-apply-plan"></a>
## 8. B4-D：显式采用／清除独立分析结果（0.68.0有限完成，2026-10-14）

用户授权在统计当前后端完成内容的同时由子代理继续推进B4。本批起点固定为已提交的`1cb42ba / 0.67.0`。以下编码前冻结的有限合同已落实，**本批有限出口已完成，完整B3／B4仍未完成**；公开合同主体稳定后已升`0.68.0 MINOR`，工程／场景／模板格式28／16／6、配置1、profile2、作者SDK与固定依赖保持。正常solution0警告／错误、统一四TRX977／977及真正HTTP原21974的367请求／2522断言已终态通过；完整实际证据见[实施§22](../history/backend-extraction-progress.md#b4-analysis-apply-0680)、ADR-0212及roadmap§18.190。当前已实现接口以[统一API](../../architecture/backend-api.md)为准。

### 8.1 显式命令、当前修订与结果来源

沿用唯一`POST /api/v1/project-sessions/{id}/commands`，不新增任务、调度器、结果副本或恢复写入路由：

| 具名命令 | 载荷与门禁 |
|---|---|
| `audio.analysis.apply` | `analysisJobId`非空且≤128字符；拒绝歌词target／startUs／endUs；只采用当前live host、当前client拥有、同project session、succeeded且未过期的analysis job登记结果 |
| `audio.analysis.clear` | 不带analysisJobId或歌词时间字段；清除当前工程已接受分析，未设置时为no-op |

`ProjectCommandDto`已追加可空`analysisJobId`；原timing／undo／redo也拒绝混带该字段。两命令保留精确`expectedRevision`、Hosting单层client回执及同一工程Writer。先查有效回执再查修订；相同requestId／载荷重试只返回原结果，异载荷复用拒绝。只读工程不能采用／清除。

分析job的`sourceRevision`只说明分析时固定的来源修订，**不要求它等于当前修订**：歌词编辑后，只要本次请求的当前`expectedRevision`精确、当前授权音频身份仍一致，就允许显式采用。不能分析完成后自动改最新版工程，也不能从previousHost recovery结果直接采用；恢复结果继续只读，不能猜孤立文件或重新运行job。

### 8.2 有界完整性检查与一次领域历史

Host先核实live owner／job kind／成功状态／同session及未过期登记引用，读取原有≤4MiB canonical结果并核全部SHA、Core网格／revision／摘要、来源profile及sourceHash。当前授权音频assetId／hash／size、AudioMedia与访问路径必须与固定来源相符；在原≤16MiB音频上有界复核实际文件hash，不能只比较可被替换的路径字符串。

随本批窄修HTTP新建候选：CreateCore发布前从已授权StoredAsset固定AudioMedia hash／size／显示名，修正原fresh candidate仅SetAudioPath导致metadata为空、强同源采用门禁全部拒绝的问题；保持revision0／dirty／hasPath、租约及共享Preparation／App语义。不在apply时补写metadata，不额外制造第二条历史，不改schema。

所有文件IO、canonical验证及读租约释放均在mutation之前完成。最后再次核对live job、当前工程／音频和Writer条件，防止结果在准备期间过期、工程关闭或来源变化。失败或释放异常不能在工程已改变后才返回拒绝。

共享Application用例捕获Workspace引用、Generation、Project、音频／原分析及连续性，执行前按当前基线拒绝过期输入；采用或清除调用原`SetAudioAnalysisCommand`，成功变化只有一次`audio.analysis`领域历史。遇活动History group，实际变化明确拒绝，防止nonmergeable命令被通用BeginGroup合并；no-op仍接受。相同规范分析重复采用、对空分析clear均no-op；拒绝与no-op不改工程修订、dirty、undo／redo或工程事件。成功后读模型一致再发布一次变化；原command响应形状保留，客户端通过summary／原事件读取状态。

| 拒绝 | HTTP／稳定code |
|---|---|
| 字段缺失、越界或混带 | 400 `invalidRequest` |
| 未知／其他client／过期／previousHost job | 404 `jobNotFound` |
| 同owner非analysis、非succeeded、其他session，或当前audio声明／assetId／hash／size／AudioMedia／访问路径与job来源不同 | 422 `commandRejected` |
| 只读工程／当前修订变化 | 403 `readOnlySession`／409 `revisionConflict` |
| 登记sidecar／canonical／摘要损坏，登记当前音频资产实际文件缺失／link／bytes hash或size损坏，IO或cleanup失败 | 500 `taskExecutionFailed` |

### 8.3 工程摘要与静态profile边界

`ProjectSessionSummaryResponse`已追加可空`audioAnalysis`：未设置为null／按现有序列化省略；已设置只给有界status（`available`／`failed`／`noStream`）。available带algorithm、sampleRate、hopSamples、originUs、sourceHash、pcmHash、sampleCount、durationUs、revision、contentHash及pointCount；新增64位样本／微秒字段为十进制字符串。failed／noStream只给status，不发points、路径或原始诊断。成功保存／重开保留原完整canonical数据与身份，摘要不是工程存储替代品。

`static-lyrics-scene-v1`本批保持不变，任何非空已接受分析仍明确unsupported。采用后预览输入、正式验证和静态导出必须明确拒绝，不能静默剥离；clear或undo回空分析后恢复原静态能力。支持分析渲染的新profile须另批决定，不由本次采用推导为已支持。

### 8.4 所有权、提交顺序与有限验收

| 归属 | 本批负责范围 |
|---|---|
| Host代理 | Contracts／Hosting／Server、live结果授权及有界完整性、单Writer／回执、摘要与相关测试 |
| Application代理 | 共享采用／清除用例、稳定基线／连续性、原领域历史与相关测试 |
| review／HTTP代理 | 新独立`tools/backend-analysis-apply-http-probe`、真实HTTP→原Worker分析→采用闭环及代码审查 |
| 文档／统一验证代理 | 本节计划、固定0.67统计、主体稳定后的0.68版本及最终实际文档、统一有限验证 |

按计划先提交→共享用例／公开合同→Host／Server接线→独立HTTP工具→源码freeze后统一验证／终态文档逐步提交并同步远程。禁止并行dotnet抢占：统一`flock -o /tmp/mkg-backend-b4-dotnet.lock`，正常restore／分析器、`-m:1 /p:UseSharedCompilation=false`，Server使用linux-x64 self-contained；等待原session真实exit／显式TRX，不以替代任务掩盖失败。

- **共享语义：** Application全项目，原分析采用／清除一次撤销、redo、no-op保留redo、错误／stale基线零变化，整数身份及原完整数据保持。
- **Host／HTTP合同：** Hosting与self-contained Server各全本项目；当前精确修订、单层回执、跨client／其他session／旧host／过期／非成功拒绝，sidecar和实际音频篡改、租约释放失败、准备期间失效、只读与零变化语义。TTL由受控时钟单列，不缩短产品1h后冒称真实HTTP实等。
- **真实闭环：** 默认真实HTTP→独立Worker→原Analyzer完整canonical→歌词变更后的显式apply→summary／undo／redo／clear→保存关闭重开，核完整分析身份／canonical；成功与no-op回执、ACL、当前音频和结果篡改拒绝、static预览／validation／export拒绝，previousHost保持只读且拒绝apply。clear后以实际PNG像素oracle、正式验证报告及320×180／1秒纯视频MP4完整帧解码证明原静态能力恢复，不能仅凭202接受状态。
- **有限回归：** 正常solution零警告／错误，Core仅`MediaAnalysisStorageTests`；Worker／Media未改时不机械重跑旧全项目，有新影响才冻结必要范围。Hosting全项目原frame／export兼容不滤除，不把其计数或历史0.65／0.66／0.67证明合并成当前全量。

本批不恢复全量、性能、GUI、长片、screenshots或人工平台范围；完整B3／B4、其他音频、包脚本／图片视频／分析渲染输入、完整字体与作者／Web继续独立。实际有限出口与最终SHA以实施§22为准，计划与过程绿灯不替代终态证据。


<a id="b4-analysis-static-profile-plan"></a>
## 9. B4-E：完整已接受分析的静态工程profile（0.69.0有限完成，2026-10-14）

用户授权在说明代码量归属的同时继续B4。起点固定为`e0d3f3b / 0.68.0`；本节编码前冻结合同已落实，**本批有限出口已完成，完整B3／B4仍未完成**。公开主体稳定后已升`0.69.0 MINOR`；不新增项目，不改Core／schema／算法／SDK／视觉语义，格式28／16／6、配置1、profile2与固定依赖保持。当前API仍以[统一规范](../../architecture/backend-api.md)为准，正常solution零警告／错误、六TRX559／559及真正HTTP原23038的232请求／1864断言均已终态，详见[实施§23](../history/backend-extraction-progress.md#b4-analysis-static-profile-0690)、ADR-0213、roadmap§18.191；详细0.68行数归属见[相对基线报告§11](../research/backend-baseline-change-report.md#line-attribution-0680)。

### 9.1 唯一快照与双profile

复用现有`snapshot-v1`与唯一Source，不另造第二套工程／分析／任务输入。Contracts集中定义BCL-only `BackendSnapshotProfiles`，各入口使用唯一自动Select：

| profile | 分析合同 |
|---|---|
| `static-lyrics-scene-v1` | 仅AudioAnalysis为null；旧默认Encode／Decode及既有字节合同保持，仍明确拒绝nonnull |
| `static-lyrics-scene-analysis-v1` | 必须nonnull；完整保留Available／Failed／NoStream，不能将失败／无流降成null或空成功 |

新profile三状态均要求AudioPath＋AudioMedia。Available携带原完整canonical JSON、wrapper与contentHash，复核sourceHash匹配原AudioMedia、语义revision／完整hash／全部points及数值；Failed／NoStream保留完整code／message，不截断或改状态。共享`BoundedAudioAnalysisCodec`作纯有界校验／还原，供Host薄适配，旧独立analysis结果及Worker相关codec合同保持。它不授予音频路径IO，也不复制4MiB结果到新store。

新增profile只让**含分析的静态工程**进入原Runtime；无效果脚本时，分析不改变静态像素，不能宣传为音频驱动画面或新分析算法。包／脚本／预设／图片视频、完整资源／字体等原unsupported范围不扩大。

### 9.2 总预算、固定身份与能力门禁

| 范围 | 已决合同 |
|---|---|
| 总Source／IPC预算 | 仍4MiB／200,000值，歌词／场景／分析／wrapper共享同一总额，不各自另给4MiB |
| 完整值计数 | JsonElement所有object／array／值递归计入，不能把完整points当一个opaque值；最多15,000points／14.4M samples及原Core网格／数值门禁保持 |
| 预览／正式验证 | 现有24／32字节头部在admission扣除，超总预算明确413，不截断结果或剥离分析 |
| 导出封包 | executor `EncodeInput`按实际metadata＋32字节预算；必须在spawn／send前guard，失败清私有candidate。Host未知实际metadata时不重造jobId，不承诺所有超限均在admission413；202后也可按稳定budget原因失败 |
| 来源身份 | Host自动选择实际profile，job／preview记录原完整编码sourceSHA及固定工程revision；换路径授权不冒充原SHA，接受后clear／undo不改变已捕获输入 |
| 恢复 | 结果恢复读取用新旧两个固定allowlist，未知profile拒绝；旧任务／账本字节和恢复语义保持，不自动重跑 |
| 公开能力 | runtime-info／validation／export保留旧单`sourceProfile`，尾增`sourceProfiles`双列表；路由／请求不变，preview与validation仍configured-unverified，不把配置当真实运行验收 |
| 私有export能力 | 添EngineIdentity／SourceProfiles，Host经实际Worker probe匹配新engine后才enabled；旧0.68 Worker缺字段或engine不符明确disabled，不虚称支持新profile |

超canonical／网格／hash／状态／总字节或值预算均明确拒绝，不截断、重新归一化、丢metadata或静默使用旧profile。预算与坏canonical重点由codec及受控Host回归证明，不扩成新的巨型重复否例矩阵。

### 9.3 纯视频与音频授权

`includeAudio:false`保持完整Source／AudioAnalysis／AudioPath／AudioMedia声明，仅以IncludeAudio=false禁止Probe和编码器音轨IO；不为了纯视频清空分析或伪造无音频工程。源声明存在不授文件能力，真实链需证明无源音频IO。

`includeAudio:true`沿原可信audio grant路径重绑定并复核固定hash／size；不强求授权访问路径等于源声明路径，保原AudioMedia身份及显示名，不因重绑定丢分析。原sourceSHA仍是**重绑定前完整codec输入**身份，授权grant另列。原PCM16 WAV／纯视频、输出计划／帧数／时限／存储与提交预算保持，不扩大其他音频容器或文件能力。

### 9.4 所有权与提交顺序

| 归属 | 本批负责范围 |
|---|---|
| codec代理 | Backend.Rendering、项目依赖及测试；完整分析codec、profile选择、深值计数与总预算 |
| Host代理 | Contracts／Hosting／Server及测试；中央profile、公开能力、实际输入SHA／预算／恢复allowlist、薄分析结果适配、export实际probe门禁 |
| Worker／HTTP代理 | 私有WorkerExportContracts／Worker及测试；新profile执行与纯视频／授权重绑定，薄新HTTP工具重用原helpers |
| 文档／统一验证代理 | 本节计划、固定0.68行数统计、主体稳定后的0.69版本与实际API／终态文档、唯一统一有限验证 |

先提交计划与统计→公开／codec合同→Host与Worker接线→真实HTTP工具→源码freeze后统一有限验证／归档，逐步提交并同步远程。全部dotnet统一`flock -o /tmp/mkg-backend-b4-dotnet.lock`，正常restore／分析器、`-m:1 /p:UseSharedCompilation=false`，Server linux-x64 self-contained。等待原session实际exit／原TRX，不覆盖失败或换任务掩盖结果；不stage／提交他人未冻结源码。

### 9.5 有限出口与不扩大范围

- **codec／兼容：** 新profile完整三状态、原canonical／双hash／sourceHash、总4MiB／深层200k值与坏输入拒绝；旧v1仍拒分析，旧字节与未知profile边界保持。完整Available不能靠只核summary或points数量通过。
- **统一有限测试：** 正常solution零警告／错误；Rendering／Hosting／Worker各全本项目，Server全项目self-contained；Core仅MediaAnalysisStorageTests原28项，必要原Runtime实际Worker静态scope随实际影响冻结。Application／App未改不机械重复，全绿后不无理由扩范围。
- **真正HTTP：** 原Analyzer→显式apply→新profile真实PNG零差、正式普通1＋透明1帧；320×180／1秒纯视频MP4、PCM16音频MP4、透明纯视频MOV三片各完整30解码帧及原Runtime／音轨oracle。验证含已接受分析进入原Runtime，不仅202接受或字段回显；有损影片不声称逐像素相等。
- **固定来源与恢复：** 接受后clear不改变固定profile／完整sourceSHA，旧／新profile任务restart均可只读恢复；原unsupported明确拒绝、纯视频无源音频IO、真实旧Worker能力缺失disabled。恢复不扩大live ACL、不重跑unknown，其他旧生命周期合同保持。

本批不恢复全量、GUI、性能、长片、screenshots或人工平台／原暂停范围；完整B3／B4、其他媒体、包脚本／完整资源／字体／作者／Web仍独立。最终有限完成状态以实施§23的原终态、完整canonical／影片及source部署SHA为准，计划与过程通过不替代验收。


<a id="b4-compressed-analysis-plan"></a>
## 10. B4-F：固定MP3／FLAC独立分析（0.70.0有限完成，2026-10-14）

root已冻结本批仅扩展独立音频分析及显式apply／clear／保存重开兼容；起点为已推送`ec23912 / 0.69.0`。以下编码前有限合同已落实，**本批有限出口已完成，完整B3／B4仍未完成**；公开主体稳定后已升`0.70.0 MINOR`，不改Core／schema／算法／SDK／依赖，不新增项目或任务体系。带音轨export扩展另批，本批Export／VideoExporter／StaticExportTask不改，includeAudio:true仍仅PCM16 WAV，纯视频维持原能力。正常solution零警告错误、四TRX551／551及真正HTTP原37041的152请求／1255断言均已终态；实际证据见[实施§24](../history/backend-extraction-progress.md#b4-compressed-analysis-0700)、ADR-0214／roadmap§18.192。当前接口只维护于[统一API](../../architecture/backend-api.md)。

### 10.1 新旧来源与精度合同

| 固定来源profile | 音频profile／时长 |
|---|---|
| 原`pcm16-wave-analysis-v1` | 原`pcm16-wave-self-contained`及精确采样帧派生SourceDurationUs保持 |
| 新`fixed-compressed-audio-analysis-v1` | `mp3-layer3-self-contained`或`native-flac-self-contained`；SourceDurationUs为null且按原序列化省略，不写0或probe估值 |

JobInput的sourceDurationUs改为nullable；BackendAudioAnalysisGrant尾增AudioProfile默认WAV保原调用，public AudioRef尾AudioProfile为nullable／默认null以保持旧WAV wire省略，旧sourceProfile缺字段只解释WAV，新压缩profile必须显式MP3／FLAC且unknown／缺字段拒绝，必要时长依据字段如实表述；公共capabilities尾追加SourceProfiles，保原SourceProfile及旧字段兼容。必要durationBasis必须说明压缩源不预报精确时长，不能把未知时长伪成0；实际analysisResult的sampleCount／durationUs仍由完整48k解码样本按原Core规则生成。sourceHash仍为原上传压缩文件完整SHA，pcmHash为实际解码float32样本SHA，semantic Revision／完整ContentHash／wire inputSHA职责分离。

只处理不可变已授权asset，精确当前工程revision、asset pin／hash／size／租约与唯一jobs／scheduler／ledger／回执保持；分析接受／完成不自动写工程。已有显式apply只采用live同session成功结果、精确当前修订和当前固定音频声明／actual bytes门禁，旧WAV／新压缩输入分别按明确allowlist匹配metadata，不能泛accept任意profile。保存／重开保完整canonical；previousHost旧／新结果继续只读，不恢复旧client或重跑unknown。

### 10.2 轻量识别、真实解码与原预算

| 范围 | 已决合同 |
|---|---|
| 文件识别 | BCL仅按bytes轻量识别候选：MP3 LayerIII首帧／有界ID3，native FLAC必要STREAMINFO；不以MIME／后缀授能力，不造完整二进制解码器 |
| 固定音频范围 | 单文件≤16MiB，真实mono／stereo，正采样率≤192kHz；MP3仅LayerIII，FLAC仅native容器，其他codec／容器／多流或外部引用不因格式扩展开放 |
| Worker门禁 | 强制wav／mp3／flac demuxer＋file,pipe协议，实际probe codec／channels／rate严格匹配；压缩解码使用-xerror／err_detect explode，损坏不发布partial available |
| 完整分析 | 原Analyzer／ToSourceData／AudioAnalysisJson及48k mono／960sample hop不变；实际≤14.4M样本／15k points，在Append前限额；完整canonical≤4MiB，不裁切／归一化改算法 |
| 时长与截短 | 压缩admission不凭header／probe估值宣称精确≤300秒，实际48k计数上限权威；合法完整帧删尾仍可能成为独立短音频，不能声称识别所有截短原文件 |
| 任务生命周期 | 原60秒deadline／2秒宽限、4MiB预约／64MiB结果store／正式配额／1h TTL／read lease与提交取消闸门保持；native／Worker真实owned group排水，不复制音源或整曲PCM |

public／Hosting／Worker私有grant统一只尾增AudioProfile；Hosting／private默认WAV，public旧省略字段仅旧WAV sourceProfile按WAV解释，demuxer由中央GetInputFormat唯一派生，不重复维护InputFormat字段；metadata的新`analysis-compressed-audio-v1`与旧`analysis-pcmwav-v1`严格对应格式（旧仅WAV），不能用户指定工具／demuxer／任意路径。真实analysis capability probe严格核匹配engine、3个audioProfiles／2个sourceProfiles数组，旧0.69 Worker不能宣称支持新增能力；未知、缺字段或不符应明确禁用新增能力，不以工具启动成功替代真实格式解码。

### 10.3 兼容范围、提交与所有权

旧WAV ledger／结果字节／回执／精确duration保持，新增结果恢复及采用按profile＋input metadata固定allowlist核对。新输入不扩snapshot-v1的两静态profile、包脚本／图片视频／字体或export音频格式；新分析采用后原PNG／正式验证／纯视频可复用完整分析静态profile，不代表PCM16之外的音轨export已开放。

按计划→公开／受信音频合同→受限Media／Worker→Host／Server及兼容→薄HTTP工具→统一freeze验证／归档逐步提交推送。Host负责BCL轻量bytes classifier、中央profile／公开DTO／registry／grant与旧新profile ACL／恢复及相关测试；Media代理负责原probe／reader的受限格式和strict decode；Worker／HTTP代理负责私有IPC、WorkerBackendAnalysisExecutor／ProcessProxy真实cap接线、分析task与进程回收，只新增薄流程复用现有helpers／原Analyzer oracle；文档代理负责计划／版本／实际API与唯一统一验证。源码实际归属由root再定，不stage他人未freeze内容。

dotnet全部统一`flock -o /tmp/mkg-backend-b4-dotnet.lock`、正常restore／分析器、`-m:1 /p:UseSharedCompilation=false`，Server linux-x64 self-contained。实际遵守主体稳定后切版本、作者恢复／freeze及root明确授权后统一；等待原session真实exit／显式TRX，失败最小修复并重跑原scope，不覆盖过程证据或换scope隐藏失败。

### 10.4 最小有限验收

- **受控合同：** 格式候选／严格codec／rate／channels／预算／canonical与来源一致，坏输入、损坏不发布partial、metadata格式错配／未知profile拒绝；旧WAV同字节／时长、原结果／恢复／采用及受影响共享依赖保持。正常solution零警告错误，统一项目／filter待实际影响冻结，不机械重复App／Runtime／Export或旧大矩阵。
- **默认真实音频：** 两个MP3（CBR／VBR）及native FLAC代表44.1／48k、mono／stereo／真实短尾，HTTP→真实Worker的完整canonical与独立原Analyzer分析同一encoded bytes逐字节核对，核sourceHash／pcmHash／全部points／真实sampleCount／duration／Revision／ContentHash。不能比较未编码原PCM后宣称有损MP3逐样本无损。
- **最小闭环：** 新音频显式apply／clear／保存重开完整数据、PNG／正式验证择一实际oracle；旧／新profile结果restart读取、真实旧0.69 Worker新增能力禁用、必要坏输入及一次真实取消／owned进程排水。原回执／ACL／TTL／巨大预算矩阵以受影响原回归保留，不再复制成独立数百请求新探针。

本批不新增音轨影片矩阵、不改export；包脚本／完整资源字体、完整B3／B4／作者／Web及全量／性能／GUI／长片／screenshots／平台人工与原暂停范围保持。最终有限状态以实施§24的原完整结果／进程终态及source部署SHA核验为准，不以计划或过程绿灯替代。


<a id="b4-self-contained-preparation-plan"></a>
## 11. B4-G：Backend固定音频准备的自包含／身份／候选释放（0.70.1有限完成，2026-10-14）

本批起点`27a06e0 / 0.70.0`，修复实施§24.4已确认旧Backend create／open默认autodetect引用卫生及未发布候选cleanup。以下历史编码合同已落实，**0.70.1有限出口已完成，完整B3／B4仍未完成**；无新route／profile／schema／SDK／项目，不扩analysis或export音轨。原App／默认Media能力保持，原过程与最终证据见[实施§25](../history/backend-extraction-progress.md#b4-self-contained-preparation-0701)，当前API只维护于[统一规范](../../architecture/backend-api.md)。

### 11.1 Backend默认入口与受限音频

直接替换两个Backend默认factory：`NewProjectPreparationService.CreateDefault`及`ConfigurePersistence`默认音频依赖，均改用新的`CreateForSelfContainedFiles`；显式受信注入接缝保持。App与Media原`AudioWaveformLoader.CreateDefault`不改，不将服务音频准备收窄为分析三格式。

| 范围 | 已决合同 |
|---|---|
| 固定容器家族 | wav／mp3／native flac／aac／mov（M4A类）／ogg六家族；支持集合以真实兼容回归冻结，不凭MIME／后缀授能力 |
| Probe／Decode | 受限probe及typed强格式reader共用格式选择，file,pipe协议；MOV drefs=0，可识别／plausible首8／16字节BMFF（含unknown合法前置box）在probe前以有界structural gate拒绝外部dref／rmra；实际probe回报MOV后及所有MOV decode前再强校验 |
| 自包含边界 | 禁止ffconcat／播放列表／网络或外部文件数据引用；限制不仅是协议禁网络，也必须拒绝本地外部引用 |
| 原音频语义 | 原native选流、源采样率与PeakBuilder／波形金字塔保持，含原M4A／AAC／Ogg及非PCM16 WAV兼容；不套分析48k／300秒／mono-stereo算法预算 |
| 原宿主边界 | 本批仍Host直接拥有native进程；准备迁Worker／新增deadline隔离另批，不借Worker group声称已覆盖Host native取消 |

新loader只处理受信固定本地文件。结构gate／格式识别有明确字节、深度／条目边界，不造完整媒体解码器、不静默剥引用后退回自动探测。非BMFF识别／任意polyglot与所有QuickTime变体不承诺；非box首部经实际probe回报MOV的fallback只证明probe后／decode前门禁，不能称所有字节排列均在native probe前识别。通过header候选不等于已解码；完整info／PCM／pyramid及native终态才作为兼容证明。

### 11.2 固定身份、发布与未发布候选

Host在prepare前后核实际SHA／size、规范fullpath及no-link，不能只核manifest原bytes而让引用文件变化决定PCM。前后hash／read lease不是OS不可变快照，不承诺检测任意ABA并发写入；本批只报告实际固定输入门禁。create按已授权StoredAsset固定身份，资源pin／read／临时候选均有明确归属；open在工程AudioMedia存在时必须与固定实际bytes匹配，缺AudioMedia不附加此identity门禁、不主动补写metadata改变历史／dirty，始终先受原Core格式／版本／闭包约束；不称已真开任意旧版本path-only音频包。

所有可能失败的固定读取与read lease释放在发布前完成，最后再核固定输入；retention／已发布资源pin沿真实所有权保留，不能把它们一概要求发布前清空；释放失败不允许先发布新会话再报失败。修复create registerthrow及open initthrow时未发布candidate的资源出口：候选释放／登记撤销有明确成功、失败与取消责任，cleanup不能覆盖primary或OperationCanceledException；释放失败不虚称无残留，旧工程及旧已成功会话保持。

发布前create失败不返回201／注册假成功；membership已加入后的event／后置异常不回滚真实已发布成功session并保持其资源pin，不能笼统承诺任何HTTP失败无新session。open失败沿原job终态／诊断，已接受任务可以产生job／events，不承诺所有失败完全无事件。保持原client回执、修订／dirty／hasPath、目录／profile租约、唯一jobs／ledger与exactRevision，不新增准备任务体系或复制资源存储。Core版本准确门禁保持；旧0.70工程需要按其实际版本运行复现，不能改版本字段冒充兼容。

### 11.3 实施、版本及证据边界

先提交本合同→受限Media loader／结构gate→Backend固定身份／候选cleanup→定向反证恢复→源码freeze后统一验证／真实HTTP→终态文档逐步提交推送。Media作者负责受限probe／typed reader／自包含factory及兼容回归；Host作者负责create／open默认接线、固定bytes／租约及候选发布／cleanup；HTTP作者复用旧工具helpers作最小新真实流程；文档作者负责晚切0.70.1、唯一API／实施§25／ADR-0215／roadmap§18.193及一轮统一验证。实际源码所有权由root分配，不stage他人未freeze内容。

所有dotnet使用`flock -o /tmp/mkg-backend-b4-dotnet.lock`、正常restore／分析器、`-m:1 /p:UseSharedCompilation=false`，Server linux-x64 self-contained。按AGENTS §7，真实新增regression由对应作者做定点成功编译变异／真实断言拒绝并逐字节恢复；root确认finalfreeze后才运行统一正常solution／Hosting全／Server self-contained全／Media实际相关scope，具体filter依最终修改冻结，不机械重跑未改Core／App／Runtime／Worker／Export等域。等原session实际exit／原TRX，不覆失败或换scope掩盖失败。

### 11.4 有限验收与不扩大范围

- **实际兼容与拒绝：** 作者九个健康组合验证完整AudioStreamInfo、原native完整PCM及波形pyramid等价；覆盖六容器家族、非PCM16 WAV／源采样率等原语义，同时ffconcat及结构外部dref／rmra拒绝，强格式／协议native argv一致。默认App／Media参数与已有合法能力不误改；六family／九健康组合只是本机有限兼容，不等于所有QuickTime变体支持。
- **候选事务：** actual hash／size／path／no-link前后、open有AudioMedia错配拒绝与path-only保持、create registerthrow／open initthrow及cleanup/OCE双故障，发布前失败无候选会话半发布，membership后真实成功者保留，所有拥有租约／候选有真实释放出口，primary／取消保真。
- **真正HTTP原缺口：** 先在隔离原0.70部署由真实create／open以owned同目录引用复现，再在新部署拒绝；被引用WAV存在与被删除时均拒绝该固定引用输入，不授予外部读取能力。证据从旧nativeargv扩为本批HTTP，仅报告实际同目录自有测试范围，不夸大跨client或任意路径逃逸。
- **真正HTTP兼容：** 六家族正常create→save／close→open闭环，HTTP核实际duration／state／source bytes／identity，AudioStreamInfo及完整波形由原oracle／作者回归单列，精确版本／回执／旧工程保持。实际open取消按Host直接native PID／startTicks证明排水，不能拿Worker group代替；失败job与events按原合同观察，不假称无事件。

本批不新增分析／导出profile、音轨影片矩阵、GUI、全量、性能、长片、screenshots或人工平台验收；完整B3／B4、Worker准备隔离／deadline、其他媒体／包脚本／字体资源／作者Web及原暂停范围保持。原0.70／更早证据不并本批计数，计划或过程通过不算有限完成，最终须原HTTP终态与source／部署／进程及完整性SHA核验。

**本批终态（0.70.1有限完成）：** 正常solution零警告错误、Hosting460／SCServer33／Media六类64，三TRX557／557；修后真正HTTP131请求／936断言，1561源码／1014部署及1391证据文件SHA、终态与direct native退出已核。旧HTTP30／238独立，完整边界与最终证据见[实施§25](../history/backend-extraction-progress.md#b4-self-contained-preparation-0701)。

<a id="b4-compressed-audio-export-plan"></a>
<a id="b4-compressed-export-plan"></a>
## 12. B4-H：固定MP3／native FLAC带音轨导出（0.71.0有限完成，2026-10-14）

起点`df5dcf7 / 0.70.1`，最近Render管线调查仅为只读文档，不含新产品或验收。本节冻结合同已落实，**0.71.0有限出口已完成，完整B3／B4仍未完成**；固定产品／工具端点`99189f2`。本批只扩固定MP3 LayerIII／native FLAC带音轨导出，不新增route／schema／项目／jobs／store；当前HTTP仍以[唯一API](../../architecture/backend-api.md)为准，终态另记实施§26／ADR-0216／roadmap§18.194。

### 12.1 新旧profile与兼容门禁

| 范围 | 已决合同 |
|---|---|
| 执行profile | 原`static-export-pcmwav-v1`用于旧PCM16 WAV／纯视频；新增`static-export-compressed-audio-v1`仅用于MP3 LayerIII／native FLAC |
| 音频profile／grant | 复用`pcm16-wave-self-contained`、`mp3-layer3-self-contained`、`native-flac-self-contained`及中央`BackendAudioInputProfiles`的profile→demuxer映射。进程内BackendExportAudioGrant可保持string默认WAV；private wire WorkerExportAudioGrant尾AudioProfile必须nullable／默认null并JsonIgnoreWhenWritingNull，metadata将WAV映为null、compressed显式profile，缺省只解释WAV，保旧private bytes／inputSHA且不新增第二codec。public AudioRef同样null省略，旧WAV继续省略，新压缩必须显式profile |
| 公开能力 | 保原singular ExecutionProfile，尾增ExecutionProfiles（旧／新两项）、三个AudioProfiles、MaxCompressedAudioDurationUs=`300000000`及CompressedAudioDurationBasis=`strict-source-rate-decoded-sample-frames`，明确完整EOF的source-rate实际帧计数 |
| 真实Worker能力 | 核当前EngineIdentity、恰两个SourceProfiles／三个AudioProfiles／两个ExecutionProfiles及实际native可用；缺失／未知／重复／不匹配明确禁用，不让真实旧0.70.1 Worker宣称支持新增能力 |
| 旧资料与恢复 | 原WAV精确admission、旧wire／报告字节及纯视频保持；旧null仅解释WAV，新压缩profile必需且未知拒绝。旧／新artifact恢复按明确allowlist核身份，不泛accept任意profile |

BackendExportInputDto尾增nullable ExecutionProfile=`null`并WhenWritingNull，WAV／纯视频继续省略，compressed显式记录`static-export-compressed-audio-v1`，不改ledger／schema／路由或增加第二metadata。恢复严格接受null／旧marker配audio null或WAV、新marker配显式MP3／FLAC，单字段缺失／错配／unknown拒绝；同时删除marker与profile而协调改写为合法legacy，在不读源音频的恢复合同下仍不可辨识，不宣称签名或穷尽任意篡改保护。

双静态sourceProfile、完整已接受分析与原sourceSHA保持；grant访问路径不是源码AudioPath，不替代原snapshot SHA。纯视频保完整声明／分析，禁止源音频IO，不因压缩输入授予任意描述路径能力。Host复用B4-G固定bytes verifier／classifier，保actual hash／size、父路径no-link、read lease与资源pin所有权及release-before-publish出口。

### 12.2 实际源时长与严格编码

压缩源仍限固定自包含单文件≤16MiB、mono／stereo、正采样率≤192kHz。Worker强格式受限probe后完整strict decode至EOF且实际exit0，按源采样率计每声道sample frames；用64KiB流式读取，只计数，不保存整曲PCM或复制音源。新增压缩源实际sample frames≤rate×300，超过即失败；这是本批显式300秒有限能力，不套旧WAV、通用prepare或48k分析算法，未来扩容须独立交付。

源足够性用整数交叉式`sampleFrames × 1_000_000 >= derivedVideoDurationUs × sampleRate`，少一个sample frame也必须失败；报告精确源时长取`floor(sampleFrames × 1_000_000 / sampleRate)`。不采用probe／header／summary估时长或分析48k计数，不把未知时长写0。原50ms只用于实际输出音轨容器／编码量化容差，不放宽源足够性。

Host私有报告期望按captured grant／registered ref解析的AudioProfile核新compressed源时长≤300秒并拒unknown profile；不新增public report字段或wire，旧WAV报告字节／预算保持且不套300秒。

完整解码及前后actual identity核验完成后才创建Runtime factory／candidate。损坏、短源、预算或OCE不发布partial媒体、不构造静态假Runtime报告；只有真实非空Runtime snapshot才沿原失败报告合同发表。解码、编码、取消和cleanup都保原primary／OCE并等待拥有native与Worker private group实际排水。

普通MP4对新增压缩源固定转码AAC，透明MOV固定PCM16；保source codec事实。Media／Export尾增nullable受信`AudioEncodingPlanOverride`，默认null保原路径／报告的AudioCopied语义；新增managed warning准确说明本次策略，不改旧WAV报告字节。新strict encoder强制profile派生`-f`、file,pipe协议及`-xerror／err_detect explode`，受限路径跳过`-shortest`并保原`-t`上限，不丢计划末帧。

原唯一jobs／scheduler／ledger、精确修订／client回执／ACL、source与grant身份、两层提交、120秒deadline／2秒取消宽限、Linux-x64单媒体64MiB文件guard、报告／store占位、帧计划与几何预算全部沿用。完整源解码也在同一任务deadline内，不新建准备任务或第二份账本。

### 12.3 所有权、提交与统一验证

Media／Export作者负责AudioEncodingPlanOverride、受限计数／编码argv及对应回归；Host作者集中负责Contracts／Hosting／Server合同、grant／admission／恢复与测试；Worker作者负责StaticExportTask／Worker、必要实际Worker接缝测试及薄HTTP工具，不修改Host拥有测试，协调授权除外。文档作者负责本节、晚切版本、唯一API、实施§26／ADR-0216／roadmap§18.194、统计／导航及唯一统一验证。root按合同→共享合同／原Media与Export→Host／Worker接线→薄HTTP→统一终态／归档逐步审阅、提交并推送，不stage未freeze内容。

所有dotnet共用`flock -o /tmp/mkg-backend-b4-dotnet.lock`，正常restore／分析器、`-m:1 /p:UseSharedCompilation=false`；作者新regression按原规则做成功编译变异、真实断言拒绝及源码字节恢复。实际源码freeze后root裁统一scope：正常solution、Hosting全、Worker全、Server linux-x64 self-contained全，以及实际受影响Media／Export定向；新增override须纳必要CommitBoundary定向。等待原session实际exit／原TRX，失败保留并最小修复后重跑原scope，不覆盖失败或另换scope。

### 12.4 最小真正HTTP出口与保留边界

- **真实影片：** 复用原ExportProbe／既有Runtime oracle，CBR MP3、VBR MP3、native FLAC各一普通片，FLAC透明一片及旧WAV一片；覆盖44.1／48k、mono／stereo及有理帧率／精确尾源。核完整实际解码视频帧、像素／Alpha、完整真实音轨；音轨oracle基于同一encoded source bytes的实际解码，PCM可逐字节比较，AAC用有损容差，不拿编码前原PCM声称MP3无损。
- **必要闭环：** 分析profile择一真实apply后导出／纯视频无音频IO；短源（含少一sample frame）、损坏、owned ffconcat及压缩源预算拒绝；一次active strict-decode取消／重试与private groups成员实际gone。真实旧0.70.1 Worker新增能力disabled，旧／新artifact恢复、认证读取、完整终态JSON与登记hash／size一致。
- **终态完整性：** 作者过程、旧557／557及旧HTTP131／936不并本批计数。统一manifest与真正HTTPmanifest分别等原工具实际exit、原TRX及source／部署／证据SHA，核观察native PID／startTicks和Worker private groups成员全退出后归档；进程父PID消失不代替成员排水。

本批有限出口不表示整曲、完整B3／B4、包脚本／完整媒体与字体资源／作者Web已完成；不扩App／Core／GUI、全量、性能、长片、screenshots或人工平台范围，原暂停保持。Render调查文档保其`feda459`历史只读基线，不改写为本次实现／验收证据。

**本批终态（0.71.0有限完成）：** 原修前716／716保持；晚期重开分析采用路径最小Host修复后正常solution零警告错误，新两TRX540／540与未改原四TRX186／186独立。真正HTTP原47134 exit0、385请求3560断言，28whole终态／七媒体报告／六旧新恢复，六影片540完整帧Runtimeoracle与音轨、透明FLAC144144PCMframes exact；纯视频另90帧无音轨、无独视觉oracle。独立三source与原HTTP／media／PCM审查、1568统一source／1308deployment inputs／1444当前deploy／434旧Worker／942evidence SHA已核，三Servers exit0／38private观察成员身份gone。两原失败20／191及94／911独立，详见[实施§26](../history/backend-extraction-progress.md#b4-compressed-export-0710)。

<a id="b4-audio-preparation-worker-plan"></a>
<a id="b4-managed-audio-preparation-plan"></a>

## 13. B4-I：Backend媒体准备Worker／deadline隔离（0.72.0有限出口已完成）

用户已授权继续B4下一部分，并将后端API维护于`docs/architecture`。实施起点为`76b7529 / 0.71.0`；唯一API正文迁移已独立提交推送`c7ca282`，旧路径仅留兼容导航，不算新产品能力。`6a7140d`已提交受管理准备实现，主体稳定后切到0.72.0；**本批有限出口已完成，完整B3／B4仍未完成**。晚期HTTP final1发现open错误wire兼容回归，`1c70fae`最小修复后，repair2统一775／775及严格真正HTTP699请求／4239断言通过。本节保留已落实的冻结合同，完整实际终态、原失败及独立证据归属见[实施§27](../history/backend-extraction-progress.md#b4-managed-audio-preparation-0720)。已实现HTTP只维护于[唯一规范](../../architecture/backend-api.md)，旧B4-H的540／186与HTTP385请求3560断言保持各自历史归属，不并本批计数。

### 13.1 入口与真实能力门禁

| 范围 | 冻结合同 |
|---|---|
| Server组合 | Backend Server的新建、open／recover媒体准备采用`managedWorker`，每次准备独立进入受管理Worker；App保原`AudioWaveformLoader.CreateDefault()`，未配置managed准备的进程内Hosting保Backend自包含`CreateForSelfContainedFiles()`默认能力 |
| 现有HTTP | 新建仍同步返回201，并使用原client级receipt；连接断开不撤销已接受操作。open／recover复用原jobs、ledger和持久化入口，不新增route、job种类、store、schema或项目 |
| 准备profile | 固定`self-contained-native-waveform-v1`，仅原自包含六家族`wav`、`mp3`、`flac`、`aac`、`mov`、`ogg`；保原受限probe／typed decode、MOV外部引用结构门禁，不开放任意描述路径 |
| 公开能力 | 现有`/api/v1/capabilities`尾增`audioPreparation`，公布enabled／disabledReason、profile、精确六families、完整payload／base桶预算、deadline／grace及全局／client running／queued上限；旧字段与已有analysis／export能力分别保持 |
| 真实Worker | 有界实际握手核当前EngineIdentity、精确profile／六families、native工具可用及private-group／subreaper隔离能力。缺失、未知、重复、不匹配或探测失败明确禁用；真实旧0.71.0 Worker不能宣称prepare能力 |
| 禁用行为 | Server不回退Host直接native准备，也不以文件存在、配置值或受控fixture替代真实Worker能力；已接受结果读取不要求重新启动准备Worker |

本批不将通用prepare改成独立分析或导出admission：不套analysis的48kHz重采样、压缩export的300秒、mono／stereo或192kHz上限。六家族与以下预算共同定义有限技术能力，不承诺覆盖全部历史输入或任意整曲。

### 13.2 原媒体结果、完整波形与传输预算

Worker复用原`AudioWaveformLoader`受限准备链。结果保原`AudioStreamInfo`五字段：DurationUs、SampleRate、Channels、CodecName、ContainerName；波形保原source采样率、实际完整解码sampleCount与baseBucketSamples=64。Info中的probe时长与波形实际采样数各保原语义，不把估算值替代实际计数，也不将准备结果写成独立分析canonical。

私有完整结果payload最多4MiB，base层最多500,000桶，包含最后不足64samples的部分桶。传输base层min／max的原float32 bits，不把桶转为伪PCM后重新聚合；Host调用与原Media builder相同的合并函数重建完整pyramid，所有层的桶数与float bits必须与原加载器一致。结果同时核profile、Info、source rate／sampleCount、桶数、完整长度及SHA，拒绝缺失、截断、未知或不一致的结果。

在接受下一decoded block前检查base桶预算，超过500,000桶明确失败`WorkerAudioPreparationBudgetExceeded`；完整payload另受4MiB协议边界，但合法结果先受500000桶约4004160字节上限约束，不把真实HTTP桶预算失败另计为独立payload-budget失败。不截断，不把前缀波形当完整成功结果。完整payload预算独立于原4MiB静态snapshot或analysis结果预算，不因共用数值合并合同。

### 13.3 排队deadline、取消与进程所有权

准备操作从进入准备队列开始计deadline，默认60秒，配置范围1～120秒；取消宽限2秒。全局最多running1／queued4，每client最多running1／queued4。复用既有scheduler的内部许可，不为一次媒体准备创建第二个public job或账本；新建先校验名称、composition、资源ID非空与splitRule等请求基础字段，再在进入`_createGate`前预约许可，保原`invalidRequest`优先级，同时避免全局新建闸门遮蔽准备排队、deadline或quota。

截止时间到达记稳定失败`WorkerAudioPreparationTimedOut`，不冒充用户cancel；用户取消保原OCE／cancel语义。queued和active准备都须响应取消与shutdown。服务停止后拒绝新操作及重新配置，对已接受准备发取消并等待实际排水；每次proxy只有在Worker及拥有的private-group／native成员关闭、终止和排水后才返回结果或异常。父进程退出、丢弃Task、迟到结果或超时返回本身不构成资源释放证据。

Host继续保原固定actual SHA／size、路径no-link、read lease／resource pin、prepare前后实际identity、未发布candidate cleanup及release-before-publish门禁。Worker授权只覆盖固定输入的内部访问grant，不替代源声明身份，不授任意路径能力；失败、预算、deadline、cancel或排水异常均不能发布半成品候选。输入租约、结果内存、旧工程历史／dirty、同句柄重试及旧成功receipt必须各自保原所有权。

### 13.4 错误、旧wire与版本边界

| 结果类别 | 冻结公开合同 |
|---|---|
| Worker不可用或真实能力门禁失败 | `taskUnavailable`／503，明确disabled原因，不回退Host native |
| 准备running／queued配额满额 | 原quota分类／429，拒绝新准备，不驱逐有效receipt或既有任务 |
| 已接受执行失败／预算／deadline／transport | `taskExecutionFailed`及稳定failureCode；新建直接错误，open／recover沿原权威job失败出口 |
| 固定来源实际身份改变 | Worker复核失败为`taskExecutionFailed`／`WorkerAudioPreparationSourceChanged`；Host原`VerifyFixedAudioSource`保原`taskExecutionFailed`且不新增failureCode，均不降为普通输入格式拒绝 |
| create原输入拒绝 | Worker确认InputRejected转sanitized IOException，沿原422 `audioPreparationFailed`及`field:AudioPath`；NoAudioStream仍null并沿原`noAudioStream`，不改成成功空波形 |
| open／recover原持久化输入拒绝 | InputRejected对应原probe／decode异常，保`projectLoadFailed`终态且无failureCode；NoAudioStream仍null，保原`audioPreparationFailed`且无failureCode，不能一律改为projectLoadFailed。仅open／recover的generic fallback不因mapper额外添`TaskExecutionFailed`；其他job种类保原逻辑，typed Worker执行失败仍走稳定failureCode |
| 用户取消 | 保原cancel／OCE语义，deadline使用`WorkerAudioPreparationTimedOut`失败区分 |

`BackendErrorResponse`只尾增nullable `FailureCode=null`并在null时省略，保旧错误wire字节和原code／message／field／protocolVersion语义。实际配置名、capability DTO与private IPC字段已在源码freeze后同步唯一API和独立Worker合同，本计划不维护第二份当前接口列表。

本批MINOR0.72.0保持工程／场景／模板格式28／16／6、配置1、包profile2、作者SDK、固定依赖与程序集划分，不新增项目。完整B3／B4、包脚本／媒体与字体资源、作者Web及原全量／性能／GUI／长片／人工平台暂停范围继续独立。

### 13.5 作者边界、统一验证与有限真正HTTP出口

Host作者负责Hosting／Server、internal permit／client receipt／candidate发布与对应回归；Worker作者负责Contracts／Worker、必要Media完整base桶重建／预算helper及对应回归；验收作者只做薄HTTP编排、原native／private-group身份观察及独立source／HTTP审查，复用现有解析、Runtime oracle和工具，不复制产品业务。文档作者负责本节、晚版本、唯一API、统一冻结验证与实际终态归档；root按合同、实现、HTTP／统一证据逐步审阅、单步提交推送，不stage未freeze内容。

所有dotnet共用`flock -o /tmp/mkg-backend-b4-dotnet.lock`，保正常restore／audit／analyzers，使用`-m:1 /p:UseSharedCompilation=false`。作者回归按原规则补成功编译变异、真实断言拒绝及源码字节恢复。源码freeze后由root裁统一scope：正常solution，Hosting／Worker全，Server linux-x64 self-contained全，及实际受影响Media／Processes／原调用边界定向；不自动恢复原全量或性能。原session实际exit、原TRX计数和输入／部署SHA全部核过后才记完成；失败归档并最小修复后重跑原scope，不覆盖失败或以缩小scope替代出口。

- **实际准备闭环：** 六家族真实固定输入新建、保存、重开／恢复，Info五字段与source rate／sampleCount、完整pyramid各层float bits对照原Media结果；旧WAV及既有analysis／export范围保持，不用受控fixture代替真实native链。
- **必要拒绝与隔离：** owned ffconcat／MOV外部引用、损坏／NoStream、base桶预算拒绝及协议payload边界定向验证；合法结果不宣称真实HTTP独立触发payload预算失败；active和queued的cancel／deadline／quota／shutdown，create断流后原receipt可重读，同句柄可重试；真实旧0.71.0 Worker准备能力disabled，无Host native fallback。
- **进程与提交：** 观察当前与旧部署Worker的实际EngineIdentity；核Worker／FFprobe／FFmpeg及拥有private groups成员的PID／startTicks、实际exit与gone，完整权威终态／receipt及candidate发布身份一致，未发布候选与lease实际释放。
- **证据归属：** 作者scope、统一tests、真正HTTP及旧B4-H证据分别记账；最终source／部署／原响应与证据文件SHA完整核验。观察到的成员排水不宣称穷尽从未观察到的进程，有限prepare能力不扩为完整B3／B4验收。

本节保留编码前冻结的合同与出口判据；下方只记实际终态，完整证据集中于实施§27，当前接口维护于唯一API。

**本批终态（0.72.0有限完成）：** repair2原八commands exit0、solution零警告错误、七TRX775／775；严格真正HTTP原session38819 exit0、699请求／4239断言，58whole终态／六actual-marker恢复／七controls通过；四Servers exit0／172观察private成员（52groups）gone，publish21与握手13另计。1602统一source／1342HTTP输入／1448部署文件／1586HTTP证据SHA已核；原753、final1失败137／987及定向修复／变异独立，完整证据与保留边界见[实施§27](../history/backend-extraction-progress.md#b4-managed-audio-preparation-0720)。
