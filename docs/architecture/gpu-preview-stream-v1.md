# GPU 预览流 v1 合同

状态：**P1/P2 已实现并冻结 0.109.0 合同；P3 两次正式720p24滑动窗口均失败，显式普通调度复验仍未达标；1440p／600s未测**。P0 的限定设备有限图案只构成条件 Go；实现、单元测试、真实 HTTP、native 消费与性能各自保留证据边界。当前生产路线是同一 Worker 的普通 Skia Vulkan surface 经一次 GPU blit 至三槽 DMA-BUF，最终帧不回读 CPU；不是零 GPU copy。32 项 [HTTP 检查](../performance/evidence/production-2026-11-01/http-negative.json)、[真实 Runtime 有限矩阵](../performance/evidence/production-2026-11-01/runtime-finite-summary.json)及[真实作者 Error 在途排空](../performance/evidence/production-2026-11-01/author-error-inflight.json)分别保留其功能、交互未通过项与证据边界；持续性能详见[生产记录](../performance/gpu-preview-production-2026-11-01.md)，不能用总体帧率抵消正式滑动窗口失败。原始门禁及阶段状态见[实施计划](../plans/gpu-shared-texture-preview-plan.md)。

## 身份与授权

一个 stream 属于一个 `hostInstanceId`、一个已认证 `clientId` 和一个固定来源。所有可靠操作复用现有 bearer/client、host instance、receipt、owner 与失效语义；不接受调用方声称的 owner。`streamId` 是标识而不是凭证。创建和替换必须复用 `PreviewBackendService.CaptureAsync` 的来源修订校验、资源准备、profile 握手和 `RetentionLease`，在 writer 之外执行 Worker 启动；发布前通过原有注册门禁复核来源和所有权。排空开始前发生输入准备失败或 CAS 竞争时释放新引用并保持旧代；排空后失败进入 failed，不能承诺恢复旧代。

现有前端 SDK 继续持有原 client 的认证并执行可靠 HTTP 操作，GPU create、replace、resize、close 使用同一 client owner；不向 preload 传递该 token，也不迁移既有注册、刷新或恢复。Electron main 每次启动生成随机 `consumerId`、256 bit `consumerCredential` 和自身 PID，启动自有 Server 时使用私有 `MAKO_DESKTOP_CONSUMER_ID`、`MAKO_DESKTOP_CONSUMER_SECRET`、`MAKO_DESKTOP_MAIN_PID` 环境注入；Server 仅承认此启动绑定且绝不输出 secret。公开 bootstrap 只提供 `consumerId`。未配置此启动身份时不公布 GPU stream 能力，也不注册 claim 端点。

consumer 不是 backend client，不能修改工程或替代 owner。create 显式绑定 `consumerId`，Server 验证它是本次启动身份；main 的窄 `attach(streamId)` 只领取绑定到自身的 stream，不允许传入任意 URL、FD 或后端凭证。main 校验请求窗口及 consumer 关联；未绑定、跨 consumer、已关闭或重放的领取失败。consumer 会话断开由 Server 停流并回收 retention，前端 close 仍走可靠 API。现有 renderer 本就持有的 client token 不因此扩散给 preload。

Worker 由 Server 自有 proxy 启动，控制通道与 broker 由本机进程建立；broker 使用私有目录、权限和同 UID/PID 校验，并由父进程私下提供一次性随机握手值绑定 `streamId`、`hostInstanceId`、`producerEpoch` 和已登记 Electron main 身份。随机值不得进入 URL、renderer、普通日志或公开状态；不能仅凭同 UID 信任对方。必须验证实际对端进程身份和握手，失败即关闭并清理 FD。具体私有传递机制在实现中验证，不以环境变量明文日志或公开 HTTP 响应传递。

## 可靠操作

App 0.110.7 为 GPU stream 增加未绑定输入的备用 Worker。每 host 的两个进程名额同时包含
opening、active、spare、retiring 和排水失败的隔离对象，最多一个未使用备用；两活动流时不另开
备用或 availability helper。备用已完成私有握手、实际运行程序集加载、参考字节固定以及 Vulkan／Skia
创建和真实绘制核验；业务绑定移交原设备／上下文，不能只凭进程已启动宣称预热完成。
每个输入代次仍使用从未绑定过业务的 Worker，用过的进程排水后退出，不返回池。
备用 TTL 为 30 秒，失败退避从 250 ms 增至最多 30 秒；实际部署闭包内容变化淘汰未绑定备用。
初次业务尝试即消费准备结果，即使输入非法也不能复用该进程。关闭失败仍占进程名额，
确认所属进程退出后才能释放输入。该实现与本机有限验收属于脚本／渲染升级 M04，
不改变上面的历史 P3 持续性能未达标结论。

所有请求均携带现有 `RequestMetadata`、host instance、owner 上下文；所有响应包含 `streamId`、`streamRevision`、`inputGeneration`、`registrationState`；Worker 当前播放／失败状态单列 `workerState`。下列字段为 v1 必填：

| 操作 | 请求字段 | 提交语义 |
| --- | --- | --- |
| `CreateStream` | `source: PreviewSourceDto`, `size: PreviewSizeDto`, `alphaMode`, `api: vulkan`, `consumerId` | 固定输入、能力、内存及 Worker 均成功后注册；失败不公开半成品。 |
| `ReplaceInput` | `streamId`, `expectedStreamRevision`, `source` | 比较 revision 并固定新输入，排空旧代后切换至新 `inputGeneration`。 |
| `Resize` | `streamId`, `expectedStreamRevision`, `size` | 排空旧池后建立新 generation；不得覆盖还被消费的旧池。 |
| `CloseStream` | `streamId`, `expectedStreamRevision` | 幂等有界排空；关闭后拒绝新控制与帧。 |
| `ReadStreamState` | `streamId` | owner/host 校验后返回权威状态、epochs、最后应用控制和有限计数。 |

新增路由及 DTO 已由 Server/Contracts 冻结并生成到 `backend/web/`，App 前端副本在 `frontend/docs/backend/`；原 PNG API、尺寸限制和导出合同不变。重复 request ID 使用现有 receipt 的相同负载幂等、不同负载冲突；revision 不匹配失败，不得先改变旧代。每 client、host 的 stream 数、固定资源、Worker 数和双代过渡内存须有明确硬预算；能力和分配之前检查 dimensions、实际 allocation 与总额，失败清理引用。第一版参数仅在能力响应声明实测支持的值，不能假定 1440p 可用。

已冻结的 HTTP 形状（现有授权 middleware 和 `RequestMetadataDto` 保持适用）：

```
POST   /api/v1/gpu-preview-streams
  { source: PreviewSourceDto, size: PreviewSizeDto, alphaMode, api: "vulkan", consumerId, metadata }
  -> { streamId, streamRevision, inputGeneration, registrationState, workerState?, ... }
PUT    /api/v1/gpu-preview-streams/{streamId}/source
  { expectedStreamRevision, source: PreviewSourceDto, metadata } -> StreamState
PUT    /api/v1/gpu-preview-streams/{streamId}/size
  { expectedStreamRevision, size: PreviewSizeDto, metadata } -> StreamState
DELETE /api/v1/gpu-preview-streams/{streamId}
  { expectedStreamRevision, metadata } -> StreamState
GET    /api/v1/gpu-preview-streams/{streamId} -> StreamState
```

`PreviewSourceDto` 原样保留 `kind`、`sourceId`、`sourceRevision`、可选 `templateSample` 和 `authorSourceRevision`，实际支持种类以现有 capture 校验为准。`api: vulkan` 是此流的显式原生 API；若内部调用现有 `renderBackend` 选择器，则映射为现有公开合同的 `gpu`，不改写原 API 枚举。`streamRevision`、`inputGeneration`、`producerEpoch`、`poolGeneration` 和 `slotEpoch` 用规范非负十进制字符串；native producerEpoch/poolGeneration 为 uint64。`frameSeq`、`controlSeq` 为非负 JS safe integer，溢出前停止并重建流。main 使用 `POST /internal/gpu-preview-streams/{streamId}/claim`，body 为 `{ consumerId, hostInstanceId, expectedInputGeneration }`，以 `X-Mako-Desktop-Secret` 私有 header 认证；Server 恒时比较 secret，并检查 consumerId、host、stream 和当前输入代次绑定，每个输入代次只领取一次。成功换代后新代可重新领取；旧代的延迟请求不能领取新代。返回 `{ producerEpoch, workerPid, brokerEndpoint, handshake }` 仅供 main。native channel 校验 `SO_PEERCRED` 等于启动 main PID 和 nonce。异常和拒绝不得记录 secret、header 或 handshake。

v1 初始硬预算：每 client 和每 consumer 最多 1 个 stream、每 host 最多 2 个 stream/Worker；每池恰好 3 槽；最大 dimensions 2560×1440，宽高下限 64，均以 capability 与实际格式能力进一步收紧；每池总实际 allocation 不超过 64 MiB，每 host GPU 池合计不超过 128 MiB。替换和 resize 先停止并 drain 旧代后建立新池，不重叠双代；失败显式进入 failed，不恢复已销毁的旧代。创建前预留预算、按驱动实际分配复核、失败回滚；资源闭包继续受已有输入预算约束。控制每流最多一个执行、一个最新待处理，native packet 最多 16 KiB / 4 FD。目标覆盖 1280×720 24 fps 至 2560×1440 30 fps；`dimensionsSupported` 是实际格式与分配探测结果，`performanceValidated` 必须由生产链路实测证明，未测量时为 false，目标不是承诺。

私有 claim 回包额外携带 `inputGeneration`、`poolGeneration` 和 `size`，供 main 复核代次和尺寸。Linux broker 路径为 `/tmp/mkg-gpu-<24位小写十六进制>/broker.sock`；main 验证私有父目录的属主和 `0700` 权限，连接还须校验进程身份与 nonce。公开的 `registrationState` 仅表示服务端登记状态，不是 Worker 播放状态；Server GET 通过私有 `ReadKind`／`ReadResultKind` 读取同一 Worker 原子快照并放入 `workerState`，native `read-state` 复用该内部状态，不能由两侧猜测播放状态。`alphaMode` 沿用现有选项，只控制输出背景是否透明，GPU 传输纹理为 BGRA 预乘 alpha，公开 `textureAlphaMode: premultiplied`。

## 内部控制合同

同一 Worker 有一个固定 Runtime，会话打开时从 `BackendPreparedProjectRuntimeInput` 的受哈希保护字节和既有 `ProjectRuntimeInput` 路径创建；包、图片和音频固定闭包及 profile 检查与静态预览等价。P1 独立流注册表拥有 proxy 和输入 retention，P2 共用 `ProjectRenderSession` 的帧准备、诊断与绘制，不复制 Runtime。

Host 先以既有 `ProjectRuntimeInputWorkerContract.OpenKind` 打开固定输入，随后发送 `GpuStreamWorkerContract.OpenKind`：`GpuStreamWorkerOpen { streamId, hostInstanceId, inputGeneration, width, height, transparentBackground, maximumAllocationBytes, expectedMainPid, brokerPath, handshake }`。成功的 `OpenResultKind` 带 `GpuStreamWorkerOpened { streamId, producerEpoch, poolGeneration, width, height, slotCount, allocationBytes, workerPid, vendorId, deviceId, drmRenderMajor, drmRenderMinor, deviceUuid }`，其中 epoch 为十进制字符串；只有真实 allocation 成功才可注册流。关闭以 `CloseKind` 发送 `GpuStreamWorkerClose { streamId, producerEpoch, inputGeneration }`，`ReadKind` 携带 `GpuStreamWorkerRead { streamId, producerEpoch, inputGeneration }`，返回 `GpuStreamWorkerState { state, lastAppliedControlSeq, timelineEpoch, timelineResetId, mode, effectiveMediaTimeUs, failureCode, drained, diagnostics }`。open/read/close 外层序号严格递增；`CloseResultKind` 的 `GpuStreamWorkerClosed { streamId, producerEpoch, drained: true }` 证明该流没有未归还消费者租约，随后固定 Runtime close 才执行池与设备清理。控制与帧在 native channel，不能混入 Host/Worker 单帧 receipt。

控制采用私有可靠有界通道，不经逐帧 HTTP receipt 或 SSE journal。以下为语义名称；实际 wire `type` 使用 `hello`、`hello-accepted`、`clock-ping`、`clock-pong`、`clock-calibrated`、`set-playback-state`、`control-applied`、`frame`、`release`、`read-state`、`state`、`stop`、`stopped`、`failed` 小写形式。所有通用控制身份包含 `protocolVersion: 1`、stream/host/producer/input/consumer，时钟特殊包以已绑定连接限定。真实序列化 fixture 位于 `backend/tests/MakoKaraGenerator.Backend.Worker.Tests/Fixtures/GpuStreamWorkerWire.json`，单测对照生产 hello/frame serializer。

控制形状：

```
SetPlaybackState { streamId, hostInstanceId, producerEpoch, inputGeneration,
  controlSeq, mode: paused|playing, mediaTimeUs, rate, targetFps,
  timelineResetId, clockSample: { mediaTimeUs, senderMonotonicNs } }
ControlApplied { streamId, producerEpoch, inputGeneration, controlSeq,
  timelineEpoch, mode, effectiveMediaTimeUs }
```

`controlSeq` 严格递增，最新序号同内容可重放，最新序号不同内容及更旧序号均拒绝；至多一个执行中和一个最新待处理状态，后者可覆盖。seek/循环通过改变明确的 `timelineResetId` 提升 `timelineEpoch`，不能根据时间回退推断；普通时钟校正允许轻微回偏而不重置。普通 2 Hz 校正允许已在途的同时间线帧完成，帧保留实际捕获的 controlSeq；pause/play、rate、FPS 或明确 reset 构成显示硬屏障。调度基于绝对单调 deadline，迟到时取最新目标，不在 render 后追加完整帧周期延时。不得跳过 Runtime 必要状态推进。`producerEpoch` 为 Worker 实例随机身份；replacement、resize 增加 `inputGeneration`，进程重启不得继承旧 epoch。`senderMonotonicNs` 为十进制字符串；单调时钟需握手估计映射及不确定度，不跨进程直接相减；无法建立时钟时只允许暂停定点帧。对外状态区分 accepted、applied、frame-ready 和 consumer-released。

私有 native `state` 的 Worker 遥测独立于 Electron main：`executingRender` 表示尚未收割的单个 render Task，`pendingControls` 是带锁 mailbox 的真实占位，`pendingRenderTargets` 只表示尚待重试的最新暂停目标，不把错过的播放 deadline 当队列。三者为 0／1；`outstanding` 是实际已导出未归还租约数，`peaks` 保存各自实际峰值。`slotCount`、`actualAllocationBytes` 来自真实池信息；`retainedAllocationBytes` 为初始化时该固定 Runtime 所有保留池分配总量，此 stream 固定 target 期间不再创建池。公共 Worker DTO 保持原形状，不能拿 main 的控制计数替代 Worker 遥测。

## 帧与生命周期合同

同一 VkDevice/queue 的 allocator 与 Skia session 使用 typed target lease；不得跨设备使用 P0 独立 producer 的 image。已冻结 C ABI v1 与 managed `IGpuRenderFrameSession`／`GpuFrameLease`，包含 device/pool/slot 身份、image、尺寸、format、当前 layout 和 queue family、ownership、错误与取消；图像绝不进入 Server JSON。普通 Skia surface 是 BottomLeft，GPU blit 同时完成 Y 翻转；描述符为 BGRA8888 premultiplied、sRGB、TopLeft、linear modifier 0。一次 GPU copy、最终 CPU readback 和 queue-idle 次数随帧计数；目标最终 CPU readback 为零。

首版同步模式固定为 Skia 同步 submit 后执行一次 GPU blit，检查 `vkQueueWaitIdle`，以 `GENERAL/FOREIGN` 释放目标后发布；每帧 fresh import，正常消费者的最终引用与 GPU 完成后才归槽。此模式会等待 CPU，但不读回像素；P3 必须计入等待成本。Electron 尚无已验证的外部 acquire fence，不能将未经验证的 semaphore 或 implicit fence 冒充等价方案。

```
FrameReady { streamId, hostInstanceId, producerEpoch, inputGeneration,
  timelineEpoch, consumerEpoch, poolGeneration, slotId, slotEpoch, frameSeq,
  timeUs, width, height, format, colorSpace, alphaMode, origin,
  modifier, planes: [{ fdIndex, stride, offset, size }], sync,
  controlSeq, timelineResetId, renderStartMonotonicNs, producerReadyMonotonicNs,
  provider: runtime|synthetic-gpu, gpu, counters, diagnostics }
Release { streamId, hostInstanceId, producerEpoch, inputGeneration, consumerEpoch,
  timelineEpoch, poolGeneration, slotId, slotEpoch, frameSeq }
```

FD 仅随本地 native channel 的 `SCM_RIGHTS` 传递，JSON 的索引不是 FD；验证设备、plane 边界、格式、descriptor 数及身份后导入。每个固定池最多三槽；每槽 `Free → Writing → Ready → ConsumerUsing → Retiring → Free`，不能借 send、draw 或 JS close 判定 GPU 完成。正常消费仅在 Electron 全引用释放且 GPU 同步成立时发布 release；无空槽时合并目标而非扩池。分配失败、设备不匹配、同步未知必须失败且不做 CPU fallback。

每帧捕获实际执行开始／GPU Ready 单调时间与应用控制身份；Ready 发布前遇到实际 Error 诊断则取消该未导出帧。frame/state/failed 携带真实计数和最多四项文本的有界诊断摘要，字段截断带 `truncated`，整个 native 消息仍限 16 KiB。普通作者 Error 停止新生产并继续接收旧 lease 的 exact release，排空后 `failed + drained:true`，前端显示诊断而不退出整个应用。

真实自含作者草稿已实测该路径：1000 ms 消费者延迟下，C# `Evaluate` 报告 Error 时仍有一个旧租约，Worker 保持 draining 且不提前 ACK；精确 native completion 后才报告排空，随后同一 App 可消费新健康流。原始帧已完成后再触发 Error 的结果与该在途结果分别归档，不混作同一证据。

正常关闭/替换先停止新帧，再等待所有实际消费者和 Worker 排空，最后释放输入、池与设备。renderer/GPU 崩溃时已转移 lease 的完成状态未知，停止整个流和 Electron 会话、隔离槽位并拒绝 ACK，不自动重启或扩池；该结果是 expected fail-stop，不是安全 drain 或恢复。已交付 FD 的 payload 引用可延长 allocation 寿命，不代表 GPU 工作完成。

renderer／Worker 丢失历史注入的 Server 清理异常传播曾导致 -6 退出；源码 `f1fe7f8` 修复后，匹配发布的两类注入与两类外部丢失复验均受控退出1、自有进程消失。严格离线分析`921b943`单独复核原日志，不改写原运行来源；注入场景精确loss至fail-stop ACK差为零，外部场景仅证明before-kill观测至检测的ACK差为零。未知租约仍隔离，不能记作正常排空或安全归槽。

## 验收和交付边界

先单测合同、owner/host/CAS、资源保留、代际 ABA、背压与错误清理；再固定构建同版本 Server/Worker、验证 profile 握手和独立流运行；最后由 Electron 实测可见帧、三槽、同步、替换/关闭和崩溃。不得把 P0 图案、202 accepted、单元测试或 capability 枚举当作 P1/P2 的生产端到端验收。性能运行只能在对应生产链路通过上述门禁之后进行。

启动时只有 GPU stream Worker 注入 `MAKO_BACKEND_GPU_PREVIEW=1`；synthetic 另需受控基准启动配置，Worker 在任何作者代码运行前固定该选择，公开 HTTP／控制字段不能切 provider。renderer→main 与 main→Worker 分别校准单调时钟并登记不确定度。正常 PNG 预览与 500 ms 控制入口不变。
