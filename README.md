# MakoKaraGenerator App

本仓库把本地最新 Backend 与 Web 的 Git tracked 源码复制为独立应用，当前选择 Web 入口，便于复用现有编辑器并隔离预览协议验证。这是最小整合方案，没有进行 Web 与 Electron 的性能比较。

`backend/` 来源 `a4783fad683bcfef3f2db4042e8963e5228830b5`，`frontend/` 来源 `c2d06fd68a051de1ab1018b9cd2b18ef2ccd8bab`。路径、分支、tree 与源工作树排除项见 [source-snapshots.json](source-snapshots.json)。两个源仓库保持原样；App 只有自己的 Git 历史，没有 remote，也没有上传。上游已有的文档、AGENTS 和历史验收保持原归属，本仓库工作规则见根 [AGENTS.md](AGENTS.md)。

需要 Linux x64、Node 26、pnpm、Python 3、仓库 `backend/global.json` 允许的 .NET 10 SDK、flock、FFmpeg/FFprobe，以及 Skia 使用的字体和系统 native 库。本机实测使用 Node 26.12.1、pnpm 11.7.0、.NET SDK 10.0.113。

在 App 根目录运行：

```bash
pnpm run setup         # 按 frontend/pnpm-lock.yaml 安装依赖
pnpm build             # 类型检查、Web生产构建、匹配Server与完整Worker发布
pnpm start             # 启动构建结果，打开 http://127.0.0.1:5173
# 或在完成build之后：
pnpm dev               # 同一匹配后端＋Vite开发页面
# 可选不同本机端口：
pnpm start --port 5174
```

首次构建需要下载 NuGet/npm 依赖时可设置 `HTTPS_PROXY=http://127.0.0.1:7897`，loopback 请求直连。根入口始终使用 App 内的 `backend/`、`frontend/` 和新发布的 `.local/backend-dist/<UTC时间>/`；`.local/backend-current.json` 指向最近成功发布。构建失败保留独立目录与日志，不覆盖旧发布。缺少构建时明确失败，不回退源仓库的 `artifacts`；外部 `MAKO_BACKEND_REPO`、外部 `MAKO_BACKEND_DIST` 和逃出 App 的部署符号链接均拒绝。

页面与 Server 仅绑定 loopback；Server 随机端口，Web 默认 5173。两个命令复用 App 专有 `.local/profile/`，保存的工程、状态和日志保留在其中；不要同时启动两个实例共用此 profile。前端连接信息写入私有 `.local/run/backend.json`（0600）；后端启动器还在本次 `.local/profile/runs/run-*/` 内持有私有 bootstrap，不打印凭证。页面的 bootstrap 通道要求准确 Host、本机地址与同源 fetch；直接在地址栏访问会拒绝。

按 Ctrl+C 或向根 App 进程发送 SIGTERM，入口先关闭自有 Web，再等待自有后端排水，清除 bootstrap；不按名称终止其他进程。`.local/`、构建目录、依赖和私人配置均被 Git 忽略。

有限验证入口：

```bash
pnpm test:launcher     # 6项启动失败清理、正常停止和符号链接边界测试
pnpm test:unit         # 上游前端单元回归
pnpm smoke:browser    # start运行时：新建、编辑、撤销、暂停seek、PNG显示、关闭工程
```

浏览器冒烟使用本机已有 Chromium（可设 `CHROME_PATH`），独立临时浏览器 profile 与临时音频/歌词，不操作用户已有浏览器。截图与成功结果保存在 `.local/integration/`。本次构建和真实运行记录见 [docs/integration/2026-11-01-web-integration.md](docs/integration/2026-11-01-web-integration.md)。

原预览控制节奏与 API 保持不变。目标原始 RGBA／2560×1440 吞吐矩阵已在门禁停止：当前公开接口返回 PNG，预览尺寸上限 1280×720；更大尺寸真实请求被拒绝。见 [协议门禁报告](docs/performance/preview-protocol-gate.md)。正常 PNG 预览、单元回归和构建成功不代表完成性能验收，也不代表完整 W1～W8 产品验收或跨平台验收。

GPU 共享纹理 P0 入口独立位于 `desktop/` 与 `native/gpu-preview/`，锁定 Electron 45.2.1，不加载编辑器或真实工程。先构建原生工具，再运行有限正确性验证：

```bash
pnpm setup:p0
headers=$(bash native/gpu-preview/bootstrap-headers.sh)
VULKAN_INCLUDE_DIR="$headers" bash native/gpu-preview/build.sh
pnpm preview:p0 --mode probe
pnpm preview:p0 --mode correctness --slots 1 --frames 1
pnpm preview:p0 --mode finite --slots 3 --frames 12
```

首次 Electron 下载可设置 `HTTPS_PROXY=http://127.0.0.1:7897 HTTP_PROXY=http://127.0.0.1:7897 ELECTRON_GET_USE_PROXY=1`；缓存、每次独立 profile、日志与结果均位于 App `.local/`。入口要求系统允许非特权 sandbox，禁止使用 `--no-sandbox`；缺 Vulkan 头时使用本地固定头文件并设置 `VULKAN_INCLUDE_DIR`，不修改系统安装。`correctness` 允许取样与截图，`finite` 不进行像素回读；这不是持续吞吐矩阵或 GPU 零复制证明。
