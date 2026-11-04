# M06 视频目标采样与编码所有权证据

日期：2026-11-04。状态：**0.110.10 的本轮有限范围通过：CPU 目标采样、GPU 画质保护、回归、HTTP、桌面／浏览器功能及正式性能已验收**。
本记录固定下面列出的发布与测试身份，不能用于宣称后续修订已经验收。本机证据根为
`.local/script-sdk-m06-20261104/`；节点要求仍以[验收清单](script-sdk-v2-render-acceptance.md)为准。
0.110.9 的发布、CPU 结果和 GPU 失败作为历史保留，当前修订的结论以下列 0.110.10 独立证据为准。


## 当前 0.110.10：采样保护、发布与回归

修复提交 `6284ddd`，产品 `0.110.10`；合同与普通 clean 发布来源为完整提交
`cf1a0557fa5950e3ab8321b0e60ab2d191e1081d`，冻结合同仍为 `1.42.4`，源码基线
`6284dddc1a020b30b519118cfab9cd4b5a845415`。对应决策为 ADR-0250。

`gpu-sampling-fix/` 保留隔离探针、完整探针源码、命令、像素样点分布及 patch 指纹。真实 Vulkan
320×180→160×90 的同一幅画面中，两轴取样组合分别出现 lower/lower 7230、lower/upper 404、
upper/upper 5906、upper/lower 860 次；4 倍缩小出现相同分布，局部表面偏移后分布又改变。
因此它不能用一个统一采样偏移保留旧画面；3／5 倍探针通过只覆盖相应样例，不推广到全部奇数比例。

0.110.10 在共享的 `ResolveBackgroundFrame` 入口按**实际设备实例**判断：只有
`CpuSkiaRenderDevice` 使用满足既有几何门槛的目标尺寸请求；Vulkan 与未识别设备保留原尺寸。
主画面及 `BackgroundMedia` 捕获共同使用此规则，避免一帧内在目标尺寸和原尺寸之间反复重启
FFmpeg。没有修改 Media 的旧接口默认行为或 FFmpeg 相位规则，没有放宽主画面逐通道 ≤2 或背景
捕获逐字节相等的断言。**本修订不宣称 Vulkan 解码／搬运字节缩减**，CPU 目标采样路径保留。

匹配普通发布目录为 `.local/backend-dist/m06-final10-20261104/`，source clean、Release、
self-contained linux-x64，Server/Worker 共 997 个发布清单文件。deployment manifest SHA-256 为
`eb9df50946a8c1ebdfbe1470ecb5e371c20913c14340c7a7805f29d24d3ba15a`。
这些身份与下述 fresh 验证匹配，不把 0.110.9 二进制或测试自动归于本修订。

| 当前修订验证 | 结果与证据 |
| --- | --- |
| 活动树完整构建 | `build-final10.log`：解决方案构建成功，0 警告、0 错误 |
| 活动树 CPU Rendering | `rendering-final10.log`、`tests/m06-rendering-final10.trx`：1196/1196，失败与跳过均 0；保留 CPU 目标采样路径 |
| 实际 Vulkan 质量 | `gpu-quality-final10/summary.json`：19/19，通过原主画面门限及背景精确捕获；测试同时断言实际请求／返回尺寸与来源尺寸一致 |
| 实际 Vulkan 相关回归 | 同目录 video-related：23/23，包含视频背景、取消、图效果绑定及实际设备纹理／回读 probe；两组显式 `MAKO_RENDER_BACKEND=vulkan MAKO_GPU_SMOKE=1`，固定 Radeon ICD，记录了实际设备进程；82 与 3 个所属观察身份均 remaining 空 |
| 前端最终同步 | `frontend-final10/`：六份合同／行为源完整复制自 `cf1a055`，protocol 产品常量为 0.110.10；typecheck、44 文件／3927 单元、fresh build 全通过，保留既有 >500 kB bundle 提示 |
| fresh Web 构建记录 | 2026-11-04T01:10:45.490Z 开始，01:10:46.893Z 成功结束；构建前 247 项 `webInputs` 与结束后相同，成功后调用 `recordWebBuild`，stamp 为 01:10:46.891Z。`web-inputs-before.json`、`web-build-after.json` 与 `build-result.json` 保存对应证据 |
| launcher | `launcher-tests.log`：7/7；`frontend-final10/launcher-compatibility.json` 确认读取产品 0.110.10 与本仓库后端源码。没有将只读 launcher 解析当成应用运行 |

上表活动树验证覆盖了隔离补丁最终采用的实际设备注入测试搭架；隔离目录里仍保留当时“待主控
复验”的历史说明，以活动树 final10 结果补全，不覆盖原实验记录。GPU 字节缩减、正式性能、
桌面呈现与浏览器工作流是不同验收项；本节不代替后两者的专门报告。

## 当前 0.110.10：有限 CPU Fresh HTTP 与跨版本像素

`http-final10/final-verification.json` 为 passed，固定上列 clean 普通发布及同版完整 Oracle 运行
依赖：复核 997 发布文件、989 证据文件、441 Oracle 发布文件。公开 API 显式使用 CPU、独立
profile 与 loopback Server；执行两种自有 YUV／alpha 视频的创建、背景设置、保存关闭重开、
普通／透明 PNG 与完整 MP4／ProRes 导出，没有借用兄弟源仓库产物。

- 194 HTTP requests、1932 assertions，凭证扫描 988 files；取消均先观察到 rendering 与已完成帧，
  最终 cancelled，没有 exportResult 或相应 artifact，`.staging` 空，仅 4 个成功产物目录。
- 31 Worker identities、62 个所属观察进程身份全部退出；旁路记录 72 个所属 FFmpeg 身份，
  `owned-ffmpeg-r1.json` 为 exit0、remaining 空。6 个临时 Runtime 输入目录均已移除。
- `cross-m04-m06-r1.json` 共 2200 项复核全通过，继续使用下文固定的 clean M04 基线。16 个实际 HTTP
  PNG 完整 RGBA、192 个原尺寸 Oracle BGRA 帧、4 个导出的 192 个完整解码 BGRA 帧均与 M04
  逐字节相同，最大通道差和导出 RGB MAE 全为 0，透明 alpha 相同；两种源各 8 帧解码内容也相同。
  原 PNG／Oracle ≤2、opaque MAE <8、transparent MAE <15 与可见 alpha 覆盖门槛均未放宽。

这是 0.110.10 的 fresh CPU 功能、取消、排水与有限跨版本像素证据，不是 GPU 优化性能或任意
codec／几何的验收。0.110.9 的请求数、跨版本检查数与下面的 IO 样本不计入当前修订。

## 当前 0.110.10：采样命中与 IO 观察边界

当前 `http-final10/owned-ffmpeg-r1.json` 的 6 个 Worker 视频解码进程，实际命令均使用
`format=bgra,format=gbrap,pad=1921:1081:1:1,crop=1920:1080:0:0,zscale=480:270:filter=point,format=bgra`，
有正写计数，未观察到 Worker 原尺寸回退；原尺寸 Oracle 不带该缩放滤镜。命令、输入身份和完整
成功输出共同证明本有限 CPU fixture 命中了目标采样路径。

本次 `/proc/io` 最后写计数未抓全：alpha Worker 的采样最大 `wchar=3694336`，Oracle 为
`58126336`，均小于对应 8 帧期望 4147200／66355200，
`completeTerminalCountersObserved=false`。因此 **该 HTTP 轮询没有可引用的已测总字节 16:1 比例**。
8294400→518400 只表示本 fixture 原尺寸与目标尺寸的单帧 BGRA 数学预算，16:1 不是该 HTTP 轮询的总 IO
实测，也不证明 codec 原生缩放或峰值内存下降；下方独立 Runtime trace 另行记录完整读取计数。

`supplement-verification-r1.json` 与同名日志保留了 exit1／`StopIteration`：旧补充检查器用精确等于
最终 8 帧计数的观察值查找进程，但轮询可错过退出前最后写入。修正仅撤销从采样最大值推定完整
总量的工具假设，保留失败，不改产品、HTTP 或像素门槛；`run-context.json` 记录旧新检查器指纹。
HTTP、质量与排水通过由各自独立结果支持，补充工具失败不冒充通过，也不解释成产品像素失败。

## 当前 0.110.10：桌面与浏览器有限功能

最终桌面发布为 `.local/backend-dist/m06-final10-desktop-20261104/`；同版 Server/Worker 与本仓库
原生接缝配套，`desktop-deployment-final10.json` 保存生产输入及原生复用核验。deployment manifest
SHA-256 为 `740ae1767e8302c304bd149c3ff8f031b1f93dfd6a5cd6971be3ae4a86b954d9`，
GPU manifest 为 `a8b374cddcff4093ab82ec12db15c460dc3ae493dfe53c87457c67c2e9f61e41`。
`.local/backend-current.json` 指向该发布，切换前的指针已保留；使用隔离 profile，未修改用户配置。

`final10-desktop-r1/report.json` 固定运行源码 `68cc7b2`，执行原有
`gpu-preview-p3.mjs --finite --ordinary-scheduling --provider runtime`。三个约 5 秒的 runtime 阶段
分别完成 121／151／151 帧，`canvas-drawn` 与 `native-allReferencesReleased` 对应完成，undrawn 为 0，
各阶段功能门禁通过。生命周期包含 20 次 attach/detach、10 次公开 resize/seek、10 次 UI 分辨率
重建，均验证正确新代次；应退出的旧 Worker 已退出。pause／seek／latest-input 各 20 次，无缺失。
激活后交互候选通过，但 latest-input 完整 UI 调用到完成的 p95 为 **1033.244477 ms**，
`candidateMet=false`（计入时钟误差上界为 1033.941823 ms），原样保留，不写成全部交互候选达标。

该报告明确为 `finite-correctness-not-performance-acceptance`：`formalRunClass=false`、
`phaseMatrix=false`。它验证匹配发布、可见 Wayland、普通调度、有限交互与资源生命周期，
不构成完整 P3 性能、视频采样性能、屏幕扫描显示 FPS 或 Chromium 内部复制量的证明。
内部 shutdown 观察 75 个身份，外部 `final10-desktop-observer-r1/process-drain.json` 观察 172 个身份，
均 exit0、remaining 空；集合重叠，不能相加。

`final10-browser-r1/browser-result.json` 的 7 项检查全部通过：连接、新建 WAV/LRC、编辑、撤销、
暂停定位到 1 秒、实际 PNG 显示与关闭，运行异常为 0。显示 PNG 为 1280×720、14680 bytes，
4281 个非黑像素，SHA-256 为 `6344e171ebed41929b7dc44793e0af8c9e3560d5b590cffebbe19d1e4ad65074`。
wrapper 仅搬移原 smoke 的输出及两处导入路径，逆向恢复后源码逐字节相同；显式 CPU 后端与
Chrome `--disable-gpu` 只证明浏览器静态预览工作流。所属后端 exit0，外部
`final10-browser-observer-r1/process-drain.json` 的 44 个观察身份全部退出。

## 0.110.9 历史实现与发布身份

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

## 0.110.9 历史构建、回归与 red/green

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

## 0.110.9 历史合同与 CPU Fresh HTTP

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

## 0.110.9 历史 M04／M06 跨版本像素

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

## 0.110.9 历史目标缩小命中

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

## 0.110.9 GPU 失败历史与阻塞解除

独立 `gpu-quality/summary.json` 固定 `3083e6b`／0.110.9，保存命令环境
`MAKO_RENDER_BACKEND=vulkan MAKO_GPU_SMOKE=1` 与 Radeon ICD；测试进程映射
`libvulkan_radeon.so` 并打开 `/dev/dri/renderD128`，不是只凭条件用例数量推定 GPU 执行。
`VideoSamplingQualityTests` 实际 19 项：12 通过、7 失败；其中 6 项为主渲染逐通道门限失败，1 项为
精确 `BackgroundMedia` 捕获失败。观察到主渲染失败涉及 2／4 偶数倍，3 倍 Cover 与 5 倍 Contain
样本通过，证据与偶数倍采样相位问题一致；不能据此宣称所有奇数倍输入正确。另有背景测试 8/8 与
设备 smoke 1/1 通过，均不能覆盖或抵消上述 7 项失败。69 个所属观察进程已全部退出，测试来源与产物未改。

**GPU 像素门禁在 0.110.9 阻塞 M06。** 该失败与原有 ≤2／精确捕获断言原样保留；后续 0.110.10
通过实际设备保护、活动树 GPU／CPU 回归及新 clean 发布的 fresh 同版 HTTP／跨版本对照解除该
正确性阻塞，见上方当前修订记录。0.110.9 CPU 结果不自动归于新修订，浏览器／桌面呈现及任意
codec／几何组合也不由这些有限 CPU fixture 代替。

## 0.110.9 历史证据摘要

| 文件（证据根内） | SHA-256 |
| --- | --- |
| `http/final-verification.json` | `0f6e27d65fb56e787ac4af527f7d57062e24053685e9827485f4fa8e81bb3fc7` |
| `http/cross-m04-m06-r1.json` | `e6bd9421f4216d0d9ceeae1b458fa85632ddc9485fe529a69404eaa426313916` |
| `http/owned-ffmpeg-r1.json` | `0af1bf9e113e37459a54d769d7aae0606928195ee40199fe797ad143ac7749d5` |
| `http/http-archive-manifest.json`（1711 文件） | `d99be192a09bc6867b8f308d65ed1f2562c7d6291153ea8601995dbb4c4056d6` |
| `gpu-quality/summary.json`（未通过） | `6d326ad2b34639162ea2585324cafc9488eb217ad621742c039e6708d6c7ee56` |

## 0.110.10 当前证据索引

| 文件（证据根内） | 已记录身份或用途 |
| --- | --- |
| `http-final10/final-verification.json` | SHA-256 `1607e9c43001dd65b818cca2f99e0fa21c2e5eaffe37c169d2c2706a8fb913a5`；当前普通发布、HTTP、跨版本及采样边界总复核 |
| `http-final10/cross-m04-m06-r1.json` | SHA-256 `122606712d2c1aeb9433dc2309cd3d6446d1170477c4d92c27fda578e342c36c`；2200 检查 |
| `http-final10/owned-ffmpeg-r1.json` | SHA-256 `12535e7ee144a9b419db4b4258e87657675b90964bee3fd63eeec6728e05a4f0`；72 个所属进程与有限 IO 采样 |
| `http-final10/supplement-verification-r1.json`／`.log` | 保留 exit1／StopIteration 工具失败；不推定完整写计数 |
| `http-final10/run-context.json` | clean 发布、临时输入清理及补充检查器修正前后身份 |
| `gpu-quality-final10/summary.json` | 活动树实际 Vulkan 19＋23 通过、命令及进程证明索引 |
| `gpu-sampling-fix/summary.json`、`evidence-sha256.json` | 隔离采样探针、patch 与完整源码变体；不替代活动树验证 |
| `frontend-final10/summary.json`、`build-result.json` | 最终合同同步、3927 单测和本次 fresh Web 构建输入／成功记录 |

## 当前 0.110.10：正式视频性能与实际读取量

`performance-final10/runs/video-factor4/` 于 2026-11-04 02:26:08–02:26:20 UTC 完成三轮交替 A/B：
旧版 `64fa09e90cbf105507b32ebd5949f6f72d30a2eb`，新版
`6284dddc1a020b30b519118cfab9cd4b5a845415`。同一 1920×1080 视频文件缩至 480×270，
sequential、非透明、monitoring off，每进程 2 个预热帧及 8 个测量帧，CPU/Vulkan 分开比较。
正式进程均为 SCHED_OTHER/nice 0；观察到的后代 nice 为 0 或 9，两侧都存在，未修改后代策略。
其他本任务构建／测试／HTTP／桌面渲染及大规模哈希均暂停；窗口前仅普通桌面后台活动，
三次 GPU busy 为 15%／13%／11%，保留环境观察，不宣称设备绝对空载。

分析 `valid=true`、errors 空：120 个项目帧、12 个 probe、60 对旧新帧、12 个正常退出进程，
同后端完整像素与诊断一致。每版本每后端汇总 24 个 steady 样本，使用 `statistics.median` 与
nearest-rank p95，两项均按 `new <= old × 1.10 + 5 ms` 判定，**2/2 组通过**。单位为 ms。

| 后端 | 中位数：旧 → 新 | p95：旧 → 新 | 中位数 / p95 上限 |
| --- | ---: | ---: | ---: |
| CPU | 2.91135 → 1.19490 | 5.6260 → 1.7062 | 8.202485 / 11.188600 |
| Vulkan | 3.13245 → 3.33500 | 6.6284 → 6.0873 | 8.445695 / 12.291240 |

`trace-video/` 是独立的 old-instrumented/new-instrumented 批次，复用正式视频的原始文件字节，
没有重新生成容器。40 个项目帧、4 个 probe、20 对旧新帧及 44 项 trace 全通过；与原版另有
40 帧完整像素／诊断对照相同。每侧 10 个顺序请求中实际读取 4 个源帧，其余为已有源帧复用。
`actual-video-bytes.json` 累加真实 `video.stdout.read`，不是用输出尺寸代算：

| 后端 | 旧版实际总读取 bytes | 新版实际总读取 bytes | 结论 |
| --- | ---: | ---: | --- |
| CPU | 33,177,600 | 2,073,600 | 为旧版 1/16；steady 部分 24,883,200→1,555,200 |
| Vulkan | 33,177,600 | 33,177,600 | 原尺寸保护，无读取量缩减 |

`managed-allocation-summary.json` 同时保留托管分配比较：每轮全部 10 个项目帧的分配总和中位数，
CPU 为 11,033,048→11,078,584 bytes，Vulkan 为 10,564,416→11,032,288 bytes。
它是 `GC.GetTotalAllocatedBytes` 差值，包含异步托管分配，不含 FFmpeg/native、驻留或峰值内存；
不支持内存下降结论。codec 仍先完整解码，构造时保留原尺寸缓冲。背景阶段计时包含等待与绘制，
原始 trace 中分别保留，不据此声称消除了等待、实现原生 codec 缩放或 GPU 异步回读。
编码容量 2 与重叠原先已存在，本轮只修复拒帧所有权并验证既有反压／顺序／取消，不归为新增性能收益。

正式与 trace 共 16 个 Runner、37 个观察到的所属进程身份均已退出。`archive.py` 复核四份构建、
输入、矩阵、像素与 trace 对照后生成 `performance-final10-archive/evidence.tar.gz`，SHA-256 为
`022445ead32284796a17a9483b55fed01338820634ca243e519f3af5734e03e1`；同目录索引包含所有原始
样本、工具及环境。旧 0.110.9 performance 准备目录保留，不混入本次 .10 结论。

## 最终归档与范围

`final-app-archive/evidence.tar.gz` 保存 22 份桌面／浏览器报告、观察、截图及 preflight/relocation
脚本，SHA-256 为 `4adf7de5b1dd6999bab5c05f07b40ef2caad70a843c22ad38a334ef4fba58fbd`。
私有 profile 和可重建的静音 WAV 不进入归档，原始本机证据保留。`final-app-credential-scan.json`
扫描 28 文件、41,813,567 bytes，两项已知桌面凭证的精确扫描与凭证字段模式扫描均无命中；
浏览器临时 token 未保留，只作模式扫描，不能将其写成已知值扫描。

此前证据检索曾误输出隔离测试凭证，相关服务均已关闭，未进入版本化文件。
`final-known-credential-cleanup.json` 再对 4 份改动文档与三份归档的全部成员作两项已知值扫描：
共 320 项、28,101,290 bytes，命中为 0。三个所属运行凭证文件已随服务清理，额外的临时凭证
清单也已删除；只清理本次拥有的文件，不声称能撤回此前工具输出。

`final-desktop-build-state.json` 的 web/deployment 均为 null、passed true，当前源码与 .10 桌面发布、
Web 构建输入匹配。本节点通过限于上述 CPU 采样、GPU 保真回退及有限验证矩阵。GPU 目标采样的
收益仍未实现，不外推任意 codec／几何、长期资源趋势、完整 P3 性能或其他平台；SDK v2／M07 未开始。
