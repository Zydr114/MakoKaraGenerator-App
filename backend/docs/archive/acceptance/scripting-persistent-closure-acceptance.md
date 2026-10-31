# 持久闭包与固定 DLL 组合验收（0.25.7）

> 2026-10-05。本文对应 `scripting-system-next-work-plan.md` 第 2 批的持久闭包出口。
> 验收覆盖当前已支持的工程、模板、包资源与正式会话组合；最终 GUI、完整 SDK、直接图／材质实例和跨输出执行仍按原合同接续。完成状态以 roadmap §18.85 为准。

## 1. 包修订与 DLL 身份

包修订包含完整清单、固定源码及资源字节。工程、模板和分享文件持有这份独立内容闭包，DLL／PDB 属于本机可重建缓存，不复制进工程或模板。工程版本、各自 schema、包 profile、路径、文件集合及资源 hash 在读取闭包时严格校验；原生 shader 的编译、反射和 ABI 在建立正式会话时继续验证。

编译 Artifact 身份取固定源码路径／文本、完整入口集合、编译选项、Roslyn 版本及实际参考程序集字节。参数和图片／shader／图资源不进入 C# 编译指纹，因此只改图片的包新修订可以复用同一 DLL。正式会话始终重新固定当前包资源；不能把命中 DLL 缓存视为当前修订的资源已验证。

当前 Artifact 的 DLL／PDB／身份损坏明确拒绝会话，不回退旧 Artifact 或重编译覆盖损坏代次。失败源码仍可保存、接受、重开和撤销；编译或初始化失败在编码器启动前阻止导出并保护旧文件。作者诊断保留真实文件行列。既往 Roslyn 内部 Emit 间歇异常的根因仍未确定，本批不改变编译运行时配置。

## 2. 验收证据

| 本计划第 2 批要求 | 自动证据 |
|---|---|
| 同一入口保存歌词、媒体、已接受分析、源码与图／材质／图片闭包；另存为、备份和搬移独立 | `UnifiedProjectClosureTests`、`MediaAnalysisStorageTests`、`ProjectEffectsStorageTests`；本批 `ProjectRenderSessionTests.Closure` 把模板分享、源材质、控制器、分析与背景组合到正式会话 |
| 发布失败不切换旧入口，不覆写旧修订 | `ProjectRevisionWorkspaceTests`、`ProjectEffectsStorageTests`、`UnifiedProjectClosureTests`；自动备份按 0.25.6 完整目录发布，失败不裁剪旧备份或更新恢复标记 |
| 引用包拒绝注销，含禁用实例及不可见源材质 | `ProjectEffectsStorageTests`、`SourceMaterialTests`，所有实际引用都参与检查 |
| 工程版本、profile、文件集合、hash、链接与 Shader ABI 门禁 | 工程／媒体／包／模板存储测试；本批在已有 DLL 下分别拒绝非法 shader、uniform ABI 和图片解码 |
| 固定 DLL 可离线重建及复用，缓存损坏不能掩盖 | `CompilerTests` 的独立编译器及真实新进程复用；本批搬移工程／副本／备份在已有和空缓存得到相同 DLL／PDB 及普通／透明像素，旧 DLL mtime 保持 |
| 当前资源修订被实际消费 | 本批资源图片新修订沿用同一 DLL，正式主 Render 像素变为新图片颜色 |
| 源码工作区保存失败修订、单实例接受、定位、重开和撤销 | `EffectWorkspaceSourcePersistenceTests` 通过真实工作区与正式会话验证，导出不使用旧 DLL；本批损坏 DLL／PDB 时编码器未启动、旧输出与损坏缓存均保持 |

本批新增 8 项测试。统一闭包场景使用真实 48 kHz 音频、已接受 20 ms hop、背景图片、模板 `.mktpl` 分享、源包材质、实例 seed／参数／订阅及模板来源。删除外部源和分享归档后，对搬移工程、副本及备份分别建立预览与导出重放会话，比较 500 ms 像素；跳到 100 ms 再返回时结果相同，callback 数量与只读历史一致。这里的导出重放像素比较没有新编码视频；真实 MP4／Alpha 编码证据由既有全量回归保留。

## 3. 出口与下一步

本计划第 2 批的持久闭包要求已由现有生产实现及组合回归覆盖，后续进入第 3 批 R5：24／30／60 FPS 的 callback／放大／音频语义、重绘／跳转／导出／取消和 Error／Warning／Info 策略。这个出口不替代原实施路线中完整 R4／R7 的直接图／材质实例、跨输出执行及最终工作区出口，也不启用 R3.1 父组捕获、复杂映射或粒子桥接。

工程／歌词／模板 schema26／16／5、包 profile2、Shader ABI、公开 SDK、程序集和依赖保持；不迁移旧工程。定向会话回归 27 项通过，最终全量构建、回归和启动证据见 roadmap §18.85。
