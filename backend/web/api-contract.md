# 前后端 API 执行合同

状态：**冻结1.42.4**，产品元数据与源码指纹更新于2026-11-04；产品 **0.110.7**，HTTP `protocolVersion:"1"`。Host 共享编译缓存、Worker 固定产物消费及同输入 HTTP seek 复用已接通；严格软取消后可在原 Worker 重开 Runtime，硬失败仍排水。关闭失败保留 `closing` 和资源所有权，实际排水／清理成功后才返回 `closed`。桌面 GPU stream 在两个进程名额内最多保留一个完整预热备用，业务一次绑定原设备，每输入仍换 Worker。公共路由、DTO、命令和 schema 保持；v1 时间线重放仍保留。详细边界见[预览行为](../docs/architecture/backend-api.md)。固定基线与文件指纹见[manifest](contract-manifest.json)，合同、匹配发布与 fresh HTTP 分别登记。

GPU 流按精确修订创建、换来源、改变尺寸和关闭；创建受真实设备及分配能力限制。`registrationState` 是登记状态，`workerState` 来自 Worker 实时查询；`textureAlphaMode` 固定 `premultiplied`，`alphaMode` 仅控制背景透明度。桌面主进程的私有 claim 不属于公开 API。详细协议和失效语义见仓库根 `docs/architecture/gpu-preview-stream-v1.md`。

**1.24.1／0.94.1修复：** `ExportJobRequest.format`透明格式与实际服务一致为`transparentProRes4444`；错误旧值`transparentMov`仍被服务拒绝。附着来源／前奏缺失分别带`TargetStale`及`command.branchId`／`command.leadInId`，错误容器带`ContainerInvalid`及`command.containerId`。无新路由、DTO或命令；匹配发布Server＋Worker后使用产品0.94.1。实际构建、HTTP与合同证据见[修复记录](../docs/backend-basic-business-bugfix-0941.md)。

静态辉光新增 `glow` 及 color/opacity/blurRadiusPx/spreadPx 四叶，支持继承、显式关闭和 restore；颜色使用完整RGBA对象。工程/歌词/模板 schema 为29/18/7；具体语义及验收入口见[辉光参考](../docs/style-reference/glow.md)。

## 1. 执行依据与文件分工

| 文件 | 用途 |
|---|---|
| [api-index.md](api-index.md) | 全部112路径／125方法、认证、请求和响应清单；包含5兼容别名 |
| [openapi.json](openapi.json) | 固定OpenAPI 3.1.0机器规范，供类型生成、请求/响应校验和mock使用 |
| [行为规范源](../docs/architecture/backend-api.md) | 认证、回执、修订、任务、SSE、字节读取及所有端点的详细语义，继续作为唯一详细行为正文 |
| [contract-manifest.json](contract-manifest.json) | 固定上述文件、工具及当前Backend源码的SHA-256；绑定这一份行为源而不是任意未来版本 |
| [examples.json](examples.json) | 可校验的合成wire示例及反例；示例身份、时间、hash不能用于实际请求，不是运行验收证据 |
| [校验工具](../tools/backend-api-contract/README.md) | 从当前编译DTO导出类型、核对路由、检查规范及指纹、校验真实HTTP留档 |

行为正文不复制到第二份手写文档。前端交接应固定**同一提交**中的本目录、`docs/architecture/backend-api.md`及manifest，核对SHA后使用；不能只取最新OpenAPI并搭配旧行为说明。机器规范与行为正文共同执行：OpenAPI约束wire形状，行为正文补充跨请求、跨字段、生命周期与原始字节完整性。二者矛盾即合同缺陷，必须在后端仓库修正并形成可审阅变更，不能由前端猜测。

OpenAPI使用[固定3.1.0标准](https://spec.openapis.org/oas/v3.1.0.html)及JSON Schema 2020-12。这里交付离线文件，**Server没有新增 `/openapi.json` 路由**。生成工具仅引用Contracts，不需要Avalonia或共享ASP.NET runtime，也不改变Server依赖。schema是客户端应发送/可接收的规范形状，不穷举JSON反序列化器对大小写、数字字符串或enum数字的所有容忍行为。

## 2. 客户端必须遵守的行为

1. **连接与认证。** 使用可信启动器ready中的实际随机loopback地址与私有bootstrap；浏览器Origin须精确允许。health公开，登记/恢复用bootstrap Bearer，其余接口同时用access Bearer与`X-Mako-Client-Id`，包括能力、SSE及所有内容下载。读取推荐带主机头；凭证不进入URL或日志。访问令牌默认120秒、client租约300秒；heartbeat续期不返回新token，resume轮换access。服务重启旧access可先401，不能只等待409 `hostRestarted`。
2. **回执。** 每个有副作用请求携带`requestId/hostInstanceId/validUntilUtc`。通常嵌套metadata，commands顶层、cancel直接正文、上传请求头。register/heartbeat除外。deadline取服务时间，最多600秒；同client、原ID、原操作、原载荷及原deadline重试取原结果，修改载荷或deadline须新意图。回执先于修订门禁。默认1024项/client及64MiB；满时拒绝新操作，不驱逐有效回执。客户端超时、断流、500不能证明操作未执行；先查权威状态，不能自动换ID重复执行未知结果。
3. **工程权威与并发。** 同工程写入串行；每次采用精确expectedRevision。真实变化/undo/redo递增修订，no-op不增修订或历史。冲突读取新状态并保留用户草稿，不盲目重放。GET、SSE及命令响应可能晚到，不能用较旧projectRevision覆盖较新状态；host变化则废弃旧身份。前端维护草稿/选择/播放头，后端拥有工程与历史。
4. **整数与空值。** 工程修订、序号及多数微秒为十进制Int64字符串，不能统一转JS Number。`composition.outputDurationUs`和`summary.audioDurationUs`仍是JSON number；超过JS安全整数需保真解析。原始analysis完整JSON内整数也是number，当前预算内安全。普通响应null省略，但样式字体／效果及local.value的显式null保留；未打轴是`time:{}`；原始验证/导出报告可显式null，分析point的beatPhase也可null。歌词行/元素读模型身份字段实际为`id`，命令目标使用`lineId/tokenId`。
5. **同步与异步。** 上传、新建分别201；新建返回summary。open/recover/save/close及validation/export/analysis返回202 JobResponse，只表示接受；GET job的200也可能state=failed。保存→关闭→重开必须分别等前一job成功。终态为succeeded/failed/cancelled/unknown；unknown不自动重做。cancel请求成功不表示资源已排水；公开committing不可撤销，读取canCancel和最终状态。
6. **SSE。** 使用带认证头的fetch流；普通EventSource不满足头合同。先client state取得游标，再连接events；Last-Event-ID优先于after。普通data是BackendEventResponse，resync.required是ClientEventsResponse，client.expired是错误DTO。游标缺口重新读client/preview/job状态；事件只是提示，不携完整工程或像素。约15秒注释心跳不续client租约。
7. **预览。** 创建时固定精确工程修订；编辑后显式PUT input，服务不自动追随。每次requestSeq递增；seek也须大于此前接受值，并采用响应的新generation。只显示仍匹配previewId/generation/sourceRevision/最新序号的帧；晚到fetch、PNG解码及旧SSE不能覆盖新帧。当前最多1280×720、裸帧4MiB，PNG默认30秒；持续帧请求也占回执，客户端需合并/限流并处理429，没有30/60fps保证。
8. **资源及原始字节。** 原始上传需安全文件名；中文名使用URLSearchParams的fileName或multipart，不直接写Fetch头。multipart恰好一个文件，字段名任意。下载使用认证fetch，再接Blob/播放器并释放旧对象。Range仅单段；If-None-Match命中304，无效Range416，HEAD/304/416无body。analysis的206可能不是完整JSON；先完整取回并验证才能解析。验证/导出完整报告没有Range合同，只有媒体及analysis等字节端点支持。
9. **恢复与权限。** live工程/job按当前client隔离；旧主机recovery清单及登记报告按本机profile权限读取，不是旧owner恢复。重新登记不恢复旧会话/旧凭证，也不自动执行unknown任务。G1只在新登记配额不足时尝试回收过期、无工程/任务/有效回执/在途请求或资源保护的client，不自动discard，不删除持久工程或任务产物。
10. **错误。** 解析`application/problem+json`中的code/可选failureCode，不依赖message。它是BackendErrorResponse，不是完整RFC Problem Details。诊断severity是普通string，持久化可用Info/Warning/Error，Runtime可用小写；不能只按小写enum解码。能力必须读实际部署响应，health、版本号、配置文件存在均不能代替真实能力握手或渲染成功。

## 3. 冻结范围与已知限制

当前九十六种工程命令，见[列表](api-index.md)及[时间动作行为§6.5](../docs/architecture/backend-api.md#65-时间动作与严格命令分型0770)。精确修订、一次历史/no-op保redo、回执和领域门禁保持。四个背景命令和`project.applyEdits`复用原编辑用例；编辑候选只读，不写工程历史。

各任务的当前输入范围读取对应`sourceProfiles`，不能从兼容单值`sourceProfile`、其他任务能力或版本推断。原静态、直接包、包与预设来源保留；需准备的图片、视频、包字体等闭包使用`runtime-project-input-v1`。Host在Writer下固定领域源与授权引用，在Writer外准备不可变输入，Worker核验完整身份并交原Runtime；HTTP不接主机路径或私有grant。旧图片profile的2MiB/4096等预算属于其独立历史路径。导出`quality`可为draft/standard/high；帧数、时长、媒体、音频及存储限额由实际部署能力公布，可空字段省略表示未设置该限额。源码实现、离线合同校验、真实Worker/HTTP、像素和浏览器验收分别登记。

独立audio analysis继续消费授权音轨，不等于效果包分析作者能力。原package无音轨导出的0.98.1／1.32.0证据与1.32.1测量勘误保留历史归属，不能用于证明本次统一输入。

S1提供来源列表、正文/注音/原时间状态完整歌词窗口与标量设置读取，见[行为§6.4](../docs/architecture/backend-api.md#64-精确修订的编辑读模型0760)。三个GET必须携带唯一expectedRevision；分页默认offset=0、limit=100，limit范围1..100，offset允许末尾空页；终页省略nextOffset。读到409 revisionConflict时重新取摘要及当前修订，不能拼接不同修订的窗口。新增设置时长为Int64字符串，旧composition/summary的number例外保持。 `outputDurationUs`省略/null表示尚未确定，导出前必须显式设置；不会自动回退到音频或歌词末尾。

资源清单读取见[行为§17.2](../docs/architecture/backend-api.md#172-工程改名与资源清单)：`GET .../project-sessions/{id}/resources` 只返回精确工程修订的实际引用，分页前先对完整闭包执行20,000项／1MiB读模型预算，不将客户端其它上传混入。直接效果包展开为 `effectPackageManifest` 与 `effectPackageFile`；manifest/file 项分别带 `packageId`、`packageRevision`、包内 `packagePath` 和实际 `packageRole`，manifest 额外带 `packageClosure` 摘要。`packageClosure.status` 与资源 `status` 新增 `validated`／`unsupported`；`packageClosure` 只出现在 manifest，`validatedFileCount` 仅表示可识别 role 文件数。包资源清单仍不直接带 `assetId`／`contentUrl`；未知 role 保留为 `unsupported`。对已验证资源可用 `POST .../project-sessions/{id}/resources/package-asset` 携带 `expectedRevision/resourceId/metadata`，复用同一 client 的受控 asset、receipt 幂等、Range/HEAD/ETag 和未引用 TTL，返回 `ProjectPackageResourceAssetResponse` 与认证 `contentUrl`。工程关闭或客户端回收释放工程引用，过期后按现有 `resourceExpired` 语义拒绝。服务端只在内存中复核包身份、文件 SHA/大小、定义引用和支持 role，不编译、执行、解码图片或打开外部媒体，因此这项清单和原始字节读取不能替代连续预览 fresh HTTP、字体/媒体闭包、导出、分析或场景测量验收。

S3a已提供正文/注音、增删复制行及六标量设置命令，见[行为§6.6](../docs/architecture/backend-api.md#66-正文行操作与标量设置0780)。每动作一次历史，no-op保redo；可选feedback只提供真实选择建议和有界统计，完整新单行超过1MiB/20000项在提交前413，无截断。S3b来源管理见[行为§6.7](../docs/architecture/backend-api.md#67-来源管理0790)，新增空翻译、完整配置、删除与主行关联；主角色保护和原时间策略/展示验证保持。元素拆合/重排/手工与整来源重切见[行为§6.8](../docs/architecture/backend-api.md#68-元素拆合重排与重切0800)；segments≤20000，单行完整S1投影及本次改变行累计16MiB操作预算提交前拒绝，不截断或部分提交。再次导入与追加见[行为§6.9](../docs/architecture/backend-api.md#69-再次导入与粘贴追加0810)：固定授权资源/明确目标，完整报告与候选/回执在一次历史前验预算，Writer外准备后修订变化精确409。G11波形见[行为§6.10](../docs/architecture/backend-api.md#610-有界同源波形读取0820)：64MiB已保留峰值payload，窗口≤8192桶/64KiB，音频身份不绑歌词修订；缓存满原创建/打开仍成功，元数据明确不可用，不二次decode、不提供PCM播放衍生资源。静态样式读写见[行为§6.11](../docs/architecture/backend-api.md#611-四层静态样式读写0830)；独立模板库见[行为§13](../docs/architecture/backend-api.md#13-独立模板库0840)，支持builtin/personal读取及个人库create/replace/copy/rename/delete。工程显式分配见[行为§13.3](../docs/architecture/backend-api.md#133-工程内显式预设分配0850)；静态预设工程可进入公共预览并读取实际PNG；直接效果包项目公共预览见§9，当前媒体/包字体共享输入见§13，完整模板与作者配置见§14～15。前端只能对已列且capability可用的功能做真实调用，其他界面可以保留草稿设计，但不能把mock成功记为后端完成。历史接续提案保存在[接续方案](../docs/backend-template-library-assignment-plan.md)；当前可调用范围以本合同和实际capabilities为准。
W4首个有限切片见[行为§13.5](../docs/architecture/backend-api.md#135-工程编排读模型与默认容器-w4-合同1130)：`GET .../orchestration?expectedRevision=...` 返回精确修订的 nodes/programs 结构投影；`orchestration.setDefaultContainer` 只接受非空 `branchId/containerId`，目标必须是当前工程已有歌词容器。成功变化一次历史并清理 redo，`changeSet.areas` 为 `orchestration` 与 `renderInputs`；相同容器是 no-op 保留 redo，回执提供 `orchestrationResult.changed`。默认容器命令不开放 null/清除；前奏编辑见行为§13.15/§13.19，最终同帧测量见§13.21，浏览器 W4 验收独立登记。 G19-B在[行为§13.10](../docs/architecture/backend-api.md#g19b-clip-lifecycle-0910)新增 `orchestration.createClip`、`renameClip`、`setClipContainer`、`deleteClip`；严格校验来源／范围／容器，成功回执提供 `clipResult`。

静态样式详见[行为§6.11](../docs/architecture/backend-api.md#611-四层静态样式读写0830)：GET style按精确修订及严格四层目标读取baseline／本层local覆盖／staticHierarchy，字体和效果显式null不省略，包材质只读身份。styles.patch／styles.restore按稳定ID同层批量，一次历史、no-op保redo，canonical材质与未选参数保留；新意图64KiB、字体256 UTF-16／回退32／色标256、新值展开与改变目标投影各16MiB，完整读取1MiB／20000项，实际完整回执预算在提交前验证。staticHierarchy不含预设／node／clip上下文，不能称最终帧外观。用户明确授权将直接写接口作为底层能力交付；前端仍可只通过模板独立制作与显式分配使用样式，不要求把目标写接口放到歌词页面。

**R1已在0.86.1修复：** 合法无音轨工程可创建静态预览。`CaptureAsync` 仅为存在的音频身份申请资源租约；无音轨跳过空资源身份，仍执行固定静态输入、认证帧读取、SSE提示和关闭排水。原0.75.1复现记录保留在[后端审查](../docs/backend-review-and-web-contract-plan.md)，不再将404 `resourceNotFound`作为该组合的当前行为。

新建可省略音频和歌词资源，直接从真实空工程开始；提供资源时仍必须先上传并使用同一client的授权ID。W0实际使用匹配完整Server/Worker的[self-contained入口](../tools/backend-web-host/README.md)。无Worker开发模式不具备真实音频准备。当前有效产品/格式精确门禁保持，不手改旧工程版本字段冒充兼容。

## 4. 冻结后的双端变更流程

- 后端仓库维护行为、OpenAPI、例子和manifest；前端仓库固定合同版本、提交及文件SHA，从同一OpenAPI生成类型并记录生成器版本。生成类型不能替代SSE、回执和状态管理适配层。
- 新API先给出路由/命令、请求响应、修订/历史粒度、no-op、错误及验收场景的合同差异，再实现；提案留在规划文档，**未实现能力不得写进本已实现清单或标为可用**。需要提前mock时使用单独候选合同，不改当前冻结版。
- 行为兼容的补充/勘误用合同PATCH；兼容新增接口/字段用MINOR；删改字段类型、身份/历史/回执语义等破坏性变更用MAJOR并给迁移方式。HTTP的`protocolVersion`及产品版本各按其规则演进，不能由合同版本自动推导。
- 每次交付更新行为源、工具中的审阅映射/语义覆盖、examples、OpenAPI、index、manifest；检查源码/路由/DTO漂移，执行对应真实HTTP及原有回归，再由前端做真实浏览器闭环。分别记录“合同已接受、mock可用、后端实跑、浏览器通过”，不相互替代。
- `--write`仅是显式重生成，不能当作漂移已审查。manifest对整个Backend源码的覆盖会报告不改HTTP行为的内部变化，此时仍记录核对依据后更新指纹；跨Runtime/Media业务语义依靠相应测试，不宣称hash能证明行为等价。

## 5. 验证边界

当前冻结检查覆盖108路径/120方法一致、574个schema、3388个合成例（2521个反例）、公开DTO与record继承的类型/空值/默认值、九十六工程命令与五种模板库命令分型、读模型与查询预算、OpenAPI标准结构、schema引用、正反例及真实HTTP留档的JSON形状；完整原始报告、Range、SSE仍需按业务验收场景验证。例子只说明wire形状，不能验证身份、修订、租约、时序、真实像素或canonical SHA。

1.23.0新增G20-B有限createNode/copySubtree、两个capability与createdNodeIds；1.22.0新增G21-B四种属性/动画命令、G22-A固定几何参考GET、G23四种手工前奏/提示容器命令与完整leadIns投影、G24单句局部微调GET/set；两个新增读取均严格唯一query/精确修订，line transform与layout.slotAnimation保留显式null。1.21.0新增G21内部布局默认位置命令、layoutResult与capability，placement可显式null清除且仅作用于已有容器内部布局；1.20.0新增G20六种有限结构命令、structureResult与capability，见[行为§13.11](../docs/architecture/backend-api.md#g20-structure-0910)及实施§53；1.19.0含G19-B四种片段生命周期命令、clipResult、capability声明及当前生成的schema与正反例，见行为§13.10及实施记录§52；1.18.0为上一版G19片段范围与编排投影，见行为§13.9及实施记录§51；1.17.0为上一版G18快速布局，1.15.0为上一版G16候选试看，见G16候选试看行为§13.6及实施记录；1.14.0为上一版R3/W4合同历史。R3固定 `ProjectPresetApplicationSourceDto` 的 nullable source 字段 wire schema，并公布 `project-sessions.preset-applications.read` capability；1.13.0为W4/G15上一版合同历史，保留其编排读模型与默认容器命令说明。模板库1.9.0的schema／例子及0.83旧264 schema／756例逐项保持；1.10.0新增四种工程命令和严格四类目标union，1.12.0新增工程预设分配读模型及严格精确修订读取。G11的382回归／340HTTP保持§39，S3d的602相关回归/5089HTTP保持§38，其他历史分别保留§32～37，不累加。

前一轮审查的982项有限回归及HTTP86请求/526断言见[审查记录](../docs/backend-review-and-web-contract-plan.md)，不并入本批。后端完成不等同相应前端浏览器、性能、完整B3/B4、长片或跨平台验收，也不恢复原暂停范围。

G20结构命令字段详见[行为§13.11](../docs/architecture/backend-api.md#g20-structure-0910)：setNode仅改名称；reparent保持世界外观；reorderChildren与reorderSlots分别给出完整直接子节点／槽位顺序；addSlot只追加空槽位；removeNode拒绝仍被引用的子树。名称与parentId的必填显式null语义必须保留。本节六命令的structureResult仅返回操作身份和顺序，null响应槽省略；有限节点创建/复制见下文G20-B与行为§13.17，几何/布局参数沿G21命令。


## G21 内部布局默认位置增量（合同1.21）

`orchestration.setLayoutDefaultPlacement` 必须显式提供非空 `containerId` 和 `placement`；placement 为完整位置对象或显式 `null`，null 清除已有内部布局默认位置。成功回执提供 `layoutResult`（containerId/changed），真实变化一次历史并更新 orchestration/renderInputs，同值为 no-op。G21-A该命令单独只编辑内部布局默认位置；合同1.22.0新增范围见下节及行为§13.13～13.16。

## 基础编排后续切片（合同1.22）

本节保留1.22历史切片的范围；当前G20～G24与作者能力见§6、§14～15。

新增四种 G21-B 命令：`orchestration.setNodeGeometry`、`setLayoutSettings`、`setSlotAnimation`、`setProgramAnimationPolicy`。对象字段全部完整提供；位置支持六种 anchor、画布外有限坐标与正缩放；参考宽高比例为正；安全区必须在0..1内有序，maxWidthRatio在(0,1]。slotAnimation完整替换十四字段与三组完整通道，微秒为非负Int64字符串；null仅普通布局可清除。program animationPolicy为Karaoke/Static/FollowPrimary或显式null。propertiesResult提供真实changed与目标身份，canonical值从精确修订orchestration读取。

G22-A：`GET .../orchestration/geometry?expectedRevision=...&nodeId=...` 返回固定容器参考与父参考，固定 `referenceKind:"fixedContainerReferenceV1"`、`finalPixelGeometry:false`，矩阵定义、四角顺序、输入预算与稳定失败见[行为§13.14](../docs/architecture/backend-api.md#g22-fixed-reference-0910)。

G23：`orchestration.createCountdown` 在已有普通组下由Core生成容器/标记稳定身份，count1..64；`createLeadIn/updateLeadIn/deleteLeadIn`维护显式手工前奏。新增/修改七字段全部必填，name可null，durationUs为正Int64字符串；不自动生成前奏。orchestration返回program.leadIns、有限countdown/textMark内容及完整slotAnimation；leadInResult返回真实身份。[详细行为§13.15](../docs/architecture/backend-api.md#g23-lead-ins-0910)。

G24：`GET .../orchestration/line-local-transform?expectedRevision=...&branchId=...&lineId=...`及`orchestration.setLineLocalTransform`维护单句offsetXRatio/offsetYRatio/rotationDegrees/scale；transform必须出现，对象或显式null。响应同样保留transform:null，比例偏移允许画布外，缩放为有限正数，不修改歌词时间。[详细行为§13.16](../docs/architecture/backend-api.md#g24-line-local-transform-0910)。

新增GET必须唯一提供所有查询字段、拒绝未知字段并保持client/session归属与精确工程修订；capabilities分别公布两个read和九个commands项。写入真实变化一次历史，no-op保redo；完整候选读模型与回执超预算在提交前拒绝。上述有限切片不构成最终Runtime像素几何、完整G20-G24、完整编排作者或浏览器验收。

## G20-B 有限节点创建与复制增量（合同1.23）

既有commands新增 `orchestration.createNode` 与 `orchestration.copySubtree`，共64工程命令，不新增路径/方法。createNode全部显式提供kind/parentId/name/slotCount；kind精确为group/auto/slotSequence，group/auto要求slotCount:null，序列1..64整数。parentId:null为画布，否则必须已有普通组。copySubtree全部显式提供nodeId/name，同父复制完整子树，name:null清除副本根名称；内部parent/slot/mark重映射，单槽位/mark追加外部owner引用；不复制歌词或程序/片段/前奏/预设绑定。默认几何、Auto布局与初始槽位动画详见[行为§13.17](../docs/architecture/backend-api.md#g20b-node-creation-0930)。

Server生成身份；成功structureResult.operation为createNode/copySubtree，nodeId为新根，changed:true，parentId根层省略，containerId/orderedIds省略。新增createdNodeIds为完整唯一新增身份，根第一、其余候选新增顺序；旧六命令继续省略。相同回执重放保持已接受ID，成功一次历史且支持撤销/重做。

名称≤256 UTF-16 units、输入身份≤128且非空白；command≤16KiB，源/候选各≤2000节点/深64，copy≤128节点。源/候选验证输入另限20000项及累计身份/名称/标记文本1048576 UTF-16 units，包含歌词branch/line/token和程序关联遍历项，长歌词也可能413；完整编排输出独立限1MiB/20000项，完整回执按实际配置提交前预检。形状400、失效目标/非法父/坏展示关系422、预算413，失败无部分提交。schema的code-point长度门禁不能代替UTF-16、真实树、完整预算或HTTP回归。

本节1.23历史批次仅覆盖有限后端基础结构与同父复制；后续G20～G24、mark/附着/自动前奏、Worker测量、公共package与作者能力见§6、§9、§13～15。通用节点JSON、浏览器W4及原全量/性能/长片/GUI/平台矩阵不由该历史切片验收。


## 6. 1.24.0 G20～G24整体补齐

历史1.24.0为70 paths／77 methods／401 schemas，1913例（1394反例）。新增14种工程命令、单句展示GET、真实Worker同帧geometry+base64 PNG测量POST和Node静态预设目标，完整字段、no-op/redo、预算与失败边界见[行为§13.18～13.21](../docs/architecture/backend-api.md#1318-布局类型与单句展示继承合同1240)。自动生成完整预览保留New/Update/Remove/Preserved/Skipped和原因，selectedLineIds显式选择应用；附着切换使用真实文字参考后提交。measurement的time/layout是不同采样语义，完整回包不截断，不创建job/preview/receipt。

旧1522例保留，其中1个响应例仅增加projectEnterAnimation:null与programs[0].leadInGeneration:null两个新必返字段，原值不变；新增完整正反例独立追加。Node目标保留所有kind已知无关字段显式null兼容，不接受跨kind非null。合同schema检查与真实Worker几何/PNG、浏览器、用户验收分别记录；本批不代表任意节点内容、完整编排、公共package或完整B3/B4/B5/B6交付。

## 7. 1.27.0 TTML与独立注音时间轴

在1.26.0歌词交换基础上，新增七种严格ruby片段命令、完整segments读模型、真实rubyResult反馈与撤销重做反馈；完整20000项预算计入每个片段。time端点为Int64十进制字符串，前端须保留超过2^53的值。四级组目标与五级片段目标独立于旧token target，严格字段、required-null和1..1000批次均见OpenAPI及[行为§18](../docs/architecture/backend-api.md#18-ttml-与独立注音时间轴0960合同1270)。旧setRubyText对同文保持no-op，对timed/multi-segment实际改字明确拒绝RubySegmentsRequireExplicitEdit。

歌词导出scope省略或branch须branchId；document仅允许ttml.project且禁止branchId（连null也拒绝）。计划与导出响应必返scope，document branchId显式null。project profile恢复明确分支集合、身份、关联、raw时间/状态与ruby参数；未承载样式须完整损失计划。AMLL保持选定来源交换。官方AMLL parser按毫秒舍入及超JS安全整数毫秒的精度风险、略去无独立时间注音/普通词的逐字列表、按ruby范围替代基础元素时间的消费端限制均纳入损失；XML仍保微秒与Missing正文，不宣称消费端无损。导出报告新增三个ruby片段计数，未接受损失不发布产物。

上传/创建/再次导入接受application/ttml+xml。多分支项目TTML再次导入须options.sourceBranchId选择源分支（可省不可null，未选SourceBranchRequired，不存在SourceBranchNotFound）；省略来源/失去跨来源关联进入完整原报告。外部多div普通TTML按文档顺序合入候选来源，不把段落任意解释为工程分支。标准父容器与AMLL时间约定分开；agent/旁轨进入报告。

歌词schema v17只迁移v16；无时间兼容片段逐片跟随基础token，高亮与基础token事件分别保语义。静态/package/Worker同源片段数据并不扩大公共B3/B4/B5/B6、浏览器/用户或原性能/长片矩阵。授权目录、音频替换/清除、改名/资源、完整.mktpl惰性传输、真实上下文21属性来源及clip扩缩拆合继续有效。最终验收状态见实施计划与分层证据，不以合同冻结或合成正反例替代运行验收。

## 8. 1.28.0 Ruby spacing 行为

产品0.97.0新增自动 ruby 墨迹间距求解，沿既有预览、测量及渲染链路生效。水平最小 gap 为1个缩放前布局像素，仅约束垂直墨迹严格相交的 ruby，并检查所有此前注音，包括跨无注音元素。完整字簇/基础 token 刚性组只做整体平移，聚合 shaping、原对齐和自然 overhang 保持；无时间和有时间采用同一求解规则。无冲突保持原布局，历史冲突画面可能改变。

ruby 锚点为原始 token 自然边界加组位移，新增 gap 不吞入前 token 的 ruby 右锚点；正文进度路径沿最终位置，gap 属于前 token 的无墨迹进度区间。完整联合墨迹继续采用既有 maxWidthRatio/safe-area 宽度缩放；用户主动移动/旋转造成出画不自动钳制。共享 cluster 组内无法安全拆分、非单调映射等采用内部保守回退，不新增公共诊断字段。详见[行为§19](../docs/architecture/backend-api.md#ruby-spacing-0970)。

HTTP路径/方法/schema/命令保持80/89/449/91，2648例及2040反例不变；协议1、歌词schema17与原工程schema保持。产品/合同MINOR表达可见布局行为改善，Server和Worker须版本匹配；版本不匹配仍由原EngineIdentity门禁拒绝。分层验收以实施记录为准，不把合同冻结当作像素或Worker验收。

## 9. P2-A直接效果包预览（1.29.0／0.98.0）

本节保留该版本的历史有限来源与验收边界；当前统一Runtime输入见§13。

使用现有合法工程open和project preview入口，不上传裸snapshot/源码，不新增路由或工程命令。服务端按完整固定snapshot选择profile，只有实际Worker能力包含`package-lyrics-scene-v1`才允许；保EngineIdentity/完整refs/diagnosticsVersion1协商，失败503 `previewUnavailable`，协议/能力不匹配为`WorkerPackageProfileMismatch`，其他Worker不可用为`WorkerPackageUnavailable`，不回退Host或静态。Hosting还按服务端非wire的VerifiedProfile标记和实际包codec闭包再次准入。普通与透明、固定输入复用和seek换代规则不变。包profile拒非空presetApplications、非Color背景/外部媒体字体闭包；B4和模板/候选预览保持原静态范围。

创建或读取直接包预览会话时，`PreviewSessionResponse.packageClosure`（可选）公布服务端已经固定的包闭包摘要：`closureRevision`、每个包的`packageId`/`revision`/文件数量/总字节/资源角色，以及总文件数量和总字节。该摘要只用于前端确认连续预览固定输入与资源预算，不暴露本地路径、资源字节或可独立读取的句柄；其读取权限与预览会话相同，关闭会话后随输入租约一起失效。没有直接包输入时该字段为null。摘要存在不代表图片、视频、字体或preset+package已经获得执行能力，后者仍须在对应Worker capability握手和独立HTTP矩阵中证明。

公共`BackendDiagnosticDto`新增可选`source/imageEffect/material`：`originKind=compileSource/imageEffect/material`时恰有对应payload，不能伴runtime occurrence。source十字段按原响应null省略语义保留（0行列合法）；image保真实graph/instance/artifact/input/code；material有script/source owner union。runtime保原occurrence，transportFailure不伪造实际发生。source `isError`或runtime Error禁止发布新PNG，失败状态无latest；旧成功frame lease按原TTL保留。图/材质旁路为warning，runtime Error同时保留旁报，实际render错误回执须HasError且source为空，经基础shape/包帧/proxy操作校验；编译失败仅限open来源报告。公共HTTP不回传完整snapshot sourceSha，完整source SHA等价依赖独立固定输入oracle/内部Worker证据。

公共完整诊断最多768项/512KiB UTF-8 DTO数组，各kind最多256项、三类包合计512项；每页最多128项、最多32页且取实际配置更小门禁。私有runtime256/64KiB及包各256/64KiB、合计512/128KiB独立保持。超限`diagnosticLimitExceeded`/`PreviewDiagnosticsBudgetExceeded`，不截断；当前generation完整页链一次发布，旧cursor失效。完整字段/稳定映射见[统一API§20](../docs/architecture/backend-api.md#public-package-preview-0980)。运行验收分列于实施记录，schema和规划不等于已完成真实HTTP/浏览器验收。

## 10. B4-P1 package validation profile（1.30.1）

本节保留该版本的历史有限来源与验收边界；当前统一Runtime输入见§13。

`package-lyrics-scene-v1` 可作为正式 validation job 的固定来源，但仅在实际匹配 Worker 完成 validation capability handshake 后加入 `ValidationTaskCapabilitiesDto.sourceProfiles`。客户端不能选择任意来源或上传裸 snapshot；Hosting 从精确工程修订生成 package snapshot，并复用既有 validation job、报告原始 JSON、诊断分页、取消/deadline、恢复读取与 owner ACL。

包诊断使用 `compileSource`、`imageEffect`、`material`、`runtime` 四类互斥 payload。编译或 runtime Error 使任务失败且不发布新帧；图效果与材质 warning 可保留成功帧。输入沿用 package codec 与验证预算（4 MiB、200000 source values、深度门禁；报告 256 KiB、256 issues、512 occurrences），任一完整性或预算错误均稳定失败且不截断。本validation package profile拒绝非空预设、非Color背景、外部媒体、字体闭包和preset+package混合；这些门禁只定义本验证来源。package无音轨export的独立有限HTTP矩阵已由[实施§76](../docs/backend-extraction-progress.md#b4-p2-package-no-audio-export-http-0981)收口；measurement沿[行为§13.21](../docs/architecture/backend-api.md#1321-真实worker同帧测量合同1240)的实际Worker五profile，可含受限的直接包与固定静态预设混合，仍限Color背景与测量预算，合同1.32.1修正枚举不代表新增fresh HTTP。独立audio analysis消费授权音频，与包渲染支持分开。历史初次有限真实HTTP收口见[实施§65](../docs/backend-extraction-progress.md#b4-p1-package-validation-0981)；该批覆盖成功报告、compileSource/runtime/image/material失败传播、预算拒绝及cancel/deadline/close。warning/recovery及主成功多帧按后续§66～69各自证据登记，不把§65单批视为完整warning、recovery或完整B3/B4。


## 11. 已保存工程图片背景预览（0.99.0／合同1.33.0）

本节保留该版本的历史有限来源与验收边界；当前统一Runtime输入见§13。

复用既有project create/input/frame/state/seek/resize/close路由，客户端仍只提交工程ID与精确修订，不提交图片bytes、路径、profile或grant。读取 `runtime-info.sourceProfiles` 中实际协商的 `project-image-preview-v1`；兼容单 `sourceProfile` 保持 `static-lyrics-scene-v1`，`enabled/status` 不能单独证明图片能力。图片探测失败只省略该profile，创建和实际open仍复核当前Worker。

仅消费ProjectStore已加载闭包中的Image主背景，基础为无预设的静态、完整静态分析或直接效果包。Host在Writer下固定snapshot与短期读引用，Writer外读取并核完整size/SHA，Worker从同一固定bytes完整解码和实际绘制；预览随后不依赖工程目录。原图≤2MiB、单边≤4096、解码BGRA≤16MiB、完整envelope≤4194280字节（含base64且已扣open头）。输出、frame lease、诊断和generation沿既有预算/时序。

create/input能力错误为503 `previewUnavailable`，`WorkerImagePreviewProfileMismatch`或`WorkerImagePreviewUnavailable`；闭包缺失/变化为422 `commandNotSupported`，`ProjectImageSourceUnavailable`或`ProjectImageSourceChanged`；原图/封包超限413 `resourceTooLarge`／`SnapshotSizeExceeded`。上述图片输入错误field为background，能力错误field为worker；不支持的基础源沿原source与Snapshot*。异步解码/尺寸预算通过state的`renderFailureCode=PackageImageDecodeFailed/PackageImageBudgetExceeded`，通用failure仍为原`internalError`，失败不发布latestFrame。详情以[统一API§22](../docs/architecture/backend-api.md#project-image-preview-0990)为准。

本节0.99预览验收未覆盖图片validation/export/measurement。其后0.100图片正式验证须独立`project-image-validation-v1`能力，见§12；本节历史0.99范围未覆盖视频、字体、图片与预设组合、模板候选/固定试样及图片export/measurement。匹配发布d961895（sourceDirty=false）的fresh HTTP final6通过1175请求／9760断言；三基础源普通/透明各4帧、seek、原RuntimePNG/像素及六组失控控制的有限范围见[实施§77](../docs/backend-extraction-progress.md#project-image-preview-0990)。本地611例、Oracle预检和合同形状检查分别登记；浏览器及完整B3/B4/B5/B6仍独立，启动器参数和stdout合同不变。


## 12. 已保存工程图片背景正式验证（0.100.0／合同1.34.0）

本节保留该版本的历史有限来源与验收边界；当前统一Runtime输入见§13。

复用既有validation-capabilities、validation-jobs、result、diagnostics与recovery-result路由。仅当实际Worker独立`project-image-validation-capabilities-v1`协商成功，`ValidationTaskCapabilitiesDto.sourceProfiles`才增加`project-image-validation-v1`；兼容首选`sourceProfile`保持原静态值。图片预览与package validation能力均不能替代；每个实际任务Worker仍重新协商，失败503 `taskUnavailable`／`WorkerImageValidationUnavailable`，field为worker，不回退Host执行。

公共请求仍只含精确`expectedRevision`、`framesPerOutput`与metadata，不接图片bytes/path/profile/grant。Host从ProjectStore已加载的受管Image主背景闭包固定完整bytes，基础仅无preset的static/static-analysis/直接package；两次进入Writer均复核owner、注册session对象身份、revision及ClosePending，准备失败释放未转移的图片/音频引用与reservation。完整输入≤4194272字节（4MiB减32字节KTI1头），嵌套base snapshot≤4MiB，编码原图≤2MiB、单边≤4096、解码BGRA≤16MiB；图片envelope仍为`project-image-preview-v1`，decoder为`memory-image-bgra-v1`。预览envelope的4194280字节上限不能用于验证任务。

固定job输入来源标记为`project-image-validation-v1`，`sourceSha256`覆盖完整内存image envelope。原Runtime执行普通/透明帧计划，完整报告保持256KiB／256 issues／512 occurrences门禁及原始bytes/size/SHA、诊断分页、owner/recovery ACL和cancel/deadline/close排水；报告不包含PNG。闭包缺失/变化为422 `commandNotSupported`／`ProjectImageSourceUnavailable`或`ProjectImageSourceChanged`；输入预算为413 `resourceTooLarge`／`SnapshotSizeExceeded`，上述输入错误field为background。实际解码和尺寸预算失败保留`PackageImageDecodeFailed`／`PackageImageBudgetExceeded`诊断，不制造成功报告。

2026-10-24已从生产基线`1276982`重新构建metadata并将1.35.0生成物写入本目录；默认冻结检查与合成例验证通过。该历史1.35.0为87路径／97方法／481 schemas／91命令，2890例（2199反例）；在完整保留1.34.0的2873例（2188反例）后追加17例图片导出配对与能力边界。fresh HTTP尚未验收。

匹配self-contained Server/Worker发布`dist-7a03863`（源码`7a03863`、`sourceDirty=false`，859生产输入／1006发布文件）的fresh HTTP `http-final1`通过1412请求／10582断言，两次Server exit0、33个观察身份（26个Worker）全部退出、无强制清理，3349文件凭证扫描通过。证据根为`artifacts/project-image-validation-01000-acceptance/`；主manifest SHA-256为`89b55cb0badb98d611d892e73ad8f64c66dec7afa7529eddfeaf3e96ee511f1a`。三基础源普通／透明各4帧、完整报告/诊断/ACL/revision/SSE、错误与输入/报告预算、固定接受bytes、queued/running与六组实际Worker/child控制及一次orderly restart/recovery属于本有限切片。正式validation没有PNG，原Runtime报告Oracle不构成像素验收；损坏图片/维度/decoded三组按实际报告核验，不冒称Oracle匹配。

真实旧0.99预览Worker（`d961895`）的`unsupported-worker/manifest.json`独立通过10请求／476断言、199文件凭证扫描，能力不公布图片验证来源且实际创建拒绝503 `taskUnavailable`／`WorkerImageValidationUnavailable`；该负例不并入主计数。本地去重242/242与二次Writer门禁变异4红／恢复4绿、合同形状、匹配发布HTTP分别登记。详见[实施§78](../docs/backend-extraction-progress.md#project-image-validation-01000)与[计划](../docs/backend-project-image-validation-plan.md)。图片export/measurement、preset/video/font、模板候选/固定试样、完整B3/B4/B5/B6、浏览器/跨平台与性能长片保持独立，启动器参数与stdout合同不变。

## 1.35.0 已保存工程Image背景导出（HTTP待验收）

本节保留该版本的历史有限来源与验收边界；当前统一Runtime输入见§13。

新增独立 `project-image-export-v1` Worker能力，沿既有export-jobs、report、diagnostics与content路由。客户端请求不新增字段；Image任务仅在登记的job input/exportInput中成对记录taskProfile/baseSourceProfile。范围限无预设static/static-analysis/direct-package基础源，package仅无音轨。KIE1完整输入4194272字节、metadata32KiB、图片2MiB/4096/BGRA16MiB、base4MiB、source200000、媒体64MiB、音频16MiB、报告256KiB。生产实现提交1276982已稳定推送，fresh HTTP尚未验收；图片measurement、preset/video/font及完整B3/B4/B5/B6保持独立。

## 13. P1/P2统一输入与编辑合同（0.102.0／1.36.0）

历史1.36.0新增四个公共操作：`POST .../lyrics-import-plans`、`GET /fonts`、`POST /fonts/inspect`、`POST .../edit-previews`；该版合同为91路径／101方法／500 schemas／96工程命令。导入计划只读，返回真实导入报告且不占工程回执。字体检查八个字段均须提供，允许null的字段与省略不同；可选工程上下文校验owner与精确修订；目录和inspect当前读取实际Worker系统字体环境，工程包字体的原Runtime渲染闭包另由固定Runtime输入提供。

创建的`audioAssetId`与`lyricsAssetId`各自可省略或null，四组合均保持revision0和原创建收据；只在提供音频时准备音轨与波形。上传允许原始字节或单文件multipart，按流处理且不要求Content-Length。`capabilities.maxAssetBytes/maxAssetQuotaBytes`必返正Int64或null；null表示该部署维度未配置限额，客户端须保真读取，不能替换为默认固定上限。

`composition.setBackgroundAsset/clearBackground/setBackgroundFit/setBackgroundPlayback`使用现有授权asset；settings/resources返回固定背景配置及受认证内容身份。`project.applyEdits`顺序复用现有数据编辑，一次准备和历史提交，不接受历史命令、资源导入、外部任务或嵌套批处理；响应逐项保留原编辑反馈。固定编辑候选复用预览生命周期，`candidateRevision`及`source.sourceRevision`是相同的不透明candidate身份，`baseProjectRevision`才是精确工程修订。候选测量的previewId/expectedPreviewGeneration/candidateRevision必须同时非null提供，且expectedRevision匹配baseProjectRevision。测量响应的projectRevision为本次固定测量来源身份：候选时等于不透明candidateRevision，普通工程时为十进制工程修订。

统一Runtime输入保完整源指纹和媒体/包资源身份，Host不为结构校验重新读取Worker私有包目录。实际Worker按固定授权完整读取并核验包目录、注册包字体、调用原Runtime/Skia。预览、固定试样、候选、测量、validation和export各自保持能力门禁、预算、取消/deadline/close和资源排水。`advancedRuntimeAvailable`只表示现有Line/Token高级模板闭包可执行，detail的definition仍必返null；静态作者定义继续由`staticAvailable`提供，组合/未知高级保持`advancedUnsupported`，该历史1.36入口不返回高级作者定义；当前完整定义通过§14的definitionFormat:"full"读取与编辑。

`.mktpl`传输默认预算与完整包闭包对齐：archive/expanded各256MiB、单文件64MiB、1024 entries、总保留1GiB；100模板、client2/total8与300秒TTL保持。大字体不会因旧8MiB单文件上限被拒绝。

本节匹配self-contained Server/Worker发布`dist-92c3e84`（源码`92c3e849e56830e5bdd1c17c7e25a5720da9843b`、`sourceDirty=false`）的fresh HTTP `http-final3`通过320请求／2215断言；范围与排水证据见[统一API§23](../docs/architecture/backend-api.md#unified-runtime-input-01020)。历史final1产品分发失败与final2探针eventCursor误比较分别保留，不并入成功计数。

离线合同留档校验通过307份实际JSON响应与91份成功JSON请求，覆盖38操作，13份非JSON/无映射留档跳过、0未配对；该历史1.36.0合同为2974例（2248反例）。候选测量projectRevision的opaque响应合同遗漏已修正，未改变产品或expectedRevision请求。证据根为`artifacts/api-completion-20261024/`；此JSON形状核验不替代原始报告、像素、媒体及排水断言。浏览器/跨平台、完整B3/B4/B5/B6及性能/长片矩阵保持独立。

## 14. P3完整模板与预设工坊（0.103.0／1.37.0）

P3新增包检查和模板试样选项；模板详情与create/replace显式选择 `definitionFormat:"full"` 可传输完整高级定义、包材质、有序配方与固定包引用。原静态分支保持；clips与高级Node预设目标复用既有命令。匹配发布及历史fresh HTTP见[计划§9](../docs/backend-api-completion-plan.md#9-p3-实施记录)。

## 15. P4作者与个人配置库（0.104.1／1.38.1；P4 fresh证据仍归属0.104.0）

N07～N14/E12～E15已接入。草稿读模型返回完整配置及试样选项，源码读取同时校验expectedDraftRevision与sourceRevision。编译省略/null/sync保留200，async返回202并固定draft/source修订；结果沿当前宿主live job读取，SSE/取消复用普通job。N10没有公开恢复结果路由。

作者试样创建复用preview-sessions，source使用 `kind:"authorDraft"`、草稿ID的sourceId、草稿修订的sourceRevision、固定配置源码的authorSourceRevision和 `templateSample:{sampleId,targetKey,materialSlot}`；创建固定 `previewKind:"authorDraft"`、`committable:false`。PUT input保持kind=authorDraft及同一draft ID，允许更新draft/source修订及试样选择，沿expectedPreviewGeneration/generation切换固定输入；project与authorDraft不能相互切换。正式验证接受expectedDraftRevision/sampleId/targetKey/可选materialSlot/metadata，固定640×360、30fps、12秒、每输出360帧；job输入与报告保留作者来源身份，sourceKind为authorDraft、sourceProfile为author-script-sample-v1。帧、诊断、任务取消和报告沿既有读取入口。

个人配置列表/详情以库expectedRevision读取；create/replace/copy/rename/delete共用严格command union与外层metadata/CAS。完整configuration携带parametersJson字符串、相对目标/读取范围、branchInput、UInt64十进制字符串或null的fixedSeed及overlayLayer；source严格选择固定草稿修订、精确个人配置修订或授权包。包检查与模板可引用kind=configuration的固定包身份；create draft也可从精确个人配置建立。保存合法源码/配置不要求编译或运行验证成功，库变更不静默更新模板/工程快照。

详细行为见[统一API§21](../docs/architecture/backend-api.md#backend-author-p4)。P4有限fresh HTTP已通过167请求／1142断言，证据目录`artifacts/p4-author-01040-r1/http-final1`，其产品版本仍归属0.104.0；本节P5勘误说明不把该证据升级为0.104.1新HTTP验收；P5的独立运行记录见[P5审查报告](../docs/backend-api-p5-closure-audit-20261025.md)。浏览器、跨平台与性能范围不由合同冻结扩大。

## 16. CPU/GPU显式执行选择（0.106.0／1.39.0）

`GET /api/v1/render-backends` 沿client鉴权返回启动默认、`cpu`/`gpu`可用性、实际API（`raster`/`vulkan`）、安全设备摘要或稳定不可用原因。该检查在隔离Worker中实际创建设备、绘制/提交/读回，只表示检查时可用；业务Worker仍核验固定选择，正式渲染成功另看帧或任务结果。

所有公开预览创建、工程/作者正式验证、导出和普通场景测量接受可选 `renderBackend`。公开值严格为小写 `cpu|gpu`；`vulkan`、`auto`、空串和其它大小写拒绝。创建/任务省略或null使用启动默认，已有预览输入更新/候选替换省略或null保留原选择；候选测量省略或null使用候选已固定的选择。部署默认仍由启动时的 `MAKO_RENDER_BACKEND=cpu|vulkan` 固定，前端偏好由前端维护，不写工程内容、历史或服务端全局开关。GPU不可用/执行失败不回落CPU。

`PUT /api/v1/preview-sessions/{id}/render-backend` 必须携带 `expectedPreviewGeneration`、非null的 `renderBackend` 和metadata，返回200 `PreviewSessionResponse`。适用于project、authorDraft、template、presetCandidate、editCandidate，保留原固定来源与媒体闭包。相同后端通过CAS后no-op；不同后端准备成功并再次核对身份/generation后才换代、清帧和失效旧输出。准备失败保留原输入/代次/帧，成功后前端采用返回generation重新请求帧；原来源切换限制继续保持。

预览/帧/事件、job input及其validation/export input、验证/导出报告和测量响应回显固定选择。排队/运行任务不随后续偏好变化，恢复读取不重新执行；历史任务、报告或旧适配器缺少选择字段时仍为未知/省略或null，不推断为CPU。详细行为和错误见[统一API§25](../docs/architecture/backend-api.md#render-backend-selection)，分层门禁与有限HTTP证据见[验收报告](../docs/render-backend-api-acceptance-20261026.md)和[实施计划](../docs/backend-render-backend-api-plan.md)。本批HTTP不包含浏览器/跨平台/性能、独立Runtime内容oracle或CPU/GPU位级像素相同结论。

## 17. W8 作者工作流增量（0.108.0／1.40.0）

新增 profile 作者草稿分页发现与显式 claim、完整包文本读取/原子保存、源码修订历史，以及作者草稿和个人配置库的认证 `.mkfx` 下载。claim 保持内容与所有权双 CAS、活动 owner 保护和普通 owner ACL；包编辑保持非文本闭包并支持可选配置原子迁移；历史按内容修订排序而非时间排序。无变化源码保存保留修订和编译摘要，`canCompile` 同时要求参考集与当前源码含 C#。完整模板校验通过既有错误 envelope 返回具体 field/failureCode。详细预算、错误和生命周期见[行为§21](../docs/architecture/backend-api.md#backend-author-p4)，调用顺序见[速查§8](frontend-workflows.md#8-作者草稿和个人配置库闭环)，验收见[修复记录](../docs/backend-w8-authoring-fixes-20261030.md)。
