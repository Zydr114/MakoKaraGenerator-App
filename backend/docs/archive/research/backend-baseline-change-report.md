# 后端拆分相对基线的变更报告

统计更新：2026-10-14，分支 `refactor/individual-backend`。§1～§5的历史统计终点固定为 `bc6b26d / 0.63.0`；该截点之后的跨进程Worker修改、未跟踪截图及报告本身不计入历史统计。历史0.64.0已提交代码与有限验收另见§6；后续0.65／0.66增量导航见§7，0.67独立音频分析有限出口见§8，不把历史统计当作当前版本统计。固定0.67最新统计见§9；0.68当前已提交施工端点与增量见§10；含0.68归档提交的详细行数归属另见§11；0.69固定端点统计见§12，0.70固定端点统计见§13，0.70.1固定端点统计见§14，当前0.71.0固定端点统计见§15。

本段及§1～§5保留原0.63.0历史截点，统计不随工作区改动自动更新；历史0.64.0增量、实际出口与固定codeEndpoint另见§6，后续能力见§7／§8。

历史截点的变化是：把既有编辑规则移到可复用的 Application 用例，增加可独立启动的本机 HTTP 服务及生命周期、持久化基础，再建立预览／Worker 技术接缝。原有歌词渲染、脚本 Runtime 和导出功能在拆分前已存在；0.63.0当时还没有可通过HTTP请求并看到真实帧的完整预览链路，也没有完整Web编辑器。

## 1. 比较基线

| 节点 | 提交 | 含义 |
|---|---|---|
| 原产品源码 | `f7940d4 / 0.49.14` | 本次拆分之前的 Avalonia 产品基线 |
| 原方案文档 | `16b05ae` | 与上行的 `src/`、`samples/`、`tests/`、`tools/`及构建入口源码相同，只加入调研文档 |
| B1 出口 | `3fdcdce / 0.56.0` | 七个进程内共享编辑切片收口 |
| B2 出口 | `1ed127d / 0.60.0` | 无 GUI 新建、本机服务、有限编辑与独立持久化闭环 |
| 本报告终点 | `bc6b26d / 0.63.0` | B3-C 同进程 Runtime 帧适配及受保护读取路由 |

以上节点依次为祖先关系，已用 `git merge-base --is-ancestor` 核实。原源码到终点共26个提交：B1区间9个（含2个调研／方案文档提交），B2区间4个，B3区间13个。版本从0.49.14升到0.63.0；工程／场景／模板格式28／16／6、配置1、profile2和固定依赖未因本次拆分升级。

## 2. 做了哪些变更，哪些才是新增能力

| 范围 | 相对原基线的变化 | 可用边界 |
|---|---|---|
| B1：共享编辑 | 时间组、拖动／微调／清除、正文／注音与行操作、导入／工程设置、来源管理、播放打点、平移／重排／拆合／重切进入 `Application/Editing`；增加稳定目标、原值与工作区代次校验、变化摘要及手势历史身份 | 编辑语义大多原已存在；新增的是独立于 UI 的公开用例及拒绝过期操作的合同。Avalonia直接调用，输入草稿、选择、焦点、声卡和 UI 生命周期仍在 App |
| B1：App 接线与修复 | 从 VM／视图移走部分规则，以结果更新歌词／时间块；修复若干旧异步结果、历史合并与选择更新边界 | 属于重构及行为修复，不能据此宣称 UI 全面重写或全部业务已迁出 |
| B2：无 GUI 准备 | `NewProjectPreparationService`固定歌词输入、准备音轨／波形并组装候选工程 | 原来新建需经过桌面入口；新增无 GUI 的调用方式，声卡／字体发布仍属于桌面适配层 |
| B2：独立服务 | 新增 Contracts、Hosting、Server三个项目；本机HTTP上传、创建、摘要／歌词／历史查询、命令回执、单写者修订、客户端登记／心跳／恢复凭证、Host／Origin约束、原子state及SSE恢复 | 这是相对原基线新增的独立服务能力。当前HTTP编辑命令仅为 `lyrics.setTimeRange`、`history.undo`、`history.redo`；正文、来源、设置等B1用例尚未普遍开放HTTP |
| B2：资源与持久化 | 固定上传资源的哈希／MIME／大小／配额，资源Range读取及租约；目录句柄／写租约；打开、保存、关闭、恢复任务与账本 | 原来桌面已有保存／重开；新增服务所有的会话、任务与受控资源链。App和Server各自持有会话，不能混用同一个裸工作区 |
| B3：预览接缝 | 中立DTO、Hosting协调器、显式来源修订／输入代次、单执行请求＋最新待处理槽、帧完整性验证；原App协调器回调改为锁外发布 | 新增服务侧预览基础，App仍用原协调器，两者尚未统一 |
| B3：Worker | 新增Worker项目、受管理子进程、长度前缀JSON、请求ID／哈希校验、取消／关闭／超时回收与取消停止时的进程树清理 | 固定终点的Worker只回显opaque输入，不执行真实Runtime渲染。工作区正在开发的真实渲染不计为已交付 |
| B3：真实Runtime适配 | `RenderFrameV1SessionFactory`包装既有渲染会话，限制尺寸／像素预算、固定快照并校验输出，串行渲染／释放 | 已证明真实静态无包工程的同进程连接。原Runtime渲染器不是本次新增；`Runtime.Hosting`、`Rendering.Skia`、`Export`生产目录相对原基线没有改动 |
| B3：帧／诊断读取 | 新增客户端隔离的帧句柄／诊断页存储，以及PNG内容GET／HEAD、Range／ETag／TTL和诊断游标读取 | Server只注册存储和读取路由，尚无预览会话创建／请求入口、像素到PNG发布方或诊断生产方；不能把读取路由等同完整预览 |

原来已存在的工程渲染、脚本编译／有状态时间线、预览、透明输出及视频导出，应描述为后续复用的现有能力。B4导出／验证／分析服务任务、B5作者闭环和Web客户端尚未完成。详细逐批证据见[实施记录](../history/backend-extraction-progress.md)，HTTP目标与实际子集见[API草案](../plans/backend-api-v1-draft.md)。

## 3. 代码量变化

以下为提交间 `git diff --numstat` 的净差异，格式是“改变文件数；新增／删除行”。生产分类包含 `src/`、`samples/`下的代码及项目资源，测试为 `tests/`，文档包含 `docs/`与 `AGENTS.md`，验证工具为 `tools/`，构建入口为根目录构建文件。同一文件在多个阶段变化时会重复计入阶段行，故阶段文件数不能直接相加为总数。

| 区间 | 生产 | 测试 | 文档 | 验证工具 | 构建入口 |
|---|---:|---:|---:|---:|---:|
| 原基线→B1 | 83；+4460／−1289 | 26；+6101／−6 | 8；+1534／−1 | 10；+1319／−0 | 1；+1／−1 |
| B1→B2 | 40；+3987／−32 | 9；+1944／−1 | 7；+340／−13 | 5；+1308／−4 | 2；+5／−1 |
| B2→终点 | 17；+1519／−4 | 10；+774／−0 | 12；+214／−5 | 0；+0／−0 | 2；+2／−1 |
| **原基线→终点** | **130；+9962／−1321** | **44；+8819／−7** | **14；+2070／−1** | **13；+2627／−4** | **2；+6／−1** |

合计203个文件（新增143、修改60、删除0），新增23484行、删除1334行，净增22150行。测试和验证工具占新增行数约48.7%；这不能解释为功能或复杂度增加48.7%。大量生产增量来自稳定编辑合同和独立服务基础，旧App虽然删去部分规则，但仍保留交互适配职责。

## 4. 当前代码卫生及接线待办

以下为固定终点的源码证据，不代表已修复，也不把静态风险写成已测量的运行泄漏。

1. **两个预览协调器仍并存。** [App协调器](../../../samples/TechnicalSpike.App/Rendering/PreviewFrameCoordinator.cs)与[Hosting协调器](../../../src/MakoKaraGenerator.Backend.Hosting/BackendPreviewCoordinator.cs)各自维护队列、会话与旧输入判断；[KaraokeCanvas](../../../samples/TechnicalSpike.App/Controls/KaraokeCanvas.Async.cs)第31～40行仍创建App版本。Hosting版在生产Server中尚未实例化。后续应统一共享调度规则，保留桌面显示适配，避免两份规则继续分叉。
2. **B3生产链尚未接线。** [Server Program](../../../src/MakoKaraGenerator.Backend.Server/Program.cs)第60～61行只注册帧／诊断存储；[端点](../../../src/MakoKaraGenerator.Backend.Server/ServerEndpoints.cs)第84～86行只映射读取。生产Server／Hosting中没有调用这些存储的 `Publish`，也没有将协调器、Runtime工厂和Worker装配成会话服务；现有发布调用来自测试。需补会话所有者、渲染任务、PNG编码／发布、事件及关闭释放链，不能用404探针替代真实成功预览验收。
3. **“有效PNG”验证目前仅检查签名。** [帧存储](../../../src/MakoKaraGenerator.Backend.Hosting/PreviewFrameHandleStore.cs)第46～52行验证合同、哈希与8字节签名，未解析PNG结构／尺寸或实际解码。[测试](../../../tests/MakoKaraGenerator.Backend.Hosting.Tests/PreviewHandleStoreTests.cs)第10行使用“PNG签名＋4个任意字节”，第20行即接受；它没有IHDR／IDAT／IEND，不能作为真实PNG证据。待补有效PNG夹具及结构／尺寸／解码验证；SHA-256只能证明字节一致，不能证明图像有效。
4. **已释放协调器仍可能保留对象图。** [Hosting协调器](../../../src/MakoKaraGenerator.Backend.Hosting/BackendPreviewCoordinator.cs)第43～57行的帧持有完整请求，请求含工厂／ProjectSnapshot；第284～299行释放时没有清空 `_latest`、`_sessionRequest`、`_session`、`_runner`或事件订阅。只要消费者继续持有旧帧或已释放协调器，快照／工厂也会继续可达。待明确帧所有权、释放时清引用和订阅出口；目前未做堆测量，不能声称已发生持续内存泄漏。
5. **诊断存储的“有界”仅限单会话页数。** [诊断存储](../../../src/MakoKaraGenerator.Backend.Hosting/PreviewDiagnosticsStore.cs)第20、40、46行对会话字典没有总数量／字节／TTL或移除入口，每个会话最多32页；第44行直接存入调用方页面，`Diagnostics`是可变数组，发布者随后修改数组会影响存储内容。读取时复制数组不能消除发布端别名。待补发布快照和会话／客户端关闭清理及总配额，再接真实长期生产链。

## 5. 验证口径

本报告执行了Git祖先、日志、提交树差异与生产调用点核对，未重新运行构建、测试、性能或用户窗口。已记录的最近有限出口为：0.63.0 solution零警告／错误，Backend.Hosting105／105、Worker IPC4／4、真实Runtime静态帧1／1、Avalonia预览20／20；Server实跑只证明未知帧／诊断错误路由及排水退出。这些历史计数见[实施记录§17](../history/backend-extraction-progress.md#b3-runtime-frame-0630)，不能替代HTTP真实成功预览或完整跨进程渲染验收。

B1的变化摘要与局部刷新为减少重复工作的实现基础，但本次拆分没有同夹具端到端延迟／内存对照；不能推定卡顿已解决、吞吐提升或Web一定更快。全量、性能、透明像素等价、持续播放、正式导出／长片、物理输入和平台人工验收仍各保留原有边界。

## 6. 0.64.0静态工程Worker与HTTP增量

本节对应已完成的0.64.0 B3-D有限出口，原始比较基线仍为 `f7940d4 / 0.49.14`（产品）及 `16b05ae`（方案文档）。当前已提交代码统计终点为 `d640807c8ec5597f0b472ded598b855af69d8897`（下称codeEndpoint）；§1～§5的0.63统计、提交数和风险记录保持历史事实，不改写成0.64统计。codeEndpoint包含此前已提交的历史文档，随后本次验收文档提交不在以下计数内。

| 原产品基线 `f7940d4` → codeEndpoint | 改变文件 | 新增行 | 删除行 |
|---|---:|---:|---:|
| 生产（`src/`＋`samples/`） | 152 | 13,005 | 1,323 |
| 测试（`tests/`） | 59 | 11,654 | 18 |
| 验证工具（`tools/`） | 16 | 3,184 | 4 |
| 文档（`docs/`＋`AGENTS.md`） | 21 | 2,423 | 1 |
| 构建入口 | 2 | 11 | 1 |
| **合计** | **250** | **30,277** | **1,347** |

以上为同一基线到固定codeEndpoint的Git净差异，共新增187／修改63／删除0个文件，净增28,930行；不是把历史阶段文件数相加，也不把行数增长解释为性能或功能增长。格式28／16／6、配置1、profile2、作者SDK和固定依赖保持，产品版本现为0.64.0。

本次增加的是独立服务的静态工程预览链：专用 `snapshot-v1 / static-lyrics-scene-v1` 输入、受管理Worker消费既有正式Runtime、服务侧预览所有者及PNG发布适配。快照保留实际歌词、多来源、注音、静态样式、完整场景和整数时间；音频路径及固定身份作为声明保留，codec不读取它们、不授予文件能力，Hosting另持资源租约。效果包、固定预设、包材质、已接受分析以及图片／视频背景目前明确拒绝，不把不支持的输入悄悄降级成纯色。

PNG适配是新增的 `Backend.Rendering.Skia` 项目；Hosting继续只依赖中立编码接口，Contracts仍不引用Core或GUI。`Runtime.Hosting`、既有 `Rendering.Skia` 和 `Export` 的业务仍大体沿用；`Rendering.Abstractions/RuntimeDiagnosticSnapshot` 的新增工厂用于跨进程诊断还原，属于传输边界，不是渲染引擎重写。新增裸帧与PNG字节合同、来源代次／请求序号、租约和HTTP查询不等于已证明性能提升。

本次已接通§4第2项的静态工程生产链，并补真实PNG结构／尺寸／CRC及预算检查、释放时协调器清引用／订阅、诊断发布快照／会话总配额／关闭清理，以及帧TTL。首次Create／open和后续帧失败都保留中立稳定code，有真实Runtime报告时保留实际发生身份，没有时明确transport诊断。两个预览协调器仍并存：Avalonia使用其原适配，Server已实例化Hosting协调器，尚未统一两份调度规则；这些有限修复不等于堆测量或完整长期负载验收。

最终solution构建0警告／错误，Hosting221、Runtime依赖＋真实Worker11、Avalonia预览20、codec40、PNG9、self-contained Server14合计315/315通过，0失败／跳过／中止。最终同一发布体的真实HTTP112请求、458工作流断言＋247凭证产物扫描共705项，三帧各57,600像素与独立真实Runtime的RGB／alpha误差0。SSE、seek、回执、跨client读取、HEAD／Range／ETag／TTL及关闭／休眠资源释放已实际覆盖；Worker受控终止和自有launcher删除分别为WorkerSessionConflict／WorkerUnavailable，两者均为transportFailure无作者occurrence。成功静态诊断为空，不把它当作脚本真实发生。Server退出0、四个观察到的Worker全部退出。

当前不足仍是完整包／脚本／预设／分析与图片视频闭包、字体能力门禁、完整有状态脚本Worker会话和真实失控脚本恢复、完整诊断生产及几何命中。B4服务导出／验证／分析、B5作者闭环、完整Web编辑器和原全量／性能／原生输入／平台人工范围继续独立。**B3-D静态有限出口完成，完整B3未完成。** 完整工程接续见[完成计划](../plans/backend-b3-completion-plan.md)，当前精确能力、接口、六个final TRX与HTTP final3 manifest及SHA集中到[实施记录§18](../history/backend-extraction-progress.md#b3-static-preview-0640)；不混入0.63历史、prefinal217或HTTP final／final2计数。


## 7. 后续0.65正式验证与0.66有限静态导出增量

§1～§6保留各自固定历史端点与numstat，本节不重算全基线文件／行数。0.65 B4-A新增固定静态正式任务接线：原RuntimeFrameValidator进入独立Worker，唯一jobs／ledger、实际进度、取消／deadline、有界完整报告及profile恢复读取，有限终态332／332和真正HTTP389请求，详见[实施§19](../history/backend-extraction-progress.md#b4-static-validation-0650)。报告验收不是影片证明。

0.66 B4-B在原Runtime／VideoExporter／FFmpeg业务上增加独立静态导出／认证下载、固定PCM16 WAV授权、共享正式任务调度器、容量预留、不可变媒体＋报告目录包及report-only实际失败诊断；新增BCL Processes支持private group／FSIZE／有界输出，修复Exporter早期primary、受限WAV透明NTSC末帧及legacy subreaper／控制写入回收边界。产品端点`2a29329`已推，统一7组8TRX合计436／436；真实HTTP764请求／5682断言，普通／透明／有理fps影片各90完整帧及音轨／内容oracle通过，stereo离线29附证另列，最终SHA已核验；有限出口见[实施§20](../history/backend-extraction-progress.md#b4-static-export-0660)。当前已实现接口只维护于[统一API](../../backend-api.md)。

这些新增的是独立执行、授权、资源归属、发布和失败合同，不是渲染引擎重写或已证明性能提升。完整B3／B4、包脚本／其他媒体／分析HTTP／Web和原全量／性能／GUI／长片／平台人工范围保持未完成；本节不把0.64的PNG、0.65的报告或受控执行器结果倒记为0.66真实影片证据。


## 8. 0.67独立离线音频分析增量（有限出口已完成）

本节不重算§1～§6固定历史numstat。0.67 B4-C新增的是固定授权音频经独立Worker进入原AudioFeatureAnalyzer、完整canonical结果和实际采样进度、共享正式任务调度／账本、容量预留／取消／deadline及认证读取／profile恢复。没有改原算法、整曲归一化或Core规范数值，也没有自动应用工程或扩展static渲染profile；来源profile／semantic Revision／完整ContentHash与wire输入SHA分别保真。共用sidecar IO保持旧validation合同，修复Prepare写入叠删除失败遮primary与残留tmp漏计费，采用源码定位加当前实际文件故障回归，不称旧binary跑红。

产品端点be56180，正常solution0警告／错误，显式五TRX合计460／460。真正HTTP原9085退出0，789请求／5943断言，六音频完整canonical与独立原Analyzer逐字节一致；source／部署／证据总SHA已核验，两Serverexit0／43观察group身份全gone。产品端点be56180／工具端点fdce074；默认真实解码与第二Server-re进程控制证据分列，最长接受6.513秒，未实际解码300秒或等待1h TTL。有限完成的权威记录见[实施§21](../history/backend-extraction-progress.md#b4-audio-analysis-0670)／ADR-0211／roadmap§18.189；精确路由只维护于[统一API](../../backend-api.md)。该音频证明不替代0.66影片／0.65报告或新的渲染／脚本验收；apply、其他音频、完整B3／B4、作者／Web及原暂停范围继续独立。


## 9. 当前0.67已提交端点的精确统计与能力（2026-10-14）

本节重新读取Git固定端点，不沿用§1～§6历史数字。终点为`1cb42ba6d4331868eb2ca049cace2d9e9a46d733 / 0.67.0`，最近B4-C起点为`1df53c10d757798d064b5eb8b86b3b0d466cb5e9 / 0.66.0`，累计起点为原产品`f7940d4956a71bc989c0ca60ccff70eecdcb8efe / 0.49.14`。工作区未跟踪screenshots及本节／后续B4-D改动不计入。

| 分类 | B4-C文件数 | B4-C新增／删除 | 累计文件数 | 累计新增／删除 |
|---|---:|---:|---:|---:|
| 生产（src／samples） | 25 | +1,247／−147 | 208 | +17,632／−1,431 |
| 测试（tests） | 10 | +804／−3 | 91 | +14,919／−19 |
| 验证工具（tools） | 4 | +623／−0 | 28 | +5,278／−4 |
| 文档（docs／AGENTS.md／README.md） | 11 | +264／−29 | 29 | +3,724／−1 |
| 根构建入口 | 2 | +4／−4 | 2 | +14／−1 |
| **合计** | **52** | **+2,942／−183** | **358** | **+41,567／−1,456** |

B4-C共7提交，新增28／修改24／删除0个文件，净增2,759行；累计共65提交，新增282／修改76／删除0个文件，净增40,111行。累计阶段提交分别为B1 9、B2 4、B3静态预览26、B4-A 8、B4-B 11、B4-C 7，包含方案和验收文档提交，不称65个纯产品功能提交。阶段文件数不能相加为累计不同文件数。本节将README归文档；若沿§3旧docs＋AGENTS严格口径，累计文档为28文件／+3,720／−1，README另有+4／−0，合计不变。测试＋工具占累计新增行约48.59%，行数不能解释为功能增幅或性能改善。

可重算命令：`git rev-list --count <start>..1cb42ba`、`git diff --numstat <start> 1cb42ba`及`git diff --name-status <start> 1cb42ba`；分类按表中路径互斥归属。源码`ServerEndpoints*.cs`显式Map调用为**52个路由模板＝47个v1规范模板＋5个兼容别名，58个显式方法映射**；GET／HEAD各算一个方法，不计全局OPTIONS中间件，模板数不是功能数。

| 当前已交付范围 | 具体能力与边界 |
|---|---|
| 共享Application编辑 | 时间组／拖动微调清除打点／行平移、正文注音与行操作、来源管理、导入与设置、元素重排拆合重切。Avalonia直接调用；UI草稿／选择／声卡／字体发布仍在App |
| 本机HTTP编辑 | 单Writer／精确修订／client单层回执、一致摘要／歌词／历史；具名编辑仅`lyrics.setTimeRange`、`history.undo`、`history.redo`，不能把全部共享用例写成HTTP已开放 |
| 客户端／资源／持久化 | bootstrap／access／resume、心跳租约、Host／Origin、SSE恢复；固定上传hash／MIME／配额／Range租约；无GUI新建、目录授权、open／save／close／recover job及唯一账本 |
| 静态Worker预览 | snapshot-v1／static-lyrics-scene-v1、真实Runtime→PNG与认证帧／诊断读取；0.64有限出口315测试、112请求／705断言、三帧像素零差 |
| 正式验证／静态导出 | 0.65固定帧计划有限出口332测试、389请求／2,152断言；0.66普通MP4／透明MOV、固定PCM16 WAV或纯视频、不可变认证下载，436测试、764请求／5,682断言，三片各90完整解码帧 |
| 独立音频分析 | 0.67固定PCM16 WAV→原Analyzer、完整canonical及认证读取／profile恢复，460测试、789请求／5,943断言、六输入byteexact；不apply、不改工程修订／dirty／undo、不扩静态profile |

各测试／HTTP数字只属于对应历史有限出口，不能相加成同一套当前全量。当前非空已接受分析、包／脚本／预设、图片视频等静态渲染输入明确拒绝；完整资源／字体闭包、有状态失控脚本隔离／诊断／恢复、完整B3／B4、作者／Web、波形／字体HTTP及全量／性能／GUI／长片／人工平台尚未验收。接续B4-D采用／清除仅为[计划§8](../plans/backend-b4-plan.md#b4-analysis-apply-plan)，不计入本节0.67已实现能力或Git统计。


<a id="current-statistics-0680"></a>
## 10. 0.68显式采用／清除的当前已提交统计（有限出口已完成）

本节固定当前产品端点`6264a94`／工具与统计端点`f4787bd`，完整Git统计截至后者；包含先提交的计划／§9文档，不包含随后本次归档文档及未跟踪screenshots。§9固定0.67端点不改写。以下为`git diff --numstat`净差异，README仍归文档：

| 分类 | 本批1cb42ba→f4787bd文件 | 新增／删除 | 累计f7940d4→f4787bd文件 | 新增／删除 |
|---|---:|---:|---:|---:|
| 生产（src／samples） | 11 | +370／−19 | 212 | +17,983／−1,431 |
| 测试（tests） | 3 | +707／−0 | 94 | +15,626／−19 |
| 工具（tools） | 2 | +575／−0 | 30 | +5,853／−4 |
| 文档（docs／AGENTS／README） | 2 | +90／−0 | 29 | +3,814／−1 |
| 根构建入口 | 1 | +1／−1 | 2 | +14／−1 |
| **合计** | **19** | **+1,743／−20** | **367** | **+43,290／−1,456** |

本批8提交，新增9／修改10／删除0文件，净增1,723行；累计73提交，新增291／修改76／删除0文件，净增41,834行。每行分类互斥，阶段文件数不与历史相加；源码新增行不等于新业务量或性能改善。路由仍52模板＝47v1＋5别名／58显式方法，新增的是原commands内apply／clear两个具名命令，总计五命令；其他B1编辑用例仍不能据此宣称已有HTTP入口。

新增共享Application稳定采用／清除用例、同源成功结果授权／完整性、一次独立历史／no-op保redo、有界summary与HTTP新建AudioMedia接缝补强；原分析器、渲染器及静态profile未扩大。正常solution0警告／错误、四TRX977／977已终态，当前产品能力为独立结果后**显式**写工程、保存／重开完整分析；真正HTTP367请求／2522断言已终态通过，同源显式采用／保存重开及clear后57,600像素PNG零差、正式普通1＋透明1帧、纯视频影片完整30帧恢复，两个Server退出0／本轮16观察group身份gone。三处工具修正属同一probe.py输入漂移，统一977原manifest保持、最终工具另补freeze。当前有限出口完成，原Analyzer／渲染器及静态profile仍不扩大。最终977／原HTTP21974与source／部署／证据SHA见[实施§22](../history/backend-extraction-progress.md#b4-analysis-apply-0680)及统一API；不把行数变化换算为性能或功能增长。完整B3／B4、analysis-aware渲染、其他媒体、完整资源／字体／作者／Web和原暂停验证范围仍独立。


<a id="line-attribution-0680"></a>
## 11. 四万行增量具体位于哪里（0.68归档端点，2026-10-14）

重新读取`git diff --numstat f7940d4 e0d3f3b`，终点固定为`e0d3f3bf8e651ddcdef0fc4e71fb3ba9e2796812`，包含0.68归档文档提交，不包含本节及后续B4-E。§9／§10固定历史端点保持不变。此范围74提交、367个不同文件；新增43,422／删除1,456，净增41,966行。所有比例均以43,422新增行为分母，**新增不等于净增，源码行数不等于交付功能量或运行成本**；生产分类也含项目文件／资源，不能将全部新增称为shipping代码。

| 分类 | 文件 | 新增 | 删除 | 净增 | 占全部新增 |
|---|---:|---:|---:|---:|---:|
| 生产 | 212 | 17,983 | 1,431 | 16,552 | 41.41% |
| 测试 | 94 | 15,626 | 19 | 15,607 | 35.99% |
| 工具 | 30 | 5,853 | 4 | 5,849 | 13.48% |
| 文档 | 29 | 3,946 | 1 | 3,945 | 9.09% |
| 构建 | 2 | 14 | 1 | 13 | 0.03% |

测试＋验证工具共新增21,479行，占49.47%；再加文档为58.55%。生产净增16,552行，比“净增四万行全部进入产品运行”小。六个新Backend项目合计新增12,392行，占生产新增68.91%／全部新增28.54%；主要是独立宿主、协议和进程基础。

### 11.1 生产：按项目及桌面App归属

| 项目 | 文件 | 新增／删除 | 净增 | 占全部新增 | 主要作用 |
|---|---:|---:|---:|---:|---|
| `src/MakoKaraGenerator.Backend.Hosting` | 52 | +7,300／−0 | 7,300 | 16.81% | client／工程单Writer与回执、资源目录租约、持久化／任务账本、预览／正式任务调度、结果存储及Worker代理 |
| `src/MakoKaraGenerator.Application` | 41 | +3,071／−18 | 3,053 | 7.07% | 既有编辑规则迁出；新增稳定目标／基线、手势历史身份、changeset、分析采用／清除共享入口 |
| `src/MakoKaraGenerator.Backend.Contracts` | 19 | +2,133／−0 | 2,133 | 4.91% | 中立BCL DTO、精确身份／时间／状态与strict传输codec |
| `samples/TechnicalSpike.App` | 51 | +1,806／−1,288 | 518 | 4.16% | 改为共享用例调用；保留输入草稿／选择／焦点、异步失效及局部刷新适配 |
| `src/MakoKaraGenerator.Backend.Server` | 16 | +1,345／−0 | 1,345 | 3.10% | loopback认证／Host／Origin、HTTP／SSE、条件二进制读取及组合根接线 |
| `src/MakoKaraGenerator.Backend.Worker` | 6 | +1,066／−0 | 1,066 | 2.45% | 有界IPC、复用原Runtime／验证器／Exporter／Analyzer及取消排水 |
| `src/MakoKaraGenerator.Backend.Rendering` | 4 | +467／−0 | 467 | 1.08% | 静态snapshot codec及输入门禁，不是新渲染引擎 |
| `src/MakoKaraGenerator.Processes` | 5 | +339／−0 | 339 | 0.78% | 拥有进程组、有界文本管道、FSIZE及生命周期 |
| `src/MakoKaraGenerator.Media` | 4 | +116／−68 | 48 | 0.27% | 受管／强WAV／有界输出接缝，原Analyzer不重写 |
| `src/MakoKaraGenerator.Runtime.Hosting` | 2 | +82／−6 | 76 | 0.19% | 验证器主因／cleanup、报告预算等窄修 |
| `src/MakoKaraGenerator.Backend.Rendering.Skia` | 2 | +81／−0 | 81 | 0.19% | 中立像素到PNG适配 |
| `src/MakoKaraGenerator.Core` | 2 | +62／−15 | 47 | 0.14% | 共享整数帧计划、歌词重切帮助函数 |
| `src/MakoKaraGenerator.Rendering.Abstractions` | 2 | +57／−0 | 57 | 0.13% | 跨进程诊断快照还原合同 |
| `src/MakoKaraGenerator.Export` | 5 | +55／−34 | 21 | 0.13% | 主因／提交／受限音频策略窄修 |
| `src/MakoKaraGenerator.Rendering.Skia` | 1 | +3／−2 | 1 | 0.01% | 仅项目引用调整；现有渲染实现.cs没有此范围差异 |

**App没有净删。** 它删除1,288行、同时新增1,806行，净增518；不能把1,288原规则删除写成App净减1,288。Application新增3,071／删除18／净增3,053。B1既有编辑规则迁入Application，同时增加稳定请求／结果／拒绝合同及UI适配，属于迁移加补强；B2／B3／B4更多是在既有内核外新增独立服务能力。旧Runtime、Skia渲染器、Exporter和Analyzer继续复用，不是把整套引擎再写一遍。两个预览协调器等已知并存边界仍在原报告里，行数不能证明所有重复或维护成本已消除。

### 11.2 测试：按项目归属

| 测试项目 | 文件 | 新增／删除 | 净增 | 占全部新增 |
|---|---:|---:|---:|---:|
| `tests/MakoKaraGenerator.Backend.Hosting.Tests` | 33 | +6,009／−0 | 6,009 | 13.84% |
| `tests/MakoKaraGenerator.Application.Tests` | 17 | +4,354／−0 | 4,354 | 10.03% |
| `tests/MakoKaraGenerator.Spike.App.Tests` | 11 | +2,298／−7 | 2,291 | 5.29% |
| `tests/MakoKaraGenerator.Backend.Server.Tests` | 4 | +861／−0 | 861 | 1.98% |
| `tests/MakoKaraGenerator.Runtime.Hosting.Tests` | 7 | +505／−12 | 493 | 1.16% |
| `tests/MakoKaraGenerator.Backend.Rendering.Tests` | 4 | +455／−0 | 455 | 1.05% |
| `tests/MakoKaraGenerator.Export.Tests` | 5 | +373／−0 | 373 | 0.86% |
| `tests/MakoKaraGenerator.Backend.Worker.Tests` | 3 | +213／−0 | 213 | 0.49% |
| `tests/MakoKaraGenerator.Processes.Tests` | 4 | +205／−0 | 205 | 0.47% |
| `tests/MakoKaraGenerator.Media.Tests` | 2 | +176／−0 | 176 | 0.41% |
| `tests/MakoKaraGenerator.Backend.Rendering.Skia.Tests` | 2 | +106／−0 | 106 | 0.24% |
| `tests/MakoKaraGenerator.Core.Tests` | 2 | +71／−0 | 71 | 0.16% |

最大三项为Hosting6,009行（认证／租约／任务／Worker／结果失败与恢复）、Application4,354行（共享编辑规则、稳定基线／历史／一次通知）及App2,298行（桌面调用与旧异步／选择／历史边界）。它们是验证代码，不计作终端用户的生产能力实现。

### 11.3 工具与文档

| 验证工具目录 | 文件 | 新增／删除 | 净增 | 占全部新增 |
|---|---:|---:|---:|---:|
| `tools/ui-suki-compatibility` | 11 | +1,615／−4 | 1,611 | 3.72% |
| `tools/backend-http-probe` | 2 | +1,012／−0 | 1,012 | 2.33% |
| `tools/backend-export-http-probe` | 5 | +831／−0 | 831 | 1.91% |
| `tools/backend-validation-http-probe` | 3 | +640／−0 | 640 | 1.47% |
| `tools/backend-analysis-http-probe` | 4 | +623／−0 | 623 | 1.43% |
| `tools/backend-analysis-apply-http-probe` | 2 | +575／−0 | 575 | 1.32% |
| `tools/backend-preview-http-probe` | 3 | +557／−0 | 557 | 1.28% |

UI兼容工具新增1,615行，用于迁出规则后桌面／Headless入口及交互回归；六组HTTP probe新增合计4,238行，覆盖生命周期／静态预览／正式验证／导出／分析／显式采用的真实Server＋Worker、oracle、凭证扫描和证据归档，不是Web产品。文档（docs＋AGENTS＋README）新增3,946／删除1／净增3,945，属于方案、唯一API、ADR、roadmap与验收证据；根构建入口新增14／删除1。

详细可重算分类记录见`artifacts/backend-extraction/b4-e-planning/baseline-line-attribution.json`。本节只做Git与当前源码归属分析，没有重跑构建／测试、性能或用户窗口，也不将后续0.69能力或代码量提前计入。


<a id="current-statistics-0690"></a>
## 12. 0.69完整分析静态profile的固定当前统计（有限出口已完成）

产品端点`57271451c44e5273d17a85b86e341bcd3c1c0c51`／工具统计端点`4378b60148ef4ee6e4caa83700297048cbf2b2b4`。本批起点为已归档0.68 `e0d3f3b`；累计起点仍原产品f7940d4。包含先提交计划／§11统计，不含随后本次归档文档、未跟踪screenshots或生成artifact；§9～§11各历史点保持。Git diff --numstat按README归文档重算：

| 分类 | 本批文件 | 本批新增／删除 | 累计文件 | 累计新增／删除 |
|---|---:|---:|---:|---:|
| 生产（src／samples） | 20 | +314／−86 | 215 | +18,211／−1,431 |
| 测试（tests） | 9 | +634／−10 | 98 | +16,250／−19 |
| 工具（tools） | 5 | +330／−7 | 32 | +6,176／−4 |
| 文档（docs／AGENTS／README） | 2 | +134／−1 | 29 | +4,079／−1 |
| 根构建入口 | 1 | +1／−1 | 2 | +14／−1 |
| **合计** | **37** | **+1,413／−105** | **376** | **+44,730／−1,456** |

本批6提交，新增9／修改28／删除0文件，净增1,308行；累计80提交，新增300／修改76／删除0文件，净增43,274行。生产累计净增16,780；测试与工具累计新增22,426，占全部新增50.14%，并非4万余行全部进入产品运行。新项目**0**，仅三个既有csproj依赖调整；新薄HTTP probe复用原helpers和既有RuntimeOracle，不复制完整探针底座。未改App／Application生产代码，App相对原基线仍净增518，不将规则迁移写成App净删。

本批新增完整分析静态profile及纯校验codec、总wire预算闭口、Host双profile及恢复allowlist、真实export engine能力门禁、纯视频保声明禁音频IO与可信grant重绑。旧snapshot wire／原算法／Core schema／SDK／视觉语义保持；静态无脚本画面不因分析变化，不等于音频驱动效果。统一正常solution零警告错误、六TRX559／559；真实HTTP232请求／1864断言，完整canonical及6同声明source records SHA一致，三PNG零差、新旧正式1＋1及restart、三片各完整30解码帧／首帧oracle／AAC有损音轨、真实旧Workerdisabled通过。三Server退出0／26观察group身份gone，最终source／部署／证据SHA见[实施§23](../history/backend-extraction-progress.md#b4-analysis-static-profile-0690)。完整B3／B4／其他媒体／包脚本／资源字体／作者／Web和原全量／性能／GUI／长片／平台人工范围继续独立。


<a id="current-statistics-0700"></a>
## 13. 0.70固定MP3／FLAC独立分析当前统计（有限出口已完成）

产品af77449／最终工具c00c3a5，Git统计固定后者；本批起点ec23912已归档0.69，累计仍f7940d4。包含先提交计划，不含随后本次终态归档文档、生成artifact或未跟踪screenshots；§9～12固定历史点保持，README归文档。

| 分类 | 本批文件 | 本批新增／删除 | 累计文件 | 累计新增／删除 |
|---|---:|---:|---:|---:|
| 生产（src／samples） | 13 | +269／−55 | 217 | +18,425／−1,431 |
| 测试（tests） | 13 | +630／−19 | 105 | +16,861／−19 |
| 工具（tools） | 2 | +218／−0 | 34 | +6,394／−4 |
| 文档（docs／AGENTS／README） | 1 | +46／−0 | 29 | +4,245／−1 |
| 根构建入口 | 1 | +1／−1 | 2 | +14／−1 |
| **合计** | **30** | **+1,164／−75** | **387** | **+45,939／−1,456** |

本批8提交、新增11／修改19／删除0文件、净增1,089行；累计89提交、新增311／修改76／删除0文件、净增44,483行，生产累计净增16,994。新增项目0；薄新probe复用原helpers／Analyzer／Runtime oracle，新完整格式分析不复制算法或扩音轨导出。App未改，相对原基线仍净+518；Git行数不换算为功能量／运行成本。

本批加入固定MP3 LayerIII／native FLAC来源、强格式及strict decode、压缩未知duration省略、真实engine/profile能力门禁、旧新分析结果读取／apply／保存重开兼容。正常solution零警告错误、四TRX551／551；真正HTTP152请求／1255断言，4完整canonical（3新增压缩＋旧WAV）43,502bytes／404points／386,496samples原Analyzer byteexact、三采用保存重开、PNG零差／formal1＋1、strict MP3取消／旧Worker禁用／旧新恢复通过；三Serverexit0／32观察身份gone。原551／单probe工具漂移、两失败及sourceSHA与grant区分见[实施§24](../history/backend-extraction-progress.md#b4-compressed-analysis-0700)。本批不改变Export／VideoExporter／StaticExportTask，includeAudio:true仍PCM16 WAV；旧create/open autodetect owned ffconcat引用卫生已确认未修，优先后批，证据边界非HTTP／跨client／任意路径。完整B3／B4、其他音频／音轨导出、包脚本／资源字体／作者Web及原全量性能GUI长片平台范围继续独立。


<a id="current-statistics-0701"></a>
## 14. 0.70.1自包含音频准备的当前固定统计（有限出口已完成）

产品8000f4b／工具统计点ce588bb，本批起点27a06e0，累计原f7940d4；含先提交计划，不含本次末尾归档doc／生成artifact／untracked screenshots，历史§9～13不回写。README归文档，gitnumstat重新读取：

| 分类 | 本批文件 | 本批新增／删除 | 累计文件 | 累计新增／删除 |
|---|---:|---:|---:|---:|
| 生产（src／samples） | 10 | +359／−43 | 220 | +18,744／−1,434 |
| 测试（tests） | 3 | +591／−0 | 108 | +17,452／−19 |
| 工具（tools） | 3 | +264／−1 | 36 | +6,657／−4 |
| 文档（docs／AGENTS／README） | 1 | +43／−0 | 29 | +4,399／−1 |
| 根构建入口 | 1 | +1／−1 | 2 | +14／−1 |
| **合计** | **18** | **+1,258／−45** | **395** | **+47,266／−1,459** |

本批5提交／新增8修改10删除0文件，净增1,213行；累计95提交／新增319修改76删除0文件，净增45,807，生产累计净增17,310；App未改，相对原基线仍净+518。新增项目0，薄probe／原oracle入口复用，不另建prepare tasks／资源store；代码行数不是shipping功能或性能增幅。

本批仅Backend六家族自包含prepare、固定identity／未发布candidatecleanup；App/defaultMedia、analysis／exportprofiles不扩，晚切0.70.1 PATCH。统一557／557正常build已终态，old0.70真HTTP30／238只修前证据，修后真实HTTP原69113 exit0、131请求／936断言，六家族实际duration兼容／外部引用拒／Host native取消与同handle重试、29完整终态JSON及1391证据文件SHA已核；Serverexit0／31directnative及4Worker成员属2groups身份gone，1561源码无漂移／1014部署文件核验通过，有限出口已完成。完整B3/B4／准备Worker/deadline／压缩音轨及原暂停矩阵仍独立，详见[实施§25](../history/backend-extraction-progress.md#b4-self-contained-preparation-0701)。

<a id="current-statistics-0710"></a>
## 15. 0.71.0固定MP3／FLAC音轨导出的当前固定统计（有限出口已完成）

固定产品／工具终点`99189f2688bf7d44d3e441b038c72d7cad4e6f1d`，本批起点`df5dcf7 / 0.70.1`，累计起点`f7940d4 / 0.49.14`。包含此前计划／产品／测试／工具提交，**不含随后本次终态归档文档、artifact或screenshots**，不更新历史§9～14端点。本节所有`*.md`（含tools README）归文档；§14工具36／文档29使用旧分类口径，保原数，不跨节相减分类；总文件／行数不受分类影响。

| 分类 | 本批文件 | 本批新增／删除 | 累计文件 | 累计新增／删除 |
|---|---:|---:|---:|---:|
| 生产（src／samples） | 13 | +273／−61 | 221 | +18,959／−1,437 |
| 测试（tests） | 12 | +948／−24 | 113 | +18,376／−19 |
| 工具（非Markdown） | 2 | +376／−3 | 31 | +6,751／−4 |
| 文档（全部Markdown） | 2 | +104／−2 | 41 | +6,251／−1 |
| 根构建入口 | 1 | +1／−1 | 2 | +14／−1 |
| **合计** | **30** | **+1,702／−91** | **408** | **+50,351／−1,462** |

本批10提交，新增7／修改23／删除0／重命名0，净增1611行；累计107提交，新增331／修改77／删除0／重命名0，净增48889行，生产累计净增17522。累计测试与工具新增25127行，占全部新增约49.9%。新增项目0、无binary提交，App本批未改，相对原基线仍净+518；行数不等于功能数或运行成本。

本批新增固定MP3 LayerIII／native FLAC音轨、源rate完整strict EOF计数与300秒预算、精确coverage、两执行profiles／旧wire恢复兼容与真实旧Worker禁用；lateHost repair保同源重开分析采用及非法替代路径拒绝。原716／716仅修前历史，修后正常solution零警告错误、新两TRX540／540与未改原四TRX186／186独立；真正HTTP原47134 exit0、385请求3560断言，六影片各90完整帧／Runtimeoracle及音轨、纯视频另90无音轨无独内容oracle、28whole终态与旧新恢复通过。独立source／rawHTTPmedia审查与源码／部署／942证据SHA、3Servers／38private观察成员gone见[实施§26](../history/backend-extraction-progress.md#b4-compressed-export-0710)。两失败独立保留，旧§14／B4G557／HTTP131／936不回写。完整B3／B4、准备隔离、脚本资源字体／作者Web及全量GUI性能长片人工平台暂停保持。

<a id="b3-package-worker-increment-0740"></a>

## 16. 0.74.0 B3-F内部package Worker的有限增量（不重算历史行数）

产品`1524625`已提交推送；本节仅记录该产品提交的有限增量，不更新§15的0.71固定统计端点，也不重算累计四万行归属。该提交14文件（7个src、1个根版本入口、6个tests含fixture）；不含本次验收文档、ignored runner／plan与生成artifacts。新增项目0，MINOR保工程格式28／16／6、配置1、包profile2、SDK1.0.0与固定依赖。

新增私有capability／open／render／close与`WorkerPackageRenderFrameSessionFactory`，一次固定完整package源、4MiB−24B净snapshot、实际refs身份与Worker Process／原pipe绑定、完整Runtime诊断与Error禁像素、static／package互斥、合作取消与真实init／evaluate失控组排水。ScriptCompiler仅公开实际ReferenceIdentity／ReferenceCount，不改编译算法；公共HTTP包能力、static allowlist、route／task／schema／权限不扩。本机Linux实际组隔离不是任意恶意脚本OS sandbox。

统一八commands exit0／solution零警告错误，七TRX971／971（155／639／70／31／2／44／30）、1618source／5849bin无漂移及448观察身份gone；独立发布434文件／raw IPC健康与缺refs拒绝／已发布Runtime20另计，作者84与mode2及原失败另计。独立最终审查9482 SHA引用／18TRX／1058唯一观察身份gone，零mismatch；manifest与各SHA见[实施§29](../history/backend-extraction-progress.md#b3-package-worker-isolation-0740)。验证manifest保原HEAD7c31865，产品源码与freeze字节一致。历史B3-E824与B4-I775／HTTP699／4239不重写，不并本批；完整B3／B4、预设／媒体字体闭包、source诊断DTO、作者Web及原暂停矩阵保持。Git行数不换算为功能量或性能证据。

<a id="b3-package-diagnostics-increment-0750"></a>

## 17. 0.75.0内部package完整诊断的有限增量（不重算历史行数）

本批在B3-F内部会话补齐source／image／material三类完整诊断及固定catalog身份恢复、独立diagnosticsVersion1协商、分项256条／64KiB与合计512条／128KiB预算。Script／Source owner严格分离；controller ArtifactHash仅格式与固定源关联，Host不独立重编译核DLL hash。公开HTTP包profile／route／正式任务、预设／外部媒体字体范围不扩；不更新§15固定统计端点或重算累计行数。

统一solution零警告错误、五TRX1001／1001（155／699／70／33／44）、1623source／5849bin无漂移、476观察身份gone；独立434文件发布、raw健康／缺refs拒绝、实际发布Runtime22及真实0.74旧诊断能力拒绝均green，不并1001。作者144／mapper22／Runtime作者22及原失败、legacy-final1 CA1869／0tests失败保持独立。完整SHA及计数边界见[实施§30](../history/backend-extraction-progress.md#b3-package-diagnostics-0750)，旧B3F971／部署20、B3E824及原HTTP699／4239不回写。MINOR保schema／SDK／包profile／固定依赖／项目，完整B3／B4与原暂停矩阵保持；行数不等于功能量或性能证据。
