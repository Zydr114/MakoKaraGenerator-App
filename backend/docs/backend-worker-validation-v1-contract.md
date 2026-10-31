# B4-A Worker 固定帧计划验证合同

> **0.69增量：** 下文首批单profile／拒分析／预算记录保留其历史范围；当前B4-E已增加完整`static-lyrics-scene-analysis-v1`，旧`static-lyrics-scene-v1`仍仅null。唯一snapshot-v1／Source自动选profile、Available canonical／Failed／NoStream完整保真，总4MiB深200k及实际wire同规则闭口，不截断。public旧sourceProfile保留并增sourceProfiles，私有export真实probe匹配EngineIdentity与双profiles，旧Worker不虚称兼容；purevideo保完整声明但禁止源音频IO，true grant重绑保Media身份，sourceSHA为原编码。preview／validation扣24／32头admission，export按实际metadata＋32在spawn/send前guard，不能承诺所有动态封包均admission拒绝。实际有限559／559及真正HTTP232请求／1864断言、三片各30完整解码帧／首帧oracle见[实施§23](archive/history/backend-extraction-progress.md#b4-analysis-static-profile-0690)／ADR-0213；当前HTTP只维护于[统一API](architecture/backend-api.md)。新profile不表示音频驱动视觉或完整B3／B4完成。


本切片通过独立受管理 Worker 调用原 `RuntimeFrameValidator` 和默认 `ProjectRenderSessionFactory`。Hosting／Server 只引用中立工程、codec、DTO 和执行器接口，不引用 Runtime.Hosting、Skia 或 Export。工程快照及每输出帧数固定，随后编辑不改变该任务的输入。

这是固定帧计划正式验证，不代表整片或完整工程能力已经通过。首批继续使用 `snapshot-v1/static-lyrics-scene-v1`，包、预设应用、音频分析和背景图片／视频由原 codec 明确拒绝；音频路径是声明，不授权 Worker 读取音频。字体仍使用本机真实 Runtime 字体解析。

## 输入和执行范围

`IBackendValidationExecutor.ExecuteAsync(ProjectSnapshot, int, IProgress<BackendJobProgressDto>?, CancellationToken)` 返回 `ValidationReportDto`。生产 `WorkerBackendValidationExecutor` 每任务拥有一个独立 Worker，所有退出路径等待进程和管道释放。

| 边界 | 固定值 |
| --- | --- |
| 每种输出的帧数 | 1～600，总计最多 1200 |
| 末帧时间 | `(F-1)*1_000_000*denominator/numerator`，不超过显式正 `OutputDurationUs` |
| Worker 本地画布 | 最大 1920×1080，BGRA 像素最多 8MiB |
| 原始帧 IPC | 无；B3 原有 raw 4MiB 帧合同保持独立 |
| 每任务期限／取消宽限 | 60 秒／2 秒 |
| 完整报告 | 最多 256KiB、256 个 Issues、512 次实际 occurrence |

adapter 和 Worker 都验证输入几何及帧计划。业务帧时刻、完整合成、Final 质量、两个独立普通／透明会话和实际诊断读取由原验证器执行，不复制其算法，也不从工程时长推断总帧数或静默截断。

## 私有 IPC

新增请求 `task-validation-v1`、进度 `task-validation-progress-v1` 和唯一终态 `task-validation-result-v1`。

输入 header 固定 32 字节：0～3 为 `KTV1`，4 为版本 1，5～7 为零，8～23 为非空 Guid，24～27 为 little-endian 每输出帧数，28～31 为零。随后是固定 snapshot-v1 字节，因此最大快照预算为 **4,194,272 字节**。`inputSha256` 保护整个封包，`sourceSha256` 保护快照。消息中的 taskId、请求 ID、来源 SHA 和帧计划必须与封包一致。

进度来自原验证器同步 callback，携带真实 completed／total、输出模式和整数时间。Worker 使用容量 1 的 DropOldest channel 和唯一 pump，不保留逐帧 Task 历史。读方允许合并进度，但严格拒绝非递增 sequence／completed、错误总数、输出模式及非计划帧时间。pump 排水完成之后才发送终态；成功必须观察到真实最后一次 callback，普通和透明计数各等于 F，任何 Error 禁止成功。

报告 payload 有独立 `outputSha256`，不可拿 input SHA 校验报告。报告必须含完整 Issues 和 Diagnostics 数组；诊断保留输出模式、原 epoch／sequence、实例、artifact、stage、时间、severity／code／message、逻辑源码位置和包身份，不伪造空报告。无实际诊断的原生静态任务显式返回真实空数组。未协商的 Material／Graph／Compilation／SampleFailure 来源明确失败，不丢字段。回执错误、过期来源或上一任务的晚到消息使私有流失效并回收进程。

## 报告预算、取消与失败

`RuntimeValidationBudget` 在 collection 增长前检查条数，并允许 Worker 同步检查每条 DTO 的有界 JSON 大小。逐项序列化使用受限 stream，预留报告对象和分隔字节，不先创建巨条无界数组。超预算抛出 `WorkerValidationReportBudgetExceeded`，不发布截断报告。私有报告 JSON 和公共侧文件采用相同的 null 字段语义。

原 `ValidateAsync(snapshot, plan, progress, token)` 签名保留，默认没有新预算；新增 overload 接受可选预算，CancellationToken 仍为最后参数。检查回调的失败直接传播，不能被通用 catch 转成另一份不完整报告。

窄 `ValidationSessionScope` 等待释放。Render 或取消是主失败时，释放失败保留在原异常 `Data["RenderSessionCleanupFailure"]`，不得覆盖主失败；只有释放失败时仍返回正式失败。中立 `RenderSessionFailureException` 的实际 snapshot 进入同一完整报告及预算流程。

用户取消保留取消语义；调用方 token 没有取消而期限到达时返回稳定中立失败 **`WorkerTaskTimedOut`**，不得被任务账本误记为用户取消。发送 cancel 后最多等待宽限，未排水则回收拥有的进程树，并观察 pending read／pump。成功报告只在两个 Runtime 会话均释放后交给任务宿主；执行器本身还等待私有进程和管道释放。缺失／不可执行 Worker 返回 `WorkerUnavailable`，没有 Runtime 进程内 fallback。

任务宿主负责唯一账本、固定来源修订、报告侧文件、TTL、最终提交和 HTTP／SSE；本文件不重复其状态机。报告校验不是新增脚本、完整媒体或导出能力。Export 原有 artifact 验证、释放、最后取消检查和不可取消 Publish 事务保持独立。

## 有限回归入口

所有 normal restore／build／test 使用 `flock -o /tmp/mkg-backend-b4-dotnet.lock`，分析器保持启用。

- `Backend.Worker.Tests/StaticValidationTaskTests`：仅内部测试 factory seam，验证原正式执行器两会话／Final、真实 buffer 发生来源、逐项条数和字节预算、未协商来源拒绝及 pump 失败释放；生产 factory 不变。
- `Backend.Hosting.Tests/WorkerValidationTests`：header、输入／输出 SHA、geometry／timing、报告 schema、实际 Error 来源、有界自有 Python IPC 故障进程、取消／期限杀树及重用、终态后晚到进度／结果拒绝。
- `Runtime.Hosting.Tests/RuntimeValidationTests`：保留原正式验证回归，增加主失败／取消与 cleanup、cleanup-only、实际中立失败 snapshot 和累计预算。
- `Runtime.Hosting.Tests/WorkerValidationIntegrationTests`：真实 Linux Worker 在 320×180 和 1920×1080 运行小帧双输出计划，与原 Runtime 正式结果和真实 callback 逐字段比较；实际 FullHD 任务收到帧进度后取消、排水和重用。

真实静态原生报告可能没有实际 diagnostic；非空来源和预算检查由明确注入的正式 adapter 回归验证，不将注入测试宣称为包／脚本的真实 Worker 支持。此证据不覆盖全量、性能、平台人工、完整媒体、整片正式导出或整个 B4。
