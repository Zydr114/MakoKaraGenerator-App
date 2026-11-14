# M07 有限采样工具

本工具只生成本轮本机的有限结构化／像素及耗时证据，不是 M07 完整验收或通用性能基准。
每次进程运行一个 `ScriptEffectsV2` 固定包（`v2 WordEmphasis`、`RepeatEmphasis`、`ClusterExit`）；SDK v1 已于 M14 移除。

## 输入矩阵

| 项目 | 固定值 |
| --- | --- |
| 合成／输出 | 640×360、透明、30 fps 源配置、365 s 总时长；查询时刻使用整数微秒 |
| 源行 | 0、1、2、180、181、360、361 s 各开始一行，每行持续 1 s |
| 文字 | 每行一个完整 `星光` token，Ruby 为 `ほし`；`Noto Sans CJK SC`、64 px，其余采用当前默认样式 |
| Word／Repeat cycle | 750000、180750000、1300000、360750000、1300000、750000 µs |
| ClusterExit cycle | 999999、1000000、1300000、1599999、1600000、180750000、360750000、1300000、999999 µs |
| 重复 | 同一会话内 cycle 重复 3–12 次，包含首次访问、同 cycle 重复和跨 cycle 返回 |

单 token 整句使本子矩阵中的 v1 token ordinal 与 v2 line ordinal 对齐。
它不覆盖多 token RepeatEmphasis 的全部语义，不证明任意 v1 字段状态已经映射。
多 token、缺时间、连字、Ruby、绑定和独立退场时长由对应 SDK／渲染回归另外验证。
这里的 ClusterExit 边界只对应包当前 600000 µs 退场声明；报告包含真实包修订。

## 构建与运行

先由负责集成的主代理串行构建；性能采样期间不要同时构建、跑测试或运行其他 GPU 采样。
本目录不应写入运行结果；使用新的证据目录，保存当前源码 SHA 和调用命令到本轮验收记录。

从 App 根目录构建：

```bash
dotnet build backend/tools/backend-script-render-upgrade/M07Runner/M07Runner.csproj -c Release
```

以下每条命令启动独立进程。将 `/tmp/m07-run-001` 换成未使用的证据目录；
参考程序集和 SDK 随项目引用复制到 Runner 输出目录，运行不读取开发机参考程序集作为替代。

```bash
dotnet backend/tools/backend-script-render-upgrade/M07Runner/bin/Release/net10.0/M07Runner.dll backend v2 WordEmphasis /tmp/m07-run-001/v2-word.json 3
dotnet backend/tools/backend-script-render-upgrade/M07Runner/bin/Release/net10.0/M07Runner.dll backend v2 RepeatEmphasis /tmp/m07-run-001/v2-repeat.json 3
dotnet backend/tools/backend-script-render-upgrade/M07Runner/bin/Release/net10.0/M07Runner.dll backend v2 ClusterExit /tmp/m07-run-001/v2-exit.json 3
```

输出需要当前仓库构建的全部运行依赖、真实可用 Vulkan 和对应字体。
字体名称是请求值，字体解析仍依赖运行环境；若要跨环境比较，需要另行记录实际字体／字体文件摘要。
工具没有启动 HTTP Server／Worker，也没有浏览器、Electron 或 shared GPU texture 传输。

## 证据格式与保护

报告 schema 为 `mako-sdk-m07-finite-v2`。成功时产生 `output.json` 和 `output.json.assets/`：

- 每个唯一时刻一份规范化 `timeUs.structure.json`，保存文字输出、冻结来源、最终几何、播放头和诊断。
  目标映射按 ordinal 身份排序；字典显式投影为数组，避免 TokenId 键的默认 JSON 转换。
  源／最终几何及图元内容保留生产顺序。该有限格式只接受纯色材质，其他材质明确失败。
- 每个唯一时刻一份 `timeUs.bgra`，640×360、紧凑预乘 BGRA，每份 921600 字节。
- 每次 Prepare／Render 调用一条记录。`repeatedTime=true` 只表示同会话已经查询过此时刻；
  重复调用核对相同 SHA-256，复用同一份证据文件，不表示独立进程、独立会话或更多唯一帧。

使用 `.pending` 文件的 CreateNew 预留和每份 asset 的 CreateNew 写入，最终报告通过不覆盖的 rename 发布。
若报告、pending 或 assets 路径已经存在，工具拒绝运行。失败／取消会保留 pending 和已写 assets，
不会生成成功报告；这些是不完整证据，下次使用新路径，不删除或覆写既有结果。
进程有 12 分钟协作取消期限；它不能中断不响应取消的原生调用或提供 Worker 硬终止隔离。
所有本次创建的求值／渲染会话按串行顺序释放；若本进程需外部停止，按记录的 PID 身份处理。

## 计时、比较及解释边界

`packageLoadMs` 包含包读取／闭包和导出解析。`compileMs` 只测显式编译，
`prepareSessionMs` 测直接求值会话创建，逐帧 preparation 的 `wallMs` 测 PrepareAsync，包含
纯求值或 legacy host 及其几何／历史成本，不绘制最终像素。`geometrySamples` 是计数包装器的
实际 GetSceneFrame 调用差，不能替代作者调用数或 GPU 计数。

`renderSessionMs` 测生产 Vulkan 会话创建，包括设备、资源和该工厂自身可能发生的再次编译。
分别报告 `explicitCompilationCount`、`renderSessionCompilations` 和 total，不能从总计数推定
只有一次编译或跨进程持久缓存命中。逐帧 rendering 的 `wallMs` 包含正式 Prepare／绘制／读回，
不含摘要、规范化、证据写盘和 Dispose；会话 Dispose 时间另列。
准备记录的 `processAllocatedBytes` 是全进程托管分配计数差，可能包含后台线程，不是当前调用精确内存峰值。

同一时刻的结构化摘要／像素摘要先在单次运行内强制一致，再从 v1／v2 对应报告与 assets 做有限对照。
不同样例或 SDK 的摘要不同不能自动判定回归；应检查具体变换、外观、冻结和像素差异。
v1／v2 RepeatEmphasis 比较仅对上面的整句 token 子矩阵成立。
首次查询、重复查询、前跳和后退分开解释，不能将混合 cycle 的均值当成单一 seek 成本。
跨进程 A/B 至少记录调用顺序及多轮样本，统计中位数／p95 时保留原始记录。

`separateBackendProbe` 是采样完成后新建的另一 Vulkan 设备及实际绘制／读回检查，
不是被测会话所持设备的直接身份记录。运行进程虽为新进程，OS、字体、驱动和 GPU 缓存没有清空，
不能将“第一次查询”称为冷设备基准。此工具不设置性能通过阈值，也不外推正式 HTTP／UI 可见延迟、
全工程表现或 M00–M06 旧门槛；M07 完成状态仍由本轮完整验收记录决定。
