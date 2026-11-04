# 视频目标采样 HTTP 验收

此工具在独立 loopback Server/Worker 上验证保存重开、视频背景 PNG、完整短导出与运行后取消。它只使用给定的 clean self-contained linux-x64 部署；Oracle 编译仅引用该部署的运行程序集、native 库和 script refs，不构建当前产品源码。现有 `ruby`、`package`、静态 `export` Oracle 不由本目录构建。

## 执行

从 App 根运行。`OUT` 是本轮新目录，每个 builder/probe 输出目录必须不存在；保留失败目录。需要 .NET 10 SDK、Python 3、NumPy、`/usr/bin/ffmpeg` 和 `/usr/bin/ffprobe`。凭证仅存于隔离 profile 的私有启动文件并在停机移除，证据记录脱敏请求和错误；仅按本轮 PID/startTicks 排水。

```bash
DIST="$PWD/.local/NEW-CLEAN-DIST"
OUT="$PWD/.local/NEW-VIDEO-HTTP"
TOOLS="$PWD/backend/tools/backend-video-sampling-acceptance"
AllowMissingPrunePackageData=true python3 "$TOOLS/build_oracle.py" --dist "$DIST" --kind video --output "$OUT/oracle-video"
python3 "$TOOLS/verify_oracle.py" --dist "$DIST" --kind video --oracle-dir "$OUT/oracle-video"
python3 "$TOOLS/video_probe.py" --dist "$DIST" --oracle "$OUT/oracle-video/publish/RuntimeOracle" --output "$OUT/video"
python3 "$TOOLS/verify_oracle.py" --dist "$DIST" --kind video --oracle-dir "$OUT/oracle-video"
```

先固定部署 commit 与 manifest SHA，再执行构建和 HTTP；任一非零即停止并检查保留的证据。正式性能窗口不得与本工具构建、FFmpeg、HTTP 或 Oracle 并行。本工具明确请求 CPU，不执行 GPU 或性能测量。

## 有限覆盖

- 生成两个自有 1920×1080 FFV1 源，各 8 帧：yuv420p 与保留 alpha 的 BGRA。独立解码前三帧验证真实时间变化与 alpha，并记录字节身份和生成命令。
- 经公开 API 上传、创建、设置背景、保存、关闭、重开；Oracle 从 `ProjectStore.Load` 的保存项目运行，核实媒体 bytes/hash。工程输出固定为 480×270、24fps、2 秒，共 48 帧。
- `OriginalSizeProvider` 只暴露旧取帧接口，强制原尺寸视频源在 Skia 中按目标采样。Oracle 每帧保存 BGRA/PNG 与 SHA；opaque 断言实际源帧尺寸为 1920×1080，transparent 按现有语义跳过背景。
- 两种源各请求 opaque/straight PNG，每种 4 个时间点，完整解码 RGBA 对照 Oracle，最大通道差上限 2。
- 每种源各导出普通 MP4 与透明 ProRes MOV，等待 terminal，核对报告/内容身份、48 帧与零失败/丢帧；独立 ffprobe 与完整逐帧解码。opaque 全序列 RGB MAE 小于 8，transparent 可见区域小于 15，并检查 alpha 覆盖、90% 像素在误差 8 内，以及 opaque 代表帧的时间顺序。
- 将输出改为 60 秒，在真实 `rendering` 且 `completedFrames > 0` 后取消；断言 cancelled terminal 与无发布引用。最终记录凭证扫描、Worker 身份与所有观察进程排水。

`tool-inputs.json` 固定工具与继承探针源 SHA；`input-manifest.json` 固定 Oracle 源、命令、部署与所有运行依赖；`evidence-manifest.json` 固定本轮证据文件。HTTP `input.sourceSha256` 是固定 Worker snapshot 身份；Oracle 的 `projectJsonSha256` 是磁盘工程入口身份，两者含义不同。

本 fixture 只覆盖无旋转、16:9 contain 的有限输入；旋转 90°/270°、cover/crop、非整数几何和原尺寸回退仍需相应回归。透明模式省略背景，不表示保留视频背景 alpha。相同版本 Oracle 与 HTTP 一致不等于跨版本像素一致；M06 正式验收还需固定旧部署对照。目标采样减少 stdout/管道像素与缓冲量，不证明 codec 原生缩放、峰值内存下降或性能提升。

## 2026-11-04 M04 工具基线

私有准备版本位于 `.local/m05-m06-http-acceptance/`；移入本目录只调整根路径与 builder kind 范围，保留已验证的视频逻辑。M04 clean commit 为 `0dc8d3fd4361cfb86219cbd8fb9392d4500e97b2`，deployment manifest SHA256 为 `abd85c45dadef783a6fbe8f31a27b939235e7392f21c1ac8b41498e8e3fbd508`。

证据位于 `.local/script-sdk-m06-20261104/http-tool-validation/`：`oracle-video-r1` 构建 0 warnings/errors，`video-m04-r2` 通过 183 HTTP requests、1865 assertions，凭证扫描 962 files，31 Worker identities 与全部 62 observed process identities 排水。16 PNG 的最大通道差全为 0；4 exports 共 192 帧。opaque RGB MAE 为 YUV `3.5059816529492456`、alpha `2.110185131601509`；transparent 两源均 `0.03852674346804631`，可见 alpha `182462/182462` 匹配。

`baseline-verification.json` 补核验 997 部署文件、963 证据文件、441 Oracle 发布文件，以及 192 BGRA/PNG 对的 SHA；取消的两个 artifact 目录不存在，`.staging` 空。两种不同视频的 transparent 48 帧完全相同、opaque 确有差异，结合 transparent 零源读取记录现有跳过背景语义。`video-m04-r1` 保留首轮 fixture 失败：`VIDEO TEST` 被实际导入器拆为三个 token；改用 `VIDEO` 后保留单 token 断言重跑。上述结果属于 M04 CPU 工具兼容性基线，最终 M05/M06 同版验收另行执行。
