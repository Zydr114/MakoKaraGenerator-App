# W8 作者工作流缺口修复

日期：2026-10-30。基线：`chore/repository-hygiene-20261029` 的 `a9c3906`，当时产品 0.107.4、合同 1.39.0；本轮交付产品 0.108.0、合同 1.40.0。来源为独立 Web 仓库 `docs/web-client-w8-design.md` §3，其实测针对 0.107.0；以下结论按当前后端源码重新确认。

## 核查与交付范围

| 编号 | 当前源码结论 | 本轮工作 |
|---|---|---|
| G33 | `draft.json` 持久保存 client owner，重启后 client 注册表不恢复，普通列表和读取无法找回；没有孤立草稿自动回收 | 增加 profile 草稿发现与显式认领，保持普通接口 owner 限制，拒绝抢占活动 owner；保留精确内容与所有权 CAS，认领后可继续编辑或显式删除 |
| G34 | 旧源码写入口要求全部且仅全部现有 C# 文件 | 保留旧入口，增加包文本文件读取与整批保存，支持 C# 增删、manifest、SkSL 与图 JSON；固定二进制保留，结构和配置不合法整批拒绝 |
| G35 | 保留包修订可按身份读，缺少列表；仅保存最近编译摘要 | 增加有界分页修订列表与持久修订时间、编译摘要；未知历史时间明确省略 |
| G36 | 作者草稿与个人配置无包下载路由 | 按精确草稿/库修订下载所选固定包为 `.mkfx`，沿现有认证、闭包与资源预算 |
| R10 | `canCompile` 只看编译参考集是否存在 | 同时判断当前待编译修订是否有 C#，覆盖材质/图包及当前与在用修订不同的情况 |
| G31 | 完整模板校验错误被折为统一消息 | 返回现有错误 envelope 的具体字段、原因与稳定 failureCode，覆盖重复包引用等拒绝 |

另外记录但不扩大修复范围：作者验证编译问题在报告 `issues`，预览诊断可按实例重复，组件摘要不含包名称/版本；前端已将这些列为观察而非缺口。

## 实施与验收

1. 登记当前核查结论及边界。
2. 分别实现模板校验反馈、包文本编辑与能力修正、草稿恢复、修订记录与包下载，并增加定向回归。新增缺陷测试先确认旧实现或对应变异失败。
3. 统一产品版本、公共能力、行为源、ADR、元数据生成器与合成示例；从新构建 metadata 生成完整合同并校验。
4. 执行 `dotnet build MakoKaraGenerator.slnx` 零警告/错误构建及相关后端定向测试，发布同源码的 self-contained Server/Worker。
5. 使用新 profile 和证据目录执行 fresh HTTP：恢复/重启、owner/CAS/回执、包文件往返与拒绝、无 C# 能力、历史与下载再导入、模板错误定位；记录凭证扫描与 PID/startTicks 排水。
6. 更新前端交接入口及本记录，按路径提交并推送当前工作分支，不合入 main。

所有构建、测试、合同、部署、HTTP、浏览器、平台与性能证据分别报告。本轮不代表浏览器接入、跨平台或性能验收。未跟踪的 `tools/backend-project-image-export-probe/` 与现有其它工作树保持原状。

## 实施记录

- G33 已增加 `GET /api/v1/profile/author-drafts` 与显式 claim 路由。profile 列表按草稿身份分页，返回 orphaned/dormant/active/ownedByCaller 状态；claim 使用草稿修订与所有权修订 CAS，并在活动 client、有在途操作、回收或目标草稿配额不足时拒绝。旧格式草稿可在首次 claim 时升级，未知 schema、非对象 JSON 和无效修订拒绝。
- G34 已增加 `GET/PUT /api/v1/drafts/{id}/package-files`。接口只开放 `csharp`、`shader`、`shaderProgram`、`graph` 文本；manifest 与全部文本原子校验，图片/字体等非文本文件的 path、role、SHA 和字节必须保持不变。文本单文件4 MiB、文本与清单合计16 MiB、最多256个文件，HTTP JSON最多32 MiB。
- G35 已增加源码修订分页、保存时间和编译摘要。保留包目录是历史真值，旧草稿缺失时间时省略 `savedAtUtc`；精确游标不属于保留历史时返回404。无变化的源码保存不递增草稿修订，也不清除最近编译摘要。
- G36 已增加作者草稿与个人配置库的 `.mkfx` 下载。下载固定精确修订，响应使用认证流、ETag 和 `Content-Disposition`，同时下载最多2个；流关闭、断开或宿主停止都会释放临时归档和名额。stale revision、owner ACL、未知修订和包结构错误均在流开始前拒绝。
- R10 已将 `canCompile` 绑定到当前源码修订实际存在 C# 文件和编译参考集，而不是只看宿主是否配置编译器。
- G31 已让完整模板预览与保存共用具体校验位置，返回 `field`、消息和稳定 `failureCode`，覆盖重复配方、重复包引用和闭包缺失/多余映射。

本轮已完成实现、定向回归、合同冻结、匹配 Server/Worker 发布和 fresh HTTP 有限验收，详情如下。浏览器、跨平台、长片和性能验收仍独立。

## 最终验收（2026-10-30）

生产实现提交 `245be6cc8f41a75460ef77bb67b35d79673c7e3b`，产品0.108.0；首轮合同与交接提交 `6569bf1b1215a7e3b6c7e839743675eee9a7c701`，合同1.40.0、HTTP协议1。之后仅完善探针请求及文档/合同说明，生产输入保持一致。

| 证据层 | 本轮结果 |
|---|---|
| 构建 | `flock /tmp/mkg-backend-b4-dotnet.lock dotnet build MakoKaraGenerator.slnx --no-restore -m:1`：0警告、0错误；新编译的 ContractMetadata Release 工具也是0/0 |
| 定向回归 | Hosting `FullyQualifiedName~BackendAuthor`：23/23；Server `FullyQualifiedName~Author`：2/2；Server `FullyQualifiedName~G31_`：11/11。共36个不同用例；单独重跑的恢复1项与G35/G36的3项不重复相加 |
| 缺陷先红后绿 | G31旧实现11个有效用例失败，恢复后11通过；R10与源码no-op旧实现失败、修复后通过。日志保留 `g31-before.log`、`g31-after.log`、`g34-r10-before.log` 及对应恢复日志 |
| 静态合同 | 新metadata→freeze/write→默认检查→validate；108路径、120方法、574 schemas、3388合成例（2521反例） |
| 匹配发布 | `artifacts/w8-authoring-20261030/dist-final1/`：源码`6569bf1`，`sourceDirty=false`，self-contained Release/linux-x64 Server+Worker；925个生产输入、1022个发布文件SHA复核全匹配 |
| fresh HTTP | `artifacts/w8-authoring-20261030/http-final3/`：144请求／984断言，含351文件凭证检查；1个异步编译任务真实成功终态 |
| HTTP形状回放 | 135份JSON响应、38份成功JSON请求通过，覆盖31个操作；9份非JSON/未映射留档跳过，0未配对。此结果不增加HTTP请求或断言计数 |
| 排水 | 两次Server/launcher均exit0；累计28个观察PID/startTicks身份（18个Worker身份）全部退出，`remaining:[]`；本次bootstrap已删除 |

成功HTTP包括：C#增加/删除/改名、manifest名称/版本更新、完整文本 no-op、拒绝缺失文本不改变修订、纯材质/图的 `canCompile:false` 与文本保存、代表作者试样真实CPU Worker产帧及PNG完整解码、源码历史分页/编译摘要、精确当前与旧修订下载再上传检查、组件库下载、重复模板包引用的具体字段/failureCode、活动owner拒绝、Server重启后orphan发现/显式claim、相同回执重放、过期所有权CAS拒绝、claim后继续预览/下载/删除，以及草稿删除后组件库仍可下载。

Hosting独立覆盖过期owner且有在途操作时拒绝、并发claim仅一个成功、未知草稿schema拒绝、分页与目标配额、历史未知游标/精确CAS、组件库stale revision、两个下载槽位及释放后恢复。它们是本地回归证据，不替代HTTP或并发压力矩阵。

发布manifest SHA-256：`d10a3c1ba145f34307a4327955200492a649d43e6feea2127497b4bee30a6e53`；成功HTTP evidence-manifest SHA-256：`ecd785775aeec37ff1f853f017a00289dae0ec770cefd7855e6b4e9cb62d9143`。静态与HTTP合同报告、最终指纹复核分别位于本证据根的 `contract-validation.json`、`http-contract-validation.json`、`final-integrity.json`。HTTP摘要生成后共352个文件纳入evidence manifest，和先前351文件凭证扫描计数分开。

失败目录保持：`http-final1` 30请求／216断言，新增PUT负例漏带metadata；`http-final2` 98请求／682断言，G31探针配方漏带required-null `configurationId`。两次均为探针请求不符合现有合同，未据此修改生产实现；Server exit0并排水，无残留。修正后用新目录跑完整 `http-final3`。

本轮没有修改图片/字体等非文本包资源的字节能力；包编辑明确保留它们。孤立草稿不自动删除，用户认领后可显式删除。`.mkfx` 下载仅完整认证GET、全局2并发、30秒期限，不承诺Range/HEAD/条件304。前端需固定最终同一提交的完整合同接入；本轮未改独立Web仓库，也不代表浏览器、跨平台、长片、性能或任意输入组合验收。
