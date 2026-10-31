# R8 公开资源包与作者交付（第 1 步）

> 2026-10-06；程序仍为 0.40.1。本步只增加公开示例包、作者索引、回归和文档，不改变宿主版本、工程／歌词／模板 schema、包 profile、SDK 程序集或依赖。

本步把 R8 计划中可实际导入的三类纯资源包放入 [`samples/ScriptEffects`](../../../samples/ScriptEffects/README.md)：

- `CustomInk`（`makokara.custom-ink`／`custom-ink`）是未唱／描边可用的 `unmasked` 纯资源材质。它只读真实颜色区域和时间，不依赖背景，也不包含 C#、DLL 或外部图片。
- `LineGlitch`（`makokara.line-glitch`／`line-glitch`）是绑定整行的 `ColorOnly` 分片位移图。它保留真实文字几何、注音和播放头，只改变颜色图像；不会把像素位移冒充 `ImageOnly`／`MappedWarp`。
- `GlassInk`（`makokara.glass-ink`／`glass-media`、`glass-backdrop`）提供媒体背景与真实下层两种 masked 材质。透明输出按图声明跳过背景依赖，使用包内 `#3380CCBF` solid fallback，并在帧中留下 `TransparentBackgroundDependency` 及真实材质来源诊断。

三个目录均为 profile2、SDK 协议声明 1.0.0、shader profile `skia-raster-3.119.4-v1`，manifest 对 README、graph、shaderProgram 和 SkSL 做 SHA-256 闭包校验。它们可通过脚本效果工作区导入目录或 `.mkfx`，保存工程／模板后删除原目录仍可重开。

自动回归覆盖：

- 目录完整性、未声明文件拒绝、目录复制、`.mkfx` 导出／删除源目录／重新导入，以及包字节和修订身份保持；
- 真实工程保存／重开、歌词注音与描边、普通／透明 Preview 与 Export 独立会话逐帧一致；透明 Glass fallback 检查真实覆盖掩码、预乘包内纯色和样式 alpha，避免把两条不同抗锯齿光栅路径强行逐字节比较；
- 媒体背景缺失、透明旁路、坏 shader、超范围参数、真实来源／阶段／实例／导出诊断；
- `RepeatEmphasis` 组合保留已唱 solid 基线，错误包材质在初始化／求值阶段明确失败。

本步验证结果：R8 定向 **10 项全部通过**，solution 串行构建 **0 警告／0 错误**。这些保留为第1步历史证据，不宣称R8完整出口。第2步同一作者工程的57秒普通／透明成片、另存为／备份及失败／取消恢复状态和用户验收路线见[R8成片验收](scripting-r8-acceptance.md)。2026-10-06用户已确认文字／粒子／资源效果均无问题并接受当前goal完成，见roadmap §18.108。R3.1／组图／父捕获／跨输出等原路线门禁保持，[后续计划](../plans/scripting-system-next-work-plan.md)记录本轮完成范围。
