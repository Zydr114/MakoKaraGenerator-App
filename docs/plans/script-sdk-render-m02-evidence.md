# M02 Worker 固定产物验收记录

日期：2026-11-03。状态：**功能、合同、匹配发布、真实 HTTP 与本机有限正式性能门槛通过**。
范围：Host 准备固定脚本产物、Worker 严格消费与依赖闭包。HTTP seek 进程复用属于 M03。

## 实现与边界

源码 `5f19aed`，产品 `0.110.5`；合同元数据同步提交 `f4d4198`，公共合同仍为 `1.42.3`。
本机原始证据保存在 `.local/script-sdk-m02-20261103/`。

- Runtime 工厂依赖 `IScriptCompilationService`；Server 为作者与固定输入准备共享编译服务。
  Worker 只使用 `FixedScriptArtifactProvider`，不含编译回退；shader、图、资源和入口校验仍在 Worker。
- Host 固定启用实例及材质引用包；原始源独立保存为 `source.bin`，上限 4 MiB。
  私有 IPC 元数据 1 MiB，PE/PDB 各 32 MiB，总复制产物 256 MiB；输入根目录权限 0700。
  总预算在复制前预检，读取分配前核对实际长度与剩余预算。
- 信封钉住完整描述及完整 manifest SHA，覆盖成功警告；Worker 在同一次读取中先验摘要再解析。
  协同替换 PE 与 manifest、同长度警告替换、链接、缺失、超限和身份不匹配均拒绝。
  原始 source SHA 与私有 envelope SHA 分开，保留既有来源身份。
- Host 编译准备纳入操作期限；Stop/Dispose 会取消尚未完成的准备。预览、验证、导出的失败归属保持。
  固定输入租约保留到最终关闭 ACK 或确认实际进程排水，删除失败保留重试。
  M03 可在同一 Worker 内用同一 `PreparedInput` 重开 Runtime；本节点不宣称 HTTP 已复用进程。

## 构建与回归

`mutation-restored-build.log`：完整 solution 0 警告、0 错误。
Hosting 1494、Scripting 292、Worker 139、Runtime.Hosting 124、Backend.Rendering 280，
合计 2329 通过、0 失败、0 跳过。Hosting/Scripting 在恢复变异后完整重跑；其他三组属于相同生产实现。

真实 Worker 测试记录 PID/startTicks 和实际加载程序集名称、路径、MVID：没有 Compiler 或 Roslyn。
两个真实 Worker 使用同一 Host 编译服务只 Emit 一次；同 PID 内部重开保持相同目录、信封及文件字节，
最终关闭后归还输入。慢编译、准备中 Dispose、预览／验证／导出期限分别有覆盖。

可逆变异取消完整描述/manifest 绑定及 Host 准备期限后，codec 2 项、实际 Worker/期限 5 项按预期失败；
原始四个生产文件 SHA 恢复一致，完整构建及 Hosting/Scripting 恢复通过。
证据为 `mutation-codec-red.*`、`mutation-worker-red.*`、`mutation-restored-*-green.*` 与 `mutation/`。
一次并行 Hosting 运行触及既有 8 秒真实编译 fixture 期限；保留失败日志，串行全组通过，未放宽期限。

## 合同、发布与实际 HTTP

新 CLR metadata 冻结/校验通过：112 路径、125 操作、597 schemas、3431 示例（含 2547 反例）。
`dist/deployment.json` 为同源码 self-contained Release/linux-x64，记录
`f4d4198a85a6a2a33e441d42978840b70bb2cebe`、`sourceDirty: false`。
manifest SHA-256 为 `25fa0a5caf8801d4fef98cefaca49eebe3ee10e4b14206db44414c4e8e78f1f7`。
fresh restore/publish 使用环境 `AllowMissingPrunePackageData=true` 处理本机 SDK prune 元数据缺失，未修改依赖版本。

`publish-closure.json` 确认 Worker 文件及 deps 无 Compiler/Roslyn，Server 含编译服务；两侧 SDK 字节一致。
这与实际加载程序集检查是独立证据；先前 Debug 陈旧 assets 中的依赖条目不作为最终发布结果。
图像 fixture 工具仅编译工具入口：运行 DLL 取固定 Worker，Compiler/Roslyn 取固定 Server，核对字节，未重建生产依赖。

| 新输出目录 | 实际范围 | HTTP 请求 / 断言 | 凭证扫描文件 |
|---|---|---:|---:|
| `http-author` | 作者 C#/图/shader 编辑、编译、预览、保存及重启认领 | 138 / 962 | 353 |
| `http-render` | 工程/作者/模板/候选 CPU→GPU→CPU，双客户端，测量、验证、短视频导出 | 424 / 2947 | 995 |
| `http-render-unavailable` | GPU 不可用拒绝，原 CPU 代次/帧/诊断保持并继续渲染 | 41 / 313 | 106 |

三组均通过，所属进程排水完成。正向组记录 40 个实际 GPU Worker 的 RADV/DRM 证据；
完整 PNG 读取/解码、CPU 返回一致性及完整短视频解码通过，不将其称为跨 CPU/GPU 像素相等或桌面显示验收。

## 正式性能

使用同一 `backend-script-render-upgrade/probe.py`，旧版 M01 `dist-cold-fixed` 与上述 M02 发布，
独立 profile、WordEmphasis、320×180、CPU、0.5/1/2/3 秒循环，每轮 20 次 seek，三轮交替 old/new、new/old、old/new。
时间从 HTTP 发出到完整 PNG 读取，含 50 ms 轮询，不含 PNG 证据写入/解码及 UI 显示。
2026-11-03 14:39:28–14:43:27 UTC（北京时间 22:39:28–22:43:27）完成采样。
本任务其他构建与测试均停止，普通桌面进程仍运行；边界 GPU busy 观察为 8–25%，不声称全机零负载。
`seek-idle-ab/comparison.json` 保存全部样本、交替命令、工具摘要、环境与 nearest-rank p95 比较。

| 60 样本/侧 | M01 基线 ms | M02 ms | 110% + 5 ms 上限 | 结论 |
|---|---:|---:|---:|---|
| 中位数 | 2016.793 | 1336.547 | 2223.472 | 通过 |
| p95 | 2510.698 | 1850.308 | 2766.768 | 通过 |

六次运行均通过，同时间解码 RGBA SHA 在两侧和全部轮次一致，凭证扫描与所属进程排水完成。
两侧每轮均观察到 20 个 Worker，说明本节点减少的是渲染进程的编译职责，未冒充 M03 的进程复用。
结合真实两 PID/单次 Emit、Worker 不加载编译器及发布闭包检查，实际编译成本下降门禁通过。
该结论仅覆盖本机与上述有限 HTTP 场景，不外推长时间线、桌面显示或后续节点。
