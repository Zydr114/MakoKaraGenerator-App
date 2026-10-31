# B4-P2 package 资源闭包与连续预览租约计划

**计划基线：** `dev/backend-development`，2026-10-21。当前主线已登记 §70 无音轨 package 导出代码接缝、§71 package 资源闭包可观察化、§72 连续 package preview 代次回归；这些条目尚未形成资源文件读取和跨任务租约的完整公共验收。本计划定义下一批可独立完成的后端切片，不把测试数量换算成整体完成度。

## 1. 目标与有限交付

本批只完成“受控 package 内存闭包的认证读取 + 连续 preview 期间的固定闭包租约”。目标是让一个已经被项目快照接受的直接效果包，在一次 preview session 中拥有可审计、不可变、可释放的资源闭包，并能通过现有 preview transport 读取包内文件的 `HEAD`、`GET`、`Range`、`ETag` 和 TTL 语义。

交付应包括：

- 在 Writer 内以同一 project revision 固定 package closure revision、manifest identity、manifest SHA、每个 package file 的 path、role、size 和 SHA；preview generation 只引用这份固定快照。
- 为允许的 package file 建立受认证的资源句柄和读取接口。句柄必须绑定 client、project revision、package closure revision、file identity 和租约；不返回主机路径，不接受客户端拼接路径。
- 在 preview create、换输入、seek、resize、alpha change、close、project close、取消、过期和恢复失败路径中，正确取得、转移和释放 closure lease。旧 generation 的句柄、cursor 和 diagnostics 读取必须稳定失效。
- 复用既有 preview session、SSE、PNG/诊断资源、ACL、任务账本和排水机制；不增加第二套 preview job registry 或绕过现有授权。
- 先支持 package 内存闭包中明确允许的静态角色及其原始字节读取。未知 role 保持稳定拒绝并可审计；资源是否能被编译、运行或解码仍由后续独立切片决定。

本批的完成定义是：匹配 self-contained Server/Worker 发布上的真实 HTTP 能完成上传或打开合法 package、读取闭包、创建连续 preview、换代、seek/resize、读取帧与 diagnostics、关闭并验证旧句柄失效和 lease 排水；普通/透明短多帧结果、ACL、Range/HEAD/ETag、过期和一次重启恢复均有独立证据。

## 2. 明确不包含

以下能力不纳入本批，也不能从本批证据外推完成：

- 外部图片、视频、字体的搬移、解码、媒体打开或跨机器字体确定性；本批只读取已固定于 package snapshot 的包内字节。
- `preset+package` 联合执行、作者草稿→源码编译→采用→发布（B5）、任意作者代码执行或新的脚本隔离模型。
- package 带音轨导出、package-aware analysis、scene measurement，以及完整 B4 验证/导出/分析任务。§70 的无音轨导出仍需独立 fresh HTTP 验收。
- 完整工程长片、性能、GUI、平台矩阵、浏览器产品验收和跨浏览器 Blob/bitmap 生命周期。浏览器只作为后续 B6 的独立证据层。
- 任意主机文件访问、目录枚举、路径解析、服务端静态文件托管或通过 URL 暴露凭证。

## 3. 当前缺口与实现原则

§71 的 `packageClosure` 是有限内存检查和身份投影；`validated` 不表示文件可读、可解码或已取得租约。§72 已覆盖 generation/close 的定向 transport 回归，但没有 fresh Server/Worker、文件读取、跨 client、lease expiry、restart/recovery 或长时间压力证据。实现必须沿真实调用链核对以下不变量：

1. **固定输入。** package bytes、manifest/file identity、size/SHA 和 closure revision 来自同一次 Writer 内的不可变 snapshot；Worker、编码器和资源读取不能重新读取当前工程。
2. **授权读取。** 每个 file handle 必须检查 client、project owner、session/generation、closure revision、file SHA/size 和租约状态；跨 client、旧 revision、旧 generation、错误 path、删除/替换后的旧句柄均拒绝。
3. **换代释放。** 新 generation 提交前淘汰旧 generation 的 pending/frame/diagnostics 引用；只有仍被读取的已发布资源在 TTL 内保留。close、project close、cancel、expiry 和失败恢复必须最终释放 closure lease。
4. **字节一致。** `HEAD` 的 size/ETag 与 `GET`/`Range` 字节一致；ETag 绑定固定 file SHA/size，Range 边界、无效范围和已过期句柄使用稳定错误。
5. **错误隔离。** 资源缺失、身份不匹配、租约过期、预算拒绝和 Worker transport failure 必须保持不同错误类别；不发布占位帧，不把资源读取错误伪装成 package 编译或 runtime 诊断。
6. **排水可观察。** Server、Worker 及其受管理子进程在正常 close、取消、expiry、restart/recovery 后都有 terminal drain 检查；不使用强制清理掩盖泄漏。

## 4. 分阶段实施与验收

### S0：合同和范围冻结

冻结 package closure/file identity、允许 role、句柄字段、Range/HEAD/ETag/TTL、错误码、generation 绑定和 lease 转移语义。更新 Contracts、统一 API、OpenAPI、contract manifest 和正反例；只写入实际字段与计数，不预写测试或 HTTP 数字。

### S1：Hosting/Server 资源句柄与租约

在现有 ResourceStore/preview session 基础上实现固定 closure snapshot、认证 file handle、读取接口和 lease 生命周期。保持所有读取在授权层之后；句柄使用固定 bytes，不读取主机路径。补充 Hosting/Server 定向回归：同 revision、旧 revision、跨 client、Range/HEAD/ETag、替换/删除、generation 淘汰、close、expiry、cancel 和错误排水。

### S2：连续 preview 调用链

沿 `capture → closure inspect → lease acquire → preview generation → frame/diagnostics read → generation invalidation → lease release` 验证真实异步生命周期。覆盖普通/透明短帧、前进与回退 seek、resize/alpha 换代、高频 pending、旧 cursor/handle、project close 和重建 session。确保 package closure 与 preview generation 同步，不能只验证帧句柄而遗漏文件租约。

### S3：匹配发布与真实 HTTP

使用 source-clean、Server/Worker/refs 匹配的 self-contained 发布，记录部署文件和源码输入 SHA。真实探针至少完成：

- 合法 package upload/open、project revision 与 closure 摘要读取；
- manifest/file `HEAD`、完整 `GET`、合法与越界 `Range`、`ETag`/`304`、TTL/过期；
- preview create、SSE/state、连续普通/透明多帧、seek、resize、alpha change、frame/diagnostics 读取；
- 旧 generation、旧 cursor、旧 file handle、跨 client、错误 owner、替换/删除后的读取拒绝；
- close/cancel/project close 后 lease 和 Worker 排水；一次 previous-host restart/recovery 后成功报告/帧读取与活动任务处理；
- Server exit code、Worker identity/startTicks、forced cleanup、survivor 和 evidence/credential 扫描。

202/accepted 必须等待 terminal state；凭证不得出现在 URL、日志、截图或提交中。PNG/帧证据要与固定 package closure、source revision、frame plan 和 file SHA 绑定；只证明字节可读时，不把它记为图片/视频/字体解码成功。

### S4：文档收口与交接

将实际产品/合同版本、源码 HEAD、发布 manifest、HTTP manifest、请求/断言计数、进程排水和失败目录登记到 `docs/backend-extraction-progress.md`；更新 `docs/backend-capability-gap-audit.md` 的最新矩阵和 `web/README.md` 交接边界。浏览器仍单独标记为未完成，除非另有真实浏览器证据。主线保持 `dev/backend-development`，每个可审阅阶段使用独立 Git 提交。

## 5. 证据边界

必须把以下证据分开记账：

| 层级 | 可以证明 | 不能证明 |
|---|---|---|
| 代码/Hosting | snapshot identity、closure hash、句柄、租约、generation invalidation、错误和排水调用链 | 公共合同、真实发布、真实媒体或浏览器行为 |
| in-process Server/Worker | ACL、Range/HEAD/ETag、旧句柄、expiry、close、跨 client、替换/删除和定向排水 | fresh self-contained 产物与真实部署环境 |
| fresh HTTP | 匹配 Server/Worker 上的真实 package 读取、连续帧、SSE、旧句柄拒绝、restart/recovery 和进程排水 | 浏览器页面无泄漏、跨浏览器、外部媒体/字体解码、完整 B5/B6 |
| 浏览器 | 鉴权 fetch、Blob/bitmap 释放、播放/seek/resize 和页面生命周期 | 未执行的后端能力、跨平台完整工程或作者闭环 |

代码存在、定向测试通过或 §71/§72 的现有回归都不能替代 fresh HTTP；浏览器报告也不能替代后端资源 ACL 和 Worker 排水证据。

## 6. 交付门槛

本批只有在以下条件全部满足后才能登记为“资源闭包与连续预览租约有限 HTTP 切片”：

- 合同、错误码、预算、租约和 generation 语义已冻结，生成物校验通过；
- 受影响 solution 构建零 warning/error，定向回归无未解释失败，至少有一条有意义变异在缺字段、放行旧句柄或跳过释放时变红；
- fresh 发布 sourceDirty、部署 SHA、Worker refs 和运行身份可追溯；
- 普通/透明短多帧、Range/HEAD/ETag/TTL、ACL、旧代次、close/expiry/recovery 和最终排水均有真实 HTTP 证据；
- 文档、失败目录和远程分支提交一致，未把外部媒体、preset+package、B5/B6 或浏览器验收写成已完成。

在这些门槛满足前，本批状态只能写作“计划中”或“代码/定向回归切片”；不得用 §70–§72 的现有记录宣称完整 B3/B4。
