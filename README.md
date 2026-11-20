# MakoKaraGenerator App

本仓库把 Backend 与 Web 的版本化源码整合为独立应用，提供浏览器入口和 Electron 桌面入口。桌面复用同一编辑器，Windows 默认使用共享内存（SHM）预览，Linux 默认使用 DMA-BUF 共享纹理；浏览器继续使用既有 PNG 静态预览。

2026-11-17 Ruby 排版设置已合入 `main`：产品 **0.115.0**、HTTP 合同 **1.56.0**。样式库和编排单句支持注音字号比例与正文距离，相邻注音按连续排版的自然字距退让。用户桌面手测通过，Linux x64 AppImage 已生成并替换本机旧包；实现、格式门禁和发布指纹见 [Ruby 验收记录](docs/integration/2026-11-17-ruby-layout-settings.md)。

Windows x64 便携包命令为 `pnpm package:windows`。媒体特征探针受 Windows Job Object 的 128 MiB 硬限额保护；实机验证见 [M4 验收记录](docs/integration/2026-11-20-cross-platform-m4-windows.md)。真实 exFAT 盘符验收暂缓。

2026-11-03 歌词导入兼容修复：后端 `0.110.2`、HTTP 合同 `1.42.2`。已修复 ASS 上传／新建准入，并从 MakoLrcStudio 的源打轴注释恢复正文、ruby 与片段时间；有效 TTML 的 project／AMLL 导入导出已验证往返。Downloads 两份原始 TTML 因导出时清空 body 命名空间而不符合 TTML 结构，按要求暂缓处理。真实样本、HTTP 和 Electron 验证见[歌词导入修复记录](docs/integration/2026-11-03-lyrics-import.md)。

2026-11-03 已合入静态外辉光后端并接通重构后的前端：后端 `0.110.0`、HTTP 合同 `1.42.0`。在样式库的“外观 → 颜色与描边 → 辉光”配置颜色、不透明度、扩张和模糊，再从外观页分配模板。取消“辉光写进模板”表示继承，选择“无辉光”表示明确关闭；只包含部分辉光属性的模板会显示“部分覆盖”。工程／歌词／模板 schema 为 `29/18/7`，旧版本由现有门禁直接拒绝，不迁移或改写原文件。实施与验证见[辉光整合记录](docs/integration/2026-11-03-glow.md)。

当前 MVP：2026-11-01 用户本机以 1280×720、24fps 预览设置手测，低效果可流畅预览，高效果／复杂效果仍明显卡顿；用户认可最小原型实现，对高效果性能仍不满意。用户判断传输链路未显示阻断级问题；这不替代尚未通过的 P3 自动门槛。具体观察及导出约 3fps 的进度条估算边界见[用户本机手测与原型评价](docs/performance/gpu-preview-production-2026-11-01.md#用户本机手测与原型评价)。

`backend/` 来源 `a4783fad683bcfef3f2db4042e8963e5228830b5`，`frontend/` 来源 `c2d06fd68a051de1ab1018b9cd2b18ef2ccd8bab`。路径、分支、tree 与源工作树排除项见 [source-snapshots.json](source-snapshots.json)。两个源仓库保持原样；App 保留自己的 Git 历史，用户授权的私有远程同步目标为 `https://github.com/Zydr114/MakoKaraGenerator-App`，暂不添加许可证；实际推送结果以远程核验为准。上游已有的文档、AGENTS 和历史验收保持原归属，本仓库工作规则见根 [AGENTS.md](AGENTS.md)。

浏览器和桌面入口支持 Windows x64 与 Linux x64。从源码构建需要 Node >=24、pnpm（也可通过 Corepack 使用）、Python >=3.10、仓库 `backend/global.json` 允许的 .NET 10 SDK，以及 Skia 使用的字体和系统 native 库。开发运行使用本机 FFmpeg/FFprobe，Windows 可从 PATH 或 WinGet 安装目录发现；Windows Portable 则只用包内工具。Linux 发布还需要 flock；Windows 使用独立旁路文件的 LockFileEx 锁。

在 App 根目录运行：

```bash
pnpm run setup         # 按 frontend/pnpm-lock.yaml 安装依赖
pnpm build             # 类型检查、Web生产构建、匹配Server与完整Worker发布
pnpm start             # 启动构建结果，打开 http://127.0.0.1:5173
# 或在完成build之后：
pnpm dev               # 同一匹配后端＋Vite开发页面
# Windows / Linux 桌面入口：
pnpm desktop           # 检查桌面构建，过期时自动重建，再打开Electron编辑器（http://127.0.0.1:5180）
pnpm build:desktop     # 只构建不启动：Web＋匹配 Server/Worker＋平台预览原生库
pnpm package:appimage  # Linux x64：校验现有桌面构建并输出 .local/release/MakoKaraGenerator-<版本>-x86_64.AppImage
# 可选不同本机端口：
pnpm start --port 5174
```

首次构建需要下载 NuGet/npm 依赖时可设置 `HTTPS_PROXY=http://127.0.0.1:7897`，loopback 请求直连。根入口始终使用 App 内的 `backend/`、`frontend/` 和新发布的 `.local/backend-dist/<UTC时间>/`；`.local/backend-current.json` 指向最近成功发布。构建失败保留独立目录与日志，不覆盖旧发布。缺少构建时明确失败，不回退源仓库的 `artifacts`；外部 `MAKO_BACKEND_REPO`、外部 `MAKO_BACKEND_DIST` 和逃出 App 的部署符号链接均拒绝。

页面与 Server 仅绑定 loopback；Server 随机端口，Web 默认 5173，桌面版固定 5180（页面的本机设置按地址保存，端口固定才留得住主题、布局和渲染设备偏好）。Linux 的运行时根目录为 App `.local/`；Windows 默认为 `%LOCALAPPDATA%\makokara-generator`，可设置绝对路径 `MAKO_RUNTIME_HOME`，实际根目录为其下的 `makokara-generator` 子目录。Windows 自定义运行时卷必须支持持久化 ACL。`start`／`dev` 复用运行时根目录下的 `profile/`，保存工程、状态和日志；不要同时启动两个实例共用此 profile。前端连接信息写入该根目录的 `run/backend.json`，后端在 `profile/runs/run-*/` 内持有 bootstrap。Linux 使用 0700/0600 权限，Windows 使用仅当前用户 SID 的受保护可继承 DACL；凭证不打印。页面的 bootstrap 通道要求准确 Host、本机地址与同源 fetch；直接在地址栏访问会拒绝。

当前 Linux 桌面使用独立 `.local/desktop-profile/` 与 `.local/desktop-electron-profile/`；其私有启动配置在 `.local/run/desktop-<PID>.json`，只交给本次 Electron main，进程退出后删除，残留的会在下次启动时按进程身份清理。桌面构建会验证实际 native ABI 符号和部署哈希，普通 `pnpm build` 的发布不能用于 GPU 桌面启动。

`pnpm desktop` 启动前按源码内容哈希判断构建是否过期：后端、GPU 预览原生源码有改动或还没有桌面构建时，整套重建（等同 `pnpm build:desktop`）；只有前端改动时只重建 Web（构建记录在 `.local/web-build.json`）。桌面版的成片预览（编排页、外观页）支持 GPU 和 CPU SHM；Linux 还支持 GPU DMA-BUF。关窗时工程、样式库模板或作者草稿有未保存的修改会先弹窗确认。

按 Ctrl+C 停止根 App；Linux 也可向根 App 进程发送 SIGTERM。自动化启动可设置 `MAKO_APP_SHUTDOWN_STDIN=1`，向启动器 stdin 写入 `SHUTDOWN` 加换行，或关闭 stdin，以受控方式退出；Windows 不应将强制终止进程当作优雅退出。Linux 桌面入口先排空共享纹理消费者，随后关闭自有 Web、等待自有后端排水，清除 bootstrap；不按名称终止其他进程。`.local/`、构建目录、依赖和私人配置均被 Git 忽略。

## Windows x64 Portable

在 Windows x64 开发机上，按[原生探针构建说明](backend/native/media-presentation/README.md)准备固定的 LGPL FFmpeg devkit：用 `MakoMediaPresentationWindowsDevkitArchive` 指向 ZIP 绝对路径，把 CMake、Ninja 和 llvm-mingw 加入 PATH，并安装用于 SHM 的 Visual Studio Build Tools C++ 工具链。然后按 `scripts/build-windows-export-tools.ps1` 的清单把固定输入归档放入 `.local/windows-media-cache`。构建无需 WSL：

```powershell
pwsh -NoProfile -File scripts/build-windows-export-tools.ps1
pnpm package:windows --build
```

已有含原生探针的 Windows 桌面构建时可省略 `--build`。输出默认是 `.local/release/MakoKaraGenerator-<版本>-windows-x64.zip`；用 `--out` 自定义 ZIP 路径，用 `--exe-name` 指定主程序文件名。用 `--ffmpeg-dir` 可选择另一目录下由同一配方生成的媒体工具包。打包不覆盖现有 ZIP。

使用时解压整个 ZIP 再双击 `MakoKaraGenerator.exe`（或自定义名称）。无需预装 Node、Python、.NET 或 FFmpeg；Vulkan 预览仍取决于本机显卡驱动。工程、配置和私有启动凭证默认写入 `%LOCALAPPDATA%\makokara-generator`，不会写到解压目录；自定义运行时卷必须支持持久化 Windows ACL。包内导出用 FFmpeg/FFprobe 与受控媒体探针彼此独立，各自附有对应源码、构建配方及第三方许可。ZIP 未做代码签名。

## 有限验证入口

```bash
pnpm test:launcher     # 启动失败清理、正常停止、符号链接与旧产品部署门禁
pnpm test:unit         # 上游前端单元回归
python scripts/cross-platform-g1-http.py --dist .local/backend-dist/<已发布目录> --output .local/g1-<新证据目录> # 有限 G1 HTTP 实测
pnpm smoke:browser    # start运行时：新建、编辑、撤销、暂停seek、PNG显示、关闭工程
pnpm smoke:glow --url http://127.0.0.1:5173  # 隔离profile运行时：辉光模板编辑、保存应用、继承开关和浅深主题
pnpm smoke:layout     # start运行时：几种窗口尺寸下编排、外观页画布完整显示、不需要滚动
pnpm smoke:desktop    # 自己启动隔离的桌面会话：默认走GPU、无CPU回读、播放／seek／编辑／换规格／切页刷新、选CPU退回PNG、关窗确认与正常退出
node scripts/windows-portable-g4.mjs --zip <ZIP路径> # 干净 NTFS 解包与启动、GPU SHM、成片导出及关窗清理；改名包加 --exe <主程序名>
```

浏览器冒烟使用本机已有 Chromium（可设 `CHROME_PATH`），独立临时浏览器 profile 与临时音频/歌词，不操作用户已有浏览器。截图与成功结果保存在 `.local/integration/`。本次构建和真实运行记录见 [docs/integration/2026-11-01-web-integration.md](docs/integration/2026-11-01-web-integration.md)；桌面版一条命令启动与默认 GPU 预览的整合记录、已知问题见 [docs/integration/2026-11-03-desktop-app.md](docs/integration/2026-11-03-desktop-app.md)。

浏览器原预览控制节奏与 API 保持不变。既有 PNG 路径上限仍为 1280×720，[协议门禁报告](docs/performance/preview-protocol-gate.md)记录的是这条路径的限制。新桌面 GPU 流通过独立的低频可靠 HTTP 操作管理固定输入、修订、尺寸和寿命，播放控制与帧使用本机通道，不逐帧消耗 HTTP receipt。

Linux DMA-BUF 生产路径是 **同一 Worker 的普通 Skia Vulkan surface → 一次 GPU blit（含方向修正）→ 三槽共享 DMA-BUF → Electron SharedTexture／VideoFrame**。最终帧不经过 CPU 像素回读或 PNG；有一次整帧 GPU copy 和 queue-idle 同步等待，不能称严格零拷贝。脚本、材质等内部已有读回与 Chromium 内部复制另计。每池最多 64 MiB、每 host 最多两流及 128 MiB，实际分配与设备握手成功后才公布流。

以下 DMA-BUF 性能结论限定本机 Linux x64／AMD 组合，Electron 固定 45.2.1；Wayland 以每次运行的实际进程证据为准。P0 已取得有限正确性和正常排空证据，P1/P2 已实现；**P3 两次正式 720p24 的滑动5s最低分别117／116帧，均未达118帧门槛；本轮以失败收口，1440p／600s未测**。[attempt1](docs/performance/evidence/production-2026-11-01/synthetic-formal-attempt1.json)与显式普通调度的[attempt2](docs/performance/evidence/production-2026-11-01/synthetic-formal-attempt2.json)各保留约180s、4320个唯一完整消费；attempt2的179个快照复核实际main／renderer／GPU／Worker身份与普通调度通过，仍不能用总体帧率抵消窗口失败或确定单一瓶颈根因。[真实 Runtime 有限矩阵](docs/performance/evidence/production-2026-11-01/runtime-finite-summary.json)完成20次切页重附着、10次公开resize及精确新代次消费，但输入生效后p95 164.06ms仍超过150ms候选；五秒阶段不构成持续性能。32 项独立 [HTTP 检查](docs/performance/evidence/production-2026-11-01/http-negative.json)覆盖 owner/CAS/claim、1440p 池换代与实际 Worker 退出。

[真实作者错误补验](docs/performance/evidence/production-2026-11-01/author-error-inflight.json)确认 `Evaluate` Error 发生时旧租约仍未完成，Worker 等待该租约的精确 native completion 后报告 `failed + drained:true`；同一 App 随后成功消费新健康流。设备、通道或消费者完成状态未知时隔离池并 fail-stop；Server 清理异常已修复，四类[真实丢失复验](docs/performance/evidence/production-2026-11-01/desktop-faults-fixed.json)按受控非零退出，不能称为成功 drain 或安全归槽。详细来源与未通过门槛见[生产验收记录](docs/performance/gpu-preview-production-2026-11-01.md)、[实施计划](docs/plans/gpu-shared-texture-preview-plan.md)及[流合同](docs/architecture/gpu-preview-stream-v1.md)。构建、单元测试或 capability 成功均不代表屏幕呈现帧率、任意内容吞吐或跨平台验收。

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
