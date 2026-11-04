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

- 合同 1.44.0 的 fresh metadata 冻结／校验、前端合同同步、匹配 Server／Worker 发布。
- 三个真实包的 HTTP 作者编译／显式采用、CAS、旧输入／租约与换代、GPU PNG、预算失败诊断。
- 三轮九进程粒子矩阵及 M07 同场景正式性能回归。M07 ClusterExit 热查询准备仍有未通过记录，
  不能由新粒子采样替代或调低门槛。
- 完整作者验证、保存库重开、九样例实际导出及 UI 调试等继续在 M11 验收。
