# 已保存工程图片背景导出有限计划

日期：2026-10-24。产品 **0.101.0**／合同 **1.35.0**。本计划定义 `project-image-export-v1` 的有限后端切片；fresh HTTP 尚未验收。

仅无预设 `static-lyrics-scene-v1`、`static-lyrics-scene-analysis-v1`、`package-lyrics-scene-v1` 的已保存 Image 主背景可用，package 仅无音轨。客户端继续使用既有 export-jobs 请求，不提交图片 bytes、路径、profile、grant 或编码参数。

Worker 使用 KIE1 32 字节头，完整任务输入上限 4,194,272 字节；metadata 上限 32KiB，原图 2MiB、4096 单边、解码 BGRA 16MiB、base snapshot 4MiB、source values 200000、媒体 64MiB、音频 16MiB、报告 256KiB。Worker 独立能力含 `project-image-export-capabilities-v1`、decoder `memory-image-bgra-v1`、三基础来源及完整 package/export capability。Host 的 job input 使用 image task profile 与完整 envelope SHA；exportInput 同时登记 taskProfile/baseSourceProfile，旧导出保持省略。

两次 Writer 进入之间固定受管图片闭包、音频 lease、revision 与 owner；第二次进入重新核 session 对象身份与 ClosePending，任一失败释放未转移 lease、reservation 与 candidate。实际 Worker、Runtime、FFmpeg/ffprobe 排水及报告/媒体身份核对须通过后才公开产物。

本轮只登记合同、schema、fixtures 与实现边界。图片 measurement、preset/video/font、完整 B3/B4/B5/B6、浏览器/跨平台和性能长片保持独立；HTTP 计数待匹配发布后的 fresh 验收补录。
