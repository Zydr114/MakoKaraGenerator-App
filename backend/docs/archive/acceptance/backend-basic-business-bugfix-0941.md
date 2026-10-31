# 0.94.1 基础业务错误修复

日期：2026-10-18。代码基线：`54cf920c5de28e397dccd8b323ca385bf70b3c69`。产品补丁版本0.94.1，API合同补丁版本1.24.1，HTTP协议仍为1。原0.94.0／1.24.0的整体能力与验收数字保持历史归属。

## 1. 修复范围

1. `ExportJobRequest.format`的OpenAPI枚举从错误的`transparentMov`修正为现有Server／Worker实际支持的`transparentProRes4444`，另一个合法值仍是`opaqueMp4`。不新增格式别名或编码器。
2. `orchestration.setLeadInAttachment`在精确修订捕获后，按来源、前奏、容器顺序返回稳定领域错误：

| 情形 | HTTP／code | failureCode | field |
|---|---|---|---|
| 来源不存在 | 422／commandRejected | TargetStale | command.branchId |
| 前奏不在指定来源 | 422／commandRejected | TargetStale | command.leadInId |
| 容器不存在、不是倒计时提示组或不是此前奏关联的提示组 | 422／commandRejected | ContainerInvalid | command.containerId |
| expectedRevision已失效 | 409／revisionConflict | 省略 | expectedRevision |

错误路径沿原回执owner，工程／修订／dirty／历史／redo原子不变，原`finally`负责释放测量名额。相同模式的合法附着仍为不要求Worker的no-op；真实附着转换仍使用已有Worker测量链路。

前端缺口核查来源是独立Web仓库`docs/web-client-w4-design.md` §3.7 R8（2026-10-18，1.24.0／0.94.0／54cf920），本补丁没有编辑前端文件。

## 2. 回归与合同

- `LeadInAttachmentErrorTransportTests`通过真实随机loopback Server HTTP检查来源缺失、前奏缺失、缺容器、错误类型容器、无关联的其他提示容器及失效修订。每种错误均重放同一意图并比较完整错误；比较工程摘要和编排投影，确认revision／dirty／undo／redo及event cursor不变；随后同client发送合法附着no-op并重放，验证名额已释放；最终redo验证原历史仍可执行。
- `ExportFormatContractTransportTests`通过真实Server HTTP拒绝三个错误格式，接受canonical透明格式并检查job输入与`.mov`私有candidate路径、同意图唯一job及executor一次调用，随后显式取消并等待终态。该测试使用受信executor接缝，只证明HTTP准入／分发／取消，不证明真实ProRes编码或新像素验收。
- `export_request_fixtures.py`增加6个完整导出请求schema例（3正／3反），包括省略format的默认、两种canonical格式及三个错误值。冻结总数由1913增至1919、反例由1394增至1397；旧例保持不变。`validate.py`为1.24.1继续执行原完整检查，并核对透明枚举及关键正反例，防止生成器或示例被同时漏审。
- 无新增route、DTO或命令：70 paths／77 methods／401 schemas／78命令保持。

## 3. 验证记录

编码完成时只执行静态语法及差异检查。主代理将统一执行正常solution构建、相关Server／Hosting回归、fresh Contracts metadata生成及freeze／validate，并在提交前补入实际结果。本节未记录的构建、运行或浏览器验收不得推定为通过。

首轮正常solution Debug构建0警告／0错误；Server完整354项中353通过、1失败。新增附着测试误将新提示组默认为FixedParent，而原Core创建的默认模式实际为CueTargetText，导致所谓no-op触发真实测量并在无Worker测试宿主返回503。修正测试为从编排读模型读取并核验实际模式后发送同模式no-op，生产行为未因测试放宽。原失败日志及TRX保留在`artifacts/basic-business-bugfix/server-tests.log`及`results/server.trx`，修后结果独立记录。

修后最终实测：原正常solution构建日志[`build.log`](../../../artifacts/basic-business-bugfix/build.log)及修正测试后的[`repair1-build.log`](../../../artifacts/basic-business-bugfix/repair1-build.log)均为0警告／0错误。修后[`server-repair1.log`](../../../artifacts/basic-business-bugfix/server-repair1.log)及对应TRX记录Server完整354／354、退出0；Hosting相关130／130、退出0，两项目合计484个不同测试。首轮353／354失败及原证据保持，不并入修后通过数字。

fresh Contracts metadata生成、freeze write／check及validate均通过；[`validation.json`](../../../artifacts/basic-business-bugfix/validation.json)记录合同1.24.1、70 paths／77 methods／401 schemas／1919 examples（1397反例）。本轮没有`http_log` schema复核；不新增浏览器或真实ProRes像素验收。

## 4. 交接与边界

前端须从同一补丁提交整体同步`web/`冻结合同和`docs/architecture/backend-api.md`，以canonical导出格式发送请求，并按稳定failureCode／field显示附着错误。产品版本改变，Server与Worker需重新匹配发布；启动器参数、stdout合同及HTTP协议无变化。浏览器／用户、完整B3／B4／B5／B6与原性能／长片／GUI／平台验收不由此补丁完成。
