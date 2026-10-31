# Web 模板库与显式分配接续方案

日期：2026-10-16。用户已确认现有前端编辑、撤销重做及已提供接口无误，并明确选择“作为底层能力一并交付，再补模板库与分配”。静态目标样式底层已在 `387bca1`／0.83.0／合同1.8.0交付；本方案接续独立模板库，再接工程内固定分配。主编码交子代理，主代理负责接口裁决、集成审查、实际 HTTP、文档、提交和推送。

状态：**模板库与工程显式分配均已完成有限后端出口**。模板库为产品0.84.0／合同1.9.0，工程分配为产品0.85.0／合同1.10.0；均不等同完整作者、场景或浏览器闭环。Claude 的前端 `3135709`／`docs/web-ui-design.md` 尚待用户审阅，其试样、未保存草稿渲染、临时分配试看、前奏标记外观归属及布局属性迁移等需求作为后续输入，不在本批改变既有模型语义。

## 1. 职责和交付顺序

模板定义不持有工程目标、播放头或时间；保存到应用级库不修改工程历史。分配把不可变模板快照写入工程，由工程的修订和一次撤销历史管理；库改名、修改或删除不自动更新已经分配的工程。直接样式写入仍为底层能力，前端可只提供模板制作和显式分配主路径。

按两个已独立验收的步骤推进：

1. 独立模板库：现有内置只读目录与 Host/profile 的个人目录；静态完整定义新建、整体替换、复制、改名、删除及有界读取。产品0.84.0，合同1.9.0，见实施§41。
2. 工程分配：精确修订读取、四类源目标应用／替换、按应用身份更新、启停和移除；一次批量历史、固定快照、无变化保 redo。产品0.85.0，合同1.10.0，见实施§42。匹配部署已完成 validation/export 的静态 preset Worker 有限 HTTP 验收；公共预览仍按既有隔离边界拒绝含 preset application 的来源，最终公共预览外观另行决定。

完整效果作者、包资源 HTTP、库文件导入导出、模板试样、临时分配试看、场景编排新业务、逐属性最终外观来源及完整 B3/B4 继续独立。不得为迁就本批把高级模板降级为静态定义、把分配烘焙为歌词覆盖，或改动旧工程格式28／歌词16／模板6。

## 2. 第一步候选 HTTP

沿用现有认证、Origin／Host、client 共享回执与错误外壳。库操作为独立 command union，不增加工程 commands 类型。

| 方法／路由 | 候选行为 |
| --- | --- |
| `GET /api/v1/template-libraries` | 只返回 `builtin`、`personal` 两个逻辑库描述（名称、只读），不读个人磁盘入口，因此坏个人库不遮蔽内置库 |
| `GET /api/v1/template-libraries/{id}/templates` | `offset` 默认0，`limit` 默认50、最多100；可选唯一 `expectedRevision`，提供时必须精确匹配；返回同一次固定读取的修订、总数及摘要窗口 |
| `GET /api/v1/template-libraries/{id}/templates/detail` | 唯一必填 `expectedRevision`、`templateId`；ID 在 query 中，保留旧ID的 Unicode、空格及斜杠 |
| `POST /api/v1/template-libraries/{id}/commands` | `{expectedRevision,metadata,command}`，command 为 `create`、`replace`、`copy`、`rename`、`delete` 严格分型；仅个人库可修改 |

revision 为实际索引字节的小写64位 SHA-256；缺失入口为 `missing`，与已经发布的空索引不同。内置目录与单模板修订使用同一个 canonical definition writer 的只读身份编码，不使用修改时间、数组下标或替换假ID生成身份。原个人库 Encode／Save 仍拒绝 `builtin:` 身份。

摘要提供 ID、名称、kind、固定 templateRevision、属性及效果能力，不伪造当前模型没有的持久修改时间。静态 detail 提供完整 canonical style 与原21个 `LyricStyleProperty` 选择；不能映射成0.83的24条 patch路径。高级 detail 明确 `definitionAvailability=advancedUnsupported`、`definition=null`，不把省略参数的 package descriptor 当作完整定义。内置“经典整行”和“逐字强调”保留现有含义。

create／replace 提交完整静态定义；严格检查所有层的 required／null、重复字段、未知字段、数值、枚举和 solid／gradient。完整 style 中即使未被 Properties 选择的 package 材质也不能借静态接口写入。kind 沿用 Line／Token／Composite 原限制，Composite 仍不包含位置和对齐。模板名称沿原工厂 trim，ID不 trim。

copy 指定 `sourceLibraryId`、`sourceRevision`、`templateId`、新名称；服务端捕获源库固定快照后复制完整对象及资源，再在目标库 CAS 提交。它不是跨库分布式原子事务。高级模板允许服务端复制、改名和删除，必须保留完整参数、配方、包和未选材质；高级整体替换明确拒绝。删除不自动清理历史包文件。

## 3. 库存储、并发与预算

Server 使用 `MAKO_BACKEND_STATE_ROOT/libraries/templates/index.json`，与现有 backend profile 持久状态同根。所有持同一 bootstrap 的临时 clients 共享此个人库；clientId 不是用户账号，也不作为持久库所有者。HTTP不接受磁盘路径，不自动导入、覆盖或迁移桌面库。

复用 `TemplateLibraryRepository`／`LibraryCommitLease`：本地和OS提交锁、真实索引 CAS、完整闭包读取、资源先发布／入口最后原子提交。损坏、版本错误、资源缺失和路径链接明确失败，不能当空库覆盖；错误详情不得泄漏绝对路径。列表分页限制响应，不能宣称全库IO也被分页限制。

所有修订和存在性判断放在回执执行 owner 内；相同成功请求在模板删除或库后来改变后仍返回原回执。新ID在 owner 内生成一次。候选结果及完整回执必须在发布索引前预算；no-op 在同一提交锁检查当前修订，不重编码入口，不发工程事件。已接受操作的等待取消、晚期取消及排水沿用现有规则，不能在提交后返回可重试失败再重复写入。

新定义完整 command 意图上限256KiB（不含 metadata），新名称／字体每项最多256 UTF-16 code units、回退链32、单渐变256色标；这些限额不反向限制旧模板摘要或完整服务端复制。每个读响应最多1MiB／20000 JSON值，超限413而非截断；完整回执仍受配置64KiB限制。既有索引16MiB、完整资源256MiB／1024文件／单64MiB保持原预算，不扩大上传配额。

## 4. 第二步实现约束

固定分配沿用 `PresetApplicationsEditor` 和 `SetPresetApplicationsCommand`，纯 Prepare→完整读模型／回执预算→一次 CommitPrepared，绑定 workspace／generation／源工程。库IO在工程 Writer 外完成，随后在 Writer 内复核 expectedRevision，再一次提交已捕获的模板，不重新生成身份。

首批写目标为 project／branch／line／token，均验证完整稳定父子身份；读取保留原六类 binding，包括 node／clips 及失效引用，不伪造其缺失父ID。相同模板更新保应用身份及匹配配方的实例身份／种子／启用状态；不同模板显式替换沿原重建配方与启用语义。移除和停用旧高级或失效记录不能要求重新构建其定义；无关源覆盖、祖先分配和已注册包保留。静态模板的具名输入集合为空，不能据此声称高级输入映射已开放。

渲染新增独立纯静态 preset snapshot profile：保留真实 PresetApplications 记录，通过唯一 Runtime/PresetStyleResolver 解析实际 node／clip／mark 上下文；旧两个 profile 的wire及拒绝分支不改，不把源级 staticHierarchy 改名为最终外观。新profile允许完整可选分析，明确拒绝任何包、配方或非静态材质（包括禁用记录）；保4MiB／200000值／深64预算。Worker capability／旧Worker拒绝、preview与正式验证／导出实际准入必须一起核对，尤其旧 `SourceProfiles.Length==2` 硬门禁，不能仅新增常量。

## 5. 验收及交付

每步正常 solution 零警告错误、相关 Core／Application／Hosting／self-contained Server 和适用 Rendering／Worker 回归；新并发或错误回归做有意义的变异验证。按相同输入发布匹配 Server＋完整 Worker，真正 HTTP 覆盖严格分型、权限／Origin、跨client共享、CAS竞态、回执重试、坏库保护、重启持久、完整定义及高级闭包不变；分配另验一次 undo／redo、库更新不自动生效、显式更新及实际 Worker 像素变化和恢复。

合同、行为源、生成工具、示例与manifest同提交冻结；保存源／部署／证据SHA、完整测试终态和观察进程退出，不合并先前失败或其他批次数字。不代记前端浏览器验收，不恢复全量、性能、GUI、平台、长片及原暂停范围。一步一个提交并推远程，交付邮件包含版本、SHA、匹配发布目录、启动参数是否变化及真实验收边界。
