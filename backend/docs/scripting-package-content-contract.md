# 效果包内容闭包实施合同

2026-10-03；依据用户已接受 R0 §7.1／ADR-0071。本批先实现包的不可变内容与目录／ZIP 传输，不修改工程、歌词或模板字段。

## 1. 唯一内容与修订

Core 的 EffectPackageContents 保存规范化 manifest.json 和逐 ordinal 路径排列的不可变文件字节、role、SHA-256；不暴露调用者数组，不保存本机路径。输入文件须与 files 声明完全一致，无缺失或额外文件；manifest 不列自身。csharp／shader／graph 源使用严格 UTF-8，不用替代字符修复。

顶层严格检查 R0 的 packageVersion、packageId、name、version、sdkVersion、shaderProfile、files、parameters、exports；版本和 profile 均由调用者传入明确支持值，不以程序集版本猜测 SDK 合同。参数／导出项作为完整规范 JSON 保留，后续语义解析必须校验全部字段与依赖；此内容集合不冒充已验证的可运行包。

所有 JSON 对象拒绝重复键，属性按 ordinal 排序，数组保留顺序、字符串按 System.Text.Json 写出、数字保留合法原始数字词形；忽略空白与对象属性顺序，不擅自舍入数字。此固定规范用于内容修订，原编辑文本不因导入而写回外部文件。

RevisionHash 是 sha256:＋规范清单及排序文件的路径、长度和内容摘要；每段明确使用 64 bit little-endian 长度前缀，文件长度为 little-endian Int64，摘要为 32 byte 原值，避免拼接歧义。role 已进入规范清单。参数默认值、README、预览和任意文件内容都影响资源修订。C# 源投影只传 csharp 内容给既有 ScriptCompiler，图片变动不进入其编译指纹；完整入口及导出验证继续按 R4。

## 2. 目录与 .mkfx

Application 从可编辑目录或 ZIP 读取相同闭包；不在 Scripting 中引用 Application，不新增程序集。路径使用相对正斜线，拒绝空段、.、..、绝对路径、反斜线、冒号、控制字符、重复项、链接及逐路径段的跨平台大小写冲突；文件与目录前缀冲突同样失败。ZIP 直接读条目，不将任意条目先解压到用户目录。

分享输出独立 .mkfx：同目录临时文件写完整 manifest 和内容，再发布目标；已有目标保留到完整成功，失败／取消清理本次临时文件。资源排序及 ZIP 时间戳固定，可重复生成；不复制本地 DLL／PDB、绝对路径或运行历史。

不存在自动写用户工程、删除外部包、资源 GC 或旧格式转换。必须带完整源码和资源，DLL-only 导入仍不开放。

## 3. 验证与边界

以真实临时目录／ZIP 验证当前清单、哈希、不变性、规范化、资源修改、大小写／路径／链接、缺失／多余文件、版本／profile、重复 JSON 键、错误 UTF-8、取消和原子分享。包移走原目录后仍可重开，并通过现有离线 Roslyn 实际编译和调用 SDK 入口；资源变化影响 RevisionHash，但不改变同 C# 的 ArtifactHash。

本批不是完整包语义解析、材质／图资源加载、受管工程保存事务或 UI 导入按钮；parameters／exports 的字段及输出依赖仍必须由后续语义解析验证，当前闭包不能直接启用。正式工程的数据断代与历史／模板依赖继续按 R4，不提前把 schema 22／16／4 写入现有工程。

**0.9.20 接续**：九类参数和控制器语义、真实图片解码门禁／入口反射及生产帧消费已进入独立解析层，见 [包语义合同](scripting-package-semantics-contract.md)／ADR-0084。内容类型仍不自行启用包；材质／图和工程事务未完成。包读取现执行 R0 已接受的 256 MiB／1024／64 MiB 初始展开预算，包含清单，规范后的闭包也校验；这是逻辑内容预算，不是内存峰值承诺。预算参数在宿主 API 中显式传入，CancellationToken 为最后参数。

## 4. 0.9.19 实施记录

代码入口：Core/Effects/EffectPackageContents.cs、Application/EffectPackageTransfer.cs、Scripting/Compilation/EffectPackageSources.cs。真实搬目录／分享重开／离线 SDK 调用与源码诊断、内容修订与编译指纹分离、实际写入取消保护原文件已经验证；既有编译工具链和生产依赖保持。

真实哈希检查、规范属性排序、原子分享三项变异实际检出后恢复；日志 `/tmp/makokara-pack-content-hash-mutation.log`、`/tmp/makokara-pack-content-canonical-mutation.log`、`/tmp/makokara-pack-content-atomic-mutation.log`。新增 Application **11**、Core **25**、Scripting **3**；全量构建 **0 警告／0 错误**，八项目 **2033 项通过、0 失败／跳过**（77／885／54／87／534／133／179／84）。最终日志 `/tmp/makokara-pack-content-build-final.log`、`/tmp/makokara-pack-content-tests-full.log`；启动证据见 roadmap §18.48。本批不提供参数／导出完整语义解析、工程自动导入或画布调度；待答叠放和五项原决策保持。

已直接启动最终 0.9.19 构建，PID **113225**；niri 确认窗口 52，标题及日志版本一致。日志 `/tmp/makokara-f93b-pack-content-final.log` 未见托管启动异常，有 JACK／ALSA 设备探测警告；未逐项人工验收音频播放，本批无新增脚本 UI。
