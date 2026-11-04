# 样式与效果的 HTTP 创作入口

本文原按后端 0.109.0 梳理；辉光接口按后续 0.110.0 工作树源码补充。生成的 [OpenAPI](../../web/openapi.json) 和[接口索引](../../web/api-index.md)以各自冻结版本为准。各接口需要 access 和客户端身份；请求前先按 [Web 接入说明](../../web/README.md) 建立客户端，ID 和修订均使用服务返回的原值。本文只作源码级梳理，不代表本次运行或像素验收。

静态字段及约束见 [静态样式](static-options.md)，动画及场景模型见 [动画和场景预设](animation-scene-presets.md)，包作者 SDK 见 [脚本 SDK](script-sdk.md)。Core 持久模型、模板定义、HTTP 读投影和可写命令不是同一个 JSON：不要直接把源码对象或 GET 的整份结果回传为命令。

## 工程样式和解析

下表路径均以 `/api/v1` 为前缀。工程及客户端的创建、授权、资源上传、保存和异步任务的一般接口见 [完整接口索引](../../web/api-index.md)。

| 方法和路径 | 用途及关键输入 |
| --- | --- |
| GET `/project-sessions/{id}/style` | 精确 `expectedRevision`、`kind` 及对应 ID：`project` 无 ID；`branch` 带 `branchId`；`line` 再带 `lineId`；`token` 再带 `tokenId`。返回 `baseline`、本层 `local` 和 `staticHierarchy`。 |
| GET `/project-sessions/{id}/resolved-style` | 必填 `expectedRevision/branchId/lineId`，可选 `tokenId/clipId/nodeId`。返回实际静态预设上下文 `context`、`style`、逐属性来源 `sources`。 |
| GET `/project-sessions/{id}/preset-applications` | 读取固定在工程中的应用及其目标有效性。 |
| GET `/project-sessions/{id}/orchestration` | 读取场景编排、节点、容器、槽位与片段；同一工程修订下组合其他读模型。 |
| GET `/project-sessions/{id}/orchestration/line-presentation` | 读取单行展示和继承结果；查询参数见 OpenAPI。 |
| GET `/project-sessions/{id}/orchestration/geometry`、`/orchestration/line-local-transform` | 读取几何参考和行局部变换。 |
| POST `/project-sessions/{id}/orchestration/measurements` | 在固定输入和 Worker 支持的来源范围内测量；不能替代最终像素检查。 |
| POST `/project-sessions/{id}/commands` | 提交下述样式、分配和场景命令；外层有 `expectedRevision`、`command` 和回执字段。 |
| GET `/project-sessions/{id}/history` | 查看撤销/重做状态；历史命令也通过工程 commands 调用。 |

`style` 的 `resolution` 为 `staticHierarchy`，只表示 project → branch → line → token 静态继承；`hasPresetApplications` 是全工程提示，并不证明目标被某个预设覆盖。`resolved-style` 为 `staticPresetHierarchy`，包括真实节点路径与适用的固定预设；`finalPixelAppearance:false`，仍不执行动画或脚本，也不是最终像素。前者 [DTO](../../src/MakoKaraGenerator.Backend.Contracts/BackendStyleContracts.cs)、后者 [DTO](../../src/MakoKaraGenerator.Backend.Contracts/BackendResolvedStyleContracts.cs)；查询检查见 [Server](../../src/MakoKaraGenerator.Backend.Server/ServerEndpoints.ResolvedStyle.cs)。

`local` 不含某属性表示继承；`typography.fontFamilyName` 的显式 null 表示选择默认字体，`stroke`/`shadow`/`glow` 的显式 null 表示关闭效果。要移除局部覆盖、恢复继承，使用 `styles.restore`，不是省略 `value` 或写 null。project 级没有父级可恢复。读取时完整样式的字体和可空效果保留 null；包材质是只读身份，`styles.patch` 的材质写入只接受完整 `solid` 或 `gradient`。父字段与其叶字段不能在同一批中重复指定。具体形状和属性到 Application 的映射见 [HTTP DTO](../../src/MakoKaraGenerator.Backend.Contracts/BackendStyleContracts.cs)、[JSON 转换器](../../src/MakoKaraGenerator.Backend.Contracts/StyleCommandJsonConverters.cs)、[编辑用例](../../src/MakoKaraGenerator.Application/Editing/ProjectStyleEditUseCase.cs)及 [值转换](../../src/MakoKaraGenerator.Application/Editing/ProjectStyleEditUseCase.Values.cs)。

辉光写入使用 `glow` 或 `glow.color`、`glow.opacity`、`glow.blurRadiusPx`、`glow.spreadPx`。完整对象例如 `{"color":{"red":255,"green":0,"blue":0,"alpha":255},"opacity":1,"blurRadiusPx":8,"spreadPx":0}`；颜色是四个字节通道的对象，不是十六进制字符串。完整对象的四项均须提供；继承及数值限制见[静态辉光](glow.md)。

工程命令外层示例（占位身份和修订必须换成实际值）：

```json
{
  "expectedRevision": "<project-revision>",
  "requestId": "<unique-request-id>",
  "hostInstanceId": "<host-instance-id>",
  "validUntilUtc": "<future-utc-timestamp>",
  "command": {
    "type": "styles.patch",
    "targets": [{"kind":"line","branchId":"<branch-id>","lineId":"<line-id>"}],
    "changes": [{"property":"typography.fontSizePx","value":80},{"property":"stroke","value":null}]
  }
}
```

要恢复上述覆盖，改用 `command`：

```json
{"type":"styles.restore","targets":[{"kind":"line","branchId":"<branch-id>","lineId":"<line-id>"}],"properties":["typography.fontSizePx","stroke"]}
```

`styles.patch`/`styles.restore` 每次接受 1..1000 个同种、互不重复的目标和非空属性集合，project 只能一个；`changes[].value` 始终必填。新意图上限 64 KiB，变化目标的新投影有 16 MiB 预算；不符合预算整批拒绝。批次原子提交，一次实际变化产生一次历史；no-op 不清 redo。`styleResult.changedTargetCount` 报告变化目标数。具体错误和完整预算见 [行为合同 §6.11](../architecture/backend-api.md#611-四层静态样式读写0830)。

## 模板与工程分配

| 方法和路径 | 用途 |
| --- | --- |
| GET `/template-libraries` | 枚举内置与个人库。 |
| GET `/template-libraries/{id}/templates`、`/templates/detail` | 分页列表和详情；详情可请求完整定义，查询字段见 OpenAPI。 |
| POST `/template-libraries/{id}/commands` | 个人库 `create/replace/copy/rename/delete`，`expectedRevision/metadata/command`；固定源复制需要 `sourceLibraryId/sourceRevision/templateId/name`。 |
| POST `/template-libraries/{id}/imports/preflight`、`/imports`、`/exports` | 模板归档预检、导入、导出；导出结果从 `/template-transfers/{id}/archive` GET/HEAD 读取。 |
| POST `/effect-packages/inspect` | 用授权 `packageAssetId`（可附 `expectedPackageRevision`）或互斥 `reference` 读取包 manifest、exports、parameters、inputs、文件摘要及诊断；检查不编译或执行。 |
| POST `/template-preview-options`、`/template-preview-sessions` | 先按 `source` 获取试样/目标/材质槽，再建立固定试样预览；可用精确 savedTemplate 或 draft 定义。 |
| POST `/project-sessions/{id}/preset-application-previews` | 对精确工程修订和目标创建候选预览，不写工程；正式分配仍须执行命令。 |

模板 `create/replace` 的 `definition` 不是样式 patch：默认静态定义包含 `name/kind/style/properties`；指定 `definitionFormat:"full"` 使用完整定义，额外包含有序 `effectRecipes` 和精确引用的 `packages`。配方字段是 `recipeId/name/target/packageId/revision/exportId/parametersJson/overlayLayer/enabled/readScope/branchInput/fixedSeed/configurationId`；`parametersJson` 是 JSON **文本字符串**，`fixedSeed` 是十进制字符串。完整定义和包引用的严格形状见 [模板 DTO](../../src/MakoKaraGenerator.Backend.Contracts/BackendTemplateLibraryContracts.cs)、[包 DTO](../../src/MakoKaraGenerator.Backend.Contracts/BackendEffectPackageContracts.cs)。`packageClosureDetails` 为只读详情，不能当定义写回。

工程 commands 的预设应用类型：

| `command.type` | 必需业务字段 |
| --- | --- |
| `presetApplications.apply` | `templateLibraryId/templateId/templateRevision/targets`，可选 `branchInputs`。 |
| `presetApplications.update` | `applicationId/templateLibraryId/templateId/templateRevision`，可选 `branchInputs`。 |
| `presetApplications.setEnabled` | `applicationId/enabled`。 |
| `presetApplications.remove` | 不重复的 `applicationIds`。 |

`targets[]` 是以下严格 union：

| `kind` | 必须提供的其他字段 |
| --- | --- |
| `project` | 无。 |
| `branch` | `branchId`。 |
| `line` | `branchId/lineId`。 |
| `token` | `branchId/lineId/tokenId`。 |
| `clips` | `branchId/clipIds`；1..1000 个非空、不重复的片段 ID，均须属于该来源。 |
| `node` | `nodeId/includeDescendants`；后者显式布尔值，nodeId 最多 128 字符。 |

无关字段可省略或为 null，不接受无关非 null 字段、未知字段或重复字段；必要 ID 非空白。具体实现见 [目标转换器](../../src/MakoKaraGenerator.Backend.Contracts/PresetApplicationTargetJsonConverter.cs)。应用固定模板快照，之后修改库不会自动改写工程。模板库自身和工程分别使用各自修订；模板的完整预览不等于正式保存或分配。[服务端路由](../../src/MakoKaraGenerator.Backend.Server/ServerEndpoints.TemplateLibrary.cs)、[应用命令](../../src/MakoKaraGenerator.Backend.Contracts/BackendPresetApplicationContracts.cs)、[预览请求](../../src/MakoKaraGenerator.Backend.Contracts/BackendPresetApplicationPreviewContracts.cs)和 [行为合同 §13、§24](../architecture/backend-api.md#13-独立模板库原0840合同190p3扩展见24) 列出完整规则。

## 场景、动画和效果实例

以下均为工程 `commands` 的 `command.type`；字段写在 `command` 对象内，外层修订及回执仍必填。表中标为“显式 null”的字段必须提供，不能用省略替代；完整对象的成员、枚举和领域限制见 [动画和场景预设](animation-scene-presets.md)、[OpenAPI](../../web/openapi.json) 及对应转换器。这些是工程数据编辑，不是把 Core 动画对象直接 POST 到独立资源。

| `orchestration.` 后缀 | 主要字段 | 效果 |
| --- | --- | --- |
| `setDefaultContainer` | `branchId/containerId` | 设置来源默认歌词容器。 |
| `setLayoutDefaultPlacement` | `containerId/placement`，placement 完整对象或显式 null | 设置或清除内部布局默认位置。 |
| `setNodeGeometry` | `nodeId/placement/referenceRect`，两个对象完整且非 null | 设置节点几何。 |
| `setLayoutSettings` | `containerId/safeArea/maxWidthRatio` | 设置安全区和最大宽度比例。 |
| `setLayoutKind` | `containerId/kind/slotIds/newSlotCount` 均显式 | 切换 Auto 或 SlotSequence；Auto 要求空槽与 0，SlotSequence 总槽数 1..64。 |
| `setSlotAnimation` | `containerId/slotAnimation`，完整对象或显式 null | 设置或清除槽位动画。 |
| `setProgramAnimationPolicy` | `branchId/animationPolicy`，命名枚举或显式 null | 设置或清除来源展示程序动画策略。 |
| `setLineEnterAnimation` | `branchId/lineId/animation`，完整五字段或显式 null | 设置行进入动画；null 恢复继承。 |
| `setProjectEnterAnimation` | `animation`，完整五字段或显式 null | 设置工程默认进入动画；null 关闭默认。 |
| `setLineLocalTransform` | `branchId/lineId/transform`，完整对象或显式 null | 设置或清除单行局部变换。 |
| `previewBasicLayout`、`applyBasicLayout` | `branchId/preset/layoutName/slotNames/selectedSegmentName/retainedSegmentName`；`selectedLineIds` 可省略或 null | 分析或应用基本布局；非 null 行数组指定连续范围，整来源与片段布局均只支持独立计时 Karaoke 来源。preview 不提交工程。 |
| `createNode` | `kind/parentId/name/slotCount` 均显式；parentId、name 可 null | 生成 group、auto 或 slotSequence 节点及 ID；group/auto 的 slotCount 为 null，slotSequence 为 1..64。 |
| `copySubtree` | `nodeId/name`；name 显式且可 null | 同父复制子树并生成 ID，不复制歌词及业务绑定；null 清副本根名称。 |
| `setNode` | `nodeId/name`；name 显式且可 null | 修改或清除节点名称。 |
| `reparent` | `nodeId/parentId`；parentId 显式且可 null | 移动节点；null 表示画布根层。 |
| `reorderChildren` | `parentId/orderedIds`；parentId 显式且可 null | 按完整同级顺序重排子节点。 |
| `removeNode` | `nodeId` | 删除未被引用的子树。 |
| `addSlot` | `containerId/slotId/name`；name 显式且可 null | 向容器添加槽位。 |
| `reorderSlots` | `containerId/orderedIds` | 按完整槽位顺序重排。 |
| `createClip` | `branchId/clipId/lineIds/containerId`；name 可省略或 null | 创建片段；lineIds 为 1..1000 个非空稳定行 ID。 |
| `renameClip` | `branchId/clipId/name`；name 显式且可 null | 修改或清除片段名。 |
| `setClipContainer` | `branchId/clipId/containerId` | 更换片段容器。 |
| `deleteClip` | `branchId/clipId` | 删除片段。 |
| `setClipMembers` | `branchId/clipId/lineIds` | 以 1..1000 个连续唯一行替换成员，保留片段身份和上下文。 |
| `splitClip` | `branchId/clipId/splitBeforeLineId/newClipId` | 在指定行之前拆分，左片保留旧身份。 |
| `mergeClips` | `branchId/clipIds/targetClipId` | 合并至少两个同容器、同上下文且成员连续的片段，保留目标身份。 |
| `createCountdown` | `parentId/name/count`；name 可 null，count 为 1..64 | 创建倒计时容器。 |
| `createLeadIn`、`updateLeadIn` | `branchId/leadInId/name/targetLineId/indicatorContainerId/durationUs/enabled`；name 显式且可 null | 创建或更新手工前奏提示。 |
| `deleteLeadIn` | `branchId/leadInId` | 删除前奏。 |
| `setLeadInGeneration` | `branchId/settings`，完整对象或显式 null | 保存或清除来源前奏生成设置。 |
| `previewLeadInGeneration` | `branchId/settings`，完整设置 | 分析候选，不改变 revision、历史或 redo。 |
| `applyLeadInGeneration` | `branchId/settings/selectedLineIds` | 对显式选择重新分析并原子应用前奏。 |
| `deleteLeadInAndExclude` | `branchId/leadInId` | 删除前奏并从后续生成中排除其目标。 |
| `setLeadInAttachment` | `branchId/leadInId/containerId/mode` | 设置 `FixedParent` 或 `CueTargetText` 附着模式。 |
| `addMark` | `containerId/text` | 添加文字标记；text 非空白且最多 4096 字符。 |
| `setMarkText` | `nodeId/text` | 修改文字标记，文本限制同上。 |
| `reorderMarks` | `containerId/orderedIds` | 按完整顺序重排标记。 |
| `spaceMarks` | `containerId` | 均匀排列标记。 |
| `setCountdownAppearance` | `containerId/appearance`，完整七字段 `typography/pendingFill/elapsedFill/stroke/shadow/opacity/glow`，显式包含可 null 的 stroke/shadow/glow | 设置倒计时文字外观。 |
| `setMarkAppearance` | `nodeId/changes` | 替换完整静态 override；空数组清除，不接受三个几何属性；支持辉光完整对象及叶字段。 |

以上命令分发包含 [普通转换器](../../src/MakoKaraGenerator.Backend.Contracts/ProjectCommandJsonConverter.cs)、[节点创建](../../src/MakoKaraGenerator.Backend.Contracts/NodeCreationCommandJson.cs)、[场景属性](../../src/MakoKaraGenerator.Backend.Contracts/OrchestrationPropertiesCommandJson.cs)、[编辑补全](../../src/MakoKaraGenerator.Backend.Contracts/OrchestrationEditingCompletionJson.cs)、[前奏](../../src/MakoKaraGenerator.Backend.Contracts/LeadInCommandJson.cs)、[前奏生成与标记](../../src/MakoKaraGenerator.Backend.Contracts/LeadInEditingCommandJson.cs)及[片段成员](../../src/MakoKaraGenerator.Backend.Contracts/ClipMembersCommandJson.cs)。

脚本效果实例可以存在完整模板的 `effectRecipes` 或个人脚本配置中。实例的目标、读取范围和覆盖层分别使用 `target`/`relativeTarget`、`readScope`、`overlayLayer`；包的导出类型和 `bindingKinds` 由包派生，不能把用户输入伪装为派生结果。参数的类型、默认值和范围通过包检查的 `parameters` 读取，其中 `defaultValueJson/minimumJson/maximumJson` 是保精度的 JSON 文本。

| 方法和路径 | 用途 |
| --- | --- |
| GET `/script-configurations`、`/{id}`、`/{id}/package` | 列表及库修订、完整配置/包详情、固定包归档。 |
| POST `/script-configurations/commands` | `create/replace/copy/rename/delete`，外层 `expectedRevision/metadata/command`；`create/replace` 提供完整 `configuration`。 |

配置写入包含 `name/exportId/parametersJson/relativeTarget/readScope/branchInput/fixedSeed/overlayLayer/source`。`source` 是草稿固定修订、已有配置固定修订或包引用之一，不能混用；精确分支和字段由 [配置 DTO 与转换器](../../src/MakoKaraGenerator.Backend.Contracts/BackendScriptConfigurationContracts.cs) 校验。个人配置库的修订与工程修订、模板库修订彼此独立。

## 作者草稿、编译与预览

| 方法和路径 | 用途 |
| --- | --- |
| POST/GET `/drafts`，GET/DELETE `/drafts/{id}` | 创建、列出、读取和按 `expectedDraftRevision` 删除草稿。创建支持包 asset 或固定个人配置来源。 |
| GET `/profile/author-drafts`，POST `/profile/author-drafts/{id}/claim` | 发现并按恢复合同认领可恢复草稿。 |
| GET `/drafts/{id}/sources`、`/source-revisions`、`/package-files`、`/package` | 读取固定 C# 源码、历史源码修订、完整包文件描述/manifest、包归档；`sources` 和 `package-files` 查询须带 `expectedDraftRevision/sourceRevision`。 |
| POST `/drafts/{id}/source-revision-jobs` | 以 `expectedDraftRevision/files:[{path,text}]` 保存完整 C# 文件集合，生成不可变源码修订。 |
| PUT `/drafts/{id}/package-files` | 提交 `expectedDraftRevision/manifestJson/textFiles`，其中 `textFiles` 是全部可编辑文本文件；可附完整 `configuration`，与包结构迁移原子绑定。 |
| PUT `/drafts/{id}/configuration` | 按精确草稿修订保存完整 `configuration`：`name/packageId/sourceRevision/exportId/parametersJson/relativeTarget/readScope/branchInput/fixedSeed/overlayLayer`。派生 `kind/bindingKinds` 不接受客户端覆盖。 |
| POST `/drafts/{id}/compilation-jobs`，GET `/drafts/{id}/compilation` | 固定草稿/源码修订编译并读取最近完整编译结果；`executionMode` 可选择同步或异步。 |
| GET `/jobs/{id}`、`/jobs/{id}/compilation-result`，POST `/jobs/{id}/cancel` | 异步编译的状态、固定结果及取消；202 仅表示已接受。 |
| POST `/drafts/{id}/adopt-source-revision` | 用 `expectedDraftRevision/sourceRevision` 显式采用符合门禁的源码修订；保存源码不自动采用。 |
| POST `/drafts/{id}/validation-jobs` | 固定草稿修订与试样运行正式验证；任务状态、取消、`validation-result` 和 `validation-diagnostics` 复用 jobs。 |

作者请求的精确 DTO 见 [草稿合同](../../src/MakoKaraGenerator.Backend.Contracts/BackendAuthorContracts.cs)、[包文件合同](../../src/MakoKaraGenerator.Backend.Contracts/BackendAuthorPackageFilesContracts.cs)、[验证合同](../../src/MakoKaraGenerator.Backend.Contracts/BackendAuthorValidationContracts.cs)；路由见 [Server](../../src/MakoKaraGenerator.Backend.Server/ServerEndpoints.Author.cs)。先读取包文件，按返回的 `editable` 和包内逻辑路径构造完整集合，涵盖 `csharp/shader/shaderProgram/graph/svg/svgResource` 文本；非文本文件字节集合须保持不变。`manifestJson`、`parametersJson` 均为字符串而非嵌套 JSON 对象。包文本保存检查结构和描述，SVG XML／引用与图片语义在作者编译前由正式 validator 校验；保存不代表编译通过。包的编译诊断绑定固定源码修订，旧结果可能标记 `stale`。SVG 失败保留资源错误码与包内路径；该类诊断行列为 0，不能解释为 C# 行列。异步 job 的 `succeeded` 只表示已产生可读编译报告，仍须检查报告的 `succeeded`。草稿认领需同时提供发现时的 `expectedDraftRevision` 和 `expectedOwnershipRevision`。

作者试样复用 `POST /preview-sessions`：source 使用 `kind:"authorDraft"`、`sourceId` 草稿 ID、`sourceRevision` 草稿修订、`authorSourceRevision` 固定配置源码修订及 `templateSample:{sampleId,targetKey,materialSlot}`。通过 preview 的 `input` 更新、`frame-requests` 请求帧、读取诊断和帧内容；创建后来源 kind 保持 authorDraft 且只能更新同一 draft，不能切换为 project 或其他 draft。验证和预览是独立流程；验证 job 接受并不意味着成功，需读取最终状态及结果。具体请求和资源生命周期见 [预览合同](../../src/MakoKaraGenerator.Backend.Contracts/BackendPreviewContracts.cs)及 [行为合同 §21](../architecture/backend-api.md#21-无-gui-作者草稿试样与个人配置库01080合同1400)。

## 修订、回执与错误

工程修改外层使用 `expectedRevision` 和顶层 `requestId/hostInstanceId/validUntilUtc`；模板库与脚本配置命令使用各自 `expectedRevision` 和嵌套 `metadata`。作者写入使用 `expectedDraftRevision` 及相应 `metadata`。不要跨库复用修订，冲突后重新读取并基于新状态构造意图；同一回执身份的成功重放返回原结果，不能将它当作新操作。精确 owner、过期、请求重放与错误码依 [API 行为合同](../architecture/backend-api.md) 和 [生命周期 DTO](../../src/MakoKaraGenerator.Backend.Contracts/BackendLifecycleContracts.cs)。

工程批量编辑在一次成功变化时建立一次历史，真实 no-op 保留 revision 和 redo；候选预览、包检查及静态读取不提交工程历史。个人模板库和个人脚本配置有各自 CAS 和回执，不是工程 undo 历史。输入的缺失、显式 null 和完整对象由每个命令分别定义，不能将 `styles.restore` 的继承语义泛化为所有字段的 null。HTTP 验证、编译、预览和最终渲染具有各自的 Worker 能力与资源限制，静态 DTO 或包结构检查不证明最终输出。
