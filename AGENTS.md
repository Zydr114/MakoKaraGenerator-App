# AGENTS.md — MakoKaraGenerator-App 开发守则与仓库指南

本文件为在本仓库中协作的 AI 代理与开发者提供仓库基本信息、核心架构约定与开发守则。

---

## 1. 仓库基本信息

**MakoKaraGenerator** 是一款跨平台（Windows x64 / Linux x64）卡拉 OK 歌词动画制作与字幕视频渲染应用，提供桌面端（Electron）与浏览器端运行模式。

- **技术栈**：
  - **后端（`backend/`）**：.NET 10（C#）、SkiaSharp（CPU/Vulkan GPU 渲染）、FFmpeg 媒体/导出管线、Roslyn 动态效果包编译与沙箱 Worker。
  - **前端（`frontend/`）**：React 19、TypeScript、Vite、Vitest、pnpm、CodeMirror 6、React Aria。
  - **桌面宿主（`desktop/` & `scripts/`）**：Electron 45、Node.js（`>=24`）、原生共享内存/纹理预览桥接与进程生命周期管理。
- **关键目录**：
  - `backend/src/`：Core、Application、Rendering.Abstractions、Rendering.Skia、Scripting、Scripting.Compiler、Media、Export、Runtime.Hosting、Backend.Contracts、Backend.Rendering、Backend.Worker、Backend.Hosting、Backend.Server 等核心工程。
  - `backend/tests/`：全部后端单元/渲染/Worker/HTTP 传输集成测试与共享测试夹具（`Shared/`）。
  - `backend/web/`：冻结 HTTP API 合同的唯一真值源（`openapi.json`、`examples.json`、`contract-manifest.json`、`api-index.md`、`api-contract.md`、`frontend-workflows.md`）。
  - `backend/tools/backend-api-contract/`：合同生成与校验工具（`freeze.py`、`validate.py`、`ContractMetadata.csproj`）。
  - `frontend/src/protocol/`：手写 TypeScript 协议类型，必须与 `backend/web/` 严格一致。
  - `frontend/src/editor/`：歌词打轴、画面编排、样式库、外观、预览与导出 UI。
  - `desktop/`、`scripts/`、`tests/`：Electron 桌面壳、统一构建启动入口 `scripts/app.mjs` 与根级启动器测试。
  - `docs/`：架构文档、SDK v2 参考文档与第三方声明。
- **关联仓库**：官方 `.mkfx` 效果包示例位于独立仓库 `MakoKaraGenerator-OfficialMkfxSamples`。

---

## 2. 强制开发守则

### 2.1 每一步开发均须提交（强制）

- **每完成一个逻辑开发步骤**（例如：领域模型与持久化改动、渲染管线改动、后端合同与宿主适配、前端协议与 UI 适配、API 合同冻结与回归测试等），在通过该步骤对应的编译与测试验证后，**必须立即执行 `git commit`**。
- 提交要求：
  - 使用清晰的 Conventional Commits 风格（如 `feat(...)`、`fix(...)`、`refactor(...)`、`test(...)`、`chore(contract)`、`docs(...)`），说明本步改动内容及 Schema / 合同版本变化。
  - 每次提交只包含当前步骤相关的文件，**严禁**使用盲目全量暂存（如 `git add -A`）将无关未跟踪目录（如 `.VSCodeCounter/`、临时日志、`.local/` 产物等）带入提交。
  - 严禁把多个不相关的开发步骤堆到最后不提交或混成一个巨型提交。
  - 除非用户明确要求，否则不要自行 `git push` 或修改远程分支历史。

### 2.2 核心架构边界

- **布局与动画解耦**：布局容器与槽位（如 `Auto`、`SlotSequence`）仅负责几何排布与显示/隐藏调度（`RevealMode`、`MaxVisibleLines`、`ConflictPolicy`、`RetentionPolicy` 等），**不内置任何入场/退场动画或透明度插值**；所有行/逐字/字簇出入场与视觉动效一律由 SDK v2 效果组件与模板实现。
- **严格持久化与 Schema 版本**：修改工程持久化结构或删除字段时，必须递增 `Project.CurrentSchemaVersion`（位于 `backend/src/MakoKaraGenerator.Core/Projects/Project.cs`），并保持 `JsonUnmappedMemberHandling.Disallow` 的严格拒绝策略，不静默吞掉非法或已废弃字段。
- **SDK v2 二进制兼容**：修改 `Scripting` 投影层时，注意保持 `MakoKaraGenerator.Scripting.Abstractions` 公开接口兼容，避免破坏已发布的 `.mkfx` 效果包（如确需破坏性变更，必须先与用户确认）。
- **API 合同单一真值源**：
  - 任何涉及 `Backend.Contracts`、HTTP 路由、命令列表或读写模型字段的改动，必须同步更新 `backend/tools/backend-api-contract/` 与 `frontend/src/protocol/`。
  - 完成后运行 `python3 backend/tools/backend-api-contract/freeze.py --write` 重新冻结 `backend/web/`，并用 `freeze.py --check`、`validate.py`（若环境已装 `jsonschema`）以及 `pnpm --dir frontend run test:unit`（`contract.test.ts`）校验零漂移。
- **桌面构建隔离**：`scripts/app.mjs` 禁止引用外部 `MAKO_BACKEND_REPO`，构建与运行产物统一收敛在 `.local/` 下，不得污染源码树。

### 2.3 验证与变更原则

- 修改代码后，必须运行受影响范围的编译与自动化测试；涉及跨层或合同改动时，应同时验证后端测试、前端类型检查/单元测试、根测试及 API 合同检查。
- 新增或修复缺陷时，优先补充针对性的自动化回归测试。
- 遇到需求歧义或破坏性方案抉择时，先向用户确认，不自行假设或擅自扩充需求范围。

---

## 3. 常用命令参考

### 后端构建与测试

```bash
# Linux 本地 / 无 systemd 媒体呈现环境构建（与 CI 一致）
dotnet build backend/MakoKaraGenerator.slnx --no-restore -p:MakoMediaPresentationEnabled=false

# 运行全部后端测试（跳过已发布包测试与 Vulkan 硬件依赖测试）
dotnet test backend/MakoKaraGenerator.slnx --no-build --no-restore -m:1 \
  --filter "Category!=PublishedServer&Category!=RequiresVulkan" \
  --blame-hang-timeout 90s --blame-hang-dump-type none

# 按单个测试工程快速验证（示例）
dotnet test backend/tests/MakoKaraGenerator.Rendering.Tests/MakoKaraGenerator.Rendering.Tests.csproj \
  --no-build --no-restore --filter "Category!=PublishedServer&Category!=RequiresVulkan"
```

### API 合同冻结与校验

```bash
# 重新生成冻结产物（backend/web/*）
python3 backend/tools/backend-api-contract/freeze.py --write

# 检查合同是否漂移
python3 backend/tools/backend-api-contract/freeze.py --check

# 完整 OpenAPI / examples / manifest 校验（需安装 backend/tools/backend-api-contract/requirements.txt）
python3 backend/tools/backend-api-contract/validate.py
```

### 前端、根测试与启动运行

```bash
# 前端类型检查、单元测试（含 contract.test.ts）与构建
pnpm --dir frontend run typecheck
pnpm --dir frontend run test:unit
pnpm --dir frontend run build

# 根级启动器与桌面运行边界测试
pnpm run test:root

# 构建并启动桌面版（端口默认 5180；后端或原生代码改动时会自动重建）
pnpm run build:desktop
pnpm run desktop

# 浏览器开发模式（前端 Vite + 后端，默认端口 5173）
pnpm run dev
```
