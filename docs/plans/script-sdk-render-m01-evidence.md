# M01 共享编译服务验收记录

日期：2026-11-03。状态：**功能、合同、实际 HTTP 与本机有限正式性能门槛通过；继续 M02–M06**。
范围：共享编译服务、profile 持久缓存、严格固定产物与租约。Worker 移除 Roslyn 属于 M02。

## 实现与测试

第一版源码 `7c8638c9acd52ef7289577131fe3d38a55e48381`，产品 `0.110.3`；
合同 `1.42.3` 在 `c63a63a` 冻结。目录 `.local/script-sdk-m01-20261103/` 保存本机原始证据。

- Roslyn 移至独立 Compiler 项目；作者编译使用 `{StateRoot}/script-artifacts`。
  同键请求合并，跨进程锁避免重复 Emit；参数、种子与非源码资源不进入编译键。
- format 3 描述分别核对输入身份和 PE/PDB 字节；SDK、实际引用、编译器、选项、引擎、规则和入口参与身份。
  严格 UTF-8 拒绝不能无损编码的字符串；不接受损坏缓存，不回退旧 DLL。
- 默认 PE/PDB 各 32 MiB、清单 1 MiB、总缓存 256 MiB；同一缓存闸门下保护租约并淘汰。
  降低配额后缓存命中和重复发布仍执行新配额。256 个固定编译锁条带避免失败源码无限留下锁文件；
  清理受中断的规范暂存闭包。预算约束读取和发布，不宣称限制 Roslyn Emit 峰值内存。
- `review-final-build.log`：完整 solution 0 警告、0 错误。
  Scripting 282、Runtime.Hosting 124、Rendering 1170、Export 177、Backend.Rendering 280、Hosting 1484，
  合计 3517 通过、0 失败、0 跳过；最终日志前缀 `review-final-` 或 `acceptance-final-`。
- 真实 Emit 计数覆盖同键 12 请求只 Emit 1 次、新 PID 缓存命中 0 次、两个竞争 PID 合计 1 次，
  参数／种子／资源不重编，源码仅影响所属包。禁用合并与条带锁的可逆变异均先失败、恢复后通过。
- 审查增加严格编码与降低配额的 3 项 red/green；其首次工具输出未落盘，不能用别的日志替代。
  最终全 Scripting 282 项包含这些回归，保存了 TRX。
- 初次相邻回归中的 4 项缓存目录旧断言已改为校验真正三文件闭包及失败无产物。
  Hosting 一次保存成功后立即关闭的竞态源于任务仍在收尾，测试按已有合同显式 `activeJobs: wait`，
  修订提交 `97d42bf`；生产保存／关闭语义未改。

## 合同与匹配发布

`metadata-build.log`、`clr-metadata.json`、`contract-validation.json`：112 路径、125 方法、597 schemas、
3431 示例（2547 反例）。生成器固定上述源码提交；校验器明确纳入 1.42.3 的既有形状门禁。
首次校验因未登记新合同号失败，补登记后通过，没有放宽历史例子和 schema 断言。

`dist/deployment.json` 记录产品 0.110.3、源码 `7c8638c`、`sourceDirty: false`，
Server/Worker 同源码 self-contained Release/linux-x64 发布及共享 SDK 字节检查通过。
这份发布仍含 Worker 的传递 Roslyn 依赖；不计作 M02。

## 三轮旧新 A/B 观察（设备占用，不能作为正式验收）

同一 `compile_probe.py`，隔离 profile，WordEmphasis 相同输入，每轮首次冷编译 1 次、暖编译 12 次。
旧发布为 M00 `0.110.2`，新发布为上述 `0.110.3`；交替顺序 old/new、new/old、old/new。
原始目录 `compile-ab-{old,new}-r{1,2,3}`；汇总 `compile-ab-result.json`。
计时为请求发送到完整同步 HTTP 响应读取，不含服务启动、UI 或帧显示。

| 范围 | 旧中位数 / p95 ms | 新中位数 / p95 ms | 110% + 5 ms |
|---|---:|---:|---|
| 冷编译，3 样本 | 1176.645 / 1183.229 | 1307.389 / 1405.527 | 数值超限，正式状态待测 |
| 暖编译，36 样本 | 16.728 / 34.615 | 4.306 / 7.810 | 数值范围内，正式状态待测 |

六个探针运行均正确完成、完整身份及诊断一致，凭证扫描及所属进程排水通过。
HTTP 产物 hash 一致不是 Emit 次数证据；真实次数由前述进程测试证明。
随后发现两批采样期间均有非本任务的游戏持续占用 CPU／GPU，因此这些数值只作观察，
不构成正式性能通过或失败。保留原始数据和当时算术比较，不据此放宽已确认门槛。

第一版匹配发布的实际作者工作流另存 `http-author-original`：204 请求、1357 断言通过，
涵盖 C#／图／shader 编辑、源码历史、编译、CPU 预览、配置保存与重启认领；凭证扫描与排水通过。
这份证据只归于 0.110.3，后续冷路径修复需用新发布重跑。


## 冷发布修复及最终 M01 功能验收

源码 `1adfe1a0fca8d426f2e803db4bfab72e12aff330`、产品 `0.110.4`：新产物以独占创建写入后，
用 4 KiB 栈缓冲逐字节核对 PE／PDB／清单及目录闭包，保留长度、EOF、链接检查；
避免立即重复解析自身刚写出的清单和分配完整 PE 副本。公开读取及已有缓存校验保持。

- `cold-publish-build.log`：完整 solution 0 警告、0 错误。
- `cold-publish-scripting` 282、`cold-publish-hosting` 112、`cold-publish-runtime` 24，合计 418 通过。
- `metadata-cold-fixed-build.log`、`clr-metadata-cold-fixed.json`、
  `contract-cold-fixed-validation.json`：新 metadata 冻结并验证 1.42.3，路径、方法、schema 和示例数同上。
- `dist-cold-fixed/deployment.json`：上述源码、`sourceDirty: false`，匹配 self-contained Server／Worker；
  deployment manifest SHA-256 `7733bff40047077ab109e2ab056f75112056b373c133b7139787d409cf1341d3`。
- `http-author-cold-fixed`：206 请求、1369 断言通过；新发布完成作者编辑／编译／预览、保存重开，
  凭证扫描和所属进程排水通过。计数随轮询次数变化，不是新增功能数量。

第二批观察为 `compile-fixed-ab-{old,new}-r{1,2,3}`、`compile-fixed-ab-result.json`：
冷编译旧 1334.406／2064.153 ms、新 1327.008／1755.360 ms；
暖编译旧 16.504／45.412 ms、新 4.423／16.324 ms（中位数／p95）。
状态明确为 `not_accepted_device_contention`，同样不计正式性能通过。

2026-11-03 用户明确选择“先继续功能开发与测试，稍后再测性能”。据此允许推进至 M06，
各节点功能、像素、资源寿命及回归门禁继续执行；正式性能仍待独占设备的同场景三轮 A/B，
维持中位数与 p95 ≤ 基线 × 1.10 + 5 ms 及真实成本下降要求。此为执行顺序调整，未豁免性能验收。


## 游戏退出后的正式三轮 A/B（通过）

用户随后告知已退出游戏。2026-11-03 14:15:50–14:16:25 UTC（北京时间 22:15:50–22:16:25），
本任务所有构建／测试均停止，使用同一 `compile_probe.py` 与上述固定 M00／M01 发布重测。
顺序 old/new、new/old、old/new；每次独立 profile，冷 1 次、暖 12 次。
`compile-idle-ab-{old,new}-r{1,2,3}` 保存六份原始证据，`compile-idle-ab-result.json` 保存
命令、部署身份、工具摘要、全部样本、nearest-rank p95 及逐项阈值比较。
采样前后记录了进程名、loadavg 和 GPU busy；无本任务竞争构建／测试、无游戏，普通桌面进程仍运行，
GPU busy 观察为 2–14%，不声称整台桌面零负载。此项测的是 CPU 作者编译 HTTP。

| 范围 | 旧中位数 / p95 ms | 新中位数 / p95 ms | 对应允许上限 ms | 结论 |
|---|---:|---:|---:|---|
| 冷编译，3 样本 | 1019.772 / 1033.856 | 1067.990 / 1104.293 | 1126.749 / 1142.242 | 通过 |
| 暖编译，36 样本 | 14.486 / 27.518 | 4.084 / 16.748 | 20.934 / 35.270 | 通过 |

六次各 25 个 HTTP 请求均成功；旧版每次扫描 60 文件、新版 66 文件，身份／完整诊断与来源保持，
所属进程均排水。结合前述真实 Emit 次数回归，M01 的有限性能与成本门禁通过。
旧两批争用数据继续保留为观察，不删除失败算术值，不将本次结果外推至其他平台、所有源码或后续节点。
