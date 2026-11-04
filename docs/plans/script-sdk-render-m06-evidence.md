# M06 视频目标采样与编码所有权证据

日期：2026-11-04。状态：**0.110.9 实现、CPU 回归与有限 CPU HTTP 已记录；GPU 像素质量阻塞，正式性能待测，M06 未通过**。
本记录固定下面列出的发布与测试身份，不能用于宣称后续修订已经验收。本机证据根为
`.local/script-sdk-m06-20261104/`；节点要求仍以[验收清单](script-sdk-v2-render-acceptance.md)为准。

## 实现与发布身份

实现提交 `3083e6b`，产品 `0.110.9`；生成合同身份提交 `7ba9260`，版本化视频工具提交 `24fc577`。
合同正文的产品元数据曾由 freeze 原样读取而保留旧值，后由 `1aad64e` 修正正文并重新生成校验；
这一文档修正不改变生产源码、产品版本或下述已发布二进制，不把文档提交当成新的运行验收。

- 视频来源增加可选目标尺寸请求，旧接口默认仍返回原尺寸。背景放置及图 `BackgroundMedia` 捕获
  继续按原始媒体几何计算；仅完整来源铺满画布、两轴同整数倍缩小的路径请求小尺寸。
  裁切、留边、非整数或非均匀缩放保留原尺寸；显示矩阵、90°/270°旋转标签保守回退。
- FFmpeg 在 BGRA 颜色转换后执行 point 采样；0.110.9 的偶数倍相位补偿满足本轮 CPU fixture，
  但实际 Vulkan 质量回归发现它不能保持全部 GPU 采样结果，见下方阻塞记录。
  尺寸变化先排空旧解码进程、清缓存并重建接收缓冲；滤镜失败时回退原尺寸。
- 编码器原本已有容量 2 的有界队列与渲染／写出重叠，本次保留顺序、反压、取消和排水。
  修复已释放会话拒收新帧时没有释放传入帧的所有权问题，并补充真实进程回归。

匹配普通发布为 `.local/backend-dist/m06-20261104/`，source clean，来源完整 SHA 为
`24fc5771f44712828a45059d925ffe74e660338b`，Release、self-contained linux-x64，Server/Worker
共 997 个清单文件；deployment manifest SHA-256 为
`f8cb4776ad993eded6970f9cf73d2fd0e6cb09771bc5108405cb48744a6f0486`。
HTTP 前后逐字节复核发布、Oracle 引用与完整运行依赖；未借用兄弟源仓库产物。

## 构建、回归与 red/green

活动树 `build.log` 记录解决方案构建 0 警告、0 错误。`tests/` 与对应日志记录 Media 209、
Export 181、Rendering 1196 通过，均 0 失败、0 跳过。这些是相邻模块回归结果；其中依赖环境开关的
条件 GPU 分支不能凭普通通过数推定执行，独立实际 Vulkan 结果另列。不同组有重叠，不相加为独立用例数。

前置隔离候选的 CPU 记录位于 `isolated-validation/`：Media 13/13、Rendering 31/31、
`BoundedEncoderOutputTests` 5/5。隔离树 HEAD `949b29c` 与活动树身份不同，不能以这些候选结果替代
后续活动树集成回归。三项定向项目构建均为 0 警告、0 错误，执行环境记录为
`AllowMissingPrunePackageData=true MAKO_GPU_SMOKE=0`。

| red/green | 真实失败与恢复证据 | 边界 |
| --- | --- | --- |
| 显示变换保护 | 仅移除 `metadata.HasDisplayTransform` 原尺寸保护后，90°/270°两例实际编译并在输出宽度断言失败：expected 160、actual 32；恢复保护后同两例 2/2 通过 | 初次旧 `rotate` 元数据素材未产生矩阵，两例失败在 fixture 探测；随后 CA1305 编译失败也保留，均不计有效变异。有效素材改用 `-display_rotation`，其他矩阵变换未逐一运行 |
| 拒帧所有权 | `encoder-red-green/summary.json`：恢复旧拒收行为后 1/1 失败；恢复修复后同例 1/1、完整队列类 5/5 通过，覆盖有序写出、满队列反压、取消、编码失败及帧释放 | 这是 CPU 与所属原生进程验证，5 例包含单例绿灯。变异在隔离树执行，16 项既有源文件与 index/diff 均保存；编码器恢复后与活动实现 SHA 相同 |

编码器恢复 SHA-256 为 `bd104053ccf1ce061ec88c2a1468b1cc4d23d1751f5dcdff836112ebd8b2077b`。
red/green 证明回归能检出对应缺陷，不证明性能门槛，也不代替 GPU 颜色／采样质量。

## 合同与 CPU Fresh HTTP

新 CLR metadata、freeze/validate 记录在 `clr-metadata*.json`、`contract-validation*.json`，
HTTP 合同保持 `1.42.4`：112 路径、125 操作、597 schemas、3431 示例，其中 2547 反例。
`http: []` 表示该次合同工具没有重放 HTTP；真实 HTTP 使用下面的独立新目录。

版本化入口见[视频 HTTP 工具](../../backend/tools/backend-video-sampling-acceptance/README.md)。
`http/oracle-video-r1/` 仅编译 Oracle 入口，引用同一不可变部署的 Worker 运行程序集及 Server 的
Compiler/Roslyn；完整运行 DLL、native 与 script refs 均来自部署，构建 0 警告、0 错误。
Oracle 的 provider 仅实现旧取帧接口，强制原尺寸解码；opaque 两源各记录 48 次 1920×1080 读取，
transparent 各 0 次视频读取，按既有语义省略背景。

`http/video-r1/` 显式请求 CPU、独立 profile、随机 loopback Server，并按公开 API 创建、设置背景、
保存关闭重开。两个自有 FFV1 素材各为 1920×1080、4fps、2 秒共 8 帧，分别 yuv420p 与保留 alpha
的 BGRA；独立解码验证时间变化及 alpha。项目输出 480×270、24fps、2 秒共 48 帧。

| 有限流程 | 0.110.9 CPU 结果 |
| --- | --- |
| opaque/straight PNG | 两种源各两模式、每模式 4 时间点，共 16 PNG；完整 RGBA 与同版原尺寸 Oracle 对照，最大通道差全为 0，保留原门限 ≤2 |
| 普通 MP4 与透明 ProRes MOV | 共 4 个导出、192 帧完整解码；报告与内容 SHA／大小一致，48/48 帧、失败与丢帧均 0，独立 ffprobe 复核尺寸与帧率，opaque 代表帧时间顺序通过 |
| 相对同版 Oracle 的编码差异 | opaque RGB MAE：YUV `3.5059816529492456`、alpha 源 `2.110185131601509`，均小于原门限 8；transparent 两源均 `0.03852674346804631`，低于门限 15，visible alpha `182462/182462` 在误差 8 内，满足原 90% 覆盖要求 |
| 实际渲染后取消 | 两源先观察 `running/rendering` 且 `completedFrames > 0`，再进入 cancelled；没有 exportResult、没有对应 artifact 目录，`.staging` 空，仅 4 个成功产物目录 |
| 身份与排水 | 184 HTTP requests、1871 assertions、14 terminals，凭证扫描 964 files；31 Worker identities、全部 62 observed process identities 正常退出，exit0、remaining 空；6 个所属临时 Runtime 输入目录均已移除 |

最终复核 997 发布文件、965 证据文件、441 Oracle 发布文件的 SHA 均匹配。旁路观察的 72 个 FFmpeg
进程也全部退出。凭证扫描使用本轮仅存内存的真实凭证；异常在写入失败／摘要记录前脱敏。

本轮没有修改产品代码或放宽门限。M04 工具准备首轮 `http-tool-validation/video-m04-r1/` 的素材
`VIDEO TEST` 被真实导入器拆为三个 token，触发严格单 token fixture 断言；改为 `VIDEO` 后在新目录
通过，失败记录保留。M06 本轮使用已版本化的相同 fixture 与断言。

## M04 与 M06 跨版本像素

基线为 clean M04 `0dc8d3fd4361cfb86219cbd8fb9392d4500e97b2`，
证据 `.local/script-sdk-m06-20261104/http-tool-validation/video-m04-r2/`，部署 manifest SHA-256 为
`abd85c45dadef783a6fbe8f31a27b939235e7392f21c1ac8b41498e8e3fbd508`。
M04 的 183 HTTP／1865 assertions 与凭证扫描、排水通过；该旧版工具记录不能归为 M06 当前请求数。

`http/cross-m04-m06-r1.json` 保留 2176 项复核，无失败。两种源的 MKV 容器 SHA 均不同，但各 8 帧
完整解码 BGRA 逐字节相同；保存的音频与完整歌词 bytes 相同。工程字段在明确排除 applicationVersion
及已证明解码等价的视频容器路径／hash／大小后相同，没有忽略样式、时序或背景放置参数。

- 16 个实际 HTTP PNG 完整 RGBA 跨版本相同，最大通道差为 0。
- 192 个 full-size Oracle BGRA 帧跨版本相同，最大通道差为 0。
- 4 个实际导出共 192 帧完整解码 BGRA 跨版本相同，RGB MAE 与最大通道差全为 0，透明 alpha 一致。

跨版本脚本保留 PNG／Oracle 最大通道差 ≤2、opaque RGB MAE <8、transparent 可见区域 RGB MAE <15、
可见 alpha 90% 在误差 8 内的门限；实际结果为零差，不是调高门限后的通过。这是有限 CPU fixture
的优化前后证据，不能抵消下方 GPU 回归。

## 目标缩小真实命中

`http/owned-ffmpeg-r1.json` 只观察本轮 Python 探针的所属后代，记录 FFmpeg 的 PID/startTicks、祖先
可执行身份、argv 与 `/proc/io` 采样；不读取 Server/Worker 的命令、环境或凭证。观察到 6 个 Worker
视频解码进程，输入 SHA 分别匹配两种自有源，各源 3 个，全部实际使用
`format=bgra,format=gbrap,pad=1921:1081:1:1,crop=1920:1080:0:0,zscale=480:270:filter=point,format=bgra`，
均有正写入计数；未观察到 Worker 原尺寸回退。2 个 direct Oracle 解码进程没有缩放滤镜。

alpha 完整导出解码进程 PID `72611`／startTicks `148776` 的采样 `wchar` 为 `4147200`
（8×480×270×4）；原尺寸 Oracle PID `71914`／startTicks `148479` 为 `66355200`
（8×1920×1080×4），两者采样总量为 16:1，与实际目标滤镜、完整输出及像素对照共同证明本 fixture
走过较小 stdout 路径。单帧 BGRA 字节预算从 8294400 降为 518400。

`wchar` 是进程写字符计数的采样，不是逐 fd 精确遥测，也不是正式耗时或内存测量。codec 仍完整解码
后再过滤，来源构造时仍分配原尺寸缓冲；不宣称 codec 原生缩放、峰值内存下降或 GPU 异步回读。

## GPU 质量阻塞与待办

独立 `gpu-quality/summary.json` 固定 `3083e6b`／0.110.9，保存命令环境
`MAKO_RENDER_BACKEND=vulkan MAKO_GPU_SMOKE=1` 与 Radeon ICD；测试进程映射
`libvulkan_radeon.so` 并打开 `/dev/dri/renderD128`，不是只凭条件用例数量推定 GPU 执行。
`VideoSamplingQualityTests` 实际 19 项：12 通过、7 失败；其中 6 项为主渲染逐通道门限失败，1 项为
精确 `BackgroundMedia` 捕获失败。观察到主渲染失败涉及 2／4 偶数倍，3 倍 Cover 与 5 倍 Contain
样本通过，证据与偶数倍采样相位问题一致；不能据此宣称所有奇数倍输入正确。另有背景测试 8/8 与
设备 smoke 1/1 通过，均不能覆盖或抵消上述 7 项失败。69 个所属观察进程已全部退出，测试来源与产物未改。

**GPU 像素门禁当前阻塞 M06。** 保留失败、原有 ≤2 与精确捕获断言，在隔离修复后重新执行实际 GPU
质量与相关 CPU 回归，并发布新 source-clean 修订做 fresh 同版视频 HTTP 和跨版本对照；本轮 0.110.9
CPU 证据不自动归于新修订。浏览器／桌面呈现、任意 codec／几何组合未由本 CPU fixture 验收。

正式性能仍待测。性能工具准备或插桩诊断不能记为无插桩门槛结果；需待正确性门禁修复、其他本任务
CPU/GPU 工作停止后，在静默窗口按验收清单的同输入、3 轮旧新 A/B，核对中位数与 p95 均不超过
`110%＋5ms`，并记录相应真实成本下降。不得用本轮输出字节路径或 CPU 像素相同代替性能验收。

## 证据摘要

| 文件（证据根内） | SHA-256 |
| --- | --- |
| `http/final-verification.json` | `0f6e27d65fb56e787ac4af527f7d57062e24053685e9827485f4fa8e81bb3fc7` |
| `http/cross-m04-m06-r1.json` | `e6bd9421f4216d0d9ceeae1b458fa85632ddc9485fe529a69404eaa426313916` |
| `http/owned-ffmpeg-r1.json` | `0af1bf9e113e37459a54d769d7aae0606928195ee40199fe797ad143ac7749d5` |
| `http/http-archive-manifest.json`（1711 文件） | `d99be192a09bc6867b8f308d65ed1f2562c7d6291153ea8601995dbb4c4056d6` |
| `gpu-quality/summary.json`（未通过） | `6d326ad2b34639162ea2585324cafc9488eb217ad621742c039e6708d6c7ee56` |
