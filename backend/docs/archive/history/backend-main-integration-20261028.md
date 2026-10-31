# 性能优化开发线合入main记录（2026-10-28）

状态：按用户授权，将 `dev/performance-optimization` 的两批性能优化、结构二次调研、统一性能日志及验收记录快进纳入 `main`，完整保留19个提交。主分支产品由0.106.0更新为 **0.107.0**，公开合同 **1.39.0**、HTTP协议 **1** 保持。本次仅Git集成、入口文档和静态核验；原构建、运行测试、发布、HTTP及性能采样保留开发分支验收来源。

## 1. 分支与集成方式

| 对象 | 固定结果 |
| --- | --- |
| 合入前main／origin/main | `1b2d37513f3307958f652b7392ce2176c1c4ee61` |
| 开发分支／origin/dev/performance-optimization | `77643f8a2096e52038f333e0b66ff8d101a076b6` |
| 祖先关系 | main是开发分支祖先，左右独立提交0／19 |
| 集成方式 | `git switch main` 后 `git merge --ff-only --no-stat dev/performance-optimization` |
| 快进后的树 | `a8a7e6d427db35fd6bc000be4094683fc5eb464f`，与merge-tree预检及开发树相同 |
| 原main到开发树 | 85文件，63902行增加／228行删除，包含代码、工具及较大的JSON证据索引 |

合入前已从远程获取main和开发分支，两组本地／远程SHA一致，受跟踪工作区干净，main没有绑定其他工作树。预检、完整19提交清单及合入核验保存在 `artifacts/main-integration-20261028/` 的 `pre-merge-state.json`、`commit-list.txt`、`accepted-evidence-audit.json`。

随后独立提交本次合入记录和入口更新；该文档提交不改生产、测试、工具或生成合同。开发分支保留在 `77643f8`，不压缩、变基、删除分支或清理其他工作树。最终main记录提交及远程同步结果在交付消息和本地 `post-push-state.json` 中登记。

## 2. 纳入主线的成果

| 阶段 | 主要提交 | 范围与依据 |
| --- | --- | --- |
| 第一批优化 | `33b9cc5`、`feb7f23`，收口 `61ee27e` | 内部不可变图像成功预乘验证去重、无ruby部件判空读回省略、有界临时缓冲复用；[第一批验收](../acceptance/backend-gpu-performance-optimization-acceptance-20261027.md) |
| 结构二次调研 | `10bad12` | 定位BottomLeft＋sRGB图像的重型raster读回，保留隔离候选与TopLeft负例；[调研报告](../research/backend-gpu-structural-performance-research-20261028.md) |
| 第二批渲染优化 | `18f8fae`、`d8fe6db`、`e7767f1` | 受限同设备颜色匹配直接读回、同对象无输入作者图共享及原子预算登记；[实施计划](../plans/backend-gpu-structural-performance-optimization-plan-20261028.md) |
| 统一性能日志与工具 | `76fa698`、`86a4ff5`、`beee0c3` | 默认summary／可选off或detailed，有界帧与脚本计量、进程资源心跳、限速与关闭排水，Server将Worker事件作为不可信遥测转发；[监控说明](../../backend-performance-monitoring.md) |
| 版本、合同与验收 | `5802ac1`、`103ba2c`、`35d1b5b`、`77643f8` | 产品0.107.0、匹配发布、正确性／HTTP与正式性能矩阵；[第二批验收](../acceptance/backend-gpu-structural-performance-optimization-acceptance-20261028.md) |

原BottomLeft、同步、完整像素检查、逐目标coverage／透明度／错误顺序、预算和设备租约语义保留。公开接口及CPU默认、GPU失败不回落的行为不因本次集成改变。

## 3. 本次静态核验

合入后的915个生产输入逐项通过SHA-256核对，并且Git blob与最终生产基线 `5802ac146fcaa89ad882229b0b3e1615c15dc5b0`、发布源码 `103ba2cc1adafe91f4256803c20a93aabdb82875` 相同。生产、测试和构建入口相对 `5802ac1` 没有差异。

既有 `artifacts/gpu-structural-optimization-20261028/acceptance/dist-103ba2c/` 为Release／linux-x64／self-contained Server和Worker，原发布 `sourceDirty=false`。本次重新读取并验证1022个发布文件SHA，`deployment.json` SHA-256为 `16b74e2935e5c639e4178b71b3ed6a08cd5d951cd98425a88cc5366364f57870`。这证明当前生产输入与原验收发布相符，不表示本次新发布或重新构建的二进制必然逐字节相同。

合同523个guard文件与4个生成物指纹匹配。复用已验收生产基线的CLR metadata执行默认freeze检查，生成内容逐字节一致；静态validate通过 **102路径／113方法／560 schemas／3331例（2483反例）**。本次没有更改合同指纹覆盖文件，因此无需重生成合同或提升版本。

```bash
python3 tools/backend-api-contract/freeze.py \
  --metadata artifacts/gpu-structural-optimization-20261028/acceptance/clr-metadata.json
artifacts/api-contract/venv/bin/python tools/backend-api-contract/validate.py \
  --report artifacts/main-integration-20261028/contract-validation.json
git diff --check
```

5份既有归档的压缩文件SHA、大小、成员清单以及全部1391个成员的解压字节SHA／大小均通过复核，未改写归档或索引：

| 原证据目录／归档 | 文件数 | 压缩归档SHA-256 |
| --- | ---: | --- |
| `20261027-optimization/evidence.tar.gz` | 483 | `cd60e42150f5d1b330f5a365cc19128f5e305f8027af521a0bb7201ff0d6697c` |
| `20261028-structural-research/evidence.tar.gz` | 209 | `f452ed96ee913a30810c21c762bbf482a2dd984b95f727584414ae45adcedce4` |
| `20261028-structural-optimization/evidence.tar.gz` | 587 | `a22eb349230142e4affca5f5f812d92a6c16172bf5c937f9e9638bb4d38572b3` |
| `20261028-structural-optimization/orchestration-evidence.tar.gz` | 33 | `3346ddd0649a41eb8b4cfd9a0513aae0bc9b59b93138cb2b5bc507717714f5a2` |
| `20261028-structural-optimization-acceptance/acceptance-evidence.tar.gz` | 79 | `2431a004887149ce81a88e1c105c5b9bf15c12518680100aacaa2d908825472c` |

上述目录均位于 `docs/performance/`。首次临时核验脚本将所有索引误按字典读取，在研究归档的列表格式处停止，未产生通过报告；兼容两种既有记录格式后完整重跑通过，没有修改被核验文件。

## 4. 原验收归属与限制

第二批在dev完成的4045次相关测试执行（含重复环境与子集）、摘要模式HTTP2032请求／13313断言和详细模式304／3951，以及246进程／4560项目帧的正式七标签性能矩阵继续由[原验收报告](../acceptance/backend-gpu-structural-performance-optimization-acceptance-20261028.md)及其固定来源说明。本次没有重复构建、运行测试、发布、fresh HTTP或性能采样；合同检查也未重放历史HTTP请求。

原五轮Vulkan主矩阵图／材质／组合中位耗时降低64.89%／73.82%／73.61%，材质／组合计量帧最大346.453／373.706ms。首帧及独立监控开销对照仍有超过500ms样本；summary组合GPU观测中位开销10.70%，detailed有27条限速丢弃而摘要覆盖完整。这些固定本机短批次结论不扩大为任意脚本、任意工程、全请求500ms、浏览器、跨平台、长片或GPU编码保证。

首次HTTP的Oracle构建来源失败、受干扰探索采样和用户暂缓性能采样的原记录均保留历史归属；后续正式通过结果不回写失败目录或删除暂缓记录。

## 5. 工作区与交付记录

同步更新README、AGENTS当前入口、roadmap §18.233、实施§84、分支记录和ADR-0241；两批验收及第二批计划仅补充后续集成说明，保留原运行来源。无关未跟踪 `tools/backend-project-image-export-probe/` 的6个文件路径和SHA与预检完全相同，不纳入提交。原始运行产物继续保存在本机artifacts。

文档记录提交完成后推送 `origin/main`，再核对远端SHA、开发分支可达性和工作区。本文不提前宣称推送成功，最终结果以交付时的远端核对为准。
