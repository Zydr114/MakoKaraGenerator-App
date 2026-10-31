# MakoKaraGenerator Backend

这是 MakoKaraGenerator 的无窗口后端仓库，提供工程领域模型、歌词与时间编辑、媒体准备、Skia 渲染、脚本运行、导出、Worker 和 HTTP Server。桌面 Avalonia 编辑器已从本仓库移除，不能把桌面启动或 GUI 验收当作后端运行前置。

当前工作基线为 `chore/repository-hygiene-20261029`，产品 **0.108.0**，HTTP 合同 **1.40.0**，HTTP 协议 **1**。本轮修复 W8 作者工作流中的草稿恢复、包文件编辑、源码历史、包下载、能力判断和模板错误定位；实现与有限验收见[W8 修复记录](docs/backend-w8-authoring-fixes-20261030.md)。浏览器、跨平台、长片和性能分别验收。

## 当前入口

- API 交接：[web/README.md](web/README.md)
- 冻结执行合同：[web/api-contract.md](web/api-contract.md)
- API 清单和 OpenAPI：[web/api-index.md](web/api-index.md)、[web/openapi.json](web/openapi.json)
- 行为源：[docs/architecture/backend-api.md](docs/architecture/backend-api.md)
- 合同生成与校验：[tools/backend-api-contract/README.md](tools/backend-api-contract/README.md)
- 发布和启动：[tools/backend-web-host/README.md](tools/backend-web-host/README.md)
- 文档目录：[docs/README.md](docs/README.md)
- 本轮计划、映射和检查：[docs/repository-hygiene-20261029.md](docs/repository-hygiene-20261029.md)、[docs/archive/README.md](docs/archive/README.md)

这些合同文件必须来自同一提交，并按 [web/contract-manifest.json](web/contract-manifest.json) 校对指纹。合同形状、代码回归、匹配部署、fresh HTTP、浏览器、平台和性能是独立证据；`202` 表示请求已接受，不表示任务已完成。

## 项目边界

生产项目包括 Contracts、Hosting、Server、Worker，以及 Core、Application、Media、Export、Processes、Timeline、Text、Rendering、Runtime 和 Scripting 相关模块。`src/` 与后端测试不引用 Avalonia 或 SukiUI；`samples/ScriptEffects` 保留脚本样例，`samples/TechnicalSpike` 仅保留无窗口的历史渲染验证。

公共 DTO/SDK、Worker IPC、owner、精确 revision 与 CAS、资源租约、预算、取消和排水语义必须保持。渲染改动须核对预乘 BGRA、诊断、状态重放和像素；编码需要 straight alpha 时显式转换，Skia 局部绘制先 `ClipRect` 再 `Clear`。

## 构建与测试

需要仓库锁定的 .NET SDK `10.0.112`，以及媒体测试使用的 FFmpeg/FFprobe 和字体环境。完成代码变更后先执行零警告构建，再运行与变更范围相称的后端定向测试：

```bash
dotnet build MakoKaraGenerator.slnx
dotnet test MakoKaraGenerator.slnx
```

新增缺陷回归应先确认旧实现或变异版本失败，再确认修复后恢复通过；编译失败或零测试执行不能记为测试通过。运行进程只按本次拥有的 PID/startTicks 清理，不能按名称 `pkill`，也不能操作仍可能使用的旧兄弟项目 `/mnt/Data/Program/MakoKaraGenerator`。

## 历史与限制

历史 ADR、路线图、开发文档、计划、调研、验收和桌面 UI 材料保存在 [docs/archive](docs/archive/README.md)，没有删除原版本证据。完整旧根入口见 [README-20261029-history.md](docs/archive/history/README-20261029-history.md)。历史报告的源码、发布、HTTP、浏览器、平台和性能数字保持原归属，不能直接升级为当前版本结论。

当前版本与合同的单一来源分别是 `Directory.Build.props` 和 `web/contract-manifest.json`；历史文档中的版本、分支和待办只用于追溯。
