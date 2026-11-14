# M13 补充：Aorist 视频背景复制与导出计时

日期：2026-11-14。范围：优化视频背景不可变快照复制，补齐必要阶段计时；不启动 M14。

## 问题与边界

较新的 `Aorist - nayuta & ARForest - 1` 为 1080p24。0.112.90 的真实导出记录为 6081 帧，
平均渲染调用约 72.9 ms；GPU 占用百分比不能区分 CPU 准备、绘制、同步或编码瓶颈。
独立复制真实 1080p BGRA 背景平均约 31.3 ms：当前 SkiaSharp `SKBitmap.Copy()` 经 CPU shader
绘制整图，背景每帧不可变复制使用了这条路径。调查与原始短段结果在
`.local/investigation-export-perf-20261114/report.md`，属于优化前证据。

本轮不引入硬件编码、异步回读、多帧渲染或新的用户设置，不改变预览控制节奏和 HTTP 形状。
用户工程不改；运行验收使用隔离副本。测试仅补复制语义和遥测汇总必要覆盖，复用已有场景、导出测试。

## 实现

- 产品 0.112.91。背景快照按原始像素复制，有 padding 时逐行复制有效字节，保留颜色类型、
  alpha 类型和色彩空间；快照独立分配、不可变、随帧释放，来源复用不影响已固定的帧。
  避免旧 shader 复制的额外预乘往返与半透明量化；不把可变视频位图直接标为 immutable。
- `BackgroundAcquireMs`、`BackgroundCopyMs`、`DrawMs`、`GpuSubmitMs` 补齐取帧、不可变复制、
  绘制和显式提交墙钟。既有 `ReadbackMs` 含义不变；嵌套阶段不可简单相加，提交计时不是 GPU 硬件时间。
- `ExportQueueWaitCalls/Ms` 独立统计编码队列入队调用，包括失败/取消；不增加渲染帧数或渲染 wall。
  保持有界队列、采样与限流，Hosting 校验和重编码新字段；细则见
  [性能监控](../../backend/docs/backend-performance-monitoring.md)。

## 验证记录

实现完整构建零警告、零错误。定向 Rendering 34、Export 94、Hosting telemetry drain 4 项通过。
仅新增 1 个 4-case 复制 theory、1 个阶段汇总 fact、1 个 2-case 编码队列 theory；另扩展既有环境模式
与日志转发测试。旧 Copy 变异在半透明的 2 cases 失败，恢复直接复制后 4 cases 全通过。
首轮完整构建因测试的 Assert.Throws 重载选择错误失败，改为明确 Action 后通过，不计为测试运行。
证据位于 `.local/fix-export-perf-20261114/`。

### 合同与匹配发布

产品 0.112.91，合同仍为 1.52.1。新 Release CLR 元数据 freeze/write/check 通过，合同验证
115 paths / 128 methods / 667 schemas / 3930 examples / 2839 negative cases，前端合同测试
3943 项通过。`pnpm build:desktop` 成功（前端保留既有大 chunk 提示）。发布目录
`.local/backend-dist/20261114T090520475Z` 来自 `49e5894`，1230 个后端源文件指纹全部匹配；
桌面 native manifest 和 Web 构建状态一并匹配。实现提交为 `baa5b43`。

### 同工程 A/B

旧发布 `.local/backend-dist/20261113T211023584Z`（0.112.90）与上述新发布独立运行。
同一个较新 Aorist 工程复制为两个隔离 fixture，新副本仅调整 applicationVersion 以满足现有
严格产品版本门禁；原工程及其媒体未改。Vulkan / Final / 1080p24，保留真实视频背景、
脚本与动态时间推进，不代理渲染设备、不固定背景。每段 120 帧，时间分别为 0–4.958333 秒
和 43–47.958333 秒。独立逐帧 SHA-256 对照共 240/240 帧完全一致；哈希运行不计入下表。

关闭遥测与哈希，使用真实 FfmpegMediaEncoder（standard，无音频）；每轮新建 session，
两轮交换 old/new 顺序。渲染均值剔除前 12 帧；端到端统计从第一帧请求至编码 Complete，
包含 120 帧与编码收尾，不含 session 创建、编码器启动、音频或 HTTP 作业调度。

| 段落 | 轮次 | 旧渲染均值 ms | 新渲染均值 ms | 旧段总耗时 s | 新段总耗时 s |
| --- | --- | ---: | ---: | ---: | ---: |
| 0 秒起 | old→new | 65.11 | 21.34 | 8.447 | 3.934 |
| 0 秒起 | new→old | 56.16 | 34.05 | 7.379 | 4.867 |
| 43 秒起 | old→new | 135.77 | 92.20 | 18.651 | 12.618 |
| 43 秒起 | new→old | 131.76 | 63.60 | 17.382 | 8.866 |

两轮段总耗时均值分别 7.913→4.400 秒（减少 44.4%）、18.017→10.742 秒（减少 40.4%）。
这是本机短段对照，轮间波动明显，未锁定机器频率或隔离所有桌面进程；不能外推整首或其他硬件
的固定提速比例，也不声称 GPU 达到 100%。记录在 `ab-results.json`、`raw-*.log`、`encode-*.log`。

### 新鲜 HTTP 与阶段计时

使用新发布和隔离 profile：GPU 静态预览 42.866666 / 50 秒 PNG 哈希与解码通过；正式 validation
无 issue。将副本输出长度设为 50 秒，GPU opaqueMp4 / standard / 带音频导出 succeeded，
1200/1200 帧、1920×1080、24 fps、H.264/AAC、零 fallback。下载字节数/SHA-256 匹配，
ffprobe 音视频均为 50.000000 秒，全片 ffmpeg 解码无错误。作业等待耗时 39.150 秒
（1 秒轮询粒度；含作业启动、音频与编码收尾），没有对应旧版 50 秒作业对照，不用于计算提速。

summary 模式、2 秒周期记录 1200 渲染帧与 1200 次入队：

| 阶段 | 每帧/次平均 ms |
| --- | ---: |
| Render wall | 25.886 |
| Prepare | 1.682 |
| BackgroundAcquire | 7.261 |
| BackgroundCopy | 1.046 |
| Draw | 10.093 |
| GpuSubmit | 5.574 |
| Readback | 4.051 |
| ExportQueueWait | 0.300 |

阶段包含嵌套与 GPU 同步，不能相加视为纯 GPU 时间。旧版 31.3 ms 为此前独立 Copy 实验，
新版 1.046 ms 为本次实际导出计时，二者不是单函数严格 A/B；性能结论以上述完整短段对照为准。
新增字段经 Hosting 转发完整保留；export Worker drain 为 accepted=36，invalid/oversized/
rateDropped/queueDropped 全为 0，事件 Errors/Cancelled/Dropped 均为 0。关闭工程、Server/Worker
正常退出、拥有的进程排空；370 个证据文件凭证扫描通过。详见 `http/summary.json`、
`http/fresh-shutdown.json`、`http-export-telemetry.json`。

### 桌面共享纹理与收尾

同一匹配发布的真实 Electron 桌面使用 session `export-perf-fix-20261114`。打开隔离副本，
通过既有验收桥将共享纹理尺寸设为 1920×1080，在 42.866666 / 50 秒各完成 runtime/drawn
精确 seek；GPU blit 累计 2/3、生产输出 CPU pixel readback 均为 0，previewDisabled=false，
无 preview failure。两张截图已人工复核背景、歌词与桌面预览正常；截图自身为验收读取，不计入
生产回读指标。此处验证共享纹理正确性，不宣称普通时间轴 UI seek、连续播放吞吐或性能数据。

launcher 正常 code 0，44 个拥有的 PID/startTicks 身份全部退出、remaining=[]；两个实际凭证值
在输出文件中未检出。验收后原工程 16 个文件逐个 SHA-256 复核不变。详见
`desktop/summary.json`、`desktop/shutdown.json`、`desktop/credential-scan.json`、
`desktop/source-postcheck.json` 与两张 `gpu-seek-*.png`。

构建已可用。现有严格产品版本门禁未改，原 0.112.90 工程没有自动迁移；0.112.91 验收副本位于
`.local/acceptance/export-perf-fix-20261114/desktop-profile/projects/export-perf-fixed`，可用
`node scripts/app.mjs desktop --session-name export-perf-fix-20261114` 打开该隔离 profile。
本轮止于复制优化、必要阶段计时及对应验收，M14 尚未启动。
