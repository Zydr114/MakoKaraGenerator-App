# Linux M1 两项修复与复验（2026-11-17）

本次处理 [首次 Linux 冒烟](2026-11-16-linux-m1-smoke-m2-m3-feasibility.md) 的两个问题，没有启动 M2/M3 开发。Windows 无实机可用；实现保留已验证的 Windows 分支，以下运行证据均来自 Linux，不能替代 Windows 复验。

## 修复范围

- `6ea25a9`：Linux 宿主有可写 stdin 时发送 `SHUTDOWN` 并等待退出；仅无可写 stdin 时使用 SIGTERM，原有超时强制回收继续生效。Windows 退出分支未改。原本失败的既有启动器测试现通过，没有为这项修复添加测试。
- `0a6164a`：Linux FFmpeg 在文件达到 `RLIMIT_FSIZE` 后可能以对应 SIGXFSZ 的 .NET 退出码 `153` 结束，实际长度恰等预算，原有严格大于预算的监控无法识别。现在写帧提前断管和编码收尾均结合退出码与长度归类为 `OutputSizeLimitExceededException`，Worker 沿原映射返回 `WorkerExportArtifactBudgetExceeded`。不修改 Windows 的字节监控、FFmpeg 参数、Job Object 或进程启动实现。
- 写帧断管后的退出状态等待限于 Linux 的预算边界，最多 1 秒；若进程只关闭输入仍在运行，保留普通失败与清理，避免无界等待。实际大于预算直接按超限处理，监控取消仍保留超限主因。正常退出且长度恰等预算允许成功，其他普通退出码不单凭长度冒充硬限额。
- .NET 的退出码本身不能区分 SIGXFSZ 与程序主动 `exit(153)`；这里依赖受控 FFmpeg 的退出行为并联合预算长度判断，没有新增原生 wait-status 通道。等待超时仍按通用失败处理。
- 产品补丁版本 `0.113.1`，HTTP 合同保持 `1.54.0`；从新编译的 CLR metadata 重生成后端合同。前端仍使用已冻结的 `0.113.0` 合同副本，现有同 minor 补丁兼容规则允许新部署，不增加 API 或前端接线。

## 验证与证据

匹配发布源为 `0a6164a4e329bfdcc650f4165c8c8d7553b6b640`，App 内新建 `linux-x64` self-contained Server＋Worker，目录 `.local/backend-dist/linux-m1-fix-20261117`。部署如实记录 `sourceDirty=true`：原工作区的执行位/换行差异和原有未跟踪文件保留，未伪造干净构建。临时 profile、bootstrap 与本轮进程均由探针独立管理。

| 项目 | 结果 |
| --- | --- |
| 原生退出诊断 | 独立 .NET Process 启动真实 FFmpeg，256 字节硬限额下退出码 153、文件 256 字节。 |
| 回归先失败 | 旧实现的 1 帧收尾、120 帧提前断管两个真实 FFmpeg 用例均失败；后者启用即时编码/输出刷新以覆盖写帧路径，限制只施加于子进程。 |
| 导出定向测试 | `BoundedEncoderOutputTests`＋`FfmpegCommandLineTests` 共 26 项通过；新增两个硬限额用例、两个等于预算的成功/普通失败对照、一个仅关 stdin 的清理用例。 |
| Node 启动/桌面模块测试 | 57 项：49 通过、8 平台跳过、零失败。 |
| Python 宿主测试 | 9 项：7 通过、2 Windows 专属跳过。 |
| 完整后端构建 | `dotnet build backend/MakoKaraGenerator.slnx --no-restore -m:1 /p:UseSharedCompilation=false`，零警告、零错误。 |
| 合同 | 新构建 CLR metadata；freeze 写入与检查通过；validate：115 paths、128 methods、672 schemas、3995 例（2879 反例）。 |

真实 HTTP 探针共 93 次请求通过：CPU/Vulkan PNG、各 48 帧视频及完整解码核验、SDK v2 编译与 720 帧正式验证均成功；256 字节预算明确返回 `WorkerExportArtifactBudgetExceeded`、下载不可用且零残片。真实 Chrome 的新建、编辑、撤销、暂停定位、PNG 显示及关闭通过，无未处理页面异常；Node 宿主正常退出码 0。HTTP 两个宿主正常退出、bootstrap 删除，全部临时 profile 清理，未观察到本轮 Server/Worker 剩余进程。

版本化证据：[检查摘要](evidence/cross-platform-linux-m1-fixes-2026-11-17/checks.json)、[HTTP](evidence/cross-platform-linux-m1-fixes-2026-11-17/http.json)、[浏览器](evidence/cross-platform-linux-m1-fixes-2026-11-17/browser.json)、[宿主退出](evidence/cross-platform-linux-m1-fixes-2026-11-17/browser-session.json)、[合同校验](evidence/cross-platform-linux-m1-fixes-2026-11-17/contract-validation.json)。探针核对实际会话凭证未进入 HTTP 摘要，版本化证据经凭证字段/私人路径扫描。

不将此前失败记录改成成功，也不宣称全量单测、Windows、Electron 连续预览或 DMA-BUF 性能验收。完整本机日志保留在 `.local/cross-platform-linux-fix-20261116/`（开始于 11 月 16 日，最终收尾于 11 月 17 日）。
