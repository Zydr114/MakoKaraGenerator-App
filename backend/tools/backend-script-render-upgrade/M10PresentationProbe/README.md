# M10 presentation 有限验收探针

本工具引用本仓库正式 `MakoKaraGenerator.Media`、`MakoKaraGenerator.Processes`，并显式
启用两个 native 构建目标。构建产物的 `media-tools/` 包含
`libmkg_media_envelope.so`、`mkg-media-exec-gate` 与 `mkg-presentation`。本项目不启用
Server/Worker，不连接 HTTP，不接入 store/index/frame-source 或 App/GPU 预览。

本工具的有限验收结果与失败历史见 `docs/plans/script-sdk-render-m10-evidence.md` 第十七片。
下列命令用于独立复跑；构建、夹具生成、依赖发现、正式验证与清理必须分别记录。

## 构建与夹具

从 App 仓库根目录执行。构建要求 Linux x86_64、.NET 10 SDK、CMake 3.24+、C17 编译器、
pkg-config、libsystemd 开发文件，以及满足正式 native CMake 声明的
`libavformat>=63`、`libavcodec>=63`、`libavutil>=61`。运行还要求同用户 systemd manager
和已委派的 cgroup v2 memory 控制器；缺失能力返回具名拒绝。

`MakoMediaPresentation.Native.targets` 默认关闭；本 probe 单独选择 `linux-x64` 并启用
presentation 与 envelope 两个目标。presentation 目标以 Release 构建
`--target mkg-presentation -DBUILD_TESTING=OFF`，只复制该 helper。构建/发布不执行
probe、生成器、CTest 或服务，也不代表受控工具闭包已经发现或通过验收。

```bash
dotnet build backend/tools/backend-script-render-upgrade/M10PresentationProbe/M10.Presentation.Probe.csproj -c Release -m:1

dotnet publish backend/tools/backend-script-render-upgrade/M10PresentationProbe/M10.Presentation.Probe.csproj \
  -c Release -r linux-x64 --no-self-contained -m:1 \
  -o /absolute/private/presentation-probe-publish-01

python3 backend/tools/backend-script-render-upgrade/M10PresentationProbe/generate-fixtures.py \
  --output /absolute/private/presentation-base-fixtures-01 \
  --ffmpeg /absolute/installed/ffmpeg \
  --ffprobe /absolute/installed/ffprobe

python3 backend/tools/backend-script-render-upgrade/M10PresentationProbe/generate-jpeg-fixtures.py \
  --base-fixtures /absolute/private/presentation-base-fixtures-01/fixtures.json \
  --output /absolute/private/presentation-jpeg-fixtures-01 \
  --cjpeg /absolute/installed/cjpeg \
  --ffmpeg /absolute/installed/ffmpeg \
  --ffprobe /absolute/installed/ffprobe
```

所有 `/absolute/...` 均须替换为本机的规范绝对路径；输入工具、文件与目录不得经 symlink
改指。生成器需要明确的本地工具路径，不下载工具，不替换 encoder 或采样布局来凑覆盖。
输出目录必须全新且私有；生成器记录工具摘要、真实布局、原始平面和失败事实。
JPEG 生成器退出 2 表示局部生成失败，完整 `fixtures.json`/`rejections.json` 不会保留，
该输出不能用于 acceptance。两次生成完成仍只说明夹具已生成。

## 发现与有限验收

以下命令使用上方 publish 目录中的 probe 和两个 packaged helper。也可使用默认 build
目录 `backend/tools/backend-script-render-upgrade/M10PresentationProbe/bin/Release/net10.0/linux-x64/`，
但必须一起选用同一产物目录内的 bridge、gate、helper。证据目录必须是新的绝对路径，
probe 创建权限为 0700 的目录与逐条落盘的 `events.jsonl`。

```bash
dotnet /absolute/private/presentation-probe-publish-01/M10.Presentation.Probe.dll \
  discover \
  --fixtures /absolute/private/presentation-jpeg-fixtures-01/fixtures.json \
  --evidence /absolute/private/presentation-jpeg-discovery-01 \
  --gate /absolute/private/presentation-probe-publish-01/media-tools/mkg-media-exec-gate \
  --helper /absolute/private/presentation-probe-publish-01/media-tools/mkg-presentation

dotnet /absolute/private/presentation-probe-publish-01/M10.Presentation.Probe.dll \
  verify \
  --fixtures /absolute/private/presentation-jpeg-fixtures-01/fixtures.json \
  --evidence /absolute/private/presentation-jpeg-verification-01 \
  --manifest /absolute/private/presentation-jpeg-discovery-01/loaded-manifest.json

dotnet /absolute/private/presentation-probe-publish-01/M10.Presentation.Probe.dll \
  reject \
  --fixtures /absolute/private/presentation-jpeg-fixtures-01/fixtures.json \
  --evidence /absolute/private/presentation-jpeg-rejections-01 \
  --manifest /absolute/private/presentation-jpeg-discovery-01/loaded-manifest.json \
  --negatives /absolute/private/presentation-jpeg-fixtures-01/rejections.json
```

`discover` 使用正式 `OwnedNativeEnvelope`、真实 helper 和 type4/type5 记录发现实际加载
闭包；退出 0 的 summary 是 `discovered-not-accepted`。完整十六例矩阵必须重新发现，
不能复用旧六例清单。`verify` 和 `reject` 分别输出独立证据，并固定发现时的夹具 SHA256；
只运行其一不足以替代另一项。tool、源和闭包变更后应从新目录重新生成/发现/验收。

| 退出码 | 含义 |
| --- | --- |
| 0 | 当前命令完成；discover 仅为发现，verify/reject 须同时检查 `summary.json` 的 `passed` |
| 42 | `capacity-unsupported`，平台或内核封套能力不足 |
| 43 | `incomplete-named-unsupported`，指定闭包负例条件未满足，不能记为通过 |
| 1 | 执行、断言、期限或 owned drain 失败 |
| 64 | 命令、路径参数或已存在的证据目录不符合要求 |

## 保留的矩阵与 oracle

默认组合清单包含六个原始正例：带 sRGB 的 PNG8 RGBA、PNG16 RGBA、SAR 2:1 与 Exif6
旋转 PNG、真实六帧 H264 B-frame MP4、VP9 DefaultDuration 的 MKV（PTS 0/33/67ms，
尾端 100334us）、仅尾帧 BlockDuration=41ms 的 MKV（尾端 108ms）。

JPEG 扩展为 baseline/progressive 各五例：YCbCr420、422、444、RGB、gray。源图为脚本
直接编写的 19×13 PPM/PGM，cjpeg 固定 quality 93 和 integer DCT。marker walker 核验
SOF0/SOF2、8-bit、奇数尺寸、组件 ID、采样因子、density 与真实 progressive 多次扫描。
RGB 使用 Adobe0/RGB IDs 且无 JFIF，因此探针正例显式传入 trusted missing-tag
interpretation；同一 RGB 不传 override 另作拒绝，不自动提升色彩权威。

FFprobe 记录真实 native 格式，FFmpeg 输出相同 native YUV/GBR/gray 平面；YUV 不经过
swscale 的 YUV→RGB。独立 Python 算术执行 BT601/full/center chroma、双线性 edge hold、
唯一编码 RGB 饱和点、sRGB EOTF、opaque alpha 和 float32 量化。最终整帧 canonical 的
逐元素绝对误差仍为 `2e-6`，不因 JPEG 有损编码放宽。源图到实际解码图的 max/RMSE
另行记录；codec 参考可能共享 FFmpeg 祖先，不能据此独立证明 JPEG IDCT 正确。

`verify` 按清单逐例验证源/profile/color、像素、精确有理 PTS、F/I 顺序、区间/tail/summary，
完整摘要还独立编码全部 64B interval 并累计 SHA256；每例再执行真实 borrowed-callback 在途取消，共默认 16 正例、32 个完整/取消场景。
消费者接收同时响应 caller 与监督取消的 linked token；回调明确抛该 token 后，最终取消
异常仍须保留原 caller token，且 callback/pixels 持有到明确释放；每项都要求准确 PID/startTicks、
cgroup 身份、terminal、Unref/Dispose、进程消失、源租约和预算归零、无 quarantine 或
retained operation。自然退出不强造 kill，按真实 owner 身份处理，禁止按进程名批量清理。

`reject` 固定十六个结构/请求负例的公开 failure category 和 native 是否应启动：JPEG ICC
prefix、重复 JFIF、JFIF/Adobe0 矛盾、YCbCr/RGB 无权威、无 EOI、串接、12-bit SOF、
Exif9；PNG iCCP、acTL、错误 IHDR CRC、无 sRGB；未知 magic、still streamIndex=128、
JPEG 请求为 Video。必须进入 native 的无权威用例保留真实图像 payload 并完成 owned drain；
其余要求 native 从未打开。结构故障不证明真实 ICC/APNG/12-bit 内容解码。

三个闭包场景分别为：过期 pinned helper hash 在 native 前拒绝；成功完整控制后漏列实际
libavcodec（没有独立条目则退出 43）；真实首帧后仅在 consumer 的有界 metadata 视图翻转
下一 type4 hash。最后一项保留真实 owner/native/pixels，是协议故障注入，不是物理 late
dlopen 或源文件替换。失败应保留证据并解决原因，不得放宽 oracle 或 drain 断言。

这些有限矩阵不证明全格式支持、CMYK/YCCK/lossless/arithmetic JPEG、损坏 entropy、真实
HDR/ICC/APNG、多流/编辑列表、OOM、源动态替换、late dlopen、性能、HTTP、Server/Worker、
store/index/frame-source、持续 GPU 路径或最终 App 验收。私有证据包含绝对路径与 owner
身份，不能直接作为公开 HTTP 诊断。

生成器核对视频容器和实际解码帧的完整 SDR 标记。Matroska 正例使用 VP9；原 VP8 夹具
码流与容器矩阵冲突的失败保留，不用 trusted override 绕过。PNG pHYs 的像素宽高比由
结构解析确定，适配固定 libav PNG 解码器以 x/y 密度填 SAR 的表示；身份文本包含该转换。
结构明确为 RGB 的 JPEG 使用实际 RGB24 分量；固定 libav MJPEG 的 matrix=5 初始化值
只在该分支按已核验的 RGB 组件解释，仍要求独立色彩权威，其他矛盾矩阵继续拒绝。
