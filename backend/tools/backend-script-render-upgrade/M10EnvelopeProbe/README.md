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


## N1 独立 observer 有限真实探针

N1 observer 已整合进正式 Processes。2026-11-07 修正 systemd 262 经 `systemd-executor`
启动 MainPid 的时序竞态（exec 前 `/proc/<pid>/exe` 可能 EACCES，单元仍为 `activating`）后，
本机四场景真实运行通过且全部 owner 已关闭，记录见 App 根 `.local/script-sdk-m10-20261105/observer-real-03.log`。

```bash
dotnet tools/backend-script-render-upgrade/M10EnvelopeProbe/bin/Release/net10.0/linux-x64/M10.NativeEnvelope.Probe.dll --observer-evidence /tmp/m10-observer-new-private-directory
```

运行前由 root 在程序集旁提供 `observer-source-manifest.json`，至少含
`matchedIntegrationReviewed: true` 与非空 `files` 路径/SHA256 清单；清单须涵盖最终
整合源码及两份冻结输入。清单是审阅者的来源声明，工具不能由二进制反推其真实性。
运行时另外计算 probe、Processes DLL、bridge、gate、helper、diagnostics DSO 的实际摘要。
本草稿根 README 说明具体整合与验收顺序。

新增四场景串行运行：错误 expected 的四个真实 native attach 拒绝；终态时 owner
先 Unref；终态时 Host 先 Unref；owner 子进程 pidfd SIGKILL 后，Host 同一个 observer
继续见原 helper live、写原 pin `cgroup.kill` 并证明 terminal+drained。
每场 Ref 集合均经真实 `Unit.Refs` 和 bus daemon 的 sender→PID 核验；独立的只读
witness 不 Ref/Unref unit，不把 active unit 存活当独立引用证明。

新增 diagnostics DSO 只供本探针做只读见证、pidfd 与 socket peer UID 核验；不修改 N1 ABI。
owner 断开场景只记录 child Process 与父子控制管道 EOF；Host 没有死去 child 的 native
三路 Stream 权威，因此绝不据此记录 native pipes EOF 或完整 Worker 复合排水。

证据主目录和 child 目录为 0700，`guardian.sock` 为 0600，服务端检查 `SO_PEERCRED`
同 UID。新目录路径须足够短，使最长 child socket 路径 UTF-8 不超过 100 字节。
存在 unresolved 引用时，进程停在活 `failed-retained`，同一真实对象/Task/bus/pin 留存。
`status` 可随时查询；保留期 `observe`、`kill-drain`、`finish-known` 返回异步 accepted
ACK，最终状态须再查。`finish-known` 只允许尚未尝试的首次自有 Unref，不重试歧义请求。
子进程控制通道断开后，应分别查看其专有 guardian；不能向已破损通道盲重发命令。
Ctrl+C 请求独立清理；不能把总预算或 helper alarm 当已清理，也不能保证失败固定时限退出。

退出 0 只表示四个有限场景全部通过并关闭本次实际 owner；42 是能力不满足且已清理，
1 是失败且已清理，64 是参数形状错误。活 `failed-retained` 无退出码。
这些结果不证明真实 Worker/泵/源/stage/配额的闭包，不开放任何媒体或 M10 能力。

`tests/` 提供受控协议、lease 保留与 guardian 异步状态测试，以及替换 syscall/poll/
peer-credential 边界的 C 测试源码。测试源码不启动真实 manager/helper；全部尚未执行。
测试项目引用 probe 项目，因此构建测试也会构建 native 产物；构建本身不启动服务。


### P1 控制通道修订（2026-11-06，未执行验证）

父端命令失败或取消时关闭自己持有的 child stdin 写端，使 child 原 Console reader
收到真实 EOF 后进入 owned cleanup/guardian；child 30s 读取 token 是协作期限，不能
单独保证中断已进入的标准输入读取。`ProbeOwnerChannel` 保留同一 command/send/read/
close Task并直接 join，迟到 ACK 不使取消成功，坏通道不重发命令。父 `JoinAsync` 在
启动尾部 stdout 读取前先 join 原 reader；仍需保留的 child 不强杀。
`tests/OwnerChannelTests.cs` 与冻结 revision 的 red mutation覆盖迟到 ACK 和错误 ACK
窗口，测试源码全部未执行；整合后必须重建并分别验证，旧结果不归于本次修复。
