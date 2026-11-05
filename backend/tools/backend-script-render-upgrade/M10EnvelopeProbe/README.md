# M10 Linux 原生封套验收探针

本工具引用本仓库正式 `MakoKaraGenerator.Processes` 和 `native/media-envelope`，不连接
HTTP、不运行媒体解码器。C helper仅接受五种固定模式；OOM负例在已核验64MiB内核限额
之后才触发，父子各触页40MiB，分别有15秒alarm兜底。无需root，不修改系统配置。

从 `backend/` 执行，证据目录必须是新的绝对路径：

```bash
dotnet build tools/backend-script-render-upgrade/M10EnvelopeProbe/M10.NativeEnvelope.Probe.csproj -c Release -m:1
dotnet tools/backend-script-render-upgrade/M10EnvelopeProbe/bin/Release/net10.0/linux-x64/M10.NativeEnvelope.Probe.dll --evidence /absolute/new/evidence-directory
dotnet publish tools/backend-script-render-upgrade/M10EnvelopeProbe/M10.NativeEnvelope.Probe.csproj -c Release --no-restore -m:1 -p:AllowMissingPrunePackageData=true -o /absolute/new/publish-directory
```

依赖本机Linux x64、.NET 10、CMake、C编译器、pkg-config、libsystemd headers、同用户
systemd manager及已委派的cgroup v2 memory控制器。缺失能力具名拒绝，不降级到无限制
进程。构建不启动服务；运行才建立带随机nonce的自有transient unit。

七场景依次验证：执行前gate与真实stdin/stdout/stderr/fd3；自然非零退出17；caller取消；
2秒管道停滞；父子合计OOM；borrow在途取消与排水；四种错误owner令牌拒绝。
两个caller取消场景额外要求公开异常持原始调用token，不能只凭异常类型判通过。
只对捕获的InvocationID、PID/startTicks、cgroup inode做动作，不按进程名称批量清理。
所有场景必须terminal、空/已移除cgroup、输出EOF、Unref和源预算释放后才算通过。

`events.jsonl`逐条fsync，包含工具摘要、准确owner与控制值；`summary.json`保存结果。
退出0表示七场景通过，42表示CapacityUnsupported，1表示断言/运行/清理失败，64表示
参数错误。失败quarantine停止测试并保留失败事实；helper的alarm兜底不是成功排水证据。
私有证据可能含绝对工具路径，不应直接作为公开HTTP诊断。

这证明有限Linux执行封套，不证明生产decoder、颜色/PTS、Worker/Server发布、Windows/macOS、
整个Worker RSS或性能。真实unit替换竞态与错误令牌测试也不是同一个结论。

2026-11-05首次正式源码本机七场景记录见App根目录
`.local/script-sdk-m10-20261105/native-envelope-real-03/`；后续正式工具发布复验单独登记。
