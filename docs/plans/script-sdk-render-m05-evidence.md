# M05 布局与有限中间等待优化证据

日期：2026-11-04。状态：**本轮独立优化通过：实现、回归、有限 HTTP、像素及三轮正式性能已验收**。
本记录不表示整个 S2 完成。本机证据根为 `.local/script-sdk-m05-20261104/`。

## 实现与发布

实现提交 `bb71ae9d82e806cf397365e3bd86628b634788e5` 与远程历史中的
`72a8585871c3b44ae8afd37404e60e00c8b62e4e` 均以 `3243c28` 为父，tree 同为
`0e80c90022e34e1b2d6bd455ca5447075069a548`。合并提交 `3a1bc43` 保留两个父提交，tree 未改变；
契约生成提交为 `64fa09e90cbf105507b32ebd5949f6f72d30a2eb`。

- 同一固定快照与字体集合的 Runtime 会话共享有界 CPU 整形结果；主字体和显式回退可共享，
  系统动态回退结果仍由各自引擎的弱引用缓存持有。布局键包含字体回退链，单个渲染器释放不清空共享结果。
- Ruby 按不可变行和样式缓存布局；有注音时建立 token 索引，无注音行不建立索引。只捕获正文时使用
  独立空列表，不清除后续绘制使用的缓存。
- `SnapshotSceneBelow` 在同一 device/context 快照之前移除一次主动 `FlushAndSubmit`；图校验、合成顺序、
  最终读回与外部 GPU 发布完成边界保留。此源码变化本身不证明性能收益。

产品 `0.110.8`、HTTP 合同 `1.42.4`。新 CLR metadata 的 freeze/validate 通过：112 路径、125 操作、
597 schemas、3431 示例，其中 2547 为反例；这里 `http: []`，并不代表合同工具重放了真实 HTTP。
匹配发布 `.local/backend-dist/m05-20261104/` 的 Server/Worker 为 Release、self-contained linux-x64，
manifest 标记 source clean，source commit 为 `64fa09e`，SHA-256 为
`4f77b2cd7981d909d1c9ac501ed819fee2ccbeb3c8280830e46e763ec6183445`。
`host.py` 完整校验 997 个发布文件、可执行入口、Worker script-refs 及共享脚本 ABI 字节；发布时源码输入
在快照复核中 965 项全部匹配。

## 构建与回归

版本化 solution 构建 0 警告、0 错误。日志与 TRX 位于证据根：Rendering 全集 1174 通过，
Runtime.Hosting 129 通过，Export 16 通过；最终 Rendering 定向组另有 95 通过，均为 0 失败、0 跳过。
这些组有重叠，不按独立测试数相加。GPU 相关定向组记录 60 通过、0 失败、0 跳过，涵盖 Vulkan 读回、
设备以及 CPU/GPU 参数化的材质图测试；现存 `gpu-tests.log` 与 TRX 未保存启动环境，条件用例依赖
`MAKO_GPU_SMOKE=1`，不能仅凭通过数量证明该组每个条件 GPU 分支实际执行。

独立变异验证 `red-green/summary-resume.json` 核对了 8 项实现与测试输入哈希，均与当时活动树一致。
动态字体回退隔离和正文捕获不破坏 Ruby 缓存的两个变异分别触发像素不一致，红灯 2/2；恢复后对应绿灯
2/2，Ruby 与字体回退定向组 18/18。此前中断的红灯运行没有完整结果，不计入统计；这些是 CPU 语义证据，
不代表 GPU 或性能验收。

## 同版 Fresh HTTP

`http/` 下两个 Oracle 只编译入口并引用同一个不可变发布的 Worker 运行程序集与 Server 编译依赖；
运行前后逐字节核验发布和 Oracle。并行 M06 工作使运行时活动树不再等于 M05 发布源，故从 `64fa09e`
导出 `http/source-64fa09e/`，逐项核对 965 个发布源码输入后运行各组；断言继续检查快照输入稳定。
各组均有脱敏 HTTP、断言标签、凭证扫描、PID/startTicks 排水和文件 SHA-256 manifest。

| 证据目录 | 有限范围 | HTTP / 断言 | 扫描文件 |
| --- | --- | ---: | ---: |
| `http/ruby-snapshot-2/` | 三类 Ruby 样例、透明/不透明、时间与布局、保存重开、旧 revision；27 个同版 Runtime 对照 | 233 / 2959 | 649 |
| `http/package-snapshot-2/` | healthy、diagnostics、lifecycle；55 次帧对照，graph 与 material 各 8 次含反事实比较 | 621 / 9411 | 1872 |
| `http/cancel-cpu-snapshot-2/` | 实际执行中取消、旧 generation 409、同一 Worker 恢复与 RGBA 一致 | 83 / 509 | 200 |

三组 `*-shutdown.json` 的 `remaining` 均为空；对应 manifest 的 650、1873、201 项文件哈希复核均无不匹配。
取消组先以原工具通过一次，后加 wrapper 保存完整断言标签及 manifest 并在新目录重跑；重跑没有删减断言。
Ruby 的首次 `http/ruby/` 因并行修改使当前源码不符，在任何 HTTP 前被源码门禁拒绝；
`http/ruby-snapshot/` 的旧 fixture 使用歌词 schema 17，现版要求 18，创建工程返回 422。修正快照 wrapper
的 fixture schema 后在新目录通过。Package 首次 wrapper 启动因 Python 导入路径失败，未发出 HTTP；
修正快照 wrapper 搜索路径后在新目录通过。失败记录保留，产品代码未为这些探针修改。

直接 Runtime 使用同版程序集，因此这些比较证明该部署上的 HTTP/Worker 与直接 Runtime 的有限一致性，
不能单独证明优化前后像素不变。CPU 取消也不能代替 GPU 取消、浏览器端验收或长期运行验证。

## 插桩路径诊断

`performance/runs/trace-main/` 的分析结果 `valid: true`：640 个项目帧、20 个 probe 帧、320 对匹配帧、
660 项 trace 验证及 20 个已退出进程。每个会话 32 个项目帧，graph、material、combined 的 CPU 和 Vulkan
两侧均有 32 次 `scene.backdropSnapshot`；`scene.mainSubmit` 从旧版每会话 32 次降至新版 0 次。
multi、graph、material、combined 的 `text.shape` 从旧版每会话 4 次降至新版 2 次；Ruby 两侧均为 3 次，
不能据此宣称 Ruby 的整形调用下降。Ruby 扫描与布局缓存属于单独的实现路径。这些是插桩调用计数和
有限同帧正确性证据，不作为正式耗时或性能门禁结果。

独立 CPU 计数诊断 `ruby-count-diagnostic/assessment.json` 为同一旧／新生产提交建立新源码副本，
只在两侧同签名的 `GetLineLayout`／`MeasureRuby` 入口及新版缓存命中处插桩，原有 performance
源码与构建产物保持原样。两侧各 8 个预热与 24 个测量帧，32/32 帧完整像素及诊断相同，trace 全部有效。
布局请求均为 160 次，实际 `MeasureRuby` 为旧 160、新 2，新版缓存命中 158 次；24 个 steady 帧中
请求均为 120 次、实际测量为旧 120、新 1、命中 119。这里证明重复测量与扫描减少，不以仪表耗时
替代正式性能。4 个 build/render 命令均正常退出；汇总器重复哈希造成的低效单独记在
`orchestrator-stop.json`，按 PID/startTicks 停止后仅运行 verify-only 完成核验，没有重复采样。

## 正式三轮性能与结论

`performance/runs/main/` 于 2026-11-04 01:58:20–02:22:34 UTC 完成正式旧新 A/B；
旧版 `0dc8d3fd4361cfb86219cbd8fb9392d4500e97b2`，新版
`bb71ae9d82e806cf397365e3bd86628b634788e5`。固定本机 Radeon 880M、同输入与字体、
1920×1080、sequential、非透明、monitoring off，每进程 8 个预热帧及 24 个测量帧；
共 3 轮，第二轮反转版本与后端顺序。普通调度 wrapper 只设置本次进程的 SCHED_OTHER/nice 0，
沿用原调度器及观察频率，不改 Runner 或产品字节。窗口内没有并行本任务构建、测试、HTTP 或渲染；
普通桌面后台仍存在，窗口前 GPU busy 三次为 1%。环境、命令与 wrapper 均随归档保存。

原始分析 `valid=true`、errors 空：2304 个项目帧、72 个 probe、1152 对完整旧新帧、72 个正常退出
进程；同后端完整 BGRA 与诊断一致，不宣称 CPU 与 Vulkan 之间逐像素相同。
版本化 `timing_gate.py` 按每组每版本 72 个 steady 样本，用 `statistics.median` 与 nearest-rank p95，
逐项判断 `new <= old × 1.10 + 5 ms`，**12/12 组两项门槛均通过**。下表单位为 ms。

| 场景 / 后端 | 中位数：旧 → 新 | p95：旧 → 新 | 门槛 |
| --- | ---: | ---: | --- |
| combined / cpu | 3191.226 → 3196.304 | 3291.647 → 3368.737 | 通过 |
| combined / vulkan | 350.394 → 348.515 | 395.352 → 384.678 | 通过 |
| graph / cpu | 405.393 → 407.125 | 457.135 → 426.503 | 通过 |
| graph / vulkan | 38.603 → 38.401 | 44.688 → 44.617 | 通过 |
| lyrics / cpu | 0.924 → 0.940 | 1.487 → 1.391 | 通过 |
| lyrics / vulkan | 2.498 → 2.448 | 5.479 → 3.626 | 通过 |
| material / cpu | 2705.306 → 2696.419 | 2796.361 → 2836.368 | 通过 |
| material / vulkan | 272.148 → 273.222 | 294.307 → 304.106 | 通过 |
| multi / cpu | 4.451 → 4.005 | 8.072 → 7.740 | 通过 |
| multi / vulkan | 6.599 → 6.576 | 9.416 → 9.333 | 通过 |
| ruby / cpu | 1.008 → 0.966 | 1.768 → 2.050 | 通过 |
| ruby / vulkan | 2.792 → 2.421 | 4.588 → 3.901 | 通过 |

这证明本轮有限矩阵没有超出已批准的回归门槛，并不表示每项耗时都下降或达到 16 ms。
实际成本改善由上节独立计数支持：共享整形 4→2、Ruby 重复测量 160→2、目标中间提交 32→0。
原版与独立 trace 的 640 个对应帧全部相同，`trace-comparison.json` 为 valid；trace 耗时不进入
正式门槛。原版与 trace 共观察 92 个所属进程身份，最终均已退出。

`archive.py` 重核生产输入、插桩差异、四份构建、完整矩阵、像素与 trace 对照后生成
`performance-archive-final/evidence.tar.gz`，SHA-256 为
`23e9869b8da8d1837ef589e02c41dd649ed034333c195bd79cace3d2360f12ca`；文件索引在同目录
`evidence-index.json`。归档保留 raw JSON、命令、环境、工具、统计及调用计数，不替代本记录上方的
HTTP 或最终 M06 桌面／浏览器功能验证。

## 留待后续的 S2 子项

图预乘颜色小结果校验的优化与更激进的空捕获优化转交 M12；当前保留原错误检测，不能以此宣称图校验
优化、整个 S2 或零中间回读完成。M12 应继续验证图计划、资源寿命、像素容差与诊断，不能吞掉原有检测。
