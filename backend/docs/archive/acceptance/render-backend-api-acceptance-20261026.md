# CPU/GPU 显式渲染 API 本机验收（2026-10-26）

状态：本批显式渲染API实施、审查与本机有限验收完成。工作分支为 `dev/gpu-render`，产品 `0.106.0`、合同 `1.39.0`、HTTP 协议 `1`，未合入 `main`。实施由 GPT 6.1 Sol xhigh 子代理完成，主代理负责设计、源码审查与独立验收。行为见[统一 API §25](../../architecture/backend-api.md#render-backend-selection)、[实施计划](../plans/backend-render-backend-api-plan.md)及 [ADR-0240](../decisions/decisions.md#adr-0240)。

## 1. 已实现行为

公开 `renderBackend` 严格为小写 `cpu|gpu`，本批 GPU 使用 Skia Ganesh/Vulkan。`GET /api/v1/render-backends` 在隔离 Worker 中创建设备、绘制、提交并读回后返回可用性；它不保证后续来源执行或驱动永不失败。部署默认在启动时从 `MAKO_RENDER_BACKEND=cpu|vulkan` 固定，未设置仍为 CPU。

创建预览及验证、导出、普通测量省略或 null 使用启动默认；已有预览 input 更新/候选替换省略或 null 保留当前选择，候选测量省略或 null 沿候选固定选择。各任务在准入时固定选择及 executor，在预览/帧/事件、job input、验证/导出报告和测量响应中回显。历史缺失字段仍表示未知。GPU 不可用或执行失败明确失败，不自动回落 CPU；前端偏好不写工程内容、修订、历史或服务器全局开关。

`PUT /api/v1/preview-sessions/{id}/render-backend` 接收精确 `expectedPreviewGeneration`、必需且非 null 的 `renderBackend` 和 metadata。project、authorDraft、template、presetCandidate、editCandidate 五类预览复用原固定来源与完整媒体引用。准备前和提交时核对 owner、会话对象及 generation；同值 CAS 后 no-op，成功切换换代并失效旧输出，失败保留旧输入、代次、帧及诊断。切设备不重新捕获工程/草稿，原 input 路由的来源限制继续保持。

## 2. 来源、部署与合同

证据根目录为 `artifacts/render-backend-api-20261026-r1/`，原始 HTTP、媒体、私有 bootstrap 和发布产物不提交。最后生产修改为严格 JSON 解析修复 `9506942118efce3f1869e0cbc9e7da1af1869b42`；合同基线与匹配发布均指向此提交。后续 probe 修订不改变该发布的生产输入。

| 证据 | 结果与边界 |
| --- | --- |
| `dist-final1/deployment.json` | 匹配 self-contained `linux-x64` Release Server/Worker；sourceCommit `9506942`，`sourceDirty=false`，911 个生产输入、1022 个部署文件逐一核对 |
| 发布 manifest SHA-256 | `5467a15380a6aaa8075b1b9c903e269ce1675150077fe3e437ec585ae1a3d966` |
| `contract-final-9506942/assessment.json` | 从该生产基线重新构建 metadata 后 freeze/write/check/validate 通过；102 paths、113 methods、560 schemas、3331 examples（2483 反例） |
| `contract-final-9506942/final-http-validation.json` | `http-final2` 的 744 份实际 JSON 响应、104 份成功 JSON 请求，以及 `http-unavailable-final1` 的 43 份响应、13 份成功请求匹配合同；26/3 项非 JSON 或未映射读取单列，两组均 0 无配对产物 |
| `contract-final-9506942/independent-evidence-audit-pre-docs.json` | 独立核对生产输入与 Git blob、全部部署文件 SHA、511 个合同 guard 文件/4 个合同产物、两组 HTTP 完整清单与 SHA、发布引用及进程身份；差异/残留均为空 |
| `audit-final/independent-source-http-audit.json` | 主代理再次核对911生产输入与 `9506942` Git blob及当前树、1022部署SHA、1680/116 HTTP inventory均一致；157/40观察身份无存活、两次exit0 |
| `audit-final/metadata-build.log`、`clr-metadata.json` | 文档收口时再次构建metadata工具零警告/零错误；新metadata与 `9506942` 导出逐字节相同，SHA-256 `407b862940f63868fe80e61f288dc1d8ce129e58d92a5f19d762c50fef208e5b` |
| `audit-final/freeze-write.log`、`freeze-check.log`、`final-http-validation.json` | 文档更新后重新生成/检查/验证均exit0；102/113/560及3331/2483保持，实际JSON仍为744/104及43/13，两组0无配对产物 |
| `audit-final/independent-contract-audit.json` | 最终511个guard文件、4个合同产物及470个部署输入交集独立核对无差异；最终合同manifest SHA-256 `f3c9097fcdfe03dedd2b3ff3bcd1b1d0a1107870bab64bed8ef41fd9c96604d8` |

生成物只由新 metadata 与 `freeze.py`/`validate.py` 更新；合同静态形状、部署来源和实际业务运行分别记账。最终manifest重冻结记录当前文档/工具指纹，不改变本次不可变发布的生产来源。

## 3. 本地回归

最终被测源码固定为 `70e45c89432fd27239955aac93f15e575faf73d9`，生产输入与 `9506942` 发布保持一致。Release solution构建零警告、零错误；最终Debug Worker→Server.Tests→Hosting.Tests依次构建，核对168项复制引用与SDK/Worker身份后执行定向验收。主代理独立读取TRX及SHA，汇总于 `local/review-final3/acceptance-summary.json`：

| 证据 | 最终结果 |
| --- | --- |
| `local/review-final/solution-release-build.log` | 完整Release solution构建零警告、零错误 |
| `local/review-final2/server-preview-results/server-preview.trx` | Server PreviewTransport 414/414，0失败/跳过/中止；原两个高级Line/Token模板场景均通过 |
| `local/review-final3/hosting-results/hosting-all.trx` | Hosting 1410/1410，0失败/跳过/中止；包含本批生命周期/选择/任务与既有Hosting回归 |
| `local/worker-final.log`、`local/tests-final/worker-final.trx` | 本批Worker定向105/105；由主代理核对原日志与TRX，不借用0.105历史数字 |
| `local/render-backend-hosting/render-backend-hosting.trx` | 早期本批生命周期/合同定向17/17；属于Hosting最终范围的子集，不重复累加 |
| `local/strict-switch/strict-switch.trx` | 严格JSON14/14；属于Server最终范围的子集，不重复累加 |

早期Release Server 412/414、Hosting 1399/1410与Debug Server 412/414的原始结果保留于 `local/review-final/`，不是最终通过结果。`local/review-final2/reference-mismatch-diagnosis.json` 核到旧SDK/SourceLink构建引用及测试/Worker复制副本不一致；固定同一HEAD顺序重建并核对引用后，原模板及SDK类失败消失，没有为此修改生产源码。Hosting首次重跑被外层143中止、无TRX，记录于 `hosting-interruption.json`，不作失败测试数或通过测试数。作者排队编译 `action:source` 曾超时，最终同一测试2.96秒通过；本次没有独立证明该偶发超时的根因已修复。

更早的 `local/hosting-final.log` 与 `local/tests-final/hosting-final.trx` 保留1397/1398，其中audio bytes夹具触发NullReferenceException。随后隔离重跑 `local/tests-final/hosting-fixed-audio-replay.trx` 18/18、最终Hosting1410/1410通过；这两次通过不证明该偶发夹具异常的根因已修复，也不把1397/1398改写为全绿。

本节是当前分支的定向回归和构建结果，不称为重新执行完整CPU solution测试，也不复用0.105完整CPU6159/6159或GPU分层数字。

## 4. 硬件可用的 fresh HTTP

`http-final2/summary.json` 为 **770 请求／5013 断言**，单次运行通过。24 份预览 PNG 完整解码并记录 PNG/RGBA SHA；返回 CPU 的帧与切换前 CPU 帧完整 RGBA 相等。CPU 与 GPU 之间没有要求位级相等，也没有把本批预览与独立 Runtime 内容 oracle 比较。

| 实际工作流 | 已核内容 |
| --- | --- |
| 五类预览 CPU→GPU→CPU | project、editCandidate、template、presetCandidate、authorDraft 均切换成功；固定 source/尺寸/透明模式/候选字段保持，generation 按换代更新 |
| no-op、回执与拒绝 | 同后端保留完整状态、帧与诊断页；同 metadata 字节一致重放，改载荷 requestIdReuse；旧 generation 冲突、非法/缺失 generation 与 backend、跨 client ACL 拒绝 |
| 两个 live client | 各自保持不同 CPU/GPU 选择及独立帧；Host 的渲染选择/ICD 环境前后不变 |
| 固定来源与 input 更新 | 原工程修订变化后只切设备仍保留旧固定 source；原工程关闭并移除后仍可 GPU→CPU，返回 CPU 的完整读回与关闭前一致。project 与 authorDraft 的 input 更新另建预览验证，省略 backend 保留 GPU、显式 CPU 更新成功 |
| 工程正式验证 | CPU/GPU 两个独立 job 接受后固定 input，工程修订后续变化不改变已登记来源；完整报告读取、bytes/size/SHA、终态及选择回显通过 |
| 作者正式验证 | GPU 作者试样验证成功，普通/透明各 360 帧，共 720 帧；完整报告与固定作者 input 回显通过。正式 validation 不产生 PNG，不表示像素验收 |
| 场景/候选测量 | 普通 CPU/GPU 测量、候选省略选择沿固定 GPU、显式单次 CPU 且候选继续 GPU；测量 PNG 完整解码、size/SHA/选择回显通过 |
| GPU 短片导出 | `opaqueMp4` 与 `transparentProRes4444` 无音轨各 24 帧；job input/report 固定 GPU，报告/媒体完整 bytes、size、SHA 一致，FFmpeg 全部 RGBA 帧解码长度通过 |

硬件证据位于 `gpu-worker-evidence.json`，仅采集本次发布的 owned Worker 后代，并按 PID/start ticks 复核身份。119 个观察 Worker 身份中，48 个取得 Vulkan loader、固定 RADV ICD 驱动映射及 DRM render fd 的硬件证据。9 组 GPU 帧绑定本次帧的 Worker；author-validation、validation-gpu、两种 export 的 240 份 running-task 采样按新鲜 Worker 身份绑定任务。可用性探测 Worker 与正式业务 Worker 分开记账。

两组同步 GPU 测量只证明请求执行区间内出现本次新鲜硬件 Worker；该区间可同时包含设备探测和测量执行 Worker，现有采样不能精确区分二者，不冒称已逐个定位测量执行 Worker。

主代理另以 `audit-final/independent-media-audit.json` 独立核两份媒体：MP4 为 H.264/yuv420p，MOV 为 ProRes4444/yuva444p12le；均为320×180、24fps、1秒、24完整帧且无音轨，各解码5,529,600字节完整RGBA，媒体/report size与SHA一致。普通片全帧alpha均为255；透明片每帧都有0、部分透明及255，首帧分别为54,780/1,515/1,305像素。两个首帧已目视确认存在“CPU GPU original”文字。该有效性/alpha核验没有与独立Runtime内容oracle比较，也没有证明CPU/GPU位级等同。

Server 正常 exit0；157 个观察进程身份（含上述 119 个 Worker）全部退出，`remaining=[]`，主代理独立复核时无身份仍存活。1680 个证据文件凭证扫描通过，完整 manifest 的文件清单和 SHA 独立核对无差异。

## 5. GPU 不可用的 fresh HTTP

`http-unavailable-final1/summary.json` 为独立 **46 请求／343 断言**，通过隔离的不可用 Vulkan 环境执行。可用性响应给出 GPU unavailable；显式 GPU 切换和 GPU 新建返回503 `taskUnavailable`、`failureCode:RenderBackendUnavailable`，切换错误带 `field:renderBackend`，没有生成替代 CPU 成功结果。

失败切换前后完整 CPU 会话、generation、诊断页保持，旧 PNG 内容句柄仍可认证读取且全 RGBA 一致，后续 CPU 请求仍能正常产帧。0 个硬件确认 Worker 为该隔离负例的预期结果。Server 正常 exit0，40 个观察身份（含 20 个 Worker）全部退出，`remaining=[]`；116 个证据文件凭证扫描通过，清单与 SHA 独立核对通过。

## 6. 保留的失败和探索记录

各目录保持原计数，不追加到两组最终通过数字。

| 原目录 | 结果及修订归属 |
| --- | --- |
| `http-explore1/` | 58 请求／412 断言；跨 client 拒绝码预期与真实 ACL 边界不一致，probe 后续采用真实合同 |
| `http-explore2/` | 346／2298；导出夹具缺少满足领域约束的 clip，返回422 exportInputRejected；工具补齐夹具，未放宽产品导出门禁 |
| `http-explore3/` | 13／103；夹具回执 requestId 重用导致409 requestIdReuse，工具改为独立意图身份 |
| `http-explore4/` | 37／267；缺 generation 落为409 冲突；产品 `077f2c9` 固定非法/缺失 generation 的400边界，`9506942` 进一步收紧严格 JSON 解析 |
| `http-final1/` | 409／2746；作者 input 更新期间访问令牌过期，返回401 clientTokenExpired；probe `70e45c8` 为每个 client 增加贯穿矩阵的25秒 heartbeat，重新使用全新 `http-final2/` |
| `http-unavailable-explore1/` | 45／330 通过；早期探索保持独立，不替代最终46／343负例 |

后续 probe 还将“固定旧 source 切设备”和“刷新 input”拆为独立预览，避免用内容刷新误测设备切换；硬件采样保留已确认的映射/DRM 观测，并要求业务任务绑定本次新鲜 Worker 身份。透明导出使用真实公开值 `transparentProRes4444`。这些修订有各自工具提交与原失败目录，不回写不可变生产发布。

## 7. 验收边界

本批关闭的是当前 Linux/RADV 开发机上 CPU/GPU 显式选择 API 的有限运行矩阵，包含固定预览切换、任务选择回显与 GPU 不可用拒绝。合同合成例、本地回归、HTTP、硬件采样、媒体有效性、来源 SHA、凭证扫描和进程排水各自保留证据边界。

本批没有浏览器按钮/交互验收、跨平台/驱动矩阵、吞吐或长片性能结果、GPU 视频编码、独立 Runtime 内容像素 oracle 或 CPU/GPU 位级像素相同结论。历史0.105的完整 CPU/GPU管线数字见[原报告](gpu-render-acceptance-20261026.md)，不升级为0.106本批新跑结果；当前 source/profile/媒体/字体支持也不因设备选择自动扩大。
