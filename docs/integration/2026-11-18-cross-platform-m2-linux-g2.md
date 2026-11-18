# 跨平台 M2（方式 B）实施与 Linux 门禁 G2 实测记录（2026-11-18）

结论：**M2 阶段按方式 B（零 Linux 假设的平台无关公共层 + Linux 完整实现 + Windows OS 叶子节点显式占位）实施完成，本机 Linux 端到端门禁 G2 五项套件全部通过。** 依照 [跨平台计划](../plans/cross-platform-adaptation-plan.md) 的阶段规则，Linux G2 证据不替代 Windows 实机 G2 验收；Windows 的 `CreateFileMappingW`、Named Pipe 与 MSVC 动态 N-API 叶子实现及实机 G2 仍待 Windows 环境补齐核验。

依据：[跨平台适配计划](../plans/cross-platform-adaptation-plan.md)、[Linux M1 冒烟与可行性](2026-11-16-linux-m1-smoke-m2-m3-feasibility.md)、[Linux M1 修复](2026-11-17-linux-m1-fixes.md)。

## 1. 范围与边界（方式 B）

基于 `main`（`6f42d17`，产品 `0.115.0`，公共 HTTP 合同 `1.56.0`）在分支 `feat/cross-platform-m2-shm` 分步提交：

1. **`50cfad0` — 渲染抽象与双端口实现**：
   - 在 [PreviewPortContracts.cs](../../backend/src/MakoKaraGenerator.Rendering.Abstractions/PreviewPortContracts.cs) 定义无 `SkiaSharp` 与无 Linux 假设的公共契约（`PreviewTransportKind`、`PreviewPixelFormat`、`PreferredGpuDevice`、`PreviewFrameTimings`、`PreviewFrameCounters`、`PreviewFrameLease`、`IPreviewRenderSession` 等）。
   - 在 [IPreviewPort.cs](../../backend/src/MakoKaraGenerator.Rendering.Skia/IPreviewPort.cs)、[DmaBufPreviewPort.cs](../../backend/src/MakoKaraGenerator.Rendering.Skia/DmaBufPreviewPort.cs)、[ShmPreviewPort.cs](../../backend/src/MakoKaraGenerator.Rendering.Skia/ShmPreviewPort.cs)、[LinuxPosixShmBacking.cs](../../backend/src/MakoKaraGenerator.Rendering.Skia/LinuxPosixShmBacking.cs) 与 [PreviewPortSessionManager.cs](../../backend/src/MakoKaraGenerator.Rendering.Skia/PreviewPortSessionManager.cs) 实现双阶段 `BeginFrame` + `Commit`、严格零静默降级的 `PreviewTransportSelector`、`ShmPreviewPort` 单次 `device.ReadPixels`（不前置调用 `FlushAndSubmit`）、异常取消回滚以及 `VulkanSkiaRenderDevice` 在 `Shm` 模式下免除 Linux DRM/DMA-BUF 扩展要求。
2. **`93dafdb` — Linux 原生只读共享内存桥接**：
   - 新增 [shm_bridge.cc](../../native/shm-preview/shm_bridge.cc)、[build.sh](../../native/shm-preview/build.sh) 与 [check-shm-bridge.mjs](../../native/shm-preview/check-shm-bridge.mjs)：`openReadOnly` 强校验 `S_ISREG`、`st_uid == getuid()`、`0600` 权限、精确字节长度及 `TMPFS_MAGIC`，完成只读 `mmap` 后**立即执行 `unlink` 与 `close(fd)`**；`readSlot` 带 acquire 栅栏与严格边界校验；`close()` 幂等并挂载 N-API Finalizer。Windows 分支保留 `#error` 显式占位。
3. **`b9dd8f4` — Worker 平台无关流通道与耗时拆分遥测**：
   - 新增 [LocalStreamPreviewChannel.cs](../../backend/src/MakoKaraGenerator.Backend.Worker/LocalStreamPreviewChannel.cs)（4 字节小端定长前缀 JSON 帧封装 + `ILocalStreamEndpoint` 抽象 + Linux UDS `SO_PEERCRED` 校验与单消费者接收即解链监听 socket；Windows 显式抛 `PlatformNotSupportedException`）。
   - 升级 [GpuStreamProtocol.cs](../../backend/src/MakoKaraGenerator.Backend.Worker/GpuStreamProtocol.cs)、[GpuStreamSession.cs](../../backend/src/MakoKaraGenerator.Backend.Worker/GpuStreamSession.cs) 与 [GpuStreamWorkerSession.cs](../../backend/src/MakoKaraGenerator.Backend.Hosting/GpuStreamWorkerSession.cs)：拆分上报 `timings: { renderDurationUs, portCaptureDurationUs }` 与 `counters.cpuPixelReadbacks`，保持合成/无告警帧 `FrameReady.diagnostics === null`，并覆盖消费者映射前异常退出的预生成 `shmToken` 清理兜底。
4. **`fd8cf62` — Electron 定长单缓冲 IPC 与沙箱 WebGL2 渲染**：
   - 新增无平台假设的 [local-stream-client.cjs](../../desktop/gpu-preview/local-stream-client.cjs) 与 [shm-consumer.cjs](../../desktop/gpu-preview/shm-consumer.cjs)：强制 `Buffer.allocUnsafeSlow(exactFrameBytes)`（`byteOffset === 0 && buffer.byteLength === exactFrameBytes`），单参数 `port.postMessage({ metadata, buffer })`（无 `transfer` 列表），维护最多 3 个在途槽位租约与完整 10 字段 `release` 回执。
   - 升级 [preload.cjs](../../desktop/gpu-preview/preload.cjs)、[wire.cjs](../../desktop/gpu-preview/wire.cjs) 与 [main.cjs](../../desktop/main.cjs)：自包含支持 `sandbox: true` + `contextIsolation: true`，使用 `WeakMap<HTMLCanvasElement, GlContextState>`、尺寸变化先 `deleteTexture` 再 `texStorage2D(gl.RGBA8)`、`drawingBufferColorSpace = 'srgb'`、片元着色器 `uHeight - 1 - int(gl_FragCoord.y)` 纵轴翻转、动态 `uSwizzleBgra`、`UNPACK_ROW_LENGTH` 行对齐解包，并在 `texSubImage2D` 后立即置空 `pixelBuffer` 断开 PartitionAlloc 外部缓冲引用。
5. **`16f0d70` / `183d03e` — G2 端到端探针与合同来源指纹同步**：
   - 新增受显式 `MAKO_SHM_G2_BENCHMARK=1` 门控的 [ShmG2ProducerRunner.cs](../../backend/src/MakoKaraGenerator.Backend.Worker/ShmG2ProducerRunner.cs)、[desktop/g2/main.cjs](../../desktop/g2/main.cjs) 与 [scripts/shm-preview-g2.mjs](../../scripts/shm-preview-g2.mjs)。
   - 公共 HTTP API（`PreviewBackendService.GpuStream.cs` 放开 `api: "cpu"` 与前端 `<canvas key={transport}>` 接线）按阶段划分留归 M3；本轮公共路由、DTO、OpenAPI 与样例零变更，通过 `freeze.py --write` 与 `validate.py` 同步 M2 新增渲染与流通道源码指纹。

## 2. 本机 Linux 门禁 G2 实测结果

实测环境：CachyOS Linux x64，Electron `45.2.1`（`sandbox: true`，`contextIsolation: true`），真实硬件 Vulkan 设备 `AMD Radeon 880M Graphics (RADV STRIX1)` 与 `Skia raster` CPU 设备。完整脱敏摘要见 [g2-summary.json](evidence/cross-platform-m2-linux-g2-2026-11-18/g2-summary.json)，原始运行目录保留于忽略路径 `.local/shm-preview-g2/`。

| G2 套件 | 配置与负载 | 关键实测指标 | 结论 |
| :--- | :--- | :--- | :--- |
| `correctness-cpu` | `Skia raster` · `1280×720`<br>`bgra8888`（紧凑 `5120`）+ `rgba8888`（填充步长 `5184`） | 12+12 帧 `gl.readPixels` 全点位最大绝对误差 **`0 LSB`**（`<= 1 LSB`）：不对称四角（红/绿/蓝/白）、顶部 16 位帧号条、预乘洋红 `[128,0,128,128]`、全透明 `[0,0,0,0]`、8 级灰阶无压暗；`openReadOnly` 后具名 `/dev/shm` 文件立即消失 | **通过** |
| `correctness-vulkan` | `AMD Radeon 880M (RADV)` · `1280×720`<br>`bgra8888`（`5120`）+ `rgba8888`（填充 `5184`） | 12+12 帧全点位最大绝对误差 **`0 LSB`**；无 DRM/DMA-BUF 扩展依赖（`drmRenderMajor=0`）；`avgRenderUs=118 µs`、`avgCaptureUs=1256 µs` | **通过** |
| `resize30` | 同一 `<canvas>` 预热后连续切换 **30 次**（共 31 代：`720p` ↔ `1440p` 交替并轮换 `bgra8888`/`rgba8888`） | `textureRecreations === 31`，零 WebGL 报错；预热后 30 次切换句柄增量 **`main=0, renderer=0, worker=0`**；`/dev/shm` 残留文件 **`0`** | **通过** |
| `slow-backpressure` | 渲染进程每帧人为延迟 `55 ms`（`1280×720 @ 30fps`，共 25 帧） | 消费端与 Worker 端峰值在途租约严格受限于 **`consumerPeakOutstanding=3, workerPeakOutstanding=3`**，无槽位踩踏或死锁 | **通过** |
| `stress1000` | `AMD Radeon 880M` · **`2560×1440 @ 30fps`** 连续 **1000 帧**（累计跨进程传输 **`14.75 GB`**） | `avgRenderUs=106 µs`、`avgCaptureUs=3141 µs`；第 100→1000 帧期间句柄增量 `main=+1, renderer=+1, worker=0`；RSS 净增 **`main=+2.5 MB, renderer=+0.4 MB`**；主进程 V8 `external` 稳定于 `17.68 MB → 17.59 MB`，渲染进程 JS Heap 稳定于 `47.40 MB → 47.40 MB`；`/dev/shm` 残留 **`0`** | **通过** |

## 3. 构建、合同与回归验证

- **全量后端构建**：`dotnet build backend/MakoKaraGenerator.slnx --no-restore -m:1 /p:UseSharedCompilation=false` 全部 30 个项目 **0 警告、0 错误**。
- **公共 API 合同门禁**：重新编译 `ContractMetadata` 导出 CLR 元数据，`freeze.py` 核验 `115 paths / 128 methods / 679 schemas` 与来源指纹一致，`validate.py` 通过全部 `4,016` 例（含 `2,886` 反例），前后端 `contract-manifest.json` 同步一致。
- **前端单测**：`npm run test:unit` 56 个测试文件、**4,587 项全部通过**。
- **Node / Desktop / Native SHM 测试**：`tests/*.test.mjs`、`desktop/gpu-preview/*.test.cjs`、`desktop/runtime/*.test.cjs` 与 `native/shm-preview/check-shm-bridge.mjs` 共 **54 项通过、8 项 Windows 专属跳过、0 失败**。
- **.NET 单元与集成回归**：`Backend.Worker.Tests`（158/158）、`Runtime.Hosting.Tests`（158/158）、`Backend.Hosting.Tests`（1,694/1,694，其中带 `SIGKILL` 扫描的 `WorkerExportLifetimeTests` 独立隔离执行以规避并行进程扫描互扰）、`Rendering.Tests`（`ShmPreviewPortTests|GpuRenderOutputTests` 8/8，含真机 Vulkan DMA-BUF 与 SHM）、`Backend.Server.Tests`（`Gpu` 5/5）全部通过。

## 4. 待 Windows 环境补齐项（不宣称跨平台 G2 完整结项）

1. C# [IPreviewPort.cs](../../backend/src/MakoKaraGenerator.Rendering.Skia/IPreviewPort.cs) 中 `ShmPoolBackingFactory` 的 Windows `CreateFileMappingW("Local\\mkg-shm-...")` + 当前用户 SID DACL + `UnmapViewOfFile`/`CloseHandle` 实现。
2. C# [LocalStreamPreviewChannel.cs](../../backend/src/MakoKaraGenerator.Backend.Worker/LocalStreamPreviewChannel.cs) 中 Windows `NamedPipeServerStream`（`PipeOptions.CurrentUserOnly | Asynchronous`）端点实现。
3. C++ [shm_bridge.cc](../../native/shm-preview/shm_bridge.cc) 中 `#ifdef _WIN32` 分支的 `GetProcAddress(GetModuleHandleW(nullptr), "napi_...")` 零导入库动态解析与 MSVC 构建脚本。
4. Windows 实机运行 `node.exe` / `electron.exe` 加载验证及 `scripts/shm-preview-g2.mjs` 句柄与 Commit Charge 验收。
