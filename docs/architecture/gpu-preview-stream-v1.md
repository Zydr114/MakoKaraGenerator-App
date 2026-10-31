# GPU 预览流 v1 合同草案

状态：供跨模块审查，未实现、未宣称能力。P0 只证明限定设备上的有限图案，不能代替生产 Runtime 验收。本文锁定 v1 的职责与字段；审查通过后实现并以实际合同测试、能力握手和发布结果确认。

## 身份与授权

一个 stream 属于一个 `hostInstanceId`、一个已认证 `clientId` 和一个固定来源。所有可靠操作复用现有 bearer/client、host instance、receipt、owner 与失效语义；不接受调用方声称的 owner。`streamId` 是标识而不是凭证。创建和替换必须复用 `PreviewBackendService.CaptureAsync` 的来源修订校验、资源准备、profile 握手和 `RetentionLease`，在 writer 之外执行 Worker 启动；发布前通过原有注册门禁复核来源和所有权。排空开始前发生输入准备失败或 CAS 竞争时释放新引用并保持旧代；排空后失败进入 failed，不能承诺恢复旧代。

现有前端 SDK 继续持有原 client 的认证并执行可靠 HTTP 操作，GPU create、replace、resize、close 使用同一 client owner；不向 preload 传递该 token，也不迁移既有注册、刷新或恢复。Electron main 每次启动生成随机 `consumerId`、256 bit `consumerCredential` 和自身 PID，启动自有 Server 时使用私有 `MAKO_DESKTOP_CONSUMER_ID`、`MAKO_DESKTOP_CONSUMER_SECRET`、`MAKO_DESKTOP_MAIN_PID` 环境注入；Server 仅承认此启动绑定且绝不输出 secret。公开 bootstrap 只提供 `consumerId`。未配置此启动身份时不公布 GPU stream 能力，也不注册 claim 端点。

consumer 不是 backend client，不能修改工程或替代 owner。create 显式绑定 `consumerId`，Server 验证它是本次启动身份；main 的窄 `attach(streamId)` 只领取绑定到自身的 stream，不允许传入任意 URL、FD 或后端凭证。main 校验请求窗口及 consumer 关联；未绑定、跨 consumer、已关闭或重放的领取失败。consumer 会话断开由 Server 停流并回收 retention，前端 close 仍走可靠 API。现有 renderer 本就持有的 client token 不因此扩散给 preload。

Worker 由 Server 自有 proxy 启动，控制通道与 broker 由本机进程建立；broker 使用私有目录、权限和同 UID/PID 校验，并由父进程私下提供一次性随机握手值绑定 `streamId`、`hostInstanceId`、`producerEpoch` 和已登记 Electron main 身份。随机值不得进入 URL、renderer、普通日志或公开状态；不能仅凭同 UID 信任对方。必须验证实际对端进程身份和握手，失败即关闭并清理 FD。具体私有传递机制在实现中验证，不以环境变量明文日志或公开 HTTP 响应传递。

## 可靠操作

所有请求均携带现有 `RequestMetadata`、host instance、owner 上下文；所有响应包含 `streamId`、`streamRevision`、`inputGeneration`、`state`。下列字段为 v1 必填：

| 操作 | 请求字段 | 提交语义 |
| --- | --- | --- |
| `CreateStream` | `source: PreviewSourceDto`, `size: PreviewSizeDto`, `alphaMode`, `api: vulkan`, `consumerId` | 固定输入、能力、内存及 Worker 均成功后注册；失败不公开半成品。 |
| `ReplaceInput` | `streamId`, `expectedStreamRevision`, `source` | 比较 revision 并固定新输入，排空旧代后切换至新 `inputGeneration`。 |
| `Resize` | `streamId`, `expectedStreamRevision`, `size` | 排空旧池后建立新 generation；不得覆盖还被消费的旧池。 |
| `CloseStream` | `streamId`, `expectedStreamRevision` | 幂等有界排空；关闭后拒绝新控制与帧。 |
| `ReadStreamState` | `streamId` | owner/host 校验后返回权威状态、epochs、最后应用控制和有限计数。 |

新增路由及 DTO 由 Server/Contracts 共同冻结和生成合同，不能声称已发布；原 PNG API、尺寸限制和导出合同不变。重复 request ID 使用现有 receipt 的相同负载幂等、不同负载冲突；revision 不匹配失败，不得先改变旧代。每 client、host 的 stream 数、固定资源、Worker 数和双代过渡内存须有明确硬预算；能力和分配之前检查 dimensions、实际 allocation 与总额，失败清理引用。第一版参数仅在能力响应声明实测支持的值，不能假定 1440p 可用。

确定的 HTTP 草案（现有授权 middleware 和 `RequestMetadataDto` 保持适用）：

```
POST   /api/v1/gpu-preview-streams
  { source: PreviewSourceDto, size: PreviewSizeDto, alphaMode, api: "vulkan", consumerId, metadata }
  -> { streamId, streamRevision, inputGeneration, state }
PUT    /api/v1/gpu-preview-streams/{streamId}/source
  { expectedStreamRevision, source: PreviewSourceDto, metadata } -> StreamState
PUT    /api/v1/gpu-preview-streams/{streamId}/size
  { expectedStreamRevision, size: PreviewSizeDto, metadata } -> StreamState
DELETE /api/v1/gpu-preview-streams/{streamId}
  { expectedStreamRevision, metadata } -> StreamState
GET    /api/v1/gpu-preview-streams/{streamId} -> StreamState
```

`PreviewSourceDto` 原样保留 `kind`、`sourceId`、`sourceRevision`、可选 `templateSample` 和 `authorSourceRevision`，实际支持种类以现有 capture 校验为准。`api: vulkan` 是此流的显式原生 API；若内部调用现有 `renderBackend` 选择器，则映射为现有公开合同的 `gpu`，不改写原 API 枚举。`streamRevision`、`inputGeneration`、`producerEpoch`、`poolGeneration` 和 `slotEpoch` 用规范非负十进制字符串；native producerEpoch/poolGeneration 为 uint64。`frameSeq`、`controlSeq` 为非负 JS safe integer，溢出前停止并重建流。main 使用 `POST /internal/gpu-preview-streams/{streamId}/claim`，body 为 `{ consumerId, hostInstanceId }`，以 `X-Mako-Desktop-Secret` 私有 header 认证；Server 恒时比较 secret，并检查 consumerId、host、stream 绑定且每 stream 只领取一次，返回 `{ producerEpoch, workerPid, brokerEndpoint, handshake }` 仅供 main。native channel 校验 `SO_PEERCRED` 等于启动 main PID 和 nonce。异常和拒绝不得记录 secret、header 或 handshake。

v1 初始硬预算：每 client 和每 consumer 最多 1 个 stream、每 host 最多 2 个 stream/Worker；每池恰好 3 槽；最大 dimensions 2560×1440，宽高下限 64，均以 capability 与实际格式能力进一步收紧；每池总实际 allocation 不超过 64 MiB，每 host GPU 池合计不超过 128 MiB。替换和 resize 先停止并 drain 旧代后建立新池，不重叠双代；失败显式进入 failed，不恢复已销毁的旧代。创建前预留预算、按驱动实际分配复核、失败回滚；资源闭包继续受已有输入预算约束。控制每流最多一个执行、一个最新待处理，native packet 最多 16 KiB / 4 FD。目标覆盖 1280×720 24 fps 至 2560×1440 30 fps；`dimensionsSupported` 是实际格式与分配探测结果，`performanceValidated` 必须由生产链路实测证明，未测量时为 false，目标不是承诺。

## 内部控制合同

同一 Worker 有一个固定 Runtime，会话打开时从 `BackendPreparedProjectRuntimeInput` 的受哈希保护字节和既有 `ProjectRuntimeInput` 路径创建；包、图片和音频固定闭包及 profile 检查与静态预览等价。P1 独立流注册表拥有 proxy 和输入 retention，P2 共用 `ProjectRenderSession` 的帧准备、诊断与绘制，不复制 Runtime。

控制采用私有可靠有界通道，不经逐帧 HTTP receipt 或 SSE journal：

```
SetPlaybackState { streamId, hostInstanceId, producerEpoch, inputGeneration,
  controlSeq, mode: paused|playing, mediaTimeUs, rate, targetFps,
  timelineResetId, clockSample: { mediaTimeUs, senderMonotonicNs } }
ControlApplied { streamId, producerEpoch, inputGeneration, controlSeq,
  timelineEpoch, mode, effectiveMediaTimeUs }
```

`controlSeq` 严格递增，最新序号同内容可重放，最新序号不同内容及更旧序号均拒绝；至多一个执行中和一个最新待处理状态，后者可覆盖。seek/循环通过改变明确的 `timelineResetId` 提升 `timelineEpoch`，不能根据时间回退推断；普通时钟校正允许轻微回偏而不重置。不得跳过 Runtime 必要状态推进。`producerEpoch` 为 Worker 实例随机身份；replacement、resize 增加 `inputGeneration`，进程重启不得继承旧 epoch。`senderMonotonicNs` 为十进制字符串；单调时钟需握手估计映射及不确定度，不跨进程直接相减；无法建立时钟时只允许暂停定点帧。对外状态区分 accepted、applied、frame-ready 和 consumer-released。

## 帧与生命周期合同

同一 VkDevice/queue 的 allocator 与 Skia session 使用 typed target lease；不得跨设备使用 P0 独立 producer 的 image。跨 agent 交付接口在实现前共同确定 ABI 版本，最少包含 device/pool/slot 身份、image、尺寸、format、当前 layout 和 queue family、ownership、错误与取消；图像绝不进入 Server JSON。一次 GPU copy 被允许但须计数，最终帧不得 CPU readback。

```
FrameReady { streamId, hostInstanceId, producerEpoch, inputGeneration,
  timelineEpoch, consumerEpoch, poolGeneration, slotId, slotEpoch, frameSeq,
  timeUs, width, height, format, colorSpace, alphaMode, origin,
  modifier, planes: [{ fdIndex, stride, offset, size }], sync }
Release { streamId, producerEpoch, inputGeneration, consumerEpoch,
  poolGeneration, slotId, slotEpoch, frameSeq }
```

FD 仅随本地 native channel 的 `SCM_RIGHTS` 传递，JSON 的索引不是 FD；验证设备、plane 边界、格式、descriptor 数及身份后导入。每个固定池最多三槽；每槽 `Free → Writing → Ready → ConsumerUsing → Retiring → Free`，不能借 send、draw 或 JS close 判定 GPU 完成。正常消费仅在 Electron 全引用释放且 GPU 同步成立时发布 release；无空槽时合并目标而非扩池。分配失败、设备不匹配、同步未知必须失败且不做 CPU fallback。

正常关闭/替换先停止新帧，再等待所有实际消费者和 Worker 排空，最后释放输入、池与设备。renderer/GPU 崩溃时已转移 lease 的完成状态未知，停止整个流和 Electron 会话、隔离槽位并拒绝 ACK，不自动重启或扩池；该结果是 expected fail-stop，不是安全 drain 或恢复。已交付 FD 的 payload 引用可延长 allocation 寿命，不代表 GPU 工作完成。

## 验收和交付边界

先单测合同、owner/host/CAS、资源保留、代际 ABA、背压与错误清理；再固定构建同版本 Server/Worker、验证 profile 握手和独立流运行；最后由 Electron 实测可见帧、三槽、同步、替换/关闭和崩溃。不得把 P0 图案、202 accepted、单元测试或 capability 枚举当作 P1/P2 的生产端到端验收。性能运行只能在对应生产链路通过上述门禁之后进行。
