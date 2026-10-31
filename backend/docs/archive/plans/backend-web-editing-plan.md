# Web 后端编辑能力接续方案

2026-10-16接续：用户确认现有前端编辑及撤销／重做已验证、后端接口无误，并授权继续补齐编辑API。[静态样式底层读写](backend-static-style-api-plan.md)已交付0.83.0／合同1.8.0；用户补充明确接续模板库与显式分配，动画／布局与场景另批；原已交付切片及版本见下文。用户反馈不扩写为未列出的前端高级功能或完整B3/B4完成。

日期：2026-10-16。用户已授权继续后端，按[审查方案 §6](backend-review-and-web-contract-plan.md#6-建议调整后的批次)推进，主要编码交子代理；Codex 负责后端合同、审查、真实 HTTP 验收及逐步提交推送，Claude 负责独立前端仓库。

**本文保留各切片候选与实施口径，不替代已实现 API 清单。** 当前S1/S2时间/S3/G11已完成有限后端出口（0.82.0／合同1.7.0，实施§39）。 当前生产合同仍以 [web/api-contract.md](../../../web/api-contract.md) 为准。各切片完成后单独更新合同、版本、证据与状态；W0 浏览器可与后端 S1 并行，不把前端回复或 mock 当作后端验收。

## 1. 顺序与边界

| 切片 | 交付 | 验收 |
|---|---|---|
| S1 | 来源列表、正文/注音/时间完整窗口、标量设置读取 | 稳定 ID、原值、精确修订、有界分页、权限及真实 HTTP |
| G3 / S2 时间 | 按 type 分型；起止打点、元素清除、微调、整行平移、元素时间批量提交 | 一次请求一次历史、无变化保 redo、回执先于修订、整批原子拒绝、保存重开 |
| S2 波形 | 既有准备结果的同源、有界、授权窗口 | 与准备结果一致、不同修订/来源不混读、租约及真实 HTTP |
| S3 | 正文、注音、增删复制行、再次导入、标量设置；继而来源管理、元素拆合/重排/规则重切 | 编辑→查询→撤销/重做→保存重开；新 ID、结构反馈、非法输入拒绝 |

公共 package、预设/媒体/字体、场景工坊与作者、完整 B3/B4、全量/性能/长片及人工平台矩阵保持原独立范围。G7/G8/R1 已登记，不把它们的修复混入编辑切片。Web 后端不以启动 Avalonia 为验收前置。

## 2. S1 候选合同

新增三个认证 GET；不需要副作用回执，不改变工程、历史、dirty、redo 或事件游标。所有读取在 `ProjectSession.Writer` 内核对当前工程修订并完成投影。

| 路径（均位于 `/api/v1/project-sessions/{id}`） | 查询 |
|---|---|
| `/lyrics/branches` | `expectedRevision` 必填；`offset=0`；`limit=100` |
| `/lyrics/window` | `branchId`、`expectedRevision` 必填；`offset=0`；`limit=100` |
| `/settings` | `expectedRevision` 必填 |

`expectedRevision` 是非负十进制 Int64 字符串；与命令修订同作用域。分页参数是十进制整数，`limit` 为 1～100，`offset` 为 0～`totalCount`，允许末尾空页。重复参数、缺字段、负数、溢出及越界为 400 `invalidRequest`；修订过期为 409 `revisionConflict`，即使曾经 undo 回相同内容仍不能复用旧修订。无会话或跨 client 均为中立 404 `sessionNotFound`；不存在来源为 404 `commandRejected`，沿用旧歌词读取语义。

来源响应包含 `projectSessionId/projectRevision/primaryBranchId/offset/totalCount/nextOffset?/branches`。每个来源提供 `id/name/role/language?/timingMode/lineCount`，role 为 `primary/translation/annotation/custom`，timingMode 为 `independent/followPrimary/none`。

歌词响应包含 `projectSessionId/projectRevision/branchId/offset/totalCount/nextOffset?/lines`。行包含 `branchId/id/text/time/timingStatus/primaryLineId?/tokens/rubyAnnotations`；元素包含 `id/text/time/timingStatus`。行正文保持权威原文，不拼接 token 替代；`time:{}` 表示缺失，`startUs:"0"` 是合法零起点。时间状态为 `missing/imported/estimated/edited/confirmed/invalid`。时间端点继续 Int64 字符串。FollowPrimary 保留原 `primaryLineId` 与来源策略，不把派生时间写成独立原时间。

注音保留 `id/baseTokenId/text/language?/relativeScale/gapPx/alignment`；alignment 为 `center/start/end`，比例与间距为 JSON number。注音跟随基础元素，没有独立发音时间。此切片不开放样式或场景对象编辑。

设置响应包含 `projectSessionId/projectRevision/name/canvasWidth/canvasHeight/frameRateNumerator/frameRateDenominator/backgroundRed/backgroundGreen/backgroundBlue/backgroundAlpha/outputDurationUs?/audioDurationUs`。新增时长字段统一 Int64 字符串；省略的 `outputDurationUs` 表示尚未确定，导出前必须显式设置，不改旧 CompositionDto 与摘要中 number 例外。

窗口最多 100 行/来源；每次投影同时限制 1 MiB UTF-8 序列化字节及 20000 个来源/行/元素/注音条目。超预算为 413 `resourceTooLarge`，不得截断单行或悄悄漏掉元素；可减小 limit 重试，单行本身超预算仍明确拒绝。字节计数遇限立即停止，不先分配完整超大 JSON。原 `/lyrics/lines` 保持兼容，不改成新分页协议。

新增 capability：`project-sessions.lyrics.branches.read`、`project-sessions.lyrics.window.read`、`project-sessions.settings.read`。拟作为兼容新增交付：产品 0.76.0，合同 1.1.0，HTTP 协议仍 `"1"`，工程/歌词/schema/SDK 不变。

## 3. G3 / S2 时间候选合同

继续使用 `POST .../commands` 与原顶层 `expectedRevision/requestId/hostInstanceId/validUntilUtc/command`。旧五命令 JSON、已知无关 nullable 字段显式 null、回执规范化及错误语义保持；新增命令按 type 接受各自独立字段，拒绝未知和跨 type 字段，不继续扩充无约束可空字段袋。未知 type 继续返回 `commandNotSupported`。

| type | command 的其他字段 |
|---|---|
| `lyrics.stampTokenStart` | `target:{branchId,lineId,tokenId}`、`playheadUs` |
| `lyrics.stampTokenEnd` | 同上 |
| `lyrics.clearTokenTime` | `target:{branchId,lineId,tokenId}` |
| `lyrics.nudgeTokenTime` | 同上，另加 `frameDelta` 整数 |
| `lyrics.shiftLine` | `target:{branchId,lineId}`、`deltaUs`，禁止 tokenId |
| `lyrics.setTokenTimes` | `changes:[{target:{branchId,lineId,tokenId},startUs,endUs}]` |

新微秒均为有符号十进制 Int64 字符串；稳定 ID 非空且最多128字符。batch 为1～1000项，重复目标拒绝，任一输入或领域校验失败均不提交。batch 保留现有 `CommitTokenBatch` 的领域校验与无媒体上界语义；单元素端点、微调及平移继续使用服务端真实准备音频时长，客户端不能伪造时长。

打点保留桌面共享用例：负播放头取0，超过已知音频取末尾；起点缺少可用终点时补一帧并记 `estimated`，无法容纳则拒绝；终点确认写 `edited`，即使端点相同但状态改变仍是真变化。这是既有明确业务语义，非通用静默纠正输入。

一次 HTTP 确认动作最多一次历史，前后断开自动合并，不跨请求悬挂 BeginGroup。无变化不增加历史、修订或事件，并保留 redo；undo/redo 的当前修订仍单调递增。同 requestId 的原载荷/截止时间重试取原回执，变更任何派生字段均是 `requestIdReuse`，不能被基类序列化吞掉。所有原子编辑都在原唯一写者闸门内调用共享 Application 用例。

响应继续 `ProjectCommandResponse`；时间 changeSet 沿 `lyricsTiming/timeline/renderInputs` 与受影响稳定 ID。stamp start/end新增可空 `timingResult:{estimatedPlaceholder:boolean}`，由原Application真实结果生成；其他及旧命令省略timingResult。新六命令领域拒绝追加稳定PascalCase `failureCode`：Untimed、InvalidRange、NudgeOutOfBounds、BeyondContentDuration、DomainValidationFailed、TargetStale等，code保持commandRejected，field改为实际command.target/playheadUs/frameDelta/deltaUs/changes路径、旧命令错误wire不变。400 invalidRequest用于字段/数字/目标形状；409 revisionConflict；422 commandRejected用于不存在目标及领域拒绝，commandNotSupported用于未开放type。

元素时间不自动扩展既有行范围，必须满足原领域的有效行边界及前后元素不重叠/不倒序；越界由DomainValidationFailed拒绝。batch按给定顺序逐项构造candidate，任何中间步骤不合法也整体拒绝，不重排用户输入。整行shift明确同时平移行与已定时元素，未定时元素保持缺失。完整changeSet默认JSON超过16KiB则保Areas、清空全部ID数组并requiresFullRefresh=true，避免64KiB回执超限；正常小结果保持精确ID，不返回截断清单。实际undoName：start/end=`token.stamp`，clear=`token.time.clear`，nudge/batch=`token.time`，shift=`line.shift`。结构及选择反馈在S3候选另定。本步拟产品0.77.0／合同1.2.0，HTTP协议仍1。

## 4. S3a 正文、行操作与标量设置候选合同

继续同一commands端点/修订/回执；新派生DTO严格限定type字段，一次HTTP动作一次历史，正文不trim或Unicode规范化。已交付产品0.78.0／合同1.3.0，证据见实施§34。

| type | 业务字段 | 历史名 |
|---|---|---|
| lyrics.setLineText | 行target、text必填非null，可空串 | lyrics.set-line-text |
| lyrics.setRubyText | 元素target、text必填非null，空串删除该基础字注音 | line.ruby |
| lyrics.addLine | branchId，afterLineId可省略/null表示末尾追加 | lyrics.add-line |
| lyrics.duplicateLine | 行target | lyrics.duplicate-line |
| lyrics.removeLine | 行target | lyrics.remove-line |
| composition.setCanvasSize | width、height，正整数，保留奇数尺寸 | composition.canvas |
| composition.setFrameRate | numerator、denominator，正整数，沿原GCD约分 | composition.frame-rate |
| composition.setBackgroundColor | red、green、blue、alpha四个必填0..255 JSON整数 | composition.background |
| composition.setOutputDuration | durationUs必填，null为尚未确定（导出前阻断），或正Int64微秒字符串；缺字段400 | composition.duration |
| composition.useLyricsEnd | 无 | composition.duration |
| composition.useAudioDuration | 无，用服务端真实准备时长，不接受客户端伪造值 | composition.duration |

正文沿原Unicode重切和ID分配，只保留公共前后缀内完整元素的身份/时间/样式；两端均变时整个中间区域重切，不按任意相同中间文字匹配；注音保身份和全部原元数据，无独立发音计时。新增空行未定时、无token；副本紧接原行、按原有效行时长后移并复制展示覆盖，保原状态/样式/主行关联/注音；后行重叠只报告overlapsNext，不自动修正，也不额外加媒体上界。删行沿原展示清理及悬空关联/注音警告规则。useLyricsEnd沿原Core有效末端过滤，无有效末端为no-op；音频时长不可用422 MediaDurationUnavailable。

五种歌词命令尾部可空feedback：selection?={branchId,hasSelection,lineId?}，缺selection表示不建议改变选择，hasSelection=true有lineId，false省略lineId表示清空该来源选区。新增/复制选新行，删除选真实相邻行或清空；正文/注音不强制选择。另含createdLineId?/deletedLineId?/insertedIndex?/overlapsNext?，正文含原用例preservedTokenCount/resetTokenCount（no-op为0/0）。不提供巨型token/ruby ID差集或臆造复制映射；相关正文/结构changeSet保守requiresFullRefresh=true并带必要来源/行ID。

桌面输入及共享用例原本没有低文本长度上限。新增中立Prepare＋CommitPrepared接缝：一次纯准备产生固定不可变candidate/反馈/ID，提交核workspace引用、generation与原Project引用并一次历史；桌面原入口复用这套实现，不受HTTP预算。Hosting在真正提交前，用S1同一投影预算校验受影响产物单行limit=1；若单行会超过1MiB/20000项，HTTP413且不改工程/历史/修订/dirty/redo，不生成第二轮ID或执行后回滚。删除超大旧行仍可修复。HTTP正文整体沿既有有界请求体，不另造低字符截断。

400用于字段/类型/必填/null/数值形状，409修订冲突，422 commandRejected带原Application稳定failureCode（如TargetStale、IdentifierExhausted、DomainValidationFailed、InvalidCanvasSize、InvalidFrameRate、InvalidOutputDuration、MediaDurationUnavailable），413用于完整产物预算。相同正文/设置、重复清注音等no-op保持redo。后续S3b来源管理、固定资源再次导入/追加、元素拆合/重排/重切各子片再冻结，不提前记已实现。

## 5. S3b来源管理候选合同

已交付0.79.0／合同1.4.0，证据见实施§35；四type仍使用commands的精确修订/回执/一次动作历史：

| type | 字段 | 历史名 |
|---|---|---|
| lyrics.addTranslationBranch | name必填、language可省/null | branch.add |
| lyrics.configureBranch | branchId/name/language/role/timingMode全部必填；language可null清除 | branch.configure |
| lyrics.removeBranch | branchId | branch.remove |
| lyrics.linkPrimaryLine | branchId/lineId/primaryLineId全部必填，primaryLineId可null解除 | branch.configure |

role/timingMode沿S1已知string enum。Add沿原业务Trim名称，创建空Translation/FollowPrimary来源；Configure保输入原名、不做语言归一化，保持StyleOverride和既有Presentation，不开放其修改。主来源不能删除或改为其他角色，非主来源不能冒充主来源；TimingMode沿原Core合法枚举及PresentationValidator验证，主来源不额外固定为Independent；link目标必须非主来源行，新非null关联必须是真实主来源行。原相同悬空关联的no-op与警告保持，不因HTTP新增自动修复。

来源使用纯Prepare→实际写后修订的S1单来源/受影响单行预算→Commit；固定新ID只分配一次、提交核workspace/gen/原Project引用，借用同一ProjectEditSubmission处理展示删除引用清理与验证，不复制规则或临时工作区。删除不检查被删大来源，允许修复旧不可读数据；必要反馈同时须符合64KiB回执，拒绝前不提交。

Add/Remove真实变化时返回可选branchResult:{selectedBranchId}，分别为新来源/真实primaryBranchId；Configure/Link/失败/no-op省略。结构changeSet保守完整刷新，不截断ID。400为字段/必填/null/enum错误，409修订冲突，422 commandRejected携原LyricBranchEditErrorCode的failureCode与实际输入field，413为完整读投影/回执预算；旧命令wire不改。

## 5.1 S3c元素拆合、重排与重切候选合同

已交付0.80.0／合同1.5.0，证据见实施§36；五type仍用commands的精确修订、稳定目标及原回执；所有目标ID非空白、最多128字符。原Core算法为业务权威，一次动作最多一次历史，no-op保redo。

| type | 业务字段 | 历史名 |
|---|---|---|
| lyrics.splitToken | 元素target | line.split |
| lyrics.mergeToken | 元素target，与下一元素合并 | line.merge |
| lyrics.reorderToken | 元素target、destinationIndex必填Int32、reassignTimeSlots必填bool | line.reorder |
| lyrics.resplitLine | 行target、segments必填string数组、estimateTimes必填bool | line.split-rule |
| lyrics.resplitBranch | branchId、rule必填string enum、estimateTimes必填bool | lyrics.split-rule |

split要求原元素有效时间且至少两个Unicode文本元素，否则no-op；按Unicode元素数floor(n/2)分，原Int128 ties-to-even分配时间，左元素保ID/样式、右元素新ID，两个状态estimated。merge最后一个为no-op；合并下一元素、左ID/样式保留，时间有缺失则missing，否则取并集，同状态保留/不同为estimated；原注音悬空规则保持，不自动重绑。

reorder同位置no-op，正文按新元素顺序拼接。reassignTimeSlots=false沿原领域顺序验证，非法422；true把原合法时间槽依次赋给新顺序的已定时元素并标edited，未定时仍缺失。resplitLine要求非空片段精确拼回原正文且均为Unicode元素边界，空行允许[]；沿原保留ID、首段继承和显式估算逻辑。resplitBranch rule为none/mixed/perTextElement/latinWords/byPunctuation/byWhitespace；none/空来源no-op，整来源原子提交，不逐行发历史。

可选tokenResult只用于这五type：changedLineCount为真实变动行数，selection?={branchId,lineId,hasTokenSelection,tokenId?}。split/merge真变化选实际元素；resplitLine真变化选该行并hasTokenSelection=false清元素；reorder/branch/no-op省略selection。preservedTokenCount?/createdTokenCount?/estimatedTokenCount?仅split/resplitLine/resplitBranch用原统计（no-op为0），merge/reorder省略，不伪造统计。无巨大映射数组。结构changeSet带四内容区域/必要来源行ID并完整刷新，branch不枚举所有行ID。

纯Prepare固定candidate/ID→宿主预算→核workspace/gen/source一次Commit；桌面旧入口复用原规则，不套HTTP限制。segments在JSON数组长度检查后才分配字符串数组，超过20000项413 command.segments；其余形状/缺失/null/非string400，空片段/拼接/Unicode边界非法422 InvalidTarget。完整最终单行S1投影仍≤1MiB/20000项；resplitBranch增加本次变更行完整单行投影累计16MiB资源预算，超限413、整批无变化，可逐行操作。

为在分配大批片段/ID前执行预算，采用中立流式Unicode枚举与唯一原拆分规则，创建片段/ID前可选回调；旧两参数API无预算且共用同一算法，不按旧合并元素字符数猜最终token数。手工segments以原字符串span及流式边界验证，不构造巨大Concat/全边界HashSet。准入固定segments副本供回执和执行共用。不是恢复全量或性能优化范围；以固定旧0.78 Core程序集与新算法独立反射对照六rule/Unicode输入，另做原Core和编辑回归。

400形状、409修订、422 commandRejected带原稳定failureCode及真实command.*字段、413资源预算；预算拒绝保历史/dirty/redo/修订/事件。后续再次导入/追加另冻结。

## 5.2 S3d再次导入与粘贴追加候选合同

已交付0.81.0／合同1.6.0，证据见实施§38；两type同commands、无新job类别，精确修订/回执/一次动作历史保持。HTTP只接受本client授权上传资源，不接受本机路径；现上传16MiB上限及旧create MIME表不变。

- `lyrics.importAsset`：assetId和destination必填；options可省，若出现须object，含importerId?/encodingName?（可null，未知沿原回退自动检测）、splitRule（默认none，出现须合法string enum）及estimateSplitTimes（默认false，出现须bool）。destination为严格union：existing{kind,branchId}或newBranch{kind,name,role}，新角色仅translation/annotation/custom。
- `lyrics.appendText`：text与destination必填；destination为existing{kind,branchId}或newTranslation{kind,name}。不允许其他分支字段混用。

import existing完整替换行，保来源名/角色/时间策略/样式，未知导入语言按原回退；newBranch沿原Trim名称与Independent策略。支持既有八导入器，文本MIME与application/octet-stream可作为新import候选（SRT/VTT/ASS/TTML常以octet上传），不是修改旧create准入。新来源/行/元素/注音由原一次前缀映射产生固定ID，同资源的新意图仍是新导入/历史，同回执重试不重复解析或生成ID。新/替换的目标及结果ID须≤128，不截断或重写，追加不扫描旧未触及行。

append按原CRLF/CR→LF、跳过空白行，保其他原始空白，Unicode元素未定时；新翻译FollowPrimary，existing保原来源策略。空结果沿原emptyLyrics拒绝。真实历史名lyrics.import/branch.import/lyrics.paste。

可选importResult含lineCount/selectedBranchId/selectedLineId/report?；file import完整report，append不造report。完整保27字段：导入器/格式/实际编码/检测依据、全部源/结果/计时/跳过/续行/推导/兜底数量、offsetUs十进制Int64字符串、BOM/newLineKind、完整issues(kind/severity/sourceLineNumber/rawText/message，0行号合法)、候选名/歧义/丢弃特征、重切统计及issueCount/maxSeverity。无问题maxSeverity省略。原有Error诊断但有合法行的输入仍沿原业务可提交，不一概拒绝，也不截断报告。

资源准备在回执owner之后获取无队列名额（Host2/client1，满429 requestQuotaExceeded）。Writer捕获固定source/workspace/gen/下一revision/audioDuration与目标，然后Writer外资源lease读/身份hash与size核验、纯parse/candidate；资源及所有易失败cleanup完成后重入Writer，任意修订变化精确409、close/只读等再核，不复活旧会话。已接受owner固定在途/回执/client生命周期，断流只停止等待；shutdown排水覆盖准备，不把Task.Run cancellation称为硬截止。

共享可选preparation guard，桌面默认无预算。HTTP每次64MiB累计准备工作配额：保留的IR/行/元素/issue/片段、字符串副本与集合/数组容量在分配/扩容前扣；原始有界bytes/有限缓冲/完整UTF16解码另受输入上限约束。这是逻辑工作配额，不是CLR heap精确上限或OS sandbox，不加任意源行/grapheme硬帽。最终新/替换行逐行按已固定前缀ID和写后revision执行S1真实1MiB/20000项；累计投影16MiB。完整成功回执（含完整report）仍≤实际配置64KiB，超限413且工程/历史/dirty/redo/修订/事件全不变，无第二次ID/提交后回滚。

本片明确修复LRC原强制编码被忽略的缺陷，使EncodingOverride按原公开参数承诺生效；默认auto检测保持。单列共享回归与固定旧程序集的八格式默认结果/report独立对照，不把这项业务修正隐藏在HTTP传输内。纯Prepare/Commit复用原业务、严格DTO及全部预算/权限/receipt/并发/close/shutdown回归和真正HTTP保存重开后才交付。

## 6. 后续波形与时间基边界（G11，候选）

前端已接受波形暂由浏览器计算，后端G11排G10之后。首轨一致性已由0.80.1／合同1.5.1独立修复，真实Worker与HTTP证据见实施§37；未新增波形接口。现有一次prepare已产生完整金字塔，但会话只保留时长；若新增保留，500000基础桶/64sample的完整层级约8MB/会话，默认会话配额最坏约2GiB额外常驻，不能当作免费路由。候选优先保留本次准备结果并明确主机总预算/同源去重/释放及cacheBudgetExceeded，不让原create/open因为新波形缓存失败，不返回空数组冒充静音；不新增解码任务、跨重启缓存或持久PCM资源。

建议元数据及最多8192桶的min/max float32小端窗口，窗口绑定原音频SHA＋准备profile＋峰值内容身份，不随歌词修订改变；确切缓存和错误合同仍待G11施工时冻结。原始资源端点提供上传字节，没有与FFmpeg输出同时间基的PCM播放衍生资源，不能只由同SHA承诺浏览器与后端逐样本一致。

此前只读native双轨选流边界现已由真实Worker和HTTP复现并修复（实施§37）；原native证据仍只证明其参数对照，不替代修后链路。WAV浏览器基线与MP3/FLAC标记偏移仍各自实测，不由首轨修正推出所有媒体与浏览器逐样本同时间基。

### 6.1 G11波形窗口实施口径（0.82.0／合同1.7.0）

首轨修复0.80.1完成后，在原一次create/open/recover准备结果上保留实际峰值，不重新decode，不引入PCM播放资源或跨重启持久缓存。Host默认64MiB已保留峰值payload预算（8×全部层桶数，非总heap，原始准备临时内存另计），按源SHA/准备profile/样本与源Info/所有层实际floatbits的规范身份去重；同身份已留存可增加session引用，即使剩余额度0。不同client即使知道hash也不能越权取他人的binding。

无容量时原创建/打开/恢复仍成功，session不保留失败的LoadedWaveform作为旁路，元数据available:false reason=cacheBudgetExceeded；之后缓存空出也不自动重新准备。无音轨noAudio，检测固定asset/path/已有AudioMedia身份变化则audioIdentityChanged，不返回旧峰值；旧AudioMedia缺失的合法来源不自动补元数据/历史。正常关闭、候选注册失败、registry退出均释放binding，最后引用释放entry；关闭后新读取404，已取得≤64KiB独立窗口字节的在途请求可完成。

| 路由（session子路径） | 请求／结果 |
|---|---|
| GET /waveform | 无查询、不带expectedRevision；返回projectSessionId、available、protocolVersion。不可用仅多reason；可用含下列完整payload |
| GET /waveform/peaks | waveformId、level、start、count全部必填唯一；count1..8192、start≥0且完整窗口不越level实际桶数，8N bytes |
| HEAD /waveform/peaks | 同GET状态/长度/ETag，所有状态无body |

可用payload：waveformId/sourceSha256/profile/sampleRate(int)/sampleCount(Int64string)/durationUs(Int64string)/sourceChannels(int)/waveformChannels:1/baseBucketSamples(int)/format:"minmax-f32le-v1"/levels[{level:int,bucketSamples:Int64string,bucketCount:int}]。数组给全部真实层，不截断。waveform身份包含明确峰值codec/首轨mono语义标识，不随歌词修订改变；metadata不触源文件IO/native/Worker握手，实际来源身份核当前固定会话事实。

peaks为application/octet-stream，float32小端min/max交错、原bits完整；ETag绑定waveformId/level/start/count和窗口bytes，If-None-Match支持原资源常规匹配→304，HEAD仍无body。此版Range明确400 invalidRequest field=Range，不提供Content-Range；未知/重复query及格式/窗口越界400，field定位实际参数。未知或旧waveformId404 resourceNotFound，无会话/跨client沿中立sessionNotFound404。只读会话可读取，仍要求通用认证/Origin/Host/client读保护。

峰值描述服务实际首轨downmix单声道（不是多channel极值合并），非finite按现准备规则处理、合法float不强夹[-1,1]；最小精度为base64sample完整尾桶。对比独立原prepare/pyramid及HTTP完整各层分窗bytes、ETag/HEAD/304/Range、跨client/close/同源去重/低预算/先前不变scope。WAV浏览器与MP3/FLAC逐样本时间基仍需各自实测，不从原上传SHA或服务峰值一致推定播放器一致。

## 7. 验收与交接

每步先正常 solution 零警告构建，再执行相关 Hosting/Server/共享用例回归；实际等待进程退出和 TRX 终态，不累加历史计数。使用新目录发布匹配 self-contained Server 与完整 Worker，记录源码与部署 SHA；真实 HTTP 使用自有 profile/素材，验证成功与失败、并发/修订/回执、保存关闭重开以及自有进程排水。

同步详细行为源、执行合同、工具显式映射、正反例后，从本次编译 DTO 重新生成 OpenAPI/index/manifest，并默认检查及校验脱敏 HTTP 留档。交付给 Claude：提交 SHA、合同/产品/协议版本、发布目录、重发要求、启动器参数/stdout变化、实跑证据与限制。前端合同接受、mock、HTTP 与浏览器验收分别记账。

当前状态：2026-10-16 S1 已完成0.76.0／合同1.1.0，正常solution零警告错误、相关回归136／136、真实HTTP245请求／1784断言，证据见[实施§32](../history/backend-extraction-progress.md#web-s1-read-models-0760)。G3/S2已完成0.77.0／合同1.2.0、225项相关回归与真正HTTP2155请求17720断言，见[实施§33](../history/backend-extraction-progress.md#web-s2-timing-0770)；S3a已交付0.78.0／合同1.3.0，407项相关回归与真正HTTP3116请求23627断言，见[实施§34](../history/backend-extraction-progress.md#web-s3a-content-0780)；S3b来源管理亦完成0.79.0／合同1.4.0、345项相关回归/3081请求25117断言（实施§35）；S3c元素结构亦完成0.80.0／合同1.5.0，518相关回归、3367请求26552断言见实施§36；S3d亦完成0.81.0／合同1.6.0（602回归/5089HTTP，实施§38）；G11亦完成0.82.0／合同1.7.0（382回归/340HTTP，实施§39）；前端W2仍独立。Claude报告用户已验收W0，并以G9（S1）、G10（时间）、G11（波形）、G12（目录列表后置P2）登记前端缺口；S3后端可先交付供后续前端消费。
