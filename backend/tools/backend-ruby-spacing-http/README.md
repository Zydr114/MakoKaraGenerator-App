# Ruby spacing 的有限真实 HTTP 验收

本工具复用现有 Web host 与基础业务探针的启动、认证、jobs 轮询和进程观察逻辑，运行匹配的 self-contained Server/Worker。需要 Linux x64、Python 3、.NET SDK、`flock`、仓库当前 native 字体/渲染环境及 `/usr/bin/ffmpeg`、`/usr/bin/ffprobe`。

## 范围与 oracle

通过公共 `ttml` importer 创建三个 `ttml.project` 输入：相邻多 token/多 ruby 碰撞、单 ruby、单个宽 ruby。每个 ruby 有两个独立定时 segment。每个工程先经公共保存、关闭、重开，使双方读取同一持久工程；随后覆盖：

- 三个 segment 时间点 × 普通/透明，及 layout × 普通/透明；重开后再次测量，共 27 次同帧 PNG/geometry 测量。 layout 沿用既有固定代表文字语义，不显示实际歌词 ruby；真实碰撞 ruby 的几何和间距在 time 模式验收。Oracle遍历独立Runtime的完整 scene definition，未出现在该帧 active elements 的原行/ruby以不可见、0 opacity、null矩阵/枢轴和空角点对照，不能将它们漏出元素集合。
- 完整 snapshot source SHA、EngineIdentity、PNG bytes 和几何元素 ID 集合/矩阵/枢轴/角点/来源逐项对照；相邻 ruby 世界间距至少为缩放后的 1 布局像素，容差 `1e-4`。
- 时间和 alpha 改变保持几何；时间推进改变 paint；测量不改变工程摘要；stale revision 返回 409；保存重开保留歌词内容、身份、时间和画面。
- 所有异步 job 等待成功终态，结束时检查观察到的自有 Server/Worker/子进程身份已退出。

专属 `RuntimeOracle` 使用 `ProjectStore.Load` 独立加载公开保存的工程，再直接调用实际 `Runtime.Hosting.MeasureAsync`，不调用 HTTP 或 Worker 测量。公开 open 会将音频声明固定到本次 session 的自有 `loaded-input` 闭包，因此工具先校验公开保存与该固定闭包的音频 bytes 完全相同，再仅将 oracle 的 `AudioPath` 声明规范化为同一绝对路径。文字、时间、样式和其他 snapshot 字段不变，最终仍强制整个 source SHA 相同。它证明同一输入在独立进程加载路径和 HTTP/Worker 路径中的完整一致性。它使用当前渲染器，**不能单独证明旧版画面零差**；该保证依赖另行运行的固定 36 帧 legacy golden 回归。手算 solver 正确性、新旧 Worker EngineIdentity 拒绝、脚本/package、导出、浏览器、性能和平台矩阵也由独立验收负责。

## 执行

从仓库根执行。先提交生产实现，确认工作树生产输入稳定。发布命令要求 `--dist` 是不存在的新目录；探针的 `--output` 同样必须不存在。下面的目录名是示例，每次失败重跑需换目录，保留之前的日志。

```bash
flock -o /tmp/mkg-ttml-dotnet.lock dotnet build \
  tools/backend-ruby-spacing-http/RuntimeOracle/RuntimeOracle.csproj \
  -m:1 -p:UseSharedCompilation=false

flock -o /tmp/mkg-ttml-dotnet.lock python3 tools/backend-web-host/host.py publish \
  --dist "$PWD/artifacts/ruby-spacing/final1/publish"

python3 tools/backend-ruby-spacing-http/probe.py \
  --dist "$PWD/artifacts/ruby-spacing/final1/publish" \
  --output "$PWD/artifacts/ruby-spacing/final1/http" \
  --oracle "$PWD/tools/backend-ruby-spacing-http/RuntimeOracle/bin/Debug/net10.0/RuntimeOracle.dll"
```

Oracle 每次调用也通过 `/tmp/mkg-ttml-dotnet.lock` 串行。发布器内部保留其原锁，外层统一锁防止与本批其他 .NET 操作并发。不要给整条 probe 命令套同一锁，避免其子进程重复申请锁。最终验收期间不要修改生产文件或重建共享依赖。

发布器记录生产提交、输入 SHA、dirty 标志、完整部署文件 SHA，并验证发布期间输入未改变。探针要求产品 `0.97.0`、`sourceDirty=false`、当前全部生产输入与发布清单逐文件 SHA 相同，结束时再次核对。运行器会先核查部署文件完整性，Oracle EngineIdentity 必须为 `MakoKaraGenerator:0.97.0`。

## 凭证、产物与失败

bootstrap/access/resume 凭证仅由现有 launcher 的私有凭证文件及探针内存处理；bootstrap 文件权限为 `0600`、所属运行目录为 `0700`，正常排水后删除。请求和响应证据中的凭证字段会脱敏，结束后使用仅存内存的实际凭证扫描全部输出文件。不要手动复制私有凭证文件到证据或日志。

`--output` 下保留脱敏 HTTP exchanges、输入 TTML、HTTP PNG、每帧 direct oracle 的 PNG/BGRA/JSON/log，以及探针自有 `profile/projects` 工程、`profile/state` 和 launcher 日志。`summary.json` 记录请求/断言计数、job 终态、27组对照摘要、Worker 身份及失败原因；`*-shutdown.json` 记录观察身份和剩余身份；`evidence-manifest.json` 固定已落盘证据 SHA。发布清单位于 `--dist/deployment.json`。

任何检查失败都返回非零，仍尝试排水和凭证扫描，保留原始证据目录。失败运行不得并入最终通过计数，也不要通过覆盖旧证据目录重跑。证据 manifest 不包含自身；交付时可单独记录其 SHA。
