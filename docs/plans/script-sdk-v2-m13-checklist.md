# M13 发布候选检查清单（2026-11-13）

依据：[验收表](script-sdk-v2-render-acceptance.md) M13 行、[主计划](script-sdk-v2-render-pipeline-plan.md) S6、
[阶段二路线](script-sdk-v2-phase-two-roadmap.md) §8、[D8 核对记录](script-sdk-v2-d8-audit-20261108.md)与[能力台账](script-sdk-v2-capability-ledger.md)。
起点：源码 `20eb1e2`，产品 0.112.86，合同 1.51.0；P3 已完成，P4 按用户 2026-11-13 决定暂不排期（路线 §7）。

**状态：原实施完成；2026-11-14 Aorist 阻断补充修复及本次范围内验收完成，待用户验收，见 [修复证据](script-sdk-m13-aorist-fix-evidence.md)。§0 五项已于同日由用户按建议确认；证据见 [M13 记录](script-sdk-render-m13-evidence.md)。** M13 通过是 M14 删除 v1 的硬前置；未完成项与未批准缺口不能写成已通过。

## 0. 用户决定（2026-11-13 按建议确认）

| # | 事项 | 决定 |
|---|---|---|
| 1 | D8 证据缺口（§1 A3，2026-11-08 核对第 2～7 条，进 M12 时暂不补）补哪些 | 补 a～e；f 只补组合导出、±1 µs 与夹具，复杂背景、视频与缩放容差列为例外 |
| 2 | G-CAP-1 作者诊断通道（v1 有、v2 没有，2026-11-08 决定暂不补） | 确认为批准例外：SDK 2.0 不含，以后另升 SDK 版本再议 |
| 3 | §6 已知问题与限制 | 全部按“不阻塞发布候选”登记 |
| 4 | SDK 2.0 冻结怎么保证 | 加一条公开表面快照测试：`Scripting.V2` 的公开类型与成员变化即失败 |
| 5 | 前端冒烟范围 | M13 是阶段收口，全部冒烟各跑一次 |

## 1. D8 能力（表达、作者入口、执行、诊断、确定性、性能）

- **A1 逐项重核**：C01～C10、S01～S09 与附加四例（ClusterExit、AdaptiveStroke、StarSweep、WaveWarp）、T01，
  按 M13 时的源码与匹配发布重跑 2026-11-08 那套测试、探针与冒烟（§5 E2、F2），结果写入台账。
- **A2 P3 新能力并入 C02**：两态外观与跟随、硬边分层与按单字坐标、多层描边、注音装饰缩放、羽化、高亮；
  SDK 按态写入（`SetFill`／`SetStrokes`／`SetShadow`／`SetGlow`）与 `SetWipe`。证据已在 P3 各步（路线 §6.2），只需登记进台账。
- **A3 证据缺口**（待决定 1）：

  | | 缺口 | 做法 | 量 |
  |---|---|---|---|
  | a | C01 `mark.affine` 无渲染测试；C02 `SetShadow` 无像素证据，`group.opacity` 无 v2 渲染 | 各补一条 v2 像素回归（含 Vulkan） | 小 |
  | b | C08 `Random` 无直接测试；v2 只有 `NotAnalyzed` 音频输入，没有 Available 音频 | 补确定性测试（同 seed 同实例相同、换实例不同）；补 Available 音频输入的 v2 测试 | 小／中 |
  | c | 样例导出：除 S03／S04／S06 与 m09 固定包外没有导出证据 | 13 例 fresh HTTP 不透明 MP4，透明 ProRes 选含粒子、图、材质的几例（沿用 `m12_http_probe` 的导出） | 小 |
  | d | S07 CustomInk、S09 GlassInk 无样例级像素断言 | 各补固定时刻的像素回归 | 小 |
  | e | 作者链：S02、S05、S07、S08、S09、ClusterExit、StarSweep、WaveWarp 只有 m11 验证；S01 缺保存重开 | m07-author 探针按样例参数化：建草稿 → 编译 → 采用 → 保存重开 → 出帧 | 中 |
  | f | T01：组合无导出、无作者链与撤销、只取三时刻；夹具缺同句双实例、连字、缺时间、父子矩阵；复杂背景与容差未测 | 补组合导出、组合 ±1 µs、夹具补全；作者链由 e 覆盖；复杂背景（纹理、镜头切换）与视频按 2026-11-08 起暂缓视频分析的决定不做；缩放／倾斜／Warp 容差随 P4 移交 | 中 |

- **A4 能力差异**：G-CAP-2 工程时长已补（0.112.43）；G-CAP-1 待决定 2。
- **A5 性能字段**：不另设门槛。用 M12 收口（M12 记录 §6）与 P3 第 8 步复测（P3 设计 §11）作证据；
  P4 余项（分层缓存、演唱揭示、批次、缩放容差）按 2026-11-13 用户决定为批准例外。

## 2. 格式与版本

- **B1 清点**：工程 31、歌词 19、模板库 9、脚本配置库 2、效果合同 2、包 v3／SDK `2.0.x`／shader profile `skia-ganesh-3.119.4-v2`；
  包内图定义 1、SVG 资源 1、shader 程序 1；`.mktpl`、`.mkfx` 传输。验收表 M13 行没有写歌词 19，随本清单补上。
- **B2 每种格式**：一致读写（保存 → 重开 → 再保存一致）、旧版本明确拒绝（错误码，前端写明原因与受影响范围）、
  旧库原样保留（拒绝时不改写磁盘文件，不追加迁移）。先列出已有回归，缺的补。
- **B3 预设应用格式**：工程里的模板快照随 P3 换成两态样式，随工程 31 一起核对读写与拒绝（验收表 M13 行要求）。

## 3. CPU 与设备

- **C1**：含 v2 效果的工程，CPU 预览、验证、导出以 `PackageBackendUnsupported` 拒绝，前端写明原因；
  不含效果的工程 CPU 照常可用（预览与导出像素回归）。
- **C2**：桌面与浏览器在渲染设备选 CPU 时，对含 v2 效果的工程给出同一说明。

## 4. 合同与前端同步

- **D1**：当前构建导出 metadata，`freeze.py` 默认检查无漂移、`validate.py` 通过。M13 期间若改 HTTP 行为，按次版本升合同。
- **D2**：前端合同副本、`CONTRACT_VERSION` 与后端同一来源（冻结点 1.51.1，后端 `2c01b2f`，前端 `5a7747a`），单元测试核对 SHA。

## 5. 匹配发布、HTTP 与回归

- **E1**：M13 冻结点源码新发布匹配的 Server／Worker，记录发布目录与产物摘要。
- **E2**：fresh HTTP 探针：m07-author、m08-author、m09-author、m09-graph-author、m09-font-author、author-metadata、
  m10-media-white、m11-author-validation、m11-complex，加上 A3 c、e 的新增部分与导出兜底（P3 第 7 步 B）。
- **E3**：按自有 PID 排水，凭证扫描。
- **F1 后端**：`dotnet build` 零警告零错误；全量 `dotnet test` 逐项目；Vulkan 过滤集（`MAKO_GPU_SMOKE=1`）；
  3 条注音 HTTP 传输测试用新发布物；M07 矩阵与 M08 各一次（阶段收口）。
- **F2 前端与 App**：`typecheck`、`test:unit`、`build`、`test:integration`；冒烟按决定 5：前端 `smoke:browser`、`editor`、`appearance`、`glow`、`orchestration`、`project`、`render`、`lyrics`、`library`、
  `author`、`author-package`、`author-replacement`、`author-metadata` 与 `author-v2-smoke.mjs`；App 根 `smoke:desktop`、`smoke:desktop-library`。

## 6. 文档与冻结

- **G1 重写 SDK v2 参考**（`docs/style-reference/script-sdk-v2.md`）：去掉“M07”阶段字样，整合 M08～M11 与 P3 的表面，作为 SDK 2.0 作者参考，
  附九例与目标样例索引。
- **G2 ADR**（主计划 S6 要求，后端 ADR 目前到 0253）：纯函数阶段、宿主持有只读索引、执行计划、仅 Vulkan、v1 淘汰。
- **G3 冻结 SDK 2.0**：登记冻结点（版本与提交）；之后改作者可见表面须升 SDK 版本；快照测试按决定 4。
- **G4 记录**：新建 M13 证据记录，更新台账、验收表 M13 行与路线 §8。

## 7. 已知问题与限制（待决定 3）

| 问题 | 出处 |
|---|---|
| 扫字线高亮不叠在包材质部件上 | P3 设计 §10.3 |
| 从右向左排版时元素区间错开一个字形（硬切换与羽化同样） | P3 设计 §11 第 5 步 |
| 已唱描边没有工坊材质槽 | P3 设计第 7 步 A 实现注 |
| 源材质只作用于有 token 的行，无 token 的行不应用 | P3 设计第 7 步 B 实现注 |
| M07 ClusterExit 热查询准备 p95 约 9 ms（门槛 5，观察项）；G1 混合组 Render p95 44.2 ms（目标 40） | M12 记录 §5、§6 |
| t01 的 AdaptiveStroke 背景分析时序；Hosting 全量首轮偶发（“切到 1440p 偶发新流停在暂停”已由 `393581a` 修复） | 路线 §9 |
| 带 Mapping（Warp）且无材质的导出对象工作框不收紧 | 路线 §9 |
| 旧单态 HTTP 验收探针只对照 1.49.x 及更早 | P3 设计第 7 步 A 实现注 |

## 8. 顺序

1. 用户确认 §0。
2. 清点：B1／B2 已有回归、C1 现状、G2 现有 ADR，列出缺项。
3. 补证据：A3、B2 缺项、G3 快照测试，各自独立提交；改程序的按 PATCH 递增。
4. 文档：G1、G2。
5. 冻结点：新发布 → 合同检查 → 全量后端回归 → HTTP 探针 → 前端回归与冒烟 → M07／M08。
6. 证据记录、台账、验收表；交用户验收 M13。

M13 不做：P4 余项、Windows 与安装包、前端类型从 OpenAPI 生成（路线 §9）；删除 v1 属于 M14。

## 9. 清点结果（第 2 步，2026-11-13，源码 `561fc67`）

- **B2 已有**：工程 31 与效果合同 2 的旧版拒绝且源字节不变、旧 schema 保存门禁在建目录之前（`SdkV2PersistenceGateTests`）；
  模板库 9 与脚本配置库 2 的旧版／未来版拒绝、拒绝覆盖、旧目录原样保留（同上，`BackendTemplateLibraryPathTests`、`ScriptConfigurationLibraryTests`）；
  预设应用快照的模板版本不等即拒绝（`PresetApplicationsTests`、`PresetStaticProjectSnapshotCodecTests`）；SDK `2.0.x` 补丁号经 `.mkfx`／`.mktpl` 往返、
  `2.1.0` 准入拒绝（`EffectPackageTransferTests`、`StyleTemplateTransferTests`、`WorkerPackageProfileAdmissionTests`）。
  **缺**：当前工程配上一版歌词单独拒绝且字节不变（现有用例工程与歌词同时为旧版）。
- **C1 已有**：预览 CPU 拒绝（`PreviewTransportTests.SdkV2Backend`）、作者验证 CPU 拒绝（`BackendAuthorV2WorkflowTests`）、运行时拒绝（`V2PurePackageTests` 等）。
  **缺**：工程级导出与正式验证的 CPU 拒绝（`ProjectSessionRegistry.Export.cs`／`Validation.cs` 有检查、无回归）。
- **G2**：后端 ADR 到 0253（2026-11-05），v2 的纯函数阶段、只读索引、执行计划、仅 Vulkan、v1 淘汰都没有 ADR。
- **A3 a／b**：仍缺。`mark.affine` 无 v2 渲染用例；v2 `SetShadow` 只有合同测试；组透明度只有 v1 导出用例（`GroupOpacityExportTests`）；
  v2 `Random` 与 Available 音频输入无用例。
