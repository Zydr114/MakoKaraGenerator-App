# M12 交接：材质路径与 1440p（2026-11-10）

分支 `dev/script-sdk-render-upgrade`，产品 0.112.61，已推送到私有远程。详细数据与决定原文见
[M12 测量记录](script-sdk-render-m12-evidence.md) §5（§5.3 为 G1 与 O7 设计，§5.4 为测试包基线，§5.7 为 F1 与缺背景实施）。

## 1. 当前状态

- **已完成**：O2（0.112.47）、O1（0.112.52～55）、O4（0.112.57）。本轮另有三项修复：
  - 0.112.58：包图校验固定用 CPU 设备，修好 GPU 预览「请求结果未知」。
  - 0.112.59：GPU 流硬失败带出内层引擎错误码、根异常类型、抛出方法与引擎文本，桌面日志保留。
  - `frontend/scripts/cdp.mjs`：CDP 连接关闭后请求立即失败，测速脚本不再静默以 0 退出。
- **G1 实测**（72 行，720p24 计时 60 s）：C、P、G 达到目标；CustomInk 720p 偏慢（p95 48.7 ms），
  切 1440p 首帧超表面预算；X 同样在 1440p 失败，720p 只有 12 fps；GlassInk 一帧未出。
- **测试包与基线（2026-11-10，已完成）**：七个测试包在 `backend/tests/Fixtures/ScriptEffectsV2Perf/`（`PerfFixturePackageTests`）；
  测速新增 `--v2-params`、`--background-image`；基线见记录 §5.4。要点：不读输入的常量色材质 Render p50 已 36 ms
  （图-直通 6 ms），1440p 失败占用与 CustomInk 逐字相同（36 张整屏），确认问题在材质路径的整屏分配而非着色器；
  背景媒体输入比同结构读下层多约 64 ms，原因未查；缺背景时必需／可选两版都首帧整条流失败。
- **材质路径排查（2026-11-10，已完成，未改代码）**：见记录 §5.5、§5.6。
  - 场景表面分配占材质渲染的 87%，着色器只占约 3%。
  - 慢的原因有三：每张整屏表面都同步等 GPU（`Submit(true)`）；每行约 19 张整屏表面，其中约 8 张是空活；层组装逐层新建表面。
  - 背景媒体多出约 64 ms：每次材质执行都重捕背景，且可变位图每次都重新上传。
- **F1（0.112.60，已完成）**：每帧只取一次背景帧并固定为不可变位图，同一对象内背景捕获复用。
  棋盘格下背景媒体两组 Render p50 从约 117 ms 降到约 61 ms，720p 从 8.4 fps 升到 16.2 fps；常量色经同时段 A/B 确认不变。
  本机当前比 2026-11-09 基线整体慢约 10 ms（常量色 p50 约 44～48 ms），之后只用同时段 A/B 比较。
- **缺背景行为（0.112.61，已完成）**：不透明缺背景媒体时，必需输入旁路／材质兜底，诊断 `BackgroundMediaUnavailable`；
  可选输入用工程底色代替。Worker 静态导出与静态渲染会话仍拒绝任何材质／图诊断，缺背景在这两条路径上仍失败，待用户决定；
  前端编辑时提示未做。
- **未开工**：F2、F5（O7）、F3、F4、F6，O3，合同冻结。

## 2. 用户决定

更早的决定（门槛分门禁／目标／观察、O4 只在调试开、修复顺序 O1 → O4 → O3）见记录 §5.0～§5.2。2026-11-10：

1. 不再用 `backend/samples` 里的现有样例定位材质／图问题，专门写测试用效果包，放在测试目录作为测试夹具，不改样例。
2. 测速增加带图片背景的版本，图片用 `/mnt/Data/Program/MakoKaraGenerator-MKFXSample/assets/test-patterns/`
   下已有的彩色棋盘格 `checker-gradient-*.png`（按画布尺寸选用，只读取，不修改该仓库）。
3. 1440p 按收紧捕获框修（O7）：材质与图的捕获框从整张画布改为目标实际区域，不放大预算。
4. O7 中，着色器实际采样超出自己声明范围的包按违约处理，不保证效果；全图分析类能力以后作为显式能力另行设计，现在不做。
5. 缺背景媒体时兜底还是拒绝应用，留待测试包基线后再定（已由第 9、10 条决定）。
6. 测试包 σ 等参数由测速打包时改清单默认值（如 `--v2-params blurPx=8`），实际取值写进结果；夹具只放一份。
7. 棋盘格固定用 `checker-gradient-1920x1080.png`（与默认画布一致），一次运行 720p／1440p 两档共用，计时中途不换背景。
8. 基线 720p 计时 60 s（与 G1 一致），约 18 次运行：7 个测试包 × 纯色／棋盘格，两个模糊包 σ 各测 0 与 8。
9. 记录 §5.6 的修复计划照做：F1 → F2 → F5（O7）→ F3、F4、F6，每项一个 PATCH。
10. 缺背景：渲染时一律退回兜底并给出写明原因的诊断，不让整条流失败；编辑时（套用此类材质、移除背景）只提示、不阻止；
    可选背景媒体输入缺失时用工程底色代替。
11. 测速期间电脑空闲，测速可直接进行，不必询问；G1（`run-g1.sh`）仍需先问用户。

## 3. 下一步（建议顺序）

1. ~~写测试包~~（已完成），放 `backend/tests/Fixtures/ScriptEffectsV2Perf/`，格式同样例（清单＋SHA），每包一个导出：
   材质-常量色、材质-区域覆盖、材质-下层（模糊 σ 可调）、材质-背景媒体必需、材质-背景媒体可选、图-直通、图-模糊。
   加一条 C# 测试验准入与原生编译，常量色包加最小像素检查。注意 σ=0 时模糊节点仍分配两张临时表面，
   读下层的开销要与「区域覆盖」对比得出；可选输入缺失目前报 `GraphOptionalInputPolicyPending`，纯色背景下两版预计都失败。
2. 测速（已完成）：`--v2-samples` 能取到夹具包（与样例同名即报错）；`--background-image <png>` 在 SDK2 内容与计时之前
   经工程设置「选择图片或视频…」上传背景；`--v2-params` 打包时改参数默认值。
   然后在当前版本测基线（已完成，见记录 §5.4；`.local/script-sdk-m12-20261110/run-baseline.sh`，
   `BASE_OUT=<目录> bash run-baseline.sh [solid|checker [组名...]]`，汇总 `aggregate-p3.py <目录>`）。
3. ~~排查材质路径与背景媒体~~（已完成，记录 §5.5）。2026-11-10 用户恢复 O7，并要求先按结构问题排查、制定计划。
4. ~~F1~~（0.112.60）、~~缺背景行为~~（0.112.61）已完成，见记录 §5.7。
5. 下一项 F2：`RasterSceneSurface` 只在需要 CPU 读回时同步（像素检查开启、空表面读回），其余只提交不等待，帧交付保持现有同步。
   回归用设备包装计数：像素检查关闭时，每帧不再逐张 `Submit(true)`。之后依次 F5（O7）、F3、F4、F6。
   每项一个 PATCH，像素回归确认画布坐标不变、区域内像素一致，再复测。
   复测须先提交，测速期间不改 `backend/src`。对比用同时段 A/B：参考本地 `.local/script-sdk-m12-20261110/ab-f1.sh`，
   交替切换 `backend-current.json` 指向新旧发布物。O7 的回归测试读 `ImageSurfaceBudgetSession.Usage`，补上基线缺的成功帧占用。
6. 待用户决定：Worker 静态导出／静态渲染会话遇到材质／图诊断时是否放开、如何随结果上报（现为整体失败，透明背景依赖同样受影响）。
   待实施：前端编辑时提示（套用背景依赖材质、移除背景）。
7. 之后做 O3、合同冻结（当前合同 1.49.1）与前端合同同步。

## 4. 参考文件

**O7 相关代码**（`backend/src/`）
- `MakoKaraGenerator.Runtime.Hosting/ProjectRenderSessionFactory.cs:76`、`:97`、`:102`：整屏捕获框，以及用它创建图与材质程序。
- `MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.ImageScene.cs:107`：一行的工作框取各程序框的并集。
- `MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.ImageScene.Materials.cs:9`（`ExecuteObjectMaterial`）、`:71`（背景捕获，经对象内 `ObjectBackgroundCache` 复用）。
- `MakoKaraGenerator.Rendering.Skia/Effects/CompiledPackageGraphExports.Materials.cs:9`（`CreateMaterialProgram`）、
  `:71`、`:78`（透明背景或缺必需背景媒体时退回兜底）；`CompiledPackageGraphExports.cs:59`（`CreateProgram`）。
- `MakoKaraGenerator.Rendering.Skia/Effects/CompiledImageGraph.MaterialCoverage.cs:75`：`MaterialCoveragePeak` 预算检查。
- `MakoKaraGenerator.Rendering.Skia/Effects/CompiledImageGraph.cs`（`Execute`、`MissingRequiredMedia`、`ActiveInputs(frame, 可用性)`）：
  缺必需背景媒体旁路；`SkiaRenderPipeline.SceneSurfaces.cs`（`BackgroundInput`）：可选媒体以底色代替。
- `MakoKaraGenerator.Rendering.Skia/Effects/MaterialAuthorGraphCache.cs`：同帧作者输出复用。
- `MakoKaraGenerator.Core/Effects/ImageGraphCompiler.cs:17`、`:21`：会话 512 MiB、外扩上限 256 px。
- `MakoKaraGenerator.Rendering.Skia/SkiaRenderPipeline.SceneSurfaces.cs:11`（背景捕获）、`:81`（逐张 `FlushAndSubmit`，F2 改这里）；
  `SkiaRenderBackend.cs:358`（Vulkan 为 `Submit(true)`）；`SkiaRenderPipeline.cs:290`～`:365`（本帧背景作用域、背景位图解析与 `DrawBitmap`）；
  `SkiaRenderPipeline.ImageScene.Surfaces.cs`（下层快照、层组装、透明度）；`SkiaRenderPipeline.MaterialCapture.cs`（材质区域两张捕获）。
- 排查工具（本地）：`.local/script-sdk-m12-20261110/trace-o7.sh`（测速期间栈采样＋GPU 忙碌率）、`speedscope-o7.py`、`busy-o7.py`、`SurfaceBench/`。
- `MakoKaraGenerator.Core/Effects/EffectPackageImageGraphs.cs:67`：图根 `bounds.source` 只接受 `targetRegion`。

**失败可观测性**
- `backend/src/MakoKaraGenerator.Backend.Worker/GpuStreamFailure.cs`、`GpuStreamSession.cs`（硬失败分支）。
- `desktop/main.cjs`（`desktop-broker-error`／`desktop-fail-stop` 日志）、`desktop/gpu-preview/wire.cjs`（`workerFailureSummary`）。

**测速**
- `scripts/gpu-preview-p3.mjs`：P3 测速；导出 `analyze(records, manifest)`，可对 `events.jsonl`＋`manifest.json` 离线重算。
- `scripts/gpu-preview-v2-content.mjs:105`：`materialSlotPlan`，未唱填充用第一个材质导出、已唱填充用最后一个。
- `.local/script-sdk-m12-20261108/run-g1.sh`（本地，未提交）：`G1_OUT=<目录> bash run-g1.sh [组名...]`，
  组为 C、P、G、M-CustomInk、M-GlassInk、X；需桌面窗口在屏可见，运行前先问用户。
- 结果目录（本地）：`.local/script-sdk-m12-20261108/g1-58/`（G1）、`g1-59/`（CustomInk 重现）、`formal-02/`（HTTP 导出）、`o4-57/`。

**样例与文档**
- `backend/samples/ScriptEffectsV2/`：CustomInk、GlassInk 为材质，LineGlitch 为图，其余为控制器。
- `backend/tests/MakoKaraGenerator.Rendering.Tests/V2ResourceSamplePackageTests.cs`：样例只验准入与编译，GlassInk 没有像素测试。
- `backend/docs/scripting-package-graph-semantics-contract.md` 第 54 行：图框来源只接受 `targetRegion`。
- `backend/docs/architecture/backend-api.md:1787`：版本说明段落，每次程序改动追加一句。

## 5. 工作约定摘要

- 程序改动：`backend/Directory.Build.props` 递增 PATCH（下一版 0.112.62），并在上面的版本说明段落追加一句；
  `flock -o /tmp/mkg-gpu-render-dotnet.lock dotnet build MakoKaraGenerator.slnx` 零警告；新回归须在旧代码上失败。
- 前端改动先过 `pnpm typecheck`、`pnpm test:unit`；不手改 `docs/backend/contract/`。
- 桌面测速前 `pnpm build:desktop` 重新发布后端；测速期间不并行构建或占用 GPU。
- Rendering 全量测试偶发 MixedEffectPackageTests／SourceMaterialRuntimeTests 失败，单独重跑通过即视为非回归。
- 中文 `type(scope): 摘要` 提交，按路径暂存；不碰未跟踪的 `.VSCodeCounter/`、`backend/tools/backend-script-render-upgrade/M07DiagnosticRunner/`、
  `TraceRead/`、`docs/plans/script-sdk-v2-m11-author-notes.md`。
