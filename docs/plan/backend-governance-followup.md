# 仓库后续治理与架构演进计划（P2–P3）

> **状态**：规划定稿（暂缓实施，待按需排期启动）  
> **前置基线**：`main` `1873785`（第一阶段 P0+P1 治理已完成并合入，共 9 个提交，净删约 25.9 万行，契约版本 `1.60.0` / 产品版本 `0.116.1`）  
> **定位**：系统承接全仓审计中尚未在 P0+P1 阶段处理的 **P2（测试体系、代码规范与工具链治理）** 与 **P3（核心架构重构与协议现代化）** 事项，作为后续分阶段重构的权威任务清单与技术约束。

---

## 零、已完成基线与已锁定架构决策

### 0.1 第一阶段（P0 + P1）已闭环内容（`b6b9272..1873785`）
后续迭代**无需再重复处理**以下已完成项：
- **P0 高危止血**：`WorkerProcessProxy` 锁原语修复；`WorkerBackendValidationExecutor` 异步探测与共享缓存；Author HTTP 端点全链路异步化；`ScriptArtifactStore` 异步锁与 30s 超时；`SkiaCountdownShapeRenderer` 引用计数租约、`SvgNativePictureCompiler.Inline` `SKColorSpace` 泄漏与安全开关；`LayoutKey` / `TextLayoutOptions` 值语义与 2,048 FIFO 淘汰；`SkiaRenderPipeline.DisposeCore` 所有权与隔离释放；`ShmPreviewPort` 锁保护与透传 `InnerException`；`LinuxPosixShmBacking` 终结器；`FfmpegMediaEncoder.CompleteAsync` 取消响应与根路径/清理边界；`MediaPresentationDeploymentDiscovery` 取消杀 `ldd` 与有界读；`ProjectDtoMapper` 静默空壳与 `null!` 注入修复、`ProjectValidator` 补齐 `Ruby` 与 `StyleOverride` 校验；Linux `shm_bridge.cc` 路径白名单与 `openat`/`unlinkat`；`mkg-envelope-bridge.c` 栈越界保护；`mkg_presentation.c` Linux FFmpeg micro 补丁兼容与 `MKG_MEDIA_METADATA_FD`；统一 `WorkerBinaryLocator` 消除 23 处 `bin/Debug` 硬编码；迁移根 `.github/workflows/ci.yml`、接入 7 个顶层 JS 测试与 `.slnx` 纳入 `Library.Worker`/`Media.Worker`。
- **P1 瘦身与合同收敛**：整删 `Timeline`（+测试）、`SoundFlow` 依赖 + 10 个死音频播放文件、21 个死 Schema 迁移文件；清退 `render`/`render-frame-v1`、`ProjectImage*` 三条平行链路、旧直连 `IGpuRenderFrameSession`、`RequiresAudio` 恒假管道、空转 `ReplayReason` 与 `FrameEffectHost` SDK v1 死分支；收敛服务端启动期短命 Worker 探测；删除 12 条未消费公开 HTTP 路由（5 无版本别名 + 5 旧主机 `recovery-jobs` + `edit-previews` + `resources/package-asset`）；清理 32 处多余 `NonPublic` 反射、幽灵 `SkiaGpuTargetProbe`、`Text.csproj` 无用 `Core` 引用、4 处生产诊断 `"喵。"` 与坏死的 `smoke.py`/`g1_smoke.py`；`probes/skia-gpu-target/` 迁移至 `native/skia/`；合并合同双副本至单一真值源 `backend/web/`（删除 `frontend/docs/backend/`）、砍掉 `backend-api-contract` 历史回滚门禁与 28k 行旧语料并冻结至 `1.60.0`。

### 0.2 已锁定的中长期架构决策
| 决策项 | 已锁定方向 | 核心约束与理由 |
| :--- | :--- | :--- |
| **1. `Application` vs `Backend.Hosting` 分层** | **瘦身 `Application` 为纯无窗口领域用例与历史栈（去 Avalonia 化）+ 将 `Backend.Hosting` 拆分为独立子域应用服务** | 不强行把紧密依赖 HTTP 会话、客户端租约、幂等回执与 Worker 进程的编排逻辑回填进 `Application` 制造空壳封装；同时彻底清除 `Application` 中的 Avalonia 遗留（手势合并窗、`FileSystemWatcher` 等），消除 HTTP 层反复调 `BreakMergeGroup()` 与之对抗的内耗，保留纯领域层极速单测优势。 |
| **2. API 契约真值源与门禁** | **`backend/web/` 为仓库唯一真值源；`validate.py` 仅校验当前契约版本** | 严禁再引入跨目录契约镜像副本或向旧历史版本倒推还原的 SHA-256 门禁；所有契约变更直接经 `freeze.py` + `validate.py` + `frontend/test/unit/contract.test.ts` 与 CI `contract` job 闭环。 |
| **3. 原生解码隔离红线** | **重型 Native 媒体/图片/字体解码原则上限定在隔离 Worker** | 后续 P3 须把目前 Host 进程内经 `Runtime.Hosting.Authoring` 触发的 Skia 图片与 HarfBuzz 字体解析迁回 Worker，重新建立 `Backend.Hosting` 与 `Rendering.Skia` 的硬边界。 |

---

## 一、P2 阶段：测试体系重构、代码规范恢复与工具链清理

> **阶段目标**：大幅提升 CI 速度与测试可信度，恢复 `.editorconfig` / `CS1591` 质量门禁，清理测试与仓库中剩余的高噪声遗留资产。**不改变核心运行时架构与外部 HTTP 契约。**

### 1.1 测试并行化与测试基础设施去重（高优先级）
1. **拆分巨型 `partial class` 恢复 xUnit 并行度**：
   - `MakoKaraGenerator.Backend.Server.Tests` 中 55 个文件（占 89% 测试用例）全部塞在同一个 `partial class PreviewTransportTests`，导致 xUnit 强制串行执行且单次全跑超 90s。将其按业务域（会话、歌词、样式、编排、导出、验证、GPU 流、模板库、作者工作流等）拆分为独立测试类，复用共享 `IClassFixture` / 工厂基类恢复并行执行。
   - 同步评估拆分 `Rendering.Tests` 中 `MainCanvasImageEffectsTests`（17 文件）、`MixedEffectPackageTests`（8 文件）、`PerfFixturePackageTests`（8 文件）与 `Export.Tests` 中 `MixedPackageExportTests`（8 文件）。
2. **提取共享测试支持库与统一夹具**：
   - 将 `backend/tests/Shared/`（16 文件，目前靠 `<Compile Include>` 硬编进十几个项目）重构为正式类库 `MakoKaraGenerator.TestSupport`。
   - 合并 `Backend.Hosting.Tests` 中 42 个文件各自复制粘贴的私有 `class Fixture`，以及跨项目重复的 `Probe`（44 处）、`Clock`/`FakeClock`（19 处）、`CountingDevice` 等通用测试桩。

### 1.2 测试可信度、稳定性与残留私有反射根治（高优先级）
1. **消除 32 处 GPU 测试静默假绿**：
   - `Rendering.Tests` 跨 18 个文件存在 32 处 `if (gpu && env != "1") return;` 静默返回，导致默认测试报告将未执行的 GPU 用例计为 `PASSED`。改为显式 `[Trait("Category", "Gpu")]` 分离过滤或条件 Skip 特性，杜绝假绿。
2. **消除 4 处硬编码 Linux 系统字体 SHA-256**：
   - `Backend.Rendering.Tests`（`PackageRuntimeFixture.cs`、`PresetSnapshotRuntimeEquivalenceTests.cs`、`StaticSnapshotRuntimeEquivalenceTests.cs`）与 `Runtime.Hosting.Tests/PackageWorkerRuntimeFixture.cs` 硬编码了本机 `Noto Sans CJK SC` 精确 SHA-256，系统升级字体包即全败。改为随测试打包小型受控开源字体文件或改为校验字形覆盖/解析能力。
3. **排查并根治 2 项既有测试稳定性缺陷（P0+P1 验收实证暴露）**：
   - `Rendering.Tests`: `BaseMaterialPixelOracleTests.真正Warp的raw覆盖与独立整数平移参考一致(gpu: False)` 在基线 `b6b9272` 即稳定失败（`raw geometry max=170, x=353, y=198`），需定位 Skia 采样/抗锯齿容差或参考实现差异并修复。
   - `Media.Tests`: 无过滤直跑 `Media.Tests` 偶发在 60–90s 超时挂起（带 `--blame-hang-timeout` 或分片运行 27s 全绿），重点排查 `VideoProcessDeadlineTests` 等并发子进程收尸与管道关闭时序；同步消除全测试套件中剩余的 4 处 `Thread.Sleep` 与高延迟 `Task.Delay`。
4. **清除剩余脆弱私有反射（`BindingFlags.NonPublic`）**：
   - 优先彻底消灭 **5 处直接篡改编译器生成字段 `<Prop>k__BackingField`** 的极脆测试（`BackendCompressedAnalysisAdoptionTests.cs`、`Core.Tests/Media/MediaFeatureInputTests.cs` 等）。
   - 为 `PreviewBackendService`、`ProjectSessionRegistry`、`FrameEffectHost` 提供受控 `internal` 测试构造器/测试接缝，消解 `GpuStreamFailureTests.cs`（28 处）、`ProjectMediaHostClosureAnchorTests.cs`（12 处）等白盒反射重灾区。

### 1.3 测试项目依赖归位与历史冗余清理（中优先级）
1. **纠正 3 处测试依赖越界**：
   - 将 `Runtime.Hosting.Tests` 中反向依赖上层 `Backend.Hosting` 的 15 个 Worker/Hosting 集成测试迁回 `Backend.Hosting.Tests` 或 `Backend.Worker.Tests`，移除 `Runtime.Hosting.Tests → Backend.Hosting` 反向引用。
   - 将 `Application.Tests/BasicLayoutTests.cs` 中 3 处像素回归迁至 `Rendering.Tests`，移除 `Application.Tests → Rendering.Skia` 越界引用（让纯应用层测试不再携带 Skia/HarfBuzz 原生库）。
   - 移除 `Export.Tests` 与 `Rendering.Tests` 中仅测阶段 0 原型的 `RuntimeShaderExportTests.cs` / `RuntimeShaderCapabilityTests.cs`，随之将无生产引用的 `backend/samples/TechnicalSpike/` 移出 `MakoKaraGenerator.slnx` 并归档/删除。
2. **合并碎片项目与整理历史切片测试**：
   - 将仅 1 文件 4 个测试的 `Backend.Rendering.Skia.Tests` 合并至 `Backend.Rendering.Tests`（或随 P3 撤并 `Backend.Rendering.Skia` 同步处理）。
   - 清理含合并残留注释且与 `MainCanvasGroupCompositionTests` 重叠的 `GroupCompositionG0Tests.cs`；修复 `UserInterfacePurityTests.cs` 重复执行两遍全仓扫描的问题；按领域逐步重命名/归并 66 个带历史阶段编号（`Phase*`/`M09*`/`G20*`/`G31*`/`V2D8*`）的测试文件。

### 1.4 恢复代码风格门禁、XML 注释与结构化日志（中优先级）
1. **分模块解除“极限压行”并恢复 `EnforceCodeStyleInBuild`**：
   - 当前 `backend/src/` 因 `EnforceCodeStyleInBuild=false` 累积了 **11,704 行超 120 字符**、**4,814 处无大括号单行控制流**与 **5,627 行单行多语句压缩写法**（最高单行 743 字符 / 11 条语句）。
   - 按项目分批次执行 `dotnet format` 与人工清理多语句压缩行（先格式化纯风格独立 commit，避免污染业务 diff），最终在 `backend/Directory.Build.props` 重新启用 `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`。
2. **清理 `<NoWarn>CS1591</NoWarn>` 妥协**：
   - 将可执行项目 `Backend.Worker` 中无需对外暴露的类型改为 `internal`，即可移除其 `CS1591` 抑制；
   - 逐步补齐 `Backend.Contracts` 与 `Backend.Hosting` 公开 API XML 注释并撤掉 `.csproj` 中的 `CS1591` 绕过。
3. **接入 `ILogger` 并治理空 `catch`**：
   - 将 `Backend.Server` / `Backend.Hosting` / `Backend.Worker` 中直写 `Console.Error.WriteLine` 的关键错误与停机诊断迁移至 ASP.NET Core `ILogger`；清理全后端剩余的裸空 `catch {}`（如 `PreviewBackendService.cs:544,548`、`GpuStream.cs`、`LocalStreamPreviewChannel.cs`、`Backend.Worker/Program.cs:196,206,215`），至少保留异常类型与上下文诊断。

### 1.5 工具链、一次性脚本、零引用旧类与文档卫生（中-低优先级）
1. **清理一次性阶段验收脚本与原生构建杂质**：
   - 从 `scripts/gpu-preview-p3.mjs`（764 行）抽离被 `desktop-smoke.mjs` / `desktop-library-smoke.mjs` 复用的公共辅助模块，随后清理或归档 12 个未接入 `package.json` 的一次性 M3/P3/G1/G4 验收脚本（~220 KB）。
   - 将 `native/gpu-preview/draw-plugin.h` 与 `producer.cc` 从默认生产构建 `native/gpu-preview/build.sh` 中分离（仅在本地测试脚本显式编译），避免每次桌面构建附带编译测试桩；修复 `native/gpu-preview/build.sh:8` 缺失 `-DNODE_GYP_MODULE_NAME` 与 `bootstrap-headers.sh` 无超时下载。
2. **清理其余经审计确认 0 生产引用的遗留类**：
   - `Core`：`SetProjectEffectsCommand`、`SetPresetApplicationsCommand`、`ProjectCommand.Combine`/`CompositeCommand`、`LyricStyleEffects`、`LyricStylePresetLibrary`/`LyricStylePreset`（含永未赋非空值的 `Project.LyricStylePresetId`）、`LyricsImportReportFormatter`、`LyricsNewLineKindExtensions`、`TokenSplitRuleExtensions`、`LyricStyleEditor` 中 6 个未使用方法、`LinePlacementEditor.TryApply`/`Distribute`、`StyleTemplateApplicator.TryApply`、`LyricStyle.UnsungGradientSpec`/`SungGradientSpec`、`PersistenceDtos` 3 对完全重复 DTO。
   - `Export` / `Processes` / `Media` / `Text`：`EncoderAvailability`、`ExportReportFormatter`、`LinuxOwnedProcessGroupGuard`、`MediaPresentationDecoder.OpenIndexedAsync`/`ExactSequentialSource`、`ShapedLine.XWithin`；并评估 `EnvelopeObserver` + `mkg-envelope-bridge.c:722-936`（~215 行 C）是正式接入生产还是整套移除。
   - 前端协议 4 条仅在 `frontend/src/protocol` 声明但业务零调用命令（`lyrics.reorderRubySegments`、`lyrics.reorderToken`、`orchestration.setLeadInGeneration`、`orchestration.setMarkAppearance`）按后续产品需求决定保留或清退。
3. **修复文档断链与同步过期文档**：
   - 清理 `Directory.Build.props`、`.csproj` 与源码注释中 209 处失效文档路径（`docs/development.md`、`docs/roadmap.md`、`docs/decisions.md` 等）和 94 处悬空 `ADR-*` 引用；更新停留在 `0.113.0 / 1.54.0` 的 `backend/docs/architecture/backend-api.md` 至 `1.60.0`。

---

## 二、P3 阶段：核心架构演进与技术债根治

> **阶段目标**：拆解用 `partial class` 掩盖的超级上帝类，重建清晰的分层与锁粒度边界，消除高耦合手写协议与重复编译开销，降低长期演进的维护成本。

### 2.1 拆解后端服务与托管层上帝类、重塑锁模型（高优先级）
1. **按子域拆分 `ProjectSessionRegistry`（48 文件 / 8,034 行）**：
   - 拆分为独立 DI 注册的子域服务：`ProjectSessionStore`（会话注册与成员生命周期）、`SessionPersistenceService`（打开/保存/恢复/自动保存与文件句柄）、`AuthoringWorkflowService`（草稿、包文件、Roslyn 编译与历史）、`TemplateLibraryService`（模板库与导入导出）、`ScriptConfigurationService`、`ExportJobService`、`ValidationJobService`、`AnalysisJobService`、`AudioPreparationService`、`SceneMeasurementService`、`FontQueryService`。
   - 彻底消除 12 个存在隐式调用顺序的 `Configure*` 后置方法，改为构造函数 DI 显式注入；销毁单类 18 把锁及跨子系统借用模板库锁的反模式；为 `_authorDraftLocks`（目前只增不删永不释放）改为带引用计数的键锁池。
   - 将全文件同步 SHA-256（`VerifyFixedAudioSource`）与递归 `Directory.Delete`（`BackendJobArtifactStore.Prune`）彻底移出 `_createGate`、`session.Writer`、`_jobGate` 等核心互斥锁，并消除创建/导出时对同一音频文件连续算两遍哈希的浪费。
2. **重构 `ProjectSession`（39 文件 / 6,838 行）命令分发**：
   - 合并分裂在 `ProjectSessionRegistry.ExecuteCommandAsync` 与 `ProjectSession.ExecuteCommandAsync` 两处的分发逻辑；用强类型 `IProjectCommandHandler<TCommand>` 注册表替代 18 个局部结果变量 + 巨型 `switch` + 7 层级联 `TryExecute*` 兜底；消除 `ProjectSession.RuntimeInput.cs:60` 锁内 `Thread.Sleep` 删目录重试。
3. **下沉并拆分 `PreviewBackendService`（1,607 行）与重构 `ServerEndpoints` / `Backend.Worker`**：
   - 将 `PreviewBackendService` 核心编排逻辑从 HTTP 宿主 `Backend.Server` 下沉至 `Backend.Hosting`，拆分其全局单把 `_membership` 信号量（只读查询与长耗时 Worker 启动/Vulkan 初始化/20s 媒体准备不再互相阻塞全服）。
   - 将 `ServerEndpoints.cs:16-85` 的巨型匿名 `app.Use` 拆分为标准中间件管道（CORS、鉴权、停机拦截、异常映射）；将 130+ 手工字符串 `Capabilities` 改为从路由/命令注册表自动派生，消除硬编码魔数 `new(2560, 1440, 3, 67108864, 2, false)`。
   - 将 `Backend.Worker/Program.cs`（780 行单 `Main` + 15 嵌套局部函数 + `goto`）拆分为独立消息分发器与任务处理器；**复用单一 stdin/stdout 流对象**（不再每条消息 `Console.OpenStandardInput/Output()`）；移除 `ConcurrentDictionary` 上无意义的 `lock (active)`；将同步前置逻辑移入后台任务避免阻塞 `cancel`/`shutdown` 主循环。
   - 精简 `WorkerProcessProxy`（16 文件 / 2,541 行）：收拢 9 个平行 `Process?` 代次追踪字段为显式状态机对象；用 `process.WaitForExitAsync` 替换 20ms 轮询；修复进程级 `Lazy<ScriptCompiler>` 临时目录泄漏；清除生产代码中对测试 `.py` 假 Worker 的特判；在 `_ownedGroup` 已建立时跳过 `WorkerProcessProxy.cs:955` 无条件遍历 `/proc` 的 `_ownedTree?.ObserveDescendants()`。

### 2.2 落实 `Application` 瘦身与统一歌词编辑链路（高优先级）
1. **完成 `Application` 去 Avalonia 化**：
   - 清理零生产引用的 Avalonia 桌面类（`PresentationEditSession`、`ProjectPackage`、`TemplateLibraryCatalogue`、`TemplateCatalogueMonitor`、`LyricTimingEditGesture`、`ProjectAutoSave.FindRecovery`、`ProjectWorkspace` 5 个旧同步方法）与桌面 900ms 手势合并窗，同时清除 `Backend.Hosting` 中 20+ 处用来抵消它的 `BreakMergeGroup()`；移除 `ProjectSession.BeatCommands.cs` 计算全树 diff 又整份丢弃的浪费。
2. **统一歌词编辑语义与清除 `PrimaryBranchId` 篡改 Hack**：
   - 重写 `LyricTimingEditor` 4 个硬编码主分支的旧方法使其原生接收 `BranchId`，彻底移除 `LyricBranchEditor.Edit` 临时篡改 `PrimaryBranchId` 的 Hack（同步消除重复执行两遍 `PresentationValidator` 的浪费）。
   - 统一 Phase 0–4 新旧命令行为：为 Phase 1 新增的打轴/清零与 `RubySegmentEditUseCase.PrepareSetSegments` 补齐乐观并发 `Baseline` 校验（并消除 5 处循环内 `immutableArray.ToArray()`）；统一 `SetLineText`（`LyricTextResplitter`）、`TtmlImporter` 未定时行与 `ApplyTextDiff`（`LyricTextDiffAligner`）均按 `TokenSplitRule.Mixed` 切分；修复 `LyricTimingEditor.Beat.cs:582` 忽略 `TryReplaceSegments` 返回值、`BuildSplitLine` 丢弃右侧行 `StyleOverride`/`PrimaryLineId` 及 clip 成员同步、`LyricTokenSemantics.IsPunctuationElement` 非 BMP 标点判断，以及 `LyricTextDiffAligner` 在 `TokenTextMismatch`/超大文本降级/`PairGap` 开头插行时的错位问题。
3. **加固 `Core` 持久化、导入与校验**：
   - 消除 `Core/Persistence/` 剩余 4 处 Sync-over-Async（`ProjectStore.cs:250`、`EffectPackageStorage.cs:36`、`StyleTemplateLibrary.cs:102`、`ScriptConfigurationLibrary.cs:103`）与 `ProjectStore.cs:413` 的 `Thread.Sleep(10)`，统一错误异常类型（`SchemaVersion` 不匹配不再抛 `EffectPackageException`），合并重复 `EnsureNoLinks` 实现，消除无意义的 JSON 字符串 → `JsonDocument` → 二次序列化往返。
   - 修复 `AutoSaveSession.cs` 固定 `.tmp` 文件名并发冲突（改 GUID 临时文件 + `try/finally` + `flushToDisk: true`）与 `AutoSaveStore.Publish` 无上限 `while (true)` 重试。
   - 修复 `TtmlImporter.cs:140-171` 内层 `catch (XmlException)` 吞掉深度>64/多根安全异常的问题，拆解 770 行可变侧信道 `Builder` 状态机，解除 `ttml.project` 与 `LyricsDocument.CurrentSchemaVersion` 的强死绑。
   - 修复 `PresentationValidator.cs:83-90` 错误归属错位与重叠报错重复；统一 `ProjectDtoMapper` 枚举解析（`ParseBranchEnum` 排除数字字符串并校验 `Enum.IsDefined`）与诊断路径 `[index]`；修复 `LibraryCommitLease.cs` 不可达 `IsMacOS` 分支并补充 Linux `EACCES=13`。

### 2.3 渲染与脚本引擎拆分、去重与 3× 启动编译优化（中-高优先级）
1. **消除 ~550 行逐字复制与 3× 重复编译**：
   - 抽取 `CompiledImageGraph` 与 `SceneCompositionProgram` 之间约 550 行逐字复制代码（`PayloadLayers`/`PayloadComposition`/`PayloadCapture`/`Drawing`/`Validation`）及重复编译的宿主 SkSL 着色器至共享 `SkiaCompositionSurfaceOps`。
   - 在 `CompiledEffectPackage` → `CompiledPackageGraphExports` → `PackageSvgResources`/`CompiledImageGraph` 间透传已验证/已编译的 SVG 与 SkSL 中间结果，消除会话创建期对同一效果包 SVG 与 Shader 重复验证/编译 **3 遍**的性能放大。
2. **拆分 `SkiaRenderPipeline`（46 文件 / 6,950 行）与 `FrameEffectHost`（21 文件 / 1,876 行）**：
   - 将 `SkiaRenderPipeline` 按阶段拆分为清晰协作对象（背景解析、文本/注音/扫字/高亮绘制器、Overlay/倒计时绘制器、图像图与组合成调度器、预览输出适配器），消除 3 个帧期可变实例字段，给 `SkiaTextLayoutEngine._fontSets` 增加容量上限淘汰；移除热路径中 `.ToArray()` 与 `BackgroundFrameSources.Copy` 逐行调用 `bitmap.GetPixels()` 的开销。
   - 将 `FrameEffectHost` 按阶段拆分（模块加载、V2 求值/粒子/锚点、材质作用域归约、输出校验），简化 `ComposePureOutput` → `FrameOutput` 二次拆装开销。
   - 清理 `Rendering.Abstractions` 中错置的运行时引擎（将 `PerformanceTelemetry` 与 `LyricSampler`/`LyricSceneSampler` 移至独立运行时程序集），统一 `PreparedRenderFrame.Warps.cs:183` 与 `SkiaSceneGeometry.Decoration.cs:66-67` 两套矛盾的阴影外扩公式；统一分散在 4 处的预览尺寸与内存池预算常量。
3. **将测试/基准桩彻底剥离出生产程序集**：
   - 将 `UnmanagedMemoryShmBacking` 迁至测试程序集；将 `SyntheticGpu` 测试棋盘图接口从公共 `Rendering.Abstractions` (`IPreviewRenderSession`) 剥离为内部/独立基准扩展；将 `SkiaRenderPipeline` 中 5 个仅测试赋值的可变开关收敛为受控测试选项；清理零生产调用的 `CompositeSceneImageEffectChain` / `ExecuteSceneImageEffect` 与反射专留方法 `V2AnchorRegion`。

### 2.4 分层边界重塑、协议现代化与 Vulkan/ABI 长期演进（中优先级）
1. **剥离 `Core` 基础设施与恢复 `Backend.Hosting` / `Rendering.Skia` 硬隔离**：
   - 将 `Core/Persistence`（磁盘 I/O 与存储）拆为 `MakoKaraGenerator.Persistence`，将 `Core/Lyrics/{Import,Export,Lrc}`（XML/ASS/VTT/SRT/UltraStar/LRC 格式解析）拆为 `MakoKaraGenerator.Lyrics.Formats`，让 `Core` 回归纯领域模型与验证。
   - 将 `PackageAuthorReplacementInspector` 的 Skia 原生图片解码与 HarfBuzz 字体解析迁回 `Backend.Worker`，使 `Backend.Hosting` 与 `Backend.Server` 不再通过 `Runtime.Hosting` 传递拉入整套 `Rendering.Skia`/Vulkan；收敛 `Rendering.Skia` 公共接口，消除对生产项目 `Runtime.Hosting` 与 `Backend.Worker` 的 `InternalsVisibleTo` 穿透；将 `IBackendPreviewPngEncoder` 下沉到公共接口层并评估直接撤并仅 82 行的 `Backend.Rendering.Skia`；重命名或合并名实不符的 `Backend.Rendering`（纯快照协议）。
2. **协议序列化与 Worker 协议现代化**：
   - 逐步以 .NET 10 原生 `[JsonPolymorphic]` / `[JsonDerivedType]` + 统一验证器替换 `Backend.Contracts` 中 18 文件 / **2,913 行**手写脆弱 JSON 转换器（尤其是 594 行 `ProjectCommandJsonConverter.cs` 及其 14 次重复 `TryGetProperty("type")` 与硬编码枚举名数组）。
   - 将 36 个可空字段平铺的扁平上帝 DTO `WorkerMessage` 拆分为按任务族强类型化的请求/响应消息，彻底消除易漏检的手工反向黑名单字段校验。
3. **Vulkan / C++ ABI 脆弱点长期替代方案**：
   - 评估将已停更的 `SharpVk 0.4.2`（含 Linux `libdl.so` 劫持补丁）替换为活跃维护的轻量 Vulkan 绑定（如 `Silk.NET.Vulkan` 或受控最小 C 桥接层）。
   - 为 `RuntimeShaderReflection.cs`（绑死 Skia C++ `std::string_view` 内存布局）与 `LinuxPosixShmBacking.cs`（硬编码 64 位内核 `statx` 偏移）增加启动期/构建期 ABI 断言或下沉为标准 C 包装函数（避免 C# 硬算内核/C++ 结构体偏移）。

### 2.5 媒体/导出/原生稳健性与前端协同收口（中-低优先级）
1. **后端媒体、导出与进程层稳健性**：
   - `FfmpegVideoFrameProvider` / `SelfContainedVideoInput` / `OwnedMediaProcess`：全面异步化消除剩余 sync-over-async（如 `PackageResourceTask` 改调 `InspectAsync`）；为 `_buffer = new byte[W*4*H]` 补分辨率上限；使用受控工具路径替代硬编码 PATH `"ffmpeg"`/`"ffprobe"`；为 `FfmpegMediaProbe`/`FfmpegPcmReader` 补默认超时与安全 JSON 数值解析；`OwnedMediaProcess` 补 `CreateNoWindow = true` 并复用 `BoundedTextReader`。
   - `VideoExporter` & `FfmpegCommandLine`：修复透明导出降级帧（应填全透明且预乘）及渲染到写入间异常的帧释放缺口；补齐默认路径 `-protocol_whitelist file,pipe` 与 `-` 开头文件名防注入及 `MaximumOutputBytes` 上界防溢出。
   - `Processes` & 原生 C/C++：修复 `WindowsJobObjectGuard.StartProcess` 部分流构造失败句柄泄漏、`OwnedProcessGroup` PID 回绕与僵尸孤儿进程、`LinuxSystemdEnvelopeBackend.OwnedPipeStream` 线程池 50ms 轮询、`WindowsJobEnvelopeBackend` 128 MiB 硬编码与 40ms `Task.Delay` 妥协、`MediaPresentationDeployment` 重复 3–4 遍全量 SHA-256；修复 `native/gpu-preview/broker.cc` 漏检 `napi_*` 返回码泄漏 fd、`gpu-preview.cc` 异常路径泄漏 `VkCommandBuffer`、`native/shm-preview/shm_bridge.cc` 两平台 N-API 包装合并去重。
2. **前端协作面（按需推进）**：
   - 将 `frontend/src/client/mako-client.ts`（2,216 行）按子域与后端新服务边界对称拆分；
   - 按模块拆分千行级页面组件（`LyricsStageCanvas.tsx`、`AppearancePage.tsx`、`Workspace.tsx`、`OrchestrationPage.tsx`），复查 22 处空 `catch {}`。

---

## 三、推荐后续实施节奏与阶段门禁

| 阶段 | 建议批次 | 关键准出验收门禁 |
| :--- | :--- | :--- |
| **P2-A（测试并行与可信度）** | 拆分 `PreviewTransportTests` + 抽 `TestSupport` + 消 GPU 假绿 + 消字体 SHA-256 + 根治 `BaseMaterialPixelOracleTests`/`Media.Tests` 偶发超时 | 全量 `dotnet test --configuration Release` 无超时、0 失败、0 假绿，CI 后端单测时长显著下降。 |
| **P2-B（依赖归位与代码规范）** | 纠正 3 处测试依赖越界 + 撤 `TechnicalSpike` + `dotnet format` 展开 5,627 压行并开启 `EnforceCodeStyleInBuild=true` + 清理 `CS1591`/一次性脚本/死文档 | `TreatWarningsAsErrors` + `EnforceCodeStyleInBuild` 双开 0 警告；全仓无跨层测试依赖与失效脚本。 |
| **P3-A（托管层与领域边界）** | 拆 `ProjectSessionRegistry`/`ProjectSession`/`PreviewBackendService`/`Worker/Program.cs` + `Application` 去 Avalonia 化 + 去 `PrimaryBranchId` Hack + 统一歌词链路 | 后端核心服务无 >1,500 行 `partial` 上帝类；锁定与异步 I/O 解耦；全套前后端集成测试全绿。 |
| **P3-B（渲染去重、协议与 ABI 演进）** | 抽 `CompiledImageGraph`/`SceneCompositionProgram` 重复 + 消 3× SVG/SkSL 编译 + `[JsonPolymorphic]` 替换手写转换器 + 拆 `WorkerMessage` + 评估 `SharpVk`/Host 去 Skia | 契约 `freeze.py --check` + `validate.py` 全绿；前端 0 回归；会话创建与渲染冷启动耗时可量化下降。 |
