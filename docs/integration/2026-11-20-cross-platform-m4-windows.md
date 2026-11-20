# 跨平台 M4：Windows 原生媒体封套与 Portable

2026-11-20 在 Windows 11 x64 验证产品 `0.116.1`（Electron `45.2.1`、HTTP 合同 `1.58.0`）。Windows 原生 M4 已实现，本轮约定的 NTFS 与模拟检查通过。**未测试真实 exFAT，完整 G4 保持待验。** 未修改 Linux AppImage 打包入口，未在 Linux 实机复跑，也未变更浏览器 PNG 路径、500 ms 控制节奏或 HTTP 形状。

## 媒体封套

- 进程创建时原子入 Job，再恢复执行；进程级和 Job 总 commit 硬上限均为 `128 MiB`。IOCP 实际收到包 `9` 或 `10` 才确认原生 `CapacityExceeded`，不是根据程序退出码或峰值猜测。Windows 不保证投递；若没收到，失败结果仍不能准入。
- Processes 68 项通过（含 12 项 Windows 新用例）：真实分别收到进程上限及 Job 总上限通知；在不由宿主终止的用例中，内核仍拒绝超额 commit。另测普通损坏、类似 OOM 的退出码和高峰值不冒充通知，以及隔离后回收。
- Hosting 的两项真实探针用例通过。4096×4096 RGBA16 PNG 的解码实测命中封套并返回 `media-capacityexceeded`，独立的 32×32 损坏 PNG 拒绝分析但不误报容量。此结果仅证明本次观察到通知，不代表重复运行必投递。Windows 部署按探针真实加载闭包、精确文件哈希和平台系统模块范围核验；解码进程使用独立源与元数据句柄。
- 自包含发行使用 `mkg-presentation.exe` 和固定的 LGPL FFmpeg 9.0.2 动态库，另附许可、完整对应源码与构建配方。独立的 `ffmpeg.exe` 与 `ffprobe.exe` 从固定 FFmpeg、x264、zlib 源码构建，使用 GPL 构建配置，应用以子进程方式调用，不进行链接。两次干净构建的 CLI 二进制字节相同；打包固定其 SHA-256，不依赖 MSYS 运行时 DLL。两套工具的许可与源码各自附带。

## 最终便携包

产物位于本机 `.local/release/`，未签名，不纳入 Git 或 GitHub Release。

| 文件 | 字节数 | SHA-256 |
| --- | ---: | --- |
| `MakoKaraGenerator-0.116.1-windows-x64.zip` | 391321676 | `c947ad7770ac82390d476a1305c4da53ea1097c06ae2557c42e25990c3bc7e56` |
| `MakoKaraGenerator-0.116.1-windows-x64-custom.zip` | 391321698 | `f8eddc98c647370953e96fd6f951094e8567af741002aa4e9c1a46b5352b7fb4` |

两份 ZIP 分别解压到新建 NTFS 中文含空格路径，使用 System32-only `PATH`、隔离的 `%LOCALAPPDATA%`，从包内 `MakoKaraGenerator.exe`／`Mako Portable 测试.exe` 启动。预览实际提交 Vulkan GPU SHM 帧；创建有音频及逐字歌词的工程，通过正式校验，导出并下载 1920×1080、30 fps、H.264/AAC MP4。包内 `ffprobe` 数到 60 帧，`ffmpeg` 完整解码无错。关工程再关窗，Electron 和 Server 均退出 `0`，11 个捕获的进程身份均退出，私有 bootstrap 删除。无持久化 ACL 的注入模拟确认解压卷不适合私有目录，但不影响选择支持持久化 ACL 的 `%LOCALAPPDATA%` 运行时根。模拟没有启动真实 exFAT 上的程序。

复跑入口：[`windows-portable-g4.mjs`](../../scripts/windows-portable-g4.mjs)。本机脱敏摘要为 `.local/windows-portable-g4/g4-mlalhj/summary.json`（默认名）和 `.local/windows-portable-g4/g4-HUjnI6/summary.json`（改名）；其中不含凭证、端口或进程 PID。源码构建步骤见 [README](../../README.md#windows-x64-portable)。

## 回归范围

- 后端解决方案构建零警告、零错误；Media 435 项、项目存储 24 项、Python 宿主 9 项通过。Windows 原生 CTest 3 项通过，另实测 H.264/JPEG 探针输入。
- Windows 启动器、桌面和打包共 75 项，73 项通过、2 项 Linux 专属跳过；包内工具实测 H.264/AAC、透明 PNG MOV、ProRes 4444 和音频预处理。
- 前端单元测试 4,615 项通过；合同 freeze/check/validate 通过（115 路径、129 方法、680 schema、4,029 样例）。`0.116.1` 兼容同 schema 的 `0.116.0` 工程，不重写原文件。
