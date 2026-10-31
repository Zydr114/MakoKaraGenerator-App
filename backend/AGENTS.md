# AGENTS.md — 后端仓库工作入口

本仓库只维护无窗口后端；桌面 Avalonia/SukiUI 编辑器已从本仓库移除，不能将其
启动或桌面测试作为后端验收前置。仓库边界与历史来源见
[拆分记录](docs/archive/history/backend-repository-split.md)。完整旧入口保存在
[历史 AGENTS](docs/archive/history/AGENTS-20261029-history.md)，其中旧分支、GUI
命令和已结束批次仅作为历史记录。

当前产品版本以 `Directory.Build.props` 为准；HTTP 合同版本以同一提交的
[manifest](web/contract-manifest.json) 与 [OpenAPI](web/openapi.json) 为准。
当前工作与证据先读 [README](README.md)、[Web 交接](web/README.md)、
[API 行为合同](docs/architecture/backend-api.md) 和相关计划，不以历史报告的
版本、提交或运行结果推定当前验收状态。

## 工作与交付

- 开工先检查当前目录、分支、工作树、相关源码和现有文档；保留无关修改和未跟踪文件。
  未经授权不删除用户数据、切换或合并其他工作分支，不委派子代理。
- 不操作仍可能使用的旧兄弟项目 `/mnt/Data/Program/MakoKaraGenerator`。清理本次启动的
  进程时核对所拥有的 PID 和 startTicks，不能按名称 `pkill` 或终止无关进程。
- 每步一个提交，按路径暂存；有远程时同步推送当前工作分支。提交信息采用中文
  `type(scope): 摘要`，记录必要的缘由与验证。
- 本机代理是 Clash Verge Rev 的 `127.0.0.1:7897`，loopback 直连。需要 root 权限时
  将具体命令交用户执行。
- 程序版本单一来源是根目录 `Directory.Build.props` 的 `VersionPrefix`；交付程序修改时
  按语义递增版本（普通修复 PATCH，1.0 前不兼容修改 MINOR）。纯文档和回归验收不占
  程序版本。合同独立版本化，产品元数据、行为源和生成物必须同步。
- 公共语义、数据格式、渲染结果或完成状态变化时同步文档；重要架构与数据决策登记
  [ADR](docs/archive/decisions/decisions.md)，补充相关回归。JSON 结构变化提升相应
  schema 并测试当前格式；不追加旧工程自动迁移，遵守当前版本加载门禁。

## 架构与数据约束

- 保持领域层与界面解耦；`src/` 和后端测试不得引入 Avalonia/SukiUI。渲染不修改
  工程源数据，预览与导出共享时间采样和渲染规则。
- 内部时间使用 `long` 微秒及半开区间 `[startUs, endUs)`；校验只报告问题，不静默
  修正用户数据。不可变编辑在无实际变化时返回原实例，维护引用判脏与 no-op 历史语义。
- `project.json` 和其 `lyricsFile` 指向的歌词文件使用独立 schema 版本；先写不可变
  歌词文件，最后发布工程入口。测试使用当前 schema 常量，避免硬编码版本号。
- `src/` 公共成员有 XML 文档；构建零警告。依赖版本由仓库配置固定，不顺手升级。
  使用整行 HarfBuzz 整形结果和共享字形布局；`SKShaper.Result` 的 codepoints 是字形 ID，
  clusters 为 UTF-8 字节偏移，不能按 UTF-16 下标或通过 `ContainsGlyphs` 猜测回退。
- 保持公开 DTO/SDK、Worker IPC、输入输出预算、owner、精确修订与 CAS、资源租约、
  取消及排水等已有合同；渲染修改应核对像素、诊断、重放与资源寿命。
- 帧数据为预乘 BGRA；编码需要 straight alpha 时显式执行正确转换。Skia 局部绘制在
  `Clear` 前先 `ClipRect`，避免擦除不属于当前区域的内容。有关编码陷阱见
  [历史入口 §7](docs/archive/history/AGENTS-20261029-history.md#7-已知缺陷陷阱与小坑)，
  其中桌面 UI 专属事项只用于历史参考。

## 合同与验证

- 同一提交的 [执行合同](web/api-contract.md)、[API 清单](web/api-index.md)、
  [OpenAPI](web/openapi.json)、[示例](web/examples.json)、manifest 与行为源共同构成
  前端依据。候选设计不能冒充已经实现的公共 API。
- HTTP 行为变化同步实现、回归、行为源、工具映射和示例，使用
  [合同工具](tools/backend-api-contract/README.md) 从新构建的 metadata 生成与校验；
  不手改生成产物。合同指纹是漂移警报，不证明行为或部署正确。
- 执行与变更范围相称的构建、定向测试和实际工作流验证；需要 Worker 的测试确保对应
  构建产物匹配。面向 HTTP 的交付还需匹配 Server/Worker 发布及 fresh HTTP 证据，
  按实际需要登记进程排水和凭证扫描。静态合同、测试、运行、性能、浏览器和平台
  验收分别报告；`202` 表示接受而非完成。不得把历史证据归于新版本。
- 完成代码变更后运行 `dotnet build MakoKaraGenerator.slnx`，要求零警告、零错误；
  按当前后端范围执行相关定向测试。新增缺陷回归应确认旧实现或对应变异先失败，
  再恢复修复后通过；编译失败或未执行测试不能记为测试通过。
- 构建入口见 [README](README.md)，发布和启动见 [backend-web-host](tools/backend-web-host/README.md)。
  不为纯文档变更虚报构建、运行或性能结果。

## 前后端协作

本仓库负责后端，独立 Web 仓库负责客户端和浏览器验收；后端拥有工程内容、修订和
历史，前端管理草稿、选择、播放头与显示状态。双方以同一后端提交的完整合同同步，
记录来源 SHA 和版本。新接口先说明场景、输入输出、稳定 ID、修订、历史、no-op、
冲突、预算和验收；mock 或候选合同需明确标记。启动器或发布要求变化时在交付中说明。
协作消息、身份与 reservation 按用户授权和实际工作区使用；凭证、bootstrap、token
及私人 profile 不进入文档或提交。历史协作细则见
[旧入口 §8.4](docs/archive/history/AGENTS-20261029-history.md#frontend-backend-collaboration)。
