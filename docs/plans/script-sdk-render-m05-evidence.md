# M05 布局与有限中间等待优化证据

日期：2026-11-04。状态：**实现、定向回归及同版有限 HTTP 已记录；正式性能待验收**。
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

## 正式性能与待办

**待补：正式性能尚未通过。** trace 运行用于路径计数和正确性诊断，不能作为无插桩正式性能结果。
待其他 CPU/GPU 工作停止后，在静默窗口完成既定旧/新同输入、多轮、无插桩比较，记录原始样本、
像素与诊断差异、设备和环境、所有进程排水以及计划中的阈值判定；同时单列 trace 的实际提交与命中证据。

图预乘颜色小结果校验的优化与更激进的空捕获优化转交 M12；当前保留原错误检测，不能以此宣称图校验
优化、整个 S2 或零中间回读完成。M12 应继续验证图计划、资源寿命、像素容差与诊断，不能吞掉原有检测。
