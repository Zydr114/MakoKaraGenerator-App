# 后端 API v1 未来设计草案

原设计日期：2026-10-11，文档基线 `16b05ae`、产品源码 `f7940d4 / 0.49.14`。2026-10-13整理为未来设计参考。

**当前已实现接口只维护于[统一后端API规范](../../architecture/backend-api.md)。本文件所有路由表、DTO示例及验收步骤均是完整目标，不是当前实现列表；与当前规范不一致时，以当前规范和真实源码为准。** 已移除原开头的多份历史／当前接口列表，避免0.59、0.60与0.64状态混写。历史过程见[实施记录](../history/backend-extraction-progress.md)，当前施工见[B4计划](backend-b4-plan.md)。

配套：[拆分方案](backend-extraction-plan.md)、[源码调研](../research/backend-web-feasibility-report.md)。B3完整剩余与B4有限任务可分别推进；未来草稿／作者、完整资源与几何、完整导出／分析等不能由静态预览能力推导为已开放；当前有限静态任务、0.67独立分析／0.68采用／0.69完整分析静态profile／0.70固定MP3与FLAC分析的实际合同以统一规范为准；分析任务不自动写工程，新profile不表示包脚本／完整资源或音频驱动视觉已开放。0.70.1 Backend自包含固定音频准备有限卫生修复见[实施§25](../history/backend-extraction-progress.md#b4-self-contained-preparation-0701)，无新增公开路由，不扩分析／exportprofile。

## 1. 传输与共同规则

| 项目 | v1 建议 |
|---|---|
| 控制 | HTTP JSON，前缀 `/api/v1`，`camelCase`；具名命令与查询 |
| 事件 | `GET /api/v1/events`，SSE；使用带鉴权头的 fetch 流客户端，实现解析与重连 |
| 大数据 | 图片、音频、帧、产物使用独立二进制响应；不内嵌 JSON Base64 |
| 类型 | 判别字段 `kind`／`type` 为稳定字符串，未知命令拒绝；查询新增可选字段允许旧客户端忽略 |
| 精度 | `timeUs`、64 位 seed、revision、事件／请求序号均为十进制字符串；客户端使用 BigInt 或字符串，不经 JS Number 往返 |
| 时间 | 保持现有整数微秒与半开区间 `[startUs,endUs)`；尚未打轴用明确 `null`，不混同 `0` |
| ID | 工程会话、草稿、任务、资源、稳定领域对象均为不透明 ID；禁止按列表位置寻址编辑目标 |
| 数值 | 有界尺寸与数量可为 JSON 整数；浮点只允许有限值；具体上限由 capabilities 公布 |
| 写入 | 具名 DTO 明确必填／可选／可清空字段；不提供任意属性反射或通用 JSON Patch |
| 错误 | `application/problem+json`，稳定 `code`、字段错误、对象 ID 与关联 trace；人类文案不作为程序判断依据 |
| 长任务 | `202 Accepted + jobId` 只表示已接受；任务查询与终态才表示成功／失败 |

初版实现时由后端 DTO／路由生成 OpenAPI 与客户端类型，并将约束写进契约测试。本草案先固定核心语义；不用两份手写 JSON Schema 与实现长期并行维护。后文路由的 `{p}`、`{d}` 等是 ID 占位符，实际没有花括号。

### 1.1 身份与修订不能混用

| 字段 | 含义 |
|---|---|
| `hostInstanceId` | 服务每次启动新值；重启后的旧会话／命令一律无效 |
| `clientId` | 一次客户端连接身份，关联资源授权、草稿与事件订阅；不能只凭知道 ID 越权访问 |
| `projectSessionId` | 新建／打开／恢复生成新会话；不是工程目录或工程文件中的身份 |
| `projectRevision` | 每次实际工程变更递增，撤销／重做也递增；无变化不递增 |
| `renderRevision` | 已发布有效渲染输入的修订；首版保守失效，不等于 `projectRevision`，并显式标识对应的工程修订 |
| `draftRevision` | 作者／场景草稿的版本，包括非法作者原文的变化；独立于工程修订 |
| `libraryRevision` | 现有 Repository 的索引内容修订，按不透明字符串处理；独立比较并提交 |
| `sourceGeneration` | 某预览上下文的源／目标／seek 代次，服务端发放；拒绝旧渲染结果 |
| `eventSeq` | 一个客户端事件流内的单调序号；用于续传／发现缺口，不是领域修订 |

`ProjectWorkspace.Generation` 继续服务于原工作区换工程门禁，不改造成所有修订的共同计数器。`isDirty` 由服务端依据当前内容与实际落盘快照判断；保存后撤销回已保存对象可以变干净，此时 `projectRevision` 仍增大。读模型同时返回 `isDirty`、`hasPath` 和已保存信息，客户端不自行猜测。

### 1.2 原子提交、重试与一致读取

工程写入在会话串行队列中依次完成：验请求身份／去重 → 校验预期修订 → 定位稳定 ID → 领域校验 → 执行一次历史 → 更新修订与变更摘要 → 发布事件。媒体 IO、编译和渲染不占用该队列；先捕获固定输入，后台执行，回交时重新检查基线。

每个有副作用请求带以下元信息；正文示例从简时也必须遵守：

```json
{
  "requestId": "req-8dce",
  "hostInstanceId": "host-f021",
  "validUntilUtc": "2026-10-11T08:05:00Z"
}
```

服务握手提供时钟与允许重试窗口，建议初值 10 分钟。`validUntilUtc` 不得超出服务允许范围；同一个 `requestId` 的方法、路径、语义载荷和截止时间必须一致。JSON 按确定的规范化规则、二进制按内容哈希比较，multipart 边界不参与业务身份。窗口内返回第一次的结果／jobId，重试不会再插入歌词或再次导出；相同 ID 不同载荷返回 `409 RequestIdReuse`。**先查有效回执，再查 expectedRevision**，避免已成功命令的合法重试被误报冲突。同 ID 并发到达时原子占用执行槽，其余请求等待／查询同一回执，不可各自执行；在副作用发生前完成身份、配额与去重保留。

回执至少保存到原截止时间；请求到期后返回 `409 RequestExpired`，即使回执已经清理也不重新执行。超过配额拒绝新的操作，不驱逐仍有效的回执。服务重启后以 hostInstanceId 拒绝旧请求，不承诺跨崩溃 exactly-once；客户端应恢复并核对工程／产物，不能更换 requestId 自动重放一个结果不明的写入。接受成功的长任务不因请求截止时间到达而取消。

分页读取绑定 `projectRevision` 或草稿／库修订；游标含相应身份与查询条件。服务若已释放该读快照，返回 `409 ReadRevisionExpired`，客户端重新读取，不把两个版本的分页拼成一份数据。会话摘要、修订与 `eventCursor` 原子捕获；先取摘要再以 cursor 订阅，避免读取和订阅之间丢更新。

## 2. 服务、客户端与资源访问

| 方法与路径 | 输入／结果 |
|---|---|
| `GET /health/live` | 仅最小存活信息；不暴露工程／文件名 |
| `GET /api/v1/capabilities` | 引擎／协议／格式版本、功能开关、字体／FFmpeg 可用性、资源配额、服务时间及重试窗口 |
| `POST /api/v1/clients` | 创建 clientId，指定支持的协议版本；返回租约／事件参数及授权范围 |
| `GET /api/v1/clients/{c}/state` | 当前客户端可访问的会话、草稿、任务摘要与 eventCursor，用于重连 |
| `POST /api/v1/clients/{c}/heartbeat` | 续约，返回主机身份；不修改领域数据 |
| `POST /api/v1/assets` | 有大小上限的 multipart 上传，返回 assetId、内容哈希、类型与状态；上传成功不等于已应用工程 |
| `POST /api/v1/file-handles` | 只在已批准的 rootGrantId 内解析相对路径，返回可读／可写句柄；不能自行扩大根权限 |
| `GET /api/v1/assets/{a}/content` | 已授权资源字节；音频支持 Range、ETag 与正确 MIME |
| `GET /api/v1/media/{a}/waveform?startUs=…&endUs=…&pixelWidth=…` | 利用既有 min/max 金字塔按显示宽度返回有界波形数据和内容修订 |
| `POST /api/v1/media/{a}/analysis-jobs` | 创建音频分析任务，结果固定到内容哈希；应用工程时另行检查源身份与修订 |
| `GET /api/v1/fonts` | 后端实际可用字体及回退／缺字诊断信息，不以浏览器字体列表代替 |

客户端创建后，除握手外的受保护请求携带 `Authorization` 与 `X-Mako-Client-Id`。所有资源同时检查授权归属，不把不可猜 ID 当作权限。

初始根授权由启动参数／可信桌面适配器建立。普通浏览器通过上传导入；本机用户也可在显式配置的工程根内打开目录。浏览器系统选择器返回的 `File`／目录内容需要上传，不传其伪路径当服务端路径。Tauri 以后可通过可信启动通道登记文件选择结果，登记接口不暴露为任意网页可调用的授权升级。

工程为多文件闭包，上传路径需保留合法相对结构。先完成有配额的 staging bundle，再原子封装为 bundleId；完整性通过后才能打开。防止 `..`、绝对路径、链接逃逸及解压膨胀，重用当前闭包校验。媒体及已发布闭包以内容哈希固定；若文件可被外部修改，须导入托管副本或检测变化并建立新修订，不能悄悄改变运行中的固定输入。

资源由工程、草稿、任务和读响应持有租约；关闭页面只解除客户端订阅，不提前删除仍被任务使用的文件。未引用上传／暂存包按公布的 TTL 回收，发布产物与诊断报告有独立保留期限及磁盘配额；正在读取的响应保留到读取结束。库／正式工程文件不属于临时资源回收范围。客户端 state 与任务结果明确返回 expiresAt，无法保存完整输出时报告失败或配额不足，不静默清理后返回成功。

建议的目录上传补充入口：`POST /api/v1/import-bundles` 建暂存包，`PUT /api/v1/import-bundles/{b}/entries/{entryId}` 上传事先声明的相对路径条目，`POST /api/v1/import-bundles/{b}/seal` 校验封装，`DELETE /api/v1/import-bundles/{b}` 取消未使用暂存包。所有变更遵守请求元信息，二进制请求放等价请求头。

首期监听 loopback，随机启动凭证通过可信启动通道或本机一次性配对交付，凭证不进 URL／日志。明确允许的 Host／Origin、拒绝宽泛 CORS；开发 Web origin 显式配置。SSE、帧和音频也要鉴权。使用 fetch 读取媒体／帧；直接交给 HTMLAudio 的 Range URL 使用单资源、短期且可续期的票据，不能将全服务 token 放入 query。断流恢复保留实际播放位置。

首期不提供远程账户、多租户、公网监听或任意远程 URL 抓取。访问本地服务的安全边界与未来 Tauri capability 分别处理。

## 3. 工程生命周期与只读视图

| 方法与路径 | 语义 |
|---|---|
| `POST /api/v1/project-sessions` | `kind:create/open/recover`；返回 `202 + jobId`，成功结果包含新 projectSessionId |
| `GET /api/v1/project-sessions/{p}` | 工程摘要、projectRevision、渲染准备状态、dirty、undo／redo、eventCursor |
| `GET /api/v1/project-sessions/{p}/settings` | 画布、背景、音频和输出设置 DTO |
| `GET /api/v1/project-sessions/{p}/lyrics/branches` | 稳定分支 ID 与摘要 |
| `GET /api/v1/project-sessions/{p}/lyrics/lines?branchId=…&cursor=…&limit=…` | 当前修订下分页行内容、元素／注音／时间数据，行数上限受配置约束 |
| `GET /api/v1/project-sessions/{p}/scene` | 节点、槽位、映射、预设实例摘要；大型列表按修订分页 |
| `GET /api/v1/project-sessions/{p}/styles` | 基线、命名预设、对象覆盖及来源；保留继承与显式关闭的区别 |
| `GET /api/v1/project-sessions/{p}/diagnostics?cursor=…&limit=…` | 工程结构化诊断及稳定目标定位；不代替正式运行验证 |
| `POST /api/v1/project-sessions/{p}/commands` | §4 的具名工程命令 |
| `POST /api/v1/project-sessions/{p}/save-jobs` | 保存／另存为当前已提交快照；指定 expectedRevision 和目标句柄 |
| `GET /api/v1/recovery-candidates` | 当前 profile 可恢复条目，描述原工程、时间及恢复来源 |
| `POST /api/v1/project-sessions/{p}/close` | 带 expectedRevision 与 `policy:requireClean/save/discard`，以及明确活动任务处置，必要时返回 jobId |

`create` 至少传名称、音频 assetId、主歌词 assetId、导入选项和必要画布设置，保持当前“音频＋主歌词准备成功才新建”的语义；返回导入报告和媒体状态。后端不要求声卡，但保持内容、字体与正式资源准备门禁。Avalonia 额外完成播放设备适配后才切换旧视图。

`open` 使用 projectDirectoryHandleId 或已封装 bundleId；先按现有精确产品版本／schema 合同检查再创建会话。失败不能清空原会话。服务不理解“当前窗口”；客户端成功后选择切换，是否关闭旧会话由显式请求决定。过渡期后端与 Avalonia 同批发布、共享现有产品版本；Web 构建版本通过协议能力协商，不能擅自改写工程的产品版本。新增后端程序集按仓库规则递增产品版本，但不自动放宽历史文件门禁。

`recover` 返回新会话与恢复诊断，不暗中覆盖原工程。一个工程目录只有一个写入租约；另一个客户端可以经授权附着同一服务会话，或取得只读／冲突结果，不能另建无协调写者。

关闭默认 `requireClean`。请求带 `activeJobs:reject/cancelAndWait`；`save`／`discard` 后也须处理活动任务与资源所有权。进入 closing 后拒绝新写入；保存失败则退出 closing 并保留会话。取消先等待事务安全点，不能杀死 Committing。自动断连不触发 discard；租约过期进入休眠／恢复策略，未保存工程和作者草稿不能因 SSE 掉线而被静默释放。当前作者草稿尚无崩溃恢复承诺，首期如未增加持久化，明确仅在服务进程存活期间保留。

## 4. 工程命令与变更结果

基础命令目录按 B1／B2 起步、B5 补齐。具体枚举必须映射现有领域操作；未实现的能力不在 capabilities 中声明，也不能通过“整棵工程替换”绕过。

| 命令族 | 代表操作 | 原子单位 |
|---|---|---|
| `lyrics.*` | 添加／删除／移动分支与行、文本／注音／元素编辑、设置时间组、清除时间、时间平移、分词／导入 | 一个字段组或一次明确批量操作；新分支＋追加行一次撤销 |
| `project.updateSettings` | 画布、背景、内容时长、输出设置 | 与当前 UI 组提交一致；非法输入不部分写入 |
| `project.setAudio` | 将准备成功的媒体候选绑定工程 | 检查源身份和 expectedRevision；失败保留原音频 |
| `style.*` | 基线／对象覆盖、继承恢复、明确关闭及批量属性应用 | 一次应用全部校验，一次历史；按对象支持范围拒绝非法字段 |
| `scene.*`／`preset.*` | 添加对象、来源映射、应用固定预设、更新固定实例等 | 稳定 ID 与完整闭包，保持未选槽位／时钟及 seed 语义 |
| `history.undo`／`history.redo` | 当前工程历史 | 服务验证可执行性，返回实际影响范围 |

合法请求示例（示例 ID 仅用于说明）：

```json
{
  "requestId": "req-8dce",
  "hostInstanceId": "host-f021",
  "validUntilUtc": "2026-10-11T08:05:00Z",
  "expectedRevision": "41",
  "command": {
    "type": "lyrics.setTimeRange",
    "target": { "kind": "line", "branchId": "branch-a", "lineId": "line-b" },
    "startUs": "1250000",
    "endUs": "2800000"
  }
}
```

此命令同时设置两个端点；清除整个时间用单独 `lyrics.clearTime`，避免字段缺失与清除混淆。元素目标增加 tokenId，组内相关状态通过领域构造保持一致。前端显示秒数时保留未编辑端点的原始微秒，不能读出后统一浮点舍入再回传。

```json
{
  "requestId": "req-8dce",
  "projectSessionId": "project-c",
  "previousRevision": "41",
  "projectRevision": "42",
  "changed": true,
  "isDirty": true,
  "history": { "canUndo": true, "canRedo": false, "undoName": "lyrics.setTimeRange" },
  "changeSet": {
    "areas": ["lyricsTiming", "timeline", "renderInputs"],
    "branchIds": ["branch-a"],
    "lineIds": ["line-b"],
    "removedIds": [],
    "requiresFullRefresh": false
  },
  "renderInput": { "status": "pending", "forProjectRevision": "42" },
  "eventCursor": "105"
}
```

`ChangeSet` 是失效提示及小范围结果，不是新增一套项目事件存储。客户端针对受影响视图按返回修订重新查询；小型已提交对象可以直接随响应返回以减少往返。删除后显式给出 removedIds，选区由客户端稳定回退。未知／复杂操作允许 `requiresFullRefresh:true`，不得为了伪增量漏更新。

无变化响应 `changed:false`，previousRevision 与 projectRevision 相同，不添加历史、不重新构建渲染输入。批量操作先全部校验再一次提交；中间目标失效则整批失败。不把服务端历史分组跨多个不受控网络请求保持打开。拖动可使用客户端瞬时预览／服务草稿，释放时提交一个最终动作。

保存例：用户在 42 发起 save，服务捕获固定快照；随后编辑为 43。save 成功返回 `savedProjectRevision:"42"`、`currentProjectRevision:"43"`、`isDirty:true`。之后 undo 回到那个已保存对象，可能返回 revision 44 且 `isDirty:false`。存储清理警告与项目根已经发布成功分别报告，不因发布后清理警告虚报整个保存失败。

保存只保存已提交工程，沿用现有保存／资源收拢规则，不额外要求通过完整导出或脚本验证。客户端负责先处理本地时间／设置字段组；独立作者草稿不因工程保存而发布。后端查询返回工程与作者各自的 dirty，不能用一个全局“已保存”覆盖两者。

## 5. 场景和作者草稿

| 方法与路径 | 语义 |
|---|---|
| `POST /api/v1/drafts` | `kind:scene/template/scriptConfiguration`，可带工程来源、库项及固定修订；新建 draftId |
| `GET /api/v1/drafts/{d}` | draftRevision、基线、原文／合法值、字段问题、dirty、可执行动作及最近验证状态 |
| `POST /api/v1/drafts/{d}/commands` | 带 expectedDraftRevision；具名编辑、撤销／重做，返回草稿结果及影响范围 |
| `POST /api/v1/drafts/{d}/commit` | 场景草稿提交到来源工程，必须同时检查 expectedProjectRevision／expectedDraftRevision |
| `POST /api/v1/drafts/{d}/library-save-jobs` | 保存／另存模板或配置到库，携带 expectedLibraryRevision；不修改工程 |
| `POST /api/v1/drafts/{d}/close` | `requireClean/discard`；忙碌 IO 按任务安全点收敛，不隐式发布 |
| `POST /api/v1/drafts/{d}/validation-jobs` | 固定合法草稿、目标、读取范围、种子与帧计划；正式宿主验证，返回 jobId |

场景草稿复用 `PresentationEditSession`：当前工程内容身份与草稿 Source 不同时提交冲突，保留草稿供显式重建／放弃；不提供未经设计的自动合并。若撤销恰好恢复原 Source，允许客户端读取新修订后按该 expectedProjectRevision 提交，不能仅凭修订计数增加就永久判定草稿过期。跨工程会话不接受这种恢复。预览必须标注这是草稿源。

模板／配置草稿区分 `rawInputs`、`lastValidValue`、`fieldIssues`。合法协议请求提交非法作者输入时仍可 `200`，草稿修订和作者历史更新，但 `canSave:false`／`canValidate:false`；预览保持最后合法输入并明确标为滞后。协议格式非法才返回 400。不能将一个非法时间工程命令也当草稿接受；工程命令仍要求整体有效。

原始文本解析提取成共享 codec／validator，保留当前语言与精度规则；原文、解析格式和字段类型明确，不依赖服务器当前 Culture 隐式猜测。输入法组合文本留在客户端，确认后再更新草稿；一个完整手势作为一个草稿命令，持续反馈可带仅用于预览的临时值，不把网络分包次数变成 undo 次数。

### 5.1 源码修订保持五个独立动作

| 方法与路径 | 动作及门禁 |
|---|---|
| `GET /api/v1/effect-packages/candidates?cursor=…` | 异步读取已授权候选修订、完整性与来源；不在请求线程同步等待异步 IO |
| `POST /api/v1/drafts/{d}/source-revision-jobs` | 保存指定源码草稿为固定包修订，检查 sourceDraftRevision；不编译／采用 |
| `POST /api/v1/drafts/{d}/compilation-jobs` | 捕获当前源码草稿的不可变候选及内容哈希；允许先编译未保存源码，不由编译隐式保存 |
| `POST /api/v1/drafts/{d}/adopt-source-revision` | 显式采用已经固定保存的 packageRevision；检查当前草稿、同包身份、导出项与原配置兼容性；不新增“必须先编译成功”的门禁 |
| `POST /api/v1/drafts/{d}/library-save-jobs` | 保存配置，要求源选择／参数及库修订门禁通过 |
| `POST /api/v1/drafts/{d}/validation-jobs` | 对允许正式验证的当前固定配置独立运行；不编译并暗中替换其源码 |

编译结果绑定 sourceDraftRevision／内容哈希／编译器合同版本。期间输入发生变化时，结果保留供阅读但标 `stale:true`，不能设置当前草稿为“编译通过”；是否允许编译期间编辑仍按既有交互门禁处理。同包新修订的采用保留配置名称、参数、目标、读取范围和 seed，不能重建成一份默认配置。验证结果同样只对捕获的固定输入有效。

这里列的是独立动作，不是强制流水线：当前 `CanAdoptSourceRevision` 不要求已有成功编译；正式运行验证捕获可合法保存的配置草稿，也不要求先发布到个人库。源码页选择尚未采用的修订只影响提示，正式验证仍使用配置实际绑定的固定包，不能自动切到源码页所选包。保留 [Revisions](../../../samples/TechnicalSpike.App/ViewModels/ScriptConfigurationWorkspaceViewModel.Revisions.cs) 与 [Runtime](../../../samples/TechnicalSpike.App/ViewModels/ScriptConfigurationRuntimeViewModel.cs) 的实际门禁，避免 API 提取时悄悄增加用户步骤。

## 6. 资源库与预设应用

| 方法与路径 | 语义 |
|---|---|
| `GET /api/v1/libraries/{kind}/items?cursor=…` | 目录摘要、libraryRevision、是否可写及诊断；kind 包括模板／配置及实际支持的库 |
| `GET /api/v1/libraries/{kind}/items/{id}` | 固定项及闭包信息，避免列表请求反复读全部大资源 |
| `POST /api/v1/libraries/{kind}/import-jobs` | 已上传／授权包的导入，冲突策略显式，带 expectedLibraryRevision |
| `POST /api/v1/libraries/{kind}/items/{id}/delete` | 明确目标与预期库修订，复用既有删除规则 |
| `POST /api/v1/project-sessions/{p}/commands` | `preset.apply` 等以固定库项／修订和明确目标应用；不把动态“最新版”持久化进工程 |

写入通过现有 Repository 的修订检查、租约和发布流程；API 不接受全库替换数组来规避其他编辑者的变化。库冲突保留作者草稿，并返回实际修订供刷新。列表来自共享只读目录，明确 stale／invalid 状态；读取失败可展示最后成功目录，但按原门禁禁止新的错误应用／更新，已经固定在工程里的实例不自动升级。

## 7. 预览会话与帧协议

| 方法与路径 | 语义 |
|---|---|
| `POST /api/v1/preview-sessions` | 指定 source、输出模式、尺寸及预览用途；返回 previewId、sourceGeneration、limits |
| `PUT /api/v1/preview-sessions/{v}/input` | 带 expectedSourceGeneration，重新绑定固定工程／草稿修订或目标尺寸／模式；产生新代次 |
| `POST /api/v1/preview-sessions/{v}/frame-requests` | 指定 generation、requestSeq、timeUs、`intent:playback/seek`；返回接受状态，不等待完整渲染 |
| `GET /api/v1/preview-sessions/{v}` | 当前源、代次、最新请求／已发布帧、忙碌／失败、诊断游标；事件丢失时可重同步 |
| `GET /api/v1/preview-frames/{f}/content` | 不可变 PNG；失效返回 410，客户端请求新帧 |
| `GET /api/v1/preview-sessions/{v}/geometry?frameId=…` | 同一帧的逻辑画布几何、稳定对象 ID、命中信息与变换；不可另取“当前时间”混合帧 |
| `GET /api/v1/preview-sessions/{v}/diagnostics?cursor=…&limit=…` | 当前执行代次的完整分页诊断 |
| `POST /api/v1/preview-sessions/{v}/close` | 停止接收请求、取消／等待、必要时终止 Worker，释放帧租约 |

source 使用判别联合，例如 `{kind:"project",projectSessionId:"project-c",projectRevision:"42"}` 或 `{kind:"draft",draftId:"draft-d",draftRevision:"7"}`。预览用途区分正式成片与现有基础布局／作者试样；用途受服务 capabilities 和领域能力限制，客户端不能把草稿试样当导出确认。

若 42 的渲染输入还在准备，接受请求并标 `preparing`，只从指定源修订出帧；不能悄悄返回 41。先将不可变源绑定到预览租约，再离开工程串行队列。项目后续变化通过显式 input 更新重新绑定；旧源在租约内保持固定。

示例请求：

```json
{
  "requestId": "req-frame-26",
  "hostInstanceId": "host-f021",
  "validUntilUtc": "2026-10-11T08:05:00Z",
  "sourceGeneration": "3",
  "requestSeq": "26",
  "timeUs": "180000000",
  "intent": "playback"
}
```

每个 previewId 的 requestSeq 由单一控制客户端递增。HTTP 乱序到达时只接受更高序号（重复按回执处理），其他客户端不能争夺同一个预览的播放头。playback 只允许时间不后退；回退明确使用 seek。seek 以预期代次原子生成新代次，客户端拿到结果后再发其后的 playback；在途旧代次请求拒绝，不猜测“谁更新”。

正常播放新时间覆盖待处理槽，不取消执行中帧。同代次中允许显示已完成但稍落后的时间帧，并保证实际显示 requestSeq 不倒退；强制只接收“最后一个已发请求”的帧会在慢机器连续播放时饥饿。换源／resize／seek 则立即拒绝旧代次帧。客户端可以保留旧图作为明确标示的等待态，但不更新其时间／命中元数据冒充新帧。

事件示例：

```json
{
  "type": "preview.frameReady",
  "hostInstanceId": "host-f021",
  "eventSeq": "108",
  "previewId": "preview-e",
  "sourceGeneration": "3",
  "requestSeq": "26",
  "source": { "kind": "project", "projectSessionId": "project-c", "projectRevision": "42" },
  "renderRevision": "18",
  "timeUs": "180000000",
  "frameId": "frame-f",
  "width": 960,
  "height": 540,
  "contentType": "image/png",
  "alphaMode": "straight",
  "contentUrl": "/api/v1/preview-frames/frame-f/content",
  "diagnosticsCursor": "diag-3-12"
}
```

内部仍按现有预乘 BGRA 帧合同，PNG 边界正确转换为带直乘 alpha 的无损图像；固定色彩／透明输出语义并做像素等价测试。帧元数据还应包含逻辑画布尺寸及显示缩放映射，用于 geometry 命中。

每会话最多一个执行中请求、一个待处理请求，发布帧按数量与字节双重上限保留（建议初值两帧）；像素不进入 SSE 回放缓冲。慢客户端、停止消费或异常断流都不能积压无限帧；GET 开始读取后持有帧引用直到响应结束，即使目录淘汰也不能释放底层内存。帧保留时间／尺寸／请求频率由 capabilities 公布，410 是正常可恢复情况。

预览诊断只发布增量摘要及游标，不在每帧复制全历史。保持来源、重复发生次数和完整报告，不按“文本相同”吞掉真实多次诊断；完整诊断落有界存储或任务产物，超额显式报错／终止，不能默默删掉后声称报告完整。

## 8. 长任务、进度与取消

除前述创建入口外，提供：

| 方法与路径 | 语义 |
|---|---|
| `POST /api/v1/project-sessions/{p}/validation-jobs` | 固定工程快照与帧计划，正式普通／透明验证 |
| `POST /api/v1/project-sessions/{p}/export-jobs` | 固定工程快照、输出设置、目标句柄或下载产物模式，返回 jobId |
| `GET /api/v1/jobs/{j}` | 输入身份、排队／执行／终态、stage、进度、canCancel、结果与诊断游标 |
| `POST /api/v1/jobs/{j}/cancel` | 幂等取消请求；已终态返回原状态，不把 succeeded 改成 cancelled |
| `GET /api/v1/jobs/{j}/diagnostics?cursor=…&limit=…` | 完整结构化诊断分页，游标绑定任务执行代次 |
| `GET /api/v1/jobs/{j}/artifacts` | 已发布可下载产物摘要、长度与哈希 |
| `GET /api/v1/artifacts/{a}/content` | 授权下载、支持 Range；不返回任意文件读取能力 |

建议任务状态：`queued → running → succeeded/failed/cancelled`；接受取消后为 `cancelRequested`，释放完成才 `cancelled`。写入／导出的最终发布使用 `committing`，此时 canCancel 为 false。进程中断恢复后只能记 `interrupted` 或经实际检查恢复终态，不能凭“进度 100%”认定成功。主服务保存最小任务元数据／终态回执与输出身份，首期不承诺自动续跑；崩溃落在发布边界时核对事务产物后再报告，未知保持未知。

导出 stage 映射现有 `Validating/Probing/Rendering/Muxing/Verifying/Committing/Completed`；progress 提供帧数、总帧数、耗时与可选 ETA。百分比只是当前阶段进度，rendering 100% 不是整项成功。最终结果必须在编码、验证、资源释放、最后取消边界与产物发布之后产生。

请求示例：

```json
{
  "requestId": "req-export-a1",
  "hostInstanceId": "host-f021",
  "validUntilUtc": "2026-10-11T08:05:00Z",
  "expectedRevision": "42",
  "output": {
    "kind": "opaque",
    "destination": { "kind": "downloadArtifact", "fileName": "sample.mp4" }
  }
}
```

工程中已有的帧率／尺寸／时长作为默认值；覆盖必须使用明确合法 DTO，具体编码能力由 FFmpeg 探测后公布。透明输出、音轨和固定包语义沿用既有 Export 用例。写指定文件需要可写目标句柄与显式覆盖策略；下载模式写服务托管目录，不等价于保存整个工程。

接受任务时冻结 sourceRevision、输入闭包、配置、随机种子、目标与资源租约；之后编辑只影响新任务。音频分析结果若需应用回工程，再发一个带 expectedRevision 的命令，不能因任务完成自动覆盖新音频。

HTTP RequestAborted 只影响接受前的请求准备／响应，不直接成为已经接受的领域任务 token；页面关闭／SSE 重连不取消导出。队列与 Worker 有全局及每会话配额；超限返回 429 与 retry 信息，不无界 Task.Run。

取消处置分阶段：排队任务可移除；运行脚本先协作取消再按时限终止其 Worker；保存 IO 已进入不可安全撤回阶段则返回 `409 OperationNotCancellable` 与当前 job；提交期间拒绝取消并等待最终查询。服务关闭先停止新任务与编辑，再收敛 Worker／写入和恢复记录，最后释放资源。

## 9. 事件、断连与恢复

SSE 每条消息使用 `id: eventSeq`、`event: type` 与 JSON data。典型类型：

| 类型 | 内容 |
|---|---|
| `project.changed` | 前后修订、ChangeSet、dirty／历史；不反复发送完整工程 |
| `project.saved` | 实际保存修订、当前 dirty、路径显示标签与非致命警告 |
| `renderInput.ready`／`renderInput.failed` | 固定工程修订的投影准备结果，不能发布晚到的旧结果为当前结果 |
| `draft.changed`／`library.changed` | 对应独立修订、问题与能力变化 |
| `job.progress`／`job.completed` | jobId、状态／阶段／计量、终态结果链接 |
| `preview.frameReady`／`preview.failed` | 当前预览代次的帧元数据或错误，无像素主体 |
| `resyncRequired` | 回放窗口不足或主机变化，客户端重新读取状态 |

每个客户端只有一条汇总事件流，明确订阅自己有权访问的会话／任务。事件流采用有界缓冲、有限重放；进度可合并，领域变更和终态不能无声丢弃。慢客户端越界时发送 resyncRequired 或关闭流，客户端取 state 和各资源权威查询再连接。不能只靠事件保持“唯一一份结果”。

重连通过 Last-Event-ID 或等价 after 参数继续；hostInstanceId 改变则丢弃旧会话引用并走恢复流程。网络断连保留任务与租约宽限；过期资源请求给明确 410，不能把同一个 ID 重新分配给另一个工程。服务端恢复工程、作者草稿与任务各自按其持久化能力报告，不能承诺恢复未保存的全部历史。

## 10. 错误、诊断与 UI 行为

```json
{
  "type": "urn:makokara:problem:revision-conflict",
  "title": "工程已发生变化",
  "status": 409,
  "code": "RevisionConflict",
  "requestId": "req-8dce",
  "projectSessionId": "project-c",
  "expectedRevision": "41",
  "actualRevision": "42",
  "fieldErrors": [],
  "traceId": "trace-97"
}
```

| HTTP／code | 客户端处理 |
|---|---|
| 400 `InvalidRequest` | 协议格式／未知命令／数值表示错误；不盲目重试 |
| 401／403 | 重新握手或说明无权限；不得自动扩大文件授权 |
| 404 `TargetNotFound` | 目标不存在或不可见，按授权策略避免泄漏信息 |
| 409 `RevisionConflict/DraftStale/LibraryConflict` | 保留输入，刷新对应状态并由用户重新提交；不静默换 expectedRevision 覆盖 |
| 409 `RequestExpired/RequestIdReuse/OperationNotCancellable/ProjectBusy` | 按具体原因处理，不能统一当网络失败重试 |
| 410 `SessionExpired/FrameExpired/ArtifactExpired` | 获取新会话／帧／产物；不能复用旧 ID |
| 413／429 | 降低载荷／等待配额，返回 limits 和可用 retry 信息 |
| 422 `DomainValidationFailed/UnsupportedProjectVersion/MissingResource` | 合法 JSON 不满足业务门禁；定位字段／资源，保留输入 |
| 500／503 | 服务错误或 Worker 不可用，返回 traceId 与结构化原因；不暴露完整敏感路径或堆栈 |

任务接受后发生的错误记录在 job 终态中，不能再用原 HTTP 响应码表示。诊断项至少含 severity、稳定 code、参数、目标 ID、包／导出／源码位置、实际发生时间或帧以及 occurrence 信息；是否阻止提交／导出由后端策略给出，客户端只渲染。

本地化主要由客户端 code＋参数完成，服务提供可读 fallback。原有成功反馈、作者错误和工程错误仍分别归属各操作；旧请求晚到的失败不能覆盖新工程状态。后端预检与正式运行验证为权威，不依赖客户端“上一次预览没报错”来放行导出。

## 11. 最小验收会话与后续扩展

第一条独立 API 验收路径（B2）：

1. 获取 capabilities、建立客户端，上传音频／歌词或拿到已授权句柄。
2. 创建工程任务，等待成功并读取会话摘要；确认新工程未保存。
3. 提交一组时间；相同请求重试不重复历史，过期修订请求被拒绝。
4. 撤销／重做，确认原整数微秒、稳定 ID 和 dirty 正确。
5. 发起保存后继续编辑；保存完成只标记捕获快照，当前仍 dirty。
6. 显式处理未保存内容并关闭；重开已保存目录核对内容、资源与版本门禁。

后续分别追加预览持续播放／seek／乱序／透明像素、真实短片导出与失败取消、作者两条完整制作路线。用同一夹具比较直接用例与 HTTP 结果，包括内容、历史、诊断和固定源身份；不只检查 HTTP 200。

v1 暂不承诺：任意工程对象整体上传替换、自动多人合并、每帧 WebSocket／WebRTC、GPU 渲染、任意脚本检查点、后台项目全部历史的崩溃恢复、远程恶意脚本执行，以及完整跨平台发布。需要时新增能力并通过协商显式开放，不让旧客户端猜测支持情况。

本草案描述完整目标，不能作为当前路由、DTO或已完成状态的依据。唯一当前规范为[backend-api.md](../../architecture/backend-api.md)；0.60.0生命周期／持久化、0.64.0静态Worker／HTTP预览及0.65.0固定静态正式验证均已有各自有限出口，历史切片见[实施记录](../history/backend-extraction-progress.md)。完整B3、B4／B5、OpenAPI／客户端类型及完整Web产品按各自计划接续，不由本草案提前宣称交付；B3完整剩余不阻止B4的独立切片继续。
