# M14 移除 SDK v1 与最终验收：证据记录

日期：2026-11-15，`dev/script-sdk-phase-two-structure`。依据[阶段二路线 §8](script-sdk-v2-phase-two-roadmap.md#8-m13m14)、[验收表 M14 行](script-sdk-v2-render-acceptance.md#3-15-个验收节点)与[主计划 §7.10](script-sdk-v2-render-pipeline-plan.md#710-m06-之后的两个执行阶段)。

**状态：M14 移除 SDK v1、全量回归、真实 HTTP／硬件 Vulkan、浏览器／桌面 UI 冒烟及 M00／M12 最终复测全部完成并验收通过。** 基点为 M13 补充收口后的 `b47da5f`，后端生产源码冻结点 `c2fbff4`（产品 `0.113.0`、SDK `2.0`），HTTP 合同冻结至 `1.53.0`（`91ca50f`），前端长耗时 GPU 建流时钟采样修复 `9577459` 与工具探针适配 `6b0447e`、`4f850cb`、`9b447e8` 后在 `HEAD = 9b447e8` 完成最终复测。

## 1. 冻结点与环境

| 项 | 值 |
|---|---|
| 分支与基点 | `dev/script-sdk-phase-two-structure`，基点 `b47da5f`（共 15 个实施与修复提交：`4eb56d3`～`9b447e8`） |
| 后端生产源码冻结点 | `c2fbff4`（产品 `0.113.0`，SDK `2.0`；`91ca50f` 起至 `9b447e8` 未再修改 `backend/src`、`Directory.Build.props` 与 `desktop/` 原生源码） |
| HTTP 合同 | `1.53.0`（`91ca50f`），双端（`backend/web/` 与 `frontend/docs/backend/contract/`）字节同步；`freeze.py` 0 漂移，`validate.py` 通过（115 路径／128 方法／667 schema／3,932 正例／2,842 反例，保持 `1.46.0`～`1.52.1` 历史语料 SHA-256 还原门禁） |
| 前端预览控制器修复 | `9577459`：只修改 `frontend/src/editor/preview/gpu-preview-controller.ts` 与单元测试 `frontend/test/unit/gpu-preview-controller.test.ts`，不修改 `backend/src`、桌面原生库或 HTTP 合同；修复后重新构建 Web 静态产物并通过 `desktopBuildState` 校验（`web=null, deployment=null`） |
| 探针／采样工具适配 | `6b0447e`（GPU 验收探针 Oracle 后端与纯 v2 失控控制检测）、`4f850cb`（Runtime 性能采样工具 `--backends`）、`9b447e8`（M07／M08 确定性矩阵纯 v2 适配），均仅修改 `backend/tools/` |
| SDK 2.0 表面快照 | `backend/tests/MakoKaraGenerator.Scripting.Tests/Snapshots/sdk-v2-public-surface.txt`（零改动，274 个公开类型） |
| 阶段七最终匹配发布 | `.local/backend-dist/20261114T204251510Z`（`version: "0.113.0"`、`sourceCommit: "4f850cba4563758e769fa8f0e9b038022bd1e58f"`、`sourceDirty: false`、`rid: "linux-x64"`、`selfContained: true`、`configuration: "Release"`、`fileCount: 1025`） |
| 最终发布摘要 SHA-256 | `deployment.json`: `5c2a364e1bfb727ec2b1426f026737609a76186602a5a92dc7bf7da5cf9b28ee`<br>`server/MakoKaraGenerator.Scripting.dll`: `d4a7ec6ce1e17f1dcb8518f431c16c23ea52e1d3de499c88a1a3e21f10e358cd`<br>`worker/MakoKaraGenerator.Scripting.dll`: `d4a7ec6ce1e17f1dcb8518f431c16c23ea52e1d3de499c88a1a3e21f10e358cd`<br>`gpu-preview-manifest.json`（`abiVersion: 1`）: `02bb9dcfa4b42f3e0af1f95e808ba35d00e37884c15f4ed1985dd7c67acb3234` |
| 阶段六 b 匹配发布 | `.local/backend-dist/20261114T175207760Z`（`version: "0.113.0"`、`sourceCommit: "91ca50ff0dd4afc848422c0e969a641a7b9d3b53"`、`sourceDirty: false`、`gpu-preview-manifest.json`: `9f4b8f748efdaaa0ca20047efad30189cdd0f9cd9e07fab61c389488e80a75c0`） |
| 机器与图形环境 | CachyOS Linux，AMD Radeon 880M（RADV，`/usr/share/vulkan/icd.d/radeon_icd.json`），独立进程与隔离 profile 运行 |
| 本地不入库证据根 | `.local/m14-phase6-acceptance/`（阶段六 b 发布测试、v1 HTTP 阻断、W8 作者、硬件 Vulkan GPU 探针及合同校验）<br>`.local/script-sdk-m14-20261115/`（`summary.json`、`g1-table.md`、前后两轮 UI 冒烟、`m00-runtime/`、`h2/`、`http/`、`g1/`、`diagnostics/g1-pre-fix/`） |

## 2. M14 删除范围、准入阻断与提交台账

### 2.1 删除范围与完整保留项

- **删除 SDK v1 公共表面（`afb1334`）**：删除 `IKaraokeEffect`、`IVisualEffect`、`EffectBase`、`FrameContext`、`StateContext`、`FrameWriter`（含 `Groups`／`ImageEffects`／`Lines`／`Marks`／`Materials`／`Style` 分部）、`VisualWriter`、`VisualContexts`、`BurstWriter`、`EmitterWriter`、`CursorEmitterDescriptor`、`CursorEndpoint`、`EmissionFrameContext`、`EmissionSchedule`、`ParticleSample`、`GeometryQueries`、`AnchorMap`、`EffectDiagnostics` 及 `TimelineHistory`；收敛纯度分析契约至纯求值接口 `IPureKaraokeEffect`／`IPureVisualEffect`（含泛型入口），同步整理纯类型文件 `FrameOutput.cs`、`ImageEffectBindings.cs`、`ParticleCurves.cs`、`BurstAnchor.cs`。
- **删除 v1 运行时重放与连续粒子路径（`a306b18`）**：移除 `FrameSession` 120 Hz 状态重放循环、`TimelineFrameSourceFactory`／`FrameSessionFactory` 中的 v1 分派与 `preservePure` 运行分支，以及 v1 连续粒子／拖尾历史缓存；**完整保留 v2 的 120 Hz 最终锚点记忆（`Continuous120Hz` 锚点插值与 `PureFrameSourceFactory`）**。
- **删除旧 v1 样例包与残留 `IsV2` 桥接（`eccb3c4`、`c2fbff4`）**：删除 `backend/samples/ScriptEffects/` 全部旧 9 样例包（37 个文件）与未被引用的 `PersistedAudioEffectFixture.cs`，完整保留 `backend/samples/ScriptEffectsV2/`；升级产品版本至 `0.113.0`，将 `EffectPackageProfiles.IsV2` 全面收敛为 `IsSupported` 并移除临时 `IsV2` 方法。

### 2.2 准入与持久化阻断

- **包与合同收敛**：`EffectPackageProfiles` 仅接受 `EffectPackageProfiles.V2`（包格式版本 `packageVersion: 3`、SDK `2.0.x`、`shaderProfile: "skia-ganesh-3.119.4-v2"`）；包内 `.program.json` 子文件继续使用合法 SkSL ABI `ShaderProgramDefinition.RasterProfile`（`"skia-raster-3.119.4-v1"`）。`AuthorSourceInspectionResponse` 将 `packageVersion` 枚举收窄为 `[3]`、`shaderProfile` 枚举收窄为 `["skia-ganesh-3.119.4-v2"]`。
- **工程与持久化门禁**：磁盘工程 Schema `31` 与效果合同版本 `2`（`effects.json`）保持不变，保证既有 v2 工程无缝打开；运行时 `ProjectEffectsJson.PackageProfile` 收敛为 `EffectPackageProfiles.V2`。导入、检查、建草稿或打开工程遇到含 v1 嵌入包（`packageVersion: 2`、`sdkVersion: "1.0.*"` 或包级 `shaderProfile: "skia-raster-3.119.4-v1"`）时显式拒绝并抛出 `PackageProfileMismatch`。
- **个人模板库保护**：通过 `/api/v1/template-libraries/{id}/imports/preflight` 预检含 v1 包的 `.mktpl` 归档时，`StyleTemplateLibrary.Deserialize` 将内嵌包的 `EffectPackageException("PackageProfileMismatch")` 收入库级 `Issues`，统一返回 HTTP `422` `code="commandRejected"`、`failureCode="LibraryInvalid"`，且磁盘原模板库文件字节与修订号完全不变。
- **旧 v1 C# API 源码编译拒绝**：作者草稿提交旧 v1 C# API（如继承 `EffectBase` 或使用 `FrameContext`／`FrameWriter`）时，Roslyn 编译阶段即因类型／成员已移除而失败（`succeeded=false` 且禁止采用）；未实现 `IPureKaraokeEffect`／`IPureVisualEffect` 入口契约的类归因为 `PackageEntryInvalid`。

### 2.3 提交台账（`4eb56d3`～`9b447e8`，共 15 个提交）

| 序号 | 提交 SHA | 提交信息 | 范围与内容 |
|---|---|---|---|
| 1 | `4eb56d3` | `test(scripting): 迁移脚本编译与契约测试至SDK v2并清理v1专属测试` | 迁移 `Scripting.Tests` 编译、宿主服务与输出契约测试至 v2，移除纯 v1 契约／重放测试 |
| 2 | `b1e71b8` | `refactor(frontend): 迁移前端脚本、集成测试与作者草稿到 SDK v2` | 迁移前端脚本、单元／集成测试与作者草稿默认模板至 v2 源码与 `packageVersion: 3` |
| 3 | `18713b7` | `refactor(tools): 迁移根目录 GPU 脚本与后端工具探针到 SDK v2` | 迁移根目录 GPU 脚本与后端工具探针夹具／生成源至 v2 |
| 4 | `87bcf39` | `test(backend): 升级共享效果包夹具与宿主测试套件至SDK v2` | 升级共享效果包夹具与 Core／Application／Backend.Hosting／Server／Worker／Media 测试至 v2，翻转为 v1 显式拒绝测试 |
| 5 | `ae7bb86` | `test(rendering): 迁移渲染与导出回归测试至SDK v2并移除v1粒子测试` | 迁移 `Rendering.Tests` 与 `Export.Tests` 至 v2 并移除 v1 粒子测试 |
| 6 | `eccb3c4` | `feat(effects): 显式阻断SDK v1效果包导入并移除v1样例包` | 删除 `backend/samples/ScriptEffects/`（37 文件），收窄 `EffectPackageProfiles` 仅接受 V2 并阻断 v1 持久化与导入 |
| 7 | `a306b18` | `refactor(scripting): 移除v1状态重放与连续粒子运行时路径` | 移除 `FrameSession` 重放循环、`preservePure` 分支与 v1 粒子运行时路径，保留 v2 120 Hz 最终锚点记忆 |
| 8 | `afb1334` | `refactor(scripting): 移除SDK v1公共表面并收敛纯度编译契约` | 删除 SDK v1 公共类型与写入器，收敛纯度分析器与泛型入口契约 |
| 9 | `d670347` | `test(rendering): 对齐共享夹具v2合流后的组变换材质与透明度测试` | 对齐组变换材质与组透明度隔离测试至合流后共享 v2 夹具 |
| 10 | `c2fbff4` | `chore(backend): 升级M14产品版本至0.113.0并收敛残余IsV2桥接` | 升级 `0.113.0`，移除残余 `EffectPackageProfiles.IsV2` 桥接，兼容 `instrument.py` |
| 11 | `91ca50f` | `docs(contract): 冻结M14移除SDK v1后的1.53.0合同与双端同步` | 冻结 HTTP 合同 `1.53.0`，更新 `freeze.py`／`validate.py` 与双端合同副本及 ADR-0261 |
| 12 | `6b0447e` | `fix(tools): 修复GPU验收探针Oracle渲染后端与纯v2失控控制检测` | 修复 `FixtureOracle` 默认渲染后端与纯 v2 `Runaway` 标记／失控控制检测 |
| 13 | `4f850cb` | `fix(tools): 支持Runtime性能采样工具按场景指定渲染后端` | 为 `backend-render-performance` 增加 `--backends` 支持按场景指定 `cpu`／`vulkan` |
| 14 | `9577459` | `fix(frontend): 刷新长耗时GPU预览建流后的播放时钟采样避免30s过期阻断` | 修复长耗时 GPU 预览建流后 `samplePerformanceMs` 超过 30 s 过期阻断 |
| 15 | `9b447e8` | `fix(tools): 适配M14移除SDK v1后的M07与M08确定性矩阵工具` | 移除 `M08FrameFingerprint.cs` v1 分支，适配 `m07_matrix.py`／`m07_thresholds.py` 纯 v2 矩阵与 `--v1-reference-dir` |

## 3. 本轮发现并修复的缺陷及探针维护

### 3.1 生产缺陷：`9577459` 长耗时 GPU 预览建流导致播放时钟采样过期

- **现象与复现**：阶段七首轮运行 G1 `solid-X`（7 包 × 72 行，纯 v2 全量编译与准备）时，首次 `gpuPreview.create()` 耗时约 `31 s`，两次运行均在 `720p24` 首阶段停止并报错 `Current public/native stream did not yield enough exact-generation drawn/native-completed frames`（修复前失败样本已归档于 `.local/script-sdk-m14-20261115/diagnostics/g1-pre-fix/failed-solid-X-1` 与 `failed-solid-X-2`）。
- **根因**：`PreviewPanel.tsx:216-219` 在 `mode === 'paused'` 时会去重抑制周期性 `controller.show()` 调用；而 `gpu-preview-controller.ts` 在 `this.client.gpuPreview.create(...)` 等待约 `31 s` 返回后，仍沿用建流前采样的 `want.playback.samplePerformanceMs` 下发 `bridge.setPlaybackState(...)`，在 `desktop/gpu-preview/preload.cjs:105` 与 `desktop/main.cjs:268` 触发 `> 30000 ms` 过期检查（`Invalid audio clock sample`／`Audio clock sample out of bounds`），导致首帧控制被拒。
- **修复与变异验证**：在 `frontend/src/editor/preview/gpu-preview-controller.ts` 引入 `freshPlayback(playback)`，对 `ageMs <= 1000` 保持原样，对 `ageMs > 1000` 在 `paused` 态刷新 `samplePerformanceMs: now`、在 `playing` 态按 `Math.max(0, Math.min(Number.MAX_SAFE_INTEGER, playback.mediaTimeUs + Math.round(ageMs * 1000 * playback.rate)))` 安全外推时间并刷新 `samplePerformanceMs`，同时保持 `this.sent = want` 去重引用。在 `frontend/test/unit/gpu-preview-controller.test.ts` 新增 2 条模拟 31 s 建流延迟的单测并做可逆变异验证（去掉 `freshPlayback` 调用后单测失败），修复后前端单测、`browser-smoke`、`desktop-smoke`、`author-error` 与 `solid-X` 全部通过。

### 3.2 工具与探针维护（仅修改 `backend/tools/`）

- **`6b0447e`（GPU 验收探针）**：阶段六 b 实跑 `backend-gpu-render-probe` 时发现 `FixtureOracle/Program.cs:66` 将 `ProjectRenderSessionFactory` 的 `renderBackend` 误写为 HTTP 枚举 `"gpu"`（内部 `SkiaRenderBackend.Parse` 仅接受 `"cpu"`／`"vulkan"`），改为读取 `MAKO_RENDER_BACKEND ?? "vulkan"`；同时移除纯 v2 `Runaway` 夹具废弃的标记文件路径并在 `lifecycle.py` 适配纯 v2 失控控制超时与取消排水检测。
- **`4f850cb`（Runtime 性能采样工具）**：移除 SDK v1 后，带 v2 效果的场景仅支持 `vulkan` 后端；为 `backend/tools/backend-render-performance/{bench.py,analyze.py}` 增加 `--backends` 参数，支持 `m00-short-v2-a`／`m00-short-v2-b`／`m00-matrix-v2` 按 `vulkan` 单后端矩阵校验（附 `test_analysis.py` 单元测试）。
- **`9b447e8`（M07／M08 确定性矩阵工具）**：移除 `M08Runner/M08FrameFingerprint.cs` 的残余 v1 分支；为 `m07_matrix.py` 增加 `--v1-reference-dir` 以对照已归档的历史 v1 参考输出，并在 `m07_thresholds.py` 支持纯 v2 矩阵（3 个样本 × 4 轮/进程共 12 条跨进程确定性检验），同时保留对历史 5 组基线（20 条）的校验兼容。

## 4. 构建、全量回归、真实 HTTP 与 UI 冒烟

### 4.1 后端构建与全量 14 项目测试（`8,350 通过 / 0 失败 / 0 跳过`）

`dotnet build backend/MakoKaraGenerator.slnx` 零警告零错误。在 `MAKO_GPU_SMOKE=1 MAKO_REQUIRE_VULKAN_TESTS=1` 下运行全部 14 个测试项目（其中 `Backend.Server.Tests` 的 3 条 `Category=PublishedServer` 用例在阶段六 b 指向匹配发布物 `.local/backend-dist/20261114T175207760Z` 实跑），共计 **8,350 通过 / 0 失败 / 0 跳过**：

| 测试项目 | 通过数 | 备注 |
|---|---:|---|
| `MakoKaraGenerator.Core.Tests` | 1,573 | 含 v1 包／持久化 `PackageProfileMismatch` 显式拒绝与 Schema 31 不变 |
| `MakoKaraGenerator.Application.Tests` | 749 | 含模板库／预设／传输拒绝回归 |
| `MakoKaraGenerator.Processes.Tests` | 52 | — |
| `MakoKaraGenerator.Timeline.Tests` | 84 | — |
| `MakoKaraGenerator.Scripting.Tests` | 633 | 含 `SdkV2PublicSurfaceSnapshotTests` 274 类型快照零变更 |
| `MakoKaraGenerator.Backend.Rendering.Skia.Tests` | 9 | — |
| `MakoKaraGenerator.Backend.Rendering.Tests` | 289 | 含快照编解码拒绝与样式回归 |
| `MakoKaraGenerator.Backend.Worker.Tests` | 148 | — |
| `MakoKaraGenerator.Export.Tests` | 180 | — |
| `MakoKaraGenerator.Runtime.Hosting.Tests` | 158 | — |
| `MakoKaraGenerator.Media.Tests` | 434 | 禁用集合并行复跑全过 |
| `MakoKaraGenerator.Backend.Hosting.Tests` | 1,678 | 含作者编译拒绝与 `PackageEntryInvalid` |
| `MakoKaraGenerator.Rendering.Tests` | 1,825 | 分 4 批含真实 Vulkan GPU（`VulkanRenderDeviceSmokeTests`／`VulkanOwnedImageReadbackTests` 8/8） |
| `MakoKaraGenerator.Backend.Server.Tests` | 538 | 非 `PublishedServer` 535 + 匹配发布 `PublishedServer` 3 |
| **合计** | **8,350** | **0 失败 / 0 跳过** |

### 4.2 前端与构建检查（`9577459` 修复后）

- `pnpm --dir frontend run typecheck`：0 错误。
- `pnpm --dir frontend run test:unit`：**55 个文件 / 4,490 条全部通过**（含 `contract.test.ts` 合同 SHA／示例核对与新增 2 条 31 s GPU 建流刷新单测）。
- `pnpm --dir frontend run test:integration`：在匹配发布物下分 4 个串行批次实跑，**19 个文件 / 191 条全部通过**。
- `pnpm --dir frontend run build` 与桌面构建态检查 `desktopBuildState` 通过（`web=null, deployment=null`）。

### 4.3 真实 HTTP 与硬件 Vulkan GPU 探针（`.local/m14-phase6-acceptance/summary.json`）

全部在匹配发布物（`0.113.0`，`sourceDirty: false`）、独立 profile、loopback 与硬件 RADV Vulkan 下执行并通过 `validate.py` 合同校验：

| 探针／批次 | 请求与断言 | 合同校验（`validate.py`） | 关键验证结果 |
|---|---|---|---|
| `v1-http-rejection` | — | 14 响应 / 2 成功请求，0 未配对 | `inspect` 与 `drafts` 均返回 `422 PackageProfileMismatch`；`template-libraries/personal/imports/preflight` 返回 `422 LibraryInvalid`，个人库前后修订均为 `missing` 原样保留；凭证扫描 33 文件通过 |
| `w8-authoring` | 147 请求 / 1,019 断言 | 138 响应 / 41 成功请求，0 未配对 | 18 个 Worker 身份，作者草稿编辑／编译／采用／CAS／保存重开／预览全过；凭证扫描 371 文件通过 |
| `gpu-http-previews-a` | 261 请求 / 2,374 断言 | 231 响应 / 50 成功请求，0 未配对 | 18 硬件 Worker，静态／缩放／图／Sprite／材质 Vulkan 预览与诊断通过 |
| `gpu-http-previews-b` | 191 请求 / 1,726 断言 | 171 响应 / 34 成功请求，0 未配对 | 12 硬件 Worker，透明与多时刻对照通过 |
| `gpu-http-exports` | 398 请求 / 2,540 断言 | 342 响应 / 26 成功请求，0 未配对 | 16 硬件 Worker，不透明与透明视频导出通过 |
| `gpu-http-preview-controls-init` | 395 请求 / 2,234 断言 | 394 响应 / 40 成功请求，0 未配对 | 27 硬件 Worker，纯 v2 `Initialize` 取消／deadline／close 失控控制排水通过 |
| `gpu-http-preview-controls-eval` | 398 请求 / 2,249 断言 | 397 响应 / 40 成功请求，0 未配对 | 26 硬件 Worker，纯 v2 `Evaluate` 取消／deadline／close 失控控制排水通过 |
| `gpu-http-export-controls` | 618 请求 / 3,327 断言 | 618 响应 / 9 成功请求，0 未配对 | 6 硬件 Worker，导出取消／终态与排水通过 |
| **GPU 探针四阶段 6 批合计** | **2,261 请求 / 14,450 断言** | **全部通过** | 含纯 v2 失控控制 6 例（`Initialize`/`Evaluate` × `cancel`/`deadline`/`close`），结束后 0 遗留进程 |

### 4.4 浏览器与桌面真实 UI 冒烟（`9577459` 修复后 `smokes-post-fix/`）

修复前（`step2PreFixSmokes`）与修复后（`postFixFrontendAndSmokes`）两轮真实 UI 冒烟均全部通过，修复后最终结果如下：

- **浏览器静态预览冒烟（`browser-smoke`）**：`appExitCode = 0`、`nonBlackRgbPixels = 4281`、`runtimeExceptions = []`。
- **桌面 GPU 共享纹理冒烟（`desktop-smoke`）**：**18/18 项检查全部通过**，实测 58 帧、0 次 CPU 回读、在途帧峰值 1，覆盖共享纹理出帧、播放、拖动、换输入、1440p 重建、编排页切换、CPU 退回静态预览、恢复跟随及关窗排水。
- **作者运行时异常恢复冒烟（`gpu-preview-author-error`）**：`passed = true`，注入 v2 运行时错误后触发 `WorkerRuntimeDiagnosticError` 并完成公开失败排水（`publicFailureDrained = true`），随后成功恢复新流 `cd549198b800446686ca4500c2863bcd` 且运行目录清理完毕（`runFilesCleaned = true`）。

## 5. M00 对照矩阵与 v1 线性重放消除

阶段七 `m00-runtime/` 全部 8 个运行批次均通过校验（`valid = true`, `errors = []`）：

| 批次 | 场景（后端） | 帧数 | wallMs 中位数（ms） | 轮次中位数／p95（ms） | 说明 |
|---|---|---:|---:|---|---|
| `m00-matrix-static-lyrics` | `lyrics`（cpu / vulkan） | 7 / 7 | 1.40 / 2.16 | p95: 76.37 / 76.66 | 无效果静态歌词 CPU 与 Vulkan 正常（首帧冷启动含初始化） |
| `m00-matrix-static-ruby` | `ruby`（cpu / vulkan） | 7 / 7 | 2.22 / 2.55 | p95: 83.80 / 94.14 | 无效果注音场景 CPU 与 Vulkan 正常 |
| `m00-matrix-static-video` | `video`（cpu / vulkan） | 7 / 7 | 16.92 / 3.16 | p95: 204.79 / 185.66 | 视频背景场景 CPU 与 Vulkan 正常 |
| `m00-matrix-v2` | `multi`（vulkan） | 7 | **3.28** | p95: 109.17（首帧 154.08） | 远端跳跃序列 `1s -> 181s -> 361s -> 1s`（见下） |
| `m00-short-static` | `lyrics`/`ruby`/`video`（cpu, vulkan × 3 轮） | 24 × 6 | cpu: 1.13 / 1.17 / 16.71<br>vk: 2.39 / 2.42 / 2.93 | 轮次中位均 ≤ 17.27 | 短批次稳定基线 |
| `m00-short-v2-a` | `csharp`/`graph`/`multi`（vulkan × 3 轮） | 24 × 3 | 3.32 / 6.66 / 3.52 | 轮次中位: 3.27 / 6.65 / 3.52 | 单一类 v2 效果耗时稳定 |
| `m00-short-v2-b` | `combined`/`material`/`particle`（vulkan × 3 轮） | 24 × 3 | 45.42 / 29.38 / 6.37 | 轮次中位: 46.51 / 28.59 / 6.20 | 重负载复合 v2 场景采样正常 |
| `m00-transparent-three` | `lyrics`/`ruby` 透明（cpu, vulkan × 3 轮） | 24 × 4 | cpu: 1.04 / 1.02<br>vk: 2.57 / 2.55 | 轮次中位均 ≤ 2.63 | 透明输出静态基线 |

**v1 线性重放彻底消除证明**：`m00-matrix-v2` 对多效果工程执行 `1s -> 1s -> 181s -> 181s -> 361s -> 361s -> 1s` 远端跳跃，7 帧逐次 `wallMs = [154.08, 4.39, 3.28, 3.01, 3.40, 2.95, 2.73]` ms（中位数 `3.28 ms`，181s 与 361s 远端跳转均保持 `3.0～3.4 ms`，不随绝对时间线性增长）。对比 [M00 基线记录](script-sdk-render-m00-evidence.md) 中历史 SDK v1 跳至 180s／360s 需耗费 `5～6.7 s` 的 120 Hz 状态重放，可执行 v1 线性重放路径已彻底从系统移除。

## 6. M12 硬门禁（H1–H4）与目标项（G1–G4）最终复测

### 6.1 G1 桌面 GPU 流 7 组完整实测表（原样引自 `.local/script-sdk-m14-20261115/g1-table.md`）

| 组 | 720p fps | 完成比 | 最差 5 s | >250 ms 卡顿 | Render p50／p95／max ms | 新鲜度 p95 | 1440p fps | 暂停 p50／p95／p99 | 跳转 p50／p95／p99 | 改输入 p50／p95 | Worker RSS／VRAM 峰 MiB（720p；1440p） | 失败 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| checker-M-GlassInk | 24.00 | 1.000 | 119 | 0 | 13.9／22.4／55.7 | 35.3 | 29.7 | 19／30／30 | 18／25／25 | 319／352 | 299／94；294／204 |  |
| solid-C | 23.72 | 0.988 | 116 | 0 | 7.8／13.8／150.5 | 24.3 | 30.0 | 13／19／20 | 13／18／23 | 505／616 | 532／224；510／266 |  |
| solid-G | 24.00 | 1.000 | 119 | 0 | 2.7／5.1／26.8 | 17.1 | 29.9 | 6／10／11 | 8／11／24 | 278／318 | 290／39；281／88 |  |
| solid-M-CustomInk | 24.00 | 1.000 | 119 | 0 | 7.3／12.0／27.1 | 27.4 | 30.1 | 11／21／23 | 11／15／17 | 303／369 | 288／66；284／129 |  |
| solid-M-GlassInk | 24.00 | 1.000 | 119 | 0 | 10.9／17.3／33.7 | 30.3 | 29.9 | 17／24／25 | 15／21／23 | 314／375 | 289／78；282／188 |  |
| solid-P | 24.00 | 1.000 | 119 | 0 | 10.6／16.3／31.4 | 27.7 | 27.2 | 13／28／29 | 12／24／29 | 477／519 | 310／24；298／67 |  |
| solid-X | 23.17 | 0.965 | 111 | 0 | 30.2／48.7／220.1 | 60.2 | 19.8 | 40／84／102 | 36／48／51 | 1689／1957 | 566／398；545／174 |  |

### 6.2 M12 硬门禁 H1–H4 逐项核对（全部通过）

| 门禁 | 门槛要求 | 实测结果 | 结论 |
|---|---|---|---|
| **H1：桌面流正确性、零卡顿、零回读与池化** | 7 组 0 失败、0 次 >250 ms 卡顿、0 未绘制完成帧、正式生产路径 0 CPU 回读、表面池受限与拥有进程全部退出 | 7/7 组「失败」列为空；所有组 `stallsOver250ms = 0`、`undrawnNativeCompletions = 0`、`productionFinalReadbacksZero = true`、`poolBounded = true`、`ownedProcessDrain = true` | **通过** |
| **H2：M07／M08 跨进程与确定性** | M07 像素与跨进程结构一致；M08 多进程像素与结构摘要全部 `equal` | `m07_matrix` 9 次运行 **24/24 BGRA 像素比较**与 **12/12 跨进程确定性**全过；`m07_thresholds` `correctnessPassed = true`，196 项耗时门禁 **194/196 通过**（仅 2 项首轮 p95 微幅波动：`WordEmphasis.prepareSessionMs` 221.03 vs 211.30 ms、`RepeatEmphasis.rendering[0].first` 61.25 vs 56.82 ms，均属非阻塞性能波动）；`m08_matrix` 3 轮 9 进程 **30/30 `equal`** | **通过** |
| **H3：图／材质逐像素检查开／关一致** | 正式关闭与调试开启两种模式像素一致、开启时原诊断保持 | 由全量后端 `Rendering.Tests`（`O4` 开／关对照回归）与阶段六 b／七匹配发布物实跑共同覆盖；正式预览与导出保持关闭中间读回 | **通过** |
| **H4：长时 HTTP 浸泡（≥ 6 min）、FD／RSS／Worker 排水** | 浸泡 ≥ 6 min、Server RSS 后半斜率 ≤ 10 MiB/min、FD 平稳、Worker 并发不突破 2、0 遗留进程 | `author-soak` 运行 **441.74 s**（≥ 360 s）、0 失败；`author-adopt-compiled`（3.026 min）Server RSS `429.5 -> 428.3 MiB`，斜率 **-0.3966 MiB/min**（≤ 10 MiB/min）；Server `fdPeak <= 280`、Worker `fdPeak = 115`、`maxConcurrent = 2`；`export-soak`（41.42 s）与 `seek-soak`（53.79 s）均 `passed`、0 失败；全部运行结束后 `.local/run/` 为空且 0 遗留进程 | **通过** |

### 6.3 M12 目标项 G1–G4 达成情况与例外登记

依据[验收表 M14 行](script-sdk-v2-render-acceptance.md#3-15-个验收节点)与 [M12 记录 §5](script-sdk-render-m12-evidence.md#5-门槛与结构性修复) 的 2026-11-09 用户确认口径，目标项未达标只作原因与差距登记，不阻塞通过：

- **`G1`（720p24 稳定预览：`fps >= 22.8 / 95%`、最差 5 s ≥ 118、Render p95 ≤ 40 ms）**：
  - `checker-M-GlassInk`、`solid-G`、`solid-M-CustomInk`、`solid-M-GlassInk`、`solid-P` 五组 **全部达标**（24.00 fps，最差 5 s 119 帧，Render p95 5.1～22.4 ms）。
  - `solid-C` 720p 完成帧率 `23.72 fps`（98.8%）、Render p95 `13.8 ms`，仅最差 5 s 窗口 `116 < 118`（2 帧波动，0 次 >250 ms 卡顿），按已知机器抖动登记。
  - `solid-X`（7 包 × 72 行全量混合最重场景）720p 完成帧率 **`23.17 fps`（完成比 `0.965 = 96.5% >= 95%`）**、0 次 >250 ms 卡顿；Render p95 **`48.7 ms`（> 40 ms 目标）** 与最差 5 s 窗口 **`111`（< 118）**，与 M12 收口时登记的混合组目标差距一致，按 2026-11-09 用户批准例外原样登记（随 P4 文字优化按需处理）。
- **`G2`（1080p 导出 96 行 ÷ 8 行比值 ≤ 1.25）**：**达标**。`g2-t01-96`（3,600 帧，43.47 fps，`23.01 ms/帧`）对比 `g2-t01-8` 3 轮中位数 `32.08 ms/帧`，比值 **`0.717 <= 1.25`**；`detailed` 遥测比值 **`0.625`**（`RasterMs = 0`、`MaterialMs = 0`、`GraphMs = 0`、`ParticleMs = 0`，`errors = 0`）。
- **`G3`（暂停 ≤ 150 ms、跳转 ≤ 200 ms）**：**全部达标**。最重 `solid-X` 暂停 p95／p99 为 `84 / 102 ms`（≤ 150 ms），跳转 p95／p99 为 `48 / 51 ms`（≤ 200 ms）；其余 6 组暂停 p99 ≤ 30 ms、跳转 p99 ≤ 29 ms。
- **`G4`（`solid-X` 改输入 `latest-input` p95 ≤ 2,000 ms）**：**全部达标**。`solid-X` 改输入 p50／p95 为 `1689 / 1957 ms`（≤ 2,000 ms），其余 6 组 p95 均在 `318～616 ms`。

注：另行采集的 `seek-t01-off`（28.71 s）、`seek-WordEmphasis-off`（15.03 s）与 `author-off`（95.67 s）HTTP 观察项均 `passed`、`failures = []`。

## 7. 结论与远程状态

- **结论**：M14 移除 SDK v1、全量回归（后端 8,350 + 前端 4,490 单元 + 191 集成）、HTTP 合同 `1.53.0` 与双端同步、匹配发布（`0.113.0`）、真实 HTTP／硬件 Vulkan 探针（2,261 请求 / 14,450 断言 + W8 147 请求 / 1,019 断言 + v1 拒绝）、浏览器与桌面 GPU 冒烟、M00 对照矩阵与 M12 硬门禁 H1–H4 全部通过，M12 目标项 G2／G3／G4 及 G1-C/P/G/M 达标、G1-X 按批准例外登记差距。**M14 最终验收通过，阶段二（M12～M14）全部收口完成。**
- **远程与部署说明**：本记录提交在本地分支 `dev/script-sdk-phase-two-structure`（按本次文档工作约定未执行 `git push`）；本提交仅归档验收文档，引用已构建与已验证的 `0.113.0` 发布物与源码提交，不将文档提交充当新的程序部署。
