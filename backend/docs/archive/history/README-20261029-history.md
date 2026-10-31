# MakoKaraGenerator Backend

这是 MakoKaraGenerator 的无窗口后端仓库，提供工程领域模型、歌词与时间编辑用例、媒体准备、Skia 渲染、脚本运行、导出、Worker 和 HTTP Server。桌面 Avalonia 编辑器已从本仓库移除，桌面产品暂时保留在源仓库中。

当前主分支为 `main`，已合入 `dev/performance-optimization` 的两批性能优化成果，产品 **0.107.0／合同1.39.0／HTTP协议1**。已接入内部拥有图像的颜色匹配直接读回、同对象无输入作者图共享及[统一性能日志](docs/backend-performance-monitoring.md)，默认周期摘要，可配置详细帧与脚本统计。零警告构建、4045次相关测试执行、匹配发布和两种日志模式的有限fresh HTTP已通过；用户授权恢复后，正式七标签性能矩阵已通过：图／材质／组合Vulkan中位降低64.89%／73.82%／73.61%，材质／组合主矩阵最大346.453／373.706ms；首帧及独立监控对照仍有超过500ms样本。见[本批验收记录](docs/backend-gpu-structural-performance-optimization-acceptance-20261028.md)；[第一批报告](docs/backend-gpu-performance-optimization-acceptance-20261027.md)保留原性能证据归属。

公开接口保持：可通过 `GET /api/v1/render-backends` 查询设备可用性，为预览、正式验证、导出和测量显式选择 `renderBackend:"cpu"|"gpu"`，通过 `PUT /api/v1/preview-sessions/{id}/render-backend` 切换既有固定预览。GPU使用Skia Ganesh/Vulkan；创建/任务省略或null使用启动默认，已有预览更新省略或null保留选择，GPU失败不回落CPU。详见[统一API§25](docs/architecture/backend-api.md#render-backend-selection)。

main于2026-10-28从 `1b2d375` 快进至 `77643f8`，完整保留本性能分支的19个提交，见[本次合入记录](docs/backend-main-integration-20261028.md)。2026-10-27原GPU开发线95个提交的集成仍见[历史记录](docs/backend-main-integration-20261027.md)。0.105管线和0.106.0显式API的HTTP、硬件和媒体证据分别保留[原GPU报告](docs/gpu-render-acceptance-20261026.md)与[原API报告](docs/render-backend-api-acceptance-20261026.md)归属，不升级为本版本新跑结果。浏览器、跨平台、长片与GPU编码继续独立。

当前分支的项目依赖、运行链、模块说明、代码行数及冗余/测试有效性分析见 [2026-10-24 架构审计](docs/backend-structure-audit-20261024.md)，包含全部 18 个生产项目的 [Mermaid 原图](docs/backend-structure-20261024.mmd)。该审计区分代码实现、本地测试和完整产品验收。

继续补齐后端接口、桌面功能覆盖矩阵和分阶段 API 变动日志见 [后端 API 功能补齐计划](docs/backend-api-completion-plan.md) 与 [完整目标 API 目录](docs/backend-api-target-catalog.md)。目录明确区分当前冻结接口和待实施提案。

## 迁移基线

- 源仓库：`/mnt/Data/Program/NewMakoKaraGenerator`
- 源分支：`refactor/individual-backend`
- 源提交：`8526b8419597d4fbd6e776fa6adf60460d468fb2`
- 源归档标签：`archive/backend-migration-20261017-8526b84`
- 当前产品版本：`0.107.0`（单一来源：`Directory.Build.props`）
- 当前HTTP合同：`1.39.0`（HTTP协议 `1`；102路径／113方法，统计与最终源码指纹以[冻结合同](web/api-contract.md)为准）。CPU/GPU可用性和固定预览切换保持，所有公开渲染入口共用选择语义；此前0.106.0有限fresh HTTP及GPU不可用负例见[原验收报告](docs/render-backend-api-acceptance-20261026.md)，本批证据见[优化报告](docs/backend-gpu-structural-performance-optimization-acceptance-20261028.md)。P1/P2、P3、P4和P5的实现与有限运行证据保留各自版本归属，见[补齐计划](docs/backend-api-completion-plan.md)与[P5审查报告](docs/backend-api-p5-closure-audit-20261025.md)。浏览器、跨平台分发与性能矩阵继续独立。

新仓库保留源仓库的完整 Git 提交历史；后续后端提交从上述归档提交继续演进。源仓库不随本仓库的 UI 删除而修改。

## 主要项目

- `src/MakoKaraGenerator.Backend.Contracts`：HTTP、Worker 和任务合同。
- `src/MakoKaraGenerator.Backend.Hosting`：会话、资源、SSE、任务和 Worker 宿主基础设施。
- `src/MakoKaraGenerator.Backend.Rendering`、`Backend.Rendering.Skia`：后端快照、静态预览和 PNG 传输。
- `src/MakoKaraGenerator.Backend.Server`：无窗口 ASP.NET Server。
- `src/MakoKaraGenerator.Backend.Worker`：受管理的任务 Worker。
- `src/MakoKaraGenerator.Application`、`Core`、`Media`、`Export`、`Processes`：共享领域和后端用例。
- `src/MakoKaraGenerator.Rendering.*`、`Runtime.Hosting`、`Scripting.*`、`Text`、`Timeline`：渲染、运行时、脚本、文本和时间模型。
- `web/`：当前 HTTP 合同、OpenAPI、例子和指纹。
- `tools/backend-web-host/`：匹配 Server/Worker 发布与本机 HTTP 验证入口。

`src/` 和后端测试不引用 Avalonia 或 SukiUI。`samples/TechnicalSpike` 仅保留无窗口的旧渲染验证；`samples/ScriptEffects` 保留脚本样例。

## 构建与定向测试

需要 .NET SDK `10.0.112`（见 `global.json`）以及媒体测试所需的 FFmpeg/字体环境。

```bash
dotnet build src/MakoKaraGenerator.Backend.Server/MakoKaraGenerator.Backend.Server.csproj \
  -c Release -m:1 /p:UseSharedCompilation=false

dotnet test tests/MakoKaraGenerator.Backend.Hosting.Tests/MakoKaraGenerator.Backend.Hosting.Tests.csproj \
  -c Release -m:1 --no-restore
```

合同工具和真实 HTTP 探针位于 `tools/backend-*`；运行完整 HTTP 验收前，使用 `tools/backend-web-host/README.md` 发布匹配的 Server 与 Worker。当前任务只要求后端定向回归，不自动恢复全量或前端测试。

## 当前能力与实施入口

前端从 [Web 交接入口](web/README.md)、[冻结 API 清单](web/api-index.md)、[OpenAPI](web/openapi.json) 与[行为合同](docs/architecture/backend-api.md)读取当前调用依据。CPU/GPU选择与前端流程见[实施计划](docs/backend-render-backend-api-plan.md)及[调用速查§5.3](web/frontend-workflows.md#53-cpugpu选择和已有预览切换)。P1/P2、P3、P4 实现和运行证据分别见[补齐计划§8～§10](docs/backend-api-completion-plan.md#8-p1p2-实施记录)，P5目录与合同审查见[P5报告](docs/backend-api-p5-closure-audit-20261025.md)。下方 G20-G24 数字保留其 2026-10-18 历史归属。

原始任务表定义的G20-G24后端范围已整体关闭，生产源码`c50516d`已推送：G20结构联合流程；G21布局kind/project enter animation；G22真实Worker time/layout同帧geometry+PNG；G23完整生成/选择应用/mark/静态Node模板/真实测量附着；G24 line enter覆盖/继承及单句变换。详见[整体计划](docs/backend-g20-g24-completion-plan.md)、[实施§57](docs/backend-extraction-progress.md#g20-g24-completion-0940)及[审计§9](docs/backend-capability-gap-audit.md#9-g20-g24-后端整体补齐更新原定义后端范围已验收关闭)。

正常solution零警告/错误，四模块1677项加另一Rendering项目定向40项共1717/1717无重复。独立匹配发布HTTP73请求/781断言，6实际测量、8成功job终态、11观察身份含8Worker全退出；Server模块内含真实动画/几何/PNG及完整10帧透明纯视频两时刻oracle。提交前dirty发布输入已逐SHA对齐产品Git blob，详情及失败过程保留于实施记录。

G19成员扩缩拆合、G25、独立时钟/任意轨道不是原任务前置。完整公共B3/B4、B5/B6、浏览器/用户及原性能/长片/GUI/平台验收保持独立；本轮关闭不代表完整Web产品或全功能独立后端完成。

## 当前合同入口

- [`web/README.md`](web/README.md)
- [`web/api-contract.md`](web/api-contract.md)
- [`web/openapi.json`](web/openapi.json)
- [`docs/architecture/backend-api.md`](docs/architecture/backend-api.md)
- [`docs/backend-extraction-progress.md`](docs/backend-extraction-progress.md)
