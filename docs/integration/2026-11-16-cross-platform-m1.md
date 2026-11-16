# 跨平台 M1 修复与 Windows G1 验收（2026-11-16）

结论：**G1 通过，M1 完成**。本记录对应[跨平台适配计划](../plans/cross-platform-adaptation-plan.md)的核心基座与浏览器模式，不包含 M2～M4 的 SHM 桌面预览、Windows 媒体特征封套或 Portable 分发验收。

## 来源与执行环境

- 首轮审查范围：`6442395..d33aa61`，随后修复并复核本记录所列问题。
- 最终验收发布：`.local/backend-dist/20261116T132307349Z/`，来自 `1bb1bef84d309aaadc6c1d35bd54a9fefb9e4ba5`；manifest 为 `win-x64`、self-contained、产品 `0.113.0`、`sourceDirty=false`。后续提交仅加入验收工具、证据及文档，不把旧发布标记成后续提交构建。
- 本机 Windows 11 x64；Node `24.21.0`、pnpm `12.21.0`、Python `3.14.7`、.NET SDK `10.0.401`、FFmpeg/ffprobe `9.0.2-full_build-www.gyan.dev`。
- 实际 GPU：AMD Radeon(TM) 880M Graphics，Vulkan `1.4.329`，vendor `0x00001002`；探针拒绝软件 Vulkan 适配器，验证实际结果中的 CPU/GPU 后端身份。
- App 入口与独立 HTTP 宿主均使用本仓库新构建、隔离 profile、loopback 和直连；凭证仅留在内存或宿主持有的私有 bootstrap 中。版本化证据不含凭证、个人路径、profile、构建产物或媒体文件。

## 修复与复审

| 提交 | 问题与修复 | 验证 |
| --- | --- | --- |
| `d29729a` | Windows 的 `pnpm.cmd` 所在路径有空格时，旧 `cmd /d /s /c` 参数拼接失败。统一解析入口，优先 Node 直接运行 pnpm/Corepack JS，批处理后备正确保留引号，并拒绝无法安全转交的展开字符。 | 临时真实 `.cmd` 路径含空格、括号与 `&`；旧调用失败，新调用完整保留 argv；6 项回归通过。 |
| `3a6fb6e` | 私有目录此前可能保留其他主体的显式 ACE，且祖先缓存放行未核验的现有子目录。现在替换完整 Access DACL，读回核验纯当前 SID、FullControl、保护与继承标志，只缓存核验过的精确目录。 | 其他主体 ACE 只在内存模拟；实际临时目录仅收紧权限并读回核验。独立进程在旁路空 `.lock` 文件上竞争 `LockFileEx`，持锁 17 秒、实际等待超过 15 秒。 |
| `fdcf022` | Windows Python 包装器无法用 `exec` 替换自身，导致记录到包装器而非目标 PID。改为直接启动目标并设置、核验普通优先级，保持实际 PID、stdin、退出码；Windows App 运行状态迁入 `%LOCALAPPDATA%\makokara-generator`。 | 目标进程自行报告的 PID 与 `ChildProcess.pid` 一致，普通优先级、stdin 退出及失败时只终止自有子进程均通过；根 `pnpm start` 验证隔离运行时路径与正常退出。 |
| `1bb1bef` | 实际 HTTP 启动发现 Python 3.14 的私有 `tempfile` 目录若用 `Set-Acl` 回写完整对象，会请求 `SeSecurityPrivilege`。两种宿主改用新的 `DirectorySecurity` 只更新 Access 节，避免写 owner/audit。 | 原实现对相同临时目录失败；修复后真实目录回归、宿主 9 项测试及完整 HTTP 门禁通过。 |

并发执行使用 6.1 Sol 子代理，由主代理核对改动、测试与最终证据。修复维持既有 HTTP API、浏览器 PNG 协议和 500ms 控制节奏；没有提前实现 M2 的连续预览协议，也没有启用非 Linux 媒体特征封套。

## G1 逐项结果

| 门禁 | 本轮结果 |
| --- | --- |
| 后端构建 | `dotnet build MakoKaraGenerator.slnx --no-restore -m:1 /p:UseSharedCompilation=false` 成功，零警告、零错误。 |
| 后端单测 | `dotnet test MakoKaraGenerator.slnx --no-build --no-restore -m:1`：14 个程序集，共 **8,387/8,387 报告通过**，零失败。部分旧测试在 Windows 提前返回；此计数不表示执行了 Linux 专属分支。 |
| 前端单测 | 根 `pnpm test:unit`：**55 个文件、4,558 项通过**。 |
| 宿主和启动回归 | 最终 Node **30 通过、2 Linux 专属跳过、0 失败**；Python **9/9 通过**，包含完整 DACL、`tempfile` 和真实长时间锁竞争。 |
| 生产构建 | 根 `pnpm build` 成功，Web、Server、完整 Worker 匹配发布。Vite 的既有大 chunk 提示仍存在，不是构建失败；后端构建无警告。 |
| 根入口与浏览器 | 隔离运行时根启动真实 `pnpm start --port 5197`；连接、上传 WAV/LRC、新建、改时、撤销、暂停 seek 到 1 秒、实际 PNG 显示、关闭工程全部通过，页面未处理异常为 0。PNG 为 1280×720、11,952 字节、4,189 个非黑 RGB 像素。stdin `SHUTDOWN` 后根入口退出 0，bootstrap 删除。 |
| 音频和波形 | 真实 2 秒、8 kHz、16,000 samples PCM 经 Worker 准备；全部 **9 层**波形 min/max float32 与探针独立计算的字节序列完全一致。 |
| 静态 PNG | CPU 与 Vulkan 各完成 320×180 PNG；长度、SHA、尺寸、全量 RGBA 解码、不透明度、非空内容、源修订/代次/后端身份及零错误诊断全部通过。 |
| CPU/Vulkan 视频导出 | 各完成 2 秒、24fps、48 帧 H.264 MP4。产品严格校验成功，失败帧、丢帧、fallback 帧均为 0；独立 ffprobe 实读 48 帧，FFmpeg `-xerror` 全量解码为 **11,059,200 字节 RGBA**，媒体和报告长度/SHA 一致。 |
| SDK2 | 内置 `WordEmphasis` 源包逐项 manifest SHA 校验、检查、草稿、真实 C# 编译、Vulkan 作者 PNG 通过；正式验证 **360 不透明帧 + 360 透明帧**完成。 |
| 超限防截断 | 独立 profile 将媒体预算设为 **256 字节**；真实 CPU 导出终态为 `failed`，精确失败码 `WorkerExportArtifactBudgetExceeded`，无 `exportResult`；下载 404 `exportArtifactNotFound`；`.staging` 全空，全部产物文件零残留。没有因 FFmpeg 正常退出而接受截断文件。 |
| 受控退出与清理 | 两次 HTTP 宿主均 stdin `SHUTDOWN` 退出 0，删除各自 bootstrap，两个临时 profile 随作用域清理完成。所有停止操作针对本轮自有进程，未按名称批量终止。 |

后端与前端全量测试完成后，最后一项修复仅影响 Node/Python ACL 初始化；重新运行了最终 Node/Python 回归、生产构建、根入口浏览器实测和完整 HTTP 验收。未将早期失败运行计为通过：HTTP attempt1～3 在宿主 ACL 初始化阶段失败；attempt4 已通过业务链路，但探针错误要求纯体积超限也必须保留诊断报告。源码只在存在运行诊断或 source-material 错误时保留失败报告，因此修正探针为“无引用则全部文件为空；有引用则只允许保留字节/SHA 匹配的报告”，随后 **attempt5 完整通过**。超限错误码、下载拒绝与媒体清理断言没有放宽。

## 可复现入口

以下从 App 根目录执行；依赖已安装且首次恢复完成。所有输出目录必须是新目录，HTTP 探针不替调用者构建。

```powershell
Push-Location backend
dotnet build MakoKaraGenerator.slnx --no-restore -m:1 /p:UseSharedCompilation=false
dotnet test MakoKaraGenerator.slnx --no-build --no-restore -m:1 --logger trx --results-directory .local/g1/test-results
Pop-Location

# 通过统一入口解析器运行根 pnpm 脚本，也可直接使用 pnpm test:unit / pnpm build。
node scripts/pnpm-run.mjs test:unit
node scripts/pnpm-run.mjs build

node --experimental-test-module-mocks --test tests/launcher.test.mjs tests/runtime-host.test.mjs tests/desktop-host.test.mjs tests/pnpm-command.test.mjs tests/private-fs-acl.test.mjs desktop/gpu-preview/launch-contract.test.cjs desktop/gpu-preview/session-paths.test.cjs desktop/gpu-preview/ordinary-exec.test.cjs desktop/runtime/app-paths.test.cjs
python backend/tools/backend-web-host/test_host.py

$g1Dist = (Get-Content .local/backend-current.json | ConvertFrom-Json).dist
python scripts/cross-platform-g1-http.py --dist $g1Dist --output .local/g1-http-new
```

浏览器实测须给根启动器独立的运行时目录：在启动终端设置一个新的绝对路径 `$env:MAKO_RUNTIME_HOME`，运行 `node scripts/pnpm-run.mjs start --port 5197`；另一个终端运行 `node scripts/browser-smoke.mjs http://127.0.0.1:5197`，完成后 Ctrl+C 退出启动器。自动化可设置 `MAKO_APP_SHUTDOWN_STDIN=1`，向该进程 stdin 写入 `SHUTDOWN\n`，等待退出 0 后核对 `run/backend.json` 删除。本轮使用这一受控 stdin 方式。

根 App 的 Windows 运行时默认为 `%LOCALAPPDATA%\makokara-generator`；`MAKO_RUNTIME_HOME` 指定其父目录，需为支持持久化 ACL 的绝对路径。构建及发布仍位于仓库 `.local/`。独立 `host.py` 的 profile 由 `--profile` 明确指定，发布锁默认仍在 `%LOCALAPPDATA%\makokara-generator\locks`。其启动、工具发现与退出细节见 [host.py 说明](../../backend/tools/backend-web-host/README.md)。

## 证据与边界

版本化白名单摘要：

- [最终 HTTP 证据](evidence/cross-platform-m1-2026-11-16/http.json)：发布与探针/宿主源码 SHA、工具、GPU、图像与视频哈希、波形、720 帧验证、超限、清理和退出状态。
- [测试汇总](evidence/cross-platform-m1-2026-11-16/checks.json)：后端各程序集计数、前端及最终宿主回归、构建与来源日志哈希。
- [根入口与浏览器证据](evidence/cross-platform-m1-2026-11-16/browser.json)：检查项、PNG 摘要、异常数、退出及 bootstrap 清理；已去掉临时个人路径。

本机完整日志保留于 `.local/g1-review-20261116/`，后端 build/test/TRX 在 `backend/.local/g1-review-20261116/`。HTTP 最终运行目录为 `.local/g1-review-20261116/http-attempt5/`；实际 PNG/MP4 仅留本地，不纳入版本库。

这是一轮 Windows x64 / AMD 880M 的有限功能验收，不能推出其他显卡、Linux 实机、任意内容吞吐或持续播放性能。导出用短时无音轨 MP4 验证渲染/编码与字节限制，音频准备通过独立真实 PCM/波形链路验证。M2 的双阶段 `IPreviewPort` 和 SHM、M3 的 Windows Electron 与 Linux DMA-BUF/SHM 回归、M4 的 128 MiB Job Object 媒体封套及 exFAT Portable 包继续按各自硬门禁验收；旧 GPU 计划 P3 的性能失败结论也未被本轮覆盖。
