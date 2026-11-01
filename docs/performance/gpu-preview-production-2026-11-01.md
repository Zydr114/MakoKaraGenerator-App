# GPU 预览生产验收记录：2026-11-01

最新用户评价（2026-11-01）：**本机低效果预览可用，用户认可最小原型实现；高效果性能仍不满意**，详见下节手测记录。
自动验收状态：**实现、有限功能与生命周期证据已归档；两次正式 720p24 滑动窗口均失败，P3 未通过，本轮停止**。
1440p／600s及正式交互生命周期尚未测量。显式普通调度复验仍失败，诊断没有确定单一根因，
也不覆盖首轮失败。本文区分源码、构建、合同、
HTTP、真实 native 消费、故障退出与性能，不把其中一项的成功扩大为其他项。

## 用户本机手测与原型评价

2026-11-01，用户在本机以 **1280×720、24fps 预览设置**实际使用后反馈：低效果场景可流畅预览，高效果或复杂效果明显卡顿。用户判断前后端预览传输链路未显示阻断级性能问题，仍不满意高效果性能，但认可当前最小原型实现。

用户同时观察到复杂效果导出约每秒完成 3 帧（约 3fps）。此估算来自导出界面进度条，没有后端埋点计时；导出分辨率、编码及其他条件未提供，不能将其写成 720p 导出基准，也不能据此把全部预览卡顿归因于渲染。

上述是用户在特定场景下的可用性观察和判断，不是完整帧计数或链路无瓶颈的证明，不替代自动门槛。下文两轮正式 P3 失败及原始数据保持不变。

## 实现与计数范围

生产路径为同一个固定 Runtime／VkDevice 的普通 Skia Vulkan BottomLeft surface，经一次
含 Y 翻转的 GPU blit 写入三槽 exportable DMA-BUF，再通过 SCM_RIGHTS 进入 Electron
SharedTexture／VideoFrame。最终预览帧不进行 CPU 像素回读或 PNG 编码；每帧一次整帧
GPU copy 与 queue-idle 等待仍存在，内部脚本／材质读回及 Chromium 内部复制另计。

唯一完整帧消费按完整 stream／host／producer／input／consumer／timeline／pool／slot／
slotEpoch／frameSeq 身份去重，只统计 canvas 已 draw 且实际 native `allReferencesReleased`
完成的交集。计数时间是该 native completion。它证明此消费者引用的完成，不能推出屏幕
扫描、真实 displayed FPS 或 Chromium 内部复制次数。rAF 和 draw 调用次数不算新增完整帧。

预览 canvas 的 backing 尺寸跟随选定的 1280×720 或 2560×1440 纹理；CSS 使用
`width:100%;height:100%;object-fit:contain`，随编辑器面板显示区域缩放。2560×1440 backing
不表示桌面上以相同 CSS 像素或物理显示像素一比一呈现。正确性截图独立于正式计时，
正式运行不调用截图或像素取样。

## 平台、版本和发布来源

本次范围为 Linux x64、AMD Radeon 880M（vendor 4098、device 5390，DRM 226:128），
Electron 固定 45.2.1；有限运行记录内核 `7.3.0-5-cachyos`、Mesa 26.2.5-arch3.1。
Chromium 的 ANGLE/OpenGL 后端记录与 Worker Vulkan 输出并存，不能凭环境变量断言
Ozone 平台。正式运行须保存实际 GPU／renderer 的 `--ozone-platform=wayland` 及可见状态。
本记录不覆盖 X11、Windows、macOS、其他显卡或任意工程内容。

| 发布目录 | 来源与边界 |
| --- | --- |
| `.local/backend-dist/20261031T192835836Z` | 历史真实 HTTP 和短时 quality debug 所用发布；清单记 `c409d3a`，各证据另保留实际 sourceInputs，不归给后续提交 |
| `.local/backend-dist/20261101T044730838Z` | 来源 `273abab`、dirty=false；真实作者 Error 两轮和早期 lifecycle 场景所用发布 |
| `.local/backend-dist/20261101T045924324Z` | 来源 `4297e027917fcc1f2dd4249b4f425341a59815e8`、dirty=false；包含 `f1fe7f8` Server shutdown 修复，用于四类故障重验、两次正式矩阵及后续诊断 |

最新发布由 `pnpm build:desktop` 成功构建，日志位于
`.local/build-logs/desktop-shutdown-fix-20261101.log`。部署清单 SHA256 为
`fdaa354a0809150a57df47cc48e578b5844e909f3ba831b2804aff3524777ac2`，native 清单为
`b0f4ac8a0a817c673de7056805d0984cc2d0cdd052573035eb7d68f9a7db2c51`。
Server DLL 为 `fcce34c09ef285bd2e43f3983867b230db6b942e6adef35a4ff506703d59b260`，
Worker DLL 为 `aed4e769741c00cadafffaa3a498bfb0bb4c124b2c6b5b95b614cb05710baea8`。
native 三项产物与清单逐一匹配，Program／ServerShutdown／GpuStreamSession 的
sourceInputs 与此次实际源码 SHA256 匹配。后续纯报告提交不改变该发布来源。
attempt1 runner 的启动 HEAD 为 `db8cee2`，其源码 SHA256 为
`c6d8f45549911377bbcd717de063398b6d7cef5dc54528e2608dfa772f9e571e`；attempt2 为
`82d8f93f3ed8cccd777e4e8de253ffbbb211a348`，runner SHA256 为
`6835b54b57ece648e7062ffd44f65979e61735ea23c0e12f662c87495c75e334`。main／preload
是启动时读取的 App 源码，前端是实际构建 bundle，均在运行 manifest 单独记哈希，不能把
runner HEAD 归给已发布 managed／native，也不能把当前源文件归给旧进程已加载的版本。

## 分层验证

| 证据 | 已验证范围 | 实际界限 |
| --- | --- | --- |
| Worker `f93dda6` 测试 | 全套 138、GPU 定向 25 项通过 | 单元测试，不是 native 生产消费 |
| Worker telemetry `09920f4` | mailbox／stream 定向 14 项通过；硬编码 render=0 的变异失败后恢复通过 | 独立 Worker 占位／峰值计数，不用 main 计数代替 |
| [Hosting／合同记录](evidence/production-2026-11-01/hosting-validation.json) | 构建、focused tests、合同生成与无 desktop HTTP 检查 | 各项来源和发布分别记录；合同校验本身没有 HTTP 请求 |
| [真实 HTTP](evidence/production-2026-11-01/http-negative.json) | 32 项 owner／CAS／strict JSON／claim、1440p 实际池换代、拥有的 Worker 退出 | 未连接 native 消费者，无帧或呈现结论 |
| [短时质量联调](evidence/production-2026-11-01/quality-debug.json) | 两档和短 lifetime 各约五秒；121／151／150 个唯一完整帧，质量／切页／公开 resize 新代次消费成功 | debug；旧发布缺 Worker telemetry；无持续性能结论 |
| [真实 Runtime 有限矩阵](evidence/production-2026-11-01/runtime-finite-summary.json) | 20 次 pause、20 次 seek、20 次输入替换，20 次 attach/detach、10 次质量重建及 10 次公开 resize/seek；旧 Worker 与新代身份复核通过 | 来源冷启动及生效后 p95 均未达到候选；五秒阶段不构成性能结论 |
| [真实作者 Error](evidence/production-2026-11-01/author-error.json) | 实际 Evaluate Error、诊断、drained:true、新流成功、App 身份不变 | 触发前旧帧已经完成 |
| [作者 Error 在途补验](evidence/production-2026-11-01/author-error-inflight.json) | 1000ms 延迟消费者下 Error 后仍保留一个旧租约，精确 completion 后才排空；新健康流成功 | 正常作者失败，非 crash 回收；单次有限场景 |
| [桌面 lifecycle](evidence/production-2026-11-01/desktop-lifecycle.json) | 慢消费者／隐藏／丢失注入及 trace 的有限原始记录 | 历史 Server -6 保留为缺陷记录；新发布重验另列 |
| [故障退出复验](evidence/production-2026-11-01/desktop-faults-fixed.json) | renderer／Worker 各一轮注入及一轮外部精确 PID 丢失，Server／launcher 受控退出 1，拥有的进程全部退出 | 完成未知的三个 lease 隔离，非安全归槽；严格离线复核来源另列 |
| [正式 attempt1](evidence/production-2026-11-01/synthetic-formal-attempt1.json) | 720p24 180s、可见 Wayland、同帧时钟／Worker 独立预算、最终 CPU readback=0 | 滑动5s最低117帧未达到118，自动停止；实际调度策略未记录 |
| [调度预检](evidence/production-2026-11-01/scheduler-preflight.json)／[显式普通调度预检](evidence/production-2026-11-01/scheduler-explicit-preflight.json) | 后续同类启动观察到IDLE继承／动态重置；显式普通调度预检5.03s、六次快照的实际pipeline为OTHER | 不是吞吐测量，不能追认attempt1历史策略或证明因果 |
| [性能诊断](evidence/production-2026-11-01/desktop-performance-diagnostic.json) | 单次有埋点180s，ready后延迟与main事件循环延迟重叠 | 诊断增加计时器与探针，不能替代正式结果；根因未定 |
| [正式 attempt2](evidence/production-2026-11-01/synthetic-formal-attempt2.json) | 720p24 180s、显式普通调度；179个快照逐一复核实际四类pipeline角色身份与策略 | 滑动5s最低116帧未达到118，其余阶段门槛通过；1440p／600s及正式交互生命周期未测 |

作者 Error 在途场景观察到：main／Worker outstanding=1，ACK=0，Worker `draining` 且
errorCount=1；约 882.73ms 后旧 lease 的实际 native completion 发生，main ACK 才增至 1，
最终公开 Worker 状态 `failed`／`drained:true`。主进程、renderer、Server 的 PID/startTicks
保持不变，同一作者来源的新健康 stream 再完成一帧。自有 launcher、两代 Worker 与其余
进程全部确认退出。该等待区间是有限人工慢消费观察，不能解释为生产延迟统计。

四类故障原始运行来源为 `7161f71`，保持原报告与日志不变。`921b943` 的严格 analyzer
在原日志上离线复核 4／4 通过，另有 32 项变异断言通过；拒绝缺失 Server 退出行、错误
退出码、launcher 信号退出或 ACK+1。analyzer SHA256 为
`16449be32f40c2febfa72e437d7f080803dcc5685e4e3f1adc32996005641e2e`，本地严格结果
`.local/gpu-preview-lifecycle/fault-fixed-final/report.strict-analyzed.json` 的 SHA256 为
`161ddd2b4516620acd210aa47899525ede040886fc6522d88f787715cd74ea30`。注入场景精确
loss→fail-stop ACK 差为零；外部丢失仅证明 before-kill 观测→检测的 ACK 差为零，不冒充
精确 loss 瞬间基线。该离线复核没有启动 GPU、没有重跑，也不覆盖原来源哈希。

## 正式 P3 矩阵与交互边界

两次正式矩阵都在首档失败后停止，第二次改变了显式调度条件，原门槛保持不变。生产 transport、broker、同步、池和消费者保持相同，仅选择受控
synthetic GPU provider 以隔离内容开销；真实 Runtime 功能验收单列。每档预热 10s，
720p24／1440p30 各记录 180s，1440p30 lifetime 记录 600s。计时期间禁止截图、像素取样、
高频 trace、其他验收实例或构建。仍在运行的用户实例只能记录为并发环境，不能擅自停止。

| 阶段 | provider | 实测时长 | 唯一完整消费率 | 最差滑动5s | 间隔p95／p99 | 新鲜度及不确定度 | Worker预算／资源趋势 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 720p24 attempt1 | synthetic-gpu | 180.0296s | 4320帧，23.9961fps，99.9836% | **117＜118，失败** | 44.12／45.63ms | 绝对drift上界p95 11.31ms，通过 | render／pending峰值1，lease峰值3；11,059,200B |
| 720p24 attempt2 | synthetic-gpu | 180.0265s | 4320帧，23.9965fps，99.9853% | **116＜118，失败** | 43.97／45.14ms | 绝对drift上界p95 11.35ms，通过 | render／pending峰值1，lease峰值3；11,059,200B |
| 1440p30 | 未测 | 未测 | 未测 | 未测 | 未测 | 未测 | 未测 |
| 1440p30 lifetime | 未测 | 未测 | 未测 | 未测 | 未测 | 未测 | 未测 |

两次720p24的整体帧数、间隔、新鲜度、同帧时钟证据、尺寸／provider、最终 CPU readback=0、
可见 Wayland、Worker 预算和 main 流控均通过对应阶段门槛；各自唯一失败均为精确滑动5s
计数。attempt1最大间隔149.756964ms，Worker ready→native completion p95／p99为
7.011125／8.274786ms，含映射不确定度上界为8.990897／10.254558ms；age p95为9.32652ms，
绝对drift含不确定度p95上界为11.307292ms。attempt2最大间隔169.604398ms，
ready→native completion p95／p99为6.611694／7.594595ms，含不确定度上界为
8.637314／9.620215ms；age p95为9.319967ms，绝对drift含不确定度p95上界为11.346587ms。
两次>250ms停顿均为零，完成事件无缺口；这些通过项不能抵消滑动窗口失败。
全局`phaseMatrix:false`和`freshness:false`包含矩阵未完成，不能误读为后续档或已测新鲜度失败。
两次canvas backing均为1280×720，实际CSS约207.2×116.55px。attempt1六个被采样自有身份、
attempt2八个被采样自有身份确认消失；runner停止并等待自己持有的launcher句柄。
attempt2未将Server纳入进程清单，不能扩充为所有Server身份均独立核对退出。

attempt2阶段179个快照逐一检查main→Browser、renderer→Tab、Worker→worker、设备所指
GPU→GPU的准确PID和非空稳定startTicks，角色没有缺失，策略均为SCHED_OTHER(0)，
Worker nice=0，main／GPU nice=-8、renderer nice=0。独立离线复核保留在attempt2归档，
不把更强复核追认为原analyzer当时已有的门禁。这里的连续覆盖指每次采样观察，不能证明
未采样瞬间。runner与launcher均观察到SCHED_IDLE(5)／nice16，作为探针环境记录，未纳入
实际pipeline门槛。Server仅在此前5.03s的六次显式预检中观察到OTHER／nice0，正式180s
阶段没有其身份清单覆盖。`sched_schedstats`关闭，零runqueue-wait不能证明没有调度等待。

attempt1没有记录实际调度策略；后续预检观察到IDLE继承及外部动态重置，不能倒推历史
PID策略或认定唯一根因。`939a832`新增的显式验收开关只对新命名验收实例启动的子进程
在exec前设置普通调度，不修改系统策略、已有进程或正常用户启动；普通条件下attempt2
仍失败，首轮结果原样保留。

180s资源观测中，attempt1 Worker FD为132→137、范围114–150并周期回落，线性斜率约
0.00334FD/s；main FD为122→123、范围122–128，renderer FD恒61。attempt2 Worker FD为
131→139、范围114–152，main为124→124、范围122–128，renderer恒61。两次Worker DRM
内存19,898,368B与main 2,109,440B恒定；attempt2 GPU进程约133.36–176.10MB。
attempt1 main／Worker RSS各增加约11.9／11.5MB，attempt2分别为12,603,392／11,436,032B。
这些原始趋势需结合缓存与采样解释，不能据三分钟结果宣称600s稳定或不存在泄漏。

吞吐门槛为总唯一完整帧达到目标的 99%，每个半开滑动 5s 窗口达到目标的 98%，间隔
p95／p99 分别不超过 2／3 帧周期，无未解释的 >250ms 停顿。新鲜度以实际音频 master
对照帧 timeUs，正负 drift 均计入绝对误差，并加同一帧映射的不确定度；候选 p95 不超过
两帧周期。Worker render、pending control、paused target 各最多 1，实际池为 3，实际分配
不超过池／host 预算；FD、RSS、GPU 分配趋势与租约高水位需要结合 600s 结果解释。

交互分开报告两个起点。UI 调用到匹配完成帧包含固定来源捕获、编译、Worker 冷启动、
资源上传、握手及控制处理；历史单次 latest-input 为 1080.60ms（含校准不确定度），不能
写成低于 150ms。生效后的消费延迟从精确新代次 hello-ready 或匹配 Worker control-applied
ACK 被 main 观察到的时刻开始，候选 p95≤150ms／p99≤250ms；其结果不能替代端到端冷启动。
main receive→observed applied ACK 也不能声称是纯 Worker apply 时间。

独立真实 Runtime 有限矩阵各有20次：UI调用至输入替换帧的p95／p99为1202.08／1262.11ms，
含调用校准不确定度上界为1203.08／1263.75ms；从新代次hello-ready生效起点计为
164.06／182.72ms，p95仍超过150ms，不能通过换起点隐藏此失败。pause生效后p95／p99为
5.78／6.18ms，seek为8.35／8.64ms。20次的p99仅是最近秩观察尾部，不是总体尾部分布。

Worker ready→native completion 包含调度、broker、import、消费者引用寿命及同步，
报告保守映射上界；不能将其解释为纯 GPU blit 或扫描延迟。20 次 pause／seek／来源替换、
20 次 attach/detach 与 10 次公开 resize/seek 的精确身份和旧 Worker 退出另作有限矩阵。

Chromium 原始 trace 仅保存在 `.local`，Git 归档保留路径与 SHA256。trace 本身采样 10s，
其 before／after 还覆盖 trace 结束和流式导出等额外时间，不能用这两个快照的计数差除以
10s 推导 FPS。trace 用于诊断消费／合成事件，不计入无 trace 正式吞吐矩阵。

## 启动、复现与本轮结论

完成本仓库桌面构建后，正常入口为：

```sh
pnpm desktop
```

显式普通调度的完整正式runner复现命令如下；它创建新的命名CDP验收实例，使用隔离
profile和loopback，不需要root，不修改已有进程或系统调度规则。运行前应确保这些端口
空闲，构建与其他验收已结束；仍运行的用户实例只记录为并发条件。

```sh
node scripts/gpu-preview-p3.mjs --ordinary-scheduling \
  --session-name p3-synthetic-repro --provider synthetic \
  --port 5186 --cdp-port 9236 \
  --output .local/gpu-preview-p3/synthetic-repro
```

此命令是复现入口，不表示本轮再运行或承诺后续档已通过。原attempt2源码与原始报告哈希
保留在归档；后续`43c3986`补齐analyzer逐快照身份覆盖门禁，23／23项回归通过，
在原日志上离线重算179／179覆盖通过、滑动窗口仍为116＜118。新分析产物独立归档，
不覆盖原运行版本或原始report哈希。

正式attempt2的最大完成间隔发生在frameSeq 3874→3875：169.604398ms。两帧render分别
0.773326／0.826867ms，推算producer-ready间隔42.130642ms；后一帧ready→broker为
14.512552ms、broker→native completion为117.570948ms。独立有埋点诊断也在ready之后
观察到main事件循环延迟与消费者完成延迟，renderer draw保持很短。证据将异常范围收窄到
ready之后的传递、main处理与消费者引用完成区间；尚不能区分OS运行队列等待、未观测的
Chromium原生任务或其他事件循环工作，更不能将跨度当作纯GPU copy时间或单一调度根因。

本轮完成生产实现与有限功能／生命周期验证，性能验收以失败收口，停止继续运行或调参。
1440p180s、1440p600s、正式synthetic交互生命周期仍未测；真实Runtime输入生效后p95
仍未达候选门槛。现有证据不支持性能通过交付，也不扩大为屏幕扫描FPS、任意内容性能、
crash后安全复用、其他平台或零GPU copy的结论。
