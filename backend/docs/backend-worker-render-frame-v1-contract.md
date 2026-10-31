# Worker 静态背景 render-frame-v1 内部合同与有限验收

此接缝通过真实 `Backend.Worker` 子进程调用 `ProjectRenderSessionFactory`，从固定输入构造无歌词的背景工程快照并返回 BGRA 像素。它只证明静态背景帧的跨进程传输、完整性与回收；不接受工程文件、歌词、场景、媒体路径、效果包或脚本，也不创建 HTTP 预览会话、PNG 句柄或正式导出。

## 固定输入

`RenderFrameV1WorkerRequest` 编解码严格为 40 字节、little-endian。保留字段必须为零；不存在自动升级或兼容解码。

| 偏移 | 字节数 | 语义 |
| --- | --- | --- |
| 0 | 4 | ASCII `KFR1` magic |
| 4 | 1 | 固定版本 1 |
| 5 | 1 | bit 0 为透明背景，其余 bit 必须为零 |
| 6 | 2 | 保留零 |
| 8 | 4 | 有符号像素宽度 |
| 12 | 4 | 有符号像素高度 |
| 16 | 8 | 非负有符号整数微秒 |
| 24 | 4 | 背景 RGBA 分量 |
| 28 | 12 | 保留零 |

宽高必须为正，分别不超过 1920／1080，且 `width × height × 4` 不超过 4,194,304 字节。1920×1080 的 8,294,400 字节因总预算被拒绝；这些单边上限不意味着支持完整 Full HD。1024×1024 恰好达到 4 MiB，1024×1025 被拒绝。时间保留原整数微秒。

## 输入与输出完整性

外层沿用 `worker-1` 长度前缀 JSON：4 字节 little-endian 正文长度，JSON 正文最多 8 MiB，单条输入或输出 payload 最多 4 MiB。base64 输出仍必须落在正文总上限内。

`render-frame-v1` 请求的 `inputSha256` 校验固定输入；成功 `result` 的同一字段只关联原请求，不能拿它校验经过渲染的输出像素。`outputSha256` 独立校验像素。旧 `render` opaque 回显保留原回显哈希校验；取消回执没有像素。

代理在交付像素前验证请求 ID、result 终态、原输入 SHA、独立输出 SHA、原宽高、`stride = width × 4`、原微秒、紧密像素长度以及固定 `bgra8888-premultiplied` 格式。长度乘法只使用已验证的输入尺寸，不从不受信回执的 `int.MaxValue` 尺寸或步长计算。缺字段、错误格式、哈希或终态冲突均拒绝为 `WorkerProtocolException`；JSON 或长度前缀破坏也使用该错误类型。

透明背景返回透明画布；非透明模式仍保留背景 RGBA 的 alpha，并按 BGRA 预乘存储。工作进程在发布前核对真实 Runtime 帧的格式、尺寸、步长、时间和字节长度，复制为 IPC 输出后释放真实帧与渲染会话。

## 取消、超时与释放

帧调用的超时必须大于零且不超过一分钟，覆盖启动／握手及调用。先发 cancel 并在 grace period 内消费匹配回执；成功消费取消回执后可复用同一 Worker。未响应时发送 shutdown 并有界等待，之后终止拥有的进程树、等待退出并观察原读取任务，后续调用可重新启动。

半条写入、错误回执或 EOF 会使私有流失效并回收 Worker。Stop／Dispose 先取消当前调用，再串行等待回执与清理；并发 Dispose 共享清理任务，已排队调用最终拒绝为已释放对象。托管信号量不在还有排队调用时提前 Dispose。管道已关闭时也仍执行进程回收。

## 本次有限证据

验证命令均经 `flock -o /tmp/mkg-b3d-dotnet.lock` 串行执行，未关闭分析器：

```bash
dotnet build src/MakoKaraGenerator.Backend.Worker/MakoKaraGenerator.Backend.Worker.csproj -m:1
dotnet test tests/MakoKaraGenerator.Backend.Hosting.Tests/MakoKaraGenerator.Backend.Hosting.Tests.csproj -m:1 --filter FullyQualifiedName~WorkerProcessProxyTests
```

正常 restore／build 已证明 Runtime.Hosting → Scripting 的 Roslyn 传递依赖完整，输出 `deps.json` 与运行目录含 Roslyn DLL；没有新增 Roslyn 版本或 Worker 直接包引用。此前未正常恢复资产的执行曾缺程序集；本次移除直接包引用与 CopyLocal 补丁后正常恢复并通过真实帧验证，没有把该旧执行作为当前依赖证据。

聚焦回归覆盖 45 项：真实 Worker 全部 opaque BGRA 像素、alpha128 的预乘像素、transparent 全零像素及独立输入／输出 SHA；固定输入 magic／版本／flags／保留零／长度／时间／尺寸／4 MiB；畸形外层长度／JSON／输入 SHA；回执字段错误和整型溢出拒绝；取消后同 PID 实际帧复用；Stop、并发 Dispose；未响应时进程树释放及新 PID 复用。

超时回收用测试拥有的 Python3 IPC 探针：首个实际帧请求生成标记、启动子进程并拒绝 cancel／shutdown；断言父子 PID 均已释放，下一次启动返回受验证帧。它用于本次 Linux 生命周期有限出口，真实 Runtime 像素由另外三项真实 Worker 测试证明，不把探针像素记作真实渲染或跨平台验收。PID 与清理结果写入测试输出／TRX。

原真实帧先复现 1 失败／4 通过；修复后的聚焦 45／45。输入／输出哈希混淆变异成功编译后，三项真实像素测试均拒绝；随后六个 Worker 源文件逐字节与 SHA-256 恢复，再重建 Worker 并复验。证据在 `artifacts/backend-b3d-worker/`：`worker-pre-repair.trx`、`worker-focused.trx`、`worker-hash-mutation.trx`、`worker-final.trx` 与 `source-sha256.json`。这是有限背景帧出口，不等于完整 B3 工程／脚本 Worker、持续播放、完整预览路线或正式导出完成。
