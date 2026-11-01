# MakoKaraGenerator App

本仓库把 Backend 与 Web 的版本化源码整合为独立应用，提供浏览器入口和 Electron 桌面入口。桌面复用同一编辑器，并新增 Linux 本机 GPU 共享纹理预览；浏览器继续使用既有 PNG 静态预览。两条预览路径分别保留自己的能力和验收边界。

`backend/` 来源 `a4783fad683bcfef3f2db4042e8963e5228830b5`，`frontend/` 来源 `c2d06fd68a051de1ab1018b9cd2b18ef2ccd8bab`。路径、分支、tree 与源工作树排除项见 [source-snapshots.json](source-snapshots.json)。两个源仓库保持原样；App 只有自己的 Git 历史，没有 remote，也没有上传。上游已有的文档、AGENTS 和历史验收保持原归属，本仓库工作规则见根 [AGENTS.md](AGENTS.md)。

需要 Linux x64、Node 26、pnpm、Python 3、仓库 `backend/global.json` 允许的 .NET 10 SDK、flock、FFmpeg/FFprobe，以及 Skia 使用的字体和系统 native 库。本机实测使用 Node 26.12.1、pnpm 11.7.0、.NET SDK 10.0.113。

在 App 根目录运行：

```bash
pnpm run setup         # 按 frontend/pnpm-lock.yaml 安装依赖
pnpm build             # 类型检查、Web生产构建、匹配Server与完整Worker发布
pnpm start             # 启动构建结果，打开 http://127.0.0.1:5173
# 或在完成build之后：
pnpm dev               # 同一匹配后端＋Vite开发页面
# Linux桌面共享纹理入口：
pnpm build:desktop     # Web＋匹配Server/Worker＋固定Skia native补丁＋GPU broker
pnpm desktop           # 使用桌面构建，打开Electron编辑器
# 可选不同本机端口：
pnpm start --port 5174
```

首次构建需要下载 NuGet/npm 依赖时可设置 `HTTPS_PROXY=http://127.0.0.1:7897`，loopback 请求直连。根入口始终使用 App 内的 `backend/`、`frontend/` 和新发布的 `.local/backend-dist/<UTC时间>/`；`.local/backend-current.json` 指向最近成功发布。构建失败保留独立目录与日志，不覆盖旧发布。缺少构建时明确失败，不回退源仓库的 `artifacts`；外部 `MAKO_BACKEND_REPO`、外部 `MAKO_BACKEND_DIST` 和逃出 App 的部署符号链接均拒绝。

页面与 Server 仅绑定 loopback；Server 随机端口，Web 默认 5173。`start`／`dev` 复用 App 专有 `.local/profile/`，保存的工程、状态和日志保留在其中；不要同时启动两个实例共用此 profile。前端连接信息写入私有 `.local/run/backend.json`（0600）；后端启动器还在本次 `.local/profile/runs/run-*/` 内持有私有 bootstrap，不打印凭证。页面的 bootstrap 通道要求准确 Host、本机地址与同源 fetch；直接在地址栏访问会拒绝。

桌面使用独立 `.local/desktop-profile/` 与 `.local/desktop-electron-profile/`；其私有启动配置在 `.local/run/desktop-<PID>.json`，只交给本次 Electron main。桌面构建会验证实际 native ABI 符号和部署哈希，普通 `pnpm build` 的发布不能用于 GPU 桌面启动。

按 Ctrl+C 或向根 App 进程发送 SIGTERM，桌面入口先排空共享纹理消费者，随后关闭自有 Web、等待自有后端排水，清除 bootstrap；不按名称终止其他进程。`.local/`、构建目录、依赖和私人配置均被 Git 忽略。

有限验证入口：

```bash
pnpm test:launcher     # 6项启动失败清理、正常停止和符号链接边界测试
pnpm test:unit         # 上游前端单元回归
pnpm smoke:browser    # start运行时：新建、编辑、撤销、暂停seek、PNG显示、关闭工程
```

浏览器冒烟使用本机已有 Chromium（可设 `CHROME_PATH`），独立临时浏览器 profile 与临时音频/歌词，不操作用户已有浏览器。截图与成功结果保存在 `.local/integration/`。本次构建和真实运行记录见 [docs/integration/2026-11-01-web-integration.md](docs/integration/2026-11-01-web-integration.md)。

浏览器原预览控制节奏与 API 保持不变。既有 PNG 路径上限仍为 1280×720，[协议门禁报告](docs/performance/preview-protocol-gate.md)记录的是这条路径的限制。新桌面 GPU 流通过独立的低频可靠 HTTP 操作管理固定输入、修订、尺寸和寿命，播放控制与帧使用本机通道，不逐帧消耗 HTTP receipt。

桌面生产路径是 **同一 Worker 的普通 Skia Vulkan surface → 一次 GPU blit（含方向修正）→ 三槽共享 DMA-BUF → Electron SharedTexture／VideoFrame**。最终帧不经过 CPU 像素回读或 PNG；有一次整帧 GPU copy 和 queue-idle 同步等待，不能称严格零拷贝。脚本、材质等内部已有读回与 Chromium 内部复制另计。每池最多 64 MiB、每 host 最多两流及 128 MiB，实际分配与设备握手成功后才公布流。

当前支持范围为本机 Linux x64／Wayland／AMD 已验证组合，Electron 固定 45.2.1。P0 已取得有限正确性和正常排空证据；P1/P2 已实现。首轮生产运行已取得 173 个唯一 drawn/native-completed 帧，切换分辨率时发生退出，完整生命周期尚未验收；32 项独立真实 HTTP 检查已通过（含 1440p 换代、owner/CAS/claim 与实际 Worker 退出），证据见 [HTTP 有限验收](docs/performance/evidence/production-2026-11-01/http-negative.json)。生产联调及 P3 的最终证据以[实施计划](docs/plans/gpu-shared-texture-preview-plan.md)和[流合同](docs/architecture/gpu-preview-stream-v1.md)为准。运行时 Error 诊断停止新帧并正常排空旧租约；设备、通道或消费者完成状态未知时隔离池并 fail-stop，不把 crash／超时当作安全归槽。构建、单元测试或 capability 成功均不代表达到目标帧率、屏幕扫描帧率、任意内容吞吐或跨平台验收。

历史独立 P0 入口位于 `desktop/` 与 `native/gpu-preview/`，锁定 Electron 45.2.1，不加载编辑器或真实工程。先构建原生工具，再运行有限正确性验证：

```bash
pnpm setup:p0
headers=$(bash native/gpu-preview/bootstrap-headers.sh)
VULKAN_INCLUDE_DIR="$headers" bash native/gpu-preview/build.sh
pnpm preview:p0 --mode probe
pnpm preview:p0 --mode correctness --slots 1 --frames 1
pnpm preview:p0 --mode finite --slots 3 --frames 12
```

首次 Electron 下载可设置 `HTTPS_PROXY=http://127.0.0.1:7897 HTTP_PROXY=http://127.0.0.1:7897 ELECTRON_GET_USE_PROXY=1`；缓存、每次独立 profile、日志与结果均位于 App `.local/`。入口要求系统允许非特权 sandbox，禁止使用 `--no-sandbox`；缺 Vulkan 头时使用本地固定头文件并设置 `VULKAN_INCLUDE_DIR`，不修改系统安装。`correctness` 允许取样与截图，`finite` 不进行像素回读；这不是持续吞吐矩阵或 GPU 零复制证明。
