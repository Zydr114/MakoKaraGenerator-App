# GPU 共享纹理预览实施计划

状态：**P0 条件 Go；P1/P2 已实现，P3 两次正式滑动窗口均失败，本轮收口；后续矩阵未测**。日期：2026-11-01。
设计源码基线为 App `3ca6730`，计划提交为 `8411e6a`；原设计阶段为只读研究，之后用户授权实施。
P0 证据位于[归档目录](../performance/evidence/p0-2026-11-01/)，生产合同见
[GPU 流 v1](../architecture/gpu-preview-stream-v1.md)。后文保留原门禁和阶段出口作为设计历史，
不将待验收性能门槛写成测量结果。

当前实现采用 **普通 Skia Vulkan BottomLeft surface → 同设备一次 GPU blit（Y 翻转）→
三槽 exportable image → SCM_RIGHTS → Electron SharedTexture／VideoFrame →
allReferencesReleased 后精确 release**。最终预览帧不回读 CPU；每帧一次整帧 GPU copy 与
queue-idle 等待均计入成本。直接绘入 exportable target 已完成 P0 探索，生产路线为保留现有
Skia surface 语义的一次 blit，不再把直接目标列为当前首选。

已实现同一固定 Runtime 的 typed GPU lease、私有有界控制、低频可靠 HTTP owner/CAS、
三槽预算、Electron 窄桥与编辑器 adapter。Worker `f93dda6` 全套 138 项测试通过，其中 GPU 定向 25 项；
后续真实占用遥测 `09920f4` 的 mailbox／stream 定向 14 项通过，硬编码 render 计数为零的变异回归失败。
测试证据只覆盖各自边界，不能替代 P3。当前限定 Linux x64／AMD 组合，Wayland 需逐次检查实际进程；
renderer/GPU crash 或消费者完成未知只证明 fail-stop／quarantine，未验证 crash 安全回收或恢复。

当前有限结果：首轮 173 帧后质量切换故障保留为历史；后续[短时生产联调](../performance/evidence/production-2026-11-01/quality-debug.json)
已完成 720p24、1440p30 和短时 lifetime 三阶段（分别 121、151、150 个唯一 drawn/native-completed 帧），
质量重建、来源更新、切页重附着和公开 resize 的新输入代次均取得精确消费，报告没有 runError。
这些阶段各约五秒，旧发布未提供 Worker 独立遥测；单次来源冷启动加时钟不确定度为 1080.60 ms，
未通过交互候选门槛。因此该轮明确为 debug；后续有限生命周期与正式 P3 结论另列如下。
独立的 32 项[真实 HTTP 检查](../performance/evidence/production-2026-11-01/http-negative.json)通过，
包括 owner/CAS/strict JSON、create 重放、私有 claim、2560×1440 实际三槽分配（44,236,800 B）
及旧／新 Worker PID 身份消失；该探针没有连接原生消费者，不构成帧或呈现证据。归档分别记录
发布 `c409d3a` 与运行探针时 HEAD，不能用后者冒充已发布 Server/Worker 来源。

独立匹配发布 `.local/backend-dist/20261101T044730838Z`（源码 `273abab`）完成
[真实作者 Error 在途排空](../performance/evidence/production-2026-11-01/author-error-inflight.json)：
Error 已出现时 main／Worker 各有一个未归还租约且 ACK 为零，Worker 保持 draining；
约 883 ms 后同一租约的 native completion 才产生精确 release，随后公开状态为 failed／drained:true。
App 主进程、renderer 与 Server 身份保持不变，新健康流再次完成帧，隔离实例全部正常退出。
[上一轮](../performance/evidence/production-2026-11-01/author-error.json)触发前帧已完成的边界单独保留。
renderer／Worker 丢失注入虽抑制后续 ACK 且自有进程退出，但 Server 清理出现未处理聚合异常并以 -6 退出；
`f1fe7f8` 已修复为记录清理错误并继续关闭，两个定向测试及合同冻结通过；新匹配发布
`.local/backend-dist/20261101T045924324Z` 的[四类故障复验](../performance/evidence/production-2026-11-01/desktop-faults-fixed.json)
确认受控非零退出而非 abort，自有进程退出。`921b943` 严格离线分析原日志4／4及32项变异断言通过，
来源与ACK观测起点详见[生产记录](../performance/gpu-preview-production-2026-11-01.md)。历史 -6 结果不覆盖，
新结果也不构成 crash 安全回收证据。

[真实 Runtime 有限矩阵](../performance/evidence/production-2026-11-01/runtime-finite-summary.json)完成20次attach/detach、
10次公开resize/seek、10次质量重建与各20次pause／seek／输入替换；输入UI调用p95为1202.08ms，
hello-ready生效后p95为164.06ms，后者仍超过150ms候选。正式synthetic的
[attempt1](../performance/evidence/production-2026-11-01/synthetic-formal-attempt1.json)在720p24记录180.0296s，
4320唯一完整帧（23.9961fps）；总体99.9836%通过，但最差滑动5s为117帧，未达118。
显式普通调度的[attempt2](../performance/evidence/production-2026-11-01/synthetic-formal-attempt2.json)记录180.0265s、
4320唯一完整帧（23.9965fps）；179个阶段快照内main／renderer／GPU／Worker的准确PID及稳定
startTicks齐全，均为SCHED_OTHER、Worker nice=0，最差滑动5s仍只有116帧。
两次均只有该阶段滑动窗口门槛失败，原门槛未改；自动停止后1440p180s、600s及正式交互生命周期
均未测。本轮完成实现、有限功能和生命周期验收，以P3失败收口，不作性能通过交付。
[诊断记录](../performance/evidence/production-2026-11-01/desktop-performance-diagnostic.json)与正式复验均观察到
ready之后main接收／消费者完成区间的延迟，但未确定单一根因；attempt1未记录实际调度策略，
不能由后续预检追认其历史PID策略或把失败单独归因于IDLE。完整复现命令与边界见
[生产记录](../performance/gpu-preview-production-2026-11-01.md)。

## 1. 决策与范围

推荐保留现有 Web 编辑器及业务 HTTP，在 Electron 桌面端新增 GPU 共享纹理预览
transport。优先证明本机 Linux／Wayland／AMD 集成 GPU 的实际路径，Windows
句柄适配作为后续独立阶段。现有浏览器 PNG 静态预览继续作为兼容入口，不把它
改名为零拷贝，也不直接提高现有 500 ms 帧请求频率。

当前主路线是 **Worker 在普通 Skia Vulkan surface 绘制 → 一次 GPU blit 至可共享 image →
本机原生句柄 broker → Electron 导入 → renderer 消费 VideoFrame → 原生消费者完成后
归还槽位**。P0 已完成有限图案与三槽循环验证并批准一次 blit 路线；P1/P2 已接入持续
控制与生产渲染接缝，真实应用与性能结果仍分别验收。

术语与承诺边界如下。

| 路径／术语 | 本文含义 | 能否满足主目标 |
| --- | --- | --- |
| CPU 共享 RAM、mmap、SharedArrayBuffer | 进程共享 CPU 可访问内存；仍可能需要 GPU 回读与再次上传 | 不能据此称 GPU 零回读；仅为备选优化方向 |
| GPU 共享纹理 | 通过操作系统句柄引用同一 GPU allocation，附带同步和寿命协议 | 主路线；须实际证明设备、格式、modifier 和消费者兼容 |
| CPU-readback-free | 最终预览帧没有 GPU→CPU 像素回读，Worker／Server／JS 不序列化整帧像素 | 主路线的可审计目标；不保证脚本、材质等内部完全没有回读 |
| 直接绘入共享目标 | 绘制直接写入 exportable VkImage | P0 探索方案；当前生产未选用 |
| 一次 GPU blit | 普通 Skia GPU surface 复制到可共享 image，并修正 BottomLeft 方向 | 已批准并实现的生产路线；无最终 CPU 回读，有一次整帧 GPU copy |

不将集成 GPU 的统一物理内存当作零拷贝证据；需要依据实际调用、句柄、同步和
数据通路判断。Chromium 合成器内部的转换或复制也必须如实登记，不能仅凭应用
未调用 `ReadPixels` 就声称端到端所有层均零拷贝。

非目标：优化实际歌词／脚本／材质渲染速度；保证任意工程达到目标帧率；改变
时间采样、效果语义和导出像素合同；替换整个编辑器；远程网络 GPU 句柄传输；
多 GPU 跨适配器共享；同时四路高分辨率播放；本轮 Windows 支持或全平台验收。
目标是排除内容渲染成本后，证明完整预览帧可从 1280×720 @ 24 fps 扩展至
2560×1440 @ 30 fps，并具有明确的流控、交互和回收边界。

## 2. 原始代码基线与当时阻塞

本节源码行号和“当前”描述保留设计阶段的旧 PNG 路径审计，不能用来判断新增 GPU 流是否实现。新增实现入口为
`GpuRenderFrame.cs`、`VulkanGpuFramePool.cs`、Worker `GpuStreamSession.cs`、Hosting
`GpuStreamWorkerSession.cs`、Server `PreviewBackendService.GpuStream.cs`、`desktop/main.cjs` 与
`frontend/src/editor/preview/gpu-preview-controller.ts`。

已有[协议门禁报告](../performance/preview-protocol-gate.md)记录同一真实来源创建
720p 成功、创建 1440p 被拒绝，公开内容为 PNG，帧请求数为零；它证明现有协议
不满足目标前提，不是吞吐测试失败或 HTTP 带宽结论。本计划不重复运行该门禁。

| 边界 | 当前源码证据（相对仓库根目录） | 新路线需要处理的内容 |
| --- | --- | --- |
| UI 节奏与呈现 | `frontend/src/editor/preview/PreviewPanel.tsx:19`、`:71`、`:126`：500 ms 请求、250 ms 观察循环、`<img>` | 新桌面显示消费者与音频时钟绑定，不能只改常量 |
| UI 尺寸与串行调度 | `frontend/src/editor/preview/preview-controller.ts:39`、`:75`、`:98` | 当前固定 1280×720；一个在途加一个最新 want，可保留其有界思想 |
| 单帧链路 | `frontend/src/editor/preview/preview-controller.ts:179`、`:193`、`:205`、`:210` | 换输入／逐帧 POST／等待状态／下载 Blob／object URL；已被新 want 超越的有效旧帧仍可显示 |
| SDK 与状态 | `frontend/src/client/mako-client.ts:1586`、`:1656`、`:1668`、`:1706` | 每帧有可靠回执；事件是 advisory，GET 权威，默认 500 ms 轮询 |
| 回执容量 | `frontend/src/client/clock.ts:60`；`backend/src/MakoKaraGenerator.Backend.Hosting/ProjectCommandRetentionOptions.cs:10`；`ClientRequestReceiptStore.cs:77`、`:88` | Web 默认 60 s；默认每客户端 1024 条；完成但未到期仍占槽。逐帧 24／30 请求每秒无法持续沿用此模型 |
| 服务入口 | `backend/src/MakoKaraGenerator.Backend.Server/PreviewBackendService.cs:271`、`:298` | 每次帧请求进入 receipt 与客户端租约检查；新帧通道不能变成另一条逐帧修改命令日志 |
| 代次和调度 | `backend/src/MakoKaraGenerator.Backend.Hosting/BackendPreviewSessionRegistry.cs:281`、`:299`、`:596`；`BackendPreviewCoordinator.cs:101`、`:175` | 已有 seek 换代次、playback 时间不后退、旧输入拒绝、一个执行者加一个最新待处理槽 |
| 有界通知 | `backend/src/MakoKaraGenerator.Backend.Hosting/BackendPreviewSessionRegistry.cs:73`、`:93` | 请求关联是 ConditionalWeakTable；状态通知有 128 条 DropOldest。不得误称已有实现无界，也不能拿 advisory SSE 充当逐帧数据通道 |
| Worker 寿命 | `backend/src/MakoKaraGenerator.Backend.Hosting/WorkerPackageRenderFrameSessionFactory.cs:113`、`:121`、`:168`、`:229`；`WorkerProcessProxy.cs:641` | 固定输入引用、独立 proxy、串行调用、close／dispose 后释放 retention；共享帧寿命必须延伸至消费者释放 |
| GPU 最终回读 | `backend/src/MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.cs:140`、`:151`、`:187`、`:192`、`:198` | GPU 路径仍分配 CPU byte[]、flush、ReadPixels 并返回 CPU RenderFrame，必须新增输出抽象 |
| Hosting 与 PNG | `backend/src/MakoKaraGenerator.Backend.Hosting/BackendPreviewCoordinator.cs:46`；`BackendPreviewSessionRegistry.cs:463`、`:473`、`:494`；`PreviewFrameHandleStore.cs:143` | 再复制像素、编码 PNG、生成内容句柄、复制 PNG 入库；GPU 流不经过这些像素路径 |
| 现有回收 | `backend/src/MakoKaraGenerator.Backend.Hosting/BackendPreviewSessionRegistry.cs:405`、`:416`；`PreviewFrameHandleStore.cs:72`、`:169`、`:235`；`BackendPreviewRetentionLease.cs:12` | 先关新读取入口，已有租约继续计预算，排空后释放固定输入；共享 GPU 资源也必须保留这些所有权原则 |
| 音频时钟 | `frontend/src/editor/audio/audio-controller.ts:131`、`:179`、`:195` | HTMLAudioElement.currentTime 是当前播放头；seek／循环会回跳，不能只用时间递增识别新旧帧 |

当前依赖是 `backend/Directory.Packages.props:14`、`:18`、`:19` 的 SkiaSharp／
SkiaSharp.Vulkan.SharpVk 3.119.4 与 SharpVk 0.4.2。不能假设另一套 Vulkan 绑定的
扩展类型或示例可以直接使用，也不能把已有 SKSurface 当作已经可导出的 image。
`backend/src/MakoKaraGenerator.Rendering.Skia/SkiaRenderBackend.cs:39` 的设备接口
只有普通 surface／读回，`:129` 的 Vulkan 对象私有，`:172`、`:188`、`:206` 的创建
没有启用共享图像扩展，GRVkExtensions 为空；`:292` 创建普通 surface 并同步提交。
现成普通 VkImage 不能事后保证可导出。内部材质仍有独立读回，如
`backend/src/MakoKaraGenerator.Rendering.Skia/Effects/CompiledImageGraph.Drawing.cs:118`
及 `SkiaRenderPipeline.ImageScene.cs:220`，不属于本计划的最终传输消除范围。

## 3. 目标架构与最小接口

本节说明已实现的内部边界；精确 DTO、路由和字段以[流 v1 合同](../architecture/gpu-preview-stream-v1.md)与生成合同为准。现有
HTTP／Worker／CPU RenderFrame 合同保持可用。新 GPU 路径使用明确的 typed lease，
不能把空 byte[] 或数值句柄塞进当前 CPU 像素结果以伪装兼容。

```text
现有业务 HTTP ── 固定输入、精确修订、权限、资源闭包
                              │
                    GPU 预览流协调器
                   1 executing + 1 latest
                              │
隔离 Worker ─ 普通 Skia surface ─ 一次 GPU blit ─ exportable image pool（3 slots）
                              │ native handle + synchronization
                    Electron main 原生 broker
                              │ imported SharedTexture / opaque token
                    preload 窄接口 → renderer VideoFrame consumer
                              │ native final-release / GPU completion
                              └──────── pool credit return
```

### 3.1 能力与 GPU 输出接缝

能力握手至少返回：协议版本、transport 类型、生产者／消费者设备标识、格式与
颜色空间、alpha、origin、支持的 handle 类型与 modifier／plane 约束、同步方式、
尺寸及实际 allocation 字节上限、池大小、失败原因。不以“Vulkan 可用”代替可共享
和可导入；不匹配必须显式拒绝。

已实现 `IGpuRenderFrameSession.InitializeGpuOutputAsync`／`RenderGpuAsync` 与 `GpuFrameLease`，
至少包含设备／pool／slot 身份、有效像素区域、格式、布局、同步与释放所有者。
现有实际绘制步骤共用 draw 核心；CPU 输出仍执行现有读回，GPU 输出从同设备普通
Skia surface 通过一次 blit 交给共享目标。固定 SkiaSharp／Skia 源码上的小型 native C ABI
补丁提供实际 image 状态交接，构建与部署均核验版本和符号；没有更换 Vulkan 绑定。
当前 `SkiaRenderPipeline.cs:145` 仅在特定效果路径使用 sRGB，其余可为 null；上游
`SKSurface.cs:198` 普通 GPU surface 默认 BottomLeft。P0 要以当前输出为基准验证
校色、alpha 和方向，不预先硬改为 TopLeft 或一律 sRGB。Runtime 的
`backend/src/MakoKaraGenerator.Scripting/Execution/FrameSession.cs:30` 按语义网格推进
状态；丢弃显示帧不能跳过 Runtime 必要的事件／状态推进或改变确定性语义。

### 3.2 低频可靠操作与持续控制分开

`CreateStream`、`ReplaceInput`、`Resize`、`SwitchBackend`、`CloseStream` 为低频
可靠操作，保留 owner、host instance、精确 source revision、CAS、幂等与资源预算。
固定输入仍通过当前准备资源闭包取得引用，输入变化原子生成新 inputGeneration，
旧资源在生产者和消费者共同排空前保留。现有静态 API 与新流不能共享错误的尺寸
声明；新 transport 的 1440p 能力只由已完成握手的流公布。

持续控制通道提交完整期望状态，而非每帧一个请求：

| 消息 | 最小字段与语义 |
| --- | --- |
| `SetPlaybackState` | `streamId, hostInstanceId, producerEpoch, inputGeneration, controlSeq, mode, mediaTimeUs, rate, targetFps, clockSample`；play／pause／seek／loop／变速由最新完整状态表达 |
| `ControlApplied` | `lastAppliedControlSeq, timelineEpoch, mode, effectiveMediaTimeUs, state`；表示状态已应用，不表示已有显示帧 |
| `ReadStreamState` | 权威快照，包含当前 epochs、最后应用控制、最新帧身份、池状态、失败原因与有界计数；重连先读取再恢复 |
| `ClockSample` | 音频播放头和发送方单调时钟；跨进程通过握手估计时钟映射及不确定度，不能直接相减不同进程的计时值 |

控制序号严格递增、重复同内容可幂等、重复异内容拒绝；同一流最多一个应用中的
控制与一个可覆盖的最新期望状态。播放内插生成目标时间，不为每个目标时间建立
receipt、SSE journal 记录或无界队列。时钟校正已限制为上限 2 Hz，而
play／pause／seek／变速／loop 立即发送；暂停目标保留一个最新请求直到完成或失败。
音频为 master，显示按目标时间选择新鲜帧；过期帧丢弃并归还槽位，不能堆积追赶。

`inputGeneration` 标识修订／尺寸／设备，`timelineEpoch` 标识 seek／循环等不连续
时间线，`producerEpoch` 标识实际 Worker 进程代次。所有帧必须匹配这三者；不要
复用时间戳或 controlSeq 代替寿命身份。v1 每次 Electron 会话固定 consumerEpoch；renderer 全文导航／重载被拒绝，崩溃停止会话，暂不自动重建。

### 3.3 帧通知与句柄通道

`FrameReady` 至少包含：stream、全部 epochs、`poolGeneration, slotId, slotEpoch,
frameSeq, timeUs, width, height, format, colorSpace, alphaMode, origin` 及同步描述。
`slotEpoch` 随该槽每次发布递增，避免延迟 release 释放了槽中的下一帧（ABA）。
`frameSeq` 是生产序号，不能用 accepted 请求数或控制序号计算显示帧率。

Linux FD 需要真实的跨进程句柄传递；将另一个进程的 FD 整数写进 JSON 没有该
语义。broker 使用本机 native channel 及描述符附件，校验连接的进程／会话身份、
描述符数量、尺寸／stride／offset／modifier、设备和预算，复制／关闭句柄各有唯一
所有者。renderer 只取得本流的不透明导入 token 与必要元数据；不开放任意 OS
句柄、路径、原始后端 token 或通用 IPC 调用。

Electron preload 暴露 `attachPreview`、`setPlaybackState`、`readPreviewState`、
`detachPreview` 等窄接口；前端新增 transport adapter，页面仍通过客户端抽象使用，
协议／client 不依赖 React。主进程验证 sender 与订阅窗口，保持 context isolation
及 sandbox；本地后端仍仅绑定 loopback、使用隔离 profile，凭证不进入 URL／日志。

## 4. 有界寿命与失败状态机

流状态：`opening → paused/playing → draining → closed`，可进入 `failed`。创建
只在能力／资源／输入准备成功后进入可用状态；关闭先拒绝新控制和新帧订阅，再
取消待处理工作、排空消费者与 Worker，最后释放 pool、设备及输入引用。客户端
断开、页面销毁、replacement 临时遮挡、窗口不可见均停止持续生产；恢复要读取
权威状态及新时钟锚点，不能一次补发隐藏期间的全部帧。

槽位状态：

```text
Free → ProducerWriting → Ready → ConsumerUsing → Retiring → Free
                         └─ superseded / stale ────┘
```

`Ready` 后 producer 不再写该槽；消费者等待生产同步，取走帧后持有 lease，最终
原生消费者释放且 GPU 使用完成后才允许回到 Free。JS 回调、收到消息、提交 draw、
frameSeq 确认均不是 GPU 已停止引用的证明。未消费 Ready 帧允许按最新优先淘汰，
但也必须执行正确的句柄和同步回收。没有 Free 槽时跳过／合并新的生产目标，禁止
扩池或覆盖消费者仍引用的图像。
pool 长期持有生产侧 VkImage allocation；P0 每次发布只创建本次 Electron
ImportedSharedTexture／VideoFrame lease，main、renderer 和真实消费者全部释放后，
以 allReferencesReleased 对应的安全完成作为槽位复用依据。不能跨帧缓存 import
对象又等待其全部引用释放，形成死锁；持久 import 配合显式每帧 fence 是后续独立
验证的优化，不属于首条可行性路径。
allReferencesReleased 只结束本轮消费，不替下一轮写入建立 acquire 同步；首次 import
的 creationSyncToken 不覆盖后续 Vulkan 写入。每一轮都重新建立 producer 完成／
layout 与外部 queue ownership 交接→导入→消费→释放的完整顺序。

pool 初始固定三个槽：一个生产、一个待消费、一个消费；实际允许状态由同步
验证决定，不假定三者必然并行。预算按实际 allocation 大小与 descriptor 数计，
不是只算 `width×height×4`。1440p 四字节紧密像素每槽为 14,745,600 B，三个槽为
44,236,800 B（42.1875 MiB）；modifier、对齐、驱动和 Chromium 内部开销另外记录。已实现每池
64 MiB、每 client／consumer 一流、每 host 两流与 128 MiB；resize／换输入先 drain 旧代，再建新代，
不重叠两代 GPU 池。

失败规则：

| 事件 | 必须行为 |
| --- | --- |
| seek／loop／输入变化 | 更新相应 epoch，取消旧待处理工作，拒绝旧帧，旧 consumer lease 仍排空 |
| resize／设备变化 | 新 pool 完整准备后切换；旧 pool 停止发布，引用归零后销毁，不能无限保留历史 pool |
| 导入／modifier／同步不支持 | 明确失败与原因；不静默改 PNG 或 CPU 并继续标记零回读 |
| 主动 detach | 停新帧并等待原生最终引用完成，再归还精确 lease |
| renderer crash | 完成状态未知时 fail-stop 整个 Electron 会话，隔离池、不发 ACK；未验证 crash 安全回收 |
| send Promise 超时／late completion | 不当作消费失败后已安全释放；隔离该 lease，等待真实完成／drain，不得覆写或新增槽绕过 |
| Worker crash／重启 | 旧 producerEpoch 失效，已有未知消费者引用隔离；停止会话，暂不自动恢复 |
| Chromium GPU process crash | 不把 crash 通知当作普遍 GPU 完成信号；隔离未知 lease，按已证明安全的 drain／device teardown 回收 |
| 消费者超时／fence 无法确认 | quarantine 受影响槽／pool，停止流；不能超时后直接重写仍可能被 GPU 使用的图像 |
| 客户端租约失效／应用退出 | 先撤销入口、停止新工作、按自身 PID 与进程身份排水，再释放授权资源 |

进程死亡或设备丢失时可销毁不再使用的隔离 GPU 上下文，但必须由实际 native／
driver 资源寿命规则证明安全，不能把“等待 N ms”当成普遍释放协议。

## 5. 实施阶段与 Go／No-go

### P0：本机原生 GPU 互操作（已条件 Go；以下保留原出口）

交付独立、最小的受控验证程序／入口，使用本仓库匹配依赖与真实父子进程。先
单张静态 GPU 图案，后再三个槽循环。生产端只生成 GPU 合成图案，不打开真实
工程、不运行歌词／脚本／材质；图案包含四角方向、RGB／灰度／alpha 基准及帧
身份。允许一次性初始化固定素材，禁止逐帧 CPU 像素生成／回读／PNG／Base64。

依次证明：

1. 查询设备与所需 external-memory／modifier／同步能力，分配可导出 image；
   验证当前 Skia／SharpVk 是否能把它安全包装为可绘制目标，以及 layout／queue
   ownership 的责任。modifier 与 BGRA 的 COLOR_ATTACHMENT、SAMPLED、TRANSFER
   usage 组合必须实际验证，不能只看扩展枚举。SkiaSharp 虽能包装 VkImage，但发行
   managed／C ABI 没有完整 mutable-state／带 semaphore flush 接口；须证明正确
   复用方案，或界定匹配固定 Skia 的 native C ABI 小补丁构建及维护成本。仅添加
   Vulkan allocator／Node addon 或更换 Vulkan 绑定，不能自动解决这个缺口。
   同时观察 Worker 实际 Vulkan GPU 与 Chromium GPU process 实际 physical GPU／
   render node，不能只比较 main JS 的标称设备名或默认适配器。
2. 真实传递 memory FD、完整 plane metadata 与同步信号至 Electron native broker；
   在目标 Linux 会话导入并通过实际 VideoFrame consumer 显示。第一轮允许生产
   queue 完整完成后发布（如 vkQueueWaitIdle）：CPU 等待不等于像素回读，但必须
   记录吞吐代价。不能假设 Electron 已有 external acquire-fence 参数。
3. 证明 producer completion、consumer final-release 与槽位重用顺序；测试静态
   显示、帧替换、无消费者、慢消费者、隐藏窗口、主动 detach、消费者退出和 GPU
   context lost；未知同步状态必须停止循环，不靠无限扩池维持运行。
4. 固定三个槽循环，验证 frame identity、颜色／alpha／origin、重复或延迟 release
   拒绝、池不增长；记录所有同步与 Chromium 内部复制的证据边界。

**Go**：上述链路真实显示且安全归槽，无逐帧 CPU 回读；格式／modifier／设备／
同步合同有可复核证据。**No-go**：任意一项只能靠虚构句柄、永久持有纹理、全局
固定等待、每帧 CPU 回读或不可界定的兼容性假设成立。No-go 时停止主路线实施，
只提交原因与候选分支；禁止先实施完整 Electron 壳、控制协议和 UI 再回来赌互操作。

直接 render target 不通时，单独评估一次 GPU blit；若仅能 GPU copy，标注其精确
位置和代价再决定是否接受。同步 API 不够时，需要 native adapter 的成本也属于
P0 结论。没有明确的生产／消费同步方案时不以 queue idle 永久替代发布协议。

### P1：持续控制与有界资源（已实现；合同与实测分别验收）

依赖 P0 Go。冻结 capability、控制、frame descriptor、同步、release、epoch、
owner、错误码和资源上限，形成独立合同审查；确认对既有静态协议与能力声明的
影响后实施。实现受控 synthetic GPU provider、单执行加最新槽、低频可靠命令、
独立控制通道、三个槽 pool、native broker 与状态恢复。

出口：协议和有界状态机测试涵盖 seek、loop、变速、暂停、重连、积压、重复 release、
renderer／Worker 崩溃和排空；全路径的帧率不会消耗逐帧 receipt。旧 PNG 路径
和 500 ms 节奏保持原语义。此阶段功能通过不等于性能达标。

### P2：生产绘制与编辑器（已实现；生产联调进行中）

依赖 P1 及 typed GPU target。提取共享 draw 接缝，将预览最终输出接到外部目标；
保留 CPU RenderFrame 与导出路径。透传原有固定输入、精确修订、资源闭包和诊断，
有 Error 诊断不能发布成功帧。编辑器以 adapter 选择桌面 GPU stream 或原静态
transport，显示帧携带真实 timeUs／revision／backend 供 overlay 使用。

出口：实际工程只做有限正确性与生命周期检查，确认像素格式、透明度、颜色、
方向、时间采样与现有输出相容；不把它当作渲染性能评估。单独审计残留的内部
readback，报告最终输出零回读与内部效果 readback 的不同范围。显式 GPU 偏好
失败仍写明原因，不自动切 CPU。关闭／切页／replacement／窗口隐藏可可靠停止流。

### P3：隔离内容渲染的有限基准与验收

依赖 P1、P2 的功能与合同通过。使用生产 transport、相同 broker／consumer／
同步／资源寿命，将生产内容替换为 synthetic GPU provider；不通过另一套模拟
HTTP 服务、PNG 测试入口或离屏不显示的空消费者“验证”目标。

基准第一步重新核对实际能力、匹配 App／Server／Worker／Electron 版本和 clean／
dirty 状态，确认新合同支持 1440p 且最终 GPU 输出没有 CPU readback。任一前提
失败立即停止矩阵；不以 `202`、FrameReady 数量或请求发出频率代替显示结果。

首验仅两档：720p24、1440p30，各预热 10 s、记录 180 s；另对 1440p30 记录 600 s
寿命稳定性。中间档在定位问题需要时再增加。正常流与强制慢消费者／隐藏窗口／seek／
输入变代次等压力情景分开。压力情景允许最新优先丢帧，必须证明预算和恢复有界。
只测试本机 Linux／Wayland；如增加 X11 则另记真实环境与结果。

P3 验收门槛（测量结果须另附证据，不能由下表推定）：

| 指标 | 提案出口 |
| --- | --- |
| 正常完整帧消费 | 在对应目标 fps 下，消费并呈现的唯一完整 frameSeq ≥目标帧数的 99%；重复呈现不计新帧 |
| 短窗口稳定性 | 每个滑动 5 s 窗口独立唯一完整帧数 ≥目标帧数的 98%，避免总量掩盖突发停顿；不得重复计同一 frameSeq |
| 消费间隔 | 稳态 p95 ≤2 个帧周期、p99 ≤3 个帧周期；无未解释的 >250 ms 停顿 |
| 交互 | pause／seek／最新输入生效至首个匹配帧消费 p95 ≤150 ms、p99 ≤250 ms；播放模式与低频控制 ack 分别记账 |
| 时间新鲜度 | 以音频 master 对照显示 timeUs，报告 signed drift、p95／p99 age；候选 p95 ≤2 帧周期，不能靠显示过时队列凑 fps |
| 有界流控 | executing ≤1、pending ≤1、固定 pool=3；过载不扩池、不累积逐帧命令和控制回执 |
| 回收与稳定性 | 600 s 过程无持续未归还 lease／FD 增长；有限 20 次 attach／detach、10 次 resize／seek 组合后计数回基线；内存按显式预算解释 |
| 正确性 | 没有撕裂、混帧、通道交换、alpha／origin 错误；旧 epoch 帧呈现数为零 |

“呈现”需要分层定义：生产完成、导入、提交绘制、Chromium 消费／合成、实际
屏幕扫描不是同一事件。至少记录 native frame consumer 的完成信号与 Chromium
可验证的消费／合成证据；若实际桌面路径没有可靠的显示完成事件，就将指标称
“唯一完整帧消费率”，同时给出窗口可见、合成 trace 及观察证据，**不宣称真实
屏幕 displayed fps 已由 JS rAF／draw 调用测出**。rAF 只用于调度，不能作新帧计数。

测量按 frame identity 关联：producer ready、broker receive、import、consumer submit、
consumer completion／release、slot recycle；记录丢弃原因与队列深度。跨进程时间
先校准单调时钟并报告误差。计时跨度包含同步及排队时就以该跨度命名，不从总
readback／等待时间推断纯 GPU copy 或 PCIe 耗时。

证据分两种有限运行：可诊断 trace 用于解释同步和复制；关闭高频 trace 的相同
负载用于吞吐／间隔结果，避免埋点影响结论。记录 CPU、进程／共享内存、GPU
allocation、FD／lease 高水位、IPC 字节量和完整帧计数。截图／取样校色如需要 CPU
读回，放在单独正确性运行，不能夹进零回读性能运行。所有本机证据脱敏、记录
时间／环境／版本／命令，所有本次拥有的进程按 PID／身份排空。

## 6. 替代分支与停止决策

| 情况 | 可行候选 | 必须明确的代价与下一步 |
| --- | --- | --- |
| Skia 无法直接包装 exportable target | GPU surface →一次 GPU blit→共享 image | 无最终 CPU 回读，仍有整帧 GPU copy；独立批准和基准，不标严格零拷贝 |
| Electron 导入／同步在本机不可用 | 原生嵌入显示 surface／独立预览窗口 | 增加窗口、overlay、输入映射和跨平台成本；先最小验证，不自动重构 UI |
| GPU 共享路径成本不可接受 | CPU shared memory＋有界 raw frame 通道 | 减少 JSON／PNG／多次 CPU copy，仍需 readback／上传；独立性能目标 |
| 需要浏览器／远程兼容 | 现有 PNG 静态预览；另行研究视频编码流 | PNG 保持静态定位；视频流有编码延迟、画质／alpha 与 GPU 编码互操作成本，不满足 raw RGBA／共享纹理证明 |

P0 成功仍不意味着 P3 帧率达标；P3 达标也不意味着任意实际内容能以该速度渲染。
如果 GPU transport 很快但实际工程渲染慢，后续应另开内容渲染性能任务，不在本
计划中混入效果降级、脚本跳帧、采样语义变更或未经审查的质量策略。

## 7. 官方资料、版本锁与本机能力边界

设计阶段于 2026-11-01 审计 Electron **v45.2.1** 的固定 tag，随后 P0 已安装并运行该固定版本；
生产桌面入口继续锁定 45.2.1。以下保留原一手来源和当时的限制说明，实际 native 小补丁、
设备与发布证据另见 P0 归档及流合同，不从历史 Electron 37／45.1 或 macOS 覆盖推定 Linux 可用。

| 一手资料 | 已核对的关键限制 |
| --- | --- |
| [shared-texture.md](https://github.com/electron/electron/blob/v45.2.1/docs/api/shared-texture.md)、[SharedTextureHandle](https://github.com/electron/electron/blob/v45.2.1/docs/api/structures/shared-texture-handle.md) | 实验性 API；main 导入、发送到 renderer、getVideoFrame；Linux nativePixmap 必须带各 plane 的 fd／stride／offset／size 与 modifier |
| [electron_api_shared_texture.cc](https://github.com/electron/electron/blob/v45.2.1/shell/common/api/electron_api_shared_texture.cc#L764-L818)、[TypeScript 入口](https://github.com/electron/electron/blob/v45.2.1/lib/browser/api/shared-texture.ts) | nativePixmap 导入逐 plane dup；Linux usage 未包含 WEBGPU，不能用 supportsZeroCopyWebGpuImport=true 推定可行；首验优先 VideoFrame→canvas／合成 |
| [原生 release](https://github.com/electron/electron/blob/v45.2.1/shell/common/api/electron_api_shared_texture.cc#L334-L375) | 消费 release 经 SyncToken 等待 GPU；send Promise、VideoFrame.close 或 JS draw 都不能单独授权 producer 重用；须释放 main／renderer 全部引用并核对 allReferencesReleased 与真实消费者的关系 |
| [Linux dma-buf](https://docs.kernel.org/driver-api/dma-buf.html) | implicit／explicit 同步各有合同。公开 Electron import 结构无外部 acquire fence 参数；P0 正确性基线后可研究 DMA_BUF_IOCTL_IMPORT_SYNC_FILE／EXPORT_SYNC_FILE 与 implicit reservation 桥接，不能假定 Vulkan 提交已自动接入 implicit fence |
| [SkiaSharp 包装入口](https://github.com/mono/SkiaSharp/blob/v3.119.4/binding/SkiaSharp/GRBackendTexture.cs#L22-L25)、[SKSurface](https://github.com/mono/SkiaSharp/blob/v3.119.4/binding/SkiaSharp/SKSurface.cs#L168-L175)、[GRContext](https://github.com/mono/SkiaSharp/blob/v3.119.4/binding/SkiaSharp/GRContext.cs#L140-L151) | 可用 GRVkImageInfo 包装外部图像，公开 flush／submit 不等于 mutable-state／外部 semaphore 已完整暴露 |
| [匹配 Skia native API](https://github.com/mono/skia/blob/7dbfc07dd33181f84e0958afb7ee805c6c769f0b/include/gpu/GrDirectContext.h#L479-L497)、[Vk backend state](https://github.com/mono/skia/blob/7dbfc07dd33181f84e0958afb7ee805c6c769f0b/include/gpu/ganesh/vk/GrVkBackendSurface.h#L40-L47) | v3.119.4 对应 Skia SHA 7dbfc07；原生有 mutable state／image layout 入口，但发行 C ABI 与本机 libSkiaSharp 未暴露相应接口。不能承诺只编一个小 addon 就可链接这些 C++ 能力 |

本次只读 vulkaninfo 枚举：AMD Radeon 880M／RADV STRIX1、Mesa 26.2.5-arch3.1，
Vulkan device 1.4.354／loader 1.4.363，kernel 7.3.0-5-cachyos，Wayland wayland-1，
renderD128（226:128），另有 XWayland DISPLAY=:0。驱动枚举 external_memory_dma_buf、
image_drm_format_modifier rev2、external_memory_fd、external_semaphore_fd、
external_fence_fd、queue_family_foreign；**枚举支持不证明目标 BGRA／modifier／usage／
export／import 组合或同步可用**。这些枚举是设计阶段基线；P0 随后取得 GPU 图案、跨进程共享和 Electron 有限消费证据。
生产路径明确有一次 GPU blit，只能报告最终帧 CPU-readback-free；Chromium 内部复制及实际
屏幕扫描帧率仍未由这些证据测定，不能宣称严格零 GPU copy。
