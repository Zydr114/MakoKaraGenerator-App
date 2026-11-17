# 静态工程独立导出 Worker 私有合同

> **0.114.1／合同1.55.1：** 当前受管导出保留完整视频帧计划；短音轨补静音、长音轨截断，没有音频声明和受管源时统一纯视频（有效`includeAudio=false`）。源文件损坏、无法解码、授权和身份检查保留。报告`audioDurationUs`为真实源时长，`actualAudioDurationUs`为成片音轨时长，继续校验后者与视频计划的50ms容差。分析不补静音、不延长，效果沿用缺失分析语义。下文旧批次的单profile和默认预算为历史范围，当前完整行为以[统一API §11](architecture/backend-api.md#11-独立成片导出任务)为准。

> **0.71增量入口：** 固定MP3 LayerIII／native FLAC音轨导出有限出口已完成，旧WAV／纯视频保持原执行profile，新增compressed执行profile；完整strict EOF按源采样帧计数≤300秒、整数coverage、AAC／PCM16策略及nullable legacy wire／恢复门禁以[B4计划§12](archive/plans/backend-b4-plan.md#b4-compressed-export-plan)／ADR-0216为准。真实旧0.70.1Workerdisabled，重开后同源分析采用修复及新540／原186独立验证、真正HTTP385请求3560断言与最终SHA见[实施§26](archive/history/backend-extraction-progress.md#b4-compressed-export-0710)。当前HTTP只维护于[统一API](architecture/backend-api.md)；下文首批B4-B合同及0.69增量保持各自历史范围，不作为当前完整profile列表。完整B3／B4仍未完成。

> **0.69增量：** 下文首批单profile／拒分析／预算记录保留其历史范围；当前B4-E已增加完整`static-lyrics-scene-analysis-v1`，旧`static-lyrics-scene-v1`仍仅null。唯一snapshot-v1／Source自动选profile、Available canonical／Failed／NoStream完整保真，总4MiB深200k及实际wire同规则闭口，不截断。public旧sourceProfile保留并增sourceProfiles，私有export真实probe匹配EngineIdentity与双profiles，旧Worker不虚称兼容；purevideo保完整声明但禁止源音频IO，true grant重绑保Media身份，sourceSHA为原编码。preview／validation扣24／32头admission，export按实际metadata＋32在spawn/send前guard，不能承诺所有动态封包均admission拒绝。实际有限559／559及真正HTTP232请求／1864断言、三片各30完整解码帧／首帧oracle见[实施§23](archive/history/backend-extraction-progress.md#b4-analysis-static-profile-0690)／ADR-0213；当前HTTP只维护于[统一API](architecture/backend-api.md)。新profile不表示音频驱动视觉或完整B3／B4完成。


本合同限定 `static-export-pcmwav-v1` 执行能力，来源仍使用 `static-lyrics-scene-v1` 快照合同。它复用原 `VideoExporter`、`ProjectRenderSessionFactory`、FFmpeg 编码器与媒体探测器，不是完整工程或脚本导出能力；包／脚本／材质／预设应用／分析／图片和视频背景仍由静态快照门禁拒绝。HTTP、唯一任务账本和最终下载产物事务见 [Host 接缝合同](backend-export-task-contracts.md)。

Worker 在任何握手前建立 Linux 私有进程组。仅导出专用 Worker 接受 Host 注入的 `MAKO_BACKEND_WORKER_MEDIA_LIMIT_BYTES=67108864`，在启动媒体子进程前设置继承的 RLIMIT_FSIZE。握手同时回报实际组身份和已生效的限制；任一缺失，导出能力关闭。当前 native 保证仅验收 Linux-x64；单文件 64MiB 是内核文件长度限制，不是进程内存或全目录的限制。辅助 `-fs` 和 40ms 候选目录监测只负责尽早失败，完整解码帧数／时长验证防止把达到大小限额后的截短影片采用为成功。

`task-export-capabilities-v1` 在独立 Worker 中实际探测固定 FFmpeg／ffprobe，并分别报告普通 MP4 与透明 ProRes4444 编码能力。文件位置来自 Host 的受信配置；HTTP 不能提供工具位置、文件路径或启动参数。

`task-export-v1` 输入为 32 字节 KEX1 header、至多 32KiB 严格 JSON metadata，以及原净快照字节。总输入至多 4MiB，按实际 metadata 长度扣预算；输入 SHA 覆盖整个封包，独立 source SHA 仅覆盖原净快照。metadata 包含两个不同 profile、格式、是否带音轨、Host 独有候选路径、工具路径和可选音频授权。task GUID、source SHA、input SHA、格式与音轨选项在每条进度、错误和结果回执中精确关联；其他帧／会话／验证消息字段不能混入导出回执。

Worker 逐字段还原 `Project`，重新编码后逐字节核对原静态快照，随后才重绑定授权音频。它不调用会建立默认表现程序的 `Project.Create`。来源音频描述不是 IO 授权：带音轨必须另有 Host 固定的路径／SHA／大小能力，Worker 在使用前后检查规则文件、非符号链接、大小、SHA、自包含 RIFF/WAVE PCM16、mono／stereo 和精确样本时长。强制 WAV 解复用和 file／pipe 协议白名单；源音频按完整帧计划补静音或截断，源不足不拒绝。显式纯视频清除音频输入，不读取快照中的音频声明。

正式帧数使用共用 `CompositionFramePlan`，保持原 `Round(AwayFromZero)`、至少一帧以及整数帧时刻。名义和派生时长均至多 10 秒，帧率至多 60，计划至多 600 帧，画布至多 1920×1080 且本地 BGRA 不超过 8MiB；MP4 要求偶数尺寸。正式渲染固定 Final，编码固定 Standard。渲染包装器拒绝任何实际 Error、未协商诊断、错误像素形状和单帧执行失败，避免原桌面普通错误占位策略在此严格能力下产生成功影片。桌面原默认占位语义保持。

原导出器真实回调经容量一的 DropOldest channel 和唯一 pump 发送，回执保留真实单调序号及完成计数。`Committing` 映射为 `preparing-result`，`Completed` 为 `prepared`；这两项都是私有候选的进度，不能使公开任务提前进入不可取消状态。Host 校验任务／来源／选项／序号／阶段及计数；成功终态必须跟随已排水的 `prepared`，迟到进度不能覆盖已完成调用。Worker 不累计已完成操作任务，只保留有界活动操作表。

实际产物以强制 MOV 解复用和 `-count_frames` 完整解码探测。必须有匹配的 codec、Alpha 像素格式、尺寸和精确有理帧率；实际解码帧数与写入帧数均等于计划，failed／dropped 为零。产物视频时长与派生计划差至多 200ms；带音轨必须有正时长和对应 AAC／PCM codec，实际音轨与派生时长差至多 50ms（明确量化容差）。源音轨可短于画面，报告仍保留真实源时长。最后检查候选规则文件、大小和 SHA。媒体不经 IPC，结果只有脱敏完整报告和受校验的大小／SHA。

报告 JSON 至多 256KiB，warnings 至多 256，实际发生至多 512。`IsVerified` 如实保留原导出器对名义时长的验证值，`StrictlyVerified` 独立表示上述新能力校验；低帧率至少一帧可能使原值为 false，不能静默改写。完整实际诊断保留 epoch、连续 sequence、instance、artifact、stage、time、源码逻辑位置和 package 身份；成功报告不能含 Error。累计诊断在进入原导出器集合前守条数和有界 JSON 字节预算。

失败使用导出专属 `exportDiagnostics` 字段，复用发生数据类型但独立校验 512 项／256KiB，不扩大预览原 256 项／64KiB 能力。framed 协议总 JSON 上限仍为 8MiB。完整失败快照超预算时不截断、不构造替代发生；`errorCode` 保留主因，`exportDiagnosticsFailureCode=WorkerExportReportBudgetExceeded` 为次要原因。中立 `RenderSessionFailureException` 保留完整实际快照；没有真实快照的 transport 错误只携稳定 code。Host 只为真实非空快照建立 failed report-only 包，不提供视频下载引用。

默认 deadline 为 120 秒，Host 受信配置可缩短；用户取消与期限耗尽分开，后者为 `WorkerExportTimedOut`。代理发送取消后最多等待既定 2 秒宽限，再终止并排水拥有的进程组，包括父 Worker 已提前退出的子进程。调用等待 read loop、stderr、Runtime、FFmpeg 和 Worker 释放后才返回；创建阶段缺失为 `WorkerExportUnavailable`，运行期间 EOF／IO 为 `WorkerSessionConflict`，真实用户取消仍为取消。媒体候选只有在这些资源全部释放后才能交给 Host 最后的取消检查和唯一公开提交闸门。

取消和 shutdown 的宽限从控制消息写入前开始，覆盖写入及回执等待；停止读取 stdin 的 Worker 不能通过填满管道阻止强制回收。导出与正式验证共用这条控制消息边界。已有故障或取消时，额外清理失败附在原异常 `Data["WorkerCleanupFailure"]`，不改写主因；没有主故障的清理失败仍直接失败。流失效后代理不能把旧进程作为可复用的健康会话。

有限回归入口：`CompositionFramePlanTests`、`WorkerExportContractTests`、`WorkerExportProcessTests`、`WorkerExportIntegrationTests`。真实普通／透明短片测试保持 30000/1001 透明样例，核对实际解码帧、歌词像素／Alpha 和解码非静音音频；另外覆盖真实取消后重试、音频 SHA／长度门禁。故障进程探针用于私有协议和失败快照传输，不代表真实脚本运行验收。最终计数、原 TRX 终态和源码／二进制 SHA 见 `artifacts/backend-b4-export-worker/worker-export-evidence-manifest.json`；尚未生成最终 manifest 时不据本合同宣称有限出口已经通过。
