# 脚本渲染升级的有限 HTTP seek 探针

从 App 仓库根运行，传入同源码构建的 Server／Worker 发布目录；输出目录必须不存在：

```bash
python3 -B backend/tools/backend-script-render-upgrade/probe.py \
  --dist "$PWD/artifacts/web-backend/current" \
  --output "$PWD/artifacts/script-render-upgrade/m00-http-seek-near"
```

探针直接继承 `backend-web-host/smoke.py` 的 `Smoke`，复用随机 loopback 启动、独立 profile、认证、请求／响应脱敏、凭证扫描和所属进程身份排水。不会连接当前用户实例或使用个人 profile。`--dist` 应为 `backend-web-host/host.py publish` 的全新匹配发布产物；`provenance.json` 保留该部署清单、探针摘要及运行参数。

默认新建 320×180、8 秒工程，导入仓库 `samples/ScriptEffects/WordEmphasis` 的完整包，通过个人模板将真实 C# controller 应用到工程歌词行。设置固定行／token 时间，然后执行 20 次串行显式 `intent: seek` 请求，循环时间为 0.5、1、2、3 秒。每次 seek 都要求新公开 generation，并核对帧的 previewId、generation、sourceRevision、requestSeq、timeUs，以及完整 PNG 的字节数、SHA、尺寸和完整解码。重复时间必须得到相同解码 RGBA SHA；这是同输入重复一致性检查，不是独立 Runtime 像素 oracle。

长工程单独运行，避免近端结果被长时间线的 v1 重放混淆：

```bash
python3 -B backend/tools/backend-script-render-upgrade/probe.py \
  --dist "$PWD/artifacts/web-backend/current" \
  --output "$PWD/artifacts/script-render-upgrade/m00-http-seek-180s" \
  --scenario 180s --timeout-seconds 120

python3 -B backend/tools/backend-script-render-upgrade/probe.py \
  --dist "$PWD/artifacts/web-backend/current" \
  --output "$PWD/artifacts/script-render-upgrade/m00-http-seek-360s" \
  --scenario 360s --timeout-seconds 180
```

`180s`／`360s` 分别新建 181／361 秒工程，循环 0.5 秒、指定远端、1 秒、相同远端；每个场景默认仍是 20 次。`--scenario all` 顺序运行三场景，各自关闭工程和预览。`--width`／`--height` 可改分辨率，`--backend cpu|gpu` 选择原 HTTP 像素入口的后端。GPU选项不测试桌面共享纹理流。`--count 2..100` 可作有限诊断，默认20；减少样本须在报告中说明。

每个 `*-sample.json` 登记：

- `acceptedMs`：实际发出 HTTP 请求前的本机单调时间到 `202` 完整读取／校验后的时间。
- `readyMs`：同一起点到轮询首次确认精确身份帧 ready；包含50 ms默认轮询分辨率及轮询 JSON 证据写入成本。
- `downloadCompleteMs`：同一起点到 PNG 响应完整读取完成，时间戳位于 PNG证据落盘及解码前。
- `pngSha256`、`rgbaSha256`、精确帧身份，以及请求期间观察到和 ready 时存活的 Worker PID／`/proc` startTicks。

这些数值不含 UI 合并等待、浏览器／桌面显示或 compositor；不能称 UI 可见端到端延迟，也不代表 SDK v2 的性能指标。工具不修改原有 500 ms播放控制节奏，只串行发送显式 seek。当前 v1 长时间线仍可能 O(t) 重放；超出 `--timeout-seconds` 会记录 `status: timeout`，使该场景和整次运行失败，不跳过后伪报通过。HTTP单次读取沿用 Smoke的30秒JSON／10秒二进制请求期限；场景期限覆盖连续轮询等待。

M00默认只观察进程复用，不要求其存在。M03完成后追加：

```bash
python3 -B backend/tools/backend-script-render-upgrade/probe.py \
  --dist "$PWD/artifacts/web-backend/m03" \
  --output "$PWD/artifacts/script-render-upgrade/m03-http-seek-near" \
  --require-reuse
```

`--require-reuse` 要求每次 ready 恰有一个部署目录内的实际 Worker，且全部请求只观察到同一 PID＋startTicks。监视器每20 ms观察自有进程树，不能证明未采样到的极短进程；与 Worker协议／单元回归证据结合解释。基线和升级后使用相同场景、尺寸、时间序列和期限比较，禁止把参数改变造成的差异归为优化收益。

每个场景最终请求预览 `closed`、工程关闭任务 `succeeded`；即使 seek失败也尝试这些关闭。最后沿用 Smoke向自有启动器发 SIGTERM、等待所属 Server／Worker身份消失，并扫描所有证据中的内存凭证。结果保留在 `summary.json`、各场景报告、逐次请求／响应、PNG及 `seek-shutdown.json`；失败目录保留，下一次使用新目录。当前探针不覆盖源码／参数改变编译、取消恢复、ALC回收或GPU备用 Worker。

准备阶段或样本失败会记录具体 `stage`、最近请求名称／方法／路径、限长脱敏的断言标签与错误消息，以及仅含文件／行号／函数的调用位置；不保存调用栈局部变量或源码文本。若尚无场景／样本，先查看 `summary.json.failures` 和对应最后一组请求／响应。错误详情同样参与退出后的凭证扫描。

静态检查（不会启动发布实例）：

```bash
python3 -B backend/tools/backend-script-render-upgrade/probe.py --help
python3 -c 'import ast,pathlib; ast.parse(pathlib.Path("backend/tools/backend-script-render-upgrade/probe.py").read_text())'
```

### 同步作者编译探针

```bash
python3 -B backend/tools/backend-script-render-upgrade/compile_probe.py \
  --dist "$PWD/artifacts/web-backend/current" \
  --output "$PWD/artifacts/script-render-upgrade/compile-round-01"
```

每轮使用全新输出目录和独立 profile，`--dist` 必须是匹配的完整 Server/Worker 发布目录。探针导入 `WordEmphasis`，同步编译一次冷样本后对同一源码串行编译 12 次暖样本（`--warm-count` 可设置 12 至 100），核对成功、草稿及源码身份、产物身份、完整诊断和前后源码快照。`summary.json` 分别保存冷样本、全部暖样本、暖样本 median 和 nearest-rank p95、部署清单摘要及工具 SHA-256。耗时从发出 HTTP 请求到完整读取响应，不包括证据落盘；它包含服务端处理和回执传输，不是单独的编译器耗时。

通过相同工具和样本数运行各轮旧／新部署；不同部署应各使用全新 profile。HTTP 产物 hash 一致不能证明实际 Roslyn Emit 次数，实际次数由独立 Scripting 测试验证。探针沿用 W8 的认证、凭证扫描和所属进程身份排水；失败仅记录阶段与异常类型。静态检查不启动服务：

```bash
python3 -B backend/tools/backend-script-render-upgrade/compile_probe.py --help
python3 -c 'import ast,pathlib; ast.parse(pathlib.Path("backend/tools/backend-script-render-upgrade/compile_probe.py").read_text())'
```
