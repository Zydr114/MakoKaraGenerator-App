# CPU/GPU 显式渲染 API 实施计划

日期：2026-10-26。分支：`dev/gpu-render`。起点：`45a5d816ab2845227e5969e12d47dca7a8be93df`（产品 0.105.0，合同 1.38.1）。状态：产品0.106.0／合同1.39.0已实施和冻结，匹配发布的本机有限fresh HTTP及GPU不可用负例通过；分层回归收口见[本批验收报告](../acceptance/render-backend-api-acceptance-20261026.md)。该开发分支已于2026-10-27随95个提交快进纳入main，见[main合入记录](../history/backend-main-integration-20261027.md)；原验收来源和数值不变。

## 1. 目标和固定决策

前端能够查询本机渲染后端、为每次预览或正式渲染任务显式选择 CPU/GPU，并切换已经存在的预览。公开值为 `cpu` 和 `gpu`；本批 `gpu` 的实际 API 为 Vulkan。偏好由前端维护，不增加工程内容、撤销历史、服务器全局可变开关或隐式跨客户端设置。

创建预览、验证、导出和测量的 `renderBackend` 可省略或为 null，此时使用服务启动时固定的默认值。原 `MAKO_RENDER_BACKEND=cpu|vulkan` 继续作为部署默认，未设置仍是 CPU；显式请求覆盖默认。更新已有预览时省略/null 保留当前选择。空串、其他大小写、`auto`、`vulkan` 等公共值均拒绝。GPU 不可用或执行失败时明确失败，不回退 CPU。

产品已升级为 0.106.0、合同升级为 1.39.0，HTTP 协议仍为 1。既有请求的省略行为继续使用原部署默认。

## 2. 公共 API

### 可用后端

新增 `GET /api/v1/render-backends`，沿用现有 client 授权。响应公布启动默认、`cpu`/`gpu` 两项的可用性、实际渲染 API、允许公开的设备摘要，以及不可用的稳定错误。检查必须在隔离 Worker 中真实创建设备、小表面、绘制、提交和读回；不能将源 profile 握手当成 GPU 设备可用。检查结果是当前部署的可用性快照，不保证未来驱动或资源永不失败。

### 选择与回显

- 所有公开预览创建入口统一接收可选 `renderBackend`：工程、模板工坊、预设候选、编辑候选、作者草稿。
- 原预览 `PUT .../input` 可指定后端；省略保留当前后端。原有来源切换限制保持。
- 新增 `PUT /api/v1/preview-sessions/{id}/render-backend`，请求为 `expectedPreviewGeneration`、必需 `renderBackend`、原回执合同要求的 `metadata`。适用于所有预览类型，只切换固定输入的执行设备；DTO 的 nullable 默认不代表可省略成功请求的回执身份。
- 工程/作者正式 validation、所有 export 来源及 scene measurement 接收同一可选字段。编辑候选测量省略时使用候选的固定后端，显式字段可以选择另一后端而不修改候选。
- 预览响应、帧和事件，以及固定 job input、验证/导出报告、测量响应回显解析后的选择。选定后端不等同于成功产帧；继续依据既有状态/终态判定执行成功。

## 3. 预览切换事务

在预览注册表内核对 client、session、状态和精确 generation，取得原固定 snapshot、来源/候选身份和媒体闭包的独立保留引用。不得重新捕获工程或草稿；这会把纯设备切换错误地变为内容刷新，而且固定来源可以比原工程会话活得更久。

目标后端先经过实际设备检查和来源能力检查，然后在提交时再次核对原 generation/对象身份。失败释放准备租约，旧代次、帧和输入保持。成功原子换代、清理旧帧/诊断、使在途旧帧失效并排水旧 Worker。相同后端通过 CAS 后为 no-op，不换代或丢帧。并发切换只有符合 CAS 的请求能提交。设备检查不执行用户脚本；正式来源在后续渲染失败仍走原失败/排水机制。

## 4. Worker 和任务固定

`WorkerProcessOptions` 显式携带选择，在唯一进程启动点仅写新子进程的环境。禁止修改 Host 的全局环境，禁止使用 AsyncLocal 将未固定选择隐式带入排队任务。每个任务在准入时固定后端及对应 executor；运行后改变前端偏好不影响已排队/运行任务。

新增对称 Worker 设备探测合同，核对请求选择与实际设备类型，拒绝旧 Worker 缺能力/错误回传。正式执行 Worker 必须使用固定配置；私有握手、实际渲染和异常映射保持可审查，不能用一台探测 Worker 的结果冒充另一台 Worker 的运行证据。

所有 static、package、image、统一 Runtime 路径均须传播选择。测量内部供命令使用的旧调用仍有明确默认；字体和音频准备不因这个开关变化。持久 job input/report 保存固定选择，重启读取不重跑，不把旧报告缺字段解释为已经验证的 GPU。

## 5. 实施分工和提交

实际编码由既有 GPT 6.1 Sol xhigh 子代理完成，主代理设计、审查与验收。

1. 计划提交及前端候选通知。
2. Worker 设备探测、显式选择及共享选择服务；定向协议/设备回归。
3. 公共 DTO、预览入口、固定输入切换和生命周期回归。
4. 正式任务、报告/持久化、测量选择及相关回归。
5. 真实切换 probe、产品版本、行为合同、生成器映射与正反例。
6. 新鲜 metadata → freeze/validate → 匹配 self-contained Server/Worker → fresh HTTP → 最终合同验证和交付文档。

按文件所有权并行，所有 dotnet 操作串行持有 `/tmp/mkg-gpu-render-dotnet.lock`。每个可审查步骤单独提交并推送当前分支。保留原未跟踪 `tools/backend-project-image-export-probe/`。

## 6. 主代理审查和验收

- 严格值域、默认/省略/null、旧请求兼容；metadata 重试相同请求返回相同结果，改变后端不复用旧回执。
- 同一预览 CPU→GPU→CPU，所有预览类型可切；同后端 no-op、旧 generation 冲突、失效旧帧/诊断、失败不换代、并发切换和关闭排水。
- 两客户端不同选择并发，验证不同 Worker 的实际后端；Host 环境不变、GPU 明确失败不降级。
- 选择贯穿 project/author 验证、普通/透明导出、static/package/image/runtime、普通/候选测量；job 入队固定及恢复报告保持。
- CPU 相关回归，匹配 Worker 的 Server/Hosting 回归；显式本机 GPU 实跑和 PNG/透明/短片有效性。
- 新发布记录 sourceCommit/sourceDirty/生产输入，fresh HTTP 留档、实际硬件 Worker 身份/DRM 证据、Server 正常退出和全部观察进程排水、凭证扫描。
- generated OpenAPI/index/examples/manifest 只由新 metadata 和工具生成；正反例及 fresh HTTP JSON 单独验证。

本批验收仍以当前开发机实际运行成立；前端按钮/浏览器集成由前端负责人交付，跨平台、性能和 GPU 编码不从本批后端验收推导。

## 7. 实施与验收记录（2026-10-26）

Worker显式选择/实际设备核验、所有公开渲染入口的选择传播、固定预览设备切换、任务准入固定及持久报告回显已接通。严格generation前置检查和严格JSON请求解析分别由 `077f2c9`、`9506942` 修复；最后生产基线为 `9506942118efce3f1869e0cbc9e7da1af1869b42`。省略/null默认、历史未知、GPU失败不回落及原来源限制按§1～4保留。

`artifacts/render-backend-api-20261026-r1/dist-final1/` 为该基线的匹配self-contained linux-x64 Server/Worker，`sourceDirty=false`，911生产输入/1022部署文件独立逐一核对，部署manifest SHA-256 `5467a15380a6aaa8075b1b9c903e269ce1675150077fe3e437ec585ae1a3d966`。新metadata冻结检查通过，合同1.39.0为102路径／113方法／560 schemas／3331例（2483反例）。

最终可用硬件 `http-final2/` 单次770请求／5013断言，独立不可用环境 `http-unavailable-final1/` 46／343通过。五类预览CPU→GPU→CPU、no-op/重放/旧代次/ACL、两client、原工程变更及关闭后的固定source、project/author input省略保留GPU、CPU/GPU正式验证、GPU作者720帧、普通/透明各24帧无音轨导出和普通/候选测量均有实际HTTP证据。48个Worker取得硬件证据；157/40观察身份全部退出、两次Server exit0、remaining均空，1680/116文件凭证扫描通过。实际JSON744响应/104成功请求及43/13匹配合同，两组0无配对产物。

本地回归、独立媒体alpha、原始失败与探索目录的归属详见[验收报告§3～6](../acceptance/render-backend-api-acceptance-20261026.md#3-本地回归)。同步测量的硬件证据仅定位请求区间，不能精确区分设备探测和测量执行Worker。本批不包含浏览器、跨平台、性能、GPU视频编码、独立Runtime内容oracle或CPU/GPU位级像素相同结论，旧0.105数字保持原版本归属。
