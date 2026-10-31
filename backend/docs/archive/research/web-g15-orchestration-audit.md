# Web G15 编排缺口审计（2026-10-17）

本审计对照前端设计文档 `MakoKaraGenerator-Web/docs/web-client-w3-design.md` §2.6、§3.1，检查当前后端源码与合同。结论是 **G15 尚未交付**；本批不新增半成品路由或 DTO。

## 已存在的能力

- 四个工程命令已经接入：`presetApplications.apply`、`update`、`setEnabled`、`remove`。入口为 `ProjectSession.ExecutePresetApplicationCommand`，领域校验和单一 Writer 提交由 `PresetApplicationsEditUseCase`、`PresetApplicationsEditor` 完成。
- 应用记录具有稳定 `PresetApplication.Id`、单一 `ProjectEffectBinding Target`、`Enabled`、完整 `StyleTemplate Snapshot`、`BranchInputs` 以及效果实例身份。工程保存／重开保留这些字段。
- `GET /api/v1/project-sessions/{id}/style` 在合同中返回 `hasPresetApplications`，并继续返回静态层级样式；这只能回答“是否存在分配”，不能列出分配。
- 模板库读模型已经有库级 `Revision`（builtin/personal）和模板摘要中的定义哈希，但该修订只存在于模板库读取过程。

## 具体缺口与证据

| G15 要求 | 当前事实 | 证据 |
|---|---|---|
| `GET .../preset-applications?expectedRevision=` | 没有路由、Server capability、Registry 方法或合同 DTO | `ServerEndpoints.ReadModels.cs` 仅注册 branches、lyrics/window、settings、style；`BackendProjectReadContracts.cs` 无分配读模型 |
| 每项返回 applicationId、目标、enabled、快照摘要、branchInputs | 这些字段只在 Core 的 `PresetApplication` 内部存在，未投影为 HTTP 响应 | `Core/Projects/PresetApplication.cs` |
| `targetValid` | 领域构造允许保存失效引用；当前只有渲染/验证时才发现目标问题，没有只读有效性投影 | `PresetApplication` 构造注释明确“对象存在性由事务与诊断检查，不阻止保存失效引用” |
| 判断“库里有新版本” | 应用快照没有保存其来源库修订。`StyleTemplate` 只有 Id/Name/Kind/Style/Properties/recipes/packages；无法将快照与当前库级 SHA 做同一语义比较 | `Core/Projects/StyleTemplate.cs`；`ProjectSessionRegistry.TemplateLibrary.cs` 的 `TemplateRevision` 只针对库快照 |
| 命令返回新 applicationId | `ProjectCommandResponse` 没有 preset application result；执行路径只返回通用 changeSet/历史字段 | `ProjectSession.cs` 的四个 `ExecutePresetApplicationCommand` 分支 |
| `changeSet.areas` 可识别 `presetApplications` | 分配命令使用 `LyricTimingEditUseCase.DescribeChanges(before, after)`；由于歌词以外字段变化，结果保守为 `FullRefresh`，不会产生 `presetApplications` area | `ProjectSession.cs` 分配分支；`LyricTimingEditUseCase.DescribeChanges` |

## 不能安全伪造的部分

不能把模板定义哈希当作模板库修订：模板定义哈希是单模板内容身份，而应用命令校验的是库级 `templateRevision`。在应用记录中补一个“当前读取时的修订”也需要持久化格式、快照编解码、历史/撤销兼容和命令执行路径同步；仅加 GET 会产生无法解释的 `new version` 结果。

同样，不能从 `Target` 的字符串形状推断 `targetValid`。必须按当前歌词来源、行、元素以及未来编排对象的真实身份规则计算，并为 node/clips 保持只读兼容。

## 下一步最小实现边界

1. 在 Core `PresetApplication` 中持久化 `TemplateLibraryId` 与 `TemplateRevision`（应用时由 resolver 一并返回），同步 JSON、历史快照和旧工程迁移规则；高级模板也必须保留来源信息。
2. 新增受精确工程修订门禁的有界 `ProjectPresetApplicationsResponse`，至少包含 `applicationId`、目标 union、`targetValid`、`enabled`、模板快照摘要、来源库/修订和 `branchInputs`。超过统一 1 MiB/20000 项预算时整批拒绝。
3. 为四个命令的成功回执增加可选分配结果（apply 返回新/复用 ID，update/setEnabled/remove 返回受影响 ID），并让 `changeSet.areas` 包含 `presetApplications`；撤销/重做也应产生同一 area。
4. 增加应用/停用/移除/撤销、失效目标、库替换后的修订差异以及旧工程迁移的 Hosting/HTTP 回归，再同步 `api-contract.md`、`api-index.md`、OpenAPI、examples 和 `contract-manifest.json`。

完成以上持久化和合同准备后，前端才可实现 G15 的目标浏览器名称、停用/移除/更新和“有新版本”提示。当前 W3 仍应按设计文档仅提供应用后预览与撤销。
