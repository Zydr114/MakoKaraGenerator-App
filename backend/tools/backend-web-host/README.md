# Web 后端启动入口（Linux x64）

仅依赖 Python 3 标准库、.NET SDK、flock 和本机已有 native 环境。运行已发布包不依赖共享 ASP.NET runtime、Avalonia 或声卡；渲染仍依赖字体／Fontconfig／FreeType／ICU 等系统库。此入口不托管前端、不向任意页面提供 bootstrap。

从仓库根发布到**不存在的新目录**：

```bash
python3 tools/backend-web-host/host.py publish \
  --dist "$PWD/artifacts/web-backend/0.95.0"
```

Server 和完整 Worker 分别 self-contained Release/linux-x64 publish，dotnet 共用 `flock -o /tmp/mkg-backend-b4-dotnet.lock`、`-m:1 /p:UseSharedCompilation=false`。发布清单记录版本、源码 HEAD、生产输入 SHA、dirty diff 指纹与部署所有文件 SHA；发布期间生产输入变化则拒绝标为成功。发布失败保留原目录和日志，修复后使用新目录。Worker 必须保留完整依赖与 `script-refs`。不覆盖、合并或删除旧目录。

前台运行；profile 应长期复用，两个 Origin 是明确的 Vite 开发地址：

```bash
python3 tools/backend-web-host/host.py run \
  --dist "$PWD/artifacts/web-backend/0.95.0" \
  --profile "$PWD/artifacts/web-backend/dev-profile" \
  --origin http://localhost:5173 --origin http://127.0.0.1:5173
```

可用 `--ffmpeg /absolute/executable --ffprobe /absolute/executable` 更换可信工具；默认固定 `/usr/bin/ffmpeg` 与 `/usr/bin/ffprobe`。启动器清除继承的 `MAKO_BACKEND_*`，仅保留下述资源限额白名单，再设置自身明确配置；access120秒／lease300秒与其他生产默认保持。profile 内 `state/` 和 `projects/` 持久化、`runs/run-*/` 为每次新建的0700私有目录，包含独立临时资源、0600 bootstrap 与日志。Server 的独占 state lease 拒绝并发使用同一 profile。

上传按64KiB缓冲区流式固定到私有文件，默认不配置单文件和总字节上限。部署可用 `--max-asset-bytes`／`--max-asset-quota-bytes` 设置正Int64字节限额，或继承 `MAKO_BACKEND_MAX_ASSET_BYTES`／`MAKO_BACKEND_MAX_ASSET_QUOTA_BYTES`。两者均设置时单文件上限不得超过总配额；暂存文件也占配额，未接受或失败上传会释放。资源数量、并发准备、TTL、MIME及客户端授权继续生效。

导出限额支持 `--export-timeout-seconds`、`--export-max-frames`、`--export-max-duration-us`、`--export-max-fps`、`--export-max-canvas-width`、`--export-max-canvas-height`、`--export-max-raw-frame-bytes`、`--export-max-media-bytes`、`--export-max-audio-bytes`、`--export-max-compressed-audio-duration-us` 和 `--export-max-artifact-storage-bytes`。对应环境变量为 `MAKO_BACKEND_` 加参数名转大写下划线；参数覆盖环境变量，省略参数则保留对应环境配置，显式 `none`／`unlimited` 清除该配置。实际生效值以capabilities为准。

stdout 稳定接口：

```text
BACKEND_READY http://127.0.0.1:<random-port> <hostInstanceId>
WEB_BACKEND_READY {"addresses":["http://127.0.0.1:<random-port>"],"hostInstanceId":"...","credentialFile":".../bootstrap.json","runDirectory":"...","version":"0.95.0","pid":1234}
WEB_BACKEND_STOPPED {"exitCode":0,"runDirectory":"..."}
```

前端可信启动器可解析 `WEB_BACKEND_READY ` 后的 JSON；bootstrap／access／resume token 均不输出。只通过 `credentialFile` 读取私有启动凭证，禁止打印、写URL或提交到前端。正常退出删除本次自有 bootstrap，保留 state、projects 与日志；下一次启动获得新随机端口、host 身份和凭证。日志默认位于 `<runDirectory>/server.log`。启动失败返回非零并指出日志路径；ready 后仍须核对 `audioPreparation.enabled`，真实PNG才证明渲染可用。

按 Ctrl+C 或向**启动器 PID**发送 SIGTERM。启动器只向自身 Server 转发信号，并等待 Server 对 Worker/native 排水完成；不使用按名称杀进程，不删除用户目录。运行在前台便于外部 `dev-backend.mjs` 用 `spawn` 管理进程，等待退出后才能重启同一 profile。

真实 HTTP 冒烟（新证据目录；会新建自有音频／工程）：

```bash
python3 tools/backend-web-host/smoke.py \
  --dist "$PWD/artifacts/web-backend/0.95.0" \
  --output "$PWD/artifacts/backend-g1-w0-0751/http-default-final1"
```

包括默认TTL登记／resume／心跳、真实Worker准备短WAV、新建／读歌词／改时／撤销／重做、静态PNG完整解码及身份核对、保存终态→关闭终态→重开、SIGTERM排水、重启后旧access拒绝及新身份打开持久工程。记录脱敏原始请求／响应、终态、PNG摘要和观察进程身份；不声称独立Runtime像素oracle或完整浏览器W0验收。G1短TTL受控HTTP证据与此默认TTL链路分开归档。
