# Web 后端启动入口（Windows / Linux x64）

启动器使用 Python >= 3.10 标准库。发布需要 .NET SDK；Linux 还需要 `flock`，Windows 使用系统 PowerShell、纯 SID ACL 与 Win32 `LockFileEx`。运行已发布的 self-contained 包不依赖共享 ASP.NET runtime、Avalonia 或声卡；仍需对应平台的字体、渲染与 FFmpeg 环境，Linux 还依赖 Fontconfig／FreeType／ICU 等系统库。此入口不托管前端、不向任意页面提供 bootstrap。

按宿主系统及架构自动选择 `win-x64`（Windows AMD64／x86_64）或 `linux-x64`（Linux x86_64），其他平台／架构明确拒绝。以下命令均从 **App 仓库根目录**执行；Windows 使用 `python`，Linux 可替换为 `python3`。

## 发布与工具发现

发布到**不存在的新目录**：

```powershell
python backend/tools/backend-web-host/host.py publish --dist .local/backend-dist/my-new-build
```

Server 和完整 Worker 分别以 self-contained Release 及当前宿主 RID 发布，使用 `-m:1 /p:UseSharedCompilation=false`。Linux 的 dotnet publish 共用 `flock -o /tmp/mkg-backend-b4-dotnet.lock`；Windows 使用 `%LOCALAPPDATA%\makokara-generator\locks\dotnet-publish.lock` 独立旁路空文件上的阻塞排他 `LockFileEx`，不锁部署清单，不使用有约 10 秒重试限制的 `msvcrt.locking`。未设置有效绝对 `LOCALAPPDATA` 时，Windows 回退到用户主目录下 `AppData\Local`。

发布清单记录版本、源码 HEAD、生产输入 SHA、dirty diff 指纹与部署文件 SHA；发布期间生产输入或 HEAD 变化则拒绝标为成功。SourceLink 会让仅提交变化也改变程序集字节，因此发布和运行都要求两端 `MakoKaraGenerator.Scripting.Abstractions.dll` 完全一致。发布失败保留目录和日志，修复后使用新目录。Worker 必须保留完整依赖与 `script-refs`；不覆盖、合并或删除旧目录。

运行示例（PowerShell；profile 应长期复用，Origin 为明确的 Vite 开发地址）：

```powershell
python backend/tools/backend-web-host/host.py run --dist .local/backend-dist/my-new-build --profile "$env:LOCALAPPDATA/makokara-generator/profiles/dev" --origin http://localhost:5173 --origin http://127.0.0.1:5173
```

Linux 可将 `--profile` 显式设为 `$HOME/.local/share/makokara-generator/profiles/dev`。`host.py run` 始终要求显式 `--profile`，不会根据 `MAKO_RUNTIME_HOME` 自动选择 profile。App／桌面启动器的 Windows 默认敏感目录位于 `%LOCALAPPDATA%\makokara-generator`，其 `MAKO_RUNTIME_HOME` 绝对路径覆盖由 App 启动层解析；直接调用此脚本时须自行传入对应 `--profile`，Windows 发布锁仍使用上述 `LOCALAPPDATA` 路径。

可用 `--ffmpeg /absolute/executable --ffprobe /absolute/executable` 指定可信工具。默认先通过 `PATH` 查找；Windows 未找到时继续查找 `%LOCALAPPDATA%\Microsoft\WinGet\Links` 及 FFmpeg 的 WinGet 包目录，Linux 未找到时回退到 `/usr/bin/ffmpeg` 与 `/usr/bin/ffprobe`。无法发现工具时明确报错，工具最终必须是绝对文件路径（Linux 还要求可执行权限）。

## 私有目录与资源配置

profile 内 `state/` 和 `projects/` 持久化，`runs/run-*/` 为每次新建的私有目录，包含独立临时资源、bootstrap 与日志。Linux 目录使用 `0700`、凭证使用 `0600`；Windows 要求卷支持 `FILE_PERSISTENT_ACLS`，对每个准入目录完整核验并在需要时设置仅当前用户 SID 的受保护可继承 FullControl DACL，使后续文件继承私有权限。既有子目录不能凭祖先缓存跳过核验；只缓存本进程已完整核验的精确目录，不处理同用户在准入后主动改写 ACL 的情形。目录不得经过符号链接／junction；不支持持久化 ACL 的自定义 Windows profile（如 exFAT）明确拒绝。Server 的独占 state lease 拒绝并发使用同一 profile。

启动器清除继承的 `MAKO_BACKEND_*`，仅保留资源限额白名单，再设置自身明确配置；access 120 秒／lease 300 秒与其他生产默认保持。

上传按 64 KiB 缓冲区流式固定到私有文件，默认不配置单文件和总字节上限。可用 `--max-asset-bytes`／`--max-asset-quota-bytes` 设置正 Int64 字节限额，或继承 `MAKO_BACKEND_MAX_ASSET_BYTES`／`MAKO_BACKEND_MAX_ASSET_QUOTA_BYTES`。两者均设置时单文件上限不得超过总配额；暂存文件也占配额，未接受或失败上传会释放。资源数量、并发准备、TTL、MIME 及客户端授权继续生效。

导出限额支持 `--export-timeout-seconds`、`--export-max-frames`、`--export-max-duration-us`、`--export-max-fps`、`--export-max-canvas-width`、`--export-max-canvas-height`、`--export-max-raw-frame-bytes`、`--export-max-media-bytes`、`--export-max-audio-bytes`、`--export-max-compressed-audio-duration-us` 和 `--export-max-artifact-storage-bytes`。对应环境变量为 `MAKO_BACKEND_` 加参数名转大写下划线；参数覆盖环境变量，省略参数则保留对应环境配置，显式 `none`／`unlimited` 清除配置。实际生效值以 capabilities 为准。

## 就绪与受控退出

stdout 稳定接口：

```text
BACKEND_READY http://127.0.0.1:<random-port> <hostInstanceId>
WEB_BACKEND_READY {"addresses":["http://127.0.0.1:<random-port>"],"hostInstanceId":"...","credentialFile":".../bootstrap.json","runDirectory":"...","version":"<current-version>","pid":1234}
WEB_BACKEND_STOPPED {"exitCode":0,"runDirectory":"..."}
```

可信启动器可解析 `WEB_BACKEND_READY ` 后的 JSON；bootstrap／access／resume token 均不输出。仅通过 `credentialFile` 读取私有凭证，禁止打印、写入 URL 或提交到前端。正常退出删除本次自有 bootstrap，保留 state、projects 与日志；下一次启动获得新随机端口、host 身份和凭证。日志默认位于 `<runDirectory>/server.log`。启动失败返回非零并报告诊断；Server 启动／退出问题会指向日志。ready 后仍须核对 `audioPreparation.enabled`，真实 PNG 才证明渲染可用。

交互运行可按 Ctrl+C。Linux 可向**启动器 PID**发送 SIGTERM，启动器向其拥有的 Server 转发信号；Windows 收到可处理的 SIGINT／SIGBREAK／SIGTERM 时，通过 Server stdin 发送 `SHUTDOWN`，等待 Worker/native 排水。Windows 外部强制终止不能视为优雅退出证据。

受控父进程应设置 `MAKO_HOST_SHUTDOWN_STDIN=1` 并保持启动器 stdin 管道打开；写入独占一行 `SHUTDOWN` 或关闭管道会请求受控退出。启动器始终启用自身 Server 的 stdin 退出协议，等待退出，超时才终止其拥有的 Server。不要按进程名称批量终止；须等待启动器退出后再重启同一 profile。

## 定向回归与真实 HTTP 验收

```powershell
python backend/tools/backend-web-host/test_host.py
```

覆盖 HEAD 漂移、两端脚本程序集不一致、匹配发布、目录 ACL／链接准入，以及 Windows 独立进程锁等待超过 15 秒和 `tempfile` 运行目录 DACL；此测试不启动实际 Server。

Windows M1 / G1 真实 HTTP 入口使用根目录的 [cross-platform-g1-http.py](../../../scripts/cross-platform-g1-http.py)（须使用新证据目录；创建并清理自有隔离 profile）：

```powershell
python scripts/cross-platform-g1-http.py --dist .local/backend-dist/my-new-build --output .local/g1-http/my-new-run
```

支持显式 `--ffmpeg`／`--ffprobe` 和 `--timeout`，要求 `--output` 尚不存在。此脚本核对真实硬件 Vulkan 可用性、音频准备及波形、CPU／GPU 静态 PNG、CPU／Vulkan 视频导出、SDK v2 编译及正式验证帧、实际导出字节超限失败，并通过 stdin 退出协议收尾。`summary.json` 记录部署、探针与宿主脚本指纹及脱敏结果；实际状态以本次结果为准。此入口及 Windows G1 实测不证明 Linux 实机已通过，也不代表 M2～M4 连续预览或便携包已经完成。

既有 Linux HTTP 冒烟入口仍保留：

```bash
python3 backend/tools/backend-web-host/smoke.py \
  --dist "$PWD/.local/backend-dist/my-new-build" \
  --output "$PWD/.local/backend-smoke/my-new-run"
```

该入口涵盖默认 TTL 登记／resume／心跳、真实 Worker 准备短 WAV、新建／读歌词／改时／撤销／重做、静态 PNG 解码与身份核对、保存→关闭→重开、SIGTERM 排水及重启身份检查。它沿用 Linux 信号与进程观察方式，不作为 Windows 受控退出的验收入口；也不代表独立 Runtime 像素 oracle 或完整浏览器验收。
