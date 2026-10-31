# R1 C#／原生 shader 能力验证报告

> 2026-10-02；程序 **0.9.1**，工程／歌词／模板 schema **21／15／3**。
> 对照[施工路线 R1](../plans/scripting-system-implementation-roadmap.md#r1双链技术验证)及 ADR-0071。R0 的 A／B／UI 已接受，R1 技术出口达成；**完整 SDK、生产渲染图、工程效果及工作区仍按 R2～R8 实施**。

## 1. 结论与验证范围

锁定的 SkiaSharp **3.119.4**／Linux native **CPU raster** 路径已实画下表能力。当前合同的二维材质、背景模糊／折射近似及整行 RGB glitch 可以继续实施，不需要为这些样本引入 GPU 或升级图形库。这里不承诺三维物理玻璃、SDF／法线生成、HDR、图像历史或吞吐性能；完整性能基准继续后置。

| 能力 | 实际证据 | 当前边界 |
|---|---|---|
| 离线 C# 编译、调用及卸载 | Roslyn 5.0.0、分发 net10.0 引用、DLL／portable PDB；SDK TokenId 在宿主与可回收加载上下文同型，实例释放后弱引用回收 | SDK 仅有最小身份值，完整生命周期在 R2／R5 |
| 持久 DLL 重开复用 | 原子内容地址目录；另起 dotnet 进程只读取 DLL／PDB 并调用，不再执行 Compile；坏字节或错误目录代次拒绝 | R4 继续接包清单／资源／SDK profile 指纹及工程引用 |
| 编译诊断和取消 | 错误源码具名文件、1 基行列、错误文本；取消保持 OperationCanceledException | UI 诊断和导出门禁在 R7／R5 |
| 自定义文字材质 | RuntimeEffect 实编译 SkSL；真实中文和注音整形；时间 uniform 改变墨迹，背景保持原像素 | 示例固定材质，不是可导入用户包 |
| 外部描边材质 | 彩色 shader 绘制外扩轮廓，减去 glyph coverage；不透明白色填充内部不受描边改色影响 | 中立材料／蒙版协议在 R3 |
| 整行捕获与 RGB glitch | 行与注音一起捕获，真实子图像 shader；非零位移改变像素，零位移恢复原图（8bit 通道允许 1 级取整差） | typed graph、组合和 AnchorMap 在 R2／R3 |
| 玻璃来源和替换合成 | 媒体／目标后方已合成内容两种输入，真实 sigma=8 模糊、4 逻辑像素折射；覆盖一次，半透明 coverage 不重复叠背景 | 多实例及父组隔离规则在 R3 |
| 世界变换与密度 | 真实父组位移／旋转；640×360 逻辑场景在完整及半尺寸 raster 对比，子图采样按逻辑单位换算 | 源 Token／Ruby 独立族变换在 R2 |
| 透明回退和预乘 Alpha | 背景玻璃使用固定纯色基础外观；没有背景像素，BGRA 各通道≤alpha；透明 glitch 继续有效 | 生产输出计划、提示清单及图滤镜原图 bypass 在 R3／R7 |
| 预览／成片同帧 | 原生样本管线各编码 1 秒 MP4／ProRes 4444，提取第 15 帧与同时间 CPU 结果比较；失败帧为 0 | 这里是真实编码验证，但尚不是脚本工程闭环 |

## 2. 代码与可重现验证

- `src/MakoKaraGenerator.Scripting.Abstractions`：BCL-only 公开 SDK 的最小 TokenId。
- `src/MakoKaraGenerator.Scripting/Compilation`、`Loading`：固定引用编译、具名诊断、内容地址 DLL／PDB、合作卸载。随项目输出／发布复制参考集及许可证，不使用开发机 SDK 查找或联网引用。
- `samples/TechnicalSpike/Rendering/RuntimeShaderFrameRenderer.cs`：明确放在技术样本内的原生 shader／捕获探针；未把生产 SkiaRenderPipeline 偷换为私有脚本渲染器。
- `CompilerTests`、`RuntimeShaderCapabilityTests`、`RuntimeShaderExportTests`：真正编译／原生绘制／编码与新进程复用；`RenderFrameAlphaTests`：实际读取协议回归。

```bash
dotnet build MakoKaraGenerator.slnx -m:1
MAKOKARA_SCRIPT_SPIKE_OUTPUT=/tmp/makokara-r1-proof \
  dotnet test MakoKaraGenerator.slnx --no-build -m:1
```

全量 build **0 警告／0 错误**，八个测试项目 **1537 项通过，0 失败／跳过**，比 1512 基线增加 25 项：编译 9、原生 shader 9、像素读取 5、真实编码 2。Windows 本机、完整发布包与长期运行未验证，不用 Linux 样本替代这些门禁。

本机可选证据目录 `/tmp/makokara-r1-proof` 包含七种 `native-*.png`、`shader.mp4`／`shader.mov`、对应解码 PNG 及 `compiled/<artifactHash>/{effect.dll,effect.pdb,build.json}`。目录可按上述命令重新生成，不纳入仓库；PNG 已目视复核，玻璃效果是验证输入及合成能力的测试外观。构建／测试日志为 `/tmp/makokara-r1-build.log`、`/tmp/makokara-r1-tests.log`。

## 3. 验证中发现的旧错误

`RenderFrame.TryGetPixel` 声明返回直通颜色，但原 Unpremultiply 对 `channel == alpha < 255` 错误返回 alpha，饱和颜色读暗。五种 Alpha 的最小输入 `[alpha,0,alpha,alpha]` 在修复前四项失败、一项通过；修复后全部返回 `(255,0,255,alpha)`，且原 BGRA 字节不变。

修复只改变**像素读取辅助方法**的直通结果，不改原生渲染、缓存／编码像素或已有样式外观；真实透明成片同帧比较不再受这个旧错误污染。避免把访问器错误归因于 shader 或编码器并修改正确图像。

## 4. 进入 R2

接下来落实完整中立身份／只读目录和进度、目标／writer／图值、整行一次整形后的 Token／Ruby 绘制单元、元素族视觉变换、最终几何和光标，接入共享准备帧接口。逐字强调作者源码必须通过真正 SDK 编译，覆盖父组、槽位、单句微调和前奏，并明确拒绝不可独立分割的跨元素字簇。

R1 不宣称包导入、参数历史、callback、粒子或脚本 UI 完成；这些保持 R4～R8 范围。源码／格式和 UI 继续按已接受合同，不重复询问已明确事项。
