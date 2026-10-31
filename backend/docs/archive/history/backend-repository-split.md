# 后端仓库拆分记录

日期：2026-10-17

## 归档与历史

新仓库 `/mnt/Data/Program/MakoKaraGenerator-Backend` 从源仓库 `/mnt/Data/Program/NewMakoKaraGenerator` 的提交 `8526b8419597d4fbd6e776fa6adf60460d468fb2` 克隆而来。源仓库已在同一提交创建并推送标签 `archive/backend-migration-20261017-8526b84`，源工作区继续保留，未在源仓库删除任何文件。

目标仓库沿用 `refactor/individual-backend` 分支并设置远程：

```text
https://github.com/Zydr114/MakoKaraGenerator-Backend
```

复制使用 `git clone --no-local`，因此源提交历史、分支和标签仍可从目标仓库的祖先对象访问。后续后端修改只在目标仓库进行。

## 已完成的删除

提交 `52bace2` 删除了仅属于桌面 UI 的内容：

- `samples/TechnicalSpike.App/` Avalonia/SukiUI 编辑器及其资源、视图和 ViewModel；
- `tests/MakoKaraGenerator.Spike.App.Tests/` 桌面 UI 集成测试；
- `tools/ui-suki-compatibility/` Avalonia 兼容性和窗口探针；
- `tools/normal-startup-probe/` 中的桌面启动 hook、启动测试项目和运行脚本；
- solution 中对应的桌面项目引用和 `Runtime.Hosting` 对已删除 UI 测试程序集的内部可见性。

同一目录保留的 `FontStreamProbe.csproj` 是无窗口的 Skia/HarfBuzz 字体流探针。

提交 `5bfb271` 删除了 UI 依赖元数据：

- `Directory.Packages.props` 中的 Avalonia、SukiUI、CommunityToolkit.Mvvm、DynamicLinq 和 Avalonia 主题/控件包版本；
- `docs/licenses/suki-ui/` 许可证副本。

SkiaSharp、HarfBuzzSharp、SoundFlow、CliWrap、Roslyn 和测试包仍由后端使用并保留。

## 保留的后端边界

`Backend.Contracts`、`Backend.Hosting`、`Backend.Rendering*`、`Backend.Server`、`Backend.Worker` 以及 `Application`、`Core`、`Media`、`Export`、`Processes`、`Rendering.*`、`Runtime.Hosting`、`Scripting.*`、`Text`、`Timeline` 和后端测试仍在 solution 中。`web/` 合同、`tools/backend-*` 探针和匹配 Server/Worker 发布入口继续保留。

## 迁移后的最小验收

本次只执行后端定向回归：

- `dotnet build MakoKaraGenerator.Backend.Server.csproj -c Release -m:1 /p:UseSharedCompilation=false`：0 警告、0 错误；
- `dotnet build MakoKaraGenerator.slnx -c Release -m:1 /p:UseSharedCompilation=false`：0 警告、0 错误；
- `BackendPresetApplicationCommandTests`：3/3；
- `WorkerProcessProxyTests`：45/45。

未执行全量测试、前端测试或桌面窗口验收。真实 HTTP 发布探针仍按 `tools/backend-web-host/README.md` 单独运行。
