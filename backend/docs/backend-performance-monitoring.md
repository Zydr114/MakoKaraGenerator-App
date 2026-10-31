# 运行时性能监控

性能监控用于观察 Worker 中真实 Runtime 帧和作者 C# 回调的宿主 wall 时间，日志不是安全审计记录，也不是 GPU 硬件计时。此功能不提供 Server 自身 HTTP 请求 CPU、GPU 硬件时间或显存指标；与公开 HTTP 合同无关。

## 配置

在启动 Server/Worker 的环境中设置以下变量。Worker 在启动时读取一次，Hosting 创建子进程时继承环境；`tools/backend-web-host/host.py` 不清除 `MAKO_PERFORMANCE_*`。

| 变量 | 默认 | 语义 |
| --- | --- | --- |
| `MAKO_PERFORMANCE_MODE` | `summary` | `off` 关闭；`summary` 仅周期摘要；`detailed` 在摘要之外采样逐帧事件。忽略大小写；其他值按 summary。 |
| `MAKO_PERFORMANCE_DETAIL_EVERY` | `1`（detailed） | 每 N 帧输出一条明细，限制 1..1000000；仅 detailed 生效。 |
| `MAKO_PERFORMANCE_INTERVAL_SECONDS` | `10` | 周期，限制 1..3600 秒。 |
| `MAKO_PERFORMANCE_MAX_EVENTS_PER_SECOND` | `10` | 每 Worker 输出速率上限，限制 1..100；摘要保留一个输出名额，设置为 1 时不输出 detail。 |

例如 `MAKO_PERFORMANCE_MODE=detailed MAKO_PERFORMANCE_DETAIL_EVERY=10` 采样约十分之一的帧。无效整数使用默认值；超出范围时裁剪。设置 `off` 后不会创建遥测帧或阶段状态；Worker 没有任何 stderr 输出时 Hosting 不写 drain 计数，其他 stderr 仍可能产生 drain 计数。短生命周期 Worker 在正常关闭时尝试排空并输出尾摘要，等待最多 500ms；挂起的日志 sink、限速导致多后端摘要未能在关闭前输出或被强杀的 Worker 不保证尾包。

进程启动时配置一次；`Configure` 不应作为运行时热切换接口。重复配置会异步停止旧采集器，`ShutdownAsync` 仅等待当前采集器，不能保证已替换采集器的尾包。

## 日志与边界

Worker 仅在 stderr 写入 `MAKO_PERFORMANCE_V1 ` 加 JSON，stdout 仍保留原有二进制协议。Hosting 丢弃其他 stderr 内容，并对遥测行执行 8192 字符上限、每 Worker 每秒最多 10 行的限速、固定字段校验及 JSON 重编码，再写入 Server stderr。转发前缀为 `worker_telemetry_untrusted pid=<pid> MAKO_PERFORMANCE_V1 `。作者代码可自行写 stderr 或伪造合法遥测；前缀和 pid 只代表该子进程管道的观察，不能证明事件可信、不能作为安全审计。未知 JSON 字段不会被转发。Hosting 输出有界队列满时丢弃，不等待慢日志；Worker 采集队列容量 128、进行中帧槽位 128、每帧脚本身份最多 16 条。

Worker stderr EOF 后，Hosting 尝试输出固定格式 `worker_telemetry_drain pid=... accepted=... invalid=... oversized=... rateDropped=... queueDropped=...`，并等待输出队列屏障最多 200ms；队列已满或 sink 挂起时计数行与尾摘要也可能丢失。`accepted` 为通过白名单的行数（包含随后因队列满而丢弃的行）。

所有 JSON 字段为 PascalCase。`Version` 为 1，`Kind` 为 `summary` 或 `frame`。`TimestampUtc` 是 UTC ISO 8601；`WindowMs` 为对应 backend 自上次成功输出摘要以来的时间，逐帧通常为 0。摘要按 backend 汇总窗口内完成的帧；限速时待发 backend 轮流优先，未发出的数据保留到下轮；没有完成帧也定期产生心跳。以下字段均为固定基数、数值或受限标识：

| 字段 | 含义 |
| --- | --- |
| `Backend` | `cpu`、`gpu`、`unknown`。 |
| `Request`, `Session` | 内部 ID 的 16 位 SHA-256 前缀或 `none`；不是 HTTP 公共身份。摘要关联窗口最慢完成帧。 |
| `Sequence`, `Width`, `Height` | 帧序号和目标像素尺寸；摘要为最慢完成帧。 |
| `Frames`, `Errors`, `Cancelled` | 窗口内完成、失败、取消的帧数；frame 事件代表一个帧。 |
| `WallMs`, `MaxWallMs` | 完成帧 wall 毫秒之和及窗口最大值；单帧事件的 `WallMs` 是本帧。 |
| `PrepareMs`, `ScriptMs` | 前者为宿主准备（含回放和作者调用），后者为实际 C# 作者回调，二者重叠。 |
| `GraphMs`, `MaterialMs`, `ParticleMs`, `RasterMs`, `ValidationMs`, `ReadbackMs` | 各阶段含其嵌套阶段的宿主 wall 毫秒，可重叠，不可相加推算帧总时长。`MaterialMs` 从材质执行核心入口开始，不包含该入口之前的场景区域捕获；`ParticleMs` 覆盖宿主粒子模拟操作。 |
| `ScriptCalls`, `Scripts`, `ScriptOverflow` | C# 回调次数；明细中 `Scripts` 按实例短哈希与固定 `Stage` 聚合 `Calls`、`WallMs`、`MaxMs`；超过 16 个键计入 overflow。直接图并非 C# 作者回调。 |
| `ImageReads`, `ImageBytes`, `SurfaceReads`, `SurfaceBytes` | 图像或表面读回次数及请求字节，不代表物理总线流量。 |
| `ProcessAllocatedBytes`, `Gc0`, `Gc1`, `Gc2` | 帧期间进程级分配与 GC 差值，可能被其他并发活动影响，重叠帧求和可能重复计数；并非脚本独占成本。 |
| `ProcessCpuMs`, `ProcessWorkingSetBytes` | 摘要时整个 Worker 累计 CPU 时间和进程工作集；不是 GPU 时间或显存。 |
| `ActiveFrames`, `LongestActiveFrameMs`, `ActiveOverflow` | 当前追踪帧数量、最长已执行 wall 和超出活动槽位的累计次数；初始化前没有帧时仍有进程心跳。 |
| `Dropped`, `DetailEvery` | Worker 采集或输出丢弃次数及明细采样间隔；Hosting 另有独立限流和队列，Worker 不知道后者的丢弃。 |

脚本阶段是 `Initialize`、`Advance`、`Evaluate`、`OnEvent`、`AdvanceVisuals`、`EvaluateVisuals`、`EvaluateEmitters` 或 `other`。这些回调只有在渲染帧上下文内才归入该帧；会话创建前或帧外执行的初始化不产生单独脚本事件，进程心跳仍可观察 Worker CPU 和工作集。日志不包含歌词、源码、完整路径、token、诊断正文或原始用户标识。采样、队列溢出和不同时间窗口意味着日志不是完整帧账本。

日志输出在后台线程执行；队列满、限流、异常或关闭等待超时时不阻塞渲染及业务错误传播。阶段用 `finally`/`Dispose` 收口，失败默认为 error，取消显式标记 cancelled。不同帧可以并发，内嵌 scope 使用异步上下文关联；超出帧生命周期的子 scope 不再计入该帧。

## 0.107.0有限实测

2026-10-28的同执行体off／summary／detailed三轮短批次对比中，Vulkan组合样本的summary／detailed中位数相对off分别增加35.053／36.846ms（10.70%／11.25%）。轻负载百分比更易受基数和调度波动影响，CPU歌词summary增加0.551ms（53.11%）。不能把监控称为零开销，具体分布见[验收报告§5.5](archive/acceptance/backend-gpu-structural-performance-optimization-acceptance-20261028.md#55-监控模式成本与日志完整性)。

该性能矩阵直接写本地JSONL，不含Server／Worker转发成本；默认配置下detailed的216个候选帧明细输出189条、限速丢弃27条，摘要仍覆盖全部432个开启监控的项目帧，像素诊断与off一致。此前真实HTTP日志中零丢弃属于另一有限样本，不能推定日志始终完整。阶段采集开销、首帧及会话创建仍是后续优化方向。
