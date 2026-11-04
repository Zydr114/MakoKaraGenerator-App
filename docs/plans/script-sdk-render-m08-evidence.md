# M08 粒子与最终锚点验收记录

更新：2026-11-04。状态：**实现与有限回归已完成，节点尚未关闭**。
首批实现为 `5ca3667`、产品 0.112.0；真实边界负例发现的诊断修复与预算恢复验证
进入 0.112.1。合同、匹配发布、fresh HTTP 与 GPU 确定性采样分开登记，不能互相替代。
本机证据根为 `.local/script-sdk-m08-20261104/`；失败记录保留。

## 已实现范围

固定／分段线性精确累计发射、声明式 Burst、结构化粒子身份、最终文字锚点、
有界事务 memo、解析运动／拖尾、Auto／Custom 晚期图元和真实
CursorTrail／VectorTrail／EventBurst 包已接通。预算及取整规则见
[实现说明](script-sdk-v2-m08-implementation-notes.md)与
[配额算法](script-sdk-v2-m08-quota-algorithm.md)。

## 构建与回归

| 证据 | 实际结果及边界 |
| --- | --- |
| `build-03.log` | 全后端解决方案零警告／零错误，首批实现 |
| `core-v2-01.log` | 47/47，通过包与能力准入回归 |
| `scripting-v2-02.log` | 138/138；首轮 18 个资源修订夹具错误及 1 个旧 Overlay 预期已修正 |
| `scripting-full-01.log` | Scripting 全部 442/442；发生在后续 Load 诊断修复之前 |
| `rendering-v2-01.log` | 19/19，含真实包、远端时间、缓存淘汰、桥接与父变换 |
| `export-v2-01.log` | 7/7，三包 MP4／透明 ProRes 完整解码与独立帧对照，以及失败保留原文件 |
| `build-08.log`、`rendering-full-01.log` | 全解决方案零警告／零错误；Rendering 全部 1258/1258，含第一批 8 个边界测试 |
| `build-09.log` | 产品 0.112.1，全解决方案零警告／零错误 |
| `boundary-build-10.log`、`boundary-06.log` | Rendering 项目零警告／零错误；两批真实边界测试共 14/14 |
| `build-11.log`、`boundary-07.log` | 全解决方案零警告／零错误；含预算与中途取消恢复的全部 18 项边界测试通过 |
| `scripting-full-02.log`、`rendering-full-02.log`、`export-v2-02.log` | 同一最终构建串行复验：Scripting 全部 442/442、Rendering 全部 1268/1268、v2 导出 7/7 |

以上并非一次新的全后端全量回归。

## 边界缺陷及修复证据

`V2ParticleBoundaryTests` 使用真实 Skia 几何和独立完整文字会话，核对 token、line、
cursor、mark 四类出生锚点、line fallback、实际可见图元像素，以及无锚点诊断。
`boundary-03.log` 为 **4 通过／4 失败**：BindingCursors 空集合未拒绝；其余三类
已有具名异常被 Load 再次包装为 ScriptExecutionFailed。生产修复统一空锚点拒绝，
并保留已有 EffectFailureException 的 code／stage／owner。`boundary-04.log` 为
**8/8 通过**；这是实际旧实现的红绿证据。

第二批覆盖仅由其他文字实例声明的 group／line／tokenFamily／baseToken 断点并集；
实际进度路径的字簇切换与 Ruby；内行替换外行、重新装载身份和旧装载退场死亡两侧。
出生时刻与独立完整文字求值逐一比较，禁止跨身份插值。
`boundary-05.log` 为 12 通过／2 失败，分别是退场夹具漏声明能力，以及把生产规范化
省略的恒等变换误当作必返输出。修正夹具后，继续核对真实两个字簇最终矩阵和 Ruby，
未削弱边界断言；`boundary-06.log` 为 **14/14 通过**。

第三批在真实六粒子查询的第二个出生时刻触发耗时、共享内存或中途取消。
内部预算接缝仅允许收紧固定上限；单调假时钟代替睡眠，保留生产默认的累计补算
作用域和 `>3000 ms` 整数门禁。失败后原会话恢复结果与新会话的出生、图元、
像素及 memo miss／文字调用统计一致，没有保留半份出生锚点。内存例恢复较小查询后，
原大查询继续按同一限额拒绝。另一个用例核对计时作用域和 3000／3001 ms 边界。
四项随 `boundary-07.log` 通过；该证据验证失败路径，不是实际三秒性能采样。

## 未完成门禁

- 三个真实包的 HTTP 作者编译／显式采用、CAS、旧输入／租约与换代、GPU PNG、预算失败诊断；
  当前真实预算错误丢失 occurrence，必须修复后使用匹配新发布重验。
- M07 同场景正式性能门禁仍有未通过记录，不能由新粒子采样替代或调低门槛。
- 完整作者验证、保存库重开、九样例实际导出及 UI 调试等继续在 M11 验收。

## 合同与前端同步

生产修复提交为 `0f26db254bdf7562008ca7a573b2bc3fd6486bb1`。
从该基线重新构建 ContractMetadata（`contract-build-01.log`，零警告／零错误），
生成 `clr-metadata-01.json`，完成 freeze write／check 和仓库 venv 的 validate。
`contract-validation-01.json` 为合同 1.44.0、产品 0.112.1：112 路径／125 方法、
597 schemas、3434 示例／2548 反例。HTTP DTO 没有增加；上个合同以来 34 个生产
源码变化路径全部进入 manifest 指纹。

前端整体复制五件合同及行为文档，同步协议版本常量。
`frontend-build-01.log` 的 TypeScript／Vite 构建通过，保留现有大 chunk 提示；
`frontend-contract-01.log` 合同测试 3447/3447 通过。该轮没有重跑前端 integration
全套，合同同步不能代替后续实际 HTTP 或浏览器显示证据。

## 匹配发布及尚未通过的 HTTP

`.local/backend-dist/script-sdk-m08-0.112.1-01/` 同时发布 self-contained Release/linux-x64
Server 与 Worker；deployment 固定源码 `1432dc07d54939c5256d0e777572fb4a91e748b2`、
产品 0.112.1、`sourceDirty=false`，脚本程序集字节一致。旧发布目录保留。

`http-author-01` 在第 12 个请求停止：工具错误地要求请求中的可空 `branchInput:null`
在响应中仍出现，而正式 DTO 省略 null。检查其余完整可写字段与 CAS 后修正工具，未放宽
非空值保留或旧修订拒绝门禁。`http-author-02` 达到 138 请求／1018 已执行断言后失败：
CursorTrail 的实际出生预算错误只有 `originKind:transportFailure`，丢失原实例、包、
制品、时间与阶段 occurrence。该问题是生产诊断缺陷，不能删除精确归属断言绕过。
两次失败均保留证据、隔离 profile，完成所属进程排水与凭证扫描；不计作 HTTP 通过。

## 粒子确定性采样及文字场景性能回归

`runner-build-03.log` 和 `m07-runner-build-01.log` 均为零警告／零错误。
`performance-01` 使用当前固定二进制，串行三轮九个新进程，真实
CursorTrail／VectorTrail／EventBurst、640×360 CJK/Ruby、0／180／600 秒三段，
各自独立顺序／乱序会话与重复查询。30 组身份／确定性检查全部一致，累计 4896 次
结构或预乘 BGRA 哈希比较一致，保留所有帧资产和完整输入／DLL 身份。
该新粒子场景只报告原始耗时，没有获准的性能通过门槛；不是 v1 像素等价证据。

`m07-performance-01` 在相同 M07 场景完成三轮 15 进程；24 组 v1/v2 像素比较和
20 组跨进程确定性比较通过。以用户确认的固定历史基线执行 196 项正式耗时检查，
**6 项失败**：WordEmphasis 编译中位数／p95 为 1097.6994／1130.7634 ms（限
1074.86225／1085.990375 ms）；RepeatEmphasis 会话准备 p95 204.9605 ms（限
204.47775 ms）；ClusterExit 热查询准备位置 7 中位数／p95 5.2014／10.2508 ms
（限 5 ms），热绘制位置 0 p95 11.0588 ms（限 10 ms）。这不是正式性能通过。
旧 0.111.3 的 runner 已独立保存在 `runner-before-m08-rebuild/` 并固定 294 个文件摘要，
后续诊断构建、正式复测与历史证据分开。
