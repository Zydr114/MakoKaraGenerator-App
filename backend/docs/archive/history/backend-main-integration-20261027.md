# main集成审查与合入记录（2026-10-27）

状态：P3/P4/P5及两批GPU工作已从 `dev/gpu-render` 快进纳入 `main`。产品 **0.106.0**、合同 **1.39.0**、HTTP协议 **1** 保持，当前合同为102路径／113方法／560 schemas、3331例（2483反例）。本次没有生产源码修改、新发布或fresh HTTP；本报告登记Git集成、当前定向回归及已验收发布/留档复核。

## 1. 分支与历史完整性

| 对象 | 固定来源 |
| --- | --- |
| 合入前main／origin/main | `3bd211fd1355e36b0566d1674efe98a9ef30a96d` |
| 合入开发线 | `dev/gpu-render`，`9ab24b0870e3474f56f385d1c9f55f4339c4e8db` |
| 祖先关系 | main是开发线祖先，左右独立提交为0／95 |
| 集成方式 | 快进到 `9ab24b0`，完整保留95个提交，不改写开发历史 |
| 最终开发树 | `07f974a99523dbfcf9ffc22e6c0129a73cda419e`；merge-tree预检exit0，与开发树完全相同 |
| 原main到开发线差异 | 208文件，涵盖P3完整模板、P4作者/配置、P5交接和0.105/0.106两批GPU工作 |

合入预检与完整提交清单位于 `artifacts/main-integration-20261027/pre-merge-state.json`、`commit-list.txt` 和 `merge-tree-preview.json`。后续入口文档和合同manifest更新与生产行为分开；默认分支继续为main，原开发分支保留，不执行分支删除或工作树清理。

## 2. 代码、提交与文档审查

主代理检查GPU设备/生命周期、Worker固定环境与实际核验、预览generation CAS/固定输入/no-op/严格JSON、验证/导出选择和媒体严格解码。并行审查检查P3 full定义/包引用与saved/draft来源边界，P4草稿owner/CAS、固定输入异步编译发布、配置库IO锁与Repository CAS，以及P5来源kind保护。

`targeted-source-review.json` 记录本次定向源码审查，没有识别出阻断合入的问题；这不声明每个历史中间提交均可单独构建。`tree-hygiene-audit.json` 记录最终树无意外桌面项目、编译/媒体/原始证据产物、冲突标记或生产TODO候选。对95提交的26,930条新增行作高置信度凭证模式扫描无命中；这不是对所有形式秘密的形式化证明。差异检查通过。

当前主入口更新为main，并将目标目录当前统计对齐1.39.0。P5、0.105及0.106原验收报告保持当时分支、版本、失败目录和运行结果，不能从新的main入口推导其曾在main重新运行。

## 3. 当前新跑回归

当前定向CPU/GPU回归固定在 `9ab24b0` 下执行，Debug solution构建为0警告／0错误；17条命令全部exit0，15份TRX均为0失败／0跳过／0中止。CPU相关套件通过 **3681/3681**，Vulkan环境套件通过 **1568/1568**，独立 `MAKO_GPU_SMOKE=1` 真实设备测试通过 **1/1**，driver自然退出0。Server 471/471、Hosting 1410/1410、Worker 105/105；测试后168项Worker/Server.Tests/Hosting.Tests引用指纹仍一致。分项结果见 `artifacts/main-integration-20261027/local/final-acceptance.json`、`regression-summary.json`、`commands.json` 与 `matched-debug-references.json`。

这些是受影响项目的定向回归，CPU/Vulkan数字存在项目重叠，不相加为唯一全量测试数；本轮未跑性能、长片或浏览器矩阵。

旧0.106记录中的SDK/SourceLink/Worker复制引用不一致与一次外层143中止保持原归属；audio bytes夹具NullReferenceException及作者排队编译偶发超时虽然后续重跑通过，本次未宣称已修复其根因。当前重跑结果不回写历史失败数，也不升级为浏览器、跨平台、长片或性能验收。

## 4. 当前生产与已验收发布/HTTP留档复核

本节是读取现有产物的复核，不是新发布或重发HTTP。`accepted-evidence-audit.json` 核对本次HEAD与已验收0.106发布：

- 原生产基线 `9506942118efce3f1869e0cbc9e7da1af1869b42`，`sourceDirty=false`；911个生产输入与Git blob和当前树一致。
- 原 `artifacts/render-backend-api-20261026-r1/dist-final1/` 的1022发布文件SHA全部匹配。合入后的生产树仍消费这一已验收行为，不把主分支名称当作发布指纹。
- 原 `http-final2/` 为770请求／5013断言，1680个证据文件的清单/完整SHA一致；原 `http-unavailable-final1/` 为46／343，116文件一致。两组状态passed、Server exit0、remaining均为空。
- 当前生成合同的511个guard文件及4个生成产物与工作树匹配；合入后从当前main重建metadata并重新freeze/check/validate，结果记录于 `artifacts/main-integration-20261027/contract/`。

合入后从当前 `main` 重新构建的 metadata 为0警告／0错误，SHA-256 `407b862940f63868fe80e61f288dc1d8ce129e58d92a5f19d762c50fef208e5b`，与已验收发布导出相同。freeze write/check与原两组HTTP日志离线校验通过：102 paths／113 methods／560 schemas／3331例（2483反例），`http-final2` 检查744份JSON响应／104份成功请求，`http-unavailable-final1` 检查43份响应／13份成功请求，两组均0 unpaired。当前manifest SHA-256为 `4418d96d2528d3ee946e785dcbbbc5a6ae0a721692ab82607a943196971f8925`，独立guard/部署交集审计见 `artifacts/main-integration-20261027/post-merge-contract-audit.json`。该校验复用已封存HTTP，不是新的发布或fresh HTTP。

## 5. 各阶段验收归属

| 阶段 | 原版本和运行结果 | 原依据 |
| --- | --- | --- |
| P3完整模板工坊 | 0.103.0／1.37.0，174请求／1151断言 | [补齐计划§9](../plans/backend-api-completion-plan.md#9-p3-实施记录) |
| P4作者/配置库 | 0.104.0／1.38.0，167／1142 | [补齐计划§10](../plans/backend-api-completion-plan.md#10-p4实施记录) |
| P5交接与来源边界 | 0.104.1／1.38.1，205／1368 | [P5审查报告](../research/backend-api-p5-closure-audit-20261025.md) |
| GPU管线 | 0.105.0／1.38.1；CPU6159/6159、分层Vulkan回归、2376／18405 | [GPU验收报告](../acceptance/gpu-render-acceptance-20261026.md) |
| 显式CPU/GPU API | 0.106.0／1.39.0；770／5013、不可用负例46／343 | [显式API验收报告](../acceptance/render-backend-api-acceptance-20261026.md) |

这些数字各属于原目录及来源，不能相加成为本次main的新跑结果。公开选择仍为 `cpu|gpu`，GPU实际API为Vulkan，默认CPU、失败不回落，历史未记录选择仍未知。现有浏览器、跨平台/驱动、性能/长片、GPU编码、独立Runtime内容oracle及CPU/GPU位级相同结论不因Git集成而增加。

## 6. 未跟踪文件与交付边界

合入前 `tools/backend-project-image-export-probe/` 六个未跟踪文件的路径和SHA已固定在预检快照；合入后逐项核对保留，不纳入本次提交。原始HTTP、bootstrap、媒体和artifacts证据不提交。无需root操作。

合入后工作树仍保留唯一无关未跟踪目录 `tools/backend-project-image-export-probe/` 的6个文件，路径与SHA和合入前快照一致；本次提交不包含它们。文档、合同manifest及本报告待本次提交后推送到 `origin/main`，推送结果由主代理在交付消息中给出；当前报告不提前宣称远端同步。
