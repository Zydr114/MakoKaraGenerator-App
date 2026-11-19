# 跨平台 M2 与 M3 阶段 Windows 侧实施与门禁 G2/G3 验收记录（2026-11-19）

结论：**跨平台适配计划 M2 与 M3 阶段 Windows x64 侧全部代码实现、硬门禁 G2 及桌面端全链路门禁 G3 已全部通过。** 本机 Windows 11 x64（真实硬件 GPU `AMD Radeon(TM) 880M Graphics` + Electron `45.2.1` 沙箱）正式贯通默认 **`GPU · SHM`** 与 **`CPU · SHM`** 连续预览路径，零定制 Skia 编译、零 `node.lib` / Electron headers 下载依赖，完整通过 G2 像素/方向/双格式/1000 帧压测/30 次缩放、G3 桌面端动态切换、30 次 `720p ↔ 1440p` 切尺寸 HandleCount 与 Commit Charge 核对、显式 `MAKO_PREVIEW_TRANSPORT=dma-buf` 零静默降级、消费者映射前 Worker 强杀清理、真实 SDK v2 作者错误在途排空、4 组注入/外部强杀故障隔离、32 项有限 HTTP 守卫与纯浏览器静态 PNG 回归。

边界声明：本轮严格止步于 M2～M3 完成验收并报告；**M4 交付项（Windows 128 MiB Job Object + IOCP 媒体特征封套、原生探针 `mkg-presentation.exe`、`pnpm package:windows` 免安装 Portable `.zip` 及 exFAT 无持久化 ACL 目录验证）尚未启动**，Windows 非 Linux 端 `MakoMediaEnvelopeEnabled=false` 边界保持不变。

依据：[跨平台适配计划](../plans/cross-platform-adaptation-plan.md)、[M1 修复与 Windows G1 实测记录](2026-11-16-cross-platform-m1.md)、[M2 方式 B 与 Linux G2 实测记录](2026-11-18-cross-platform-m2-linux-g2.md)、[M3 Linux 侧全链路实施与门禁验收记录](2026-11-19-cross-platform-m3-linux.md)。

## 1. 范围与关键提交（`03e4d69..2f3a676`）

所有改动基于 `main` 分支按步实施、单步提交并同步至私有远程 `origin/main`：

1. **M2 Windows OS 叶子节点与 G2 探针（`ff1ed6e`、`40104a2`、`d7dc19e`、`e466429`）**：
   - [WindowsShmBacking.cs](../../backend/src/MakoKaraGenerator.Rendering.Skia/WindowsShmBacking.cs) 与 [IPreviewPort.cs](../../backend/src/MakoKaraGenerator.Rendering.Skia/IPreviewPort.cs)：实现 `CreateFileMappingW("Local\\mkg-shm-<token>")`（附加仅授予当前用户 SID 的受保护 DACL `D:P(A;;GA;;;<CurrentUserSID>)`，拒绝 `ERROR_ALREADY_EXISTS` 重名冲突）、`MapViewOfFile` 与幂等配对 `UnmapViewOfFile` + `CloseHandle` 释放，并接入 `ShmPoolBackingFactory`。
   - [LocalStreamPreviewChannel.cs](../../backend/src/MakoKaraGenerator.Backend.Worker/LocalStreamPreviewChannel.cs) 与 [GpuStreamWorkerSession.cs](../../backend/src/MakoKaraGenerator.Backend.Hosting/GpuStreamWorkerSession.cs)：实现 `WindowsNamedPipeStreamEndpoint`（`NamedPipeServerStream` + `PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance | PipeOptions.Asynchronous`、`GetNamedPipeClientProcessId` 精确消费者 PID 校验、非阻塞异步读写泵与停机短暂刷出），端点固定为 `\\.\pipe\mkg-shm-<32-hex>`；同步适配 [local-stream-client.cjs](../../desktop/gpu-preview/local-stream-client.cjs) 的 Windows `EBUSY` 重试。
   - [shm_bridge.cc](../../native/shm-preview/shm_bridge.cc)、[build.mjs](../../native/shm-preview/build.mjs) 与 [check-shm-bridge.mjs](../../native/shm-preview/check-shm-bridge.mjs)：实现 `#ifdef _WIN32` 自包含稳定 Node-API v1 动态符号表（`GetProcAddress(GetModuleHandleW(nullptr), "napi_...")`，零 `node.lib` 与零静态 `node.exe`/`electron.exe` 导入）、`Local\mkg-shm-*` 校验、`OpenFileMappingW(FILE_MAP_READ | READ_CONTROL)` + `GetSecurityInfo` 拥有者 SID 校验、`VirtualQuery` 映射长度校验及配对 `UnmapViewOfFile` + `CloseHandle`；由 VS BuildTools `cl.exe` 离线编译并自检 PE32+ 导出/导入表。
   - [scripts/shm-preview-g2.mjs](../../scripts/shm-preview-g2.mjs) 与 [desktop/g2/main.cjs](../../desktop/g2/main.cjs)：适配 Windows Worker `.exe`、Named Pipe 端点以及基于 `Get-Process` 的 `HandleCount`、`WorkingSet64`、`PrivateMemorySize64`（Commit Charge）监控。
2. **M3 Windows 桌面能力开放、GPU 预热守卫修复与 MSVC 构建链（`7411baf`、`8c58385`、`83a7ba0`、`b6a5dbd`）**：
   - [PreviewBackendService.GpuStream.cs](../../backend/src/MakoKaraGenerator.Backend.Server/PreviewBackendService.GpuStream.cs)：向 Windows 开放 `GpuStreamsAvailableAsync()`，并在非 Linux 下显式指定 `MAKO_PREVIEW_TRANSPORT=dma-buf` 时返回 `false`（绝不静默降级）。
   - [WorkerProcessProxy.GpuWarmup.cs](../../backend/src/MakoKaraGenerator.Backend.Hosting/WorkerProcessProxy.GpuWarmup.cs)：修复 Windows `WindowsJobObjectGuard` 下因 `_ownedGroup` 为空导致 `WarmGpuAsync` / `ReadGpuDeviceProofAsync` 拒绝核验的问题，改为通过 `TryGetOwnedProcessIdentity()` 同时支持 Linux `_ownedGroup` 与 Windows `_windowsGuard`（带 `VerifyIdentity()` 与 `CreationTimeFileTime` 身份绑定），并补充真实 Windows Worker 预热回归测试 [GpuWorkerWarmupContractTests.cs](../../backend/tests/MakoKaraGenerator.Backend.Hosting.Tests/GpuWorkerWarmupContractTests.cs)。
   - [scripts/desktop-build.mjs](../../scripts/desktop-build.mjs)、[desktop/p0/setup.mjs](../../desktop/p0/setup.mjs) 与 [desktop/main.cjs](../../desktop/main.cjs)：打通 `win32/x64` 桌面原生构建链（跳过 Linux 定制 Skia 与 DMA-BUF，直接调用 `buildShmBridge()` 并核验 PE32+ 导出与零宿主静态导入，生成 `platform: "win-x64"`、`defaultTransport: "shm"`、`desktop/shm_bridge.node` 的 `gpu-preview-manifest.json`）；统一 `privateFs.isSamePath` / `isInsideRoot` 路径边界与 Windows `\\.\pipe\mkg-shm-*` 校验。
   - [freeze.py](../../backend/tools/backend-api-contract/freeze.py) 与前后端合同清单：将 `WindowsShmBacking.cs` 纳入 M2/M3 来源指纹，修复 Windows 下 `freeze.py` 的 UTF-8 读写与跨平台 POSIX 路径键排序（`p.relative_to(ROOT).parts`），同步合同基线至 `83a7ba0637f1eae70ab228882eaac643d50dd42a`（合同仍为 `1.57.0`、产品 `0.116.0`）。
3. **跨平台验收脚本与 Windows M3 门禁套件（`35d6c18`、`2000822`、`955a7b8`、`d97b3ba`、`b5b767f`、`f729706`、`2f3a676`）**：
   - 新增 [scripts/process-identity.mjs](../../scripts/process-identity.mjs)（统一 Linux `/proc` 与 Windows `Get-Process`/`Win32_Process` 批量身份、进程树、HandleCount/Commit Charge 与 stdin 优雅停止）与 [scripts/shm-preview-m3-windows.mjs](../../scripts/shm-preview-m3-windows.mjs)（覆盖 `switch`、`shm-resize30`、`dmabuf-no-fallback`、`premap-kill` 四项 Windows M3 门禁）。
   - 适配 [scripts/desktop-smoke.mjs](../../scripts/desktop-smoke.mjs)、[scripts/gpu-preview-http-negative.mjs](../../scripts/gpu-preview-http-negative.mjs)、[scripts/gpu-preview-author-error.mjs](../../scripts/gpu-preview-author-error.mjs)、[scripts/gpu-preview-lifecycle.mjs](../../scripts/gpu-preview-lifecycle.mjs) 与 [scripts/gpu-preview-fault-acceptance.mjs](../../scripts/gpu-preview-fault-acceptance.mjs) 至 Windows 路径边界、`%LOCALAPPDATA%\makokara-generator\run` 启动目录、高 DPI 窄屏覆盖面板自动收起及 `MAKO_APP_SHUTDOWN_STDIN=1` 优雅停机。

## 2. Windows 实机门禁 G2 验收结果

实测环境：Windows 11 x64，Electron `45.2.1`（`sandbox: true`、`contextIsolation: true`），真实硬件 Vulkan 设备 `AMD Radeon(TM) 880M Graphics` 与 `Skia raster` CPU 设备，VS BuildTools MSVC `14.51.36231`。脱敏数据见 [summary.json](evidence/cross-platform-m2-m3-windows-2026-11-19/summary.json)，原始输出保留于忽略路径 `.local/shm-preview-g2/`。

| G2 套件 | 配置与负载 | 关键实测指标 | 结论 |
| :--- | :--- | :--- | :--- |
| `check-shm-bridge` | `node.exe`（24.21.0）+ `electron.exe`（45.2.1） | 3/3 通过：PE32+ 导出 `napi_register_module_v1`、零 `node.exe`/`electron.exe` 静态导入；双宿主动态加载并完成 3 槽实写、越界/非法 `Global\` 拒绝与配对 `UnmapViewOfFile` + `CloseHandle` | **通过** |
| `correctness-cpu` | `Skia raster` · `1280×720`<br>`bgra8888`（`5120`）+ `rgba8888`（填充 `5184`） | 12+12 帧全 14 点位 + 顶部 16 位帧号条最大误差 **`0 LSB`**（`<= 1 LSB`）；`avgRenderUs=1001 µs, avgCaptureUs=633 µs` | **通过** |
| `correctness-vulkan` | `AMD Radeon(TM) 880M` · `1280×720`<br>`bgra8888`（`5120`）+ `rgba8888`（`5184`） | 12+12 帧全点位最大误差 **`0 LSB`**；无 Linux DRM 扩展依赖（`drmRenderMajor=0`）；`avgRenderUs=134 µs, avgCaptureUs=2860 µs` | **通过** |
| `resize30` | 同一 `<canvas>` 2 代预热后连续切换 **30 次**（共 32 代 `720p ↔ 1440p` + 轮换 `bgra8888`/`rgba8888`） | `textureRecreations === 32`，零 WebGL 报错；HandleCount（预热→中点→结束）：`main 1108→1108→1109 (+1)`、`renderer 339→354→354 (中→末 0)`、`worker 398→401→401 (中→末 0)`；Commit Charge 净增 `renderer +1.25 MB, worker +27.31 MB`（无未释 `42.2 MiB` 槽位累积） | **通过** |
| `slow-backpressure` | 渲染进程人为延迟 `55 ms`（`1280×720 @ 30fps`，25 帧） | 严格受限于 **`consumerPeakOutstanding=3, workerPeakOutstanding=3`**，`avgRenderUs=328 µs, avgCaptureUs=2672 µs` | **通过** |
| `stress1000` | `AMD Radeon(TM) 880M` · **`2560×1440 @ 30fps`** 连续 **1000 帧**（累计传输 **`14.75 GB`**） | `avgRenderUs=166 µs, avgCaptureUs=6567 µs`；第 100→1000 帧 HandleCount `main -6, renderer +1, worker +10`；RSS `main -40.05 MB, renderer -252.40 MB`；Commit Charge `main -0.71 MB, renderer -253.84 MB, worker -7.82 MB`；主进程 V8 `external` 稳定于 `17.70 MB → 17.72 MB`，渲染进程 JS Heap 稳定于 `47.40 MB → 47.40 MB` | **通过** |

## 3. 干净匹配部署与 Windows 门禁 G3 验收结果

全部 G3 实机门禁运行于干净工作区构建的匹配 Windows 桌面部署 `.local/backend-dist/20261119T075026242Z`（`rid: "win-x64"`、产品 `0.116.0`、源码提交 `b6a5dbdec095d9beb729dff62c140f3ed3b2b9cf`、`deployment.json` 与 `gpu-preview-manifest.json` 均为 **`sourceDirty: false`**，携带 `desktop/shm_bridge.node` SHA-256 `4af8ee36...`）。脱敏摘要见 [summary.json](evidence/cross-platform-m2-m3-windows-2026-11-19/summary.json)。

| G3 门禁套件 | 覆盖路径与条件 | 关键实测指标 | 结论 |
| :--- | :--- | :--- | :--- |
| `desktop-smoke`（默认） | Windows 默认 **`GPU · SHM`** ↔ `1440p` ↔ **`CPU · SHM`** ↔ **`GPU · SHM`** | **18/18 项全部通过**；2 秒播放出帧 `47` 帧（24 fps），`cpuPixelReadbacks=1`，全程 59 帧 `peakOutstanding=1`；背景改色、暂停拖动定位、1440p 重建、编排/歌词切页、未保存拦截均通过；退出码 `0`，`%LOCALAPPDATA%\makokara-generator\run` 私有启动文件删除 | **通过** |
| `m3-windows: switch` | 动态设备切换、同一 `<canvas key="shm">` 复用与真像素校验 | `GPU · SHM → CPU · SHM → GPU · SHM` 均复用同一 `<canvas key="shm">` 且无 WebGL2 上下文冲突；`CPU · SHM` 改背景后角像素精确 `[40,170,90,255]`、`portCaptureDurationUs=2469 µs`、1.5 秒播放 `35` 帧（`peakOutstanding=1`）；零运行时异常，退出码 `0`，启动文件删除 | **通过** |
| `m3-windows: shm-resize30` | 预热后连续 **30 次 `720p ↔ 1440p` 切尺寸** + 切 `CPU · SHM` | 30 次切尺寸与跨设备切换均复用同一 `<canvas key="shm">`，零异常；主进程 HandleCount **`1166→1164→1164`（`-2`，中→末 `0`）**、Commit Charge 中→末 **`78,404→77,036 KiB`**；渲染进程 HandleCount `422→430→436`、Commit Charge **`-18,272 KiB`**、WorkingSet **`-16,280 KiB`**；切换 `CPU · SHM` 采样 128 色，退出码 `0` | **通过** |
| `m3-windows: dmabuf-no-fallback` | Windows 下显式 `MAKO_PREVIEW_TRANSPORT=dma-buf` | `/api/v1/capabilities` 不宣称 `preview.gpu-streams`；直调 `POST /api/v1/gpu-preview-streams`：`vulkan` 精确返回 `503 previewUnavailable / GpuPreviewUnsupportedPlatform`，`cpu` 精确返回 `422 commandNotSupported / PreviewTransportUnsupported`，零静默降级至 SHM；后端退出码 `0` | **通过** |
| `m3-windows: premap-kill` | 分别对 `vulkan` 与 `cpu` SHM 流在消费者 `openReadOnly` 前精确强杀 Worker PID | 强杀前均校验命名管道匹配 `\\.\pipe\mkg-shm-*`；强杀后 `GET` 返回 `registrationState="failed"` / `failureCode="GpuWorkerExited"`，`consumer-failed` 返回 `terminated=true`（Windows 内核随末句柄关闭自动回收 `Local\mkg-shm-*` Section）；后端退出码 `0` | **通过** |
| `author-error --inflight` | `--slow-consumer-ms 1000` + 真实 SDK v2 作者脚本在 `1.0s` 抛错 | 首帧在途租约（`mainOutstanding=1, workerOutstanding=1`）阻塞至精确完成（`brokerToCompletionMs=1021.5 ms`, `drawn=true`）后才上报 `WorkerRuntimeDiagnosticError`（含 `ScriptExecutionFailed`）且 `drained=true`；重建新健康流正常出帧；所有进程存活、退出码 `0`、启动文件删除 | **通过** |
| `fault-acceptance` | `--slow-consumer-ms 180`；注入与真实外部强杀分别针对 `renderer` 与 `worker`（共 4 组） | **4/4 全部通过**：`unknownOutstanding` 为 `2–3`、`zeroAcknowledgements=true`（`ackDelta=0`）、`serverExit=0`、`launcherExit={code:0,signal:null}`、`allOwnedExited=true` | **通过** |
| `gpu-preview-http-negative` | 匹配 Windows SHM 部署的有限 HTTP 守卫 | **32/32 项全部通过**（含 1440p 换代、旧 Worker 退出验证、私有 claim 单次消费与关闭回收）；退出码 `0`、启动文件删除 | **通过** |
| `browser-smoke` | 独立 `MAKO_RUNTIME_HOME` + `node scripts/app.mjs start --port 5197` + 真实 Chrome | 连接、上传 WAV/LRC、改时与撤销、暂停定位 1 秒、实际 `1280×720` PNG（11,952 字节 / 4,189 非黑 RGB 像素）与关闭全绿，零未处理异常，stdin `SHUTDOWN` 退出码 `0` 且 bootstrap 删除；既有浏览器静态预览协议与 500ms 节奏零退化 | **通过** |

## 4. 构建、合同与单元测试汇总

- **全量后端构建**：`dotnet build backend/MakoKaraGenerator.slnx --no-restore -m:1 /p:UseSharedCompilation=false` 全部 34 个项目 **0 警告、0 错误**。
- **公共 API 合同门禁**：`ContractMetadata`（Release）+ `freeze.py` + `validate.py` 通过 **`115 paths / 128 operations / 679 schemas / 4,023 examples（2,890 反例）`**，前后端 `contract-manifest.json` 源码指纹同步一致（基线 `83a7ba0637f1eae70ab228882eaac643d50dd42a`）。
- **前端类型与单测**：`tsc --noEmit` 零错误；`node scripts/pnpm-run.mjs test:unit` 56 个测试文件、**4,595/4,595 项全部通过**（含 4,037 项合同测试）。
- **Node / Desktop / Native / Python Host 回归**：
  - `tests/*.test.mjs`、`desktop/gpu-preview/*.test.cjs`、`desktop/runtime/*.test.cjs`：**63 项通过、2 项 Linux 专属跳过、0 失败**。
  - `native/shm-preview/check-shm-bridge.mjs`：**3/3 通过**。
  - `backend/tools/backend-web-host/test_host.py`：**9/9 通过**（含真实 17 秒 `LockFileEx` 锁竞争与纯净 SID DACL）。
- **.NET 定向回归**：`Rendering.Tests`（`ShmPreviewPortTests|GpuRenderOutputTests` **10/10**）、`Backend.Worker.Tests`（`LocalStreamPreviewChannelTests|GpuStream` **25/25**）、`Backend.Hosting.Tests`（`GpuPreviewStreamRegistryTests|GpuWorkerSparePoolTests|GpuDeploymentIdentityTests|WorkerProcessProxyTests|GpuWorkerWarmupContractTests` **68/68**）、`Backend.Server.Tests`（GPU 故障、传输协商与能力门禁 **5/5**）全部通过。

## 5. 下一阶段入口（M4，暂未开始）

M1～M3 双平台（Linux + Windows）均已通过对应门禁（G1～G3），下一步进入 **M4（Windows 侧验证与打包）**：

1. Windows `128 MiB` Job Object + IOCP（`JOB_OBJECT_MSG_PROCESS_MEMORY_LIMIT` / `JOB_OBJECT_MSG_JOB_MEMORY_LIMIT`）受控媒体特征分析封套与原生探针 `mkg-presentation.exe`。
2. `scripts/package-windows.mjs`（`pnpm package:windows`）免安装 Portable `.zip` 便携包组装、定制可执行文件名 `shm_bridge.node` 加载校验，以及干净隔离目录与无持久化 ACL（exFAT）目录实机门禁 G4 验收。
