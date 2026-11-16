# Linux M1 冒烟与 M2/M3 本机开发可行性（2026-11-16）

结论：**M2/M3 的主体可以先在本机 Linux 开发，Linux SHM 必须作为正式支持路径；本轮 Linux 冒烟有两项失败，不能标记为全部通过。** 正常浏览器、CPU/Vulkan 渲染和导出已通过，失败集中于退出测试的跨平台预期和极小导出预算的错误分类。本轮仅验证与评估，没有实施 M2/M3 或修改生产代码，也不撤销已有 Windows G1 验收。

依据：[跨平台计划](../plans/cross-platform-adaptation-plan.md)、[Windows M1 验收](2026-11-16-cross-platform-m1.md)。

## 来源与范围

- 当前 `main` 为 `afb3641b2f6eed17fead422b2227ccab656383e9`。开始时已有 171 个已跟踪文件显示变化；忽略行尾差异后没有文本修改，但包含执行位变化。三个原有未跟踪路径保持不动。
- 从当前 App 内源码重新发布 `linux-x64` self-contained Server＋Worker，产品 `0.113.0`；部署记录如实保留 `sourceDirty=true`，不伪称干净提交构建。
- 本机 Node `26.13.1`、pnpm `11.7.0`、.NET SDK `10.0.114`、Python `3.14.10`、FFmpeg `n9.0.2`；真实 Vulkan 设备为 AMD Radeon 880M Graphics（RADV STRIX1），没有使用软件 Vulkan 冒充硬件。
- 本轮用独立临时 profile、loopback 和匹配发布；凭证不写入版本化证据。浏览器复用 App 的 `startBackend` 与 `startStaticWebServer`，显式传入隔离 profile；未启动使用个人默认 profile 的根 `pnpm start`。
- 不包含全量后端/前端单测、Electron 连续播放、DMA-BUF 性能或 SHM 实测；SHM 尚未实现。版本化证据见 [evidence](evidence/cross-platform-linux-m1-2026-11-16/checks.json)。

## 冒烟结果

| 检查 | 结果 |
| --- | --- |
| Linux 依赖恢复 | 原 `frontend/node_modules` 只有 Windows TypeScript 原生包，首次构建失败。保留旧依赖于 `.local/cross-platform-linux-20261116/frontend-node_modules-windows` 后，按现有锁文件全新安装，未改锁文件。 |
| Web 与后端构建 | `tsc --noEmit`、Vite 生产构建、Linux Server＋Worker self-contained 发布通过。Vite 有既有大 chunk 提示。 |
| Node 启动/桌面模块测试 | 共 57 项：48 通过、8 平台跳过、1 失败；单独重跑失败用例仍失败。 |
| Python host 测试 | 9 项：7 通过、2 Windows 专属跳过。 |
| Linux 进程守卫定向测试 | 3 项通过；未把未运行的测试计为通过。 |
| HTTP 正常链路 | WAV/完整波形金字塔；CPU/Vulkan PNG；CPU/Vulkan 各 48 帧视频且解码严格核验；SDK v2 WordEmphasis 编译、GPU PNG 和 360 不透明＋360 透明帧正式验证通过。 |
| HTTP 256 字节预算 | 导出失败且未发布产物，但错误码不符合 G1 探针要求；完整探针判定失败。独立复测相同，详见下文。 |
| 真实 Chrome | 连接、新建 WAV/LRC 工程、时间编辑、撤销、暂停定位 1 秒、实际 1280×720 PNG 显示、关闭工程通过；没有未处理页面异常。 |
| 退出与清理 | Python HTTP 宿主和 Node 浏览器宿主真实 Server 均退出码 0；bootstrap 删除、临时 profile 清理，本轮匹配发布的 Server/Worker 无剩余进程。 |

HTTP 原始有限证据：[http.json](evidence/cross-platform-linux-m1-2026-11-16/http.json)。浏览器证据：[browser.json](evidence/cross-platform-linux-m1-2026-11-16/browser.json)、[browser-session.json](evidence/cross-platform-linux-m1-2026-11-16/browser-session.json)。日志及截图保留在忽略目录 `.local/cross-platform-linux-20261116/` 与 `.local/integration/`。

### 失败一：退出测试的 Linux 语义不匹配

`tests/launcher.test.mjs:144` 要求 stdin 退出收到确认并返回 0；`desktop/runtime/backend-host.cjs:425` 在写入 `SHUTDOWN` 后仍立即给非 Windows 子进程发送 SIGTERM。测试 Node 子进程没有真实 .NET Server 的信号处理，因此被信号结束并被宿主归为 128。两次复现一致。

真实 Server 本轮在 Python/Node 两种宿主下均正常退出，因此不能将这个测试失败直接等同于真实 Server 无法优雅退出。后续应明确宿主跨平台退出合同，决定优先等待 stdin，或让测试准确覆盖 Linux 信号路径；不能仅跳过断言来宣称全绿。

### 失败二：Linux 极小导出预算的错误分类

`scripts/cross-platform-g1-http.py:319` 期望 `WorkerExportArtifactBudgetExceeded`，Linux 两次实跑均得到 `WorkerExportFailed`。独立复测确认 `exportResult` 为空、下载返回 `exportArtifactNotFound`、产物目录无文件，未观察到静默截断成功或残片发布。证据：[limit-diagnostic.json](evidence/cross-platform-linux-m1-2026-11-16/limit-diagnostic.json)。

待排查方向：Linux Worker 继承 `RLIMIT_FSIZE`，而 `FfmpegMediaEncoder` 和 `StaticExportTask` 的监控以实际长度严格大于预算识别超限。内核先在预算处拒绝写入时，可能进入通用编码失败路径。此处是源码推断，尚未通过原生跟踪确定根因；本轮也没有构建 M1 之前版本作 A/B，因此不将其归因为已证实的 M1 新回归。应先修复并复测精确错误分类，再标记 Linux 冒烟全绿。

## M2/M3 可以怎样在 Linux 推进

| 范围 | 本机可开发与验收 | 仍需 Windows |
| --- | --- | --- |
| M2 渲染与通道 | `IPreviewPort` 双阶段租约、DMA-BUF 封装、CPU/Vulkan SHM、三槽背压、UDS、0600 tmpfs/mmap、双像素格式/stride、计时字段 | Named Pipe `CurrentUserOnly`、`Local\\` Section 的 SID ACL、视图/句柄释放 |
| M2 消费端与 G2 | Linux `.node`、精确帧 Buffer、MessagePortMain、沙箱 preload WebGL2；四角/帧号/灰阶/透明像素；千帧压力、30 次切尺寸、慢消费者 | MSVC 构建、动态 N-API 解析、`node.exe`/`electron.exe` 真加载、Windows 句柄与 Commit 内存 |
| M3 完整接线 | HTTP/Worker/前端协商、CPU·SHM / GPU·SHM / GPU·DMA-BUF；换源/定位/暂停/故障排空；Linux 强杀清理；浏览器静态协议与 500ms 节奏回归 | `pnpm build:desktop` / `pnpm desktop`、Windows CPU/GPU SHM 实际播放、切尺寸和退出 |

当前连续预览仍绑定 Linux/Vulkan：`SkiaRenderPipeline.RenderToGpu`、`ProjectRenderSession`、`PreviewBackendService.GpuStream`、`GpuStreamWorkerSession`、前端 `usesGpuStream`、Electron `import-consumer` 均需按计划改造；本机正常 Vulkan PNG 成功不等于连续流或 SHM 已成立。

建议把 **Linux CPU·SHM、Linux GPU·SHM、Linux GPU·DMA-BUF** 固定为三条验收路径，Linux 默认保留 DMA-BUF，显式 `MAKO_PREVIEW_TRANSPORT=shm` 必须真正使用 SHM，初始化失败明确报错。CPU 不能使用 DMA-BUF：应明确“未指定传输＋CPU”的协商规则，并对“显式 dma-buf＋CPU”报错，避免默认策略与 CPU 连续预览要求冲突。

顺序上先处理本轮两项失败，再完成 M2 的 Linux 实现与 G2 Linux 证据；Windows G2 验证通过后按原硬门禁进入 M3，M3 主体同样可继续在 Linux 开发。若希望 Windows 验收前就正式推进 M3，需要先明确拆分 G2 的阶段规则，不能把 Linux 结果替代计划中的 Windows 硬门禁。完整 G2/G3 均不能仅凭 Linux 标记通过。M4 Windows 媒体封套与打包保持原范围。

## 实施前需明确的验收细节

1. **映射前强杀窗口**：计划在消费者 `openReadOnly` 完成映射后 unlink；Worker 创建具名 SHM 后、消费者映射前若被强杀，仍有潜在残留窗口。需增加此时间点的故障测试及所有权可核验的清理/恢复方案，不能只测映射后的 SIGKILL。
2. **GPU 提交计数范围**：计划将单次 `ReadPixels` 写成全帧最多一次提交，但 `SceneSurfaces`、`Warp`、`GroupIsolation` 绘制路径已有提交/同步。应限定最终端口捕获不重复同步，并对复杂场景完整测量；不能只移除最终 Flush 就宣称全帧一次提交。
3. **透明像素金值**：明确 `gl.readPixels` 预乘字节与 Canvas 解预乘颜色的区别。半透明紫的预乘 RGBA 约为 `[128,0,128,128]`；计划中的 `[255,0,255,128]` 若作为输入未预乘颜色，应写明转换与采样点。
4. **性能数字**：计划中的捕获耗时只作为待验证目标；本轮没有跑 SHM，不能将其当成本机测量值。三槽/64 MiB 预算之外，还需分别观测主进程及 renderer 的 RSS、外部像素缓冲和稳定期增长趋势。
