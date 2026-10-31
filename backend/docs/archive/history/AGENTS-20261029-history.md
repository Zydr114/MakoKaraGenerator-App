# AGENTS.md — 项目入口（写给在本仓库工作的 AI 代理）

> **2026-10-28 main当前入口：** 已按用户授权，将 `dev/performance-optimization` 的 `77643f8` 从祖先 `1b2d375` 快进纳入 `main`，完整保留19个提交，包含两批性能优化、结构二次调研、统一性能日志及验收证据。产品0.107.0／合同1.39.0／HTTP协议1。合入核验确认915生产输入、1022既有发布文件、5份归档内1391文件及合同检查通过；本次仅Git集成和文档，不增加构建、运行测试、发布、HTTP或性能采样结论。原dev分支及无关未跟踪 `tools/backend-project-image-export-probe/` 六文件保留。当前依据见[main合入记录](backend-main-integration-20261028.md)，原验收归属及500ms限制见[结构优化验收](../acceptance/backend-gpu-structural-performance-optimization-acceptance-20261028.md)。

> **历史2026-10-28 结构优化与统一监控实施验收：** 当时工作分支`dev/performance-optimization`，产品0.107.0／合同1.39.0／协议1。生产`5802ac1`已接入受限颜色匹配直接读回、同对象无输入作者图共享与Worker性能日志（默认summary，支持off/detailed，Server转发为不可信遥测）。正常solution零警告错误、4045次相关测试执行全部通过；匹配clean发布`103ba2c`的摘要HTTP2032请求／13313断言和详细HTTP304／3951通过，152／54观察身份全部退出。首次HTTP的Oracle构建来源差异失败单独保留。用户恢复测试后，七标签正式矩阵通过，246进程／4560帧，1956对旧新、608对原版／插桩及432对监控开关完整像素诊断一致。图／材质／组合Vulkan中位降低64.89%／73.82%／73.61%；材质／组合主矩阵最大346.453／373.706ms，首帧和独立开销矩阵仍有超过500ms样本。summary的Vulkan组合观测中位开销10.70%，detailed限速丢弃27条但摘要完整。计划、精确边界、失败与证据见[验收记录](../acceptance/backend-gpu-structural-performance-optimization-acceptance-20261028.md)、[监控说明](../../backend-performance-monitoring.md)。该次验收完成时尚未合入main，随后集成见上方入口；无关未跟踪`tools/backend-project-image-export-probe/`保留。

> **历史2026-10-28 GPU结构二次调研：** 当时分支 `dev/performance-optimization`，调研基线 `61ee27e`，产品0.106.1／合同1.39.0保持。锁定源码与隔离实验定位到 BottomLeft＋sRGB 图像 raster 化时使用 null 目标颜色信息的较重读回路径；独立副本保留同步、改为颜色匹配直接读回，三轮 graph／material／combined 中位耗时降低66.80%／70.72%／67.92%，161对同后端完整BGRA及诊断一致，组合最大506.331ms，500ms目标未达成。TopLeft替换出现5/20对Runtime像素差异，不推荐直接采用。纯歌词没有中间读回，差距主要落在最终读回与同步；逐token材质图重复执行仍是后续共享重点。本轮仅调研与隔离候选证据，无生产修改／完整回归／发布／HTTP，不能当作第二批优化已交付。见[二次调研](../research/backend-gpu-structural-performance-research-20261028.md)及[证据](../../performance/20261028-structural-research/README.md)。

> **2026-10-27 GPU性能优化调研：** 基于既有短批次基线与当前源码，确认材质样本16次材质调用／48次图Raster／112次预乘验证＋54次原始部件读回。建议先去重不可变图像验证、减少可证明为空的捕获，再研究同帧作者图共享、GPU全域验证小结果读回及受限ROI。500ms/帧为固定本机样本的期望，材质／组合计量帧最大值需下降75.96%／81.40%，尚无优化收益承诺。完整调用链、版本依据、语义约束和实施顺序见[调研报告](../research/backend-gpu-performance-optimization-research-20261027.md)。本轮仅文档，无新构建／测试／渲染或生产修改，产品0.106.0／合同1.39.0保持。

> **2026-10-27 CPU/GPU短批次性能基线：** 固定生产源码 `8681bc0`、工具 `3d87528`，产品0.106.0／合同1.39.0保持。六类1080p Final不透明样本完成原版wall与独立副本阶段插桩；普通场景8+24帧、材质/组合4+8帧各五轮，另有8帧seek三轮及3帧效果对照。170进程／3310项目帧／170小表面probe、1616对同后端像素和诊断、1688 trace、48效果贡献和72重放对照通过，85个GPU进程有硬件证据且全部进程自然退出；每进程最多32+1帧。图/材质/组合CPU/GPU中位数比2.59／2.31／1.99，轻负载CPU更低；中间读回、捕获及分配为后续调查重点，尚未优化生产代码。结果、波动与口径见[基线报告](../research/backend-render-performance-baseline-20261027.md)，原始JSON/CSV与407文件摘要见[证据目录](../../performance/20261027/)。仅本机直接Runtime短批次，不扩大为HTTP、浏览器、编码、完整工程或长片性能验收。

> **历史2026-10-27 main合入：** 已将 `dev/gpu-render` 的 `9ab24b0` 从祖先 `3bd211f` 快进纳入 `main`，完整保留95个提交，包含P3/P4/P5和两批GPU工作。产品0.106.0／合同1.39.0／HTTP协议1、102路径／113方法保持；后续工作从main继续。合入前定向回归、合同、原发布与留档复核见[main合入记录](backend-main-integration-20261027.md)；本次没有新生产修改、发布或fresh HTTP，历史验收数字各保原归属，无关未跟踪 `tools/backend-project-image-export-probe/` 六文件保留。

> **历史2026-10-26 CPU/GPU显式API验收入口：** 当时工作分支为 `dev/gpu-render`，产品0.106.0／合同1.39.0／HTTP协议1，102路径／113方法；当时未合入main。公开 `renderBackend` 严格为 `cpu|gpu`，GPU实际API为Vulkan；创建/任务省略或null使用启动默认，已有预览更新省略或null保留当前选择，候选测量省略使用候选固定选择。新增 `GET /api/v1/render-backends` 与 `PUT /api/v1/preview-sessions/{id}/render-backend`；后者保留原固定来源/媒体，只按精确generation切换设备。所有公开渲染任务在准入时固定选择并在input/report回显，历史缺失值保留未知，GPU失败不回落。匹配clean发布 `9506942` 的fresh HTTP770请求／5013断言、独立GPU不可用负例46／343均通过；48个硬件确认Worker，157／40个观察身份全部退出，1680／116文件凭证扫描通过。分层回归、媒体、硬件归属限制与失败历史见[本批验收报告](../acceptance/render-backend-api-acceptance-20261026.md)；行为见[统一API§25](../../architecture/backend-api.md#render-backend-selection)、[实施计划](../plans/backend-render-backend-api-plan.md)及[ADR-0240](../decisions/decisions.md#adr-0240)。合同静态门禁及下方0.105证据保留各自归属，不扩大为浏览器/跨平台/性能验收。

> **历史2026-10-26 GPU管线入口：** `dev/gpu-render` 已完成本机Skia Ganesh/Vulkan接入与验收，产品0.105.0／合同1.38.1／HTTP协议1，未合入main。默认CPU，`MAKO_RENDER_BACKEND=vulkan`显式启用且失败不回落。完整CPU6159/6159，Vulkan Rendering1055/1055、后端/Runtime集成513/513与真实设备smoke1/1分层登记；匹配发布`ca3b495`／`sourceDirty=false`的fresh HTTP2376请求／18405断言、52组PNG对比、8短片168帧、6预览及3导出控制通过。49个Worker取得硬件证据，Server退出0、129个观察身份排水、5459证据文件凭证扫描通过。计划、真实GPU缺陷及原有集成修复、源码/部署指纹和限制见[GPU验收报告](../acceptance/gpu-render-acceptance-20261026.md)与[架构](../../architecture/render-skia-gpu-backend.md)。只覆盖当前Linux/RADV机器，不扩大为跨平台、性能、长片、浏览器或GPU编码结论；不得提交无关未跟踪`tools/backend-project-image-export-probe/`。

> **2026-10-25 P5历史入口：** 本次完整后端 API 审查与前端交接收口位于 `dev/backend-api-completion-p2`，产品0.104.1／合同1.38.1／HTTP协议1。`main` 是当前开发线的祖先基线和 GitHub 默认分支，P3/P4/P5尚未合入 `main`；提交和推送应继续当前工作分支，不据默认分支名推定已集成。前端依据为[Web交接入口](../../../web/README.md)、[冻结清单](../../../web/api-index.md)、[OpenAPI](../../../web/openapi.json)与[行为合同](../../architecture/backend-api.md)；本次发现、修订及验证分层见[P5审查报告](../research/backend-api-p5-closure-audit-20261025.md)。

> **2026-10-25 分支整理历史：** 原 `dev/backend-api-completion` 已改名为 `main`，本地跟踪 `origin/main`，GitHub 默认分支为 `main`。当时删除三个无独立提交的旧集成分支，并保留18个仍有独立提交SHA的任务分支；[分支与开发记录](backend-branch-development-records-20261025.md)中的数量与远程列表是整理时快照，后续P3/P4已新增开发及任务分支。历史文档中的旧分支名保留来源含义。

> **2026-10-25 P3/P4已交付范围：** P3完整模板/预设工坊与P4作者草稿、固定源码编译、authorDraft试样预览/验证、个人配置库已接入产品0.104.0；P4原冻结合同1.38.0和有限fresh HTTP证据见[计划§10](../plans/backend-api-completion-plan.md#10-p4实施记录)与[统一API§21](../../architecture/backend-api.md#backend-author-p4)。P5合同勘误1.38.1保持100 paths／111 methods；产品0.104.1另修复预览input跨project/authorDraft来源切换的拒绝边界，P4原HTTP证据不升级为0.104.1验收；浏览器、平台及性能范围仍独立。

> **2026-10-25 P1/P2当前计划代表后端范围完成（产品0.102.0／合同1.36.0）：** 空创建/流式上传、导入规划/Worker字体诊断、媒体背景与编辑候选/原子应用，以及统一Runtime媒体/包字体/高级Line与整曲导出已接入并通过匹配发布fresh HTTP。完整证据与失败归属见[实施§80](backend-extraction-progress.md#api-completion-01020)、[计划§8](../plans/backend-api-completion-plan.md#8-p1p2-实施记录)与[统一API§23](../../architecture/backend-api.md#unified-runtime-input-01020)。P3/P4/P5、浏览器、平台及性能范围继续独立。以下0.99/0.100/0.101等旧入口按当时范围保留，不覆盖当前能力。

> **2026-10-24 已保存工程图片正式验证fresh HTTP有限切片完成（产品0.100.0／合同1.34.0）：** 仅无预设static/static-analysis/直接package基础源的已保存Image主背景，复用既有validation路由、job与报告。独立source profile `project-image-validation-v1`、capability `project-image-validation-capabilities-v1`、task `task-project-image-validation-v1`／`KTI1`；envelope仍为`project-image-preview-v1`，decoder为`memory-image-bgra-v1`，任务输入4,194,272字节（4MiB−32），base snapshot4MiB。Host第二次进入Writer后须复核owner、注册session对象身份、revision与ClosePending，所有失败释放未转移的图片/音频lease及reservation。匹配self-contained Server/Worker发布`dist-7a03863`（源码`7a03863`、`sourceDirty=false`）的fresh HTTP `http-final1`通过1412请求／10582断言；两次Server exit0，33个观察身份（含26个Worker）全部退出、无强制清理，3349个文件凭证扫描通过。三基础源普通／透明各4帧及原Runtime报告Oracle、完整报告与诊断分页、六组实际Worker/child控制和一次orderly restart/recovery通过；正式validation没有PNG，不构成像素验收。旧0.99 Worker拒绝负例10请求／476断言、199文件另计。本地去重242/242与二次Writer变异4红／恢复4绿分层登记；图片export/measurement、preset/video/font、完整B3/B4/B5/B6、浏览器/跨平台与性能矩阵独立。见[独立计划](../plans/backend-project-image-validation-plan.md)、[统一API§10.1](../../architecture/backend-api.md#project-image-validation-api)、[实施§78](backend-extraction-progress.md#project-image-validation-01000)与[ADR-0237](../decisions/decisions.md#adr-0237)。

> **2026-10-23 图片背景预览fresh HTTP有限切片完成（产品0.99.0／合同1.33.0）：** 已保存工程Image主背景经独立 `project-image-preview-v1` Worker能力和固定内存闭包进入原Runtime/Skia预览；公共请求仍只交工程ID与精确修订，原图2MiB/单边4096/BGRA16MiB/完整envelope4194280字节。仅无预设static/static-analysis/直接package基础源，保原音频租约/generation/close排水。匹配self-contained Server/Worker发布源码 `d961895`、`sourceDirty=false`；fresh HTTP `artifacts/project-image-preview-0990-acceptance/http-final6/` 通过1175请求／9760断言。static/static-analysis/直接package普通与透明各4帧及回退seek、40组原Runtime完整PNG/像素比较、Initialize/Evaluate×cancel/deadline/close六组实际Worker/child控制与排水通过；Server exit0、105个观察身份（含83个Worker身份）全部退出，2752个证据文件凭证扫描通过。本地611/611及codec变异红1/24、恢复绿24/24另按有限范围登记。见[计划](../plans/backend-project-image-preview-plan.md)、[实施§77](backend-extraction-progress.md#project-image-preview-0990)、[统一API§22](../../architecture/backend-api.md#project-image-preview-0990)与[ADR-0236](../decisions/decisions.md#adr-0236)。视频/字体、图片与preset、validation/export/measurement图片输入、完整B3/B4/B5/B6、浏览器/跨平台及性能长片/完整工程验收继续独立。

> **2026-10-22 B4-P2 package无音轨导出fresh HTTP有限切片已完成（产品0.98.1／合同1.32.0）：** 验收源码基线 `4e0f909`的匹配self-contained Server/Worker发布`artifacts/package-export-20261022/dist/`记录`sourceDirty=false`；新证据`artifacts/package-export-20261022/http/`为457请求／2575断言通过。普通／透明各21帧、report/media bytes/size/SHA、独立ffprobe帧数/无音轨与ffmpeg BGRA全帧完整长度/帧数/alpha检查（未对Runtime内容oracle）、SSE、ACL、Range/HEAD/ETag/304、includeAudio=true拒绝、cancel/deadline/close、一次previous-host orderly restart/recovery、两次Server exit0、50个观察身份排水、remaining=[]及1097文件凭证扫描通过。实现细节见[实施§76](backend-extraction-progress.md#b4-p2-package-no-audio-export-http-0981)。本条只关闭直接效果包无音轨短片fresh HTTP主成功与控制矩阵；package带音轨、外部图片/视频/字体闭包、preset+package、package-aware analysis/measurement、完整B4、B5/B6、浏览器/跨平台及性能/长片/GUI/平台矩阵仍独立。

> **2026-10-20 B4-P1主成功路径4+4帧有限HTTP切片已完成（产品0.98.1／合同1.30.1）：** 最新证据见[实施§69](backend-extraction-progress.md#b4-p1-package-validation-success-multiframe-0981)，probe `0fdf080`与匹配发布 `package-validation-dist-r4-0fdf080`、fresh HTTP `package-validation-http-success-multiframe-0fdf080`为450请求／1533断言，基础scale普通／透明各4帧，成功输入、进度、不可变report bytes/size/SHA及diagnostics page一致性通过。image/material warning维持§68的各4+4帧与union断言；warning helper未读取其diagnostics page。12个Worker本体identity、4个终态排水及1个crash后SIGCONT/EOF退出检查通过，最终Server exit0，无强制清理/残留，原probe当时1073个证据文件凭证扫描通过。crash-success仍各1帧；本条只关闭主成功路径多帧，不代表完整B4-P1或完整B3/B4。下方432／1427等数字保留历史归属。

> **2026-10-20 B4-P1 warning/recovery有限HTTP切片已完成（产品0.98.1／合同1.30.1）：** 新增证据见[实施§66](backend-extraction-progress.md#b4-p1-package-validation-warning-recovery-0981)，匹配发布`package-validation-dist-0981-final4`与HTTP `package-validation-http-0981-final4`为432请求／1427断言，image/material合法`BackgroundColor`输入下的warning与普通/透明成功帧共存；同一轮完成一次previous-host restart/recovery，成功报告仅经recovery route读取，活动任务持久化为cancelled且不自动重放。10个Worker identity、4个终态排水检查全部通过，Server两次exit0，无强制清理/残留，1032个证据文件通过凭证扫描。完整多代recovery、脚本/媒体/字体闭包、完整B3/B4、B5/B6、浏览器/用户及性能/长片/GUI/平台矩阵仍未完成。

> **2026-10-20 B4-P1 package validation有限切片已完成本轮收口（产品0.98.1／合同1.30.1）：** 新增证据见[实施§65](backend-extraction-progress.md#b4-p1-package-validation-0981)，fresh匹配发布`package-validation-dist-0981-final3`与HTTP `package-validation-http-0981-final3`为386请求／1264断言，覆盖成功不可变报告、runtime/compile/image/material失败报告、报告预算拒绝、cancel/deadline/close；`WorkerTaskTimedOut`已固定，9个Worker身份、4个终态排水检查全部通过，Server exit0，无强制清理/残留，937个证据文件凭证扫描通过。编译诊断union的null输入统一协议拒绝，旧`Diagnostic=null`兼容保留。该条仍是有限B4-P1切片，不覆盖restart/recovery、完整warning成功矩阵、完整B3/B4、B5/B6、浏览器/用户及性能/长片/GUI/平台矩阵。

> **历史2026-10-20 B4-P1 package validation诊断有限HTTP切片（合同1.30.0）：** 证据见[实施§64](backend-extraction-progress.md#b4-p1-package-validation-diagnostics-1300)，匹配发布`package-validation-http-diagnostics8`为161请求／552断言，覆盖runtime Error、compile failure、image/material diagnostic failure与预算拒绝；旧编译union bug导致compile无报告，已由后续0.98.1修复。该历史目录与计数保持原归属。

> **2026-10-19 P2-A直接效果包公共预览已完成有限HTTP验收（产品0.98.0／合同1.29.0）：** 工具最终HEAD `ff6fff1`；使用不可变 `final2-6f4293d/dist`（deployment manifest sourceCommit `6f4293d`、生产输入匹配）与 Release Oracle。final6 单次真实HTTP **1513请求／14090断言**通过，59组PNG/像素比较、47诊断场景、6组失控、84个Worker/子进程身份排水，3656个证据文件凭证扫描通过；evidence manifest SHA-256 `bfb411ddabf94a5036d521710923fdb2e5167edecd98a60893cce6597099957f`。完整证据见[实施§62](backend-extraction-progress.md#public-package-preview-0980)、[计划§8](../plans/backend-public-package-preview-plan.md#8-实施与验收状态2026-10-19-最终验收)及[ADR-0235](../decisions/decisions.md#adr-0235)。final2 Debug artifactHash不一致、final3项目会话配额、final5 B4预期码均为独立失败目录；Release Oracle复核修正后最终通过。历史 current-version 变体的静态旧schema拒绝为 `SnapshotVersionMismatch`，不冒称兼容。公共HTTP无完整source SHA；完整公共B3/B4、B5/B6、浏览器/用户及性能/长片/GUI/平台矩阵仍独立。Agent Mail #97/#98仍按persisted/未ack记账。

> **2026-10-19 Ruby spacing有限后端链路已完成（产品0.97.0／合同1.28.0）：** 生产80a1943、fresh发布源码cc30d4c已推送，sourceDirty=false；时间无关的1布局px间距求解与6处Skia共享布局接通，旧36帧golden不改，正常solution零警告错误、去重1330/1330（新增25已包含）、变异红3/恢复19。fresh匹配发布HTTP179请求/2689断言，27组同帧完整source SHA/PNG/definition geometry核对通过、21成功job、55个观察身份（含36Worker）全退出。合同80/89/449/91/2648/2040保持；证据见[实施§61](backend-extraction-progress.md#ruby-spacing-0970)、[计划](../plans/backend-ruby-spacing-plan.md)与[ADR-0234](../decisions/decisions.md#adr-0234)。旧Worker拒绝为codec负例，非旧0.96二进制实跑；共享cluster组内/非单调映射、完整RTL/竖排、完整公共B3/B4/B5/B6、浏览器/用户与原暂停矩阵仍独立。

> **2026-10-19 TTML/ruby 时间轴已完成（产品0.96.0／合同1.27.0）：** 见[实施计划](../plans/backend-ttml-ruby-timeline-plan.md)、[实施§59](backend-extraction-progress.md#ttml-ruby-timeline-0960)及[ADR-0233](../decisions/decisions.md#adr-0233)。本批保留现有歌词分支结构，为 ruby 增加独立片段时间，提供 `ttml.project` 项目往返与 `ttml.amll` 交换；最终3929/3929定向回归、合同80/89/449/91/2648/2040、真实HTTP 3/3 已登记。该历史批次未包含spacing，本批0.97状态见顶部新入口；完整公共B3/B4、B5/B6、浏览器/用户及性能/长片/GUI/平台矩阵仍独立。

> **2026-10-19 综合验收补录：** 生产源码基线 `38f2fa9` 完成16条命令全部exit0、3930/3930定向回归、1760个tracked输入稳定、121个观察产品进程身份退出；fresh B～G HTTP 257/1757与617个evidence核验见[实施§60](backend-extraction-progress.md#comprehensive-acceptance-0960)。旧TTML3929及3/3仍保留历史归属；该历史综合验收时spacing仍规划，0.97状态见顶部新入口。

> **2026-10-18基础业务实施入口：产品0.95.0／合同1.25.0，后端批次已完成。** 授权单层目录、单来源LRC评估/确认/下载、工程音频替换/清除与历史资源同步、改名/资源清单、完整.mktpl惰性保真传输、真实上下文最终静态样式/21属性来源、clip成员扩缩/拆合按[计划](../plans/backend-basic-business-completion-plan.md)实施。实际行为见[统一API§17](../../architecture/backend-api.md#basic-business-0950)，验收状态见[实施§58](backend-extraction-progress.md#basic-business-0950)；0.95/1.25 final2 保留 257 请求／1756 断言、9 个成功 job、发布源 `40ee152` 的历史归属，本次验收生产源码基线 `38f2fa9` 的0.96/1.27 fresh replay另以 `artifacts/basic-business-current-38f2fa9/assessment.json` 登记（257/1757、9 jobs、18 identities、834→806、sourceDirty=false、175/63 JSON）。当前B～G重放不覆盖TTML document scope/ruby API；`.mktpl`允许原传输器合法高级对象且不执行。完整公共B3/B4、媒体背景/字体闭包、整曲任务、B5/B6、G25和浏览器/用户验收不扩大。


> **2026-10-18补丁入口：产品0.94.1／合同1.24.1。** 本次仅修复透明导出请求format枚举与前奏附着错误的稳定failureCode/field；70路径／77方法／401 schemas／78命令保持，新增6导出请求正反例后1919例（1397反例）。实现与验证记录见[修复记录](../acceptance/backend-basic-business-bugfix-0941.md)。下面0.94.0整体关闭及1717项、HTTP73／781为历史证据，不能算本补丁新跑结果。

> **历史状态（2026-10-18，0.94.0原定义G20-G24后端能力整体关闭）：** 产品 **0.94.0**，生产源码 **`c50516d`已推送**。本轮覆盖G20结构联合操作、G21布局kind/project动画、G22真实Worker同帧geometry+PNG time/layout测量、G23生成/完整候选/selected apply/exclude/mark/静态Node模板/真实测量附着、G24 line动画覆盖/继承及既有局部变换。冻结合同 **1.24.0／70 paths／77 methods／401 schemas／1913 examples（1394反例）／78命令**，合同提交 `b312a835`，fresh及root独立校验、73实际HTTP JSON全匹配见[实施§57](backend-extraction-progress.md#g20-g24-completion-0940)。正常solution零警告/错误；Hosting1033、Server352、Backend.Rendering214、Worker78及另一Rendering项目定向40共1717/1717无重复，Server31精确工作流为子集。独立匹配0.94发布与HTTP73请求/781断言、6测量/8成功job终态、11观察身份含8Worker全gone；发布提交前dirty输入经Git blob核验等于c50516d，不能称clean clone。范围、证据与失败过程见[整体计划](../plans/backend-g20-g24-completion-plan.md)、[实施§57](backend-extraction-progress.md#g20-g24-completion-0940)及[审计§9](../../backend-capability-gap-audit.md#9-g20-g24-后端整体补齐更新原定义后端范围已验收关闭)。G19成员扩缩拆合/G25非原任务前置；完整B3/B4/B5/B6、浏览器/用户及原性能/长片/GUI/平台矩阵未由此完成。合同以[Web入口](../../../web/README.md)、[统一API](../../architecture/backend-api.md)和冻结生成物为准；版本单一来源为Directory.Build.props。下方早期记录保留其历史归属。

## 后端仓库边界（2026-10-17）

本仓库从 `NewMakoKaraGenerator` 的 `8526b84` 归档提交复制而来，保留完整 Git 历史，后续只维护无窗口后端。Avalonia/SukiUI 编辑器、桌面 UI 测试、UI 兼容性探针和桌面启动探针已在 `52bace2` 移除；UI 历史说明仍可从祖先提交查阅。不要把桌面项目或 UI 包重新加入本仓库，后端合同与 Server/Worker/Rendering/Hosting 定向回归以当前代码和 `README.md` 为准。拆分记录见 [`docs/backend-repository-split.md`](backend-repository-split.md)。

> **2026-10-16 静态样式底层API已交付（0.83.0／合同1.8.0）：** 用户确认现有前端编辑／撤销重做与后端接口无误，并明确“作为底层能力一并交付，再补模板库与分配”。GET四层canonical样式、styles.patch／restore共35命令已接通，主编码交子代理；正常solution零警告错误、相关750／750，正式HTTP38779请求414328断言、47成功终态及61观察身份gone，见[实施§40](backend-extraction-progress.md#web-static-style-0830)。staticHierarchy不冒充预设／场景最终外观，前端三工作区职责保持；模板库／分配接续，完整B3/B4及原暂停矩阵独立。

> **2026-10-16 G11波形已交付（0.82.0／合同1.7.0）：** 原一次准备峰值64MiB保留payload去重、session授权与≤8192桶窗口、GET/HEAD/ETag/304和明确不可用已接通，不重decode/不新增PCM。正常solution零警告错误，相关382／382、正式HTTP340请求2766断言/86完整层对照、16成功终态与25观察身份gone，见[实施§39](backend-extraction-progress.md#web-g11-waveform-0820)。计划S1/S2时间/S3/G11有限后端出口完成，前端W2与完整B3/B4独立。

> **2026-10-16 S3d再次导入／追加已交付（0.81.0／合同1.6.0）：** 两命令、严格目标union/完整27字段报告、Writer外固定准备与逐最终行/完整回执预算已接通；LRC强制编码单列修复。正常solution零警告错误、相关602／602、独立576默认对照、真实HTTP5089请求45600断言、16成功终态/22观察身份gone，见[实施§38](backend-extraction-progress.md#web-s3d-import-0810)。计划S1/S2时间/S3有限编辑出口完成，G11/前端W2独立。

> **2026-10-16 首音轨准备一致性已修复（0.80.1／合同1.5.1）：** 受限self-contained解码显式选择首音轨，与probe一致；真实Worker原双轨1s→6s修复并核完整峰值。正常solution零警告错误，相关158／158，真实HTTP93请求604断言、8成功终态与13观察身份gone，见[实施§37](backend-extraction-progress.md#first-audio-stream-0801)。无新route/DTO/PCM或波形接口，S3d/G11继续独立。

> **2026-10-16 S3c元素结构编辑已交付（0.80.0／合同1.5.0）：** 五元素命令与真实tokenResult、唯一流式规则/纯候选、完整单行及16MiB累计预算、segments固定回执已接通。正常solution零警告错误，相关518／518、独立7422旧新片段对照，真实HTTP3367请求26552断言、53成功终态与67观察身份gone，见[实施§36](backend-extraction-progress.md#web-s3c-tokens-0800)。再次导入／波形与前端W2后续独立。

> **2026-10-16 S3b来源管理已交付（0.79.0／合同1.4.0）：** 四来源命令与真实选择反馈、纯准备与完整读投影/回执预算已接通，保原角色保护及合法时间策略。正常solution零警告错误，相关345／345、真实HTTP3081请求25117断言、40成功终态与52观察身份gone，见[实施§35](backend-extraction-progress.md#web-s3b-branches-0790)。元素结构／再次导入／波形与前端W2仍独立。

> **2026-10-16 S3a正文／行操作／标量设置已交付（0.78.0／合同1.3.0）：** 新增十一严格命令与有界feedback，共享纯Prepare→完整单行预算→一次提交；no-op保redo，旧大行可删除修复。正常solution零警告错误、相关407／407，真实HTTP3116请求23627断言、43成功终态与55观察身份gone，见[实施§34](backend-extraction-progress.md#web-s3a-content-0780)。来源／元素／再次导入与G11波形后续独立，前端W2和原暂停矩阵未记完成。

> **2026-10-16 合同1.2.1勘误（产品仍0.77.0）：** outputDurationUs省略/null表示尚未确定，导出前必须显式设置；不是自动取音频或歌词末端。只改合同/说明/探针标签，不改产品与wire，原匹配部署继续可用。后续S3的null清除也必须保持此原业务语义。

> **2026-10-16 G3／S2时间编辑已交付（0.77.0／合同1.2.0）：** 新增起止打点、元素清除、微调、整行平移、1..1000元素批量时间，十一命令按type严格分型且保原五wire；每次HTTP确认一次历史、no-op保redo。准入固定batch输入，changeSet超过16KiB转完整刷新保护64KiB回执。正常solution零警告错误，Hosting148＋完整self-contained Server77＝225／225，786输入无漂移；真正HTTP2155请求17720断言、19成功终态及25观察身份gone，见[实施§33](backend-extraction-progress.md#web-s2-timing-0770)。接续S3a正文／注音／行操作与标量设置；波形G11及完整B3/B4保持独立。

> **2026-10-16 S1 Web编辑读模型已交付（0.76.0／合同1.1.0）：** 用户已授权继续补齐后端编辑能力，按[接续方案](../plans/backend-web-editing-plan.md)推进，主编码交子代理。来源列表、精确修订歌词窗口（完整正文／注音／时间状态／主行关联）和标量设置读取已接通；旧五命令与旧歌词接口兼容。正常solution零警告错误、Hosting相关72＋完整self-contained Server64＝136／136，匹配发布与真实HTTP245请求／1784断言、11成功job终态及16观察身份gone；见[实施§32](backend-extraction-progress.md#web-s1-read-models-0760)。下一步G3／S2六时间命令，随后波形与S3写编辑。W0获验收是Claude的用户侧报告；本仓库不代记W1浏览器完成。

> **2026-10-16 前后端分仓协作已建立：** Codex 负责本仓库 C# 后端，Claude 负责独立 Web 前端；通过 MCP Agent Mail 沟通。先读 [§8.4 前后端协作](#frontend-backend-collaboration)，再读 [web/README.md](../../../web/README.md) 与冻结合同。双向收发与回执已验证，API 细节由 Codex 在用户批准的目标内决定。

> **历史：2026-10-16 首次冻结API合同1.0.0：** 先读 [web/api-contract.md](../../../web/api-contract.md)、[完整清单](../../../web/api-index.md)与[OpenAPI](../../../web/openapi.json)。详细行为仍在 `docs/architecture/backend-api.md`，由 `web/contract-manifest.json` 固定指纹；本次仅文档和校验工具，产品保持0.75.1，不新增API。`web/`现保存交接与合同，不再仅README；后续改路由/DTO/行为须同步合同和证据。

> **2026-10-15 用户已授权恢复G1与Web后端启动基础（0.75.1）：** 先读 [web/README.md](../../../web/README.md) 与 [实施§31](backend-extraction-progress.md#web-g1-backend-0751)。G1仅在HTTP新登记配额不足时回收过期且无保护依据的client，保护工程、任务、保留期终态job、有效回执、在途请求／SSE／资源读取；不自动discard或删除持久数据。匹配self-contained Server＋完整Worker稳定发布／启动入口为 [tools/backend-web-host](../../../tools/backend-web-host/README.md)，随机loopback、显式Origin、私有bootstrap与正常排水，不依赖Avalonia／声卡／共享ASP.NET runtime。正常solution零警告错误，Hosting711＋Server46＝757／757、1375输入无漂移；默认TTL HTTP79／491、独立短TTL70／494与64登记均通过，前端浏览器W0仍未完成；其余原暂停范围不自动恢复。下方暂停交接记录作为历史保留，后续顺序仍为W0联调→W1命令及读模型→波形与预览浏览器闭环→完整输入→B5／B6。

> **历史：2026-10-15 用户当时要求暂时停止功能开发，转入 Web 交接：** 先读 [web/README.md](../../../web/README.md)，该目录只保留这一份前后端开发交接文档。当前产品仍0.75.0／`696acea`；本次仅文档，无新功能验收。恢复开发需等待用户指示；恢复后的优先级调整为联调基础→HTTP工程编辑命令及读模型→波形与现有静态预览浏览器闭环→公共package HTTP及完整渲染输入→B5／B6。现有HTTP已提供异步静态帧预览，尚未交付完整工程实时体验；当前commands仍仅五种。Avalonia不是headless服务或Web链路的运行／验收前置。原版本、业务语义、已验收证据与暂停矩阵保持，见roadmap§18.199。

> **0.75.0 B3-G内部package三类完整诊断有限出口已完成（2026-10-15）：** source十字段（0行列合法）、图效果与Script／Source材质owner完整传输，Host固定catalog恢复身份，capability显式diagnosticsVersion1，旧0.74真实拒绝。各类256条／64KiB、合计512条／128KiB超限明确失败不截断；Runtime Error禁像素、会话关联与原失控六组合保持。正常solution零警告错误、五TRX1001／1001（155＋699＋70＋33＋44），1623source／5849bin无漂移、476观察身份gone；独立434文件发布／raw健康及缺refs拒绝／已发布Runtime22／真实0.74拒绝均green、不并1001。controller ArtifactHash只核格式与固定源关联，不做Host独立重编译。公共HTTP仍双静态profile，完整B3／B4及原暂停矩阵保持。见[实施§30](backend-extraction-progress.md#b3-package-diagnostics-0750)、[计划§10](../plans/backend-b3-completion-plan.md#b3-package-diagnostics-plan)、ADR-0220与roadmap§18.198。

> **0.74.0 B3-F内部package Worker会话与失控隔离有限出口已完成（2026-10-15）：** 产品`1524625`已提交推送，私有capability／open／render／close绑定实际Worker与原pipe，固定包源一次open、完整Runtime诊断与Error禁像素、static／package互斥及init／evaluate×cancel／deadline／Dispose六组合实际组排水已接通。正常solution零警告错误，七TRX971／971（155＋639＋70＋31＋2＋44＋30），1618source／5849bin无漂移，448观察身份gone；独立self-contained发布434文件、raw IPC健康／缺refs拒绝及匹配Release Host的20／20另计。公共HTTP仍仅双静态profile；本机Linux实际组隔离不构成任意恶意脚本OS sandbox。完整B3／B4与原暂停范围保持。见[实施§29](backend-extraction-progress.md#b3-package-worker-isolation-0740)、[计划§9](../plans/backend-b3-completion-plan.md#b3-package-worker-isolation-plan)、ADR-0219与roadmap§18.197。

> **0.73.0 B3-E固定效果包源codec有限出口已完成（2026-10-15）：** `8de271a`新增仅在Backend.Rendering使用的`package-lyrics-scene-v1`，保完整15源字段、规范包bytes／实例／材质／五预算／完整分析及原效果输入指纹；4MiB／总200000源值／深度门禁与包profile对称拒绝已接通。正常solution零警告错误，Rendering155＋Hosting555＋Worker70＋self-contained Server44＝824／824，1609source／5849bin无漂移、362实际观察身份gone。同进程原Runtime普通／透明、前进／回退与反事实像素贡献通过；公共Worker／HTTP仍为原双静态profile，不宣称包执行或隔离完成。原失败与变异另存，不并824；完整B3／B4及原暂停矩阵保持。见[实施§28](backend-extraction-progress.md#b3-package-source-codec-0730)、[计划§8](../plans/backend-b3-completion-plan.md#b3-package-source-codec-plan)、ADR-0218与roadmap§18.196。

> **0.72.0 B4-I受管理媒体准备／deadline隔离有限出口已完成（2026-10-15）：** Server显式Worker-only、真实能力门禁与private-group/native排水已接通，无能力不回退Host native；App保原default，未配置managed的Hosting保自包含default。五Info字段／源rate完整sampleCount、64samples尾桶／float bits与完整pyramid保持，base500000桶／payload4MiB独立预算，不套analysis或export300秒。晚期open错误wire回归由`1c70fae`最小修复，repair2原八commands exit0／solution零警告错误，七TRX775／775（555／70／44／80／2／11／13），1602source／5849bin无漂移，465观察身份gone。严格真正HTTP原session38819 exit0、699请求／4239断言，58whole终态／六actual-marker恢复／七controls通过，四Servers exit0／172观察成员（52groups）gone；publish21与握手13另计。1342HTTP输入／1448四role部署／1586证据SHA已核。repair1 753、final1失败137／987、原测试及CA1869失败独立保留，不重写旧B4-H数字；完整B3／B4与原暂停矩阵保持。完整证据见[实施§27](backend-extraction-progress.md#b4-managed-audio-preparation-0720)、[计划§13](../plans/backend-b4-plan.md#b4-managed-audio-preparation-plan)、ADR-0217与roadmap§18.195。

> **0.71.0 B4-H固定MP3／native FLAC音轨导出有限出口已完成（2026-10-14）：** 双执行profile、完整strict EOF源采样帧计数／300秒预算与精确coverage，MP4 AAC／MOV PCM16、旧WAV／纯视频wire保持；重开后同源分析采用路径最小修复保身份租约。原修前716／716保留，修后正常solution零警告错误、新两TRX540／540与未改原四TRX186／186独立；真正HTTP原47134 exit0、385请求3560断言，28whole终态／七媒体报告／六旧新恢复，六影片540完整帧Runtimeoracle与音轨，纯视频另90无音轨无独内容oracle；透明FLAC144144frames bitexact。独立三source与原HTTPmedia审查、1568统一source／1308deploymentinputs／1444当前deploy／434旧Worker／942evidence SHA已核，三Servers exit0／38private观察成员（13groups）gone，27publishidentities另计。固定产品／工具99189f2，见[实施§26](backend-extraction-progress.md#b4-compressed-export-0710)、ADR-0216、roadmap§18.194与[唯一API](../../architecture/backend-api.md)。两原失败独立，不重写716或旧B4G数字；MINOR保schema／SDK／依赖／项目，完整B3／B4及原暂停矩阵保持。

> **0.70.1 B4-G Backend自包含固定音频准备有限出口已完成（2026-10-14）：** 两个Backend默认入口收紧六家族受限probe／typed decode及MOV外部引用结构门禁，原App／Media默认保持；actual身份前后核验与未发布候选cleanup保原所有权。正常solution零警告错误／三TRX557／557；真正修后HTTP原69113 exit0、131请求／936断言，六家族创建保存重开实际duration兼容、ffconcat拒绝及Host native取消／同handle重试通过，29完整终态JSON及1391文件SHA已核。Server退出0／31direct native与4Worker成员（2groups）身份gone；1561源码无漂移／1014部署文件已核。版本／产品8000f4b／工具ce588bb，旧0.70 HTTP30／238仅修前证据，不并当前。见[实施§25](backend-extraction-progress.md#b4-self-contained-preparation-0701)、ADR-0215、roadmap§18.193与[唯一API](../../architecture/backend-api.md)。PATCH无新route/profile/schema，仍Host直接native；完整B3／B4及原暂停范围保持。

> **0.70.0 B4-F固定MP3／FLAC独立分析有限出口已完成（2026-10-14）：** 强格式／严格decode保原Analyzer完整canonical，压缩SourceDurationUs未知省略，旧WAV精确时长／账本保持；显式apply／保存重开兼容。正常solution0警告错误、四TRX551／551；真正HTTP原37041 exit0／152请求／1255断言，4canonical（3压缩＋旧WAV）43,502B／404points／386,496samples byteexact，PNG零差／formal1＋1、strict MP3取消／旧0.69 Worker禁用／旧新恢复通过；三Server退出0／32观察身份gone。产品af77449／工具c00c3a5；原551不重写，仅新probe两修漂移，其余1552源不变。见[实施§24](backend-extraction-progress.md#b4-compressed-analysis-0700)、ADR-0214、roadmap§18.192／[唯一API](../../architecture/backend-api.md)。Export不改、带音轨仍PCM16 WAV；旧create/open autodetect owned ffconcat引用缺口已确认未修、优先后批（未证HTTP／跨client／任意路径）。完整B3／B4仍未完成。

> **0.69.0 B4-E完整分析静态profile有限出口已完成（2026-10-14）：** 新static-lyrics-scene-analysis-v1保完整Available canonical及Failed／NoStream，旧v1仅null，自动选profile／总4MiB深200k／真实export engine门禁已接通；纯视频保完整声明禁音频IO。正常solution0警告错误、六TRX559／559；真正HTTP原23038 exit0／232请求／1864断言，原Analyzer完整canonical、同声明sourceSHA、新旧PNG零差／正式1＋1、三片各30完整解码帧及首帧oracle／有损音轨通过，真实旧0.68 Workerexportdisabled；三Server退出0／26观察group身份gone。产品5727145／工具4378b60，1542源无漂移／最终总SHA已核；见[实施§23](backend-extraction-progress.md#b4-analysis-static-profile-0690)、ADR-0213、roadmap§18.191和[唯一API](../../architecture/backend-api.md)。新profile不表示包脚本或音频驱动视觉，完整B3／B4仍未完成。

> **0.68.0 B4-D显式采用／清除分析有限出口已完成（2026-10-14）：** 原commands新增audio.analysis.apply／clear，精确当前修订／同源live成功结果／单层回执，完整性与租约释放先于一次领域历史；有界summary、保存重开及clear后静态恢复已接通。正常solution0警告／错误、四TRX977／977；真正HTTP原21974退出0、367请求／2522断言，两音频canonical byteexact、PNG57,600像素RGB／alpha零差、正式1＋1及纯视频MP4完整30帧恢复；两Server退出0／本轮16观察group身份gone。产品6264a94／工具f4787bd；工具三处修正仅同一probe.py输入漂移，原977 manifest保持，最终证据见[实施§22](backend-extraction-progress.md#b4-analysis-apply-0680)、ADR-0212、roadmap§18.190及[唯一API](../../architecture/backend-api.md)。staticprofile不扩，完整B3／B4仍未完成。

> **0.67.0 B4-C独立离线音频分析有限出口已完成（2026-10-13）：** 原AudioFeatureAnalyzer经真实Worker处理固定授权PCM16 WAV，完整canonical结果独立保存／认证读取与profile恢复，不apply、不改变工程历史或静态渲染profile。统一正常solution0警告／错误、五TRX460/460；真实HTTP原9085退出0，789请求／5943断言，六输入全部canonical bytes与原Analyzer一致。最终源码／部署／证据SHA已核验，两Server／43观察private-group身份全退出；完整B3／B4及apply／其他音频继续独立。见[实施§21](backend-extraction-progress.md#b4-audio-analysis-0670)、ADR-0211、roadmap§18.189与[唯一API](../../architecture/backend-api.md)。

> **0.66.0 B4-B固定静态工程独立导出有限出口已完成（2026-10-13）：** 原Runtime／VideoExporter经真实Worker生成普通MP4／透明MOV，固定PCM16 WAV或显式纯视频、唯一任务／调度器／账本、私有candidate与公开产物包两层提交、认证流式下载及失败报告已接通。修后solution0警告／错误，7组8TRX共436/436；独立真实HTTP764请求／5682断言（4057工作流＋1625凭证扫描），普通／透明／有理fps各90完整解码帧和真实音轨／原Runtime内容oracle通过，stereo离线29附证另列；最终SHA一致，两Server／19Worker＋13同组子进程全退出。完整B3／B4及其他音频／包脚本／图片视频／分析HTTP／Web继续独立；见[实施§20](backend-extraction-progress.md#b4-static-export-0660)、ADR-0210、roadmap§18.188与[唯一当前API](../../architecture/backend-api.md)。

> **0.65.0 B4-A固定静态工程正式验证有限出口已完成（2026-10-13）：** 原RuntimeFrameValidator经真实任务Worker执行固定普通／透明帧计划，共用唯一jobs／ledger、实际进度、取消／deadline、完整报告sidecar与授权读取／重启恢复。solution0警告／错误，Hosting268、Runtime限定28、Worker8、self-contained Server16、Export定向10、作者取消2合计332/332；真正HTTP389请求／2152断言（1344工作流＋808凭证扫描），FullHD3＋3帧报告／进度／整数时刻及同固定输入SHA／profile与独立原验证器一致，两个Server／八Worker全退出。完整B3／B4仍未完成，导出／分析HTTP、脚本／媒体和Web产品继续独立；详见[实施§19](backend-extraction-progress.md#b4-static-validation-0650)、ADR-0209、roadmap§18.187及[唯一当前API](../../architecture/backend-api.md)。

> **0.64.0 B3-D静态工程Worker与HTTP有限出口已完成（2026-10-13）：** 专用snapshot-v1保留真实静态歌词／注音／场景，持久Worker复用既有Runtime并经PNG适配进入受认证HTTP／SSE；音频声明无损保留，授权资源由Hosting持租约。solution0警告／错误，Hosting221、Runtime依赖＋真实Worker11、Avalonia预览20、codec40、PNG9、self-contained Server14共315/315通过；真实HTTP112请求／705断言（458工作流＋247凭证扫描），三帧各57,600像素RGB／alpha误差0。真实Worker终止／首次启动丢失分别保留稳定失败编号与transport诊断，没有虚构作者发生。**完整B3仍未完成**：效果包／预设／分析／图片视频背景明确拒绝，完整资源与字体能力、脚本诊断及失控脚本隔离后续继续。详见[实施记录§18](backend-extraction-progress.md#b3-static-preview-0640)、ADR-0208、roadmap§18.186及[相对基线报告§6](../research/backend-baseline-change-report.md#6-0640静态工程worker与http增量)。

> **0.63.0 B3-C有限出口已完成（2026-10-13）：** 已接入真实 Runtime.Hosting 的 render-frame-v1 同进程适配、受验证 PNG 帧句柄存储、ETag/Range/TTL/HEAD读取、诊断分页读取，并修复 Worker 取消停止时的孤儿进程风险。solution构建0警告／错误，Backend.Hosting105/105、Worker4/4、Runtime.Hosting真实静态帧1/1、Avalonia预览20/20通过；跨进程Worker真实渲染仍未完成。详见[实施记录§17](backend-extraction-progress.md#b3-runtime-frame-0630)、ADR-0207与roadmap§18.185。

> **0.62.0 B3-B帧发布完整性合同已完成（2026-10-13）：** 在0.61.0 B3-A接缝上新增帧发布前验证器，固定代次、来源修订、请求序号、PNG句柄租约、尺寸、大小和SHA-256一致性。Backend.Hosting定向98/98、solution构建0警告／错误；尚未新增Server预览路由或真实Worker渲染。详见[实施记录§16](backend-extraction-progress.md#b3-frame-integrity-0620)、ADR-0206与roadmap§18.184。

> **0.61.0 B3-A有限技术出口已完成（2026-10-13）：** 已新增中立预览协调器接缝、预览帧／诊断传输DTO及受管理Worker的有界IPC生命周期。solution构建0警告／错误，Backend.Hosting 93/93、预览协调器20/20、Worker IPC3/3通过。当前Worker仍回显经过校验的opaque输入，尚未接入Runtime.Hosting真实渲染、HTTP帧路由或PNG句柄；后续B3-B继续。详见[实施记录§15](backend-extraction-progress.md#b3-preview-worker-0610)、ADR-0205与roadmap§18.183。

> **0.60.0 B2-C有限出口已验收（2026-10-12）：** 本机生命周期、Hosting单层client级回执、资源与目录租约、SSE、独立保存／重开／恢复及基本任务账本已通过有限真实链路。Hosting82/82、真实HTTP1224断言／241请求、solution零警告／错误；最终manifest见[实施记录§14](backend-extraction-progress.md#b2-lifecycle-persistence-0600)／ADR-0204／roadmap§18.182。探针证据已脱敏，不落盘access/resume/bootstrap凭证。Worker／frame／导出、完整B2之外的作者闭环与原暂停范围不自动恢复。

> **2026-10-12 后端拆分历史实施入口：** 用户已授权逐批实施，保留可用Avalonia、直接调用共享后端用例；长期采用Web，Tauri可选。先读[拆分方案](../plans/backend-extraction-plan.md)、[当前HTTP规范](../../architecture/backend-api.md)与[实施记录](backend-extraction-progress.md)；[API v1草案](../plans/backend-api-v1-draft.md)仅为未来设计。0.50.0～0.56.0完成B1七个进程内编辑切片，0.57.0完成无GUI新建候选；a8c8a63／0.58.0已推B2-A Contracts／Server固定上传与创建摘要种子，当时因缺ASP.NET运行时未实跑HTTP。0.59.0 B2-B已完成单写者、精确修订、具名时间／撤销／重做、一致查询与有界命令回执的有限验收，真实HTTP及新建dirty／hasPath修复已归档：Hosting61项、HTTP688断言／110请求、solution零警告／错误；见实施记录§13／ADR-0203／roadmap§18.181。当时临时clientId标签不是凭证认证，eventCursor未开放，HTTP省略该字段；客户端握手／Host／Origin、资源／目录租约、保存重开、job／Worker／frame当时仍未完成，后续0.60／0.64实际状态以上方当前条目为准。全量、性能、原生窗口、人工平台及原暂停范围不自动恢复。

> 本文件面向**接手本仓库的 AI 代理**。读完它就能动手：包含项目硬约束、构建与验证命令、
> **尚未完成的工作清单**（每项带验收判据与现状证据）、以及**必须问用户才能决定的悬空事项**。
>
> 需求与设计的完整来源在 `docs/`，本文件只做导航与状态盘点，**不替代**它们。
> **当前已批准 UI 重构入口**为 [0.49.6 自然引导重构规划](../legacy-ui/ui-0496-refactoring-plan.md)。2026-10-10 已交付0.49.7共用基础、0.49.8外壳、0.49.9歌词编辑/时间组提交、0.49.10工程设置/统一添加歌词、0.49.11画面对象选择/来源映射/候选预览、0.49.12复制到工坊/返回原对象、0.49.13工坊基础外观首屏/资源库状态及0.49.14效果组件配置/验证/源码工作流切片：工程/作者保存归属、稳定草稿、独立时间区、设置入口、明确来源追加、正式预览会话、保存返回门禁、库状态反馈和源码修订门禁已接通；效果作者完整闭环、整页重排、设置/导出往返与完整重构尚未完成，详见[实施记录](../legacy-ui/ui-0496-refactoring-progress.md)。本轮布局/引导冲突以新规划优先，原数据语义与功能保留。
> 与 ADR（`docs/decisions.md`）冲突时，**以 ADR 为准**；GUI 外观口径见 **ADR-0045**
> （已取代 ADR-0001 的外观取向部分）；已交付的 Suki 全面迁移依据 **ADR-0142～0144**，实际完成状态见 roadmap §18.109 起。
> **下一步 UI 开发方案**为用户于 2026-10-07 批准的 [三工作区职责重构与预设制作流程](../legacy-ui/ui-workspace-ownership-plan.md)；
> 新方案调整后续工作区职责与预设流程，已固化为 **ADR-0145～0162**；不要把规划中的版本、schema 或界面记为已经交付。
> 接手后续开发先读该方案 **§10「下一步开发入口」**：三工作区基线0.43.10的图形脚本参数面板、独立正式运行验证、歌词行选择、必填新建及明确分支导入已接通。当前0.49.6的新建／导出修复已同步至本工作区；控件接线检查的模板作用域误报已单独修正，非渲染范围2311项通过，见[接线检查与验证](../legacy-ui/control-wiring-test-contract.md)／roadmap §18.163。用户已授权通过后快进集成main。本批不改产品或版本；含渲染全量、性能、未知提示及人工／平台边界仍不自动恢复，原goal保持暂停。完成状态以roadmap证据为准。

---

## 0. 一句话现状

**当前基础业务实施见顶部0.95.0入口；以下为0.94.0历史关闭证据。** 原始任务表定义的G20-G24后端范围于0.94.0关闭，源码c50516d已推送。 五项覆盖与1717项相关回归、独立匹配发布HTTP73请求781断言、真实Worker/同帧几何/PNG和完整10帧成片证据见[整体计划§6](../plans/backend-g20-g24-completion-plan.md#6-最终证据与关闭结论)。合同b312a835冻结1.24.0/70 paths/77 methods/401 schemas/1913例（1394反例）/78命令，独立校验及73实际HTTP JSON匹配见实施§57。G20-B及基础批次证据保持原归属；G19成员扩缩拆合、G25、独立时钟/任意轨道不列为本轮遗留。后续完整公共B3/B4输入及任务、B5作者/B6分发收口仍独立；PinkPike邮件68报告既有W3于2026-10-17用户验收通过（d33051a/f571dfb），为对端历史报告，本轮合同1.24浏览器/用户验收未做。

**此前0.75.0 B3-G内部package三类完整诊断有限出口已完成**：统一1001／1001、独立发布Runtime22与真实0.74旧诊断版本拒绝等证据见[实施§30](backend-extraction-progress.md#b3-package-diagnostics-0750)。仅扩内部完整诊断；公共HTTP包profile／route与正式任务仍未开放，后续需独立合同验收，完整B3／B4仍未完成。B3-F971及独立20、B3-E824与B4-I775／真正HTTP699请求4239断言分别保[§29](backend-extraction-progress.md#b3-package-worker-isolation-0740)、[§28](backend-extraction-progress.md#b3-package-source-codec-0730)、[§27](backend-extraction-progress.md#b4-managed-audio-preparation-0720)历史归属，不并当前。

**接续边界**：完整B3／B4、包脚本媒体资源字体／作者Web及原全量性能GUI长片平台暂停保持。准备base500000桶／4MiB预算独立于analysis与压缩export300秒；旧0.71 B4-H原716／修后540／原186及HTTP385／3560保持[§26](backend-extraction-progress.md#b4-compressed-export-0710)历史。当前接口唯一[统一API](../../architecture/backend-api.md)，最近固定行数统计仍为0.71[报告§15](../research/backend-baseline-change-report.md#current-statistics-0710)，四万行归属历史[§11](../research/backend-baseline-change-report.md#line-attribution-0680)。

**历史B2出口**：生命周期／持久化见[实施§14](backend-extraction-progress.md#b2-lifecycle-persistence-0600)，时间／历史见[§13](backend-extraction-progress.md#b2-timing-http-0590)。

## 0.1 历史切片摘要

**2026-10-12 B2-A 0.58.0种子已提交推送**：a8c8a63新增Contracts／Server、固定上传SHA／MIME／大小／配额和标签归属、同步创建／零修订摘要。Hosting35项通过、Server编译通过；HTTP因缺ASP.NET运行时当时未实跑，早期假媒体探针不算真实媒体验收。0.59.0补真实HTTP并修新建保存状态，历史与边界见实施记录§12／ADR-0202／roadmap§18.180。

**2026-10-12 B2无GUI新建候选0.57.0已交付**：Hosting提取固定歌词、无GUI音轨／波形和领域组装，App保留声卡／字体／发布。242项有限定向、Headless89＋51、真实媒体30项与19源码恢复通过；341文件隔离包PID167170，日志`/tmp/makokara-backend-b2-0570-startup.log`。当批仅歌词固定，音频仍为受信本机路径；该历史范围不覆盖后续HTTP，见实施记录§11／ADR-0201／roadmap§18.179。

**2026-10-12后端拆分B1共享编辑收口0.56.0**：共享Application覆盖整行时间平移、行内元素重排、元素拆分／合并、固定行重切、明确来源全行规则重切，并接入播放打点快捷键；Avalonia保留输入、草稿、确认、选择和媒体生命周期，已迁出的规则只调用共享用例。最终solution零警告／错误，Application285、App113、Core59、宿主依赖2共459项定向通过；Headless六流58／68／70／33／41／34共304项通过；三项成功编译后的变异均由真实断言拒绝，32个冻结源码逐字节恢复。普通338文件隔离包PID6260，日志`/tmp/makokara-backend-0560-startup.log`仅版本。完整B0、HTTP／SSE／Contracts／Hosting／Server／Worker、无GUI媒体／保存重开、全量／性能／正式导出／长片、物理输入／平台人工验收及原暂停goal不自动恢复。证据见[实施记录§10](backend-extraction-progress.md#b1-closure-0560)、ADR-0200和roadmap§18.178。

**2026-10-11后端拆分打点／时间点切片0.55.0**：共享Application新增稳定元素起点／终点打点用例。按Workspace／Generation、来源／行／元素稳定ID及原时间验证；起点按播放头与已准备媒体边界补一帧估算终点，终点确认后提升为Edited；一次`token.stamp`历史，拒绝不改工程／dirty／redo，App仅保留选择、消息与终点后选择下一个元素。完整回归与变异恢复见[实施记录§9](backend-extraction-progress.md#timing-stamps-0550)／ADR-0199／roadmap§18.177。MINOR保持格式28/16/6、配置1、profile2、SDK、生产程序集及固定依赖；整行平移、拆分／合并、规则重切与行内重排等仍未迁出，完整B0／B1、HTTP／SSE／服务／Worker及原暂停范围不自动恢复。

**2026-10-11后端拆分导入／工程设置切片0.53.0**：共享Application从固定字节检测／准备歌词，再按Workspace／Generation、既有完整来源或新来源完整歌词原值替换／创建／追加；文件新来源保留Independent，粘贴新翻译来源保留FollowPrimary。成功一次历史，结果选区与报告一致后一次通知；所有文件入口统一拒绝空候选。GUI异步导入额外验证Session、Project与Snapshot，拒绝编辑后Undo回同Project的旧结果；共享用例仍允许无关字段变更。画布／帧率／底色／时长及当前歌词末尾、已准备媒体时长标量共享，非法用户数值拒绝，Core表达非法数据与奇数尺寸保持。新建只复用纯解析与固定候选，媒体／播放准备留App。原基线223及Headless73；原DLL复现五类旧行为。最终solution零警告／错误、328项有限定向、Headless51／73／33、四项成功编译后真实断言拒绝和31源码字节／SHA恢复通过，见[实施记录§7](backend-extraction-progress.md#lyrics-import-settings-0530)／ADR-0197／roadmap§18.175。338文件普通隔离包PID1420716／niri731已启动，日志`/tmp/makokara-backend-0530-startup.log`仅版本；旧0.52 PID1374333保留，无自动交互或截图，不等同人工验收。MINOR保持格式28/16/6、配置1、profile2、SDK、生产程序集及固定依赖。完整B0／B1和HTTP／SSE／服务／Worker未完成；全量、性能、正式导出、长片、原生输入／人工平台及原暂停goal不自动恢复。

**2026-10-11后端拆分正文与行操作切片0.52.0**：正文／注音／新增／复制／删除行调用共享Application用例，按Workspace／Generation、稳定来源／行／元素与原值验证，完整Unicode、未改身份／样式／微秒、注音元数据、复制映射与原Warning保持。全歌词ID含注音，耗尽明确拒绝；正文／注音按稳定目标与操作合并，前批时间手势合同保持。App同步TextProperty捕获输入，内容增量、结构／未知完整刷新；行结果选择在Adopt内完成再通知一次，空来源清空块下标。原443有限基线和原DLL五项真实VM缺陷已记录，同ID换工程注音旧路线未复现。最终solution零警告／错误、519定向与Headless正文33／草稿70／时间41／设置添加73通过；四项成功编译后变异均被真实断言拒绝，40冻结源码字节／SHA恢复及正常复验。见[实施记录§6](backend-extraction-progress.md#lyrics-content-0520)／roadmap§18.174／ADR-0196。普通338文件隔离手测包PID1374333／niri707已启动，标题 `MakoKara — 首页 0.52.0`，日志 `/tmp/makokara-backend-0520-startup.log`仅版本，无自动输入／用户工程操作／截图，启动不算原生交互验收。MINOR新增公开能力，格式28/16/6、配置1、profile2、SDK、程序集及固定依赖保持；完整B0／B1、导入／设置／HTTP／SSE／服务／Worker仍未完成，打点／平移／拆合／规则重切／排序和旧BeginGroup容量缺陷继续。全量、性能、正式导出、长片、人工／平台及原暂停goal不自动恢复。

**2026-10-11后端拆分时间动作切片0.51.0**：现有逐字块／行边沿拖动、微调及选中元素清除调用共享Application用例，按Workspace／Generation、稳定来源／行／元素与原时间验证；App保留Session与实际控件唯一身份。实时提交，同次拖动跨原900毫秒仍一次撤销，微调按目标／操作隔离；capture loss／detach保留接受变化并完成一次，旧Update／Completed、外部编辑后Undo回同Project不能复活。自身历史通知只读状态不误失效；无变化／dirty／redo、精确微秒、原边界／媒体／FollowPrimary与增量显示保持。原有限基线268，最终solution零警告／错误、335项定向及Headless动作41／草稿70通过，四项成功编译后变异被真实回归拒绝，25冻结源码SHA精确恢复。见[实施记录§5](backend-extraction-progress.md#timing-actions-0510)／roadmap §18.173／ADR-0195。MINOR新增公开Application能力，格式28/16/6、配置1、profile2、SDK、生产程序集及固定依赖保持；完整B0／B1、正文／行操作、导入／设置、HTTP／SSE／服务／Worker仍未完成，打点／整体平移／拆分合并及旧BeginGroup满容量缺陷继续。未启动原生／常驻程序、未操作用户窗口或截图；全量、性能、正式导出、长片、人工／平台和原暂停goal不自动恢复。

**2026-10-11后端拆分首个时间组切片0.50.0**：共享 Application 用例按工作区引用／Generation、稳定来源／行／元素 ID 和原时间校验整数微秒，一次命令，无变化保留历史／dirty／redo；App 保留分组草稿并直接接入。时间提交／撤销／重做保留行／元素实例与选择，更新受影响时间块，读模型一致后发布一次工程通知；场景草稿过期提示不覆盖真实反馈，撤销回原 Source 可继续原草稿。原基线87项通过，恢复变异源码后 solution 零警告／错误、定向205项及 Headless70个断言通过，五项成功编译后变异被真实回归拒绝，21个冻结源码文件 SHA 完全恢复。见[实施记录](backend-extraction-progress.md)／roadmap §18.172／ADR-0194。MINOR新增公开 Application 能力，格式28/16/6、配置1、profile2、SDK、生产程序集与固定依赖保持；旧拖动／微调／清除用例、其余B1、完整B0、HTTP／SSE／服务／Worker均未完成。未启动原生／常驻窗口或操作用户窗口；全量、性能、长片、完整UI重构、人工／平台及原暂停goal不自动恢复。

**2026-10-10自然引导UI重构效果组件配置/验证/源码工作流切片0.49.14**：独立效果组件工作区明确为配置、验证、源码三页；无包时显示空态和现有导入入口。源码页按固定修订缓存编译结果，源码草稿变更立即使旧结果过期，保存/编译/采用/正式验证保持独立；同一效果包的新固定修订通过“采用此修订”进入当前组件并保留名称、参数、目标、读取范围、种子等配置语义。验证页显式绑定原 Runtime，运行仍受保存配置、源码选择和目标门禁。solution零警告/错误，定向xUnit97、Headless380、原生脚本28/截图2、参数52/截图7、资源库80/截图8、作者352/14图通过；作者流程两组720帧正式试样验证通过。普通0.49.14独立手测包已启动，PID634820、niri窗口516，标题 `MakoKara — 首页 0.49.14`，日志`/tmp/makokara-ui-04914-startup.log`仅版本；设置/导出往返、整页收口、全量/性能/长片及物理输入与平台人工边界不自动恢复。PATCH保持格式28/16/6、配置1、profile2、SDK、程序集划分和固定依赖。

**2026-10-10自然引导UI重构工程设置/统一添加歌词切片0.49.10**：顶栏独立工程设置工作页保留画布、背景、音频、输出四组草稿，返回原任务和同一工坊实例；宽高/颜色/时长分组提交、非法/输入法预编辑/过期回调门禁、媒体缩略图与音频恢复沿原会话释放。工程命令先检查设置草稿，作者保存上下文不被替代。歌词工具栏统一添加歌词弹层，空来源可直接打开；粘贴明确已有来源末尾或新建来源，新来源与整批歌词一次撤销，旧会话/Generation拒绝并保留输入；来源管理、行操作、文件菜单和原数据语义保留。Solution/Headless/原生工具零警告错误，74项定向、Headless286项断言，设置/歌词原生各100项覆盖深浅三视口和实际1.8倍；两项成功编译后变异均被拒绝且源码恢复。代表截图和隔离配置见实施记录§7，普通0.49.10独立手测包 PID191930/niri126 已启动，日志仅版本，无自动交互；设置/导出往返、画面对象/预设闭环、正式试看、全量/性能/长片及物理输入与平台人工边界不自动恢复。PATCH保持格式28/16/6、配置1、profile2、SDK、程序集划分和固定依赖。

**2026-10-10自然引导UI重构歌词切片0.49.9**：来源管理并入行列表列，中央正文保留文本/注音/文字序列，平移/粘贴按需展开；时间区独立于正文和预览，播放栏上方两组字段、清除/前后行/定位/试听/撤销常驻。Enter/离开整组提交一次，Esc恢复当前值，取消重复写入按钮；稳定ID/会话/工作区Generation草稿保留非法值，拒绝过期回调，一组一次原历史撤销，未改端点保留整数微秒。保存/导出及时间变换准备草稿，删除目标可撤回全部未提交输入；文本/注音仍即时编辑，选择行不寻址，试听依据真实音频状态启用。solution/两工具零警告/错误，64项定向、Headless462、原生185/22图、两项成功编译后变异及恢复复查通过；深浅三尺寸/实际1.8倍、长正文/错误/预览，代表图已查看，自建测试窗口关闭。普通PID132577/niri83已启动0.49.9独立手测包，日志仅版本，无自动交互。见[实施记录§6](../legacy-ui/ui-0496-refactoring-progress.md)／roadmap §18.166／ADR-0188。PATCH保持格式28／16／6、配置1、profile2、SDK、程序集划分和固定依赖。下一项独立工程设置/统一添加歌词/空来源；L01音频归属、L03/L06及第2步/整体重构未完成。原goal、全量、性能、物理输入/听辨及平台边界不自动恢复。

**2026-10-10自然引导UI重构外壳切片0.49.8**：三任务统一为“歌词与打轴／画面与编排／预设工坊”，工程任务相邻、工坊独立分组。有工程时全局工程名、独立保存状态、保存工程与两种导出跨任务保留；无工程只标个人预设库。全局操作复用工程命令，菜单保存/Ctrl+S/作者按钮仍保存活动作者，非法作者草稿不阻止保存工程已提交内容，也不会随工程保存发布。工坊标题保留作者名称/脏状态，按钮为保存预设；原输入准备、历史、资源、快照保存、串行/关闭及导出门禁保持。最终solution和两工具零警告/错误，48项定向、Headless49＋135、原生54＋139/18图通过；实际深浅1440×900／1100×760／760×640及1.8倍，全部自建窗口关闭；成功编译后错目标变异被拒绝，源字节恢复并复查。见[实施记录](../legacy-ui/ui-0496-refactoring-progress.md)／roadmap §18.165／ADR-0187。PATCH保持格式28／16／6、配置1、profile2、SDK、程序集划分及固定依赖；下一项歌词页与时间组提交，第2步及整个重构未完成。原goal、全量、性能和人工/平台边界不自动恢复。

**2026-10-10 UI 重构首个基础切片，当前0.49.7**：起点000d6da，分支codex/ui-interaction-redesign-0496；冻结210个AXAML动作及14处动态构造，共用4/8/12/16/24间距，工程保存改为单一短反馈，画面操作接同一组件。成功约4.5秒收起，错误按任务/稳定目标隔离，晚到失败保留，换工程和拆除正确释放。36项定向、52＋118个Headless断言、57个原生断言／12图通过；深浅主题1440／1100／760×640，实际1.8倍缩放；solution与工具零警告／错误，专用窗口关闭。见[实施记录](../legacy-ui/ui-0496-refactoring-progress.md)／roadmap §18.164／ADR-0186。格式28／16／6、配置1、profile2及固定依赖保持；整页重构、其余缩放／平台／人工出口待后续；原审查goal、全量及性能继续暂停。

**2026-10-10控件接线检查修复与非渲染集成出口，保持0.49.6**：固定Suki模板声明PART_Popup，原正则把NameScope模板查找误当成工程视图查找。检查器改用已有Roslyn语法树区分接收者，普通Find／Require及PART_名称仍检查，注释／字符串不制造调用。15项定向与2311项非渲染范围全部通过，零失败／跳过；构建零警告／错误，原失败项实际执行通过。当前工作区已同步新建／导出修复；本批按用户授权用于main快进集成，实际Git引用为准。构建复用仓库内固定目录并在结束后清理；仅本次提交使用[skip ci]避免CI运行被排除的渲染，CI配置保持。产品／版本／格式／依赖不改，原goal及性能／含渲染全量／人工平台边界不自动推进。见[接线检查与验证](../legacy-ui/control-wiring-test-contract.md)／roadmap §18.163。

**2026-10-09独立导出入口修复，保持0.49.6**：用户要求移除原goal并修复后重启，不改版本。goal工具无删除接口，系统记录保持暂停，原路线不自动推进。菜单／快捷按钮允许进入负责FFmpeg探测的对话框，开始编码仍受原CanExport门禁；19＋2项定向、118个Headless断言、旧谓词成功编译后变异及普通程序两入口各14断言通过。实际短片各640×480／24fps／72帧／3秒音轨完整解码，当前构建零警告／错误。已备份用户Test工程和库／状态，工程SHA保持；准备关闭时旧PID203090已退出，原因未知，新普通PID283038／niri231已启动，原入口转向健康新包并复用原profile，无自动交互。见[导出入口合同](../legacy-ui/export-entry-contract.md)／roadmap §18.162；全量、性能及既有人工／平台边界保持。原NTFS目录未同步。

**2026-10-09用户人工反馈与清单整理**：用户确认“测试完成，目前已经可以正常新建窗口”，0.49.6新建入口恢复记为人工通过；其余原goal条目不扩展确认。具体改动／提交／逐项操作和自动复查入口见[用户验收清单](../acceptance/code-review-remediation-user-acceptance.md)。本批仅整理文档与反馈，不改产品、不运行构建／测试／性能或原生自动化；原goal继续暂停，整体出口未完成。

**2026-10-09普通启动故障修复，当前0.49.6**：用户0.49.5日志确认SKShaper空`asset`异常，旧普通执行体注入后首页部分发布、模板菜单退出-6已复现；用户原字体流为空的环境原因未确认。当前字体管理器匹配并门禁可读取字体，失败在新建发布／取走候选音频前诊断，旧工程、历史及播放保持；两个入口显示错误并允许重试。Rendering64／App5、普通真实入口32个断言／6图、真实无字体进程及三项成功编译后变异通过，生产源字节恢复，solution零警告／错误构建；恢复专用模板重试PID201175退出0，专用窗口关闭。健康副本`/home/zydr/.cache/MakoKaraGenerator/new-project-repair-checkout`源于精确相同`1f67606`，本批从该副本提交推送；原NTFS内核写入阻塞且自有D状态线程尚未保证释放，未执行root／卸载／磁盘修复，旧目录及旧脚本未同步。普通338文件独立包已核验并启动，PID203090／niri153，标题`MakoKara — 首页 0.49.6`，日志只有版本记录；入口`/home/zydr/.cache/MakoKaraGenerator/start-0.49.6.sh`，独立配置且无自动交互。见[新建合同](../legacy-ui/new-project-startup-contract.md)／roadmap §18.161／ADR-0185。PATCH保持公开签名、SDK、程序集、格式28／16／6、配置1、profile2及固定依赖。**原goal保持暂停；全量、性能和未知失败提示均不自动恢复。** 用户原系统选择器／输入／试听及整体目标未验收，旧长片／连续路线保留各自执行体范围。

**2026-10-09代码审查修复F6f，当前0.49.5**：保持打开的来源菜单旧位置已由真实截图复现，用户明确选择“接受自动收起并保留选择”。画面具名来源沿用Suki模板／原生弹层，实际窗口大小／可报告的位置／缩放或输入矩形改变时收起，包含祖先滚动；稳定布局保持打开，关闭／拆除／模板重建解除监听，布局不自动重开。来源／工程／历史保持，重开使用新菜单。App领域9、Headless483、最终原生186／6图、两项成功编译后变异及零警告构建通过，源字节恢复；PID91984退出0，全部自建窗口关闭。见[菜单位置合同](../legacy-ui/preset-input-popup-motion-contract.md)／roadmap §18.160／ADR-0184；不记旧菜单重定位机制已修复。普通0.49.5独立手测入口已准备，未启动；物理拖动／IME／听辨及完整人工制作继续，整个目标未完成。**全量、性能及用户暂缓的未知失败提示均不自动恢复。** SDK／中立接口、程序集、格式28／16／6、配置1、profile2及固定依赖保持；0.49.4作者与0.49.2长片保留各自执行体范围。

**2026-10-09当前0.49.4功能连续路线复查通过，产品保持**：F6e已以`a4e5dd4`提交推送。新编译专用原生作者328个断言／14图、两配置各720帧正式验证、预设／继承／撤销／布局、实际保存／闭包搬移、双输出短片、失败／取消及首页恢复通过；短片各60解码帧与88,200个44.1kHz单声道音频样本，均2秒。PID54880退出0，自建窗口全部关闭，未操作用户kitty／Edge／QQ窗口。第15帧编码失败由探针明确注入，用于旧片保护和释放／重试验收；当前日志无预览渲染失败。21个实施提交祖先及保留长片SHA／终态TRX已复核；不把0.49.2长片记作当前全量，不重启已结束任务。见[最终出口核对](../research/code-review-remediation-final-audit.md)／roadmap §18.159。用户所见失败提示按最新指示暂时忽略，原文未确认，不自动恢复该条排查。普通手测入口已准备，未替用户启动窗口；下一项人工键盘／IME／听辨和既有弹层位置边界，整个目标未完成。**全量与性能继续按用户要求暂停，不自动恢复。** 本批只更新验收文档／记录，产品／合同／格式／依赖保持。

**2026-10-09代码审查修复施工0.49.4，F6e定向验收通过**：分支`dev/code-review-repair`，F6d为`9fbea74`。歌词卡片按当前位置复用并保持树连接，只创建新增／移除多余位置，空行清空；事件消费当前行／元素，捕获固定工程引用／分支，换工程（即使元素ID相同）／换行／离开取消旧捕获。焦点与最终当前ID同步，空行后源输入不被抢焦点；共用FocusBorderThickness预留边框，聚焦不改变尺寸／排列。App50、Headless650、九项成功编译后变异和有限原生69项／8图通过，当前构建零警告／错误；专用PID50249退出0并关闭窗口。原执行体先复现紧凑125高度旧断言和焦点布局问题，再校正探针／修复产品；未通过正向的草稿变异和未进入新断言的增量产物均排除。当前7.2.9内核重新建立原／当前基准，最终六次交替样本P99仍约1.3秒；局部累计RebuildTokens采样下降，不记整体延迟改善。证据见[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.158／ADR-0183。PATCH0.49.4新增GUI共享焦点尺寸项，作者SDK／中立接口、程序集、格式28／16／6、配置1、profile2及固定依赖保持。**全量继续暂停，不自动恢复。** **用户最新要求暂跳过性能相关，不自动恢复优化或性能复测。** 整体排队／Reload／主题／字体布局暂缓；下一项功能连续／人工试听／物理输入／原生弹层搬动和最终总体出口；整个目标未完成。

**2026-10-09代码审查修复施工0.49.3，F6d定向验收通过**：分支`dev/code-review-repair`，上一批成片记录为`2f731ff`。画面区复用相同对象／预设／歌词项目及展示模板，多选重复刷新保持；编辑／撤销／重做按稳定ID保留分段并显示新文字，端点删除安全回退，换工程明确重置。同内容目录完整重读使用当前对象，引用门禁及实际映射／实例身份保持。App20／Application11、Headless656、三项成功编译后变异及有限原生54项／6图通过，当前构建零警告／错误；PID1455618已退出0，全部专用窗口已关闭。原执行体先复现1.8倍尺寸测量问题，再修正物理像素取整探针；旧原生弹层搬动位置仍有边界，不拿重开截图证明修复。全部六次未插桩同夹具样本及当前线程记录见[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.157／ADR-0182，整体P99仍未改善，歌词元素重建与主题／字体布局继续。PATCH0.49.3保持公开签名、SDK、中立接口、程序集、格式28／16／6、配置1、profile2及固定依赖。**全量继续暂停，不自动恢复。** 整体目标未完成；人工试听／物理输入／Windows及最终总体出口保持。已记录失败编译和一次误用旧二进制均排除，不作为当前证据；当前版本重新编译及恢复复查通过。

**2026-10-09代码审查修复连续路线复查，产品保持0.49.2**：分支`dev/code-review-repair`，F6c已以`aadcd04`提交推送。当前专用原生作者链328个断言／14图通过，两配置各720帧正式验证、实际保存对话框／后台写入、固定闭包搬移、双输出短片、失败／取消真实界面和恢复已覆盖；七组Headless567个断言、仓储／保存29项与导出最后提交边界52项通过。自有300秒音频／60秒视频备份后终止专用写入进程，真实新进程在外部媒体不可用时恢复完整闭包。原生PID1316290已退出0，所有自建窗口已关闭，未操作历史0.43.10窗口。同一完整57秒双输出单项定向验收已退出0，普通／透明各1710个编码输入及解码帧、完整音轨与失败／取消／释放通过；TRX及保留产物位于`artifacts/code-review-repair/final-acceptance/fullfilm-0.49.2/`，不重新启动该已结束任务。证据与有限探针修正见[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.155～18.156。整体UI排队延迟、人工试听／物理输入及最终总体出口继续，目标未完成；**全量继续暂停，不自动恢复**。本批只修改验收工具／记录，产品、公开合同、格式和依赖保持。

**2026-10-09代码审查修复施工0.49.2，F6c整形回退定向验收通过**：分支`dev/code-review-repair`，F8c2为`dea76c9`。按R13当前栈补完整元素自动回退／缺字缓存，1024项／64个UTF-16代码单元边界，已持有族复用；字体选项、引擎／线程及原生释放保持，字形／诊断／像素与新引擎相等。Rendering76／App5、Headless417、三项成功编译后变异及零警告构建通过，五次同fixture基准与精确原执行体对照见[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.154／ADR-0181。局部查找累计采样下降，整体P99仍波动，不记全部卡顿解决。PATCH0.49.2保持公共整形API／SDK／中立接口、程序集、格式28／16／6、配置1、profile2及固定依赖；内部观测仅测试使用。**用户要求暂时不跑全量，不自动恢复。** 下一项最终连续／人工路线及整体排队边界，目标未完成；不启动／操作用户0.43.10窗口，不推定原生、Windows或物理输入／试听验收。

**2026-10-09代码审查修复施工0.49.1，F8c2定向验收通过**：分支`dev/code-review-repair`，F8c1为`8032114`。当前生产317处AXAML数据Binding、17个模板及12处C#绑定明确类型，11个命令控件初始范围与编辑器模型分开，泛型Value保持；当前无生产反射例外。先验证实际上下文／模板／错误／命令，再移除App全局false；固定12.1.3及MSBuild默认均为true。材质槽模型／显示／运行请求保持一致，未变集合不重建；模板字符串只读名称，不遍历包材质的纯色访问器，只读配方标签及空行标题安全。App135／Core57、Headless665个断言、六项成功编译后变异及一项编译门禁通过，源码字节恢复，证据见[作者模块与绑定](../legacy-ui/authoring-module-and-bindings-contract.md)／[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.153／ADR-0180。PATCH0.49.1保持格式28／16／6、配置1、profile2、SDK／中立接口、程序集及固定依赖。**用户要求暂时不跑全量，不自动恢复。** F8c定向范围收口，下一项最终连续／人工路线，目标未完成；F6b剩余UI延迟保持未解决边界。原生工具仅编译，不启动／操作用户0.43.10窗口，不推定Windows或物理输入验收。

**2026-10-09代码审查修复施工0.49.0，F8c1定向验收通过**：分支`dev/code-review-repair`，F1～F8b已提交推送，F8b为`e3e448c`。引用清单确认旧实例／基线窗口无当前产品入口，旧窗口、专属模型和无消费者命令／事件退出生产；当前共享样式字段、草稿／编解码器及绑定说明迁入App.Authoring，IStyleDraftContext明确实时预览／属性支持，独立脚本配置和实际预设应用保持。历史后端夹具仅在测试程序集，不冒充当前作者UI验收。首批四样式视图明确类型／局部编译绑定，App133＋真实后端19、Headless作者15／草稿129／通用313／任务93／分页11及三项成功编译后变异、一项绑定编译门禁通过，当前solution及原生工具编译零警告／错误。旧诊断断言先在迁移前执行体复现，再验证完整报告／实际翻页Error来源。见[作者模块与绑定](../legacy-ui/authoring-module-and-bindings-contract.md)／[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.152／ADR-0179。MINOR0.49.0退出旧公开UI类型／命令并迁移共享作者类型位置；格式28／16／6、配置1、profile2、作者SDK及固定依赖保持。**用户要求暂时不跑全量，不自动恢复。** 下一项F8c2其余稳定视图与动态例外，最后移除全局默认，再最终连续／人工边界；F8c整体及目标未完成，F6b剩余UI延迟不记为解决。原生工具仅编译，不启动或操作用户0.43.10窗口，不推定Windows、物理输入或人工验收。

**此前代码审查修复施工0.48.0，F8b定向验收通过**：分支`dev/code-review-repair`，F1～F8a已提交推送，F8a为`1adf71f`。Runtime.Hosting新增固定快照／帧计划执行器、结构化报告及无控件作者试样／真实媒体服务；App仅捕获请求与格式化中文。BasicLayoutOperation及拆分引用映射迁入Application，生产只引用Core，UI显式输入名称并映射错误；原ID／固定实例和seed、未选槽位／时钟、范围配方拒绝及一次撤销保持。新增16项，宿主16／布局21／App56、Headless通用332／真实布局确认29／分页11和四项编译后变异通过，原App各360帧及六短片全部68编码输入帧／完整解码保持，当前solution零警告／错误。见[验证与布局](../../runtime-validation-and-layout-contract.md)／[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.151／ADR-0178。MINOR0.48.0新增公开验证／作者／布局用例，格式28／16／6、配置1、profile2、作者SDK、中立接口和固定依赖保持。**用户要求暂时不跑全量，不自动恢复。** 下一项F8c共享作者模型／旧入口／编译绑定，再最终连续／人工边界，整个目标未完成；F6b已记录的剩余UI延迟不记为解决。不启动或操作用户0.43.10窗口，不推定Windows、物理输入或人工验收。

**此前代码审查修复施工0.47.0，F8a定向验收通过**：分支`dev/code-review-repair`，F1～F7b已提交推送，F7b为`c6fcf62`。Runtime.Hosting独立于GUI，正式快照编译、固定native资源装配、像素会话及完整编译来源诊断从App迁出；组合根显式创建工厂，Export仍通过中立接口消费。执行代码和释放顺序保持，无GUI宿主9项、App63项与四项编译后变异通过，原执行体和新宿主双输出12次帧／完整诊断对照相等，当前solution零警告／错误。见[正式宿主](../../runtime-hosting-contract.md)／[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.150／ADR-0177。MINOR0.47.0新增正式宿主程序集，格式28／16／6、配置1、profile2、作者SDK和固定NuGet版本保持。**用户要求暂时不跑全量，不自动恢复。** 下一项F8b验证／作者服务／布局用例，再F8c旧入口／编译绑定及最终连续／人工验收，整个目标未完成。不启动或操作用户0.43.10窗口，不推定Windows、物理输入或人工验收。

**此前代码审查修复施工0.46.0，F7b定向验收通过**：分支`dev/code-review-repair`，F1～F7a已提交推送，F7a为`80f3831`。完整诊断以32条不可变分块、代次／顺序号共享，分数及绘制后缀独立，旧数组按需物化；执行、Skia、缓存、预览、导出和运行验证增量读取，真实同文多次报告及完整来源保留，Error被捕获仍失败，早期门禁及释放异常保留此前日志。完整导出／验证记录与50项分页接通。新增32项，当前Scripting63／Rendering144／App69／Export58、Headless分页11、10项编译后变异通过，solution零警告／错误；24／30／60fps普通／透明短片每个编码输入及完整解码通过，缓存释放后保持。81个隔离样本逐一核对复制／访问；180秒Warning读取58,336,201降为21,601条，分配6100 MB降至504 MB，完整数量保持，明确完整数组及Records仍有二次成本。见[诊断快照](../../scripting-diagnostic-snapshot-contract.md)／[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.149／ADR-0176。**用户要求暂时不跑全量，不自动恢复全量。** MINOR0.46.0新增宿主快照／读取／完整报告，作者报告方法及旧数组签名、格式28／16／6、配置1、profile2、固定依赖与生产程序集保持；F8a独立运行宿主、F8b用例、F8c旧入口／编译绑定及最终连续／人工验收继续，整个目标未完成。不启动或操作用户0.43.10窗口，不推定Windows或人工验收。

**此前代码审查修复施工0.45.5，F7a定向验收通过**：分支`dev/code-review-repair`，F1～F6b已提交推送，F6b为`3a1c8d8`。每个重放代次一次校验固定事件表，历史共享可见前缀，同刻逐条推进、旧快照不增长；公开Records维持ImmutableArray并显式读取才物化，公共构造严格校验保持。新增9项，Scripting59／Rendering82＋端点计量1／App46及六项编译后变异通过，当前solution构建零警告／错误。前后各36个10／30／180秒样本，180秒50Hz无数组作者170,127,900条校验／宿主复制降为校验9000、复制0，分配约11.38 GB降至673 MB；显式Records仍有物化、Warning诊断仍约6100 MB，F7b继续。真实24／30／60fps普通／透明全部编码帧、过去／未来独立端点及桥接首次／命中保持，完整证据见[历史前缀](../../scripting-history-prefix-contract.md)／[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.148／ADR-0175。**用户要求暂时不跑全量，仅构建与相关定向验证，不自动恢复全量。** PATCH0.45.5，公开作者／宿主签名、格式28／16／6、配置1、profile2、固定依赖及生产程序集保持。下一项F7b诊断，再F8及最终连续／人工验收；整个目标未完成。不启动或操作用户0.43.10窗口，不推定Windows或人工验收。

**此前代码审查修复施工0.45.4，F6b定向验收通过**：分支`dev/code-review-repair`，F1～F6a已提交推送，F6a为`51a03c8`。应用层共享只读模板目录，画面控件显式注入；普通歌词编辑及对象选择只过滤内存，激活／失效后台复核真实索引与完整资源，单读取者合并，隐藏不读，迟到读取／乱序通知受保护。错误保留最后成功目录并门禁新应用／更新，已有固定工程应用和作者草稿不升级。新增11项，Application27／Core18／App40、Headless目录29／布局332／反馈22／保存23／命令93／库57及六项编译后变异通过，构建零警告／错误。63 MB工程／三个包／100材质模板基准中199次UI全库读取降为首次一次后台读取，普通编辑／选择全库及索引均0；最终带窗口Headless P99仍1456.9587ms，剩余UI延迟不宣称解决。合同与实际边界见[目录合同](../../template-library-catalogue-contract.md)／[修复验收](../acceptance/code-review-remediation-acceptance.md)／roadmap §18.147／ADR-0174。**用户要求暂时不跑全量，只做构建与相关定向回归，不自动恢复全量。** PATCH0.45.4，格式28／16／6、配置1、profile2、SDK、固定依赖及生产程序集保持。下一项F7脚本历史／诊断，再F8及最终连续／人工验收。未启动原生窗口，不操作或新开用户0.43.10窗口，不推定Windows或人工验收。

**2026-10-08 主分支集成完成**：`main`已从`29e39b3`快进至`0ce4030`并推送`origin/main`，完整包含脚本系统、SukiUI迁移、三工作区重构及图形脚本参数面板的67个提交。后续开发以`main`为基线；三个`codex/`分支及旧Copilot分支保留，全部现有远程开发分支的提交均已包含。集成未修改已验证代码，3369项全量与零警告构建证据继续适用；本次只补集成记录，不扩大此前人工验收范围。详情见roadmap §18.135。

**此前三工作区施工0.43.10**：独立脚本参数按固定包／导出生成；枚举／图片下拉、有限数值滑块＋文本、宽范围纯文本、开关、文字、精确RGBA及曲线控件已接通。默认不写覆盖，时长保持整微秒／整数不经浮点；非法草稿保留、参与撤销及离开检查并阻止保存／运行验证，一次手势一次撤销。30项定向、353项Headless、38项／7图原生及4项变异通过；当前完整solution3369项全部通过，零失败／跳过，构建零警告／错误，证据见roadmap §18.134；用户已反馈本批0.43.10窗口“测试完成，无问题”，参数面板人工验收通过，完整连续制作路线不由此推定。格式28／16／6、配置1、profile2和固定依赖保持。用户明确要求后已启动当前0.43.10人工窗口，PID496626／niri230，不自动操作该窗口。

**0.43.9历史基线**：画面区新增默认可见的歌词行目录，分支／多选同步实际应用与一次撤销，窄窗选好后明确进入预设面板；新建须音频＋主歌词全部准备成功，初始标为未保存，追加歌词明确已有／新建分支。独立脚本正式运行验证普通／透明各360帧，保存、编译及采用修订分开；取消／关闭等待释放。29项定向、321项Headless、47项／10图原生反馈、266项／29图连续作者链通过，构建零警告／错误。保留弹层打开调整视口的36项／6图通过，未复现旧递归，也未宣称定位其根因。当前完整solution3358项全部通过，零失败／跳过，包含57秒双输出全部帧及音轨；用户人工验收未获结果。旧0.43.6／0.43.7人工进程均已退出，日志未记原因；不操作其他用户窗口。证据见roadmap §18.132～18.133。

**本目标的最新UI测试授权（2026-10-07）**：用户明确允许自行启动窗口交互测试和截图，覆盖下文历史“不得恢复窗口自动化”的限制。默认后台／无窗口验证；必要的原生测试使用专用窗口与隔离配置，不操作用户已有窗口，不留下交互循环。

**2026-10-08 三工作区重构施工版0.43.9，第2步数据／存储、正式运行投影、对象应用及模板制作入口已接通，自动出口已验证、用户人工验收待完成**：分支 `codex/ui-workspace-ownership`，合同见[三工作区方案](../legacy-ui/ui-workspace-ownership-plan.md)／ADR-0145～0161。第1步原型／UiControlMetrics及共享ScriptConfiguration/schema1闭包已验证；现已增加Composite预设、Application范围配方、每对象一个PresetApplication、输入映射、固定实例身份和原子应用／更新／移除命令。工程／歌词／模板格式当前为28／16／6，包profile2保持；templates-v6不转换或覆写旧库，程序精确门禁继续拒绝旧工程。纯继承／效果投影、正式ProjectSnapshot／脚本准备／几何／画布消费及模板制作同级任务已接通；画面与样式主入口已改为稳定目标＋预设应用＋布局，不再直接编辑样式字段，并已提供工程、来源、行、文字、片段和节点的应用对象管理；歌词任务已切换到只读样式的中性实时预览，空档显示 `···`；模板工坊已接入独立脚本配置窗口和Composite组合预设制作。高级编排已作为画面区内部的基础预览模式接通，包材质使用声明回退，脚本数据及正式输出保持完整；四种可视布局和精确分段已接通，schema28调度上下文保留未选歌词原槽位及显示时钟；直接片段绑定、相对成员应用身份和种子同步，不能安全拆分的范围配方整次取消，一次撤销，证据见roadmap §18.124。无工程工坊、自有12秒试播、完整快照复制和独立作者历史已接通，证据见roadmap §18.125；歌词区职责清理、关联翻译中性预览和一次LRC损失确认已接通（roadmap §18.126）；具名面板已接通，中央编辑和预览不被覆盖，证据见roadmap §18.127；用户连续制作验收仍未交付；当前57秒完整长片已自动验证，不能替代用户真实连续制作与人工验收。所有已裁决的作用范围、继承、固定快照和精确选区行为不重复询问；静态／脚本参数只在工坊制作，同一对象一个组合预设。**统一尺寸为开发硬约束：AXAML与动态控件共用集中定义，不任意指定普通元素大小。**

**0.43.6画面应用出口补齐**：多选对象一次预演／确认／撤销，来源卡显示真实上级预设与动态槽位父链，具名来源输入必须明确映射；切换对象／撤销／重做同步实际映射，更新保留实例ID与种子。solution零警告／错误，定向6项、Headless299项、原生36项／6图通过。较早0.43.5完整solution3317项已全部通过，0.43.6非长片3320项全部通过；最新隔离0.43.5完整回归也已3317项全部通过。迟到音频／演示保护随后由0.43.7接通，下一项独立脚本正式运行验证；弹层打开期间视口变化的模板绑定边界仍需核对，不把探针关闭弹层后的成功记作修复。见roadmap §18.130。

**0.43.5连续制作与目标安全**：新建→文本／时间→独立脚本配置→玻璃／逐字强调组合预设→精确对象应用／局部继承／撤销→双行布局→闭包搬移重开→普通／透明导出及失败／取消／恢复已通过隔离原生265项／29图，Headless274项，当前隔离solution构建零警告／错误。修复新预设进入画面区未刷新、包材质名称搜索取色异常、范围快捷选择与实际目标不同步、空个人库无Composite基线及配方动作裁切。较早构建3317项完整回归已全部通过，最新提交隔离0.43.5完整回归也已3317项通过，用户人工验收待完成；此证据不能替代后续施工版最终全量。见[连续验收](../legacy-ui/ui-workspace-ownership-acceptance.md)／roadmap §18.129。

**当前人工专用窗口（用户于2026-10-08明确要求启动）**：0.43.10来自`artifacts/ui-parameters-final-0.43.10`当前零警告构建，PID496626／niri230，标题`MakoKara — 首页 0.43.10`。独立profile为`artifacts/ui-parameters-current-0.43.10`；复制0.43.9的templates-v6、script-configurations-v1及theme.json，未复制工程状态／缓存。日志`/tmp/makokara-parameters-0.43.10-startup.log`有JACK／ALSA设备探测警告，未见托管异常。启动时旧235563已退出，未对其执行关闭，原日志未记原因。当前全量3369项全部通过；用户随后反馈本批窗口测试无问题，参数面板人工验收通过，其他路线不由此推定；不自动操作或再开重复窗口。

**0.43.9历史人工窗口**：0.43.9来自`artifacts/ui-ownership-final-0.43.9`当前构建，PID235563／niri165，标题`MakoKara — 首页 0.43.9`，日志`/tmp/makokara-ownership-0.43.9-startup.log`仅版本记录，未见托管启动异常。独立配置`artifacts/ui-ownership-current-0.43.9`从旧0.43.7手测config复制模板／脚本库，旧副本保留；state／cache仍独立。完整回归3358项已全部通过，启动不等同人工通过。已向用户请求本批交互反馈，待答复；不操作或替换该窗口、不再开重复手测窗口。旧0.43.6／0.43.7进程均已退出，日志未记退出原因，不作为产品失败。

**0.43.4作者入口与脚本安全**：生产只保留三个同级任务、工坊下属独立脚本窗口及画面内部编排；直接工程效果作者和临时高级页签已退出。未保存源码参与统一关闭检查，固定修订持久保存／重开且不升级原配置，迟到导入受保护。7项定向、Headless265项及原生脚本24项／2图通过，默认solution零警告／错误；非长片solution3316项通过。历史0.43.2完整成片App554项通过，solution3314项有46项渲染编译失败，独立Rendering944项复查通过但根因未定位。下一项继续真实三工作区连续制作和最终验收，见roadmap §18.128。

**0.43.3当前面板证据**：独立solution零警告／错误，Headless270项、隔离原生面板158项／19图与基础编排41项／6图通过，三项遮挡／互斥／提示串入变异触发实际断言；非长片solution3313项通过；0.43.2完整长片App554项通过，但solution有46项渲染编译失败，不记为全量完成。下一步为连续制作、重复入口清理和最终当前构建／用户验收，见roadmap §18.127。

**0.43.2歌词证据**：歌词定向23项、Headless182项、隔离原生58项与7张截图通过；构建零警告／错误，四项变异捕获真实断言失败。首轮solution3309项中的透明旧片失败已独立复现为14秒画面与14.506667秒PCM交错产物；输出时长上限修复后作者短链1项、Export102项通过，修复后App554项含完整长片通过；solution3314项中46项渲染编译失败，不记为全量通过。证据及边界见roadmap §18.126；具名面板的接续见下一批§18.127。

**0.43.1历史自动证据**：非长片solution3305项、作者收尾App549项、滑块捕获收尾定向68项通过；最终构建零警告／错误，Headless160项／隔离原生57项和7张截图通过。正式普通／透明会话保存重开同帧，五项变异捕获真实断言失败；本批未重跑57秒R8长片，不能替代最终连续制作及人工验收。证据与边界见roadmap §18.125。

**2026-10-07 UI 重设计已实施至0.41.1，最终人工验收待完成**：独立分支 `codex/ui-suki-redesign`；固定SukiUI7.0.1／Avalonia12.1.3，Semi与Material模拟层已退出生产。首页、两个常驻任务、共享播放及三个高级工作区已接通；用户实测反馈的弹层实底、重复视图入口、页签和按钮层级已修复。0.41.1非长片回归3197项全部通过，最后局部密度清理后114项Headless检查通过；最终构建零警告／错误。0.41.0已有3198项全量出口，本轮未重跑单项57秒长片。入口见 [使用指南](../legacy-ui/ui-workspace-guide.md)、[施工合同](../legacy-ui/ui-redesign-and-suki-migration-plan.md)及 [验收记录](../legacy-ui/ui-redesign-acceptance.md)，权威状态见roadmap §18.109～18.113。schema26／16／5、包profile2及精确版本门禁保持。**用户反馈窗口自动化干扰使用后已停止，不得自行恢复原生窗口自动化；后续默认后台／无窗口验证，不操作用户桌面。**

**本轮人工反馈修复已交付0.41.1**：帮助／诊断／高级／取色等Flyout使用原生Suki实底，视图菜单已移除重复效果入口；常驻与高级页统一ToggleButton页签和排他选中，标准文本动作36px、紧凑区32px，同一密度统一字号与留白。深浅主题弹层模板／离屏像素、菜单入口、反复切换、关闭及760／940／1440宽度通过Headless检查；应用正常启动供人工复查，不启用桌面自动化。依据ADR-0144／roadmap §18.113。

**2026-10-06当前脚本goal已完成**：用户实测确认文字脚本、粒子脚本与资源效果均无问题，并明确接受本轮目标完成。验收及范围见roadmap §18.108／ADR-0141；程序保持0.40.2，原路线完整SDK／R3.1等未实现能力继续后续。

**Linux 上可用的逐字歌词打轴编辑器已经能用，也已经能导出成片**：导入音频 → 导入/输入歌词 →
手工逐字打轴（含循环选区与 J/K/L 播放控制）→ 样式与合成参数调整 → 预览 → 导出带音轨的 MP4；
改完忘了保存也有兜底（每 30 秒自动写备份，重开时能恢复）。
**GUI 当前使用原生 Suki 外观**，强调色从应用级 `theme.json` 读取并实时更新；中文回退、视频字体和工程外观与界面主题独立。历史Material You／Semi记录保留作来源，不作为当前施工依据。
阶段 0~5 的出口判据均已达成（阶段 5 已人工验收）；**阶段 6（C# 脚本）已完成 R0／R1，R2 已接上源／场景目录、固定字形、准备帧、生产元素族缩放、固定帧服务、封闭事件／订阅／历史协议及静态元素基线／生产 Style writer、六阶段与生产圆点，完整功能仍按 R2～R8 实施；阶段 7 已有核心测试与 Linux CI，发布相关工作尚未完成**。
MVP 14 条：**全部达成**（逐条对照以 `docs/roadmap.md` §18.2 为准）。

**此前已交付程序 0.41.1（2026-10-07，UI依据见ADR-0142～0144；领域样式／场景依据见ADR-0050～0067）**：模板工坊制作行／元素模板，歌词行编辑器编辑源文字、独立时间及套用；通用展示树保存 `nodes`、`programs.clips`、`lineOverrides.localTransform`，任意槽位与共同父组共用位置和编辑规则，双槽／三槽只是预设。0.8.1 U1～U4 已获用户“测试验收通过”。**0.9.0 卡拉 OK 前奏 L1～L4 已实施、自动验证及人工验收通过**：提示组／文字标记是真实节点，`programs.leadIns` 绑定目标句，准备阶段保持原片段、槽位及 BindingId；目标在倒计时开始一次入场、起唱连续衔接，支持跟随目标句／固定父组。开头及长间奏后自动候选先分析和预览，手工／禁用／排除受保护，选定结果加入同一展示草稿，一次应用／撤销。工程／歌词／模板 schema **26／16／5**，精确版本门禁保持，不转换旧工程。 U1 已接通模板工坊未唱／已唱／描边材质槽位、完整九类包参数、目录／`.mkfx`导入及正式异步试样；不改变工程／歌词／模板 schema 或包 profile。原六句编排、注音及历史验收继续保留。U2 已接通脚本效果实例工作区的包／实例分栏、实例 CRUD／顺序、绑定／订阅／seed／参数编辑、目录／.mkfx 闭包导入与原子分享；源码跨包／文件草稿、新修订独立编译与诊断定位、单实例接受、失败源码保存和撤销提交已接通。保存与运行验证分开，新建实例禁用；行模板 TokenFamily 制作已在0.39.0接通，完整模板闭包分享／导入入口已在0.40.0接通；本计划R7连续组合已在0.40.1自动验收；本计划R8完整成片与恢复已由0.40.2自动验收；跨输出、父捕获仍未完成；2026-10-06文字／粒子／资源效果已获用户总体验收通过，当前goal完成（roadmap §18.108／ADR-0141）。权威状态见 roadmap §18.24.1，证据与使用路线见 [`docs/karaoke-lead-in-plan.md`](../plans/karaoke-lead-in-plan.md) §13～14，当前架构见 [`docs/current-architecture.md`](current-architecture.md)。**脚本 R0 已接受，R1 双链验证已完成，完整功能未交付**：用户本轮选择 C#＋Roslyn，并确认不重排的逐字／重复视觉放大、注音跟随、起唱计数及两种粒子跨行方式；新增方向见 ADR-0068 和 [`docs/scripting-effects-sdk-package-plan.md`](../plans/scripting-effects-sdk-package-plan.md)。用户随后提出自定义文字／描边 shader、玻璃与整行 glitch；最新评审见 ADR-0069 和 [`docs/advanced-material-render-graph-plan.md`](../plans/advanced-material-render-graph-plan.md)。用户已确认透明输出跳过背景依赖效果并提示，材质回退为包内纯色／渐变。最新具体施工、能力边界和破坏性变更见 [`docs/scripting-system-implementation-roadmap.md`](../plans/scripting-system-implementation-roadmap.md) R0～R8／ADR-0070；用户已接受完整合同 A／B／UI（ADR-0071）；0.9.1 已有最小 SDK、离线编译／持久 DLL／卸载和真实 CPU shader／透明编码证据，详见 [R1 报告](../research/scripting-r1-capability-report.md)及 roadmap §18.30；0.9.8 已实施 R2 源／场景目录、固定字形、纯帧求值／prepared frame、生产元素族缩放、九类参数／资源／随机／音频输入、封闭事件／源订阅／只读历史及静态元素基线／生产 Style writer；共享 OnEvent／Advance／Evaluate、120 Hz／精确事件、真实 RepeatEmphasis 与 Error 诊断门禁已接通；最终播放头、阶段限定基础查询、独立未来端点和 typed 映射值已接通；0.9.9 已补齐行／组／标记基线、真实 FrameMark／组几何和生产标记 Style／仿射；组级规则按 ADR-0073；0.9.10 已接通整行仿射、真实运行父树传播及最终行／族／标记／组几何和播放头（ADR-0074）；0.9.11 已接通同实例六阶段、最终几何视觉上下文与生产圆点（ADR-0075）；0.9.12 已补齐 R5 独立音频分支的真实 48 kHz／20 ms 分析、修订、节奏／低频及初始化 required 门禁（ADR-0076），音轨外 required 按0.16.0仅检查初始化；0.9.13 已补齐固定 shader 声明／七类 uniform 帧值及正式 Skia 原语的严格编译、反射／CPU 像素验证（ADR-0077）；0.9.14 已补齐中立图定义／拓扑与端口校验、正式 CPU 五类操作／替换合成／透明旁路及真实短片同帧验证（ADR-0078）；0.9.15 已实现生产固定字形／注音／前奏／真实组捕获、ImageEffects 中立绑定及真实实例图执行适配，用户确认同实例同目标最后绑定、跨实例串接（ADR-0079）；0.9.16 已将会话／帧中间表面账接入捕获、图执行、合成及真实绑定，存活产物／借用别名／跨帧／Blur 和显式捕获图层计费已验证（ADR-0080）；0.9.17 已实现保留全部替换范围的合成链、真实媒体／底色输入及绑定级合成错误（ADR-0081）；0.9.18 已实现六类真实彩色部件、共同 alpha 分离及显式顺序重组（ADR-0082）；0.9.19 已实现 R4 独立内容闭包、目录／.mkfx 原子分享与固定源码离线编译（ADR-0083）；0.9.20 已实现九类包参数、控制器导出／依赖校验、真实图片解码门禁和清单到生产帧／像素路径（ADR-0084）；0.9.21 已按用户确认接通独立 shaderProgram JSON、七类 ABI／数组及正式原生验证／固定逐帧程序集合（ADR-0085）；0.9.22 已按确认 B 接通包图 JSON／逐输入捕获、真实图片图输入及 CPU／透明旁路验证（ADR-0086）；0.9.23 接通固定时间连续发射、真实出生几何、解析粒子／拖尾及公共圆点输出（ADR-0087）；0.10.0 按用户确认增加独立纯 EvaluateEmitters，作者完整粒子／区间全部出生及独立100000条记录预算、真实 CursorTrail 包分享／消费已验证（ADR-0088）；0.12.1 已接入元素／行／标记主画布重组与真实下层；组图／父捕获、包材质及完整 native 资源出口仍待接入；0.11.0 已补事件 Burst、四类真实粒子来源、边界出生和统一预算（ADR-0089）；整行 Style 等其余 writer、实际图像映射及目标内捕获仍待完成；独立线段／带状拖尾已在 0.27.0、固定图片精灵已在 0.28.0、Bridge 发射已在 0.30.0 接通，见 [R2 记录](../research/scripting-r2-sdk-geometry-progress.md)；R2 出口及 R3.1～R8 仍未完成；R3 Core 已按 roadmap §18.81 收口，本计划 R4 持久闭包组合验收见 §18.82～18.85；本计划 R5 自动出口已按 §18.86～18.89 收口：诊断、双文字／音频／FPS／前奏、预览输入、参数／源码撤销、热替换及取消编码保护与释放已验证；本计划 R6 自动出口已按 §18.90～18.95 收口，R7连续自动出口已按§18.105收口，本计划R8已按§18.107收口自动出口；完整SDK／原路线高级输出／R3.1继续；本轮goal用户验收已通过，性能基准后置。

**0.12.1 主画布接续**：固定图集合接到正式 Render 的元素／整行／标记部件协调；逐输入配方、最高层融合、元素产物进入整行、真实 BackdropBelow、历史替换范围、共同 alpha 和透明旁路诊断／缓存／导出提示已验证。用户确认融合后局部切分须拒绝、无目标输入图替换全目标。组隔离／跨行父组及自身后置粒子排除、owned overlay、区域材质、实际映射和持久工作区仍未完成；缺少固定资源报告 ImageEffectResourcesRequired，不输出无效果成片。最新合同见 [`docs/scripting-main-canvas-composition-contract.md`](../../scripting-main-canvas-composition-contract.md)／ADR-0093／roadmap §18.59。

**0.12.2 组透明度接续**：Style.MultiplyOpacity(GroupTarget, factor) 及真实包 StyleModulation/group 已接到准备帧和主 Render，仍仅工程／显式节点可写；因子按调用／实例相乘并最后校验，父子组在真实运行父树上逐层隔离，孩子实际图先完成、组 alpha 各一次。交错后代的连续单位差异经帧／缓存／导出报告保留。原始 TargetColor 捕获遇到组 alpha 明确报告 GroupOpacityCapturePending，不返回忽略输出的原图；组图、可按部件读取的父捕获、owned overlay／自身粒子、区域材质及持久工作区继续。合同见 [`docs/scripting-group-opacity-isolation-contract.md`](../../scripting-group-opacity-isolation-contract.md)／ADR-0094／roadmap §18.60。

**0.12.3 父捕获合成产物基础**：现有CPU图后端新增独立前景／实际擦除产物，提取同目标历史、按绘制顺序合成独立孩子及应用真实下层；两份表面独立拥有、共同alpha各一次，严格预算／上下文／取消与释放。组图writer／父捕获仍未开放；部分孩子alpha、无可见孩子、父图继承擦除三项已询问待答，不默认实施。合同见 [`docs/scripting-parent-composition-contract.md`](../../scripting-parent-composition-contract.md)／ADR-0095／roadmap §18.61。

**0.13.0 区域材质接缝**：用户已确认最终输出限制在绑定区域、TargetColor 读取包内基础外观，以及明确 unmasked／masked 两种覆盖模式。新材质清单必填 coverageMode，源码示例／测试 package profile 升至2；真实固定字形／注音／外描边与互补逐字区域捕获、包导出执行、一次覆盖／已覆盖像素验证和透明基础外观旁路已接通。0.13.0当批准备帧和主Render未接入；0.13.1生产接续见下文，仍不是完整R3或工作区。实施与证据见 [区域材质合同](../../scripting-region-material-contract.md)／roadmap §18.62／ADR-0096。

**0.13.1 区域材质生产接续**：0.13.1 已接通元素族／真实前奏标记的 PackageExport 准备帧和正式主 Render。未唱／已唱／描边逐属性保留最后写入者，真实 StyleView 保留包句柄／参数，LyricStyle 只作明确几何绘制种子；没有归属或固定资源拒绝。已唱／未唱互补裁切，基础字及注音使用实际材质，后续整行图读取已处理彩色部件；元素局部 alpha、标记／行共同 alpha、组 alpha 各一次。透明背景依赖使用自身包基础外观并保留真实 MaterialHandle 提示，缓存和导出按实例去重。原始 TargetColor／彩色部件捕获遇到未重组材质报告 MaterialProcessedCaptureRequired，不返回白色种子。 本批不新增工程字段、包编码、程序集或依赖；程序升 PATCH 0.13.1，schema21／15／3与package profile2保持。Materials.SetUniform、组图／父配方、映射、跨输出执行计划、工程／模板／分析事务和工作区仍待实施；原七项待答不使用默认值。完整 R2～R8／R3 出口保持未完成。 真实包／像素／MP4与Alpha、中途失败及全量2460项已验证；当前构建已启动PID668689，日志仅版本记录；见[区域材质合同](../../scripting-region-material-contract.md)／roadmap §18.63／ADR-0097。

**0.13.2 直接渐变描边与透明度修正**：现有元素族／标记SetStroke已支持直接GradientMaterial，Linear／Radial、Canvas／Line、LinearRgb／Hsv和三种连接方式进入真实准备帧／固定glyph。Core有效渐变允许Color=null，注音共用族外观。普通渐变填充／描边去掉画笔重复的历史纯色alpha与style.Opacity，色标及样式alpha各一次；纯色与包材质独立覆盖保持，非法组合／原生退化明确失败。没有新公开方法、编码、程序集或依赖，schema21／15／3及profile2不变；完整SDK未冻结、R2～R8未完成，组图三项及原四项待答不默认。2485项全量通过、零警告／错误，真实MP4／Alpha同帧和中途失败保护已验证，当前0.13.2已启动PID699180／niri336且日志仅版本记录。设计／实际证据见[样式帧合同 §5](../../scripting-r2-style-frame-contract.md)／roadmap §18.64／ADR-0098。

**0.13.3 材质逐节点uniform**：用户确认图节点ID＋名字、显式未唱／已唱／描边及Set按调用顺序清除旧覆盖后，Materials.SetUniform已接入元素族／标记、准备帧与真实主Render。StyleUpdate保留有序材料操作，宿主逐实例检查固定资源、实际区域／节点／名字、七类／数组／float32；后实例可在同修订及写权下覆盖前实例，真实归属包含最后uniform写入者，alpha／阴影不抢归属。透明回退前仍校验；非法早期写入不能由后Set掩盖，帧内不编译。原有音频／Callback重放比较的数组引用回归已在生产内容相等性修复，原断言保留。2527项全量通过、零警告／错误，真实MP4／Alpha同帧及200ms错误保护旧产物通过；schema21／15／3与包profile2不改。整行Style、组图／父配方、映射、跨输出／工程／模板／分析事务和工作区仍未完成，七项旧待答不默认。具体合同见[材质uniform](../../scripting-material-uniform-writer-proposal.md)，权威证据见roadmap §18.65／ADR-0099。

**0.14.0 工程入口与不可变歌词修订接续（自动验证通过）**：按已接受合同A§7.3，project.json新增lyricsFile指向真实UTF-8字节SHA-256的data/lyrics/<hash>.json；已有修订完整比对且不覆写，唯一暂存／刷盘／校验后发布，根入口最后替换。加载根与歌词分别只读一次，版本门禁最先；缺失／非法路径／链接／hash或JSON失败不给出空工程。另存为及每份自动备份持有独立歌词修订，失败不更新saved引用；旧根lyrics.json及历史修订不删除。工程schema22，歌词15／模板3／包profile2保持，不迁移旧工程。完整媒体／效果包／分析事务、模板资源及工作区继续R4～R8。本轮新的待定细节按更新后的目标由代理依据合同决策并记录；不把旧待答当作已实现。2560项全量通过、0失败／跳过，构建0警告／0错误；最终0.14.0已启动PID815437／niri346，启动日志仅版本记录。见[存储合同](../../scripting-project-revision-storage-contract.md)／roadmap §18.66／ADR-0100。

**0.15.0 工程效果闭包与实例投影（自动验证通过）**：project.effects保存固定包／顺序实例、封闭绑定／订阅、16位hex种子、规范参数及预算。完整源码与资源先发布到effects/packages/<hash>，再歌词、最后根入口；已有内容不覆写，另存为及备份独立闭包，结构／链接／hash／身份损坏拒绝打开，非法参数与失效绑定保留。SetProjectEffectsCommand共用撤销，引用包拒绝注销，复制新ID／seed，接受修订只升级选中实例。Snapshot自动指纹，编译失败不回退旧DLL或部分效果；CompiledProjectEffects真实离线编译及控制器会话已接入，MP4／Alpha同帧通过。程序0.15.0、schema23／15／3及包profile2，无新依赖。完整媒体／分析／模板事务、直接材质／图实例、跨输出计划及工作区继续，不关闭R2～R8。2586项全量通过、0失败／跳过，构建0警告／错误；最终构建已启动PID852452／niri348，日志仅0.15.0记录；启动恢复旧工程出现版本门禁提示349，未作为GUI验收通过。见[工程效果合同](../../scripting-project-effects-contract.md)／roadmap §18.67／ADR-0101。

**0.16.0 媒体与已接受音频分析闭包**：工程schema24新增必填media／audioAnalysis，真实音频、背景图片／视频流式发布到data/media/<hash>.media，界面保留原名称。保存／另存为／每份备份全部持有媒体和完整真实hop修订；副本、源未变、长度／hash／链接／profile／网格严格验证，已有修订不覆写，资源先发布入口最后替换，失败保留旧入口与saved引用。分析区分未分析／无流／失败／可用，不加载时重算或制造静音；Core源值与SDK隔离，接受分析及切换源共用历史。CompileAsync(Project,...)把已接受分析投影回原AudioFeatures／真实hop事件和历史；完整内容进入会话指纹，required只要求初始化Available，音轨外仍OutsideRange／null，作者Error阻止导出。模板资源、直接图／材质实例、跨输出、其余SDK／粒子和R7工作区继续，完整R2～R8未交付。新增42项，全量2628项全部通过、0失败／跳过，构建0警告／0错误；真实音频重放、MP4／ProRes4444同帧、200ms错误保护旧输出及受管图片／视频解码通过。最终0.16.0已启动PID910561／niri350，日志仅版本记录；351为旧工程版本提示。设计与实际证据见[媒体／分析合同](../../scripting-media-analysis-storage-contract.md)／ADR-0102／roadmap §18.68。

**0.17.0 模板效果配方与资源闭包**：templates-v4/index.json与完整packages目录、.mktpl独立分享、严格语义／文件集合／共享展开预算及包先发布入口最后替换已接入，旧库不读、不覆写。相对line／tokenFamily配方进入工程单目标实例及来源槽，重套用保留ID／seed／enabled，更新修订／参数／配方顺序，仅清理本次来源；其他模板／直接实例保留，静态与效果一步历史，相同不重复。实例复制脱离模板，显式清除与静态继承独立。层覆盖进入真实圆点／连续／Burst，本帧ParticleSample.Layer可读。工坊静态保存／复制保持配方及包并持续标明预览范围；配方编辑、分享按钮与实时工作区仍后续。程序0.17.0／工程25／歌词15／模板4／包profile2，不迁移旧工程。源MaterialSpec、直接图实例、跨输出执行、自动组合根及完整R2～R8继续。新增46项，全量2674项全部通过，0失败／跳过，构建0警告／0错误；真实模板分享／重开／文字注音／图元层／MP4／Alpha同帧及200ms错误保护通过。编译门禁串行、原诊断／取消保持，六次并发DLL／PDB一致；没有宣称已定位Roslyn缺陷根因。最终0.17.0已启动PID1196201／niri352，日志仅版本记录；353为旧工程版本提示。最终验证证据见[模板合同](../../scripting-template-recipes-contract.md)／ADR-0103／roadmap§18.69。

**0.18.0 源材质唯一真值与闭包（M1）**：FillSpec只持有solid／gradient／packageMaterial，描边几何独立、材质整体继承；颜色／渐变不再保存隐藏双值，关闭渐变用首色标solid。严格当前JSON及工程／模板完整源材质资源、区域／导出／参数诊断、源引用拒绝注销已接入；schema26／16／5、templates-v5，旧库不转换或覆写。工坊保留资源、旧颜色控件只读、未就绪预览留空。源包运行明确SourceMaterialPreparationPending，M2真实归属／固定纯材质／准备帧和M3选择／参数继续；已有帧脚本材质与uniform保持真实路径。新增32项，全量2706项通过，构建0警告／错误；真实静态像素及独立源模板分享闭包已验证。Roslyn内部Emit间歇异常曾复现，最终标准全量通过但根因未确定、风险未解决，本批未改运行时配置。完整R2～R8不关闭。最终构建已直接启动PID1290924／niri362、日志仅0.18.0记录，363为旧工程版本提示，非人工验收。见[源材质合同](../../scripting-source-material-contract.md)／ADR-0104／roadmap§18.70。

**0.19.0 源材质正式运行（M2）**：完整源包／参数／继承路径在会话前固定，纯材质无需C#或假实例；真实包基线、源Source／后写Script归属、元素族／注音／描边／前奏准备帧与正式Render已接通。纯帧／时间线／独立端点共用投影，改变源拒绝旧会话。Set／uniform按显式部位接管来源，alpha不抢归属；源错误／透明提示保留真实路径与修订，预算失败回收并阻止导出。新增14项，全量2720项全部通过，构建0警告／错误；真实源／C#写入逐像素一致、MP4／Alpha同帧和200ms失败保护旧产物已验证。公开宿主来源协议升MINOR0.19.0，schema26／16／5及profile2保持。M3自动组合根、材质选择／参数与工坊实时预览继续，当前静态工坊仍明确未就绪；完整R2～R8未交付。既往Roslyn内部Emit风险仍未解决，本批未改运行时。最终直接启动PID1324042／niri371，日志有ALSA／JACK设备探测警告、未见托管启动异常，非材质GUI或音频人工验收。见[源材质合同](../../scripting-source-material-contract.md)／ADR-0105／roadmap§18.71。

**0.24.0 作者工作区接续已自动验证**：完整包导入／分享、跨包／文件草稿、多文件不可变修订、独立完整包编译及诊断定位、单实例接受、失败源码保存与关闭清理。字段错误分别保留，定义诊断与未提交输入分开；最终2776项通过、构建0警告／0错误、宽窄真实控件探针通过，尚无用户新GUI验收。已直接启动PID1673202，提交后复查已退出、日志未记退出原因；日志 `/tmp/makokara-f93b-source-workspace-final.log` 仅版本和ALSA／JACK设备探测警告，未见托管异常。证据见roadmap§18.76／ADR-0110；完整R2～R8继续。

- 提交历史：见 `git log`（最新提交即在最前）。
- 历史Suki迁移UI验证（0.41.0～0.41.1）：集成全量`dotnet test MakoKaraGenerator.slnx --no-build -m:1` → **3198项全部通过，0失败／跳过**；最新界面收尾后3197项非长片回归及38项无窗口布局检查再次通过，长片领域路径未改变。默认0.41.0构建0警告／错误，未自行启动额外窗口；证据与人工路线见[UI验收](../legacy-ui/ui-redesign-acceptance.md)／roadmap §18.112。历史0.40.2脚本3187项、57秒成片与恢复证据仍见[R8验收](../acceptance/scripting-r8-acceptance.md)。既往Emit／TypeLoad及本轮基线R8失败原因未定位，不因后续全绿记为修复。
- 主干：`main`（2026-09-26 的 Material You 外观与统一样式工作区改动已快进合并并推送到 `origin/main`，临时 `feat/*`／`design/*` 分支已删除）；远程 `origin` = `git@github.com:Zydr114/MakoKaraGenerator.git`（私有，当前已配置 SSH）。

---

最新接续：0.25.7 在 roadmap §18.85 收口本计划第 2 批持久闭包组合验收：模板分享、工程副本及备份搬移后，经已有／空 DLL 缓存保持相同资源、实例身份和像素；缓存命中仍校验当前 shader／ABI／图片，损坏 DLL／PDB 和失败源码在编码前阻止导出并保护旧输出。下一步 R5 会话，完整 R4／R7、R3.1 与 R5～R8 继续。
最新接续：0.34.0 已接通本计划第5批 R7 上下文效果入口：模板工坊保留合法行／元素配方，歌词行／元素编辑器与场景对象检查器把稳定 Lines／Tokens／Node 绑定传入统一效果工作区，已有同绑定实例优先选中，新实例在导出支持时复用目标；不自动启用、保存或提交场景草稿。容器／组效果继续由主画布门禁管理，完整绑定场景与最终 R7 人工验收仍待完成。见 [`docs/scripting-r7-binding-entry-contract.md`](../legacy-ui/scripting-r7-binding-entry-contract.md)／ADR-0131、roadmap §18.98。
历史施工接续：0.39.0 已接通行模板整行／行内全部元素族的相对目标选择，元素模板固定所选元素族；按目标筛选包导出，元素族非图元输出须为tokenFamily，UI与Core共同拒绝非图元跨行／标记写入及组输出。编辑按目标恢复精确导出，缺失匹配禁用更新；显式目标更新保留RecipeId，经完整参数／闭包验证后才发布试样，保存重开保留目标与包。见 [`docs/scripting-r7-template-target-contract.md`](../../scripting-r7-template-target-contract.md)／ADR-0136、roadmap §18.103。下一步 `.mktpl` 分享／导入 UI，再继续 R7 组合及 R8／人工验收。 最终3118项全量通过，构建0警告／0错误；当前构建PID **1938840**／niri **471**，日志 `/tmp/makokara-0.39.0-final-startup.log` 已核对版本且未见托管异常；472是自动重开旧工程的版本提示，不计为GUI人工验收。
最新施工接续：0.40.0 已接通模板工坊 `.mktpl` 完整闭包分享／导入，保留源材质、行／元素配方及精确包资源；同ID同内容幂等跳过，同ID不同内容整批拒绝，重读当前磁盘库且索引发布成功后才更新清单／选择。未提交样式／材质错误／配方输入共用离开门禁，传输期间禁用编辑，关闭取消并等待；分享保护库入口／受管包目录及旧文件。真实包材质模板选择的隐藏渐变崩溃与材质下拉重建丢失来源／参数已修复。新增31项回归、工坊定向57项、宽窄原生按钮／正式试样／关闭等待及变异验证通过，最终3149项全量通过，构建0警告／0错误。见 [`docs/scripting-r7-template-transfer-contract.md`](../../scripting-r7-template-transfer-contract.md)／ADR-0137、roadmap §18.104；下一步R7连续组合，随后R8／用户人工验收。既往Emit及本轮独立TypeLoad间歇风险均未定位，不因复跑通过记为修复。 最终当前构建PID **2240717**／niri **480**，标题已核对0.40.0；日志 `/tmp/makokara-0.40.0-final-startup.log` 仅版本记录，未见托管启动异常；481为重开旧工程的版本门禁提示，启动不替代用户人工验收，未终止旧窗口。
最新接续：**0.40.1本计划第5批R7连续自动验收已收口**。真实工坊作者→完整模板分享／导入→删除来源→真实行／元素套用→精确实例编辑→保存／撤销／重开→独立普通／透明正式会话，及模板来源坏源码／单实例接受／诊断定位／旧输出保护／撤销恢复与合法容器另链通过。材质影响比较崩溃、包库存编码漂移、窄窗失败长诊断挤掉旧画布三项普通缺陷已修复，升PATCH0.40.1，schema26／16／5及profile2保持。新增23项，全量3172项通过，构建0警告／错误；73项原生断言和15张截图、同包全部升级变异失败捕获均完成。详见[`docs/scripting-r7-acceptance.md`](../acceptance/scripting-r7-acceptance.md)／ADR-0138、roadmap §18.105。最终0.40.1已直接启动，实际PID **2450547**／niri窗口 **546**，启动时标题“演示工程 — MakoKaraGenerator · 技术验证 0.40.1”及进程存活已核对；提交后复查PID已退出，日志没有退出原因，journal与coredump未找到该PID记录，退出原因未确认。日志 `/tmp/makokara-0.40.1-final-startup.log` 记录版本及ALSA／JACK设备探测警告，未见托管启动异常；未终止用户旧窗口。启动与自动探针不替代用户人工验收。 下一步第6批R8公开材质／ColorOnly Glitch／玻璃包及作者文档，再验同一工程的完整成片／备份／失败取消恢复；原路线高级能力及最终用户人工验收继续。
当前施工计划（R8第1步公开资源包／作者交付，已完成自动验证）：CustomInk、LineGlitch、GlassInk纯资源profile2目录包、公开包索引、目录／.mkfx离开来源、工程重开、普通／透明正式Preview与Export同帧、真实背景依赖回退、非法shader／参数及RepeatEmphasis已唱solid边界回归已接通。只增加示例／测试／文档，宿主保持0.40.1，包版本1.0.0，不增加SDK或格式。整行Glitch仅ColorOnly，Glass分别提供真实媒体／下层区域材质，未唱与描边可用；ImageOnly和组图等门禁保持。R8定向10项、solution全量3182项通过，构建0警告／错误；当前应用PID **2591709**（dotnet run PID **2591688**）已启动，日志 `/tmp/makokara-0.40.1-r8-public-startup.log` 仅版本记录。已由 `ae7e0c4` 提交并推送至 `origin/codex/scripting-system`；同一作者工程的完整57秒成片、另存为／备份和中途失败取消恢复另步验证，不把本步样本双会话当作R8完整出口。见 [`docs/scripting-r8-public-packages.md`](../acceptance/scripting-r8-public-packages.md)。
自动验收记录：**0.40.2本计划第6批R8完整成片与恢复自动出口已收口**。九包／34实例作者链贯穿工坊制作／分享导入／删除来源／真实歌词套用／工作区订阅、工程搬移／独立副本／备份及两格式关键帧；普通MP4与透明ProRes4444各57秒／1710帧与独立正式Preview严格相等，全部解码及完整真实音轨、30秒作者Error／12.266666秒活动帧取消／新token恢复、14秒旧片全部字节、实际作者上下文和临时文件释放通过。透明视频／音频报告及导出动态默认项普通缺陷修复，升PATCH0.40.2，schema26／16／5及profile2保持。最终3187项全量通过、构建0警告／错误，真实工程／模板／成片／旧片／报告／hash与人工路线保留在 `artifacts/scripting-r8-author-20261006-final4`；最终92项原生断言及宽窄精确视口、正式画布、模板与实际报告通过，10张本轮截图与默认项截图保留。当前构建实际PID2872945／niri769已启动并核对0.40.2标题，日志 `/tmp/makokara-0.40.2-r8-accepted-startup.log`有ALSA／JACK设备探测警告、未见托管启动异常；未终止用户旧窗口。此前PID2865057复查已退出，日志无原因，原因未确认，未作为最终存活证据。具体见[R8成片验收](../acceptance/scripting-r8-acceptance.md)／ADR-0140、roadmap §18.107。完整SDK、ImageOnly／复杂映射、组图／父捕获、跨输出／R3.1等原路线能力继续；本轮用户总体验收及goal完成见§18.108，性能基准后置。
历史施工接续：0.38.0 已把模板配方原位编辑与顺序调整接入工坊；更新保留 RecipeId，包／修订／导出及参数先通过完整闭包校验，移动只改变有序列表，保存重开保持身份与顺序，失败保护旧预览。详细证据见 [`docs/scripting-r7-recipe-editing-contract.md`](../legacy-ui/scripting-r7-recipe-editing-contract.md)／ADR-0135、roadmap §18.102。该批未完成的行模板元素族制作已在0.39.0接通，`.mktpl` 分享／导入入口已在0.40.0接通；R7 组合和人工验收继续。
历史接续：0.37.0 已接通纯图导出正式运行与模板引用闭包；0.36.0 已接通歌词行／元素与展示树对象的相关实例清单、可读绑定与独立订阅及精确实例ID／节点定位。稳定源关联在未装载时保留，Node／Clips和祖先组影响共享正式当前帧运行父树；父组自身影响不扩大孩子写权，禁用／相关失效项及同绑定多实例保留。定位按保存／放弃／取消并保护未应用／过期场景。27项新增定向、变异和原生宽窄操作通过，最终全量3065项通过、构建0警告／错误；PID **1081000**／niri146、日志`/tmp/makokara-0.36.0-final-startup.log`有版本及ALSA／JACK探测警告、未见托管启动异常。合同见[目标效果合同](../../scripting-r7-target-effects-contract.md)／ADR-0133、roadmap §18.100。下一步R7组合，R8及用户人工验收继续；既往Roslyn Emit风险未解决。
最新接续：0.35.0 已保留歌词行／元素／场景对象入口播放头；无效果使用布局参考，含效果／源材质使用正式动态时间场景，有效布局纳入迟到帧门禁，显式验证复用同一请求。静态试样不影响导出、Preparing不冒充成功，未应用／过期场景入口禁用并提示；不合并场景草稿，保存／修订／撤销协议保持独立。上下文合同见 [`docs/scripting-r7-scene-preview-context-contract.md`](../legacy-ui/scripting-r7-scene-preview-context-contract.md)／ADR-0132、roadmap §18.99；完整容器／组效果、R3.1、R8 与最终人工验收继续。最终0.35.0构建已直接启动PID **1025198**／niri118，窗口标题已核对；日志`/tmp/makokara-0.35.0-final-startup.log`有版本及ALSA／JACK设备探测警告、未见托管启动异常；全量3038项通过，首轮既往Roslyn内部Emit异常风险仍未解决。
最终 0.32.0 已直接启动，PID **691846**；`/tmp/makokara-0.32.0-startup.log` 有版本行及 ALSA／JACK 设备探测警告，未见托管异常；该启动证据不替代用户人工验收。0.34.0上下文入口接续已完成最终启动与全量验证：PID **869758**，日志 `/tmp/makokara-0.34.0-startup.log`；窗口标题已由 niri 核对，全量 3025 项通过，详见 roadmap §18.98。

最新接续：本计划第4批R6自动出口已按roadmap §18.95收口：四类锚点／图元、连续／Burst、Jump／Bridge、顺序／随机／预算、六段真实视频全部410个编码输入帧及两种取消后的108个恢复帧与独立预览一致；完整解码、回退、旧产物保护和作者上下文释放已验证。只增加回归／文档，程序保持0.31.0；下一步第5批R7工作区／模板整合。原路线完整SDK／R6高级输出、R3.1、最终工作区及用户人工验收继续，不开放现有门禁。见 [`docs/scripting-r6-acceptance.md`](../acceptance/scripting-r6-acceptance.md)／ADR-0128。

最新接续：0.31.0 已接通完整VectorTrail／SpriteTrail桥接参数、EventBurst真实四类来源与混合持久工程。三种向量呈现／图片Bridge／Burst、双文字、音频与整行Glitch在正式预览和MP4／Alpha共用准备帧；预算、订阅权限、FPS／重建、热替换及关闭回收有自动证据。全量2997项通过、构建0警告／错误；当前构建已启动PID526499，日志仅版本记录，非GUI人工验收。本计划R6出口继续核对，目标内捕获及完整SDK／R6／R3.1／R7／R8保持开放；见[粒子包合同](../../scripting-particle-packages-contract.md)／ADR-0127、roadmap §18.94。

最新接续：0.30.0 已接通 Bridge 固定半开飞行、真实来源、逐边独立精确配额与正式圆点／拖尾。网格内起点只裁切积分、不改变共享语义边界；独立端点消费全部效果／音频／源材质，缓存固定值，未来 Error 保留实际失败实例，关闭／取消／重建清空。CursorTrail 1.1.0 完整包保留 Jump 默认，Bridge 可分享及持久重开。目标内捕获及完整 R6／SDK／R3.1／R7／R8 继续；见[桥接合同](../../scripting-cursor-bridge-contract.md)／ADR-0126、roadmap §18.93。
最新接续：0.29.0 已接通固定真实 CursorTransition 事件、并发后继集合及按实际 BindingId 筛选的独立最终端点。相邻同时起唱保留并发组，重叠 GapUs=0；不逆时间接续、跨缺行时间或猜第一条。当前 Bridge 发射仍明确拒绝，下一步接入固定半开飞行区间、真实来源／配额与正式绘制；完整 R6／SDK／R3.1／R7／R8 继续。见[后继合同](../../scripting-cursor-successor-contract.md)／ADR-0125、roadmap §18.92。
最新接续：0.28.0 已接通 R6 固定图片精灵、归一化裁片／世界仿射／Tint、正式固定图片表与同帧资源账。SpriteTrail 完整包使用真实 Custom 粒子及固定拖尾，当前图片新修订可复用 DLL 但仍消费新像素。Bridge 合法后继与独立未来端点、目标内捕获及完整 R6／SDK／R3.1／R7／R8 继续；见[精灵合同](../../scripting-sprite-overlay-contract.md)／ADR-0124、roadmap §18.91。
最新接续：0.27.0 已接通 R6 封闭混合图元、独立线段／带状拖尾及真实 VectorTrail 包；Custom 关闭自动绘制，保留全部模拟及四类预算，由纯视觉求值输出图元。工程实例／批次顺序、名字、层、准备帧复制及自身下层门禁统一。Bridge、图片精灵、目标内捕获和完整 R6 继续；见 [`docs/scripting-vector-overlay-contract.md`](../../scripting-vector-overlay-contract.md)／ADR-0123、roadmap §18.90。
最新接续：0.26.3 在 roadmap §18.89 收口本计划第 3 批 R5 自动出口：普通／透明两组参数和源码独立撤销／重做、显式单实例接受及旧修订保持、热替换四个加载上下文回收；真实 MP4／ProRes4444 在第 39 帧取消后保持旧输出、清理临时文件并释放帧／会话／上下文，新 token 重导出成功。下一步 R6，完整 SDK、R3.1、R7／R8 与人工验收继续。见 [`docs/scripting-session-revision-acceptance.md`](../acceptance/scripting-session-revision-acceptance.md)／ADR-0122。
最新接续：0.26.1 补齐 RepeatEmphasis 完整包及持久组合证据；0.26.2 补齐预览工厂／快照／解析目标／透明／质量／布局输入身份，输出改变后清除旧失败，迟到的取消工作不再发布像素或错误；保存重开的双文字／真实音频在24／30／60 FPS下切换普通、透明、密度、质量并与独立会话同帧，两个文字包上下文关闭后回收。新增预览定向回归，完整 R5～R8 未关闭。见 [`docs/scripting-preview-input-contract.md`](../../scripting-preview-input-contract.md)／ADR-0121、roadmap §18.88。
最新接续：0.26.0 R5 已补齐七阶段及原生绘制／释放的实际控制器包／修订／导出身份，Error 与音频门禁保留来源；准备帧作者 Info／Warning 进入普通文字及合成像素帧、缓存和导出报告，不改变像素。当批新增16项，会话定向43项通过；其后组合验收已接续至上文0.26.1。合同见 [`docs/scripting-runtime-diagnostics-contract.md`](../../scripting-runtime-diagnostics-contract.md)／ADR-0119、roadmap §18.86。
最新接续：0.24.0已完成U2作者源码接续（§18.76）：跨文件／跨包草稿保留、固定修订独立编译和行列定位、显式单实例升级保留入口／参数、失败源码保存与新建实例禁用；保存与运行验证分开。0.23.0中间构建包闭包接续（§18.75）支持目录／.mkfx导入、重复修订拒绝、草稿登记和原子分享；0.22.0 U2基础（§18.74）已接通包／实例分栏、实例新增／复制／删除／顺序、绑定／订阅／seed／参数编辑、保存前工程校验与正式会话验证，一次撤销提交；0.21.0 U1材质工坊接续（§18.73）与0.20.0 U0异步运行装配（§18.72）已保留。U0运行组合根已完成；直接图／跨输出执行计划、组／父捕获、模板与编排入口、宽窄工作区及完整R7继续。0.18.0已接入源MaterialSpec唯一真值、完整工程／模板材质闭包及静态编辑保护（§18.70）；0.17.0已接入模板配方／独立资源闭包、幂等套用与真实图元层（§18.69），完整材质源／工作区继续；0.16.0已接通完整媒体／分析内容地址、独立保存／备份和固定音频运行投影（§18.68），模板资源及工作区继续；0.15.0已实现工程效果聚合、包闭包发布、实例历史及真实控制器投影（§18.67）；0.14.0已实现歌词内容地址与入口最后发布（§18.66）；0.13.3逐节点材质uniform已接入主Render（§18.65）；0.13.1包材质已进入真实准备帧／主Render及成片（§18.63）；0.12.3 已补独立前景／实际擦除的CPU产物，尚未开放组图／父捕获，三项边界待答；0.12.2 已接通组 alpha writer、嵌套主 Render 隔离；0.12.1 已将固定包图接到主 Render 的最高层部件协调、元素到整行和真实下层；融合局部切分拒绝、无目标输入全替换、透明旁路／缓存／导出提示已接通。组图／部件化父捕获、自身后置粒子排除、区域材质和持久工作区继续原路线，见 [主画布合同](../../scripting-main-canvas-composition-contract.md)／ADR-0093／roadmap §18.59。

组级仿射入口：[真实组仿射合同](../../scripting-group-affine-contract.md)。用户确认仅工程／显式节点绑定可写组；0.11.1 接通真实运行父树、槽位／歌词／注音／前奏、最终播放头／捕获和粒子出生。0.11.2 已修复真实控制器包创建实例时漏掉 group 仿射的旧门禁，真实包参数／权限／像素和 MP4／Alpha 同帧已验证；0.12.2 已接通组透明度与主画布隔离，组图仍需接续，最新证据见 roadmap §18.60。

**0.25.7 本计划 R4 持久闭包自动验收**：工程、另存为、备份、模板分享、固定 DLL 和失败源码工作区已组合验证。包修订与 C# Artifact 各自固定身份，资源改变可复用 DLL 但必须消费当前资源并通过原生门禁；失败不覆写入口、缓存代次或旧输出。组图父捕获、直接图／材质实例、复杂映射、跨输出及完整工作区继续原合同，不作为完整 R4／R7 已交付。下一步 R5 会话；验收表见 [`docs/scripting-persistent-closure-acceptance.md`](../acceptance/scripting-persistent-closure-acceptance.md)／ADR-0118、roadmap §18.85。

## 1. 先读什么

此前脚本目标状态：**该轮goal已完成**。后续计划第1～6批限定范围与R7／R8自动出口已收口，当批0.40.2、3187项全量通过。2026-10-06用户确认文字脚本、粒子脚本与资源效果均无问题，并明确接受该目标完成；总体记录见[R8验收记录](../acceptance/scripting-r8-acceptance.md)／roadmap §18.108／ADR-0141。此记录不代表当前三工作区重构完成；本轮开发入口见下表与§5。未实现的完整SDK、R3.1等高级能力继续保持门禁；下列版本记录保留当批历史状态。

连续粒子实施入口：[固定时间连续粒子合同](../../scripting-continuous-particles-contract.md)。用户确认每条合法播放头分别按 rate 发射；实现前明确身份／精确配额／纯出生几何／拖尾及预算。0.9.23 已接通 Jump 与 SemanticCursor；0.10.0 接通作者查询，0.11.0 接通事件 Burst。桥接、目标捕获及完整 R6 仍后续实施。

作者粒子查询与独立阶段见 [发射阶段实施合同](../../scripting-emission-stage-proposal.md)：用户已确认并在0.10.0实施 EvaluateEmitters／EmitterWriter、完整 Particles／EmissionSchedule及全实例每区间100000条记录预算；旧 VisualWriter.Emitters已删除。实际 CursorTrail 包消费／分享验证通过，证据见 roadmap §18.53。

事件 Burst 规则已获确认，实施见 [事件 Burst 合同](../../scripting-event-burst-contract.md)：每锚点独立 count，同事件同发射器最后登记，指定锚点缺失阻止导出，同刻 Burst 在前；四类真实来源不伪造轨道。0.11.0 已实施并自动验证，见 roadmap §18.54；完整 R6 仍未交付。

| 顺序 | 文件 | 为什么 |
|---|---|---|
| 下一步 UI 开发 | [三工作区职责重构与预设制作流程](../legacy-ui/ui-workspace-ownership-plan.md) + ADR-0145 | **已批准施工依据**：歌词与时间只管内容和时间，模板制作负责全部样式，画面与样式负责应用与布局；脚本独立窗口、高级编排区内切换。按方案§5顺序实施，已验证进度见roadmap §18.114起 |
| 1 | `docs/roadmap.md` | **任务号**（`CORE-xx`/`EDITOR-xx`/…）、阶段出口判据、DoD、提交约定、**§18 完成度盘点（权威状态）** |
| 2 | `docs/development.md` | 功能分级 P0/P1/P2、MVP 验收 14 条、时间模型、数据格式、渲染架构 |
| 3 | `docs/decisions.md` | **ADR：所有已固化的决策与偏差，冲突时以此为准**。含 ADR-0030：状态只维护在 `roadmap.md` §18，本文件 §5 只写"怎么动手" |
| 4 | `docs/timeline-ui.md` | 时间轴/歌词栏的界面规格与对齐不变式 |
| 5 | 本文件 §5 | 还没做完的清单（含证据，省去重新盘点） |
| 新架构 | `docs/temporary-style-line-editor-optimization.md` + `docs/decisions.md` ADR-0050/0051 | 0.2.0 模板／行编辑职责见 §11；0.2.1 模板工坊修订见 §12；0.2.2 渲染修复见 §13；0.2.3 清除时间见 §14；0.2.4 选中同步见 §15 |
| 样式与布局收尾 | `docs/style-animation-system-plan.md` §13～16 + ADR-0052～0056 | 0.2.7 描边与 0.2.8 行位置已验收；翻译、注音、分支切换及双行／非双行布局已确认；0.3.1 双行槽位接管位置、逐行布局禁用已实现；0.3.2 分支级双行整体变换已实现并通过用户验收 |
| 当前改版 | `docs/template-application-ux-plan.md` | 0.2.5～0.2.6 歌词行编辑器体验及实施证据：顶部波形聚焦当前行；下方「文字与时间／样式选择」两页签；样式页左侧窄模板列表、右侧套用与读取／恢复入口；指定实窗操作已验收 |
| 统一样式模型 | `docs/style-data-model-unification-plan.md` + ADR-0057 | **0.4.0 已实施**：规范字体、填充、描边、阴影和覆盖数据合同已贯通 Core、持久化、模板与渲染；工程／歌词／模板 schema 为 v16／v13／v2，严格拒绝旧平面键，不追加旧格式迁移 |
| 脚本系统架构 | `docs/scripting-system-architecture-plan.md` + ADR-0059～0061／0068 | **原脚本运行边界已收口；当前实施按 R0 合同／R1 报告**：主进程内串行、时间线历史、节奏／低频、失败阻止导出、参数撤销和持久 DLL；仅调制样式为前轮范围，本轮视觉缩放／粒子扩展及平台选择见下一项 |
| 脚本效果 SDK 与打包 | `docs/scripting-effects-sdk-package-plan.md` + ADR-0068 | **三个效果的初稿，未实施**：C#＋Roslyn 与视觉缩放、注音、起唱计数及跨行粒子行为已确认；源码包／工程副本／本机 DLL、只读投影、事件／发射／拖尾及 UI 为拟议合同。固定 writer／单入口阶段二选一须按下项高阶提案修订，三个效果验收目标保留 |
| 高阶材质与整行效果 | `docs/advanced-material-render-graph-plan.md` + ADR-0069 | **最新评审提案，未实施**：玻璃来源两种选择已确认；区域材质、自定义 SkSL、行／组捕获、多遍渲染图、同一实例多阶段输出与锚点映射待审阅。生产歌词尚未连接自定义 shader，正式后端已有严格 shader 原语和独立 CPU 图执行（0.9.13～0.9.14）；透明模式跳过背景依赖效果、提示和包内基础外观已确认（ADR-0070），具体绕过合同已获 R0 接受，生产集成按下一项 |
| 脚本具体落地路线 | `docs/scripting-system-implementation-roadmap.md` + ADR-0070 | **最新施工入口，R0／R1 已完成，R2 实施中，R3～R8 未完成**：R0 合同／UI→R1 C#／Skia 验证→R2 SDK／帧→R3 材质／图→R4 包与数据断代→R5 会话／callback→R6 粒子→R7 UI／模板→R8 成片。§2 能力、§3 透明跳过、§8 三条格式／资源／API／导出破坏性变更；具体数字和协议按已接受合同实施 |
| 脚本系统后续实施计划 | `docs/scripting-system-next-work-plan.md` | **0.40.2本轮goal已完成**：限定开发／自动出口已收口，文字／粒子／资源效果用户总体验收通过；原路线完整SDK／R3.1等高级能力继续保持门禁 |
| 脚本实施合同／公开 SDK | `docs/scripting-system-implementation-contract.md` + `docs/scripting-sdk-contract.md` + ADR-0071 | **R0 已获接受，R1 已通过技术出口**：两程序集、绑定／材质／图、120 Hz／预算／样例参数、包修订／歌词内容地址／模板资源、历史／删除／保存及宽窄 UI；旧提案冲突按该合同。完整 SDK／会话／效果 UI 仍未实现 |
| 脚本技术验证 | `docs/scripting-r1-capability-report.md` | **0.9.1 R1 已实施**：最小 TokenId SDK、Roslyn／分发引用、DLL／PDB 原子缓存、新进程复用及可回收代次；真实 CPU 文字／描边 shader、行 glitch、两种玻璃输入及 MP4／Alpha 同帧。不是完整脚本工作区，下一步 R2 |
| 脚本 SDK／共享几何实施 | `docs/scripting-r2-sdk-geometry-progress.md` | **0.9.11 R2 基础、六阶段／几何查询、场景目标读取、标记／整行仿射及生产圆点已实施**：源／场景目录、固定字形、完整源进度、准备帧、生产 Token／Ruby 缩放及九类参数／资源／随机／音频输入；封闭事件／订阅／历史已实施；共享回调／推进／诊断和最终播放头／基础查询／独立端点／typed 映射值已实施；行／组／标记基线、真实帧对象／几何和生产标记 Style／仿射、整行仿射及运行父树传播已接通；六阶段和生产圆点已接通；整行 Style／组透明度等其余 writer、实际映射及桥接／完整粒子绘制仍待实施；作者完整查询及 Burst 已接通；服务合同见 docs/scripting-r2-frame-services-contract.md，事件记录见 docs/scripting-r2-timeline-protocol.md |
| 脚本基线与生产样式帧 | `docs/scripting-r2-style-frame-contract.md` | **0.9.6 已实施**：固定完整元素基线、两态材质／描边／阴影／透明度、Set 顺序及 alpha 合并、注音共同外观；静态覆盖与脚本共用固定单元。R3 包材质／图及其余 R2 目标仍未完成 |
| 脚本共享语义会话与诊断 | `docs/scripting-r2-semantic-session-contract.md` + ADR-0072 | **0.9.7 已实施**：同一实例共享回调／整数 120 Hz 推进／求值，完整前缀历史、重放／取消清理、真实 RepeatEmphasis；作者 Error 阻止导出，Warning／Info 仅记录；中途编码临时发布保护原输出。0.9.11 已扩展视觉阶段；完整 R5／UI 未交付 |
| 脚本播放头与几何查询 | `docs/scripting-r2-playback-geometry-contract.md` | **0.9.8 已实施**：真实 Current／整行依据、最终矩阵播放头、限阶段基本查询、独立 AnchorQuery 重放、仿射／波形值；0.29.0 已依 ADR-0125 接通真实转换事件及按实际绑定筛选的后继／独立端点；0.30.0 Bridge 发射已接通，实际 MappedWarp 未完成 |
| 脚本行／组／文字标记目标 | `docs/scripting-r2-scene-target-contract.md` + ADR-0073 | **0.9.9 已实施读取及标记生产输出**：真实行基线、组仅静态 alpha、完整标记基线；真实帧／运行父树／参考框／墨迹；标记两态外观和仿射通过真实像素对照。整行仿射后续见下一项，整行 Style／组其余 writer 和组 capture alpha 仍按 R2～R3 |
| 脚本整行与层次输出 | `docs/scripting-r2-hierarchy-output-contract.md` + ADR-0074 | **0.9.10 已实施整行仿射及运行父树传播**：注音／附着前奏跟随，固定提示按自身父关系；最终行／族／标记／组几何及播放头共用矩阵，原生 float 退化明确失败。整行样式覆盖和组输出范围仍待答复 |
| 脚本视觉阶段与圆点 | `docs/scripting-r2-visual-stages-contract.md` + ADR-0075 | **0.9.11 已实施六阶段与真实圆点输出**：最终几何／播放头、前后历史、120 Hz＋事件、分数帧不积分、AnchorQuery 跳过视觉；真实像素／编码失败及全实例预算验证通过。Emitter／粒子／拖尾仍按 R6，图按 R3，工作区按 R7 |
| 脚本基础音频分析 | `docs/scripting-audio-analysis-contract.md` + ADR-0076 | **0.9.12 已实施 R5 独立音频分支**：实际流式解码／48 kHz／20 ms、20～200 Hz 近似低频、Pulse／近似节奏／confidence、内容修订、失败／取消及初始化 required 门禁；真实脚本／像素／重放验证。音轨外 required 待答；分析持久闭包及 UI 后续实施 |
| 固定 shader 与帧 uniform | `docs/scripting-shader-program-contract.md` + ADR-0077 | **0.9.13 已实现 R2／R3 基础原语**：Core 固定声明、SDK 七类值及数组、正式 Skia 原生反射／编译与真实 CPU 像素；类型／编码／预算／失败／取消／释放已验证。Material／Graph writer、包解析、生产歌词集成及完整 R3 资源释放仍待实施 |
| 中立图与 CPU 图执行 | `docs/scripting-image-graph-contract.md` + ADR-0078 | **0.9.14 已实现独立图后端**：Core 不可变定义／拓扑／端口／参数约束，shader／Blur／Mask／Transform／Blend、单次图预算／及时释放、替换合成／透明旁路、真实像素与 MP4／Alpha 同帧。Canvas 以外适配、生产 capture／writer、全局资源账本和错误附加实例仍待接入；optional 缺失策略已询问，答复前明确失败 |
| 生产捕获与图绑定 | `docs/scripting-production-capture-contract.md` + ADR-0079 | **0.9.15 已实施捕获／绑定／执行适配**：真实 Line／TokenFamily／Mark／Group、五类通道及最终矩阵；同实例同目标最后一次 Bind，跨实例顺序闭包；正式图执行附加真实实例失败，真实歌词 MP4／Alpha 同帧及中途编码失败验证。Render 主画布调度仍待接通，遇到图绑定明确失败；背景／下层输入、组写权、全局账本、材质／包／UI 尚未完成 |
| 中间表面共享预算 | `docs/scripting-surface-budget-contract.md` + ADR-0080 | **0.9.16 已实施账本基础**：每帧活动／会话字节、产物生命周期、输入别名与跨帧去重、图峰值前置及 Blur／捕获显式图层；真实 200 ms 预算错误中止编码并回收。父子主画布调度／媒体及完整 native scratch 仍未完成 |
| 场景图合成 | `docs/scripting-scene-composition-contract.md` + ADR-0081 | **0.9.17 实施合成链与背景输入**：后续普通效果保留历史替换范围、透明旁路、真实媒体／底色和绑定级失败；未纳入注音／描边／阴影保留的行为已确认，其正式分区／父子 Render 调度／下层快照尚待实施 |
| 真实彩色部件与重组 | `docs/scripting-color-parts-contract.md` + ADR-0082 | **0.9.18 已实施**：基础字／注音填充、外描边、阴影六部件，实际颜色／固定字形／共同 alpha 分离及显式顺序重组；最高层叠放已获确认，正式效果自动重组与父子 Render 调度未完成 |
| 效果包内容闭包 | `docs/scripting-package-content-contract.md` + ADR-0083 | **0.9.19 已实施**：规范清单／严格哈希／不可变资源、目录／ZIP 同闭包、原子分享和真实离线 C# 消费；参数／控制器接续见下一行，材质／图接续见包图合同 §10；工程持久事务和 UI 未完成，内容类型不直接启用 |
| 效果包参数与控制器 | `docs/scripting-package-semantics-contract.md` + ADR-0084 | **0.9.20 已实施**：九类参数及覆盖、控制器／绑定／输出依赖、真实 DLL 入口与图片解码、包展开预算；清单到生产帧／实际像素及声明外输出门禁通过。统一材质／图和显式旁路接续见包图合同 §10；区域材质、跨输出执行、工程事务和工作区仍未完成 |
| 包内 shader 程序与图编码接续 | `docs/scripting-package-graph-semantics-contract.md` + ADR-0085／0086／0092 | **0.9.21 A／0.9.22 B 已实施**：固定程序、严格包图／逐输入配方、真实 CPU／图片／透明旁路；0.12.0 C 统一语义／真实资源目录与回退门禁已实施，生产区域材质／主画布及完整包事务尚未交付 |
| 主画布图与部件来源 | `docs/scripting-main-canvas-composition-contract.md` + ADR-0093 | **0.12.1 元素／整行／标记主 Render 已实施**：最高层叠放、每输入实际配方、融合局部切分拒绝、无目标输入全替换、元素到整行及真实下层、共同 alpha 和旁路报告／缓存；组隔离／自身粒子排除及区域材质仍按 R3 |
| 组透明度与嵌套隔离 | `docs/scripting-group-opacity-isolation-contract.md` + ADR-0094 | **0.12.2 已实施 writer／真实包／主 Render**：明确写权、最终因子、父子一次 alpha、实际孩子图与下层、顺序诊断／缓存／导出、共享预算；组图及配方读取产物继续 R3 |
| 父级捕获合成产物 | `docs/scripting-parent-composition-contract.md` + ADR-0095 | **0.12.3 已实施独立CPU接缝**：前景／擦除分离、孩子顺序合成、真实下层及共同alpha、资源预算／生命周期；三项父边界待答，尚未接入组图 |
| 工程效果闭包与实例 | `docs/scripting-project-effects-contract.md` + ADR-0101 | **0.15.0已实施并自动验证**：固定源码／资源与顺序实例持久化、独立备份／另存为、历史与失败门禁、真实离线编译／帧及成片；完整媒体／分析／模板事务与工作区继续 |
| 模板配方与资源闭包 | `docs/scripting-template-recipes-contract.md` + ADR-0103 | **0.17.0已实施**：新库／.mktpl全包闭包、相对配方、实例来源与幂等套用／一次历史、实际图元层；源材质与工作区继续 |
| 源材质唯一真值与实际运行 | `docs/scripting-source-material-contract.md` + ADR-0104～0105 | **0.18.0 M1／0.19.0 M2已实施**：solid／gradient／packageMaterial整体覆盖、26／16／5当前格式、完整闭包／固定参数、真实来源与区域Render／成片；M3自动组合根、材质选择／参数和工坊实时预览继续 |
| 工作区运行装配 | `docs/scripting-runtime-composition-contract.md` | **0.24.0 U2接续已实施**：中立异步像素会话、组合根固定native资源／本机DLL缓存、后台预览合并／错误状态和独立导出；模板工坊三材质槽位、包参数、目录／.mkfx导入和正式实时试样；脚本效果工作区管理工程实例 CRUD／顺序／绑定／参数，支持包闭包导入／分享、跨选择源码草稿、新修订独立编译／诊断、单实例接受、失败源码保存；正式工程验证另行执行。直接图／跨输出、父捕获及其余R2～R8保持后续 |
| 作者源码与接受修订 | `docs/scripting-workspace-source-editing-contract.md` + ADR-0110 | **0.24.0已实施**：跨选择草稿、多文件新修订、独立完整包编译、诊断行列定位、单实例升级保留参数、失败源码可保存；新建包／文件、多引用升级与完整宽窄工作区后续 |
| 工程媒体与音频分析 | `docs/scripting-media-analysis-storage-contract.md` + ADR-0102 | **0.16.0已实施**：完整流式媒体与规范分析内容地址、独立备份／另存为、原名称及失败保护、固定分析投影／真实hop历史；模板资源、自动分析与工作区继续 |
| 工程入口与歌词修订 | `docs/scripting-project-revision-storage-contract.md` + ADR-0100 | **0.14.0实施并自动验证**：先发布不可变歌词修订，最后发布唯一入口；严格读取、独立备份／另存为及失败saved引用保护；完整媒体／效果包／分析事务继续 |
| 区域材质覆盖与生产接入 | `docs/scripting-region-material-contract.md` + ADR-0096 | **0.13.0 已实施严格编码、真实区域／基础外观捕获和包材质执行原语**；包 profile2，不转换旧清单。0.13.1已接通准备帧与主Render，工程持久事务及完整R3／R4继续 |
| 当前架构与统一场景元素 | `docs/current-architecture.md` + `docs/unified-scene-element-plan.md` | **0.8.0 通用树及编辑器已实现**：当前盘点记录 nodes／clips／localTransform、真实父子矩阵及草稿历史；统一场景方案保留 0.6.0 历史，最新合同按 ADR-0065 |
| 容器编排复盘 | `docs/scene-container-composition-review.md` | **0.6.0 历史调研**：固定分支树的局限促成 ADR-0063；0.7.0 已实施通用树与独占案例，0.8.0 后续通用编辑交互见实施合同 |
| 独占显示案例与原生容器树 | `docs/lyric-exclusive-display-case-plan.md` + ADR-0063 | **0.7.0 已实施，双行显示与整体编辑已确认**：独立节点树、显示程序及源行覆盖；双行—独立居中—恢复双行案例，树／片段／槽位的新编辑 UI 后置 |
| 通用容器编辑器 | `docs/scene-container-editor-plan.md` + `docs/scene-editor-implementation-contract.md` + ADR-0065 | **0.8.0 G1～G4 已实施，基本编排流程获人工确认**：原生 nodes／clips／localTransform、通用树／任意槽位／片段编辑及单句微调；实施合同 §8 记录自动测试、真实控件探针和验收路线，G0 UI 稿保留历史 |
| 编排体验优化 | `docs/scene-editor-ux-optimization-plan.md` + ADR-0066 | **0.8.1 已实施并获人工验收通过**：按钮式入口、容器／槽位角色、直接布局方式、树菜单／拖放及片段区分层；槽位树中拖动改变轮换顺序，证据及验收记录见 §13 |
| 卡拉 OK 前奏 | `docs/karaoke-lead-in-plan.md` + ADR-0067 | **0.9.0 L1～L4 已实施并获人工验收通过**：开头及长间奏后生成提示，归零对齐首个定时元素／行起点，提前显示目标句并只入场一次；支持跟随目标句／固定父组。用户确认功能全部正常、前奏是真实容器且绑定目标句；自动证据含模型、像素、MP4、草稿／历史及真实控件，见 §13 |
| 按需 | `docs/style-system-redesign.md` | **样式工作区交互设计**：内置／我的／草稿／工程结果的边界、编辑与应用流程；第一轮已实施 |
| 按需 | `docs/color-picker-interaction-plan.md` | **取色器交互与实现状态**：默认常用色板、高级 `ColorView`、RGB/HSV/通道约束与验收 |
| 按需 | 本文件 §10 | 样式设计独立面板与批量应用的历史方案；实施与取舍以 ADR-0044 为准 |

`development.md §16` 与 `roadmap.md` 的**阶段编号不一致**（前者 7 个阶段、后者 8 个），
引用时请指明是哪一份文档的编号。

---

## 2. 环境与命令

```bash
# 全量构建（必须用这个；改过库之后尤其要用）
dotnet build MakoKaraGenerator.slnx

# 全部测试
dotnet test MakoKaraGenerator.slnx

# 启动编辑器（后台，日志落 /tmp）
setsid -f ./samples/TechnicalSpike.App/bin/Debug/net10.0/MakoKaraGenerator.Spike.App \
  > /tmp/makokara.log 2>&1 < /dev/null
```

本仓库的工作约定：每次开发完成并通过构建／测试后，直接按上面的命令启动当前构建，
把进程 PID 和日志中的异常情况告知用户，方便立即验收；除非用户明确要求不要启动。

| 项 | 值 |
|---|---|
| .NET SDK | `10.0.112`（`global.json` 锁定，`rollForward: latestFeature`） |
| 目标框架 | `net10.0`，`TreatWarningsAsErrors=true`，`Nullable=enable` |
| 平台 | CachyOS / niri 26.04 Wayland；显示器 2880×1800 物理、**逻辑 1600×1000（scale 1.8）** |
| FFmpeg / FFprobe | n9.0.2，系统安装（`PATH` 中） |
| 字体 | `Noto Sans CJK SC`、`Noto Color Emoji`（整形测试依赖，见 §7） |
| 代理 | FlClash `127.0.0.1:7897`；**GitHub 本机直连可用**（`gh auth login` 挂代理反而会失败） |

### 2.1 会踩的命令坑

- ⚠️ **`dotnet test` 只构建测试项目及其依赖，不重建应用项目**。改完库直接启动应用会跑旧副本，
  曾因此误判"bug 修好了但应用里还是老样子"。**改库之后先 `dotnet build MakoKaraGenerator.slnx`。**
- 杀进程与启动**分两次命令**；遍历 `/proc/*/cmdline` 匹配完整可执行路径后 `SIGTERM`，
  并排除自己的父 shell 链。**绝不要按名字 `pkill`**。
- 本机**没有** `ydotool`/`wtype`/`xdotool` → **无法注入键鼠输入**，交互验证手法见 §6。

---

## 3. 硬约束（违反不会报错，只会静默出错）

### 3.1 架构与依赖

- **依赖方向 `App → Application → Core`**（ADR-0025）。`Core`/`Application`/`Timeline`/`Media`/
  `Rendering.Abstractions`/`Text` **不得引用 Avalonia**。
- 界面层**不得出现** `ffmpeg`/`ffprobe` 字符串，**不得出现任何播放器名字**（只认 `IAudioPlayback`）。
- **渲染器不得修改项目源数据**；渲染只读 `ProjectSnapshot`。
- **预览与最终导出必须共享同一套时间采样与渲染规则**（`LyricSceneSampler` 统一分支可见区间与动态场景，逐字时间只由 `LyricSampler` 解释）。
- `Timeline`/`Media`/`Rendering` 项目**必须显式引用** `SkiaSharp.NativeAssets.Linux` /
  `HarfBuzzSharp.NativeAssets.Linux`（ADR-0007）。

### 3.2 数据与时间

- 内部时间统一 **`long` 整数微秒**，区间是**半开区间 `[startUs, endUs)`**。
- **禁止静默修正用户数据**（`development.md §19` 设计底线）——校验失败只报告、不改值。
- **不可变模型 + 引用比较**：任何编辑函数在"没有实际变化"时**必须返回原实例**，
  否则"有未保存改动"的判据（引用比较，ADR-0025）会把刚打开的工程标成已修改。
  这条已经踩过两次（预设应用、音频恢复）。
- `project.json` 与它通过 `lyricsFile` 指向的 `data/lyrics/<hash>.json` 是**两份独立文件、各有自己的 schema 版本线**（ADR-0029／0100）；0.14.0先写不可变歌词、最后发布根入口，不再读取根lyrics.json。历史代码的迁移框架仍在。按 ADR-0050，0.2.0 起改 JSON 结构需提升对应 schema 并测试当前格式，**不为旧工程追加自动迁移步骤**；打开时先检查 `project.json.applicationVersion`，版本缺失或不一致提示并拒绝加载。
- 程序版本的单一来源是根目录 `Directory.Build.props` 的 `VersionPrefix`；合同版本的冻结来源是 `web/contract-manifest.json` 与 `web/openapi.json`，两者必须随合同变更同步。每次完成并交付程序修改都递增 `MAJOR.MINOR.PATCH`（普通修复 PATCH、1.0 前不兼容 MINOR）；程序集、关于界面、日志、工程文件必须一致。本批交付版本为 `0.94.0`（生产源码 `c50516d`，已推送）；合同 `1.24.0` 的最终冻结和G20-G24整体验收见整体计划第6节、实施§57及本文顶栏。G20-B的0.93.0/1.23.0证据保留节点创建计划。上一批 `0.92.0`／合同 `1.22.0` 的G20-G24基础证据保留原计划归属。`0.86.0`为公共静态预设预览（§43），`0.85.0`为工程显式预设分配（§42），`0.84.0`为独立模板库（§41），`0.83.0`为静态样式底层API（§40），其余历史版本见实施记录。纯文档或回归验收批次不占生产版本。
- ⚠️ 测试里禁止硬编码 `"schemaVersion": 2` 这类版本号（版本一升会**静默失效**，
  替换不中但文件仍合法，测试会走进别的分支）；用 `Project.CurrentSchemaVersion`。

### 3.3 代码规范

- `src/` 下**公共成员必须有 XML 注释**（CS1591 视为错误）；
  `samples/TechnicalSpike.App` 不受此约束。
- 提交必须**编译通过且 0 警告**。文案**只来自** `Resources/Strings.axaml`，
  颜色**只来自** `Theme/Colors.axaml`（ADR-0001）。
- 界面文案是中文（带"喵"的语气词，例：「还没有导入音频喵」）。
- 用 `Loc.Format(key, args)`，**不要** `string.Format(Loc.Get(key), …)`（`CA1863` 在此项目是错误）。
- **当前 GUI 外观采用原生 Suki**（ADR-0142～0148）：`SukiTheme` 提供控件模板与状态，
  `Theme/EditorStyles.axaml` 管编辑器密度／专业布局及固定版本Flyout外壳修复，
  `Theme/Colors.axaml` 管语义别名；`Theme/UiSukiTheme.cs` 的 `Apply(Color seed)` 更新应用级强调色。
  主题只写应用级 `theme.json`，不修改工程或视频外观；Semi／Material旧取向已被取代。

### 3.4 版本锁定（不要顺手升级）

- `Avalonia` **12.1.3**（`Avalonia.Wayland` 同版本，**HiDPI 唯一解**，ADR-0017）
- `SkiaSharp` / `SkiaSharp.HarfBuzz` **3.119.4**（**绝不用 4.x**）、`HarfBuzzSharp` 8.3.1.5
- `SukiUI` **7.0.1**、`Avalonia.Themes.Simple` **12.1.3**；`SoundFlow` 1.4.1、`CliWrap` 3.10.5

### 3.5 图形

- **Skia 自定义绘制必须先 `canvas.ClipRect` 再 `Clear`**，否则会把整窗口擦掉（ADR-0015）。
- 绘制文本使用 **HarfBuzz 整行结果的 `DrawShapedText` 缓存字形入口**（ADR-0071／R2），不按子串重整形、不使用 `DrawText(string)` 猜字形；最终 positioned blob 绘制与上游流程一致。字体回退**不能**用 `SKTypeface.ContainsGlyphs`（ADR-0010）。
- `SKShaper.Result` 的 `Codepoints` 是**字形 ID**，`Clusters` 是 **UTF-8 字节偏移**（ADR-0008）——
  不是 UTF-16 下标。
- 一次整形整行、再按进度换色，是"高亮不跳动"的前提。

---

## 4. 代码地图

| 路径 | 职责 | 不引用 Avalonia |
|---|---|---|
| `src/MakoKaraGenerator.Core` | 领域模型（`Project`/`Lyrics`/`Composition`）、校验器、JSON 持久化与 schema 迁移、样式解析与编辑、命令接口 | ✅ |
| `src/MakoKaraGenerator.Application` | 用例层：`ProjectWorkspace`（工程生命周期 + 脏判据）、`ProjectHistory`（撤销/重做栈）、`ProjectPackage` | ✅ |
| `src/MakoKaraGenerator.Timeline` | 时间↔像素唯一映射（`TimeAxis`）、轨道布局、吸附、命中测试 | ✅ |
| `src/MakoKaraGenerator.Media` | FFprobe 探测（音频与全流）、FFmpeg PCM 流式解码、波形峰值金字塔、`IAudioPlayback` 适配 | ✅ |
| `src/MakoKaraGenerator.Export` | 视频导出：`IMediaEncoder` + CliWrap 适配、质量预设、进度/取消、产物校验与报告 | ✅ |
| `src/MakoKaraGenerator.Text` | `ITextLayoutEngine` 抽象 | ✅ |
| `src/MakoKaraGenerator.Rendering.Abstractions` | `IRenderPipeline`、`ProjectSnapshot`、`RenderFrame`（裸 BGRA8888 预乘）、`LyricSampler` | ✅ |
| `src/MakoKaraGenerator.Scripting.Abstractions` | 中立公开 SDK；已有身份、目录／进度、有限几何、Token 族、行／组／标记基线、帧及固定服务／事件／订阅／历史；完整上下文／writer／生命周期按 R2～R6 | ✅ |
| `src/MakoKaraGenerator.Scripting` | 离线编译／诊断、DLL／PDB 缓存／可回收加载、目录投影、独立纯帧／共享语义会话、真实场景目标／标记输出、重放与作者诊断；完整视觉阶段／粒子后续接入 | ✅ |
| `src/MakoKaraGenerator.Rendering.Skia` | Skia 管线实现、文本布局引擎 | ❌（用 SkiaSharp） |
| `samples/TechnicalSpike.App` | **编辑器本体**（Avalonia 窗口 + `EditorViewModel` + 自绘控件） | — |
| `samples/TechnicalSpike` | 早期整形/离屏渲染验证 | — |

`EditorViewModel` 按关注点拆成 21 个 partial 文件；本批主要入口为 `.SceneEditing.cs`（稳定选择、展示草稿与操作）、`.SceneLayout.cs`（统一进入与原生布局方式）、`.Placement.cs`（布局模式／快照）、`.Commands.cs`（统一历史与源编辑校验）、`.State.cs`（替换及派生刷新）。`SceneTreeInteraction.cs` 解释拖放目标并复用 Core 校验。`MainWindow` 拆成 15 个 partial；`.Placement.cs` 挂载通用工作区，`SceneEditorControl` 按主工作区、Navigation、Tree、SlotDrag、Inspector、Dialogs 拆分。

界面主题入口：`samples/TechnicalSpike.App/App.axaml` → `SukiTheme` + `Theme/EditorStyles.axaml` +
`Theme/Colors.axaml`；强调色的运行时接口是 `Theme/UiSukiTheme.cs`。
界面种子色只写应用级 `theme.json`，不要把界面颜色写进 `project.json` / `lyrics.json`，
也不要让视频渲染读取界面色阶。

- ⚠️ **`EditorViewModel.UpdateProject` 只有一个调用点**（`AdoptProject`）。这是"所有改动都进撤销栈"
  的不变量，别在别处直接换工程实例。**撤销之后派生视图的刷新也收在 `AdoptProject`**（ADR-0027）。
- ⚠️ 命名空间 `MakoKaraGenerator.Application` **遮蔽了 Avalonia 的 `Application`**，
  所以 `App.axaml.cs` 里写 `Avalonia.Application`（**不要**起别名）。

---

## 5. 未完成的工作（行动视角）

> **状态的权威来源是 `docs/roadmap.md` §18「完成度盘点」**
> （阶段出口达成情况、MVP 验收 14 条对照、全 68 个任务号的状态）。
> 本节**不重复**那些表格，只补代理动手时需要的东西：**每项要碰哪里、验收判据是什么、有哪些坑**。
> 两份内容不一致时，以 `roadmap.md` §18 为准。

### 下一步已批准开发：三工作区职责重构

完整合同见 [docs/ui-workspace-ownership-plan.md](../legacy-ui/ui-workspace-ownership-plan.md)，交互原型见 [三工作区原型](../../design/ui-workspace-ownership-prototype.html)。用户已接受方案；第1步原型与集中控件规格已验证，后续按合同§5推进，不把原型当作生产界面。

**接下来执行方案§10第6项人工出口**：精确布局、独立模板作者流程、歌词职责清理和具名面板已接通；0.43.6～0.43.9补齐多选、具体继承来源、具名输入、迟到音频保护、独立脚本正式运行验证及歌词选择／明确导入。弹层保持打开调整视口的有限序列已验证，当前构建完整3358项全绿。继续收集用户连续制作、物理输入法和缩放反馈；不要重复运行已通过门禁或操作手测窗口，出现实际问题再修复并验证。编译、配置保存与运行验证继续分开，不读取当前工程作为工坊试样，不增加SDK能力。

1. **先完成数据与闭包**：组合预设、每对象一个 `PresetApplication`、共享脚本配置库、统一运行投影及保存。✅ 已完成并由roadmap §18.115～18.117记录；继续验证局部外观覆盖／上级效果继承、移除恢复、固定快照／手动更新，以及配方实例 ID／种子保留。
2. **再接通作者与编辑流程**：三个同级常驻任务；无工程也可使用的工坊及自有试播；工坊下属独立脚本窗口。模板制作已成为第三个同级任务，画面区已迁出直接样式字段并接入预设应用；歌词区已切换中性实时预览和 `···` 空档提示；独立脚本窗口首个生产入口已接通，内部基础编排已经接通，无工程工坊、自有试播、完整快照复制及作者独立历史已接通。歌词区已保留行／逐字时间、注音翻译和来源关联，移除样式／脚本／前奏入口及按键实时落点，预览与试听使用中性源采样，增强LRC损失一次确认。
3. **完成应用与布局**：画面区只选择组合预设和应用对象，提供布局预设与精确连续分段；高级编排为内部模式，禁用脚本的试样使用包内声明回退，不改正式输出。工程、来源、行、文字、片段和节点对象清单及替换／移除／启停／同身份更新已接通，内部基础编排已经接通，并保留时间预览与草稿离开检查；四种布局、精确分段和具名面板已接通；多选及具名输入已接通。卡拉 OK 双行采用 `(5%, 60%)` 左侧中心和 `(95%, 80%)` 右侧中心。
4. **最后清理与验收**：具名工作区面板，窄窗一次一个；普通控件统一标准36px／紧凑32px并共用 `UiControlMetrics`，不自行指定大小；验证草稿离开／异步结果、闭包独立性、撤销恢复、普通／透明同帧和失败取消保护。每个可验证步骤更新文档、提交并推送。

施工构建当前0.43.9／工程28／歌词16／模板6／脚本配置库1／包profile2；最新完整UI交付基线仍为0.41.1，模板制作同级任务、画面区预设应用与对象管理、歌词中性预览、独立脚本配置窗口和Composite预设创建已接通，基础编排、四种布局与精确分段、独立工坊作者流程已接通，歌词职责清理和具名面板已接通，连续制作自动链、零警告构建和3358项完整回归已通过，用户最终人工验收待完成。旧工程及旧模板库保留且严格拒绝，不增加转换或未交付 SDK 能力。实际进度只凭代码与验证证据更新。

分类依据是**成本量级**（不是阶段）：A 完全没开工、B 引擎已有界面差最后一公里、
C 模型里根本没有这个能力、D 距 MVP 的总差距。

### 5.1 A 类：完全没开工（0 行代码）

| 工作 | 任务号 | 级别 | 验收判据 |
|---|---|---|---|
| **视频导出** | `EXPORT-01`~`09` | ✅ 全部完成 | 不透明带音轨 MP4（ADR-0033）、图片背景（ADR-0036）、透明歌词视频 ProRes 4444（ADR-0037）。入口是工具条「导出视频…」，输出类型在对话框里选 |
| **背景媒体：图片** | `EXPORT-04` | ✅ 已完成（2026-09-25，ADR-0036） | schema v6 的 `Project.Background` + 渲染背景层 + 三档适配（默认完整显示）+ 保存时收拢进工程包 `assets/` |
| **元素级覆盖真机不生效** | 待查 | P1 批次① 的收尾 | 装修 → 选中元素 → 改颜色：画面没有变化（字号按设计不支持，但提示不够醒目）。**VM/Core 层测试全绿，缺渲染层像素测试** ⇒ 先补一条"元素覆盖的颜色必须真的画出来"的像素断言，再据此判断是渲染没读覆盖还是界面没写进去/没重绘 |
| **背景媒体：视频** | `EXPORT-04` 的视频部分 | ✅ 已完成（2026-09-25，ADR-0041）：模型/时间策略/schema v8、FFmpeg 帧来源（抽象在 Abstractions、实现在 Media）、Skia 帧来源与分派、检查器入口与导出贯通 | `development.md` line 176："背景视频完整支持，以及背景裁切、缩放和定位"；§12.1 要求明确"循环 / 冻结最后一帧 / 裁切 / 填充"策略 |
| **帧缓存 / 代理预览 / 低分辨率预览** | `RENDER-05` | P1 | 拖动时间轴不再每帧重绘全质量 |
| **C# 脚本效果系统全套** | `SPIKE-07`、`SCRIPT-01`~`09` | P2 | R0 合同 A／B／UI 已接受，R1 最小编译／卸载与原生 shader 双链出口达成，SPIKE-07 完成；SCRIPT-01／04／05 有基础但完整 SDK、包与运行时仍按 R2～R8 实施，性能基准后置 |
| **发布与诊断**：渲染金样测试、导出集成测试、性能基准、Linux 发布包、Windows 构建、诊断包、示例工程与脚本 | `QA-02`~`04`、`QA-06`~`08`、`DOC-01` | 阶段 7 | 未参与开发的成员能按文档构建、运行、导出示例 |
| **CI**（Linux 构建 + 单测） | （文档无任务号） | DoD 精神 | ✅ `.github/workflows/ci.yml`；推 PR 或 `main` 时自动 `build` + `test` |

> `SPIKE-05` 已有**一半**：FFmpeg **解码**方向已通（PCM 流式读取+实测），
> ~~把渲染帧**写成视频**还没做。`SPIKE-06`（Alpha 编码路径）完全未探测。~~（2026-09-25 起都已补齐：`EXPORT-06` 与 `SPIKE-06`／`EXPORT-07`）

### 5.2 B 类：引擎/模型已有，**界面差最后一公里**（最便宜，也最容易漏）

| 工作 | 现状证据 | 要做的 |
|---|---|---|
| **元素级样式覆盖入口** | 模型、解析、持久化、渲染和检查器均已支持；颜色族、描边、阴影、不透明度可按元素绘制，整行排版属性按契约拒绝（ADR-0038/0040） | — |
| **纵向位置控件** | `LyricStyle.VerticalCenterRatio` 模型、解析器、校验器与检查器滑块均已完成 | — |
| **预览质量档位** | `RenderQuality{Draft, Standard, Final}`、抗锯齿开关与检查器下拉框均已接通 | — |

### 5.3 C 类：模型里根本没有这个能力

| 工作 | 任务号 | 级别 | 现状 |
|---|---|---|---|
| **渐变 / 描边 / 阴影 / 不透明度 / 基础缩放** | `STYLE-03` | **P0** | ✅ 已完成（ADR-0032/0040）：未唱/已唱各一对渐变起止色 + 描边 + 阴影 + 不透明度 + 缩放，贯通模型/持久化/渲染/检查器 |
| **对齐方式** | （P0 第 7 条要求） | **P0** | ✅ 已完成（schema v5；行级左/中/右对齐，元素级拒绝） |
| **关键帧与缓动** | `STYLE-04` | P1 | 无时间曲线模型。按 ADR-0031 **不计入阶段 4 出口**，可在阶段 5 之后完成 |
| **行复制** | （P0 歌词要求"新建、删除、复制、合并、拆分"） | **P0** | ✅ 已完成；文本、切分、逐字时间、样式覆盖复制，副本按原行时长顺延 |
| **多轨道 / 图层顺序** | P1 | P1 | 单轨硬编码 |
| **样式复制与继承** | P1 | P1 | 预设 ✅ + 逐属性覆盖 ✅（算部分达成） |
| **字体预览与缺失字体提示** | P1 | P1 | 有整形诊断，无"字体预览/回退提示"界面 |
| **导出队列与失败重试** | P1 | P1 | 依赖导出本身 |

### 5.4 D 类：距 MVP 的总差距

MVP 验收 14 条的逐条对照见 `docs/roadmap.md` §18.2，目前**全部达成**。
本节旧的视频导出缺口已由阶段 5 与背景媒体工作补齐；后续按 §5.6 收尾界面重排，
脚本运行方式已确定；0.7.0 已完成原生通用树及独占居中完整案例，用户已确认双行显示和整体位置联动。0.7.1 注音已验收，0.8.0 通用编排及 0.8.1 体验优化已实施，0.8.1 用户验收通过。前奏已实现并获人工验收，接下来固定包含准备显示、视觉缩放、粒子和 callback 的脚本 SDK；R0／R1 已完成，当前下一步按具体脚本落地路线 R2～R8；原三个效果方案保留验收目标，能力边界及破坏性变更见路线 §2／8。

### 5.6 样式系统历史与当前入口

0.1.0 时期的独立样式设计窗口、目标选择与批量套用方案记录在
[`docs/style-system-redesign.md`](../legacy-ui/style-system-redesign.md)、
[`docs/style-editor-ux-redesign-plan.md`](../legacy-ui/style-editor-ux-redesign-plan.md) 与 ADR-0044。
**0.2.0 已按 ADR-0050 移除旧窗口及其样式库视图模型**：现在模板工坊只制作行／元素模板，歌词行编辑器独占选择和套用；工程基线样式在主窗口的合成设置中编辑。
旧 `styles.json` 不读取也不主动删除；旧库解析类型和旧应用窗口已从源码移除。

旧方案的真机验收与第三步参数记录仍可在历史计划中查看；它们不能作为当前新架构的界面验收。当前改造的验收证据见 §5.7 和临时优化方案 §11～15。

### 5.5 已完成的部分（避免重复劳动）

阶段出口与全部 68 个任务号的状态见 `docs/roadmap.md` §18.1 / §18.3。一句话：
阶段 0 骨架 + `SPIKE-01`~`04`、阶段 1 `CORE-01`~`07`、阶段 2 `EDITOR-01`~`08`、
阶段 3 `MEDIA-01`~`04` 与 `LYRICS-01`~`04`、阶段 4 的 `RENDER-01`~`03` 与 `STYLE-01`/`02`
均已完成，阶段 4 的 `STYLE-03`、`RENDER-04` 与阶段 5 的 `EXPORT-01`~`03`/`05`/`06`/`08`/`09` 也已补齐，`QA-01`（当前 1938 项测试）已达成。

### 5.7 当前改造：模板与歌词行编辑器

完整设计、历史调研、时间真值表、断代和验收见 [`docs/temporary-style-line-editor-optimization.md`](../legacy-ui/temporary-style-line-editor-optimization.md)，固化决策见 ADR-0050；模板工坊的验收后修订见 ADR-0051 和方案 §12。`StyleWorkspaceViewModel.TargetIndex/Apply`、旧 `LyricStyleLibrary`、`TimelineHit.AdjacentBlockIndex` 与元素拖动自动扩行均已移除；新建工程不引用旧内置预设。构建与自动测试通过，新窗口在真实 Avalonia 生命周期中可见并成功构造。用户已验收歌词行编辑器主要操作、模板工坊关键交互、共享边界、行时间轨、预览／导出对比及未定时字画面；未逐项反馈的分支范围见方案 §12.4。

**0.2.1 模板工坊修订**：①行模板预览不刷新的根因是 `LyricLine.Equals/GetHashCode` 漏掉行样式与动画；已补齐并以快照通知、像素差异测试覆盖。②个人模板按现有模板创建，新建入口在列表上方，创建对话框默认「经典整行／行模板」并可命名、选类型和同类基线；个人模板显示编辑与未保存状态，样式实时预览；底部只留统一「保存模板」和警告红色「删除模板」，卡片右键复制／删除，内置只读，离开脏草稿可保存／放弃／取消。构建／自动测试及真实窗口构造通过；**用户确认行模板改色即时预览、新建／复制、保存／放弃、右键删除和最小窗口操作全部通过**。未逐项反馈的分支见方案 §12.4。

**0.2.2 未定时元素渲染修复**：采样器虽将未定时元素标为无进度，旧 Skia 的连续已唱裁切仍会在后续元素演唱时误染中间未定时字。现按每个已定时元素的进度分别建立已唱区域，整行仍只整形一次；新增像素测试先复现 1915 个误变像素，修复后通过。用户已确认共享边界、行轨、预览／导出对比均符合预期；未定时字无法在 0.2.2 界面中人为制作，故该画面仍待验收，见方案 §13。

**0.2.3 清除元素时间**：歌词行编辑器新增显式按钮，清除所选元素区间并标为 `Missing`；行和其他元素时间不变，派生色块移除，撤销可恢复。空白时间输入仍作为无效精确输入提示。模型、视图模型和保存／重开往返测试通过；实窗操作及未定时字画面已在 0.2.4 获用户验收，见方案 §14～15。

**0.2.4 选中同步修复**：清除时间后，旧选中块索引会落到邻近色块；新增断言先复现索引错误，修复后未定时元素的选中块索引为 `-1`，切回有时间元素与撤销后恢复正确索引。用户在 0.2.4 窗口确认清除后色块消失、邻块不误高亮、未定时字保持未唱外观且撤销可恢复，回复「全部通过」，见方案 §15。

**0.2.5 歌词行编辑器改版（待实窗操作验收）**：用户反馈旧模板区被长页面压到底部，随后指出首轮最小窗口排版过密。现将行编辑器的 128 像素紧凑波形固定在页签上方，持有独立 `TimeAxis` 并自动聚焦当前行；其下只有「文字与时间」「样式选择」两页。样式页左侧窄模板卡列表，右侧是随类型变化的当前／全部两个套用按钮、影响详情及从当前行／元素读取入口；工坊入口在页眉，大预览画布已从行编辑器移除。全部范围含未定时对象，批量操作先预演、再确认、一步撤销；无变化不写工程。720×560 实窗探针复测列表约 180 像素高、四个主要按钮可见，探针已移除。全量构建 0 警告、0 错误，1295 项测试通过；新版用户验收见 [`docs/template-application-ux-plan.md`](../legacy-ui/template-application-ux-plan.md) §10 及 `roadmap.md` §18.7。

**0.2.6 样式页直达操作（实窗验收通过）**：0.2.5 用户指出「查看全部写入属性」「更多操作」两层折叠不直观。写入属性与影响现直接呈现，读取本行／元素和恢复整行／元素继承四个淡色次要按钮放在同一行。720×560 实窗探针测得四按钮总宽约 365 逻辑像素，完整落在右侧面板内；探针已移除。全量构建 0 警告、0 错误，1296 项测试通过；用户确认新版布局和指定套用操作「都通过」，见方案 §10。

**0.2.7 描边引擎收尾（指定参数实窗验收通过）**：完整 `StrokeSpec` 已进入属性范围、行／元素覆盖、模板库及当前效果读取；工坊与工程基线共用一个外部描边区。空覆盖继承、显式 null 关闭整个效果、对象启用；基础颜色／宽度的局部覆盖仍能调整上层完整对象。渲染先画外扩轮廓再减去不透明字形遮罩，解决透明填充下内部轮廓外露。全量构建 0 警告、0 错误，1308 项测试通过；`lyrics.json` v9、`project.json` v15、`templates.json` v1。用户确认外部描边改色、改宽度、改连接方式均实时生效；其他分支未逐项反馈。具体证据、用户验收路线和后续未完成项见样式计划 §13 与 `roadmap.md` §18.8。

**0.2.8 独立行位置（用户实窗验收通过）**：行位置以归一化坐标独立持久化，不写样式模板。画布布局模式显示当前行静态试样，暂停播放并收起主时间轴；数值栏、六种锚点、旋转／缩放、拖动／吸附、方向键、取消、恢复默认及跨行批量预演已接入。预演不改工程，单次提交一步撤销；非法或失效结果拒绝。全量构建 0 警告、0 错误，1335 项测试通过，歌词 schema v10，工程 v15、模板库 v1；940×640／560×440 实窗探针通过并已移除。用户确认「布局编辑可用，功能全部正常」；分支默认／批量随阶段 E 接入。具体见样式计划 §14 和 `roadmap.md` §18.9。

**0.3.2 分支级双行整体变换（用户验收通过）**：在 0.3.1 的槽位布局所有权基础上，歌词 schema 升至 v12，分支可保存双行组合的归一化横／纵偏移、绕画布中心旋转和整体缩放。变换只作用于槽位外层，保留槽位一／二之间的相对关系；普通模式仍使用本行位置。分支设置提供整体变换开关和参数，关闭时不写入变换。全部测试、保存重开往返、统一采样位置和实际 Skia 画面差异断言已通过；用户确认所有功能正常。高级编排见样式计划 §15～16、roadmap §18.12。

**0.5.0 样式引擎收尾（已实施）**：在 0.4.1 规范模型基础上，ADR-0058 和 [`docs/style-engine-next-phase-plan.md`](../plans/style-engine-next-phase-plan.md) 固化并完成线性／径向渐变的画布／行坐标闭环、RGB／HSV 能力矩阵诊断、旧渐变端点和阴影 XY 编辑地址清理，以及按顺序持久化的字体回退链。工程、歌词、模板 schema 分别为 v17、v14、v3；程序版本为 0.5.0；不读取、不迁移旧格式。预览与导出共用同一渐变采样和字体解析，Core、Skia、DTO、编辑器和回归测试已完成。

**当前脚本状态（0.40.2 本轮goal已完成）**：R0／R1已通过，U0／U1与U2基础已接通，完整R2～R8未交付。实际源／场景、固定字形、事件／历史／120Hz、七阶段、几何／变换、音频、粒子／Burst、区域材质及逐节点uniform的已实施范围以roadmap§18.30～18.71为准。工程歌词／效果包／媒体／分析修订及模板配方／资源闭包已持久化；M1源MaterialSpec已统一，M2真实源归属、无需C#的固定纯材质资源、基线与准备帧／正式Render及MP4／Alpha同帧已接通，不伪造实例或无效果成片。U0已接通异步运行装配、预览合并和独立导出；U1已接通材质选择／参数、完整包导入和模板实时预览；U2基础已接通工程效果实例工作区、包／实例分栏、CRUD／顺序、绑定／订阅／seed／参数编辑和保存前正式会话验证；U2接续已接通目录／.mkfx包闭包导入与原子分享、跨选择多文件源码草稿、新修订独立编译和行列定位、单实例接受保参数、失败源码保存；R7实时预览、模板配方和歌词行／元素／场景对象上下文入口及真实时间／静态参考边界已接通，入口复用统一工作区并保持保存／验证分开，新建实例禁用。目标相关实例清单、共用实际帧绑定范围、独立源订阅说明及精确实例／节点定位已接通，工坊行模板元素族制作、完整模板分享／导入入口已接通，本计划R7／R8自动出口已分别按§18.105／§18.107收口，2026-10-06文字／粒子／资源效果用户总体验收通过并接受当前goal完成（§18.108）；纯图实例正式运行已在0.37.0接通；原路线跨输出计划、组图／父配方、其余writer、实际映射和目标内捕获仍保持原有门禁。粒子Jump／Bridge、VectorTrail／SpriteTrail／Burst及本计划R6自动出口已经按roadmap §18.90～18.95完成。当前目标已授权新决策按已有合同记录ADR，既有明确选择不重问；历史待答不当作已实现。下列历史记录不代替当前状态，性能基准后置。

**脚本与场景架构暂停（2026-09-30，ADR-0061 设计已确定）**：本轮不改代码。脚本在主程序进程内按会话串行运行，不占用 UI 线程；允许 Roslyn 热编译、可重建时间线历史和节奏／低频输入，参数进入撤销栈，启用脚本编译／加载／重放／求值失败阻止导出。预览／导出各自重建会话，播放／暂停／跳转控制历史不进入首期视觉输入。分支显示容器在普通／双行模式共用外层 `Placement` 和固定枢轴，`FreeLine`／`DualSlots` 负责局部布局；展示配置将进入工程 `lyricPresentation`，旧歌词展示字段退出。脚本只写填充／渐变、描边、阴影、不透明度，字体／字号／缩放、布局和粒子不开放。源码保存到工程 `scripts/`；成功 DLL／PDB／构建清单按完整指纹保存到应用本地数据目录，Linux 默认 `~/.local/share/MakoKaraGenerator/scripts/`，退出后保留。

**0.6.0 统一场景及共用位置编辑（ADR-0062，双行与内部槽位已获用户确认）**：用户恢复开发并批准先做 S0～S3。工程级 LyricPresentationDefinition 集中保存容器、内部位置和内置动画；源歌词只保存源语义和样式覆盖。普通与双行共用外层位置，位置编辑默认容器，内部可选本行／槽位一／二。SceneDefinitionBuilder、SampleFrame、GetSceneFrame 贯通稳定 ID、绑定、翻译／注音测量与世界几何；绘制、布局试样、命中和成片共用同一入口。旧位置旗标、源展示字段与 DualLineGroupTransform 类型已删除。1402 项自动测试及全量构建通过；实窗探针覆盖目标切换、数值、预演取消撤销和最小窗口布局，并修复下拉通知递归。工程／歌词／模板 schema 为 v18／v15／v3；不读旧工程。具体证据、验收路线和脚本下一阶段见方案 §10、roadmap §18.17。

**0.7.0 原生通用容器树与独占案例（ADR-0063，双行显示与整体编辑已确认）**：按用户明确选择直接改为 nodes／programs／lineOverrides，不保留旧展示 branches。共同父组、常规容器、槽位和独立容器共用一个节点合同，任意父子层级及参考尺寸参与真实世界矩阵。独占区间严格读取源行时间，硬切容器、保留自身入场动画，前后片段隔离调度且恢复槽位一；翻译原位、注音随基础字。TrySetTree 支持预演与诊断，时间修改／平移拒绝演唱冲突，旧编辑入口保持自定义身份与片段，删除只清理源关联、撤销恢复。演示工程有五句，8.5～13.8 秒为暖色居中句。工程／歌词／模板为 v19／v15／v3；1431 项测试通过，含三个阶段真实 MP4 与预览对比；1400×880 实窗探针通过父组编辑、撤销及保存重开，探针已删除。完整证据见案例方案 §7 与 roadmap §18.18。用户已确认双行正常显示、整体位置修改带动组合变化；其他细项未逐项反馈，注音问题在 0.7.1 修订。下一步设计通用容器／片段／槽位交互，详见新编辑器方案；脚本尚未开工。

**0.7.1 注音修订（ADR-0064，用户确认注音恢复正常）**：演示 qǐng zhào 原先全部绑定到「请」；按用户选择改为两个正确元素关联，字号比例 0.4 避免相邻字形交叠，原元素时间不变。真实演示资源、保存重开、中心几何、父组旋转／缩放、各自已唱／未唱像素和注音间距测试通过；旧错误数据使新增回归测试失败。全量构建 0 警告、0 错误，1434 项测试通过，schema 不变。全面编辑器提案和用户两项方向见新方案；尚未实现新编排 UI。

**通用编排编辑器 G0（ADR-0065，数据／UI 审阅稿）**：用户已授权开始开发并要求先确定具体结构和 UI。已确认多句区间来自首末源行、重挂保持画布位置／旋转／大小、槽位变化后整段从首槽重新分配。[实施合同](../legacy-ui/scene-editor-implementation-contract.md)细化默认容器与通用片段、单句局部变换、动态节点选择、重挂与历史；docs/design 保存交互原型及宽／窄截图。G0 原型已检查演示交互和排版，未接入工程，也不算 Avalonia 成品验收。具体格式、默认归属／片段删除回归、连续且定时的成员及复制子树不复制绑定均已获批准。G0 时程序保持 0.7.1；随后实施见下项。审阅历史见 roadmap §18.20。

实施顺序与证据：① `applicationVersion` 打开门禁；②共用的行/元素时间校验；③ `LyricSampler` 与像素断言；④选中元素单边手柄和独立行轨；⑤歌词行编辑器与模板选择／套用；⑥应用级 `templates.json` v1 并移除旧加载与应用窗口。A～F 同属 0.2.0；旧文件不主动删除但新程序不加载。人工验收后记录实际结果；之后每次程序交付按 ADR-0050 递增版本。


**0.8.0 通用编排编辑器（ADR-0065，G1～G4 已实施、基本流程获人工确认）**：工程 schema 升为 v20，歌词／模板保持 v15／v3。ScenePlacement、SlotSequence、LyricDisplayClip 连续源成员和 LocalTransform 贯通 Core／DTO／采样／渲染。SceneTreeEditor 与 Core 世界矩阵支持任意节点、槽位重排、复制／删除及保持世界外观重挂；PresentationEditSession 统一草稿、取消、一次提交、无变化与过期拒绝。主窗口新增容器树／源歌词、共享画布、片段和所选对象属性，窄区使用 300 px 覆盖属性面板；旧固定两槽目标、双行专用表单和批量位置窗口退出 App。1470 项测试及全量构建通过，含新工程完整六句双槽→居中→三槽、注音／翻译、保存重开、MP4 抽帧对比；真实 Avalonia 控件探针通过创建三槽、默认归属、片段、提交／撤销重做、空工程及 1400×760／940×520 内容区布局。探针已删除，截图保存在 docs/design；自动证据不代替人工验收。具体路线见实施合同 §8，权威状态见 roadmap §18.21。用户已实际制作工程，确认容器与片段绑定和轮换基本生效；树重挂、错误分支和导出等不扩大为全部人工通过。脚本和性能基准未进入本批。

**0.8.1 编排体验优化（ADR-0066，已实施并获人工验收通过）**：用户批准[优化方案](../legacy-ui/scene-editor-ux-optimization-plan.md)后完成 U1～U4 的入口、术语、布局方式、槽位列表、树菜单／拖放、片段区及对齐。树中槽位按 SlotIds 显示与排序，普通子节点保持 Nodes 顺序；边缘同级重排、主体重挂确认并保持世界外观，Esc 取消不改草稿。自由／轮换切换保留原节点，明确选择参与槽位；来源、默认设置与唯一片段创建分层。新增 10 项 App 回归及变异检查，真实控件探针覆盖宽窄内容区、菜单／指针路由、重挂和树状态保留。用户随后明确“测试验收通过”；schema 保持 20／15／3，脚本未开工，证据见方案 §13，状态以 roadmap §18.23 为准。

**0.9.0 卡拉 OK 前奏（ADR-0067，自动验证及人工验收通过）**：用户授权按[前奏方案](../plans/karaoke-lead-in-plan.md)实施 L1～L4。nodes 保存 CountdownSequence／TextMark 内容和附着，programs 保存 leadIns／生成参数；源时间不变，完整准备区间由首定时元素／行起点派生。共同绝对时间计划保持原片段／槽位及装载身份，倒计时开始一次入场、切换原保留句、归零连续起唱。提示字体／渐变／描边／阴影与共享 Skia 渲染贯通，联合边界和命中复用最终矩阵；静态实际目标参考下切换定位换算保持外观。树／属性／前奏页签和候选分析／预览／取消／加入草稿已接入，手工、禁用和排除受保护，顶部一次提交／撤销。新增 32 项测试；全量构建 0 警告／0 错误，1512 项通过，含 24／30 fps MP4 对比及真实 Avalonia 宽窄控件。版本／schema 为 0.9.0／21／15／3，不迁移旧工程。2026-10-02 用户确认“功能全部正常，前奏验证可用”，且真实容器／目标句绑定符合预期；具体证据及本机临时工程路线见方案 §13～14。脚本未开工。
---

## 6. 如何验证（本机没有输入注入工具）

1. **视图模型级测试**（首选）：`tests/MakoKaraGenerator.Spike.App.Tests/` —— **不创建窗口**，
   直接驱动 `EditorViewModel`。`Loc.Get` 在无 Avalonia 应用时回退为键名，所以断言"有反馈"而非具体文案。
   ⚠️ 只读派生属性（如 `LyricStyle`）**直接读测不出通知有没有发**，要订阅 `PropertyChanged`。
2. **临时探针**：在 `MainWindow.OnLoaded` 里跑真实路径，把结果 `Console.Error.WriteLine`，
   启动后读日志，**验证完必须删掉**（不要留在提交里）。
3. **离屏渲帧导 PPM**：`P6\n{w} {h}\n255\n` + BGRA 直写，比截图可靠得多（见 `PreviewDumpTests.cs`）。
   导出侧同理：`Export.Tests` 会真的导出 MP4、再用 FFprobe 探测、再抽一帧逐像素确认"画面里真有歌词"；
   另有一条守卫测试扫描界面层源码，禁止出现编码器名与命令行参数（`UserInterfacePurityTests`）。
4. **截图**：`grim`。⚠️ **不要为了截图去动用户的窗口**——
   `niri msg action expand-column-to-available-width` 会把用户正开着的 QQ 窗口展开。
   `focus-window --id N` 是安全的。
5. **测试素材**：`tests/Shared/WavWriter.cs`（自写 WAV）、`TemporaryDirectory.cs`；
   `/tmp/makokara-demo/` 的演示音频与 LRC 已被清理，需要时用 `WavWriter` 重新生成。

---

## 7. 已知缺陷、陷阱与小坑

### 已知行为缺陷（用户已知，未要求修）

- **时间轴"铺满整曲"只在装载时按当时宽度算一次**，窗口被 WM 改尺寸后不适配
  （可能需要"用户未手动缩放时随尺寸重算"或「适配全曲」快捷键）。
- **元素级覆盖的属性限制**：模型支持行级全属性；**元素级支持颜色族（含渐变）+ 描边/阴影/不透明度**，
  因为它们能按元素裁切绘制；**缩放/字号/字体/纵向位置属于整行排版与整体变换**，元素级会被校验器
  报警（`UnsupportedTokenStyleOverride`）并在渲染时忽略（ADR-0032）。
- **Ctrl+N 工程操作已修复并完成真机验收**：工程操作入口现在复用同一 workspace，快捷键按下沿只提交一次请求；取消未保存确认后不会消费同一次按键的重复事件。
- `samples/TechnicalSpike` 已被渲染管线取代，应用不再引用它（去留待定）。

### 编码陷阱（都踩过，勿重犯）

| 陷阱 | 症状 / 正确做法 |
|---|---|
| `RenderFrame.CopyPixelsTo` 的默认参数 | `height < 0` 是"整帧"哨兵；曾因先校验负数导致**默认调用必抛**，画布只剩一条提示条 |
| `PointerCaptureLost` | 是 **Direct 路由**，父控件收不到——滑块上写它会得到一个永不触发的处理器 |
| `TextBox.TextChanged` | **延后到调度器回合**才触发；程序改文本后立刻读状态会读到旧值。⚠️ 因此**不能用“护栏布尔”防回环**（写文本时护栏已撤）——要比“解析结果是否等于当前值”。另外**不要**无条件把生效值写回用户正在输入的框（会吃掉后续输入） |
| `TextBox.Watermark` | Avalonia 12 已废弃 → 用 **`PlaceholderText`**；写错每处一个 `AVLN5001`，在 0 警告要求下就是失败 |
| `TabControl.SelectionChanged` 会接到子控件的选择事件 | 歌词栏「样式选择」页签曾在此事件里重建候选 `ObservableCollection`：清空集合引发下拉框选择变化，事件再冒泡回来，Avalonia 因选择更新期间源集合再次变化而抛 `Source collection was modified during selection update`，点击页签即崩。候选在 VM 构造时初始化，样式库增删改由 `SavedStyles.CollectionChanged` 同步；不要在页签选择事件里重建列表 |
| 两份文件的迁移步骤表 | `project.json` 用 `ProjectSchemaMigrator.Default`、`lyrics.json` 用 `LyricsDefault`，**不能混用**：两张表的 2→3 讲的是不同的事（音频路径 vs 样式覆盖）。拆表时歌词文件必须自己登记 `1→2`，否则 v1 歌词文件会因“缺少步骤”整体读不出来（实测八个诊断测试直接红） |
| 时间轴标尺的两种拖动 | **Shift+拖标尺 = 画循环选区；不按 Shift 才是定位播放头**。按住 Shift 时"抓住播放头"的判定要放行，否则两种意图会互相抢。加标尺手势前先看这条 |
| 数值输入框不要逐键提交 | 工程级数值（画布宽高等）逐键提交会让 `1920` 在敲到 `1` 时就写进工程——值先变 1、撤销栈还多三步。用"回车 / 失焦提交"，与字符型颜色框（逐键更跟手）区分开 |
| `x = 条件 ? default : 非空值` | 三元表达式的类型由**两个分支共同**推断：兄弟分支是非空 `float` / 结构体时，`default` 会变成 `0f` / 默认色，**而不是 `null`** ⇒ "恢复继承"静默写成"覆盖成 0"（`StyleBatchEditor` 里 18 个属性中 11 个中招，编译不报，只有遍历全部属性的测试能抓到）。要空就写 `null`，要 `default(T)` 就把类型参数写出来 |
| 元素级覆盖的"排除矩形"必须含**装饰外扩量** | 描边与阴影会画到字框之外：整行那一遍只按字框排除时，元素的描边**外沿**仍会被画出来，元素即使明确关闭描边也擦不掉（表现为字框边界一列半透明基线色）。两遍必须用**同一个**矩形，且含描边半宽 + 阴影偏移/模糊 |
| 核对"还有谁在用这个成员"要用**裸标识符** | 用 `.成员名` / `成员名(` 这类调用形状去 grep，会漏掉 `nameof(成员名)`（派生视图的刷新通知就是这么写的）⇒ 误判"没人用"并标成弃用，直到编译报 CS0618 才发现 |
| 批量替换**每条**都要带断言 | 不带断言的 `replace` 会**静默漏改** ⇒ 文件进入"有的改了有的没改"的混合状态，红测的根因查不清（2026-09-26 本喵因此把一整批改动退回重做）。改完顺手核对"关键行是否真的变了" |
| 被批量改过多次的文件，应**重写**而不是继续打补丁 | 同一文件反复 patch 之后，靠读代码已经拼不出真实状态（残留已删方法的注释、同名成员两处）⇒ 按"引用清单"（XAML 绑定 + 代码调用 + 其它文件引用）重写一遍，比继续修快也更靠得住 |
| C# 14 的 `field` 关键字 | 在**属性访问器**里把 `field` 当变量名（`Fields.Any(field => …)`）会与合成后备字段冲突（CS9273/CS9258）；方法体内没问题 ⇒ 属性和方法里统一换成 `entry` 之类更省事 |
| 能派生的清单就别手写；必须手写的，配一条"遍历全部成员"的测试 | 元素级支持范围曾被写成三份（`TokenStyleSupport`、校验器、渲染器私有），渲染那一份无人看守 ⇒ ①放开一项而③忘了加，就是"界面允许设、画面里没有、不报错"。现在归到一份事实派生，并用遍历 `LyricStylePropertyExtensions.All` 的闸门测试钉住（变异检查过）。同理：`IsEmpty` 这类"逐项检查"的成员也要被同一条测试覆盖 |
| 加字段时，"参数 / 属性 / 写入 / 读取"四处要成对改 | 行进入动画加进 `LyricLine` 时踩了两次：①工厂 `Create` 收了 `animation` 参数却**忘了写进对象**（编译器不报——参数没被用到只是不算错）②行映射的 `ToDto` 漏了 `EnterAnimation`（保存不报错，读回来是 `null`）。两次都是"测试红了才发现"。加字段后的自检：工厂有没有存、DTO 有没有写、映射有没有读，三处都要看一眼 |
| 加新的种类取值时，把所有 `HasXxx` 早退条件过一遍 | 背景视频接进来后画面只剩背景色：`DrawBackground` 的早退条件还是 `!background.HasImage`，视频被**整个跳过且不报错**。这类条件不会因为类型变多而自动变宽——ADR-0036 特意不预占 `BackgroundKind.Video` 就是为了逼出这件事 |
| FFprobe 的 JSON 里数字与字符串混着来 | 宽高是**数字**、时长是**字符串**、帧率是 `"10/1"` 分数。只按字符串读会让宽高静默变成 0，表现为"这个视频里没有画面"。两种都要认 |
| 别在 README 里维护第二份阶段表 | README 的进度表长期停在阶段 3 时代（写着"还不能导出视频"、阶段 4/5 状态过期），而状态权威只有 `roadmap.md` §18 一份（ADR-0030）。README 只该放一句摘要 + 链接；改完阶段状态要顺手核对 README |
| 说某一层"支持"之前，先在该层写一条断言 | 批次① 的元素级覆盖只有 Core/VM 层测试 + 真机抽查，**渲染层没有像素测试** ⇒ 主人一验收就是"改了没反应"。凡是"某个覆盖/样式会画进画面"的说法，都要有一条像素断言兜着；否则"写进模型了"与"画出来了"之间的那段路无人看守 |
| 改"作用范围"的分支时要连调用一起改 | 样式入口是"按范围写哪一层"：只把条件从 `== 行` 改成 `!= 工程` 而**忘记换调用**，元素档的改动会静默写进**行**覆盖——界面看着像生效了，其实改错了层。现在只有一个分派点 `EditScopedOverride` |
| 渲染帧是**预乘** BGRA，ffmpeg 的 `bgra` 是直乘 | 不透明时两者数值相同，所以这个坑只在**透明输出**里现形：半透明像素会被二次压暗（50% 的红变成暗红）。喂编码器之前必须反预乘（`AlphaFrameConverter`），只处理 `0 < alpha < 255` 的像素 |
| 验收透明输出看**像素格式**，不是扩展名 | 实测本机 `libvpx-vp9` 三种参数组合都"成功导出"却悄悄丢了 Alpha（扩展名、容器全正常）⇒ 判据只能是 ffprobe 报出来的 `pix_fmt`（`MediaStreamInfo.HasAlpha`），并把结论写进报告 |
| 背景图片是**叠在**底色之上的 | 底色（`Composition.BackgroundColor`）永远先铺满，图片画在它上面 ⇒ 带透明度的图片会与底色叠加。断言"背景生效"时要挑对像素：留白处/透明处仍是**底色**，不是黑也不是图 |
| 保存时收拢资源必须是幂等的 | 每次保存都会跑一遍 `ProjectAssetCollector`：只按"文件已存在"判断，第二次保存就会造出 `bg-xxxxxxxx.png`，assets 目录越攒越多 ⇒ 同名**同内容就复用**，只在同名不同内容时才加哈希后缀。另外**自动保存必须收拢功能关闭**，否则 10 份备份各塞一份大图 |
| 画位图不要开抗锯齿 | 轴对齐的位图不需要抗锯齿，开了只会在边缘引入半透明像素，让"图铺到哪了"这种像素断言变得难写又难读。像素测试的容差按"纯色区域"取，别跨边缘取样 |
| `Progress<T>` 的构造位置 | 它在**构造那一刻**捕获 SynchronizationContext。<b>别把它写在 `Task.Run` 里</b>（那里的上下文是空的）→ 回调直接在线程池线程上跑 → 界面更新控件就是 `The calling thread cannot access this object`，**点导出直接闪退**。要在界面线程上构造，再传进去。控件侧另加一道 `Dispatcher.UIThread.CheckAccess()` 兜底 |
| 回归测试要能红 | 本喵给上面那条写的第一个测试是"数 `Post` 被调用几次"——**假通过**（`await` 的续体也会 Post 一次）。改成"数回调真正跑在哪个线程上"（排队式上下文 + 测试线程自己泵）后，做**变异检查**（把 bug 改回去）确认它真的会红。**写完回归测试必须做一次变异检查** |
| 对话框必须有公开无参构造 | 否则 XAML 资源报 **AVLN3001**（"won't be reachable via runtime loader"）：写成"无参构造 + 注入构造"两个重载，注入的那个 `: this()` 链过去 |
| `string.Create(CultureInfo, $"...")` | 只能接**单个**内插字符串字面量：写成 `$"..." + expr` 会解析到 `ref` 版重载而报 CS1620。要把整句写进一个字面量里 |
| Avalonia 12 的 `TextBox` 滚动条 | 不是 `VerticalScrollBarVisibility` 属性，而是附加属性：`ScrollViewer.VerticalScrollBarVisibility` |
| `StorageProvider.SaveFilePickerAsync` | 返回**单个** `IStorageFile?`（不是列表）；`OpenFolderPickerAsync` 才返回列表 |
| 遍历 `/proc/*/cmdline` 杀进程 | 会匹配到**自己所在的 shell**（命令行里就写着那个字符串）→ SIGTERM 自己（退出码 143）。用 `readlink /proc/PID/exe` 精确匹配可执行文件 |
| `Colors.axaml` 的主题字典 | Dark/Light 是**两个独立字典**，加颜色键必须两边都加；**加完必须启动一次**（曾因重复键启动即崩） |
| 位置记录实现同名成员接口 | `record` + 接口 `Apply` 冲突 → CS0535，用 `class` |
| `Loc.Get`/`Loc.Format` | 用 `Loc.Format`（`CA1863` 是错误） |
| `TimelineBlockTimeChange` | 在 `MakoKaraGenerator.Spike.App.Controls` 命名空间，不在 `Timeline` |
| `LyricToken.Create` | 给了时间必须给匹配的 `TimingStatus`，`Missing` + 时间会抛异常 |
| xUnit 分析器 | `Assert.Equal(1, collection.Length)` → `xUnit2013`，用 `Assert.Single`；返回 `object?` 的测试辅助 → `CA1859`（本项目当错误） |
| 无音频时 `ContentDurationUs == 0` | 打点会被夹到 0 而返回 false（测试要另找路径） |
| `git checkout -- <file>` | 会连**本轮未提交**的改动一起回退（2026-09-26 本喵真踩过：用它"还原变异"把刚写好的分组实现整块回退了，只能重做一遍）⇒ **还原变异要用改写脚本，别用 `git checkout`**；`git add -A` 会把两段改动合成一个提交 → **先按路径 add，再提交** |

### 依赖本机字体

整形测试依赖 `Noto Sans CJK SC` + `Noto Color Emoji`；换机器会布局漂移。
`QA-02` 渲染金样测试**必须在固定字体环境下运行**。

---

## 8. 已裁决与仍悬空的事项

### 8.1 已裁决（2026-09-25，ADR-0031）

一次完整需求盘点发现 4 处需求文档自身的矛盾，加上 4 项会在导出一开工就挡路的待决策，
已逐条请用户确认：

| 事项 | 决定 |
|---|---|
| 两份文档阶段编号不一致 | 以 `roadmap.md` 为准（8 阶段）；`development.md` §16 只作技术演进说明 |
| P0 的样式下限 | **照 P0 原文全做**：纯色、水平渐变、描边、阴影、不透明度、基础缩放（`STYLE-03` 全覆盖） |
| 阶段 4 出口的"进入动画" | **不计入出口**（依赖 P1 的 `STYLE-04`）；出口只要求渐变样式 |
| 自动保存与崩溃恢复 | **提前到阶段 2 之后**，新增 `EDITOR-10`（最小版）；阶段 7 的 `QA-05` 改为回归与强化 |
| 视频容器/编码器/质量预设 | MP4 + `libx264`（`yuv420p`）；草稿 CRF 26/`fast`、标准 CRF 20/`medium`、高 CRF 16/`slow` |
| 是否随应用分发 FFmpeg | **不自带**：启动探测 + 缺失提示 + 文档安装说明（发布包保持轻量） |
| 透明视频首期格式 | **ProRes 4444（`.mov`）**；允许延后，不阻塞 `EXPORT-06` |

### 8.1.1 已裁决（2026-09-25，ADR-0032；历史契约）

`STYLE-03` 实现中撞到的三处契约问题（详见 ADR）：

| 事项 | 决定 |
|---|---|
| 渐变语义 | **三态各自一对起止色**（6 字段）；两端都设置才走渐变，只设一端退回纯色 |
| 元素级覆盖契约 | 允许颜色族（含渐变）+ 描边/阴影/不透明度；拒绝缩放/字号/字体/纵向位置 |
| schema 版本 | **两条线各自推一步**：`project.json` 3→4、`lyrics.json` 2→3；步骤表按文件拆开 |

### 8.1.3 已裁决（2026-09-25，ADR-0042）

| 事项 | 决定 |
|---|---|
| 关键帧动画的范围 | **最小版「行进入动画」**：三通道（不透明度/缩放/纵向偏移）× 四种缓动，时间从行起点起算；通用关键帧表与持续/退出动画暂不做 |
| 动画的数据归属 | 工程级默认 + 行级覆盖（行级 `null` = 跟随默认）；**没有元素级**（会与逐字时间语义打架） |
| 动画的启用方式 | 显式启用：两层都为 `null` 时不动画，老工程迁移后画面不变 |

### 8.1.2 已裁决（2026-09-25，ADR-0040）

| 事项 | 决定 |
|---|---|
| 字幕颜色状态 | 只保留未唱与已唱两种输出颜色；当前播放或选中元素由编辑器高亮与逐字进度边界表达 |
| 旧当前颜色字段 | `project.json` 升至 schema v7、`lyrics.json` 升至 schema v4；迁移删除 `currentColor`、`currentGradientStartColor`、`currentGradientEndColor` |

### 8.1.4 已裁决（2026-09-30，ADR-0054）

用户直接要求继续歌词分支、注音和动态布局：支持唯一主歌词分支与多个翻译分支，稳定行关联、分支样式／布局，预览与导出共享场景。此授权解决此前「是否支持多个歌词轨道」的歌词范围；通用媒体多轨仍另行裁决。

### 8.1.5 已裁决（2026-09-30，ADR-0055）

双行动态模式下由槽位拥有主歌词的位置；本行显式位置保留但不参与动态渲染，布局编辑器禁用逐行拖动、位置输入、恢复默认和批量位置入口。关闭双行后，本行位置恢复生效；分支级整体变换留待后续阶段。

### 8.2 仍然悬空，动手前必须先问用户

| 待决策 | 何时需要 |
|---|---|
| Linux 发布形式（AppImage / Flatpak / 其他） | **阶段 7 开工前** |
| 通用视频／音频多轨和图层编排 | 动媒体轨道模型之前 |
| 扩大脚本范围到视觉缩放、粒子、布局改写、播放操作录制 | **本轮已请求视觉缩放／粒子设计，已确认行为见 ADR-0068；具体 SDK／包／数据／UI 已按 R0 获接受，按 R2～R8 实施。布局／源时序改写和播放操作录制继续后置** |
| 自定义材质／shader、整行效果与渲染图 | **本轮要求重新设计，背景来源两种选择已确认。具体材质／图／SDK／包／模板依赖／UI 已由 R0 合同接受（ADR-0071），R1 后端验证通过；透明跳过／提示／包内基础外观及 bypass 按合同在生产路径实施** |
| 更丰富音频分析（完整频谱、高潮估计、多频段） | 首期只提供节奏／低频可视化数据，后续扩展 |

`development.md` §18 的各项已按上述结论就地标注。

### 8.3 工作方式约定

- 一般任务**拿不准就先问用户**；大结构决策需确认。**2026-10-04当前脚本系统目标已更新授权**：需决策时参考已有文档，按代理最优理解实施并记录ADR；原明确选择保持，新的架构／数据及接口细节不重复等待询问。该授权仅适用于当前脚本目标；不扩大为其他任务或删除历史用户文件。
- 用户没要求的**不要自己发挥**。
- **未经授权不得委派子代理**。
- 破坏性操作（删工程/删文件/杀进程）先确认；**旧项目 `/mnt/Data/Program/MakoKaraGenerator`
  （无 "New"）可能仍在使用，绝不能杀**。

<a id="frontend-backend-collaboration"></a>

### 8.4 前后端协作（2026-10-16 双方确认）

#### 职责与决策

- **Codex 只负责后端**：本仓库的 Core／Application／Contracts／Hosting／Server／Worker、相关基础设施、文档和后端验收。**Claude 负责前端**：`/mnt/Data/Program/MakoKaraGenerator-Web` 的协议客户端、Web 界面与浏览器验收。双方可只读查看对方实现以定位问题，修改交给对应仓库负责人。
- 用户决定业务目标、批次与优先级；在已批准范围内，**API 规范及后端实现细节由 Codex 决定**，Claude 提出前端场景、缺口及实现代价，双方核对可消费性。涉及扩大目标、持久化语义或重大兼容性取舍时交用户决定，遵守既有暂停范围。
- 后端拥有工程内容、修订和历史；前端维护输入草稿、选择、播放头与显示状态。现有桌面实现可用于核对业务语义；headless 后端及 Web 联调不以启动 Avalonia 窗口为前置。

#### Agent Mail 使用方式

| 角色 | 实际目录／`project_key` | 首次联通身份 |
|---|---|---|
| Codex 后端 | `/mnt/Data/Program/NewMakoKaraGenerator` | `CoralCarp` |
| Claude 前端 | `/mnt/Data/Program/MakoKaraGenerator-Web` | `PinkPike` |

- 使用本机 MCP Agent Mail。每次会话先检查服务、项目和代理列表，核对身份后复用或登记；上表是首次联通记录，不能假定对端身份永久不变。`project_key` 使用实际工作目录的绝对路径，不把两个仓库登记成同一项目。
- 跨项目地址为 `代理名@项目绝对路径`，例如 `PinkPike@/mnt/Data/Program/MakoKaraGenerator-Web`。先完成联系请求／批准；从自己的项目收件箱读取并确认消息，不代替对方读取或确认。
- 会话开始、关键实施步骤后、提交／交付前检查 inbox；开工、阻塞、合同待评审、交付及联调结果主动通知。需要明确答复时设 `ack_required=true`，收到后先 acknowledge，再给实质回复；**ack 只表示收悉，不表示接受合同或验收通过**。
- 按异步投递使用，不能假定邮件会自动唤醒对方。等待回复时先推进独立工作；只有无独立工作可做时才有界查询，超时向用户报告“等待谁／哪个线程／缺什么”，不持续空转，也不把沉默视为同意。
- 文档和邮件使用中文与绝对日期。主题使用 `[提案]`、`[合同]`、`[缺口]`、`[交付]`、`[联调]`、`[问题]`、`[协作]` 前缀；`importance=high` 只用于阻塞对端工作的事项。凭证、私有 bootstrap、token 与用户 profile 不进入邮件、文档或提交。

#### 任务、文档与合同规范

- 缺口／缺陷沿用全局 **G#／R#**，跨阶段连续；新编号先与对端核对，防止重号。线程 `thread_id` 直接使用编号，如 `G7`、`R1`。批次沿用 [后端审查 §6](../plans/backend-review-and-web-contract-plan.md) 的 S0～S5，线程可用 `S1-read-model`、`S2-waveform`；协作规则线程为 `frontend-backend-collab-20261016`。
- 前端需求与缺口登记在当前阶段设计文档的“后端缺口”节；W0 为前端仓库 `docs/web-client-w0-design.md` §9，W1 开工时另建对应设计文档。后端方案／候选合同保存在本仓库 `docs/`，交接入口为 [web/README.md](../../../web/README.md)。邮件给摘要、文档路径和提交 SHA；重要决定落到所属文档，避免只存在于邮件中。
- 后端权威合同集为同一提交中的 [执行合同](../../../web/api-contract.md)、[API 清单](../../../web/api-index.md)、[OpenAPI](../../../web/openapi.json)、[示例](../../../web/examples.json)、[manifest](../../../web/contract-manifest.json) 及 [详细行为源](../../architecture/backend-api.md)。遵守执行合同 §4 的独立版本与变更规则；OpenAPI、行为正文与实际 Server 冲突时视为缺陷，交 Codex 核对修复，不由前端猜测语义。
- 前端 `docs/backend/` 是注明来源 SHA 的副本；从**同一后端提交整体同步合同及行为源**，更新来源记录、`CONTRACT_VERSION`／`CONTRACT_PRODUCT_VERSION` 及客户端类型，不混用不同版本或手改生成文件。当前 W0 手写类型及漂移检查的实际策略见前端设计 §12.4，后续生成类型也须记录工具版本。
- 新 API 先说明用户场景、现有缺口、路由／命令及请求响应、稳定 ID／Int64 表示、精确修订、历史粒度、no-op、冲突／错误、changeSet 与验收场景；候选方案不提前写入冻结的已实现清单。提前 mock 只能依据 Codex 提供的带提交 SHA 的候选合同，代码和文档明确标为“候选／mock”。

#### 交付、验收与 Git

- 影响 HTTP 行为的后端交付同步实现、对应回归、行为源、合同工具映射及示例，按 [生成与校验工具说明](../../../tools/backend-api-contract/README.md) 生成 OpenAPI／index／manifest，并完成合同检查和对应真实 HTTP 验证；不能把重新生成指纹当作行为已验证。
- 后端交付邮件包含：任务／范围、提交 SHA、合同／产品／协议版本、兼容性及差异、**发布目录与是否须重新匹配发布 Server＋Worker**、`host.py run` 参数或 stdout 合同是否变化、复现／验收步骤、实际证据和限制。前端依赖 `MAKO_BACKEND_DIST` 和 `scripts/backend-launcher.mjs`，启动器变化必须明确通知。
- Claude 整体同步后回报：合同版本和来源 SHA、前端提交 SHA，以及 `typecheck`、单元、真实 HTTP 集成、`smoke:browser` 各自的结果与限制。**合同已接受、mock 可用、后端实跑、浏览器通过**分别记录，互不替代；纯文档任务按实际影响做文档及合同检查。
- 本后端按用户要求**一步一个提交，有远程则同步推送当前工作分支**，按路径暂存并保留他人改动。前端提交／推送遵循其用户指示，由 Claude 执行。出现共享文件或多人同仓修改时，先检查 Agent Mail reservation，精确预留路径，有冲突先协商，结束后释放。
- 本机网络代理为 `127.0.0.1:7897`（Clash Verge Rev），loopback 直连；需要 root 的命令交用户执行。

---

## 9. 提交规范

一个任务算完成（`roadmap.md §17` DoD）：代码在正确的层、公共语义有文档、
**有对应测试**、不绕过撤销/重做与校验、Linux 已验证、不产生未跟踪缓存或个人资源依赖、
错误可读、改 JSON 就提升对应 schema 并测试当前格式（ADR-0050 起不新增旧工程迁移）、改渲染结果就记录差异、**相关文档同步**。

- 提交信息：中文，`type(scope): 摘要` 形式，正文说明"为什么"与验证方式。
- 提交前：`dotnet build MakoKaraGenerator.slnx`（0 警告）+ `dotnet test MakoKaraGenerator.slnx`（全绿）。
  当前按用户2026-10-08指示暂不跑全量，以零警告构建和相关定向验证替代；2026-10-09进一步暂跳过性能优化／基准／采样，两者均不自动恢复。
- 改了数据格式或渲染结果 → **同时补 ADR**（`docs/decisions.md`）；
  改了完成度 → 更新 `docs/roadmap.md` 的实现状态标注。
- 一个提交只做一件事；多件事**按路径分别 add**。
- 按用户 2026-10-03 更新的工作约定，编码任务一步一个提交；存在远程时同步推送工作分支。2026-10-08已将脚本系统、Suki迁移及三工作区／参数面板全部集成并推送`main`，后续开发以`main`为基线。`codex/ui-workspace-ownership`、`codex/ui-suki-redesign`及`codex/scripting-system`作为已集成历史分支保留；旧 `codex/scripting-system-f93b` 已删除。

---

## 10. 样式设计独立面板与批量应用（待审批方案）

**调研日期：2026-09-26。状态：方案已批准并实施第 1~3 批（见 `docs/decisions.md` ADR-0044）；
交互收尾与文档整备待做。** 本节保留为方案原文；**实现与取舍以 ADR-0044 为准**，
完成度只在 `roadmap.md` §18 维护（ADR-0030），待收尾清单在 §5.6。
2026-09-26 用户要求重新考虑样式交互；新的**未实施**提案见 `docs/style-system-redesign.md`。

### 10.1 当前实现与问题依据

| 实际入口 | 调研结果及对方案的影响 |
|---|---|
| `samples/TechnicalSpike.App/Views/MainWindow.axaml` | 主区为 `280,*,270` 三列；右栏将行文字、字体、作用范围、预设、样式、动画、合成和诊断放在长滚动区。继续堆按钮会增加查找成本 |
| `Views/MainWindow.Inspector.cs`、`ViewModels/EditorViewModel.Style.cs` | 样式控件直接调用 setter，按 `CurrentStyleScope` 写入工程/行/元素；切换选择会刷新显示值。设计过程和目标对象绑定，无法保留一份独立设计反复套用 |
| `EditorViewModel.ApplyStylePreset` | 预设始终写入工程基线，不跟随样式作用范围。新入口必须改成“载入设计稿”，避免选择预设时误改工程 |
| `EditorViewModel.Selection.cs`、`Controls/TimelineControl.cs` | 当前是活动行、活动元素、单个 `SelectedBlockIndex`；尚无可直接复用的跨行批量目标集合。文档中列出的多选需求不能当作已实现 |
| `Core/Projects/LyricStyleEditor.cs`、`LyricStyleResolver.cs` | 已有基线、行覆盖、元素覆盖及逐属性来源，可复用。批量应用要一次生成结果并提交，不能逐目标调用现有 VM setter |
| `Core/Projects/TokenStyleSupport.cs` | 元素能力已有唯一事实源：颜色族、描边、阴影、不透明度可用；字体、字号、对齐、纵向位置、缩放不可用。新面板必须继续据此判断 |
| `Core/Projects/LyricStyleOverride.cs`、`LyricStyleResolver.Apply` | 可选颜色目前用 `null` 表示继承，不能表示“明确关闭上层渐变/描边/阴影”；字体已经用 `OptionalValue` 区分继承和显式空值。完整样式复制须处理这个缺口，见 §10.6 |
| `EditorViewModel.Commands.cs` | 修改必须经 `Execute → AdoptProject`；无变化返回原工程。可用一次命令实现整批撤销，草稿编辑不能进入工程历史 |
| `MainWindow.Animation.cs`、`EditorViewModel.Animation.cs` | 动画仍有自己的范围下拉和即时写入路径；若保留，会在同一设计面板形成两套提交逻辑，建议一并纳入草稿 |

调研以代码、ADR-0038/0040/0042/0043 和 `roadmap.md` §18 为依据。
旧入口文档有滞后描述（如对齐、行复制、帧缓存等），本方案不依据这些旧描述重复开发。

### 10.2 推荐布局：右侧独立停靠面板

沿用现有 Semi 控件、主题资源、面板标题、圆角、间距和中文文案，不另做一套视觉主题。
“独立”首期指**独立视图 + 独立视图模型 + 独立草稿状态**，停靠在主窗口右侧；
不采用模态对话框，便于同时观察歌词与画布。浮动窗口、拖拽停靠系统不列入本次。

右侧用两个平级页签组织，页签仅切换工具，**不选择样式作用范围**：

- **样式设计**：设计稿来源、局部试样预览、样式参数、进入动画、批量应用。
- **属性**：当前行文字与对象信息、只读的生效样式/来源、合成与背景、诊断报告。
  预览质量移到画布标题栏附近，避免混入可应用的样式属性。

示意布局（文字是结构示意，实际文案统一放进 `Resources/Strings.axaml`）：

```text
歌词列表            工程画布                  右侧面板
行 / 元素           已应用的工程结果           [样式设计] [属性]
                                             设计稿 · 尚未应用
                                             [载入预设] [从当前对象读取]
                                             ┌ 试样预览 / 进度 ┐
                                             └─────────────────┘
                                             ▾ 文字与布局
                                             ▾ 未唱 / 已唱填充
                                             ▸ 描边与阴影
                                             ▸ 不透明度与缩放
                                             ▸ 进入动画
                                             ── 固定底部 ──
                                             应用内容：已勾选 6 项
                                             [应用到工程默认]
                                             [应用到行…] [应用到元素…]
底部时间轴                                    结果与不可用原因
```

- 设计参数分组折叠，应用按钮固定在底部；错误提示贴近对应字段，不能只写到状态栏。
- 右栏默认宽度建议约 340 逻辑像素，可拖动调整和折叠；在 1600×1000 与当前最小窗口
  940×640 下分别验收。窄窗口折叠非必要区域、按钮换行，不能遮挡提交按钮或挤没画布。
- 当前对象的生效值与设计稿值必须有明确标题。切换行/元素只能更新对象信息，不能覆盖草稿。

### 10.3 设计稿：先编辑，再应用

1. 首次打开面板，从当前工程基线初始化一份设计稿。以后切换页签、播放、选择对象均保留草稿。
2. 内置预设改为显式“载入设计稿”；也可分别“读取工程默认”“读取当前行”“读取当前元素”。
   读取的是解析后的生效值，并显示来源。只允许从单个明确对象读取，不混合多个目标的值。
3. 草稿包含完整静态样式、可选进入动画、参与应用的属性集合；这些都是编辑会话状态。
   普通输入只修改草稿：工程引用、保存脏标记、自动保存内容、工程历史和主画布都保持原样。
4. 局部试样使用独立临时快照与既有渲染管线，可查看未唱、进行中、已唱和动画进度；
   无歌词/无音频也能用内置样例。试样文字和试样进度不写入工程。
   可手动载入当前行作为试样，但不因活动选择变化自动换掉试样。
5. 试样默认展示设计本身；批量目标区域同时说明继承和子层覆盖的影响。
   主画布始终展示已提交工程，不把试样当作最终成片效果；应用后再以主画布核对实际结果。
6. 数值输入回车/失焦接受，滑块即时更新试样；非法输入保留原文并阻止应用，不能夹值或静默忽略。
   点击应用时先处理当前输入框尚未提交的编辑，再校验并生成应用摘要。
7. 应用完成后保留设计稿，允许继续套用给其他对象；显示“最近应用到 3 行”等反馈。
   “已应用”不能等同于“所有对象都与草稿一致”。
8. 首期只维护一份会话草稿，不建可命名用户样式库、不引入 `styles.json` 或动态样式引用。
   面板收起不丢草稿，工程保存不保存草稿，界面常驻说明“设计稿仅在本次会话保留喵”。
   重置/载入替换有改动的草稿时提供放弃确认；关闭应用或切换工程时，对尚未应用的修改提供
   “返回设计 / 放弃草稿”，不能代用户自动应用。已应用的工程内容按原保存流程处理。

### 10.4 批量目标与应用内容

**不再维护“当前样式作用范围”下拉状态。** 三个按钮把目的写清楚；
目标只决定本次提交写到哪里，不改变设计控件含义。

| 操作 | 目标与写入规则 |
|---|---|
| 应用到工程默认 | 更新工程基线的勾选属性；保留现有行/元素覆盖。提交区说明“已有局部覆盖仍优先”，不能宣称整首全部变成同一样式 |
| 应用到行… | 展开行勾选清单，可跨行多选、全选、清空；当前活动行作为首次默认项。确认后只改勾选行的相应覆盖，元素覆盖保留 |
| 应用到元素… | 展开按行分组的元素勾选清单，可跨行勾选、按行选全部元素、全选、清空；当前活动元素作为首次默认项。未打时间的元素同样可选 |

- 清单在同一面板中展开，显示行号、歌词摘要、元素位置及选中数量，提交前可检查具体目标。
  选择清单不自动改变播放头。首期不扩展时间轴多选拖动，也不将循环选区当作样式目标。
- 同一批次只写一种层级，防止同时选中父行和子元素造成难以理解的重复覆盖。
- 使用独立的批量勾选状态，与现有打轴活动选择区分；不能把活动对象切换当作清空批量列表。
  清单绑定当前工程及结构版本，行增删/重排、元素拆合、歌词导入、结构撤销/重做后清空失效目标并提示；
  仅改时间或样式时保留仍有效的勾选。首期不为此新增持久化 ID。
- 应用前再次检查目标及版本；失效则整批拒绝，不能按旧下标误写其他对象，也不静默跳过部分目标。

**应用内容采用逐属性勾选，分组提供全选/清空。**
普通编辑自动勾选改过的属性；初始化默认不勾选，载入预设默认勾选全部静态样式属性，
读取对象默认不勾选（用户可以“全部静态样式”一键勾选）。动画始终单独勾选，避免套颜色时顺带改动画。
渐变起止、描边及阴影等相关参数可成组勾选，提交摘要列出实际会修改的属性。

- 默认**合并勾选属性**：只替换这些属性在目标层的值，其余覆盖保留；不会把未勾选的生效值固化。
- “全部静态样式”是明确的全量复制操作，会把该层支持的全部静态属性固化为覆盖，
  界面要说明这些属性以后不再跟随上层变化；它不清除子层覆盖。
- 元素目标遇到字体/字号/位置/对齐/缩放或动画时，在提交区列出具体不支持项并禁用提交。
  提供显式“仅勾选元素支持的属性”操作，用户点击后重新显示内容摘要；不能后台偷偷过滤，
  更不能把不支持的元素修改转写到所在行。
- 没有目标、没有勾选属性、输入非法时禁用提交并说明原因。
  真正提交是一个按钮动作，不对普通应用再弹一次重复确认。
- 提供独立次要操作“所选属性恢复继承”：只移除选定行/元素的指定属性覆盖，不作用于工程基线，
  不清空其他属性、不递归清空子层。操作前同样展示目标与属性摘要，并支持一步撤销。

### 10.5 动画、继承与撤销

- 现有进入动画移入设计稿分组，沿用三通道、四种缓动及原时间语义；动画预览使用同一
  `LineAnimationEvaluator`。移除旧动画范围下拉和即时写入入口。
- 首期动画按一整组应用（不是逐通道混合）：可应用到工程默认或批量行，元素不可用。
  应用行独立动画写入已有 `LyricLine.EnterAnimation`；“恢复继承”清除行动画覆盖。
  行的 `null` 仍表示跟随工程，不能标成“本行不动画”；首期不增加“单行禁用上层动画”的新契约。
- 一次点击应用，包括静态样式与勾选动画，是**一条不可部分成功的工程命令**。
  先检查所有目标及结果，成功后一次 `Execute`，由 `AdoptProject` 统一刷新。
  失败不改任意目标；无实际数据变化返回原实例、不新增历史、不污染脏标记。
- 工程撤销/重做恢复整批结果，草稿及试样不随工程历史改变；连续两次应用是两步，不参与滑块合并组。
- 草稿提供“重置为本次载入状态”；首期不增加完整草稿撤销栈。输入框内 Ctrl+Z 沿用文本撤销，
  面板非输入区域的 Ctrl+Z 仍撤销工程，按钮与提示明确标为“撤销工程应用”，避免误认成撤销草稿滑块。
- 来源展示保留在属性页，显示当前对象的实际解析来源。批量目标来源不一致时显示“多种来源”，
  不能以第一项的来源冒充整批来源。

### 10.6 需审批的数据语义补齐：继承与明确关闭

**建议随本次批准：为覆盖中的 6 个可选颜色字段引入三态。**
它们是未唱/已唱渐变起止色共 4 项、描边颜色、阴影颜色。

| 状态 | 语义 |
|---|---|
| 未设置 | 继承上层，由“恢复继承”产生 |
| 设置为颜色 | 覆盖上层，由应用设计稿产生 |
| 明确设置为空 | 本层关闭对应可选效果，由应用设计稿中的关闭状态产生 |

建议复用 `OptionalValue<RgbaColor?>` 表达，不新增样式层级；解析、来源、`IsEmpty`、
`TokenStyleSupport.SetProperties`、校验、DTO 和渲染覆盖判断必须同步适配。
新 JSON 明确区分属性缺失（继承）与显式 `null`（关闭），序列化器不能省略后者。
`project.json` 和 `lyrics.json` 各自升一个版本、分别登记迁移；旧文件里这些覆盖字段的
缺失或 `null` 都迁移为未设置，保持原渲染结果。基线的可空颜色语义不变。

例：工程默认是渐变，将纯色设计应用到某行后，该行真的改为纯色；
将该行渐变属性恢复继承后，又跟随工程渐变。这两个操作不能都写成同一个 `null`。

**如果用户不批准数据格式调整**：可以先做面板、草稿及批量应用，但对无法表达的“明确关闭”
必须拒绝并提示，不能以继承或透明色冒充关闭；该方案会限制完整样式复制，需明确作为降级版批准。
本节不是已批准的 schema 变更，实施前以审批结论为准。

### 10.7 实施拆分与代码落点（批准后执行）

不新建项目或依赖层，不升级组件；下列类名是拟定名，可在现有层内按代码规范调整。

| 批次 | 工作及落点 | 本批验收 |
|---|---|---|
| 1. 契约与领域批量操作 | 按审批补 ADR；在 `Core/Projects` 增加纯批量应用/恢复继承操作，显式携带目标、属性集合和操作类型；完成 §10.6 模型、解析与两条迁移 | 目标去重、原子性、合并/全量语义、原实例返回、旧文件兼容、显式关闭都可测 |
| 2. 草稿与目标状态 | App 内新增 `StyleDesignerViewModel`、草稿/勾选状态；复用 `EditorViewModel.Commands` 和 Application 的历史机制，统一目标校验 | 编辑不改工程，选择不覆盖草稿，一次应用一步撤销，结构变化不会误写 |
| 3. 独立面板与试样 | App 内新增 `StyleDesignerPanel` 视图；拆出右侧属性页，重接样式、预设、动画事件；试样使用独立快照/渲染缓存实例 | 无旧范围下拉和即时样式写入路径；无音频可设计；试样与主画布数据隔离 |
| 4. 批量清单与交互收尾 | 行/元素勾选清单、内容摘要、能力限制、继承操作、焦点/键盘、输入提交、草稿生命周期；文案与颜色资源化 | 跨行批量应用、继承提示、非法值及空目标处理完整；小窗口可用 |
| 5. 集成验证与文档 | Core/VM/渲染测试、必要界面集成测试、Linux 启动与人工验收；更新 `development.md`、`timeline-ui.md`，实施状态只在 `roadmap.md` §18 维护 | 全量构建 0 警告、全量测试通过；记录实际完成情况与渲染差异，不提前宣告完成 |

旧 `SetStyle*`/范围分派的去留以调用点为准：迁移后不得保留界面可达的旧即时修改路径，
不为兼容旧测试而留两个用户入口。原有领域能力和回归断言迁移到新提交路径，不能直接删测试来过关。

### 10.8 验收清单

- **草稿隔离**：改字号/颜色/预设/动画后，工程引用、脏标记、历史、主快照不变；试样确实变化。
  保存/导出只读取工程快照；关闭面板再开草稿仍在；选择对象不会覆写未完成输入。
- **批量正确**：跨多行应用到若干元素时，只改选定元素且不改行覆盖；应用到若干行时保留其元素覆盖；
  应用工程默认时保留局部覆盖。未勾选属性、时间、歌词、背景和合成参数保持不变。
- **语义可见**：只改一种颜色不会固化其他属性；全部应用会明确固化支持项；
  元素不支持项会阻止提交；从预设载入不再直接改工程；恢复继承有真实来源变化。
- **三态往返**：纯色覆盖上层渐变、关闭描边/阴影、恢复继承均有解析与像素断言；
  新文件保存重开不丢显式空值，两条旧版本迁移不改变原画面。
- **历史与异常**：一批应用及恢复继承均一步撤销/重做；两次应用互不合并；
  无变化不新增历史；非法值/失效目标整批不提交。属性通知也要断言，不能只读派生属性。
- **渲染与交互**：固定字体的像素测试证明目标区域改变、其他区域保持预期；
  相同快照和时刻仍走同一采样/渲染规则。试样与工程缓存不串用，切换工程不会带入旧目标。
- **真机检查**：深浅主题、1600×1000 与 940×640、面板折叠/调宽、滚动区与固定底部、
  TextBox 失焦提交、点击按钮时的最新输入、中文输入和快捷键焦点；无法自动注入的交互明确交给人工验收。
- 回归测试按 §7 要求做对应变异检查；按项目规定执行
  `dotnet build MakoKaraGenerator.slnx` 和 `dotnet test MakoKaraGenerator.slnx`。

### 10.9 本次请用户审批的四项

1. **布局**：采用右侧独立停靠的“样式设计 / 属性”页签，保留当前视觉风格；首期不做浮动窗口。
2. **工作流**：一份会话草稿 + 独立试样 + 明确应用按钮，行/元素用勾选清单批量选目标；
   默认按勾选属性合并、保留子层覆盖，现有进入动画也改为先设计再应用。
3. **数据契约**：批准 §10.6 的可选颜色三态及两条 schema 迁移，使关闭效果与恢复继承真正不同。
4. **范围与历史**：一次应用/恢复继承对应一步工程撤销；草稿不进工程历史、首期不跨会话保存；
   用户样式库、浮动停靠、时间轴多选拖动、递归清空所有局部覆盖均留到后续明确需求。

以上是具体推荐方案，用户可以整体批准或按编号修改；**审批通过前，本节不构成实施授权**。

> **2026-10-24 已保存工程图片导出实现与合同登记（产品0.101.0／合同1.35.0，fresh HTTP待验收）：** 独立 `project-image-export-v1`／KIE1 Worker能力已接入既有export-jobs；仅无预设static/static-analysis/direct-package基础源，package仅无音轨。完整输入4194272字节（4MiB−32）、metadata32KiB、图片2MiB/4096/BGRA16MiB/base4MiB/source200000、媒体64MiB/音频16MiB/报告256KiB；job.input与exportInput的taskProfile/baseSourceProfile成对登记，旧导出保持省略。生产实现`1276982`已稳定推送，合同为87路径／97方法／481 schemas／2890例（2199反例）；fresh HTTP尚未验收。见[计划](../plans/backend-project-image-export-plan.md)、[统一API§11.5](../../architecture/backend-api.md#已保存工程image背景导出01010合同1350http待验收)、[实施§79](backend-extraction-progress.md#已保存工程image背景导出合同与实现登记产品01010合同1350http待验收)与[ADR-0238](../decisions/decisions.md#adr-0238)。
