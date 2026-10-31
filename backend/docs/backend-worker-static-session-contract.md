# snapshot-v1 静态工程持久 Worker 会话

> **0.69增量：** 下文首批单profile／拒分析／预算记录保留其历史范围；当前B4-E已增加完整`static-lyrics-scene-analysis-v1`，旧`static-lyrics-scene-v1`仍仅null。唯一snapshot-v1／Source自动选profile、Available canonical／Failed／NoStream完整保真，总4MiB深200k及实际wire同规则闭口，不截断。public旧sourceProfile保留并增sourceProfiles，私有export真实probe匹配EngineIdentity与双profiles，旧Worker不虚称兼容；purevideo保完整声明但禁止源音频IO，true grant重绑保Media身份，sourceSHA为原编码。preview／validation扣24／32头admission，export按实际metadata＋32在spawn/send前guard，不能承诺所有动态封包均admission拒绝。实际有限559／559及真正HTTP232请求／1864断言、三片各30完整解码帧／首帧oracle见[实施§23](archive/history/backend-extraction-progress.md#b4-analysis-static-profile-0690)／ADR-0213；当前HTTP只维护于[统一API](architecture/backend-api.md)。新profile不表示音频驱动视觉或完整B3／B4完成。


本接缝让 `WorkerRenderFrameSessionFactory` 实现中立 `IRenderFrameSessionFactory`：每次 Create 为不可变 `ProjectSnapshot` 建立独立受管理 Worker，open 只解码一次专用 snapshot-v1，并只调用一次真实 `ProjectRenderSessionFactory.CreateAsync`。同代次连续前向帧复用Runtime；私有Worker接口同快照下也支持后退时刻，原生测试直接验证该能力。HTTP seek的合同另行换generation、由Coordinator.Invalidate排水／释放旧Runtime及Worker，下一帧重新open，不能解释为HTTP seek沿用原会话。close 等待帧排水并释放该会话，工厂会话 Dispose 随后回收拥有的进程。

它使用 `StaticProjectSnapshotCodec` 的 `static-lyrics-scene-v1` 有限能力，支持普通静态歌词、注音、多来源、内置样式和场景；效果包、预设应用、分析和背景媒体严格拒绝。音频路径／身份只作为声明传输，不能授予 IO；真实验证故意使用不存在的音频声明。Hosting 只引用 Backend.Rendering 和中立 Rendering.Abstractions，不引用 Runtime.Hosting 或 Skia，不回退到同进程渲染。

## 私有封包

外层沿用 worker-1 的有界长度前缀 JSON，每个输入／输出 payload 最多 4 MiB，正文最多 8 MiB。新 kind 与旧 opaque／40 字节背景探针明确分离：

| kind | 输入／成功输出 |
| --- | --- |
| `static-session-open-v1` | 24 字节 KSO1 头＋专用快照净字节；回 `static-session-result-v1` |
| `static-session-render-v1` | 固定 80 字节 KSF1 帧输入；回 `static-session-frame-v1`，含实际像素与完整诊断 |
| `static-session-close-v1` | 固定 64 字节 KSC1 关闭输入；回 `static-session-result-v1` |

三个头部均为 little-endian：magic 在 0～3，版本 1 在 4，会话 Guid 原始 16 字节在 8～23。open 的 5～7 保留零；快照从 24 起，净上限为 4,194,304−24＝4,194,280 字节，不能把 codec 的 4 MiB 上限直接塞入额外头部。

render 的 byte 5 为透明模式 0／1，byte 6 为质量 0 Draft／1 Standard／2 Final，byte 7 保留零。固定快照 SHA 原始 32 字节在 24～55，宽／高在 56／60，整数微秒在 64，单调正序号在 72。close 的 5～7 保留零，固定源 SHA 在 24～55，单调关闭序号在 56。

所有新输入 kind 也必须携带正确 inputSha256。open 将快照净字节 SHA 固定为源身份；每帧只含固定会话、源 SHA、时间、目标、透明模式、质量和序号，不携带可变工程。消息级会话／SHA／序号必须与受输入 SHA 保护的封包相同；成功回执还必须匹配请求 ID、原输入 SHA、目标、时间、透明模式、质量、紧密 BGRA 步长、独立输出 SHA 和像素长度。旧会话、错误源 SHA、非递增序号或晚到回执均失败，不交付像素。

输出每边仍受 1920／1080 上限约束，且像素总字节不超过 4,194,304。1920×1080 因 8,294,400 字节被拒绝，这不是完整 Full HD 出口。LayoutLineId、PreparedFrame 和未知质量被明确拒绝为 WorkerRequestUnsupported，不静默忽略。

## 实际诊断与失败

每份成功静态帧都必须携带来自真实 Runtime 帧 `DiagnosticSnapshot` 的完整发生数组，即使实际数组为空。传输保留真实 epoch／sequence、实例、产物、阶段、微秒、严重度、代码、消息、逻辑源码位置和实际包身份。包内容修订保持现有 `sha256:`＋64 位小写十六进制语义，不剥前缀。静态能力意外产生尚未协商的图像效果／材质诊断时明确失败，不能丢弃。

本静态能力的完整诊断快照上限为 256 条且独立 JSON 不超过 64 KiB；超限为 WorkerDiagnosticsBudgetExceeded，不截断。空 epoch、重复代次、不是从一开始连续递增的序号、缺报告／来源字段、错误严重度或伪造 HasError 标记均拒绝。Worker Error 携带实际完整快照时保留它；成功像素回执只要有任意 Error 也拒绝。

`RuntimeDiagnosticSnapshot.FromOccurrences` 是此次批准的窄中立导入工厂：验证完整发生身份并隔离输入集合，保留真实 epoch 和 sequence。原 FromArray 保持兼容；不会通过它重新生成发生身份，重复帧不会让 RuntimeDiagnosticCursor 重复消费已有报告。

工厂将失败的真实 wire 发生快照保真还原为中立 `RenderSessionFailureException(Code, DiagnosticSnapshot, inner)`。预算／畸形消息没有实际快照时保持 null，不造空诊断。Code 非空，内部异常保留用于本机诊断；HTTP 边界必须另行脱敏并不得输出内部路径或栈。Coordinator／Registry 的失败诊断接入由独立预览链路切片负责。

## 资源归属

每个工厂会话独占 Worker，open／render／close 在 Worker 内串行排水。代理同样串行调用，取消／超时沿用 cancel、shutdown、有界等待及整个拥有进程树终止。半条写入、EOF、坏回执或 Runtime 失败使工厂会话失效并回收进程；调用方可以从同一工厂重新 Create 固定输入，不能把旧会话身份带进新 Worker。

Worker 只保留活动 Task：完成立即 remove，最多两个操作用于单调用与已发布回执的交接窗口，第三个活动操作为 WorkerBusy。旧 opaque 和背景探针也使用这个上限；shutdown 只排水当前 pending，不累积持续播放的已完成历史。

静态输入没有 Effects/Fingerprint，Runtime.Hosting 在静态分支中先返回，不创建 Lazy ScriptCompiler 或编译缓存。artifactRoot 只是传入的未创建路径；本批不把未来脚本的缓存回收或失控脚本隔离记作完成。实际 Native 帧、渲染会话及子进程均按归属释放。

## 有限验证证据

最终验证经共享 `flock -o /tmp/mkg-b3d-dotnet.lock` 串行，正常 restore，分析器和警告即错误保持：Worker build 0 警告／错误；Hosting 的四类 Worker 聚焦 89／89，Runtime.Hosting 的真实静态会话 9／9，均 0 失败／跳过。完整 solution／HTTP、持续播放和平台出口由统一验收另行记录。

9 项真实测试使用本机 Noto Sans CJK SC：两种透明模式×Draft／Standard／Final 的六组合，每次连续三个时刻加回退寻址，全部像素与同字体真实 Runtime 会话逐字节相等；覆盖多来源、注音、倒计时／场景、渐变及背景 alpha，并验证变化时刻的像素确实不同、关闭后 recreate。其余三个真实流程核对同 PID close 再 open，以及旧 session／错误源 SHA／重复序号拒绝后的恢复。

89 项聚焦包含旧背景 Worker 45 项、新固定封包／4 MiB／头部扣预算、诊断预算／发生身份／数组隔离／游标、精确错误字段／晚到回执、失败会话释放、deadline 后同工厂 recreate、缺执行体 typed unavailable，以及真实 Worker raw IPC 的第三活动调用拒绝、完成后八次重用和实际 shutdown ExitCode 0。Python3 自有 IPC 探针用于注入非空诊断、Error、坏回执与失控等待，不把探针像素或诊断当作真实 Runtime 脚本证据；本 static profile 真实 Runtime 诊断通常为空，其空快照仍来自真实帧。

旧进程树探针的子进程在 KillTree 后可能由内核稍后结束／回收，观察限于两秒，仍存活就失败；不降低终止条件。此次中间测试曾发现包修订前缀错验，已按真实身份合同修复；CA1869／CA1870 均通过遵守原分析器规则修复，未关闭分析器。

终态证据位于 `artifacts/backend-b3d-static-worker/`：`worker-static-focused-fixed.trx`（89），`worker-static-native-quality.trx`（9）、`worker-static-validation.log` 和 `worker-static-evidence-manifest.json`。范围止于 timed static project；资源闭包、脚本执行、完整作者／导出及跨平台出口不自动完成。
