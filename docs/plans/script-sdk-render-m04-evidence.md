# M04 GPU 备用 Worker 验收记录

日期：2026-11-04。状态：**功能、匹配发布、真实 GPU 正确性、正式性能及对称初始化诊断通过**。
本节点只涉及 GPU 持续流准备；SDK v1 求值语义、浏览器静态预览和 500 ms 控制节奏保持。

## 实现与发布

实现提交 `9dada95`；发布源为 `0dc8d3fd4361cfb86219cbd8fb9392d4500e97b2`，
产品 `0.110.7`，HTTP 合同 `1.42.4`。本机证据根为 `.local/script-sdk-m04-20261104/`。

- 每 host 的两个槽位包含 opening、bound、spare、retiring 和 quarantine；最多一个未使用备用。
  使用过的 Worker 不返回备用池，每个新输入仍绑定新进程。
- 完整预热加载实际运行引用与实现程序集，创建 Vulkan device 和 Skia context，并执行真实绘制／读回探测。
  业务绑定转移原 device/context，单次绑定发生在业务输入解码前。
- 备用 TTL 为 30 秒，连续失败退避为 250 ms 至 30 秒。活动流已有 Worker 时直接查询该 Worker 的设备状态。
- 准备与准入核对实际 Worker 文件闭包及 manifest 字节；拒绝符号链接，不使用 mtime 缓存。
  排水失败保留槽位和输入所有权，继续按原 PID/startTicks 重试。

新 CLR metadata 的 freeze/validate 通过：112 路径、125 操作、597 schemas、3431 示例，含 2547 反例。
普通匹配发布 `.local/backend-dist/m04-20261104/` 为 clean source，manifest SHA-256
`abd85c45dadef783a6fbe8f31a27b939235e7392f21c1ac8b41498e8e3fbd508`。
Server/Worker 的共享程序集字节一致，Worker 不含 Compiler/Roslyn。

桌面另用 `.local/backend-dist/m04-desktop-20261104/`，manifest SHA-256
`ae5e45b63fae0cc76ac6cd6c8fc7d0a49a6bcff18cc403c4d57559dd83b08043`，普通发布未覆盖。
原生三文件复用源 `0be255d3197d52286341cd62080f352b317474b5` 的相同字节，已核对原生源码输入未变。
M03/M04 的隔离桌面发布各有 1001 文件；`ab-preparation.json` 记录完整 inventory、发布身份、
相同的 runner/main/preload/controller/Electron、12 个前端构建文件和原生 manifest。
各隔离树的前端合同 manifest 来自该侧实际后端合同，launcher 的 web/deployment 检查通过。

## 构建与回归

完整 solution 构建 0 警告、0 错误。CPU 回归 3767 项通过：Hosting 1536、Worker 139、
Runtime.Hosting 129、Server 504、Rendering 1170、Backend.Rendering 280、Backend.Rendering.Skia 9。
Rendering 使用 `MAKO_GPU_SMOKE=0`，这组结果不冒称 GPU 验证。
匹配发布后的 Ruby HTTP 三项另行通过，合计 **3770 项通过、0 跳过**。
日志、TRX 和分组统计见 `cpu-validation/`、`test-results/ruby-published.trx`。

对完整实际闭包、设备失败状态不缓存等关键边界，隔离阶段保留三项有效变异红灯及恢复绿灯；
它们与最终集成树的回归分别登记于 `.local/m04-scratch/m04-production-manifest.json`。

## 真实 HTTP 与 GPU

同一普通发布的 `http-seek-cpu/`：83 请求、516 断言，四次精确帧 PNG 完整读取，保持同一 Worker 身份；
198 文件凭证扫描通过。`http-cancel-cpu/`：71 请求、449 断言，脚本取消屏障和同进程 Runtime 恢复通过，
176 文件凭证扫描通过。两组自有进程均排空。这是本版本的有限回归，不替代 M03 正式三轮 seek 性能。

真实 GPU 使用当前版本的完整 WordEmphasis 包快照，SHA-256
`9294d692e72d5e6ef1e23db67573784a15d3644cbb0f0d40076e5156bb6ce24f`。
工具仅编译入口，直接引用上述不可变发布的 DLL；SDK/runtime/Host 不另行重建。
每次验收对部署作精确副本，部署变更测试只修改该副本并恢复。

`console-r3/` 通过：11 次业务打开含 8 次连续输入替换，实际准入为 8 次 opening-spare-wait、
3 次 prepared-spare-hit；最大槽位和观察到的存活 Worker 都为 2。原 device/context 初始化证明各为 1，
业务绑定保留原 preparation/initialization 身份。双活动流、第三次拒绝、无额外设备检查进程、
退回单流补热、30 秒 TTL 换代、部署 manifest 变化及恢复失效均通过；16 个原进程身份最终全部退出。

`dual-r1/` 使用两个独立 sandbox webContents 和生产 preload/native broker：两路均获得精确六身份的
native final release 与 drawn；关闭第二路后补备用，第一路原 attachment 继续完成帧，driver exit 0，
final-drain remaining 为空。该工具组合根用于两个真实消费者，不等于产品 UI 已提供双窗口工作流。
逐项断言与执行工具摘要见 `gpu-correctness-summary.json` 和 `gpu-correctness-tool-hashes.json`。

保留的工具失败：console-r1 的非法 JSON 在 Host 先抛出解析异常，超出工具预期捕获范围；
修正为合法 JSON 的无效快照后重新执行。console-r2 的存活观察器包含可变进程组身份，
console-r3 改为 PID/startTicks 后完整重跑。Host 无效快照负例只证明租约排水，
不替代直接 Worker 畸形 envelope 的单次绑定验证。

## 正式性能与剩余边界

2026-11-03 17:06:38–17:23:01 UTC（北京时间 11 月 4 日 01:06:38–01:23:01），
暂停本任务其他构建、测试与渲染后完成六次未插桩运行，顺序为旧／新、新／旧、旧／新。
三轮合并后，中位数使用 `statistics.median`，p95 使用 nearest rank；四类动作全部满足旧版 × 110% + 5 ms。

| 动作（每侧样本） | 旧版 median / p95 ms | M04 median / p95 ms | 门禁 |
|---|---:|---:|---|
| 输入替换（60） | 893.467 / 1045.545 | 868.594 / 1023.080 | 通过 |
| 挂接（60） | 1895.580 / 2053.239 | 1685.539 / 1987.196 | 通过 |
| UI 分辨率重建（30） | 2904.335 / 3085.126 | 2828.996 / 3054.306 | 通过 |
| 公共 resize 后 seek（30） | 834.003 / 952.715 | 736.277 / 916.712 | 通过 |

计时从 CDP 调用映射到 main 单调时钟，到精确六身份的首次 drawn/native final release 原始时间戳，
不把轮询返回时间、多帧正确性等待或动作后的旧 PID 排水加进终点。时钟映射不确定度另列。
每次运行保持实际 Wayland 可见窗口、ordinary scheduler、完整动作数量与原控制节奏，所有运行 exit 0 且 owned drain 为空。
`finite-idle-ab-r2/comparison.json` 保存完整原始样本、哈希、分轮观察和合并门禁。

首个调度尝试因证据目录不在隔离 App 的 `.local` 内而在启动前拒绝；目录保留。离线比较器曾错误地要求
UI 重建后的数值 generation 必须不同；新 stream 正常从 1 开始，现改为用原始动作的完整 stream/input 身份
交叉核验，并新增同身份负例。九项离线工具回归通过，原六次运行证据没有修改或筛选。

独立、对称的实际 device/context 初始化诊断完成，两侧各有 63 次成功业务打开。
旧版业务阶段的 device/context 创建增量全部为 1/1；M04 全部为 0/0，且同 PID/startTicks 绑定了业务前
真实 probe 的原 pair。业务 Worker 的完整创建数量分别为 2/2 和 1/1。所有进程（含独立设备查询等）
旧版记录 185 个身份、235 对实际创建，新版 148 个身份、135 对；该总量受动作与备用寿命影响，只作诊断观察。
两侧插桩工具完全相同，直接在实际创建成功位置计数；未将带诊断耗时用于正式比较。
两次完整 finite 运行 exit 0、owned drain 为空。清单和原始事件见 `.local/m04-init-diagnostic/`。

`malformed-worker-r1/` 进一步通过直接 Worker 协议负例：在真实 proxy 预热后借用原管道并持有原调用锁，
第一畸形内部 envelope 精确返回 `WorkerSnapshotBudgetExceeded`；同 PID 再次合法预热和完整 envelope
业务请求均精确返回 `WorkerGpuWarmupInvalid`。前后 device/context 证明各为 1 且原 pair、Process/管道对象不变，
证明首次失败已在输入解析前消耗单次绑定。最后仍由原 proxy Stop/Dispose，独立核对原 PID/startTicks 已不存在，
remaining 为空。此工具接缝只用于连续观察拒绝状态，不修改产品的遇错 fail-stop 行为。
有限动作比较不替代历史 P3 的 180 秒阶段、600 秒寿命和完整吞吐／新鲜度门槛。
本节点在上述本机有限范围通过；不外推其他硬件、长时 P3 或 SDK v2。
