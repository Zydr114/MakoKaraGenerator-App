# 离线音频分析 Worker 私有合同

> **0.70.1导航：** Backend create／open默认媒体准备的已确认引用卫生及未发布候选释放已按[实施§25](archive/history/backend-extraction-progress.md#b4-self-contained-preparation-0701)完成有限出口；此独立准备政策不改本私有analysis合同、预算、profile或原默认Media行为，也不扩大音轨export。上方0.70记录的未修状态是该批历史截点。

> **0.70增量：** 原0.67 WAV有限合同正文保历史范围；当前新增fixed-compressed-audio-analysis-v1、MP3 LayerIII／native FLAC明确AudioProfile及analysis-compressed-audio-v1私有执行。旧WAV精确SourceDurationUs／AudioProfile省略兼容，新压缩duration未知省略且必须显式AudioProfile；grant只存profile、demuxer中央派生。BCL轻候选识别、Worker实际codec/channel/rate／强格式file,pipe与严格-xerror／err_detect explode不发布partial；合法帧删尾可能有效，不承诺查全截短。真实engine＋3audio／2source probe禁用旧0.69 Worker，完整canonical／预算／恢复固定allowlist保持。551／551、HTTP152／1255与声明sourceSHA／授权grant区别、两工具失败及旧create/open autodetect已证未修的有限卫生范围见[实施§24](archive/history/backend-extraction-progress.md#b4-compressed-analysis-0700)／ADR-0214；当前API只维护[统一规范](architecture/backend-api.md)，带音轨export不扩大。


执行 profile 为 `analysis-pcmwav-v1`，公开来源 profile 为 `pcm16-wave-analysis-v1`。本能力只分析 Host 固定的音频授权，不传工程快照、不扩大静态工程 codec，不接受 HTTP 路径／工具参数，也不自动把结果写入最新工程。共享 jobs／scheduler、规范结果 sidecar、读取和恢复由 Host 管理；应用工程另需精确来源核对及领域命令，本批不开放 apply。

算法完整调用原 `AudioFeatureAnalyzer`，以原 `ToSourceData` 桥接和 `AudioAnalysisJson` 编码返回。`bass-pulse-rbj-v1`、48kHz 单声道、960 个实际样本／20ms hop、低频／Pulse／近似节奏／confidence、短尾、double 精度、源文件 SHA／规范 PCM SHA 与修订计算均保持。它不是精准拍点、完整 FFT 或高潮识别服务。原 Analyzer／Builder 默认 API 和算法未修改。

输入仅为 Host 已持有租约的自包含 PCM16 mono／stereo WAV，采样率 1..192000Hz，文件至多 16MiB、精确采样帧派生时长至多 300 秒。Host 与 Worker 复用纯 BCL `PcmWaveInput` 结构检查，Worker 另检查文件身份／非链接路径、大小、使用前后 SHA。源文件声明不授予 IO；私有 grant 才携受信路径、大小和原源 SHA。媒体探测和实际重采样强制 WAV 解复用及 file／pipe 协议白名单。源音频不复制，整曲 float PCM 不保存在内存。

实际解码上限为 14,400,000 个 48kHz mono 样本、15,000 个完整 hop。reader decorator 在原 Builder 接收每块 PCM 之前检查累积预算；超限抛非 IOException 的稳定 `WorkerAnalysisBudgetExceeded`，不会被原 Analyzer 的 IO→Unavailable 规则变成普通不可用，也不通过丢尾／补零／部分结果满足预算。取消保持 OperationCanceledException。失败或无音频流没有可用结果文件或虚构静音序列。

原解码器固定 byte64KiB／float64KiB 复用缓冲，加 Builder hash16KiB；57,600,000 字节是最大累计 PCM 流量，不是整曲 PCM 内存分配。hop、SDK 点、Core 源数据及规范文件均按上述条数有界，FFmpeg 与整个 Worker 的 RSS 不由这些缓冲上限作硬保证。stderr 独立有界保留并持续排水，防止日志增长或满管道。

私有 `task-audio-analysis-v1` 封包使用 32 字节 KAN1 header，严格 metadata 至多 32KiB，携 task GUID、执行 profile、私有音频 grant 和受信工具位置。它没有工程 snapshot。`inputSha256` 覆盖该封包，用于本次调用关联；`sourceSha256` 只等于原 WAV SHA，二者不能当作同一身份。结果 payload 是原完整规范 JSON 净字节，`outputSha256` 等于规范文件 ContentHash。PCM SHA 是原完整小端 float 流 SHA，语义 Revision 和 ContentHash 保持原不同职责。

完整净结果至多 4MiB，与既有 payload 上限一致；framed JSON 的 8MiB 上限足以容纳其 base64。固定规范字段按每个 double 预留 32 个 ASCII 字符、每点 201 字节、15,000 点及完整 header／分隔符计算，保守上界为 3,030,393 字节；仍执行实际 byte cap，不截断。Host／Proxy 的 `AnalysisResultCodec` 先检查字节／条数／样本预算，再调用原 `AudioAnalysisJson.Read` 核对网格、有限值、当前算法、规范重编码、源 SHA、PCM SHA、语义修订和完整内容 SHA。没有第二套分析算法或非规范重序列化结果。

进度来自实际 decoder callback 在原 append 成功后的采样数；正常约每 4,800 样本报告，容量一 DropOldest channel 加唯一 pump 防止累计历史。stage 为 validating-source、probing、decoding、finalizing-analysis、prepared，可因合并跳过阶段但不能倒退。解码期间 TotalSamples=null，TimeUs 按实际已解码样本向下取整；原 Complete 和完整结果完成以后，prepared 才有实际 TotalSamples 及向上取整短尾时长。进度不携最终点或未完成归一化值。每条消息严格关联 task／input SHA／source SHA、递增序号、采样计数和阶段；准备完成的终态必须与最后实际进度及完整结果计数一致。晚到消息不能复活已完成调用。

能力探测独立于视频导出编码器和 FSIZE。Host 必须获得实际 Worker privategroup／child-subreaper 支持，再在独立 Worker 启动受信 FFmpeg／ffprobe 做有界启动探测。`ToolStartupVerified` 只表示工具能启动，不声称已完成任意输入的解码验证。此任务不设置导出专用 RLIMIT_FSIZE，当前进程组回收保证限定 Linux-x64。

deadline 默认 60 秒，Host 可信配置可缩短到 1..60；控制写入及取消回执共用 2 秒宽限。期限耗尽为 `WorkerAnalysisTimedOut`，调用方取消仍为取消；没有实际 Runtime 作者发生时不附假 RuntimeDiagnosticSnapshot。失效流必须关闭并终止／排水拥有的 Worker 组，父 Worker 提前退出也回收其真实媒体子进程。中立执行器只有在 pipe task、stderr、FFmpeg 和 Worker 释放后才返回规范净字节或失败；之后 Host 才能进入唯一公开 committing 闸门发布结果 sidecar。

有限回归类为 `WorkerAnalysisIntegrationTests`（真实 Worker 与原 Analyzer 的完整 canonical／双 SHA／全部 hop 比较、取消后重试及授权失败）、`BoundedAnalysisReaderTests`（原 Analyzer 实际执行预算／取消，明确 controlled decoder seam）、`WorkerAnalysisContractTests` 和 `WorkerAnalysisProcessTests`（畸形关联、完整结果、真实拥有的故障进程排水与重建）。controlled 进程／reader 回归不冒充真实音频解码；真实原 Analyzer oracle 与 Worker 分别调用原媒体执行器。最终原 TRX、源码及二进制 SHA 将归档于 `artifacts/backend-b4-analysis-worker/worker-analysis-evidence-manifest.json`，生成前不据本合同宣称有限出口已验收。
