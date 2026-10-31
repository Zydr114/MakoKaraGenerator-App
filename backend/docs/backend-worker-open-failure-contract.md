# Worker 首次打开的中立失败边界

首次懒 open 的失败与后续帧失败统一通过 `RenderSessionFailureException` 进入中立预览协调器。此前 CreateCore 将缺失／不可执行 Worker 转为 `PreparedFrameException`，而协调器只读取中立异常的 Code，导致已接受请求在 GET／SSE 中只剩通用失败、丢失 `WorkerUnavailable`。

修复在 proxy Dispose 完成后映射三类具名失败：WorkerProtocolException 保留其稳定 Code 与存在时的真实完整诊断；PreparedFrameException 保留 Code；Win32Exception、FileNotFoundException、DirectoryNotFoundException 使用 WorkerUnavailable。内部原始异常保留为 InnerException，不向外部消息拼接本机路径。协作取消保持取消语义，失败没有像素或占位会话。

实际诊断仍使用既有 DecodeDiagnostics／FromOccurrences 保留 epoch、sequence、实例、产物、阶段、时刻、逻辑位置和包身份；坏回执没有实际完整快照时保持 null，不能生成空 Runtime 快照代替。Coordinator／Registry 直接消费该中立边界，不依赖私有 wire DTO。

本批只修改工厂、其聚焦测试及获批的 Server transport 测试夹具。新 Server 夹具可注入真实 WorkerRenderFrameSessionFactory；新增缺失、删除、不可执行三种边界，所有复制／删除／Unix 权限操作仅发生于测试拥有的临时 apphost。实际 HTTP 验证 202 接受后 GET 与 SSE 的失败 Code 保留 WorkerUnavailable、无最新帧、不暴露路径；随后工程与预览关闭，音频引用按原 TTL 回收，证明首 open 失败也解除独立运行引用。

终态：Factory 聚焦 **19／19**、Server transport **14／14**，均 0 失败／跳过。Factory 聚焦包含三种 apphost 不可用及 open 畸形回执／实际 Error 诊断快照；受控 IPC 探针用于诊断来源与生命周期注入，不当作真实脚本能力。

所有 dotnet 经共享锁、正常 restore、原分析器执行。原 session60662 的 Factory19成功；其 Server framework-dependent testhost因系统缺少 ASP.NET10 未启动，aborted TRX 不计成功。随后原 Server14 范围沿本轮既有 self-contained 入口执行，session45508 真实退出0：

```bash
dotnet test tests/MakoKaraGenerator.Backend.Server.Tests/MakoKaraGenerator.Backend.Server.Tests.csproj -m:1 -r linux-x64 -p:SelfContained=true /p:UseSharedCompilation=false --nologo
```

证据在 `artifacts/backend-b3d-worker-open-neutral/`：`worker-open-neutral-focused.trx`、`preview-transport-open-neutral-selfcontained.trx`、原被排除的 aborted TRX、执行日志及 `worker-open-neutral-evidence-manifest.json`。无需安装系统运行时或操作用户 Worker bin；全局版本、Coordinator／Registry 生产逻辑及原暂停范围未扩展。
