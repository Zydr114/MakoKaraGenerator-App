# B4-B 固定静态导出接缝合同

> **0.114.1／合同1.55.1：** 当前受管导出保留完整视频帧计划；短音轨补静音、长音轨截断，没有音频声明和受管源时统一纯视频（有效`includeAudio=false`）。源文件损坏、无法解码、授权和身份检查保留。报告`audioDurationUs`为真实源时长，`actualAudioDurationUs`为成片音轨时长，继续校验后者与视频计划的50ms容差。分析不补静音、不延长，效果沿用缺失分析语义。下文旧批次的单profile和默认预算为历史范围，当前完整行为以[统一API §11](architecture/backend-api.md#11-独立成片导出任务)为准。

> **0.71增量入口：** 固定MP3 LayerIII／native FLAC音轨导出有限出口已完成，旧WAV／纯视频保持原执行profile，新增compressed执行profile；完整strict EOF按源采样帧计数≤300秒、整数coverage、AAC／PCM16策略及nullable legacy wire／恢复门禁以[B4计划§12](archive/plans/backend-b4-plan.md#b4-compressed-export-plan)／ADR-0216为准。真实旧0.70.1Workerdisabled，重开后同源分析采用修复及新540／原186独立验证、真正HTTP385请求3560断言与最终SHA见[实施§26](archive/history/backend-extraction-progress.md#b4-compressed-export-0710)。当前HTTP只维护于[统一API](architecture/backend-api.md)；下文首批B4-B合同及0.69增量保持各自历史范围，不作为当前完整profile列表。完整B3／B4仍未完成。

> **0.69增量：** 下文首批单profile／拒分析／预算记录保留其历史范围；当前B4-E已增加完整`static-lyrics-scene-analysis-v1`，旧`static-lyrics-scene-v1`仍仅null。唯一snapshot-v1／Source自动选profile、Available canonical／Failed／NoStream完整保真，总4MiB深200k及实际wire同规则闭口，不截断。public旧sourceProfile保留并增sourceProfiles，私有export真实probe匹配EngineIdentity与双profiles，旧Worker不虚称兼容；purevideo保完整声明但禁止源音频IO，true grant重绑保Media身份，sourceSHA为原编码。preview／validation扣24／32头admission，export按实际metadata＋32在spawn/send前guard，不能承诺所有动态封包均admission拒绝。实际有限559／559及真正HTTP232请求／1864断言、三片各30完整解码帧／首帧oracle见[实施§23](archive/history/backend-extraction-progress.md#b4-analysis-static-profile-0690)／ADR-0213；当前HTTP只维护于[统一API](architecture/backend-api.md)。新profile不表示音频驱动视觉或完整B3／B4完成。


本文冻结Host／Worker共享接口，实施与验收结果另记；不将接缝代码记作真实导出验收。

公共 `ExportJobRequest`：`expectedRevision`、`format`（opaqueMp4／transparentProRes4444）、`includeAudio`（默认true）、`metadata`。编码固定Standard，渲染Final。禁止请求快照、路径、输出目录、工具位置或FFmpeg参数。

HTTP：`POST /api/v1/project-sessions/{id}/export-jobs`、`GET /api/v1/export-capabilities`；继续唯一GET job／cancel／SSE；完整报告`GET /api/v1/jobs/{id}/export-result`、诊断`GET .../export-diagnostics`，流式下载`GET|HEAD .../export-content`。previousHost恢复显式使用`/api/v1/recovery-jobs/{oldJobId}/export-result`与`.../export-content`，沿既有profile恢复权限；当前live job仍严格client所有权，不回退旧宿主或孤立文件。

`ExportReportDto`保存格式／音轨、expected／decoded／written帧数、dropped／failed计数、尺寸／有理帧率、derived／actual视频时长、源音频时长`audioDurationUs`与产物音轨时长`actualAudioDurationUs`、实际codec／pixelformat／alpha、媒体大小／SHA、完整bounded warnings与typed实际发生诊断。`isVerified`保留原Exporter对nominal时长的验证值，`strictlyVerified`才是新派生计划严格门禁；低fps至少一帧可使两者不同，报告不得改写原值。不得含私有路径、栈、启动参数或凭证。实际音轨容器时长与派生视频时长差最多50ms，此为输出时间量化容差，不代表逐样本无损；源音频不足时补静音，源PCM时长仍如实记录，decoded视频帧数严格等于计划。

公共sourceProfile仍为`static-lyrics-scene-v1`，执行profile为`static-export-pcmwav-v1`。只接受真实自包含PCM16 WAV、mono／stereo，授权来源由ResourceStore固定asset id／SHA／size。Host和Worker复用RIFF结构校验器，Worker强制WAV demuxer，不允许snapshot描述路径授予任何文件能力。纯视频不给音频grant。原SourceSHA在路径重绑定前捕获，音频授权SHA独立保留；不复制音频。

中立Hosting接口由Worker作者实现：`IBackendExportExecutor.ProbeAsync`返回实际文件限制和两种编码能力；`ValidateInput(snapshot,input)`在接受前验证动态封包总4MiB；`ExecuteAsync(snapshot,input,progress,token)`返回`ExportReportDto`。`BackendExportExecutionInput`和`BackendExportAudioGrant`中的路径只由Host分配，不是HTTP DTO。Core的`CompositionFramePlan`复用原Round(AwayFromZero)/至少1帧和整数时间语义。

来源／计划限制：显式时长0..10秒（不含0）、帧率≤60、≤600计划帧、派生视频时长≤10秒、1920×1080／raw8MiB；MP4要求偶数尺寸。deadline默认120秒，可由`MAKO_BACKEND_EXPORT_TIMEOUT_SECONDS`缩短到1..120，取消宽限2秒。工具位置来自host-only `MAKO_BACKEND_FFMPEG_PATH`／`MAKO_BACKEND_FFPROBE_PATH`或受信PATH；能力在真实Worker有界探测后公布。

Linux-x64 Worker的RLIMIT_FSIZE限制单媒体文件64MiB，辅助-fs和监测不当作全目录或内存硬限制。完整报告256KiB另算。每次接受预留64MiB+256KiB，总512MiB包含queued／running预留、retained和expired active-read；输出量无法事前预测，达到媒体限制明确失败，不接受截短文件作为成功。validation／export使用唯一正式任务调度器2global／1client／1project及16global／4client queue；原持久化控制任务不占这些执行许可。

原Exporter仅发布独有私有candidate，内部Committing映为preparing-result。等待Worker、FFmpeg、Runtime全部排水后，Host验证候选并写完整报告、fsync，然后最后取消检查→唯一Job.EnterCommitting→媒体＋报告目录包一次同文件系统Directory.Move→唯一账本终态。没有下载URL或完整Job引用前不公开候选。终态账本失败沿unknown规则保留已发生提交，不重放。

产物位于StateRoot/export-artifacts。授权TTL沿JobRetention（默认1小时）；物理清理在启动及新export接受时触发，active-read继续占实际包大小且可以读完。未能删除的候选不提前释放磁盘记账。读取校验登记ref、身份、大小、SHA和固定计划；使用文件流／64KiB缓冲，不整片分配byte[]。

异常携带真实non-empty RuntimeDiagnosticSnapshot时，Host保留完整实际发生到discriminated `ExportFailureReportDto(kind=exportFailure)`：failureCode、固定source/input与diagnostics。JobResponse只保`failureReportRef`小引用，ExportResult保持null，content读取拒绝。资源排水后删除候选媒体，报告单独作为report-only包沿同一committing闸门原子提交，终态仍failed；取消先赢闸门不发布。没有真实发生的transport-only失败只保stable code，不创造snapshot／报告；512次／256KiB超限或报告准备失败附加内部原因，保原primary，不截断伪完整。previousHost只读已登记failure ref，不猜测文件恢复。
