# M13 发布候选：证据记录

日期：2026-11-13，`dev/script-sdk-phase-two-structure`。依据 [M13 检查清单](script-sdk-v2-m13-checklist.md)（§0 五项用户决定）、
[验收表 M13 行](script-sdk-v2-render-acceptance.md)与[阶段二路线 §8](script-sdk-v2-phase-two-roadmap.md)。

**状态：冻结点回归完成，待用户验收。** 起点源码 `20eb1e2`（产品 0.112.86，合同 1.51.0），冻结点产品 0.112.88、合同 1.51.1、SDK 2.0；
桌面 GPU 附着竞态修复 `393581a`（不改后端与协议）。

## 1. 冻结点与环境

| 项 | 值 |
|---|---|
| 生产源码 | `a1f4379`（最后一次改 `backend/src`），产品 0.112.88 |
| 合同 | 1.51.1，后端 `2c01b2f`（基线 `a1f4379`），前端副本 `5a7747a` |
| 冻结点提交 | `5a7747a`；其后 `95a9545`、`4d92c2f`、`1f93787`、`34d3a02` 只改探针与冒烟脚本（§9.2），不动 `backend/src` 与前端源码 |
| 桌面 | `393581a` 修复附着竞态（§3 缺陷 3），只改 `desktop/main.cjs` 与预加载脚本；桌面构建为 `.local/backend-dist/20261113T123310066Z`（源码 `1f93787`，后端与冻结点相同） |
| SDK | 2.0；公开表面快照 `backend/tests/MakoKaraGenerator.Scripting.Tests/Snapshots/sdk-v2-public-surface.txt`（`99fd41c`，274 个类型） |
| 匹配发布 | `.local/backend-dist/script-sdk-m13-0.112.88-02`（`host.py publish`，源码 `5a7747a`，`sourceDirty:false`，linux-x64 自包含 Release） |
| 发布摘要 | `deployment.json` `19582b97…0fd8`；Server 可执行 `006d8e22…e401`；`Backend.Server.dll` `b58f3228…21a0`；`Backend.Worker.dll` `df60c511…d697`；`Scripting.dll` `939f76cc…8171` |
| 机器 | Radeon 880M（RADV，`/usr/share/vulkan/icd.d/radeon_icd.json`），CachyOS，电源模式 performance |
| 证据根 | `.local/script-sdk-m13-20261113/`（不入库）：`f1-freeze/`、`e2/`、`h2/`、`f2/`、`contract/`、`freeze-point.txt` |

## 2. 本轮补的证据（检查清单 §1 A3、§2、§3、§6 G3）

| 缺口 | 提交 | 内容 | 变异 |
|---|---|---|---|
| B2 歌词单独拒绝；C1 工程级导出／验证 CPU 拒绝 | `ceb4144` | 导出三条入口与正式验证：用到 SDK v2 的工程未选 GPU 时，在登记任务、取输入、调用执行器之前以 `PackageBackendUnsupported` 拒绝，引用、预留与调度器归零；当前工程配上一版歌词报 `UnsupportedSchemaVersion`，两个文件字节不变 | 去掉运行输入导出与运行验证的检查，对应用例失败 |
| A3 a：`mark.affine`、`SetShadow`、`group.opacity` | `21b045c` | 真实 v3 包→工程实例→编译→纯求值→渲染：两态阴影与静态阴影逐字节相同；前奏标记平移／旋转缩放与静态编辑逐字节相同；根组 `MultiplyOpacity(.5)` 预乘像素减半（舍入差 ≤2）；CPU 与 Vulkan | 阴影只写未唱、标记矩阵改在父空间、透明度开方，各自失败 |
| A3 b：`Random`、Available 音频 | `d832436` | 正式准备会话：Declare／Evaluate 取值等于 `DeterministicRandom(seed, 实例 Id)`，回跳与重开不变，换实例或种子即不同；真实 WAV 分析为 Available，低频取色与静态编辑逐字节相同，越界 `OutsideRange` 不写外观 | 实例身份固定为常量、会话改传 `NotAnalyzed`，对应用例失败 |
| A3 c／f：样例导出与 T01 组合导出 | `6985f77` | `m13_export_probe.py`：13 个样例与 T01 不透明 MP4，T01 与五个含粒子／图／材质的样例再做透明 ProRes4444；成品与预览同一时刻比较 | 首轮暴露 §3 缺陷 1 |
| A3 d：CustomInk、GlassInk 样例级像素 | `72a53ff` | 字形内部像素按 README 公式核对；GlassInk 透明输出每字节等于兜底色 `#3380CCBF` 乘覆盖（差 ≤1）；CPU 与 Vulkan | 宿主时间 ×1.1 使 CustomInk 失败；关闭透明兜底使 GlassInk 抛 `GraphBackgroundBypassMissing` |
| A3 e：逐样例作者链 | `414510f`、`1f93787` | `m13_author_chain_probe.py`：S01、S02、S05、S07～S09、ClusterExit、StarSweep、WaveWarp 共九例；冻结点补跑 S03、S04、S06、AdaptiveStroke | 首轮暴露 §3 缺陷 2 |
| A3 f：T01 夹具补全与 ±1 µs | `1c521f5` | 组合夹具并入同句双实例、缺时间字、跨 token 连字、场景父子矩阵；字、行、行间空档、退场终点、连字行前后 1 µs 取帧；倒序与重开签名相同；CPU 与 Vulkan | 副本晚 1 µs 出现、退场终点含端点，新用例失败 |
| G3 SDK 2.0 冻结 | `99fd41c` | `Scripting.V2` 公开类型及其签名可达的项目内公开类型写入快照，任何变化即失败；首行 SDK 主次版本须与包准入一致 | `ShadowValue` 默认 `distancePx` 4→5，快照失败 |

文档：G1 SDK 2.0 作者参考重写（`a8bc1b6`，`docs/style-reference/script-sdk-v2.md`）；G2 ADR-0254～0258（`f55a4cd`）。

## 3. 本轮发现并修复的缺陷

前两项由 M13 新探针发现，均为行为修正，HTTP 形状不变，合同随之升 PATCH 到 1.51.1（§6）；第 3 项由桌面冒烟发现，只改桌面端。

| 产品 | 提交 | 问题 | 修复与回归 |
|---|---|---|---|
| 0.112.87 | `fd62ca1` | 工程预览经 `PUT …/input` 从 opaque 切到 straight 后，依赖背景媒体的包（AdaptiveStroke、T01）以 `WorkerPackageRuntimeFailed` 失败：换输入按旧会话的 alphaMode 固定媒体输入 | 按请求的 alphaMode 固定（straight→NoMedia，反向同样修正）；Server 回归 opaque→straight→opaque 逐次核对固定输入，旧实现在第一次切换处失败 |
| 0.112.88 | `a1f4379` | 模板工坊与作者草稿试样每次捕获都重新抽取实例身份与种子，同一草稿反复刷新时粒子散布不同（作者链探针 SpriteTrail 帧不一致） | 按配方与绑定次序分配固定身份 `fx:workshop-sample-{n}`，种子取配方 `fixedSeed` 或试样种子；Runtime 回归 |
| 桌面 | `393581a` | 桌面 GPU 新流首帧偶发不显示：主进程先发 `attached` 事件再返回附着结果，两者分开送达，事件晚到时清掉页面已发出的首个暂停控制，回包不记闸门序号（`hardControlSeq` 停在 null），唯一的暂停帧以 `waitingBarrier` 丢弃，直到下一次播放才恢复。M12 记录 §5.8 ① 的“新流停在暂停”即此 | 每次附着编 `attachSeq`，返回值与事件谁先到用谁，晚到重复事件与旧附着的 `detach` 丢弃；两条预加载回归在旧实现上失败，`desktop/gpu-preview` 26 条通过 |

## 4. D8 逐项结论

基础证据见 [2026-11-08 逐项核对](script-sdk-v2-d8-audit-20261108.md)，本表只列 M13 补上的部分与结论。
P（性能）按决定不另设门槛：以 M12 收口（M12 记录 §6）与 P3 第 8 步复测（P3 设计 §11）为证据，P4 余项为批准例外。
P3 新能力（两态外观与跟随、硬边分层、多层描边、注音装饰缩放、羽化、高亮，SDK 按态写入与 `SetWipe`）并入 C02，证据在 P3 各步（路线 §6.2）。

| ID | 2026-11-08 缺口 | M13 补充 | 结论 |
|---|---|---|---|
| C01 仿射 | `mark.affine` 无渲染 | `21b045c` 标记仿射像素；T01 父子矩阵 ±1 µs（`1c521f5`） | E/A/X/D/T 齐 |
| C02 样式与透明度 | `SetShadow` 无像素、`group.opacity` 无 v2 渲染 | `21b045c`；P3 两态与 `SetWipe` 并入 | E/A/X/D/T 齐 |
| C03 材质 uniform／图 | 无 | 冻结点 m09-graph-author 六场景重跑；CustomInk、GlassInk 样例级像素（`72a53ff`） | E/A/X/D/T 齐 |
| C04 事件与历史 | RepeatEmphasis 作者 UI 链 | author-v2 RepeatEmphasis UI 冒烟（0.112.43 起通过）；作者链探针 RepeatEmphasis | E/A/X/D/T 齐 |
| C05 连续发射与 Burst | 无 | 冻结点 m08-author 重跑；导出探针含 CursorTrail、EventBurst、SpriteTrail、VectorTrail | E/A/X/D/T 齐 |
| C06 解析运动、桥接、拖尾 | 无 | 冻结点 M08 跨进程 `equal` | E/A/X/D/T 齐 |
| C07 Overlay／Sprite／SVG | 无 | 冻结点 m09-author 重跑；SpriteTrail 导出与作者链 | E/A/X/D/T 齐 |
| C08 音频、随机与媒体特征 | 音频只有 `NotAnalyzed`、`Random` 无直接测试 | `d832436`；m10-media-white、m11-complex 重跑 | E/A/X/D/T 齐；视频分析按 2026-11-08 决定暂缓 |
| C09 字段状态的实际用途 | 样例范围内无未映射用途 | — | 齐（同 C04／C05） |
| C10 Warp | 无 | WaveWarp 导出与作者链；T01 Warp 起唱包络 ±1 µs | E/A/X/D/T 齐；缩放／倾斜／Warp 容差随 P4 移交（批准例外） |

样例（全部 13 包冻结点 m11-author-validation 360＋360 帧通过）：

| ID | 2026-11-08 缺口 | 作者链（M13 探针，保存重开逐字节） | 导出（M13 探针） | 结论 |
|---|---|---|---|---|
| S01 WordEmphasis | 导出、保存重开、UI 链 | 通过 | 不透明 | 齐 |
| S02 RepeatEmphasis | 作者链、导出 | 通过 | 不透明 | 齐 |
| S03 CursorTrail | 保存重开 | 通过（冻结点补跑） | 不透明 | 齐 |
| S04 VectorTrail | 保存重开 | 通过（冻结点补跑） | 不透明＋透明 | 齐 |
| S05 SpriteTrail | 作者链、导出、预算负例 | 通过 | 不透明＋透明 | 齐（数量预算负例在 `V2MixedOverlayBudgetTests`） |
| S06 EventBurst | 保存重开 | 通过（冻结点补跑） | 不透明 | 齐 |
| S07 CustomInk | 样例级像素、作者链、导出 | 通过 | 不透明＋透明 | 齐（`72a53ff`） |
| S08 LineGlitch | 作者链、导出 | 通过 | 不透明＋透明 | 齐 |
| S09 GlassInk | 样例级像素、作者链、导出 | 通过 | 不透明＋透明（透明带 4 条兜底诊断，导出成功） | 齐（`72a53ff`） |
| ClusterExit | 作者链、导出 | 通过 | 不透明 | 齐 |
| AdaptiveStroke | 导出 | 通过（冻结点补跑，先等完整背景分析，§9.1） | 不透明 | 齐（视频背景暂缓） |
| StarSweep | 作者链、导出 | 通过 | 不透明 | 齐 |
| WaveWarp | 作者链、导出 | 通过 | 不透明 | 齐 |

T01 目标复杂样式：

| 步骤 | M13 后 |
|---|---|
| 1 固定测试工程 | 齐：同句双实例、连字、缺时间、父子矩阵已并入组合夹具（`1c521f5`） |
| 2 作者链与撤销 | 组合整体作者链由逐样例作者链覆盖（决定 1）；撤销链由 M11 验证与单项测试覆盖 |
| 3 边界 ±1 µs | 齐（`1c521f5`） |
| 4 顺序／乱序／重复／重开 | 齐：倒序取帧与重开签名相同 |
| 5 背景与媒体修订 | 纯色、图片、Pending→Available；复杂背景（纹理、镜头切换）与视频按决定 1 列为例外 |
| 6 预览与导出 | 齐：不透明 MP4 与透明 ProRes4444 与预览对照通过；容差测量随 P4 移交 |
| 7 性能 | 按决定不另设门槛（M12 收口与 P3 第 8 步） |

v1→v2 能力差异：G-CAP-2 工程时长已补（0.112.43）；G-CAP-1 作者诊断通道按决定 2 为批准例外，SDK 2.0 不含（ADR-0254）。

## 5. 格式与版本、CPU 与设备

- **B1**：工程 31、歌词 19、模板库 9、脚本配置库 2、效果合同 2；包 v3、SDK `2.0.x`、shader profile `skia-ganesh-3.119.4-v2`。
- **B2**：已有回归见检查清单 §9；本轮补当前工程配上一版歌词单独拒绝（`ceb4144`）。旧版拒绝时源文件字节不变、不迁移。
- **B3**：预设应用快照的模板版本不等即拒绝（`PresetApplicationsTests`、`PresetStaticProjectSnapshotCodecTests`），随工程 31 一并核对。
- **C1**：预览、作者验证、运行时拒绝已有；本轮补工程级导出三条入口与正式验证（`ceb4144`）。无效果工程 CPU 照常（全量回归中的 CPU 像素用例）。
- **C2**：桌面与浏览器选 CPU 时由同一 `PackageBackendUnsupported` 说明：author-v2 冒烟在真实界面核对 CPU 下试样与验证的具名拒绝，
  `smoke:render` 走 CPU／GPU 切换（§7.4）。

## 6. 合同与前端同步

- **D1**：冻结点构建导出 metadata 后 `freeze.py` 检查发现漂移（产品 0.112.86→0.112.88 与两项行为修正）。
  按执行合同 §4“行为兼容的勘误用合同 PATCH”冻结 **1.51.1**（`2c01b2f`）：`freeze.py` 只改版本、基线 `a1f4379` 与 scope，
  `validate.py` 把 1.51.1 并入现行门禁、历史 1.51.0 对应 0.112.86；形状不变（115 路径／128 方法／663 schema，3926 例、2837 反例）。
  `--write` 后默认检查与 `validate.py` 均通过。
- **D2**：前端合同五件与 `backend-api.md` 整体复制自 `2c01b2f`，`CONTRACT_VERSION` 1.51.1、`CONTRACT_PRODUCT_VERSION` 0.112.88（`5a7747a`）；
  typecheck 与单元 4481 条（含合同 SHA 核对）通过。

## 7. 冻结点回归

### 7.1 后端（F1）

`dotnet build MakoKaraGenerator.slnx` 零警告零错误。全量逐项目测试（`MAKO_GPU_SMOKE=1`，3 条注音 HTTP 传输用例指向 E1 发布物）：

| 项目 | 通过 | 项目 | 通过 |
|---|---|---|---|
| Application | 749 | Export | 188 |
| Backend.Hosting | 1676 | Media | 434 |
| Backend.Rendering.Skia | 9 | Processes | 52 |
| Backend.Rendering | 286 | Rendering | 1905／1906 → 重跑通过 |
| Backend.Server | 537（含 3 条注音 HTTP） | Runtime.Hosting | 157 |
| Backend.Worker | 145 | Scripting | 712 |
| Core | 1572 | Timeline | 84 |

合计 8507。唯一失败是 `MainCanvasImageEffectsTests.EmptyRubyCapturesPreservePixelsAndPhysicalSurfaceAdmissions`
（读回指针集合偶发两项），即已登记的偶发项；整类单独重跑 106／106 通过。

### 7.2 HTTP 探针（E2、E3）

全部在 E1 发布物上 fresh 启动、独立 profile、Vulkan：

| 探针 | 结果 | 覆盖 |
|---|---|---|
| m07-author | 通过，760 断言，14 s | WordEmphasis 编辑、编译失败与修正、显式采用、旧 CAS 拒绝、CPU 拒绝、GPU 预览 |
| m08-author | 通过，2411 断言，31 s | CursorTrail／VectorTrail／EventBurst 完整作者链与晚期预算失败归属 |
| m09-author | 通过，2582 断言，40 s | svg-vector／svg-atlas／warp-base 完整包编辑、坏 SVG、修复、保存重开、导出 |
| m09-graph-author | 通过，9182 断言，141 s | 六个图／材质场景作者→保存重开→导出 |
| m09-font-author | 通过，1397 断言（修正探针后重跑，§9.2） | 固定字体资源采用、重开、透明导出 |
| author-metadata | 通过，4336 断言，18 s | 声明元数据与原子采用 |
| m10-media-white | 通过，13 s | AdaptiveStroke 白底媒体轨 Pending→Available |
| m11-author-validation | 通过，73 s | 13 包各 360 不透明＋360 透明帧（9360 帧） |
| m11-complex | 通过，24 s | 复杂样式三时刻、Ruby 检查投影、AdaptiveStroke 作者试样 |
| m13-export | 通过，111 s | 14 组不透明 MP4（RGB 均值差 1.7～8.5，限 12）；6 组透明 ProRes4444（预乘 0.11～0.43、alpha 0.21～0.90，各限 2）；GlassInk 透明导出 `fallbackDiagnosticCount` 4 |
| m13-author-chain | 通过，85 s | 九例作者链，注释修订采用前后帧与保存重开前后帧逐字节相同 |
| m13-author-chain 补跑 | 通过（`1f93787` 后） | CursorTrail、VectorTrail、EventBurst、AdaptiveStroke 同一链；AdaptiveStroke 两帧均带描边 |

导出兜底（P3 第 7 步 B）由 m13-export 的 GlassInk 透明导出覆盖：缺背景时导出成功并在任务结果中列出兜底诊断数。
每个探针按自有 PID＋startTicks 排水（shutdown 记录退出码 0），并对证据目录做凭证扫描（前六个探针共扫描 6659 个文件）；结束后无遗留 Server／Worker 进程。

### 7.3 M07／M08（阶段收口）

- **M08**：3 轮 × 3 个 fresh 进程，30 组结构与 Vulkan 像素 SHA 全部 `equal`。
- **M07**：正确性（像素、结构）全部通过。性能项 196 项中 12 项超过 2026-11-04 门槛：WordEmphasis／RepeatEmphasis 的编译、准备会话、
  首轮渲染比 P3 第 8 步慢约 30%（编译中位 902→1179 ms），另有已登记的 ClusterExit 热查询准备 p95（9.8 ms，门槛 5）。
  A/B：同一机器状态下 P3 第 8 步源码 `223be39` 也是 13 项超限、数值相同（编译 1141 对 1144 ms、准备会话 229 对 228 ms），
  期间 `backend/src` 只改了两处共 15 行且不在 M07 路径上，判为环境因素（测量时有持续占用约一核的用户程序），不是 M13 回退。
  结果在 `h2/m07*-thresholds.json`。

### 7.4 前端与 App（F2）

- typecheck、单元 4481、`pnpm build` 通过；集成测试 19 文件 191 条通过（E1 发布物）。
- 浏览器冒烟（决定 5，各跑一次；E1 发布物＋`dev-backend.mjs` 独立 profile＋Vite）：

  | 冒烟 | 检查 | 冒烟 | 检查 |
  |---|---|---|---|
  | browser | 18／18 | library | 18／18 |
  | editor | 60／60 | author | 6 |
  | appearance | 34／34 | author-package | 83 |
  | glow | 19 | author-replacement | 8 |
  | orchestration | 95／95 | author-metadata | 140 |
  | project | 28／28 | author-v2 WordEmphasis | 35 断言／5 帧 |
  | render | 15／15 | author-v2 RepeatEmphasis | 18 断言／2 帧 |
  | lyrics | 44／44 | author-v2 ClusterExit | 18 断言／2 帧 |

  glow、author-package、author-replacement、author-metadata 与 lyrics 为第二轮结果（§9.2：URL 环境与歌词 schema 夹具）。
  两轮 Vite 均以 SIGTERM（143）结束、后端退出码 0。
- 桌面冒烟（App 根目录，`pnpm build:desktop` 后）：
  - `smoke:desktop` 修复前 pnpm 启动 3／3 卡在首个外观页出帧（另一次直接 node 启动卡在“改回跟随默认”的新流），
    同一会话以 attach 方式抓到现场后定位为 §3 缺陷 3；修复后两轮 18／18 通过（GPU 共享纹理出帧、无 CPU 回读、
    播放 2 秒、拖动、改背景换输入、1440p 重建、编排页、离开与回到预览页、CPU 退回静态预览、改回跟随、关窗确认与排水）。
  - `smoke:desktop-library`：脚本仍按单态辉光与 templates-v7 编写，跟随后（`34d3a02`）22 项通过：
    schema6 旧库只复制不迁移且字节不变、写入 templates-v9、同 profile 重启辉光与属性组保持、格式错误负例与恢复。

## 8. SDK 2.0 冻结（G3）

冻结点：产品 0.112.88、提交 `5a7747a`，表面快照 `99fd41c`。之后改作者可见表面须先升 SDK 版本，再以 `MAKO_UPDATE_SDK_SURFACE=1` 重写快照（ADR-0258）。

## 9. 观察与已知限制

### 9.1 观察

- **字形边缘**（`72a53ff`）：同色静态填充与材质路径的字形边缘不同（Skia 字形抗锯齿随文字颜色变化，材质路径用白字覆盖），内部像素一致，不属于回归。
- **媒体事件不代表本工程分析完成**：任一分析完成（含作者草稿试样）都会让所有打开的工程各自发布 `project.mediaFeaturesChanged`，
  即使按 `summary.projectSessionId` 过滤（前端已如此）也只能当“刷新一下”的提示。工程预览只等检查，分析未完成时按 pending 出帧；
  导出等待完整分析制品。导出探针首轮未过滤，已改为只认本组工程；作者链探针对声明 `media.features` 的包先导出一次再固定预览输入
  （`1f93787`，否则 AdaptiveStroke 保存前帧无描边、重开后命中缓存有描边）。属文档行为，不是缺陷。
- **全量首轮偶发**：Runtime.Hosting 真实 Worker 会话（M13 第 3 步全量）与 Rendering EmptyRuby 读回（本轮），单独重跑通过。

### 9.2 探针维护

- `95a9545`：m09-font-author 仍写 1.49.x 的模板属性名（`fontFamilyName`），1.50.0 起为 `typography.fontFamilyName`，建模板 400；只改探针后重跑通过。
- `4d92c2f`：`smoke:lyrics` 的工程 TTML 写死歌词 schema 17（11-01 起已是 18，P3 第 2 步升 19），后端以 `UnsupportedSchemaVersion` 拒绝；
  改为读后端 `LyricsDocument.CurrentSchemaVersion` 后 44／44 通过。
- `1f93787`：作者链探针媒体就绪（§9.1）。
- `34d3a02`：桌面样式库冒烟跟随两态辉光（`未唱辉光开关`、`style.unsung.glow`、属性路径）与模板库 v9（目录与 schema 读后端常量）。
- 冒烟 URL：Vite 绑 `localhost`（解析到 ::1），默认连 127.0.0.1 的 glow、author-package、author-replacement、author-metadata
  需传 `--url http://localhost:5173`，首轮 `ERR_CONNECTION_REFUSED` 属环境，重跑通过。

### 9.3 已知限制（决定 3：不阻塞发布候选）

见检查清单 §7：扫字线高亮不叠在包材质部件上；从右向左排版元素区间错一个字形；已唱描边无工坊材质槽；源材质只作用于有 token 的行；
M07 ClusterExit 热查询准备 p95 与 G1 混合组 Render p95 观察项；t01 AdaptiveStroke 分析时序、Hosting 全量首轮偶发；
带 Mapping（Warp）且无材质的导出对象工作框不收紧；旧单态 HTTP 验收探针只对照 1.49.x 及更早。
原列的“切到 1440p 偶发新流停在暂停”已由 `393581a` 修复（§3 缺陷 3）。

## 10. 批准例外

- G-CAP-1 作者诊断通道（决定 2）。
- T01 复杂背景（纹理、镜头切换）与视频；缩放／倾斜／Warp 容差随 P4 移交（决定 1）。
- 性能：不另设门槛，P4 余项（分层缓存、演唱揭示、批次、缩放容差）为批准例外（检查清单 A5）。

## 11. 结论

检查清单 §1～§6 各项均有证据或批准例外：D8 的 C01–C10、S01–S09、附加四例与 T01 的 E/A/X/D/T 齐，P 按决定以 M12 收口与
P3 第 8 步为证据；格式与版本、CPU 拒绝、合同 1.51.1 与前端同步、全量后端 8507 条、11 个 HTTP 探针、M08 跨进程、
前端单元与集成、16 次浏览器冒烟在冻结点通过，2 项桌面冒烟在 `393581a` 修复后通过；SDK 2.0 表面已冻结。
期间发现并修复 3 项缺陷（§3）；3 个过期探针与冒烟脚本已跟随现行协议，作者链探针补媒体就绪等待（§9.2）。M07 有 12 项性能超过 2026-11-04 门槛，同环境 A/B 判为测量环境因素（§7.3）。

**请用户验收 M13。** 通过后进入 M14（删除 v1 与最终验收）。


## 12. 2026-11-14 阻断补充

上文保留 0.112.88／合同 1.51.1 冻结点的历史结果。随后 Aorist 真实工程暴露单帧表面数超限、预览退出及导出主因丢失，
现已在 0.112.89／合同 1.52.0 修复并完成本次范围内验收，见 [Aorist 修复证据](script-sdk-m13-aorist-fix-evidence.md)。
新记录包含整曲预览与导出、浏览器 PNG、真实故障隔离、完整构建／回归、资源对照及原工程保护。

随后长时间编辑并重开工程出现“固定运行输入无法准备”，根因为删除目标后保留的启用预设被整份运行校验拒绝。
已在 0.112.90／合同 1.52.1 修复失效预设运行隔离，并修复实际验收发现的布局参考行样式解析缺陷，
见 [失效预设与布局参考修复方案及证据](script-sdk-m13-orphan-preset-fix.md)。匹配发布的真实工程测量、
浏览器 PNG、桌面共享纹理及正常时间轴拖动、50 秒 1500 帧带音频导出、保存重开全部通过；
本轮不重复声称整曲、长时间交互或跨平台验收。
M13 仍待用户验收；M14 尚未开始。
