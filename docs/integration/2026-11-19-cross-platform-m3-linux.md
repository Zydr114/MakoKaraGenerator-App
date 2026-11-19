# 跨平台 M3 阶段 Linux 侧全链路实施与门禁验收记录（2026-11-19）

结论：**M3 阶段 Linux 侧全链路集成与多模连续预览门禁已全部通过。** 桌面端在 Linux 下正式贯通 **`GPU · DMA-BUF`**（默认）、**`GPU · SHM`**（`MAKO_PREVIEW_TRANSPORT=shm`）与 **`CPU · SHM`** 三条连续预览路径，完整通过动态设备/传输切换、`<canvas key={transport}>` 节点隔离、SHM 30 次 `720p ↔ 1440p` 连续切尺寸、显式 `MAKO_PREVIEW_TRANSPORT=dma-buf` 零静默降级、消费者映射前 `SIGKILL` 清理、真实 SDK v2 作者脚本错误在途排空、注入/外部 `SIGKILL` 故障隔离、HTTP 守卫与纯浏览器静态 PNG 回归。

边界声明：依照 [跨平台适配计划](../plans/cross-platform-adaptation-plan.md) 的阶段门禁规则，**本轮未编写 Windows 侧原生/Win32 代码，Linux 实机结果不替代 Windows 实机 G2/G3 验收**；Windows 侧 M2 OS 叶子实现、Windows `pnpm build:desktop`/`pnpm desktop` 实机 G3 及 M4（媒体封套与 Portable `.zip` 便携包）统一延后至 M4 阶段在 Windows 环境下实施与验收。

依据：[跨平台适配计划](../plans/cross-platform-adaptation-plan.md)、[M2 方式 B 与 Linux G2 实测记录](2026-11-18-cross-platform-m2-linux-g2.md)、[Linux M1 冒烟与可行性](2026-11-16-linux-m1-smoke-m2-m3-feasibility.md)。

## 1. 范围与关键变更

前置步骤已将 `feat/cross-platform-m2-shm`（`d95e631`）快进合并至 `main` 并推送 `origin/main`；本轮基于 `main` 在分支 `feat/cross-platform-m3-linux-shm` 按步提交：

1. **后端多设备连续流、传输协商与按需 CPU Worker（`7ca2b77`、`c8c47a3`、`0685fb5`）**：
   - 产品版本升至 **`0.116.0`**，公共 HTTP 合同冻结升至 **`1.57.0`**（`GpuPreviewStreamCreateRequest.api` 与 `GpuPreviewStreamState.api` 放开 `"vulkan" | "cpu"`，`GpuPreviewStreamState` 与内部 `GpuPreviewWorkerClaim` 新增必需字段 `transport: "dma-buf" | "shm"`；传输模式覆盖仍严格由 `MAKO_PREVIEW_TRANSPORT` 环境变量控制）。
   - [GpuStreamWorkerSession.cs](../../backend/src/MakoKaraGenerator.Backend.Hosting/GpuStreamWorkerSession.cs) 落地 `ResolveEffectiveTransport` 严守零静默降级：显式 `MAKO_PREVIEW_TRANSPORT=dma-buf` 请求 `api="cpu"` 精确返回 `422 commandNotSupported` / `failureCode: "PreviewTransportUnsupported"`；未指定环境变量时 `vulkan` 在 Linux 默认走 `dma-buf`，`cpu` 默认走 `shm`。
   - [GpuWorkerPool.cs](../../backend/src/MakoKaraGenerator.Backend.Hosting/GpuWorkerPool.cs) 与 [WorkerProcessProxy.cs](../../backend/src/MakoKaraGenerator.Backend.Hosting/WorkerProcessProxy.cs) 保留 `vulkan` 预热备用池，并为 `cpu` 连续预览流提供纳入统一停机排空与双流配额的按需轻量 Worker（`WorkerProcessProxy.GpuStream.cs` 放宽 `shm` 模式下空 `DeviceUuid` 校验）。
   - [LinuxPosixShmBacking.cs](../../backend/src/MakoKaraGenerator.Rendering.Skia/LinuxPosixShmBacking.cs) 加固 `TryCleanupUnopened`（校验 `TMPFS_MAGIC`、`statx(AT_SYMLINK_NOFOLLOW)` 常规非符号链接文件、`0600` 权限与 `stx_uid == geteuid()` 后再解链），并由 `GpuPreviewStreamRegistry.MarkExited` 与 `GpuStreamWorkerSession.RetryTerminationAsync` 同步回收消费者映射前已死亡 Worker 的具名 SHM 文件及私有 `/tmp/mkg-gpu-*` 目录。
2. **合同冻结与同步（`f862d69`、`b5b5a4e`）**：
   - 新增 [preview_transport_fixtures.py](../../backend/tools/backend-api-contract/preview_transport_fixtures.py) 实现 `1.56.0 ↔ 1.57.0` 可逆迁移与 7 组新增正反例，重生成并核验 `115 paths / 128 operations / 679 schemas / 4,023 examples（2,890 反例）`，同步更新前后端 `contract-manifest.json` 源码指纹（基线 `c8c47a36c529a09cb1d0be594a629cf36aa78a36`）。
3. **桌面构建部署与 Electron 双传输宿主（`a3d950f`、`76b97ca`、`d348cb5`）**：
   - [scripts/desktop-build.mjs](../../scripts/desktop-build.mjs) 接入 `native/shm-preview/build.sh` 编译、符号校验（`napi_register_module_v1`）、产物封装（`desktop/shm_bridge.node`）与 `gpu-preview-manifest.json` SHA-256 登记，同时显式拦截非 Linux 平台并标注 Windows 构建留归 M4。
   - [desktop/main.cjs](../../desktop/main.cjs) 与 [desktop/gpu-preview/preload.cjs](../../desktop/gpu-preview/preload.cjs) 接通 SHM UDS 权限/UID 核验、修复 `mako-gpu:drawn` 与 `port.onmessage` 竞态计数、暴露 `previewConfig()`、补齐 SHM 校准时钟延时统计，并在故障隔离路径透传关闭底层通道句柄防止 FD 泄漏；[desktop/runtime/backend-host.cjs](../../desktop/runtime/backend-host.cjs) 补齐退出时 `WEB_BACKEND_STOPPED` 结构化退出码日志。
4. **前端多模连续预览与 Canvas 节点隔离（`933f2e1`）**：
   - [render-device.ts](../../frontend/src/editor/model/render-device.ts) 放开桌面端 `cpu` 与纯 CPU 环境的 `usesGpuStream`，新增 `resolvePreviewStreamApi` 与 `previewStreamBadge`（`GPU · DMA-BUF` / `GPU · SHM` / `CPU · SHM`）。
   - [gpu-preview-controller.ts](../../frontend/src/editor/preview/gpu-preview-controller.ts) 与 [PreviewPanel.tsx](../../frontend/src/editor/preview/PreviewPanel.tsx) 透传 `api` 与服务端权威 `transport`，以 `<canvas id="mako-gpu-preview" key={canvasTransport} ... />` 隔离 2D 与 WebGL2 上下文（传输模式切换前等待 `requestAnimationFrame` 提交新 DOM 节点），并将带 `failureCode` 的后端拒绝明确呈现于预览面板。
5. **M3 门禁脚本与冒烟升级（`19d0eb0`、`90794cd`、`433e6e6`）**：
   - 升级 [scripts/desktop-smoke.mjs](../../scripts/desktop-smoke.mjs) 使用 CDP `Page.captureScreenshot` 像素取样（不削弱生产 WebGL2 `preserveDrawingBuffer=false`），覆盖默认模式与 `MAKO_PREVIEW_TRANSPORT=shm` 模式下的 `GPU ↔ CPU · SHM` 动态切换。
   - 新增 [scripts/shm-preview-m3-linux.mjs](../../scripts/shm-preview-m3-linux.mjs) 覆盖 `switch`、`shm-resize30`、`dmabuf-no-fallback`、`premap-sigkill` 四大 Linux 实机门禁；[scripts/gpu-preview-lifecycle.mjs](../../scripts/gpu-preview-lifecycle.mjs) 适配 Electron 45 渲染进程 `SIGKILL` 后 CDP `Inspector.targetCrashed` 事件。

## 2. 干净匹配部署与 Linux 门禁验收结果

全部实机门禁运行于 CachyOS Linux x64、Electron `45.2.1`（`sandbox: true`、`contextIsolation: true`）、真实硬件 Vulkan 设备 `AMD Radeon 880M Graphics (RADV STRIX1)`，使用干净工作区构建的匹配部署 `.local/backend-dist/20261119T041239826Z`（版本 `0.116.0`、提交 `19d0eb068dd2b441482b138fc882de4e68e44e2f`、`sourceDirty: false`）。脱敏摘要见 [m3-linux-summary.json](evidence/cross-platform-m3-linux-2026-11-19/m3-linux-summary.json)，原始运行日志与截图保留于忽略目录 `.local/shm-preview-m3-linux/` 与 `.local/desktop-smoke/`。

| 门禁套件 | 覆盖路径与条件 | 关键实测指标 | 结论 |
| :--- | :--- | :--- | :--- |
| `desktop-smoke`（默认） | `GPU · DMA-BUF` ↔ `CPU · SHM` ↔ `GPU · DMA-BUF` | 18/18 通过；DMA-BUF `cpuPixelReadbacks=0`、CPU·SHM `cpuPixelReadbacks=1`；播放/暂停定位/背景改色/1440p/切页/未保存拦截/优雅退出 0；`/dev/shm` 零残留 | **通过** |
| `desktop-smoke`（`SHM`） | `MAKO_PREVIEW_TRANSPORT=shm`：`GPU · SHM` ↔ `CPU · SHM` ↔ `GPU · SHM` | 18/18 通过；2 秒播放出帧 `48` 帧（24 fps 满帧）；切换 CPU·SHM 及恢复 GPU·SHM 无上下文冲突；退出码 0；`/dev/shm` 零残留 | **通过** |
| `m3-linux: switch` | Linux 默认模式动态切换、DOM 节点隔离与真像素核验 | `DMA-BUF → CPU·SHM` 与 `CPU·SHM → DMA-BUF` 均实测 `<canvas>` DOM 节点重建（`canvasRemounted=true`）；CPU·SHM 改背景色后实呈角像素精确为 `[40,170,90,255]`、`portCaptureDurationUs=1225 µs`、1.5 秒播放 `36` 帧（`peakOutstanding=1`）；退出码 0、tmpfs 零残留 | **通过** |
| `m3-linux: shm-resize30` | `MAKO_PREVIEW_TRANSPORT=shm`：预热后连续 **30 次 `720p ↔ 1440p` 切尺寸**，再切换 `CPU · SHM` | 全程 30 次切尺寸及 `GPU·SHM → CPU·SHM` 均复用同一 `<canvas key="shm">` 节点，零 WebGL 报错与运行时异常；主进程 **`fdDelta=0`（`189→189`）、`RSS -1,820 KiB`、`VmSize` 净增 `0 KiB`**；渲染进程 **`fdDelta=-2`（`63→61`）、`RSS -25,020 KiB`**；退出码 0、tmpfs 零残留 | **通过** |
| `m3-linux: dmabuf-no-fallback` | 显式 `MAKO_PREVIEW_TRANSPORT=dma-buf` 切至 CPU 再切回默认 | 面板明确呈现 `PreviewTransportUnsupported`，无静态 `<img>` 回退、无 SHM 文件创建（`tmpfs=0`）；切回默认立即恢复 `GPU · DMA-BUF`；退出码 0 | **通过** |
| `m3-linux: premap-sigkill` | `MAKO_PREVIEW_TRANSPORT=shm`，分别对 `api="vulkan"` 与 `api="cpu"` 在消费者调用 `openReadOnly` **前**对精确 Worker PID 发送 `SIGKILL` | 强杀前均确认 tmpfs 存在 1 个 `0600` 具名 SHM 文件；强杀后首次 `GET` 返回 `registrationState="failed"` / `failureCode="GpuWorkerExited"` 且**当即清除未映射 SHM 文件（`postDetectTmpfsResidue=0`）**；随后 `consumer-failed` 确认终止并删除私有 `/tmp/mkg-gpu-*` 目录，后端退出码 `0` | **通过** |
| `author-error --inflight`（`SHM`） | `MAKO_PREVIEW_TRANSPORT=shm` + `--slow-consumer-ms 1000` + 真实 SDK v2 作者脚本在 `1.0s` 抛错 | 首帧在途租约（`mainOutstanding=1, workerOutstanding=1`）阻塞至精确完成回执后才上报 `WorkerRuntimeDiagnosticError`（含 `ScriptExecutionFailed`）且 `drained=true`；重建新健康流正常出帧；主/渲/后端进程存活；退出码 0、`/dev/shm` 零残留 | **通过** |
| `fault-acceptance`（`SHM`） | `MAKO_PREVIEW_TRANSPORT=shm` + `--slow-consumer-ms 180`；注入与真实外部 `SIGKILL` 分别强杀 `renderer` 与 `worker`（共 4 组） | 4/4 全部通过：`unknownOutstanding=3`、故障后 `zeroAcknowledgements=true`（`ackDelta=0`）、主进程存活、显式关闭后 `serverExit=0`、`launcherExit=0`、全部自有进程树退出且 `/dev/shm` 零残留 | **通过** |
| `gpu-preview-http-negative` | 匹配 `0.116.0` 部署（分别在默认 `dma-buf` 与 `MAKO_PREVIEW_TRANSPORT=shm` 下执行） | 两种模式均通过全部 **32/32** 项有限 HTTP 边界、CAS、配额、私有 claim 与未连接 Worker 生命周期守卫；退出码 0、`/dev/shm` 零残留 | **通过** |
| `browser-smoke` | 独立隔离 profile 与匹配 `0.116.0` 静态 Web + Server，真实 Chrome | 连接、上传 WAV/LRC、时间编辑与撤销、暂停定位 1 秒、实际 `1280×720` PNG 显示与关闭全绿，零未处理页面异常，后端退出码 0；既有浏览器静态预览协议与 500ms 控制节奏零退化 | **通过** |

## 3. 构建、合同与单元测试汇总

- **全量后端构建**：`dotnet build backend/MakoKaraGenerator.slnx --no-restore -m:1 /p:UseSharedCompilation=false` 全部 30 个项目 **0 警告、0 错误**。
- **公共 API 合同门禁**：`ContractMetadata`（Release）+ `freeze.py` + `validate.py` 通过 **`115 paths / 128 operations / 679 schemas / 4,023 examples（2,890 反例）`** 及全部历史语料 SHA-256 校验。
- **前端类型与单测**：`tsc --noEmit` 零错误；`npm run test:unit` 56 个测试文件、**4,595/4,595 项全部通过**（含 4,037 项合同测试）。
- **Node / Desktop / Native SHM 测试**：`tests/*.test.mjs`、`desktop/gpu-preview/*.test.cjs`、`desktop/runtime/*.test.cjs` **57 项通过、8 项 Windows 专属跳过、0 失败**；`native/shm-preview/check-shm-bridge.mjs` **5/5 通过**。
- **.NET 定向回归**：`Rendering.Tests`（`ShmPreviewPortTests|GpuRenderOutputTests` 9/9）、`Backend.Worker.Tests`（`GpuStream|LocalStream` 24/24）、`Backend.Hosting.Tests`（`GpuPreviewStreamRegistryTests|GpuWorkerSparePoolTests|GpuDeploymentIdentityTests|WorkerProcessProxyTests|GpuWorkerWarmupContractTests` 67/67）、`Backend.Server.Tests`（GPU 故障与传输协商 4/4）全部通过。

## 4. 延后至 M4（Windows 环境统一实施与验收）的事项

1. **Windows M2 OS 叶子节点补齐**：`CreateFileMappingW("Local\\mkg-shm-...")` + 当前用户 SID DACL、`NamedPipeServerStream(CurrentUserOnly)`、`shm_bridge.cc` 的 `#ifdef _WIN32` 动态 N-API 解析（`GetProcAddress(GetModuleHandleW(nullptr), ...)`）与 Windows 实机 G2。
2. **Windows M3 构建与实机 G3**：`scripts/desktop-build.mjs` 的 VS BuildTools `cl.exe` 构建链（免定制 Skia 与免 `node.lib` 下载）、Windows 实机 `pnpm build:desktop` / `pnpm desktop`、`GPU · SHM` / `CPU · SHM` 播放与 30 次切尺寸 Commit Charge 验收。
3. **Windows M4 交付项**：128 MiB Job Object + IOCP 媒体特征封套、`mkg-presentation.exe` 原生探针、`scripts/package-windows.mjs`（`pnpm package:windows`）免安装 Portable `.zip` 及 exFAT 无持久化 ACL 目录验证。
