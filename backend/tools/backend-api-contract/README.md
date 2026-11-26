# 冻结 API 合同生成与校验

当前工具目标为 **合同 1.62.0／产品 0.120.2／HTTP 协议 1**（2026-11-26），生产源码基线 `b5d4d71464a142cbc0e49806870526a5adb77e77`，单一真值源产物位于 `backend/web/`：

- 同步清理已下线的 12 条历史路由（5 条无版本兼容别名、5 条 `/api/v1/recovery-jobs*`、`POST /api/v1/project-sessions/{id}/edit-previews`、`POST /api/v1/project-sessions/{id}/resources/package-asset`）与已清退的独立 `project-image-*` 来源 profile（`project-image-preview-v1`、`project-image-validation-v1`、`project-image-export-v1`）；
- 移除 5 个未导入孤立脚本、28,126 行历史语料归档 `style_v2_legacy_cases.json` 以及向历史版本倒推还原的 SHA-256 门禁；
- `freeze.py` 保留纯正向模块化 fixture 刷新、CLR 反射核对、Server 路由 100% 双向匹配及 `backend/web/` 产物指纹；
- `validate.py` 直接对当前 `1.60.0` 契约执行严格校验：OpenAPI 3.1 结构校验、全量内部 `$ref` 完整性、全量 Draft 2020-12 schema 合法性、全部正反例（3,984 例／2,862 反例）、`contract-manifest.json` 版本与产物 SHA-256 一致性，以及可选 `--http-log`／`--report`。
- 当前合同规模：**103 个路径／115 个显式方法／686 个 schemas／106 种工程命令**。合同生成与静态校验不替代匹配发布、fresh HTTP 与运行回归验收。

## 检查当前合同

从仓库根目录执行（使用仓库固定 .NET SDK 与安装好 `requirements.txt` 的 Python 3 环境）：

```bash
# 方式一：自动运行 ContractMetadata.csproj 导出临时 CLR metadata 并检查零漂移
python3 backend/tools/backend-api-contract/freeze.py --check

# 方式二：先显式构建并导出 clr-metadata.json（如写入忽略目录 backend/artifacts/api-contract/）
dotnet run --project backend/tools/backend-api-contract/ContractMetadata.csproj -- \
  backend/artifacts/api-contract/clr-metadata.json
python3 backend/tools/backend-api-contract/freeze.py \
  --metadata backend/artifacts/api-contract/clr-metadata.json --check

# 校验当前冻结 OpenAPI、$ref、Draft 2020-12 schemas、全量正反例及 manifest SHA-256
python3 backend/tools/backend-api-contract/validate.py \
  --report backend/artifacts/api-contract/validation.json
```

`freeze.py` 默认处于 `--check` 只读检查模式，不修改任何冻结文件。检查内容：

- 编译 `MakoKaraGenerator.Backend.Contracts` 后通过 `ContractMetadata.csproj` 反射真实属性类型、可空性、枚举、构造默认值与严格未知字段标记，再按显式公共路由图递归生成 schema，排除私有 Worker 协议。
- 将 `ServerEndpoints*.cs` 中全部 `MapGet`/`MapPost`/`MapPut`/`MapDelete`/`MapMethods` 与 `freeze.py` 路由表做 100% 双向比较，未识别或遗漏的路由直接失败。
- 核对重生成的 `backend/web/{openapi.json, api-index.md, examples.json, contract-manifest.json}` 与磁盘文件逐字节一致，并覆盖 `api-contract.md`、`frontend-workflows.md` 及相关后端源码指纹。

## 更新合同

先按 [合同变更流程](../../web/api-contract.md#4-冻结后的双端变更流程) 审阅实际行为差异；同步行为文档、工具 schema/fixture 覆盖与合同版本号后，执行显式写入：

```bash
python3 backend/tools/backend-api-contract/freeze.py --write
python3 backend/tools/backend-api-contract/freeze.py --check
python3 backend/tools/backend-api-contract/validate.py
```

不要手工编辑生成的 `openapi.json`、`api-index.md`、`examples.json` 或 `contract-manifest.json`。`freeze.py` 中的 `BASELINE` 由审阅者固定为已提交的生产源码 SHA，同时写入 OpenAPI 的 `x-source-baseline` 和 manifest 的 `sourceBaselineCommit`；产品版本从 `backend/Directory.Build.props` 读取。

## 校验真实 HTTP 留档

可选参数 `--http-log` 接受已脱敏的配对 `*.request.json`/`*.response.json` 留档，离线校验实际 JSON 请求与响应是否符合当前 OpenAPI schema（二进制/SSE、无配对文件及无映射请求分别跳过计数）：

```bash
python3 backend/tools/backend-api-contract/validate.py \
  --http-log backend/artifacts/http-smoke \
  --report backend/artifacts/api-contract/http-schema-validation.json
```

## 独立模板库增量（0.84 / 合同1.9）

`template_library_schema.py` 和 `template_library_fixtures.py` 单独维护四路由/五命令、原21项模板属性及三kind约束。与工程35 commands及0.83的24属性路径union无关。完整静态read/input使用新的schema clone，逐字段审阅真实CLR类型、nullable与默认值；旧264组件与756例逐项保持不变。metadata从当前Contracts编译输出，不用规划文字替代真实DTO。

发现接口仅返回builtin/personal描述，不读个人入口；list初读可省expectedRevision，detail必须精确revision/templateId。revision是`missing`或64位小写SHA，不是Int64。静态detail始终带完整definition；高级detail显式`advancedUnsupported`且definition必须null，不传残缺参数。copy使用固定sourceLibraryId/sourceRevision快照，再对personal expectedRevision CAS。只静态完整定义可create/replace；旧高级copy/rename/delete保原闭包，HTTP不提供路径、包作者或工程目标。

增量例独立输出，预览可与基线一起验证，不修改冻结文件：

```bash
python3 tools/backend-api-contract/template_library_fixtures.py > artifacts/api-contract/template-library-examples.json
artifacts/api-contract/venv/bin/python tools/backend-api-contract/validate.py \
  --spec artifacts/api-contract/openapi-preview.json \
  --extra-examples artifacts/api-contract/template-library-examples.json \
  --report artifacts/api-contract/template-library-validation.json
```

1.9正式生成时主代理将增量追加到冻结examples，保留原756例的名称/schema/valid/value，不能重复追加。256KiB按完整新command canonical JSON计且不含metadata；name/font按UTF-16、fallback32、单gradient256只管新输入。schema仅提供code-point必要长度限制，UTF-16、完整UTF-8响应1MiB/20000值、完整回执及原索引/闭包预算仍需服务/真实HTTP。读取与服务端copy/rename/delete不套新定义长度限额。分页约束响应，不能声称全库IO已经分页。

manifest精确固定库Application用例、原repository/commit lease、Core索引修订/闭包/唯一identity writer和kind/21props/token事实来源；不把全库任意src都归入本批。回执owner/CAS、晚期取消、OS租约和未知终态须实际回归，静态schema不能证明这些时序成立。

## 工程预设分配增量（0.85 / 合同1.10）

`freeze.py` 从当前Contracts metadata生成四种工程命令的严格分型：`presetApplications.apply`、`update`、`setEnabled`、`remove`。apply目标是 project／branch／line／token 四类闭合union，targets及applicationIds均限制1..1000且不重复；templateLibraryId只允许 builtin/personal，`templateRevision`为非空快照身份，branchInputs为可省略或null的字符串映射。`preset_application_fixtures.py`保存增量正反例，写入冻结examples后与旧例一起执行validate。schema只验证wire形状；模板快照捕获、工程修订、历史、库更新不追改及实际渲染仍由Hosting/Application与真实部署验收。


## G19-B 片段命令增量（合同1.19）

当前冻结工具从 Contracts metadata 审阅四种显式片段命令：`orchestration.createClip`、`orchestration.renameClip`、`orchestration.setClipContainer`、`orchestration.deleteClip`。它们共享非空白、最多128字符的 `branchId`／`clipId`；创建要求1..1000个唯一行ID和容器，名称可省略或显式null；改名必须显式提供可空名称；移动要求容器；删除不接受额外业务字段。生成器将每个分型收窄为 strict schema，并保留 `ProjectCommandResponse.clipResult` 的可空名称。schema 只覆盖 wire 形状，来源策略、连续范围、容器归属、领域校验、历史/no-op 与 capabilities 仍需 Hosting／HTTP 回归。


## G20 结构命令增量（合同1.20）

冻结工具显式审阅六种结构命令与 structureResult。setNode只公开nodeId/name；reparent与reorderChildren的parentId必填且可显式null；addSlot的name必填可null；reorderSlots的containerId必填非空。两种orderedIds限定1..2000个唯一稳定ID。名称null与根层parentId在请求中必须保留，响应中的空值身份仍省略。结构集合完整性、世界外观、引用保护、历史与no-op不能只由schema证明，继续由Core/Hosting/HTTP回归验收。

## 基础编排后续切片（0.92 / 合同1.22）

`orchestration_properties_schema.py` 在当前Contracts反射之后审阅G21-B四种属性/动画命令、G23四种手工前奏/提示容器命令、G24单句微调命令，并剔除所有继承自ProjectCommandDto的无关wire槽。每种命令独立strict字段集合；名称/配置的显式null与缺失分离。三动画通道及十四个SlotAnimation字段完整替换，Int64字符串边界/前导零经独立schema覆盖，通用时间overlay保留已审阅的精确规则。

新增两个GET为geometry与line-local-transform；查询唯一必填、未知项拒绝、预算与read capability固定在操作上。geometry固定referenceKind与finalPixelGeometry，四角恰好四项；line transform及layout.slotAnimation的JsonIgnore(Never)字段在响应中required且可null。`orchestration_properties_fixtures.py`生成独立正反例，覆盖九命令/外层envelope、继承槽拒绝、完整动画/通道、Int64、所有六anchor、两GET query/response、leadIns/content与required-null。validate额外校验62命令discriminator、两GET required query/capability、const和required-null，不用反射替代行为审阅。

该批manifest新增固定几何/动画/前奏/本句微调Core真值源。schema不验证跨字段安全区有序、真实节点存在、SlotSequence不可清空、引用保护、单次历史、租约/生命周期或最终Runtime像素，这些继续由对应Core/Hosting/HTTP及后续像素/浏览器验收区分。

## G20-B 有限节点创建与复制（0.93 / 合同1.23）

`node_creation_schema.py` 在当前Contracts反射之后审阅CreateOrchestrationNodeCommandDto与CopyOrchestrationSubtreeCommandDto的全部继承形状、CLR类型、nullable与默认值，分别收窄为全字段必填的strict wire。createNode有group/auto/slotSequence三个互斥闭合分支；前两者slotCount必须null，序列必须1..64整数。kind大小写精确；parentId:null为画布，其余需已有普通组。copySubtree只接受nodeId/name，同父完整复制。name可null且最多256 UTF-16 units；schema的code-point限制只是必要条件。

structureResult.createdNodeIds沿真实nullable/default-null/JsonIgnore(WhenWritingNull)声明审阅；仅两种新操作必返回完整唯一ID（1..128，新根第一），旧六命令不出现。新操作changed必须true，containerId/orderedIds省略。字段的真实身份、owner追加及内部重映射、Core候选校验、回执重放与单次历史仍须HTTP/Hosting验收。

`node_creation_fixtures.py` 独立提供132例（103反例），覆盖严格字段、缺失/null、kind大小写/组合、槽位边界、名称/身份必要长度、信封/union与新旧结果形状；原1390例的name/schema/valid/value逐项保留。1.23时validate额外核对64命令discriminator、两命令字段/capability/strict规则及三kind分支。

command16KiB、源/候选各2000节点/深64、copy128节点、create初始槽位64；验证输入另限20000项和1048576 UTF-16累计身份/名称/标记文本，包含歌词branch/line/token及程序关联遍历项，长歌词也可能413。完整编排输出独立1MiB/20000项及实际完整回执预算在提交前预检。schema只描述额度，不能证明跨字段真实树或内存/时序安全。

manifest显式固定20个编排Core真值源：原15项及SceneContainerDefinition.cs、SceneContent.cs、LyricPresentationDefinition.Tree.cs、Validation/PresentationValidator.cs、PresentationValidator.LeadIns.cs，任何文件缺失均失败，不以空glob漏掉节点/内容/引用规则。正式metadata从源码基线d045eae重新编译Release导出；本轮离线证据见`artifacts/backend-g20b-contract/`，实际构建和HTTP证据见实施§56，各范围独立登记。


## 1.24.0 G20～G24整体补齐

当前合同1.24.0为70 paths／77 methods／401 schemas，1913个合成例（1394反例）；新增391例，原1522例保留并仅迁移下述1个响应例的两个新增字段。最终metadata从产品代码`c50516da6cf421e1bb3fbc3cc01fc11d0235e93b`重新编译导出；正式冻结检查与全例/实际HTTP JSON形状校验报告位于`artifacts/g20g24-contract/`。真实HTTP留档源为`artifacts/backend-g20g24/http-final3`，形状校验不能替代该probe的生命周期、几何、PNG和排水断言。

`editing_completion_schema.py`审阅14种新增命令、单句展示继承、Node静态preset目标、真实Worker测量请求/完整响应。PNG的CLR byte[]按实际System.Text.Json wire覆为base64字符串；必返nullable字段显式required+null union。measurement严格time/layout union、canonical Int64与无query/无receipt；源/完整响应预算由实际服务验收，不由fixture证明。

增量fixture由`editing_completion_fixtures.py`生成，正式追加须按name去重。原1522例全部保留，仅`basic-orchestration-orchestration-projection`新增两个必返字段：根`projectEnterAnimation:null`与`programs[0].leadInGeneration:null`；所有原值保持。迁移后的旧例和新增例均验证当前合同，不使用历史schema特判。原生成器同样补齐这两个字段。

manifest在原20个Core显式行为文件基础上加入行动画、LayoutReferenceScene、MeasuredRenderFrame、SceneAttachmentReferences、Skia Geometry/TextMarks/Measurements/RenderCore与Runtime测量会话公式；Backend源仍全覆盖。更名/缺文件明确失败，不能依靠文件名glob默默漏公式。

## 1.24.1 透明导出枚举与附着错误修复

产品0.94.1保持70路径／77方法／401 schemas／78工程命令。`ExportJobRequest.format`改为真实服务接受的`opaqueMp4`／`transparentProRes4444`，不新增`transparentMov`别名；`export_request_fixtures.py`的6例已追加到冻结examples（3正／3反），总1919例／1397反例，validate另核对canonical枚举及关键正反例。附着错误的稳定failureCode/field为Server行为修复，详见[修复记录](../../docs/archive/acceptance/backend-basic-business-bugfix-0941.md)。这些schema例不是真实HTTP或成片编码验收，Server定向回归独立记录。

## 1.25.0 基础工程业务

产品0.95.0，80路径／89方法／436 schemas／84工程命令。`basic_business_schema.py`固定六命令严格分型、三读取的严格查询、LRC计划/导出与模板传输请求/响应，审阅真实DTO字段及CLR类型；library revision维持missing/小写SHA，不套工程Int64。resolved-style的tokenId/clipId必须出现并允许null，21属性来源保持Core顺序，明确非最终像素。新LRC/归档的字节计数为JSON Int64 number，resources.sizeBytes为Int64字符串。

`basic_business_fixtures.py`新增400例（351反例）；旧1919例逐值保留，总2319例／1748反例。validate保留此前全部断言，并检查80/89、84命令、严格metadata、必返nullable上下文、Range/HEAD及非{id}路径参数匹配。生成检查和合成例验证不代表真实HTTP、Worker、浏览器或用户验收；实际证据另见实施§58。

## 1.27.0 TTML与独立注音片段

`ruby_timeline_schema.py`审阅七个派生record完整继承形状后，将CLR的RubyTarget/SegmentStartUs/SegmentEndUs映射为真实wire target/startUs/endUs。四级组与五级片段目标各自strict；add的afterSegmentId必填可null且不接受时间，reorder为完整orderedSegmentIds，batch是1..1000条完整目标/time条目。schema的Int64字符串边界保超过2^53的值；非负有序时间、隶属/重复、非空正文与完整顺序仍由业务校验。

`basic_business_schema.py`同步真实LyricsExportPlanRequest scope union、两响应scope/required-null branchId和三个ruby导出计数。sourceBranchId可省不可null，application/ttml+xml与实际上传MIME一致。manifest固定TTML时间树/解析/领域、v16→v17迁移/映射、Application编辑/导入、sampler/Skia和脚本timeline的实际行为源；Backend源仍全覆盖。

当前1.27冻结为80路径／89方法／449 schemas／91命令、2648例（2040反例），validate固定这些精确计数。`ruby_timeline_fixtures.py`新增329例，增量覆盖严格七命令、CLR别名拒绝、五级父链、required-null、新旧目标不可混用、Int64边界与batch 1/1000/1001、document scope正反例、segments状态、rubyResult和导出计数。原2319例保留原名称/schema/valid及全部原字段值，仅迁移必返新增report计数和export scope；没有删除旧例。metadata必须从当前源码重编译，freeze --write后再执行默认drift检查与venv validate；静态形状校验不代表HTTP/Worker/PNG或浏览器验收。

本轮0.96最终metadata实际留在`artifacts/ttml-ruby/validation/clr-metadata.json`，SHA-256为`742bd46d75c78f925d4c129c564a9f1eb0f4b2bf7231a3fae98628b66f610aa9`；独立复核此轮现有证据时使用该路径，`artifacts/api-contract/clr-metadata.json`可能仍是历史产物，不能直接假定其属于1.27。正式源码生产基线为`8a000a33a1ac17026b4da84ec42a6774dd9851e7`，版本/合同见其后的P6提交。此轮复核：

```bash
python3 tools/backend-api-contract/freeze.py \
  --metadata artifacts/ttml-ruby/validation/clr-metadata.json
artifacts/api-contract/venv/bin/python tools/backend-api-contract/validate.py \
  --report artifacts/ttml-ruby/validation/contract.json
```

## P2-A公共包预览（合同1.29.0）

`package_preview_schema.py`在当前CLR反射后审阅完整Source/ImageEffect/Material响应类型、origin/payload与material owner互斥、source关联身份、0行列及slot0..2。普通响应保持忽略未知字段；新增nullable payload缺省省略，旧B2/raw保存报告不要求迁移新增字段。`package_preview_fixtures.py`仅追加合成正反例，原2648例的顺序/字段值保持。包资源身份/revision/artifact格式不等于实际catalog关联或执行成功；JSON字符串parametersJson的对象语法与字段一致性由服务验证。

80路径/89方法、91命令不变；仅预览profile实际协商后新增package-lyrics-scene-v1。公共诊断组合768项/512KiB、128项每页/最多32页；byte、真实Worker能力、Error禁新帧、旧成功lease TTL及关闭排水仍由真实HTTP/Runtime验收。B4、静态模板/候选、preset包和外媒体字体范围不扩大。最终metadata须在源稳定后重新build并导出，freeze/write/check/validate及HTTP exchange校验分层登记，期间工具预览不称最终冻结。


## 1.32.1 已实现同帧测量来源勘误

产品保持0.98.1；`e1942c0`／`9b907c5` 已将公共 `orchestration/measurements` 固定输入接到真实 Worker 的五来源握手，生成器此前仍将 `SceneMeasurementDataDto.sourceProfile` 限为三种静态值。1.32.1补齐 `package-lyrics-scene-v1` 与 `package-lyrics-scene-presets-v1` 两值，追加2正例／1反例，原2818例保留顺序和值；现在为2821例／2162反例，87路径／97方法／481 schemas及91命令保持。混合来源仅指直接包与固定静态预设测量，Color背景、codec及测量预算继续适用；不扩大preview、validation、export来源准入，不代表新增fresh HTTP或完整B3/B4验收。

本次另复核 `4e0f909` 的 Worker 能力代次隔离和 `339a88b` 合同提交中生成manifest之后追加的正文／校验器／24条作者示例，重新固定最终指纹。更新前精确差异保存在本机 `artifacts/api-contract/reconcile-before.json`；metadata必须在源码稳定后重编译并导出，再执行write/check/validate。

验证器把冻结 `web/examples.json` 的3条1.32.1测量来源例视为固定尾部；`--extra-examples`只在这3条之后追加，历史例子顺序与2821／2162基线计数保持不变，额外文件只增加本次报告的计数。


## 1.33.0 已保存工程Image背景预览

产品0.99.0新增独立 `project-image-preview-v1` 预览来源；不新增公共路由、命令或图片输入DTO。`project_image_preview_schema.py` 审阅runtime-info的兼容singular与实际协商plural字段、create/input/状态说明和有限预算/稳定failure映射，该历史0.99预览范围不把图片加入measurement/validation/export来源；后续0.100图片正式验证的独立能力见下节1.34.0；当时measurement/export不含图片，现行统一Runtime能力见文末1.36.0。仅ProjectStore已加载的受管Image闭包，无预设static/static-analysis/直接package基础源，详情见统一API§22。

`project_image_preview_fixtures.py`追加27条合成正反例，覆盖五来源runtime-info、图片探测省略/disabled/旧字段省略、错误singular/未知与重复profiles、现有project create/input、不接受bytes/path/profile/grant以及同步/异步失败DTO。原2821例按完整SHA保持，新合计2848例／2175反例，87路径／97方法／481 schemas／91命令保持。manifest显式纳入内存resolver、Runtime工厂/会话、原背景来源及Core内容身份/placement/playback真值，不能只冻结私有codec。

从稳定当前树重新build `ContractMetadata.csproj` 并导出metadata后再freeze --write、默认check与validate；本批离线证据位于 `artifacts/project-image-preview-local/contract/`。`--extra-examples`仍仅追加到冻结尾部，不改变历史计数与顺序断言。本地传输测试、Oracle预检、合同生成或源码指纹不代表clean匹配发布fresh HTTP/浏览器验收；Server＋Worker须重新匹配发布，后续实际证据独立补录。

合同1.33.0的匹配发布图片预览fresh HTTP final6为1175请求／9760断言；离线留档形状校验为1129份响应与166份成功JSON请求，40项非JSON/未映射跳过、无未配对文件。HTTP范围与原Runtime像素/控制/排水证据见[实施§77](../../docs/archive/history/backend-extraction-progress.md#project-image-preview-0990)，离线schema校验不增加HTTP计数。

## 1.34.0 已保存工程Image背景正式验证

产品0.100.0新增独立 `project-image-validation-v1` Worker能力，复用既有 validation-capabilities、validation-jobs、result、diagnostics 路由与任务生命周期，不新增图片 bytes/path/profile 输入。`project_image_validation_schema.py` 只扩展既有 `sourceProfile/sourceProfiles` 投影和操作说明：图片正式验证必须经独立能力握手，`project-image-preview-v1`、package validation 或旧静态能力均不能代替；该历史0.100范围当时不覆盖export/measurement、video/font/preset组合，现行统一Runtime能力见文末1.36.0。

完整 KTI1 输入预算为 `4194272` 字节（4MiB 减 32 字节头），固定原图2MiB、单边4096、解码16MiB、基础 snapshot 4MiB；decoder profile 为 `memory-image-bgra-v1`。`project_image_validation_fixtures.py` 追加能力来源、公共请求不接受 bytes/path/profile/grant、preview profile 隔离、image 输入来源和稳定失败报告例。该overlay须在`extend_validation_package_schemas`之后应用，避免旧package来源枚举覆盖图片验证来源。25项增量例（13反例）追加并完整保留1.33.0的2848例；validate精确核对4194272输入、4MiB base预算与预览8字节预算差，并检查job来源和measurement/export隔离。

2026-10-24从生产基线`eb54f8d`重新构建并导出metadata，正式生成物已写入`web/`并通过默认freeze检查与validate：87路径／97方法／481 schemas／91命令，2873例（2188反例）。本次证据位于`artifacts/api-contract/image-validation-1340-applied/`；此前`image-validation-1340/`仅是离线留档，不能代替仓库内生成物。匹配发布fresh HTTP有限切片随后通过1412请求／10582断言，Server exit 0；不支持Worker边界10请求／476断言另计，完整范围见`artifacts/project-image-validation-01000-acceptance/http-final1/`。`web/api-contract.md`保留人工维护正文；`freeze.py`同步生成api-index的验收文案，再生成manifest；验收补录保持1.34.0，须从稳定源码视图运行，避免把其他任务的未验收源码写入当前合同指纹。复核命令如下。

```bash
python3 tools/backend-api-contract/freeze.py \
  --metadata artifacts/api-contract/image-validation-1340-applied/clr-metadata.json
artifacts/api-contract/venv/bin/python tools/backend-api-contract/validate.py \
  --report artifacts/api-contract/image-validation-1340-applied/validation.json
```


## 1.36.0 P1/P2统一Runtime输入（正式冻结）

`p1p2_runtime_schema.py`在历史图片overlay之后审阅当前来源集合：preview保留旧图片来源并增加package+presets和runtime；measurement/export为原五来源加runtime；validation保留旧独立图片来源并增加package+presets和runtime。公开请求仍不接私有文件grant。旧0.99/0.100/0.101有限图片能力和预算仅属于各历史profile，现行能力以实际Worker协商后公布的sourceProfiles为准。字体Worker私有kind/profile不增加公共HTTP路径或schema。

创建的两个素材ID各自可省略/null，空白拒绝。上传capabilities的maxAssetBytes/maxAssetQuotaBytes固定必填，值为正Int64或null；null表示该部署维度未配置字节限额，不可读成省略或默认固定上限。四种video MIME与ResourceStore实际allowlist一致。候选sourceRevision/candidateRevision为不透明candidate身份，baseProjectRevision仍是原精确工程修订。

本轮从0.102.0稳定基线`2f64c197f44a4e3272d0c8771b82fa442dcfd699`重建metadata，在独立artifacts中验证91路径／101方法／500 schemas、2974例（2248反例），包含84个P1/P2增量例；保留所有2890个历史例子的name/schema/valid，仅对四个新增默认质量响应字段执行可逆迁移。此为离线合同形状与历史指纹核验；metadata、默认freeze检查及validate报告位于`artifacts/api-contract/final-1360/`。正式web生成物与产品0.102.0保持一致，合同冻结不能替代匹配发布或HTTP验收。

高级模板`advancedRuntimeAvailable`仅公开现有Line/Token闭包执行可用，detail的`definition`仍必返null；`staticAvailable`继续提供静态作者定义，组合/未知高级保持`advancedUnsupported`。导入preflight同步availability枚举，不扩大高级作者定义编辑。

1.36.0的`.mktpl`默认传输预算对齐原完整包：archive/expanded各256MiB、单文件64MiB、1024entries、总保留1GiB；旧fixture逐值保留，新增大字体和256MiB边界响应例。

候选测量成功响应的`projectRevision`与候选`sourceRevision`一样是不透明candidate身份；普通工程测量仍为十进制修订。1.36.0补充专门正例，禁止按字段名将该响应一律解析为Int64。

最终`http-final3`留档的307 JSON响应／91成功JSON请求（38操作）通过合同校验，13非JSON/无映射留档跳过、0未配对；HTTP320请求／2215断言是独立probe结果。匹配发布为`dist-92c3e84`且sourceDirty=false，生产合同BASELINE继续2f64c19；详细最终范围见统一API§23及实施记录。

## 1.38.1 P5合同勘误与示例聚合

历史产品0.104.1／合同1.38.1为100路径／111方法／556 schemas、96工程命令、3156例（2374反例）。作者草稿和个人配置路由统一指向行为§21；编译路由的同步200 `AuthorCompilationResponse`和异步202 `JobResponse`同时写入OpenAPI及生成清单。内容端点的200/206成功状态也从同一OpenAPI生成，CORS说明包含实际允许的DELETE。产品0.104.1含预览来源切换边界修复；P4的167请求／1142断言fresh HTTP继续归属产品0.104.0，本轮合同门禁不登记为新的完整HTTP验收。

`web/examples.json`保存已审阅的历史示例底座；`p4_author_fixtures.append_to`调用P3聚合器，按稳定名称前缀替换各自的P3/P4增量片段，再追加当前fixture，保留其他历史例子的顺序和值。`freeze.py`最后统一写入聚合文件的`contractVersion`，不再继承历史底座的1.36.0标识；`validate.py`要求该值与OpenAPI `info.version`完全一致，并继续核对历史切片、反例计数和历史指纹。新增例子应修改对应fixture并重生成，不手工修改生成物中的版本或增量片段。

## 0.105.0 GPU产品刷新与合同1.38.1

在0.105.0时，GPU后端为内部显式配置，未新增公共HTTP字段、路由或合同语义，该版合同保持1.38.1；0.106.0的公共选择增量见下节。当前仓库默认校验的`web/openapi.json`产品版本必须与`Directory.Build.props`的`VersionPrefix`相同；显式`--spec`离线文件仍可校验原已审阅的0.104.1，1.37.0／1.38.0的历史产品映射不变。路径、方法、schema、工程命令及正反例的原精确门禁保持。

设备选择、Vulkan创建/清理、设备引用生命周期，以及本轮修改的graph、capture、group isolation和中间surface源码加入manifest显式指纹列表；文件缺失或改名要求重新审阅。已有Runtime session/factory、主pipeline与依赖版本指纹继续覆盖组合根。这些指纹只用于漂移报警，不证明实际硬件渲染、像素、HTTP、浏览器或跨平台验收。

2026-10-26从最终生产源码`6dded955c578d6c280e4cccc1fb8e80ee12395fe`重新build并导出新的CLR metadata，再执行`--write`、默认freeze检查和validate；证据位于`artifacts/gpu-render-20261026-r1/contract/`。本轮100路径／111方法／556 schemas、96工程命令和原3156例（2374反例）保持；示例逐字节不变，OpenAPI除产品/基线声明外的语义结构不变，清单只改首行，manifest覆盖全部本轮27个生产差异路径（其中20个关键文件为新增指纹）。GPU的真实HTTP证据另行登记，旧P4/P5证据保持原版本归属。

## 1.39.0 CPU/GPU显式API（0.106.0）

新增 `GET /api/v1/render-backends` 和 `PUT /api/v1/preview-sessions/{id}/render-backend`。`render_backend_schema.py`审阅4个新增schema及各预览/验证/导出/测量请求和回显字段：严格 `cpu|gpu`、启动默认/保留现有/候选固定默认、必填切换字段与metadata、设备可用/不可用union、GPU API为vulkan、历史记录可缺少选择。C# nullable与HTTP presence继续分别审阅，历史缺失值不自动补成CPU。

`render_backend_fixtures.py`维护175个增量例（109反例），与历史例子迁移后生成总3331例（2483反例）；schema总数为560，102路径／113方法，96工程命令保持。新增fixture、schema及生产设备选择/Worker核验源码均由manifest固定，生成的OpenAPI/index/examples/manifest仍只能经metadata与freeze重生成。

生产源码审阅基线固定为 `9506942118efce3f1869e0cbc9e7da1af1869b42`，必须从匹配源码重新build/导出metadata，再freeze/validate。该基线包含切换路由的严格JSON读取修复；DTO和合同计数保持不变。指纹与合成例不证明设备切换事务、GPU硬件、任务固定、原始报告、进程排水或浏览器验收；匹配clean发布的本机有限fresh HTTP770请求／5013断言及独立GPU不可用负例46／343已通过，分层回归、硬件/媒体/排水与限制见[验收报告](../../docs/archive/acceptance/render-backend-api-acceptance-20261026.md)和[实施计划](../../docs/archive/plans/backend-render-backend-api-plan.md)，浏览器、跨平台和性能仍独立。
