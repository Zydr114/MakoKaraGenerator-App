# 已保存工程图片背景正式验证有限计划

日期：2026-10-24。产品 **0.100.0**／合同 **1.34.0**／HTTP协议1。本计划定义的已保存Image正式验证有限切片已完成匹配发布fresh HTTP验收，具体证据与边界见下方收口记录及[实施§78](../history/backend-extraction-progress.md#project-image-validation-01000)。

## 有限范围

已保存并由ProjectStore加载到受管 `loaded-input` 闭包的Image主背景，通过既有 `POST /api/v1/project-sessions/{id}/validation-jobs` 进入正式验证。基础来源仅为无预设的 `static-lyrics-scene-v1`、`static-lyrics-scene-analysis-v1` 和直接 `package-lyrics-scene-v1`；保留完整基础snapshot与声明，不剥离不支持的字段后执行。

本批复用validation capability、job、scheduler、进度、报告sidecar、诊断分页、取消/deadline/close、owner/recovery ACL及账本。客户端仍只提交精确工程修订、帧计划和metadata，不提交图片bytes、profile、路径、grant或裸snapshot。图片export、图片measurement、固定预设与图片组合、video/font、模板候选/固定试样、完整B3/B4/B5/B6、浏览器/跨平台及性能长片继续独立。

## 独立身份与预算

| 层次 | 固定身份或上限 |
|---|---|
| validation source profile | `project-image-validation-v1` |
| 私有capability kind | `project-image-validation-capabilities-v1` |
| 私有task kind | `task-project-image-validation-v1` |
| task二进制magic／头长 | `KTI1`／32字节 |
| 图片envelope codec | `project-image-preview-v1` |
| 实际图片decoder profile | `memory-image-bgra-v1` |
| 完整图片envelope作为任务输入 | 4,194,272字节（4MiB−32字节任务头） |
| 嵌套base snapshot | 4MiB及200000 source values原门禁 |
| 编码图片 | 2MiB |
| 图片单边 | 4096像素 |
| 解码BGRA | 16MiB |
| 完整验证报告 | 原256KiB／256 issues／512 occurrences门禁 |

32字节头加完整输入不得超过4MiB Worker payload。图片preview的24字节open头及4,194,280字节envelope上限不适用于此任务；复用envelope codec不代表复用preview预算。完整envelope预算包含base64扩张、全部基础snapshot及背景身份。未知/重复字段、错误magic/profile/版本、预算或固定身份不符均拒绝，不截断、不缩图。

Worker能力必须同时匹配当前engine、上述独立validation profile与decoder、恰好三个基础profile、图片/输入/base snapshot/报告预算、package diagnostics版本及完整package capability/refs。预览图片能力不证明正式验证能力；能力公布与每个实际任务Worker重新协商分别核对，失败不回退Host native或原静态验证。

## 两段Writer捕获与所有权

第一次进入工程Writer，核owner、注册中的session对象身份、精确revision及ClosePending/关闭状态；固定snapshot、短期图片读引用与既有音频资源引用。此临界区不执行文件IO、图片头读取、native decode、Worker调用或脚本编译。

离开Writer后只读取固定的受管内容地址；核全部路径无链接、完整size与SHA，再编码完整envelope并核任务输入预算。读完或放弃立即归还短期图片读引用；工程关闭等待该引用排水后清理loaded-input。成功固定bytes后Worker不再读取原工程路径，不新建图片staging副本。

准备完成后第二次进入Writer，必须重新核当前client owner、session仍为注册中的同一对象、精确revision和ClosePending/关闭状态，之后才取得scheduler reservation并登记唯一validation job。图片读取期间发生改修订、会话移除/替换、owner失效或close，不得提交陈旧任务。二次检查是接受任务的门禁，不能用第一次捕获时已通过替代。

读取拒绝/异常、SHA或链接不符、envelope编码/预算失败、二次门禁拒绝、配额不足、StartJob拒绝以及取消路径都释放未转移的图片读lease、音频reference与scheduler reservation；各Dispose按既有幂等所有权规则执行。仅任务接受成功才将固定bytes、音频引用与reservation转交work；work在真正Worker/native排水后释放，不能以job终态或HTTP202提前证明资源已归还。

## Worker执行与报告

任务封包绑定task ID、完整envelope SHA与帧计划；Worker再次核预算/关联、解码envelope并恢复完整Image身份。每个Runtime session独占内存resolver/native bitmap，通过原ProjectRenderSessionFactory及RuntimeFrameValidator执行普通/透明帧计划。正常、编译/准备失败、损坏图片、取消、deadline和dispose都沿原工厂所有权链释放；不发布黑底替代成功。

报告复用完整issues/diagnostics及既有帧计数、sidecar bytes/size/SHA和读取身份。图片解码/尺寸预算分别保留稳定 `PackageImageDecodeFailed`／`PackageImageBudgetExceeded`；任务执行失败、编译或Runtime Error、取消/deadline按最终实现与合同明确区分，不制造成功报告。完整报告超预算整体拒绝，不截断诊断。正式validation报告不包含PNG产物，不能以报告通过代称图片export或公共像素验收。

## 验收与收口顺序

1. 完成独立能力/封包、Host两段Writer捕获与所有失败释放、实际Worker原Runtime执行和报告恢复读取；先用本地定向回归证明每层接缝，包括错误decoder/profile、8字节preview/validation预算差、二次门禁竞态与lease排水。
2. 从稳定源码更新0.100.0／1.34.0，生成并检查合同、合成正反例及指纹；本地构建/回归与合同结果分别留档，不推算为HTTP验收。
3. 从已提交clean生产基线匹配发布self-contained Server/Worker，固定发布manifest；独立Oracle只构建工具并引用同一不可变Worker依赖，先核完整依赖字节身份。
4. 新建fresh HTTP证据目录，证明能力公布与不支持Worker拒绝、三基础源普通/透明帧计划成功、完整报告/诊断读取及bytes/size/SHA、错误/预算/ACL/精确revision、SSE、cancel/deadline/close/shutdown及实际Worker/child排水；故障注入只作用于探针自有fixture并明确标记内部边界。
5. 保留全部失败目录与旧预览证据归属；终态、发布/证据SHA、观察身份排水和凭证扫描独立复核后，才在实施/API/入口补录精确有限范围与实际计数。无证据不宣称restart/recovery、音频引用关闭竞态、图片像素或其他未跑矩阵完成。

## 已完成的有限验收（2026-10-24）

功能源码`eb54f8d`、合同`7a03863`、独立工具`7543436`分层登记；匹配发布使用`7a03863`，不能把后续入口文档提交`e170926`当作发布源码。本地去重242/242与二次Writer门禁变异4红／恢复4绿分列，合同保持87路径／97方法／481 schemas／91命令、2873例（2188反例）。

匹配self-contained Server/Worker发布`dist-7a03863`（源码`7a03863`、`sourceDirty=false`）的fresh HTTP `http-final1`通过1412请求／10582断言；两次Server exit0，33个观察身份（含26个Worker）全部退出、无强制清理，3349个文件凭证扫描通过。发布manifest固定859个生产输入与1006个发布文件；证据根为`artifacts/project-image-validation-01000-acceptance/`，HTTP manifest SHA-256为`89b55cb0badb98d611d892e73ad8f64c66dec7afa7529eddfeaf3e96ee511f1a`。三基础源普通／透明各4帧、原Runtime报告Oracle、报告/诊断/ACL/revision/SSE、输入/报告预算、错误/固定接受bytes、queued/running与六组实际失控控制以及一次orderly restart/recovery通过。图片损坏/尺寸/decoded三组按实际报告核验，不冒称Oracle匹配；正式validation没有PNG，报告比较不构成像素验收。旧0.99 Worker独立负能力边界10请求／476断言、199文件另计，实际拒绝503 `taskUnavailable`／`WorkerImageValidationUnavailable`。

该有限验收不关闭图片export/measurement、preset/video/font、完整B3/B4/B5/B6、浏览器/跨平台、性能长片或完整工程；完整范围、终态排水及证据字段见实施记录。

实施记录见[§78](../history/backend-extraction-progress.md#project-image-validation-01000)，公共行为见[统一API§10.1](../../architecture/backend-api.md#project-image-validation-api)，决策见[ADR-0237](../decisions/decisions.md#adr-0237)。
