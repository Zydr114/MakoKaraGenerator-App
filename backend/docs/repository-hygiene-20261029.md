# 后端仓库卫生审计与实施计划

日期：2026-10-29。基线为 `main` 的 `4e70334`，实施分支为
`chore/repository-hygiene-20261029`。仓库包含 18 个生产项目；本计划的目标是
审查并整理可证明无行为变化的冗余，以及使当前后端入口与历史文档各有明确位置。
本文件保留实施计划并记录已完成的审查与归档；最终构建、合同、部署和 HTTP 证据另按实际结果登记。

## 范围与分工

主代理负责确定候选范围、审查计划与各阶段结果、集成提交及最终验收。四个子代理
按实际分配承担代码审计与清理、独立审查、合同与验证、文档审计和归档等工作；
分工和交接以各自实际结果为准。并行工作前确认文件所有权，涉及共享文件先协调。
各阶段按路径暂存，使用独立提交；存在远程时按用户约定同步当前工作分支。

当前程序版本为 **0.107.4**，合同版本保持 **1.39.0**，HTTP 协议
保持 **1**。版本和生成合同由合同负责人统一处理。文档代理不修改产品版本、
`freeze.py` 元数据或 `web/` 生成文件。纯文档历史不改写为新版本的运行证据。

无关未跟踪目录 `tools/backend-project-image-export-probe/` 保留，不纳入本次修改或提交。
需要 root 权限时将具体命令交给用户执行。

## 代码审计边界

每个清理候选须说明现有调用与所有权、修改范围、可观察行为不变的依据，以及
适合风险的验证方式；无法证明时保留现状或单独报告，不按文件名或表面重复删除。
清理不得改变公共 DTO、SDK、私有 IPC 协议、输入与输出预算、owner、CAS、
生命周期、取消与排水语义，也不得改变渲染像素、诊断和状态重放行为。
对错误顺序、资源寿命及跨模块合同的影响须结合具体候选核对。每项生产代码清理
作为独立可审查提交，关联必要的回归和文档；不借此扩展产品能力。

审查另发现 `BackendAudioInputClassifier` 仅读取四字节，却检查第五字节及第五至第八
字节的签名。本次审查另批准作为独立缺陷修复，并进行红绿回归；这是明确的
行为修复，不记为纯等价清理。具体影响范围和测试结果由该提交与最终验收登记。

## 文档整理边界

逐篇核定根目录文档的当前用途、完成状态、引用和归档去向，生成可检查的旧路径到
新路径映射。名称含 `plan`、`contract` 或 `acceptance` 本身不能证明文档已过时；
仍然有效的行为合同、架构说明、操作指南及待执行计划保留为当前入口。历史 ADR、
决策、已完成或被取代的计划和调研不删除，按以下目录组织：

- `docs/archive/decisions/`：历史决策及 ADR 正文。
- `docs/archive/history/`：历史项目入口、开发记录与路线图快照。
- `docs/archive/plans/`：已完成或被取代的实施计划。
- `docs/archive/research/`：历史调研、审计与基线报告。
- `docs/archive/acceptance/`：按原批次归属保存的验收报告。
- `docs/archive/legacy-ui/`：源仓库桌面 UI 的历史计划、合同与说明。

完整旧 `AGENTS.md` 移入归档，可修复其中实际文档链接；新入口保留经核对仍有效的
后端规则、合同入口、Git 与环境约定，并明确桌面 GUI 已从本仓库移除。旧
`docs/roadmap.md`、`docs/development.md` 和 `docs/decisions.md` 可保留少量导航页，
解释历史章节或 ADR 的新位置，避免批量修改生产源码中的历史注释。优先将可编辑
文档的真实 Markdown 入站链接修正到归档目标，保留片段锚点和历史含义。

`docs/performance/` 的历史证据和 manifest 均逐字节保留，不能为修复链接修改它们；
若证据中的链接仍指向移动前路径，则在必要的旧路径保留导航页。历史报告中的版本、
源码、发布和测试结论保留原归属，路径修复不能变成新构建或新验收的声明。
当前合同固定路径 `docs/architecture/backend-api.md` 和
`docs/runtime-validation-and-layout-contract.md` 保持；更改合同输入或受检文档的
链接时，由合同负责人审阅并按生成流程更新指纹，不手工伪造生成结果。

新增可复用的轻量链接和归档完整性检查工具：检查实际相对 Markdown 链接、目标及
锚点，核对映射的目标与保留入口，并明确检查范围与例外。实施前记录仓库既有失效
链接及分类，实施后分别报告已修复、仍存在的基线问题和新出现的问题；不能把
既有缺陷静默排除。证据目录另以归档前后文件集合和 SHA-256 比较证明未改字节。

## 阶段与出口

1. **审计**：固定 Git 基线、工作区和候选清单，逐篇核定文档；记录已有链接缺陷、
   不可变证据文件指纹、合同输入及有风险的源码引用。候选与预期验证交主代理审查。
2. **代码清理**：各候选独立实现和审查，运行与影响范围相称的定向回归；逐项提交，
   保留不能证明等价的代码。记录实际修改、验证结果与未解决风险。
3. **文档归档**：完成映射、必要导航页、新入口和链接修复；实现检查工具，比较
   链接基线和证据指纹，确认历史材料完整可访问。文档变更经审查后独立提交。
4. **合同与运行验证**：按实际最终源码重新构建合同 metadata，执行冻结和校验，
   完成适用的定向回归；发布匹配的自包含 Server/Worker，并以 fresh HTTP 验证
   本次变更相关的代表性行为。记录源码和发布指纹、请求和断言范围、进程排水及
   凭证扫描。静态合同、测试、发布和 HTTP 是独立证据，不互相替代。
5. **验收与交付**：主代理审查代码差异、文档映射、历史证据完整性和各层验证，
   登记实际结果、失败与限制，完成验收文档并按用户约定提交、推送当前分支。
   本计划不预先声称浏览器、跨平台、长片或性能验收完成。

## 实施审查记录

本节记录本轮实际结果；它不是把静态审查扩大为完整平台或产品验收。

- `artifacts/repository-hygiene-20261029/domain-review.md` 覆盖 Core、Application、Media、Export、Processes、Timeline：360 个生产 C# 文件、40,870 行和 202 个测试文件。删除 `ProjectDtoMapper.MapStyleColor`、`MapNonNegative` 和 `LyricContentEditUseCase.ExecuteCandidate` 三个无调用私有实现；`StyleTemplateLibrary.SaveCore` 与 `ScriptConfigurationLibrary.SaveCore` 在已验证的固定数组上复用私有编码 helper，公开 `Encode` 和保存入口仍各自验证。Core 1317/1317 通过；三组变异分别先红后恢复为 2/2。Application 首次 691/695，原因是归档误改示例包 README 导致内容哈希变化；恢复原字节后重跑 695/695，失败和差异保留。
- `artifacts/repository-hygiene-20261029/host-review.md` 覆盖 Contracts、Hosting、Server、Worker：271 个生产 C# 文件、34,611 行和 162 个测试文件。删除 `ProjectCommandExecutionResult.CopyForTransport` 和 `ProjectSession.AttachResourceReference` 两个无调用内部成员；Server validation/export 诊断查询复用 `TryReadDiagnosticsPage`，保留重复参数优先级、`NumberStyles.None` 和原错误文本。音频分类器修复是本次审查另批准作为独立缺陷修复的行为变更：旧实现 49 项中 9 项失败，修复后 classifier/transport 77/77 通过；最终有限合同、部署和 HTTP 结果见下方分层记录。
- `artifacts/repository-hygiene-20261029/runtime-review.md` 覆盖 Backend.Rendering、Backend.Rendering.Skia、Rendering.Abstractions、Rendering.Skia、Runtime.Hosting、Scripting、Scripting.Abstractions、Text。布局高度和定位阶段分别缓存四角，保留各阶段浮点表达式顺序；Ruby 求解排序移除未使用的 tuple index，保留 `OrderBy` 对相等键的稳定顺序。该静态报告本身未执行构建、运行、HTTP 或渲染采样，也不作性能推断。
- 三份审查合计覆盖 18 个项目；扫描覆盖不等于逐行证明。审查深入持久化闭包与内容哈希、双 schema、prepare/commit 的 no-op 和 stale、CAS 两阶段与锁、Worker IPC 及渲染资源链路，没有因低引用数删除公共或动态入口。保留跨信任边界的重复校验和错误顺序、二次 Writer 的 owner/revision/ClosePending 检查、租约及 reservation 释放、PID/startTicks 身份、Media EOF/cancel、Export 预乘转 straight alpha 与主错误、捕获图预算和重放、Vulkan 原生回调保活与 GPU 不回落；这些操作各有独立语义或生命周期，不能只因形式重复合并。本文件不替代测试日志或合同验收。

### 保留热点与后续候选

- 公共音频播放链和 Timeline 几何虽非当前 Server/Worker 调用入口，仍是共享库公开合同并有专门测试，未按仓库内引用数删除。工程和歌词各自的 schema、内容哈希、资源闭包及原子发布也承担兼容与损坏检测职责。
- 两库保存流程的早期修订检查、最终 CAS、包先于索引发布和通知隔离各有时序职责；保留索引发布的 JSON 规范化，因为直接发布编码字节会改变实际字节和 `LibraryIndexRevision`。`VideoExporter` 与 `CompositionFramePlan` 帧计算在极值下并不等价，后续若合并需要另做溢出和行为审查。
- 工程和多个 preset 可重复调用 `InspectResourceClosure`；每个角色的预算、合并和错误顺序必须保留。后续可独立研究单次锁内只读投影的检查结果复用，仍逐次计费，不采用跨对象或跨修订缓存。Worker 多协议调度与 Hosting registry 是维护热点，但其 gate、correlation、取消、关闭和租约顺序不同，本轮未泛化。
- 渲染的公开捕获入口分别验证独立调用、可逆变换、预算和错误顺序；图输入选择还涉及计费、取消和资源所有权。`MaterialAuthorGraphCache` 的按位输入匹配和有限共享边界、Vulkan 委托保活均保留；未来优化须分别验证像素、诊断、预算、资源生命周期与失败顺序。

## 归档与链接结果

截至本次整理，旧根入口、历史文档和必要导航由 `docs/archive/document-map-20261029.json` 逐项登记：共 189 条基线文档记录，其中 113 条正文迁入 `decisions`、`history`、`plans`、`research`、`acceptance` 或 `legacy-ui`，76 条保留原位置。保留项包括当前入口、业务合同、未完成范围和历史参考材料，不能全部视为当前计划；映射说明各自主题与适用边界。22 个旧路径保留导航页；完整旧 `AGENTS.md` 和旧根 `README.md` 快照保留在 `docs/archive/history/`。

迁移前检查基线为 252 个 Markdown 文件、3072 个本地链接、202 个已存在问题：153 个已移除桌面源码/测试目标、9 个已移除 UI 兼容工具目标、5 个缺失图片/许可入口、35 个历史锚点未匹配。最终检查扫描 279 个 Markdown 文件、3166 个本地链接，原有问题剩余 200 项、修复 2 项，新增门禁问题为 0；另有原字节 README 快照迁入新目录后产生的 33 项相对链接问题，因快照不重写而单独排除在新增门禁之外，原始总问题数为 233。归档正文与 `4e70334` Git blob 的 SHA-256 和仅链接重定位规则一致。

`docs/performance/` 的 28 个历史证据文件及 37 个 `samples/ScriptEffects` 跟踪文件均通过哈希保持检查；示例包 README 的一次意外改写已恢复，原始差异保留在 `artifacts/repository-hygiene-20261029/effect-readme-link-diff-before-restore.patch`。检查命令、解析依赖、基线分类和失败门禁见 `docs/archive/README.md`、`tools/check-document-archive.py` 与 `tools/document-archive-requirements.txt`。

当前文档入口为 0.107.4／合同1.39.0；旧性能、分支、UI 和产品阶段数字仍按历史归属解释。合同生成物及 `freeze.py` 元数据由合同流程独立更新，文档归档未修改这些生成输入与正文。

## 最终验收登记

以下按日志来源分别记账，运行次数可能重叠，不相加为最终版本全量测试。证据均在 `artifacts/repository-hygiene-20261029/`。

**早期宽回归与环境修正。** Core 1317/1317、Application 恢复示例 README 后 695/695；此前 Application 691/695 的 4 个失败及哈希差异见 `domain-tests/`，恢复补丁留存于 `effect-readme-link-diff-before-restore.patch`。早期 Rendering CPU 1148/1148、Scripting CPU 266/266。Runtime.Hosting 70/124、Hosting 1429/1440、Server 482/484 的首次运行分别失败 54、11、2 项；日志有 Worker 包编译参考集不匹配和 `WorkerPackageProfileMismatch`，Hosting 另有一次作者工作流超时，不能将全部失败归为同一原因或称作基线缺陷。重新构建匹配 Worker、Host 和测试程序集后，`matched-final/assembly-sha256.txt` 记录的五份 `Scripting.Abstractions.dll` 哈希一致，同目录 Runtime.Hosting 124/124、Hosting 1440/1440、Server 484/484，Backend.Rendering 275/275、Backend.Rendering.Skia 9/9，Vulkan 定向 37/37。该组在后续 0.107.2–0.107.4 修复之前执行，不冒称最终版本宽回归。

**定向回归与失败保留。** 分类器旧实现 40/49、修复后 classifier/transport 77/77；两个库三组变异各红 2/2、恢复绿 2/2；Server 诊断分页变异 7/9、恢复 9/9。Worker 后续分析 MOV 修复 109/109，导出 MOV 修复 113/113，Runtime 导出定向 19/19；编码器门禁修复的 Export 测试 19/19、Worker 113/113，见 `export-encoder-policy.log`、`worker-encoder-fix.log`。另有早期 Runtime 导出 19/33、核心 0/19 的失败日志及后续 33/33、19/19 重跑，记录于 `runtime-hosting-export*.log`，不以重跑覆盖失败。

**独立红绿复核。** 基于 `c1ff0f2` 的隔离源码副本恢复三个修复前判断式，重新编译运行新增回归：分析 MOV 与导出 MOV 各 2/4 通过、2 失败，恢复后各 4/4；编码器 MOV 门禁及既有未知格式拒绝合计 1/2 通过、1 失败，恢复后 2/2。TRX 分别为 `final-independent-review/results/worker-old-red.trx`、`worker-restored-green.trx`、`encoder-old-red.trx` 和 `encoder-restored-green.trx`；复核报告与恢复后文件对照见 `final-independent-review.md`。编码器用例验证 MOV 门禁与未知格式拒绝，不能推断 AAC/Ogg 或真实编码及 HTTP 成功。隔离审查还指出编码阶段对不同压缩格式的错误参数不同，输入经多次路径打开，SHA 复核不证明单一不可变句柄或并发替换安全；此部分是静态审查，未执行恶意容器或并发篡改实验。

**构建与合同。** `contract-final4/solution-build.log` 的 solution 构建为 0 警告、0 错误；该目录 metadata 导出、freeze write/check 与 validate 的 `.exit` 均为 0，静态校验记录合同 1.39.0、102 paths、113 methods、560 schemas、3331 examples（2483 反例）。这些是静态合同结果，不代表 HTTP 已通过。此前 `contract-final`、`contract-final2`、`contract-final3` 保留各自源码阶段与结果。

**Fresh HTTP 失败历史。** `http-final2` 使用 clean 发布源码 `4553027`，M4A 分析终态失败（`WorkerAnalysisInputRejected`）；随后 0.107.2 修复分析 MOV 列表判断。`http-final3` 使用 `6b5d42b`，M4A 导出终态失败；随后 0.107.3 修复 Worker 导出 MOV 列表判断。`http-final4` 使用 `ed7bc3c`，分析 Oracle 指向不存在的路径而中止；`http-final5` 使用同一发布，Oracle 可执行后 M4A 导出终态仍失败，随后 0.107.4 修复编码器格式门禁。这四次均保留各自 `failure.txt`、请求响应与发布记录，不计入成功验收。

**最终匹配发布与 fresh HTTP。** 产品 0.107.4 的 `dist-final5` 来自生产源码 `bea622cd860dab98827db430d575d0ad6cfd553f`，915 个生产输入与 1022 个发布文件匹配；发布 manifest SHA-256 为 `3f8e335af29ade67c142b3f243890dcb35f54ca61b84a505aad00d64328e1a21`。`sourceDirty=false` 仅指生产源码及构建配置输入，运行时交付文档仍在修改。Oracle 为本机 net10.0 独立 Release framework-dependent apphost，共 37 文件。

`http-final6` 完成 318 请求／1976 断言；9 个正式任务（4 分析、4 导出、1 验证）均成功且终态不可取消。4 组分析与原 Analyzer 完整 canonical 字节一致；4 个带音轨 MP4 各 3 帧，合计 12 帧，视频和音频完整解码；验证覆盖普通／透明各 1 帧，两库覆盖 CAS 拒绝和重启重载。两次 Server／launcher 均 exit 0，分别排水 55 和 59 个观察身份，最终 59 个均退出、`remaining=[]`。原生证据扫描 741 文件；另一次补充扫描覆盖发布目录 1023、Oracle 37、证据 742，共 1802 文件，零凭证命中，与原生扫描分别记账。manifest SHA-256 为 `6ce7e0e36fa10f6bfc204eb54fe66df5bcdeec4a2ad877cddeaa69ae003d7c29`。

HTTP 合同校验检查 305 个配对 JSON 响应、22 个成功 JSON 请求，13 个未配对二进制工件跳过，未映射为 0；冻结合同仍为 102 paths／113 methods／560 schemas。独立 assessment `release-http-final6-assessment.json` 的 SHA-256 为 `99092abb8afbc720f268519b834fa9764c24009d682f83d1addc3f94b7874776`，合同和补充扫描分别见 `contract-http-final6.json`、`http-final6.supplemental-credential-scan.json`。主代理另复核已有证据：1022 发布文件、915 生产输入、16 工具及 37 Oracle 文件、523 合同输入与 4 产物哈希一致，59 个观察身份当前均不存在，wrapper/native 哈希一致，无关未跟踪文件集合及哈希保持；此复核不作为新增运行测试。

**范围。** final6 仅覆盖本机 Linux、CPU 和四个固定容器样本；导出验证完整传输身份、流存在和解码，未建立独立像素或有损音频质量 oracle。Ogg 非法版本和短头属于单元红绿，未纳入本次成功 HTTP；两库使用一个完整样例包，不涵盖脚本编译或作者草稿恢复。排水仅证明观察到的进程身份退出。文档归档检查见上一节；浏览器、跨平台、长片、性能、通用恢复矩阵及仓库全量运行验收均未由本轮推定完成。
