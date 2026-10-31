# GPU 预览流 v1 合同草案

状态：供跨模块审查，未实现、未宣称能力。P0 只证明限定设备上的有限图案，不能代替生产 Runtime 验收。本文锁定 v1 的职责与字段；审查通过后实现并以实际合同测试、能力握手和发布结果确认。

## 身份与授权

一个 stream 属于一个 `hostInstanceId`、一个已认证 `clientId` 和一个固定来源。所有可靠操作复用现有 bearer/client、host instance、receipt、owner 与失效语义；不接受调用方声称的 owner。`streamId` 是标识而不是凭证。创建和替换必须复用 `PreviewBackendService.CaptureAsync` 的来源修订校验、资源准备、profile 握手和 `RetentionLease`，在 writer 之外执行 Worker 启动；发布前通过原有注册门禁复核来源和所有权。失败或 CAS 竞争时释放新引用，旧代仍有效。

桌面入口的目标是 main 持有同一个已注册 client 的访问凭证，preload 只暴露具名操作，main 校验调用窗口、帧与本地来源，并将 bearer 放在 loopback HTTP Authorization header；返回给 renderer 的是状态和不透明 streamId，不含 bearer、resume token、broker 路径、原始 FD 或 Worker 凭证。renderer 不得提供任意 URL、headers 或 clientId 指令。必须先实现并验证主进程接管 client 注册、刷新、恢复和清理，使既有项目操作与 stream 使用同一 owner；不能为 stream 新建 client 后绕过 owner，也不能把现有 renderer 持有的 token 重新传给 main 冒充无凭证方案。非桌面浏览器现有认证和 PNG 路径不变。该接管是桌面接线前置条件，具体迁移不得破坏现有 client 生命周期。

Worker 由 Server 自有 proxy 启动，控制通道与 broker 由本机进程建立；broker 使用私有目录、权限和同 UID/PID 校验，并由父进程私下提供一次性随机握手值绑定 `streamId`、`hostInstanceId`、`producerEpoch` 和已登记 Electron main 身份。随机值不得进入 URL、renderer、普通日志或公开状态；不能仅凭同 UID 信任对方。必须验证实际对端进程身份和握手，失败即关闭并清理 FD。具体私有传递机制在实现中验证，不以环境变量明文日志或公开 HTTP 响应传递。

## 可靠操作

所有请求均携带现有 `RequestMetadata`、host instance、owner 上下文；所有响应包含 `streamId`、`streamRevision`、`inputGeneration`、`state`。下列字段为 v1 必填：

| 操作 | 请求字段 | 提交语义 |
| --- | --- | --- |
| `CreateStream` | `source { kind, sourceId, sourceRevision }`, `width`, `height`, `alphaMode`, `renderBackend: gpu` | 固定输入、能力、内存及 Worker 均成功后注册；失败不公开半成品。 |
| `ReplaceInput` | `streamId`, `expectedStreamRevision`, `source` | 比较 revision 后原子切换至新 `inputGeneration`；旧输入和池在其 lease 排空后释放。 |
| `Resize` | `streamId`, `expectedStreamRevision`, `width`, `height` | 使用新 generation；不得覆盖还被消费的旧池。 |
| `CloseStream` | `streamId`, `expectedStreamRevision` | 幂等有界排空；关闭后拒绝新控制与帧。 |
| `ReadStreamState` | `streamId` | owner/host 校验后返回权威状态、epochs、最后应用控制和有限计数。 |

新增路由及 DTO 由 Server/Contracts 共同冻结和生成合同，不能声称已发布；原 PNG API、尺寸限制和导出合同不变。重复 request ID 使用现有 receipt 的相同负载幂等、不同负载冲突；revision 不匹配失败，不得先改变旧代。每 client、host 的 stream 数、固定资源、Worker 数和双代过渡内存须有明确硬预算；能力和分配之前检查 dimensions、实际 allocation 与总额，失败清理引用。第一版参数仅在能力响应声明实测支持的值，不能假定 1440p 可用。

## 内部控制合同

同一 Worker 有一个固定 Runtime，会话打开时从 `BackendPreparedProjectRuntimeInput` 的受哈希保护字节和既有 `ProjectRuntimeInput` 路径创建；包、图片和音频固定闭包及 profile 检查与静态预览等价。P1 独立流注册表拥有 proxy 和输入 retention，P2 共用 `ProjectRenderSession` 的帧准备、诊断与绘制，不复制 Runtime。

控制采用私有可靠有界通道，不经逐帧 HTTP receipt 或 SSE journal：

```
SetPlaybackState { streamId, hostInstanceId, producerEpoch, inputGeneration,
  controlSeq, mode: paused|playing, mediaTimeUs, rate, targetFps,
  clockSample: { mediaTimeUs, senderMonotonicNs } }
ControlApplied { streamId, producerEpoch, inputGeneration, controlSeq,
  timelineEpoch, mode, effectiveMediaTimeUs }
```

`controlSeq` 严格递增，同序同内容可重放，不同内容拒绝；至多一个执行中和一个最新待处理状态，后者可覆盖。seek/循环不连续提升 `timelineEpoch`，不得跳过 Runtime 必要状态推进。`producerEpoch` 为 Worker 实例随机身份；replacement、resize 增加 `inputGeneration`，进程重启不得继承旧 epoch。单调时钟需握手估计映射及不确定度，不跨进程直接相减；无法建立时钟时只允许暂停定点帧。对外状态区分 accepted、applied、frame-ready 和 consumer-released。

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
