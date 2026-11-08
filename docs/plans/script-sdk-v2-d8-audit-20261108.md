# SDK v2 D8 逐项核对记录（2026-11-08）

本记录落实[台账](script-sdk-v2-capability-ledger.md)与[主计划 §5.9](script-sdk-v2-render-pipeline-plan.md#59-d8-能力矩阵与九样例验收)
的逐项 E/A/X/D/T 核对。按用户 2026-11-08 指示：本轮**只运行测试、探针与浏览器冒烟，不修改源码、测试或工具**；
性能 P 暂缓，视频分析不作为门槛。核对结论只代表下述源码与发布物；M13 关闭时仍须按当时源码重核。

## 环境

| 项 | 值 |
|---|---|
| 源码 | `dev/script-sdk-render-upgrade` HEAD `66ec952` |
| 发布物 | `.local/backend-dist/20261108T130840059Z`，由 `739311e` 构建，产品 0.112.42；与 HEAD 差异仅 `m10_media_probe.py` 等待逻辑和进度文档 |
| GPU | AMD Radeon 880M（RADV STRIX1），Vulkan 1.4.354 |
| 前端 | vite 本地开发服务器（loopback 5173），隔离 profile `frontend/.dev/d8-audit-profile-*` |
| 证据目录 | `backend/artifacts/d8-audit-20261108/`（`tests/`、`probes/`、`ui/`；被 Git 忽略，只保存在本机） |

全部探针与冒烟结束后按自有 PID 排水（启动器、host.py、Server/Worker、vite），凭证扫描通过，未改动兄弟仓库。

## 运行汇总

### 后端测试（`dotnet test` 逐项目，构建 exit 0）

| 项目 | 通过/失败/跳过 | 说明 |
|---|---|---|
| Application | 714/0/0 | |
| Backend.Hosting | 1644/0/0 | |
| Backend.Rendering.Skia | 9/0/0 | |
| Backend.Rendering | 282/0/0 | |
| Backend.Server | 514/3/0 | 3 例 `RubyTimelineTransportTests` 缺发布物（环境）；指定 `MAKO_RUBY_HTTP_SERVER_PATH`／`MAKO_RUBY_HTTP_WORKER_PATH` 到上述发布物重跑 3/0/0 |
| Backend.Worker | 140/0/0 | |
| Core | 1562/0/0 | |
| Export | 188/0/0 | |
| Media | 434/0/0 | |
| Processes | 52/0/0 | |
| Rendering | 1717/0/0 | 默认无 GPU，Vulkan 门控分支提前返回 |
| Runtime.Hosting | 153/0/0 | |
| Scripting | 700/0/0 | |
| Timeline | 84/0/0 | |
| Rendering（Vulkan） | 245/0/0 | `MAKO_GPU_SMOKE=1 MAKO_RENDER_BACKEND=vulkan`，过滤 `.V2\|M09ResourceRuntime\|Warp\|BaseMaterial\|GroupImageEffect\|ExitReplica` |

前端：`pnpm typecheck` exit 0；`pnpm test:unit` 53 文件 4160/4160。

### HTTP 探针（真实 Server/Worker，Vulkan）

| 探针 | 结果 | 覆盖 |
|---|---|---|
| m07-author | 通过，766 断言，13 s | WordEmphasis：建草稿、非法 X 保存与编译失败、正确 B 编译、显式采用、旧 CAS 拒绝、历史 A 编译、CPU 拒绝、GPU 预览与旧输入钉住 |
| m08-author | 通过，2483 断言，35 s | CursorTrail／VectorTrail／EventBurst 建草稿→配置→编译→采用→GPU 帧→新输入→检查→删除；晚期预算失败归属 |
| m09-author | 通过，2523 断言，39 s | svg-vector／svg-atlas／warp-base 完整包编辑、坏 SVG 编译、修复、保存重开、导出 |
| m09-graph-author | 通过，9538 断言，144 s | direct-line、typed-dynamic、base-uniform、group-owned、group-nested、group-direct 六场景，作者→保存重开→导出 |
| m09-font-author | 通过，1409 断言，28 s | 固定字体资源采用、重开、导出 |
| author-metadata | 通过，4282 断言，16 s | 声明元数据与原子采用 |
| m10-media-white | 通过，12 s | AdaptiveStroke 白底媒体轨 Pending→Available |
| m11-author-validation | 通过，94 s | 13 包各 360 不透明＋360 透明帧验证（共 9360 帧）；GlassInk 透明输出带 `TransparentBackgroundDependency` 诊断 |
| m11-complex | 通过，25 s | 复杂样式三时刻 Pending/Available 帧、Ruby 检查投影、AdaptiveStroke 作者试样 opaque／straight |

### 真实 Chrome 作者 UI 冒烟（不修改脚本）

| 冒烟 | 结果 | 结论 |
|---|---|---|
| author-v2 WordEmphasis | 失败（5 项通过后） | **脚本过时**：等待旧 `GET /drafts/{id}/sources`；UI 自 `5f13c84` 起读 `/package-files`。失败前已验证导入 package3/SDK2、参数控件、CPU 预览与验证具名拒绝、GPU 初始帧 |
| author-v2 RepeatEmphasis | 失败（3 项通过后） | 同上 |
| author-v2 ClusterExit | 失败（3 项通过后） | 同上 |
| author-package（svg-vector 固定包） | 失败（72 项通过、3 帧后） | **脚本过时**：点击“采用编译通过的修订”后等待直接 `adopt-source-revision`；UI 自 `1e83e0d` 起先弹出“采用修订并配置”对话框走原子采用。失败前已验证包编辑、坏 SVG 编译失败、修复编译与显示帧 |
| author-metadata | 通过，140 检查／7 帧 | 精确声明、完整配置原子采用 |
| author-replacement | 通过，8 检查 | 作者草稿替换包内素材 |
| inspection | 通过，18 检查 | 调试叠加层与成本视图 |

以上 4 项失败均为冒烟脚本未跟随已交付的 UI 流程，未发现产品回归；对应 HTTP 链（m07、m09-author、author-metadata）本轮通过。
修复冒烟属于代码改动，本轮未做，列为证据缺口 G-UI。

## 能力逐项（C01–C10）

计数为本轮 TRX 通过数；“vk”指 Vulkan 重跑中真正执行 GPU 分支的同名类通过数。

| ID | E 表达 | A 作者 | X 执行 | D 诊断 | T 确定性 | 缺口 |
|---|---|---|---|---|---|---|
| C01 仿射 | `TextOutput.AppendTransform`；能力 token／line／group／mark／cluster.affine；`V2SdkContractTests` 8 | m07 WordEmphasis 编辑链；m11 ClusterExit 验证 | `V2TextPartitionTests` 13（vk 13）、`V2ExitPackageTests` 7（vk 7）、`ExitReplicaGeometryTests` 4（vk 4）、`TimedRubyComplexClusterTests` 4；粒子父组／两实例变换只应用一次（`V2ParticlePackageTests` vk 12）；m11-complex Ruby 投影 | 不可拆连字、非法浮点、不可逆、越界能力拒绝（`V2SdkContractTests`、`V2TextPartitionTests`） | `V2PurePackageTests` 4（vk 4）重复乱序重建；`V2ExitPackageTests` 半开边界乱序重建 | **mark.affine 无渲染测试**（Mark 仅有 Warp／图／材质测试） |
| C02 样式与透明度 | SetFill／SetStroke／SetShadow／SetGlow／MultiplyOpacity | AdaptiveStroke 作者试样（m11-complex） | `BaseMaterial*` 共 34（Composition 8、Ownership 7、PixelOracle 5、Runtime 14，均 vk）；`V2TextPartitionTests` Glow 全域部件顺序；`V2MediaSamplePackageTests` 3（vk 3） | 缺失原生资源归属（`V2FailureAttributionTests` 2，vk 2） | BaseMaterial 冷热／重开 | **SetShadow 仅输出合同测试，无像素证据**；group.opacity 仅纯度分析（`SceneCatalogSdkPurityTests` 4），无 v2 渲染 |
| C03 材质 uniform／图 | BindGraph、SetMaterialUniform；`V2GraphOutputContractTests` 8、`V2GroupGraphOutputContractTests` 6、`V2MaterialUniformOutputContractTests` 8 | m09-graph-author 六场景作者→保存重开→导出；author-metadata UI | `V2MaterialUniformPackageTests` 15、`V2GraphControllerPackageTests` 12、`V2GroupGraphPackageTests` 20、`V2HostDirectGraphPackageTests` 4、`GroupImageEffectHostTests` 16（均 vk）；`V2GroupCaptureSelectionTests` 4 | `GroupImagePreflightBudgetTests` 5、`GroupImageEffectWriterTests` 3、坏参数／uniform／变换绑定 | `MaterialAuthorGraphCacheTests` 30；随机远端时刻重复与重开 | 无（本项证据最完整） |
| C04 事件与历史 | `TimelineIndex`、typed selector、occurrence ordinal | m11 RepeatEmphasis 验证 | `PureSourceIndexesTests` 11；`V2PurePackageTests`（RepeatEmphasis） | `V2SourceIndexBudgetTests` 3 | 边界前后 1 µs 与乱序查询半开一致、重复装载不增 occurrence（`V2SdkContractTests`）；`V2ReloadIdentityTests` 2（vk 2） | RepeatEmphasis 作者 UI 链未完成（G-UI） |
| C05 连续发射与 Burst | `ParticleSchedule`、RateTrack、BurstEventRow；`V2ParticleSdkContractTests` 22、`EffectPackageV2ParticleAdmissionTests` 17 | m08 三包完整作者链与预算失败 | `V2ExactParticleScheduleTests` 18、`V2ParticleBoundaryTests` 19、`V2ParticlePackageTests` 12、`V2ParticleRuntimeTests` 5、`V2ParticleFastPathTests` 10（均 vk）；`V2ParticleExportTests` 7（MP4＋Alpha） | 寿命 >5 s 拒绝（`ParticleLifetimeExceeded`、`maxLifetimeUs=5_000_001`）；晚期预算中止导出不发布半份 | 近端／3 min／10 min 乱序重复重开；长 seek 逆序冷重建 | 无 |
| C06 解析运动、桥接、拖尾 | BridgePolicy、最终锚点查询 | m08 | `V2FinalAnchorTests` 4（vk 4）、`V2ParticleKinematicsTests` 6；Bridge 用真实 End−1 与未来 Start、Jump 无飞行头 | 跨身份／断点不插值 | memo 冷热及超出容量重算精确、取消不污染 | 无 |
| C07 Overlay／Sprite／SVG | VisualOutput Dots／Segments／Trails／Sprites／Paths／AtlasSprites；`SvgSdkContractTests` 11、`SvgSdkPurityTests` 6 | m08 VectorTrail；m09-author svg-vector／svg-atlas；author-replacement UI | `SvgOverlayRenderingTests` 19、`SvgRuntimePackageIntegrationTests` 18、`EffectPackageSvgResourcesTests` 65；SpriteTrail 星芒（`V2ParticlePackageTests`） | `PackageSvgValidatorTests` 96（含外链 href 拒绝）、`SvgCompilationValidationTests` 35、`V2MixedOverlayBudgetTests` 2（vk 2） | `V2MixedReplayTests` 4（vk 4） | 无 |
| C08 音频、随机与媒体特征 | `DeclareContext.Audio`（AudioFeatures）、`Random`（DeterministicRandom(seed, instanceId)）、`frame.Media.Sample` | 媒体：m11-complex 作者试样；`BackendAuthorV2WorkflowTests` 媒体依赖试样 | 媒体：`MediaFeature*` 共 155、`ProjectMediaFeatureProjectionTests` 56、`V2MediaSamplePackageTests` 3（vk 3）、m10-media-white | 媒体：Pending／Unavailable 只撤回描边、透明输出照常（`V2TargetComplexStyleTests`） | 媒体分析制品复用同一修订 | **v2 音频只有 `NotAnalyzed` 输入，无 Available 音频的 v2 测试或样例；`Random` 无直接 v2 测试，也无样例使用** |
| C09 字段状态的实际用途 | RepeatEmphasis v1 的 `counts`／`ordinal` 字典改为 occurrence ordinal；时变发射改为 RateTrack；`PreparedEffect` 只读 | 同 C04／C05 | 同 C04／C05 | `ScriptV2PurityAnalyzerTests` 64、`ScriptV2PurityMultiTreeTests` 7 拒绝可变状态；帧不公开旧状态句柄 | 同 C04 | v1 样例中跨帧累积字段仅 RepeatEmphasis（已映射）；其余样例字段为初始化参数／描述符，对应 Declare 的只读准备结果；样例范围内无未映射用途 |
| C10 Warp | SetWarp，token／cluster／mark.warp；`GridWarpTests` 15、`GridWarpMappingTests` 5 | m09-author warp-base；m11 WaveWarp 验证 | `WarpPixelRenderingTests` 23、`V2WarpPackageTests` 11（均 vk）；字族＋字簇网格后最终 cursor 与粒子锚点只应用一次 | 退化／翻面／坍缩拒绝；帧预算超限归实际跨线实例；被覆盖域错不发布 | 任意时刻重复乱序重开保持几何与像素 | 无 |

### v1→v2 能力差异（需用户决定）

| 编号 | v1 能力 | v2 现状 | 影响 |
|---|---|---|---|
| G-CAP-1 | `FrameContext.Diagnostics.Report(severity, code, message, location)`，作者自定义 Info／Warning／Error 进入渲染／导出报告（`backend/docs/scripting-sdk-contract.md`） | v2 无对应通道；作者只能抛异常（`EffectOutputException` 等宿主诊断） | 作者无法上报非致命警告 |
| G-CAP-2 | `ProjectContext.DurationUs`（输出总时长） | `DeclareContext` 无工程时长 | 依赖全曲进度的效果无法表达 |

v1 向作者暴露 `CancellationToken`，v2 改为宿主在查询与输出处检查取消，属设计取舍，不计缺口。

## 样例逐项（S01–S09 及附加四例）

全部 13 包均有 m11 验证（360＋360 帧，真实 HTTP、Vulkan）。“A”列只记完整作者链证据。

| ID | E／X | A | D／T | 导出 | 缺口 |
|---|---|---|---|---|---|
| S01 WordEmphasis | `V2PurePackageTests`、`V2MixedReplayTests` | m07 HTTP 链；UI 冒烟到初始帧（G-UI） | 取消恢复、乱序重建 | 无 | 导出、保存重开、UI 链 |
| S02 RepeatEmphasis | `V2PurePackageTests`；occurrence ordinal | 仅 m11 验证；UI 冒烟到初始帧 | 重复装载去重 | 无 | 作者链、导出 |
| S03 CursorTrail | `V2ParticlePackageTests`、`V2ParticleBoundaryTests` 等 | m08 | 预算、取消、memo | `V2ParticleExportTests`（MP4＋Alpha） | 保存重开 |
| S04 VectorTrail | Custom 出生窗／拖尾 | m08 | 晚期绘制预算归属 | 同上 | 保存重开 |
| S05 SpriteTrail | 包内图片星芒、拖尾淡出 | 仅 m11 验证；素材替换 UI（author-replacement） | — | 无 | 作者链、导出、预算负例 |
| S06 EventBurst | `V2ParticleBoundaryTests`、`V2ParticleSampleContractTests` | m08 | 同边界顺序、配额 | `V2ParticleExportTests` | 保存重开 |
| S07 CustomInk | `V2ResourceSamplePackageTests`：图可原生编译 | 仅 m11 验证 | — | 无 | 样例级像素、作者链、导出（同类固定包 base-uniform 有完整链） |
| S08 LineGlitch | 整行分片真实位移、强度 0 与无效果一致 | 仅 m11 验证 | — | 无 | 作者链、导出（同类 direct-line 有完整链） |
| S09 GlassInk | 图可原生编译；透明输出 `TransparentBackgroundDependency` | 仅 m11 验证 | 透明回退诊断 | 无 | 样例级像素、作者链、导出 |
| ClusterExit | `V2ExitPackageTests` 7、`V2ExitBoundaryOwnershipTests` 2、`V2ReloadIdentityTests` | 仅 m11 验证；UI 冒烟到初始帧 | 3 s 上限、取消恢复、半开边界 | 无 | 作者链、导出 |
| AdaptiveStroke | `V2MediaSamplePackageTests` 3 | m11-complex 作者试样 opaque／straight；m10-media-white | 媒体不可用只撤回描边 | 无 | 导出（视频分析相关按用户指示暂缓） |
| StarSweep | `V2TargetComplexStyleTests`（星芒读取 Warp 后最终播放头） | 仅 m11 验证 | 乱序重复重开 | 无 | 作者链、导出 |
| WaveWarp | `V2TargetComplexStyleTests`、Warp 系列 | 仅 m11 验证 | 同上 | 无 | 作者链、导出 |

## T01 目标复杂样式

组合测试 `V2TargetComplexStyleTests` 3（vk 3）与 m11-complex 探针覆盖退场、Ruby、Warp 锚点、粒子、SVG 星星和媒体描边。

| 步骤 | 状态 | 证据／缺口 |
|---|---|---|
| 1 固定测试工程 | 部分 | 组合夹具含 Ruby、退场、Warp、粒子、SVG、媒体；**同句双实例、连字、缺时间歌词、场景父子矩阵未进组合**（各自另有单项测试） |
| 2 作者修改→编译→采用→撤销→保存重开 | 部分 | 组合整体无作者链；具名拒绝分项齐全：不可拆字簇、退场 >3 s、粒子 >5 s、SVG 外链／超预算、退化 Warp |
| 3 边界 ±1 µs 与重叠取帧 | 部分 | 分项有；组合只取 0.5／1.3／2.0 s 三时刻 |
| 4 顺序／乱序／重复／memo／重开／撤销 | 部分 | 组合有乱序重复重开；memo 冷热淘汰分项（粒子）；组合无撤销 |
| 5 背景与媒体修订 | 部分 | 纯色与图片背景、Pending→Available、Unavailable；**无纹理／镜头切换／循环背景、分析失败／取消组合**；视频按用户指示暂缓 |
| 6 普通／透明预览与导出 | 部分 | 预览两种输出均有；**组合无导出**；缩放／倾斜／Warp 容差未测量；本轮目检三时刻浅色纯色背景帧可读，复杂背景可读性未核查 |
| 7 性能 | 暂缓 | 按用户指示不作本轮门槛 |

## 缺口汇总

**需用户决定的能力缺口**：G-CAP-1 作者诊断通道、G-CAP-2 工程时长（见上）。

**证据缺口**（补齐需新增或更新测试／探针／冒烟，本轮未做）：

1. G-UI：`author-v2-smoke.mjs` 未跟随 package-files 读取，`author-package-smoke.mjs` 未跟随原子采用对话框。
2. C08：v2 Available 音频输入与 `Random` 的直接测试；无样例使用两者。
3. C01／C02：mark.affine 渲染、SetShadow 像素、group.opacity v2 渲染。
4. 样例作者链：S02、S05、S07、S08、S09、ClusterExit、StarSweep、WaveWarp 只有 m11 验证；S01 缺保存重开。
5. 样例导出：除 S03／S04／S06（单元级 MP4＋Alpha）及 m09 固定包外均无导出证据。
6. S07、S09 样例级像素断言。
7. T01：组合导出、组合作者链与撤销、组合 ±1 µs、组合夹具补全、复杂背景、容差测量与可读性目检。

完整 D8 不因本轮关闭；P 暂缓，其余字段待上述缺口按用户决定处理后再核。

## 后续处理（同日，产品 0.112.43）

按用户决定：

- G-CAP-2 已补：`DeclareContext.ProjectDurationUs`（`long?`，整数微秒），`TextFrame`／`VisualFrame` 提供同名快捷属性。
  - 值来自工程成片时长固定输入；工程未设置成片时长时为 `null`，宿主不按最后一行歌词或媒体时长代填。
  - 修改成片时长会重新准备，旧准备结果读不到新值。
  - 测试：`V2ProjectDurationTests`（宿主把时长交给 `Declare` 与逐帧读取，覆盖 2.5 s、7 s、未设置三种，并含 seek 回 0 再前进）、
    `V2SdkContractTests` 新增一项（只接受正值或未知）。去掉宿主传参的变异会让前者失败。
  - 提交 `f8429d4`。
- G-CAP-1 暂不补：v2 纯函数逐帧求值，作者警告无法去重，会挤满导出／验证 512 条诊断预算。作者目前仍只能抛异常。
- G-UI 已修（提交 `18a2231`）：
  - `author-v2-smoke.mjs` 改为先切到 C# 入口，保存走完整包 PUT 并核对规范 GET，采用经「采用修订并配置」对话框确认；
  - `author-package-smoke.mjs` 的显式采用同样经该对话框确认；
  - fresh publish 0.112.43 实跑：WordEmphasis 35 项、RepeatEmphasis 18 项、ClusterExit 18 项、author-package 83 项，全部通过。
- 其余证据缺口 2～7 按用户指示暂不补，下一步进入 M12。
