# 私有 Worker 包身份协商

M07 在原有 `package-render-*-v1`、统一工程输入、图片预览、验证与导出私有 IPC 中增加
`PackageRenderWorkerCapabilities.supportedPackageProfiles`。HTTP API、浏览器静态预览来源协议和
500ms 控制节奏保持原合同；这个字段不授予新的公共来源 profile。

回执的顶层 `packageVersion`、`sdkVersion`、`shaderProfile` 保留 v1 基础身份：
包 2、SDK `1.0.0`、`skia-raster-3.119.4-v1`。新数组明确列出实际部署的 v1 和 v2 合同：

| 合同 | packageVersion | sdkVersion | shaderProfile |
| --- | --- | --- | --- |
| v1 | 2 | `1.0.0` | `skia-raster-3.119.4-v1` |
| v2 | 3 | `2.0.0` | `skia-ganesh-3.119.4-v2` |

编码器仍能读取没有数组的旧回执形状，便于产生稳定诊断；新 Host 准入必须收到同时包含这两条
精确身份的数组。旧 Worker、仅声明 v1、错误 v2 SDK 或 shader 身份均在发送固定输入前拒绝。
数组最多 16 项，不得为空、含空项、缺少身份字段或重复；嵌套未知字段和重复 JSON 字段同样拒绝。

Host 从固定工程闭包读取每个包的实际 profile，在打开、重新打开持久会话、验证和导出前逐一
核验该进程所声明的支持列表。SDK `2.0.patch` 使用当前 `2.0.0` 合同能力，归档与工程仍保留
清单里的实际补丁版本；未来 minor/major、混用 v1 shader 或未知包版本不会回退为 v1。
包 profile 与包内部 shaderProgram 文件的 profile 分别校验。

数组不代替原有引擎身份、实际编译引用集合 hash/count、来源 profile、验证/图片/导出独立能力、
资源预算和 Linux 进程隔离门禁。Host 继续把能力证明绑定到实际 Worker 进程代次；进程改变后须
重新协商。v2 必须在明确支持的 GPU 后端执行，不能借助此数组进入 CPU/raster 权威路径。

v1 在 M13 发布验收完成前继续保留。工程 schema30、效果 contract2、模板库8、脚本配置库2
共同承载显式 v1/v2 包；schema29 和旧库入口不隐式写入 v2，也不覆盖旧文件。
