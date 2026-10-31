# 背景依赖二维玻璃材质

纯资源 profile2 包 `makokara.glass-ink` 提供两个明确独立的材质导出，均允许fill／stroke：
- `glass-media`：“媒体背景玻璃”，只读取实际 BackgroundMedia；没有背景媒体时正常输出明确失败。
- `glass-backdrop`：“真实下层玻璃”，读取 BackdropBelow，包括宿主在当前目标前已经合成的真实下层；
  本图声明opaqueCanvas背景需求，透明输出会跳过；这是输出策略声明，不是逐像素不透明验证器。

在模板工坊导入本目录，在未唱或描边材质来源明确选择一种导出并调参。
源材质存储的是完整包修订／导出／规范参数，没有假控制器或第二份背景路径。
不捕获当前目标自己的最终图或尚未绘制的内容，不声明owned overlay、组图、父捕获或跨输出依赖。

背景首先由正式Blur节点处理；shader再以有界二维正弦偏移重采样模糊背景并叠加高光。
这不是背景原样复制：默认blurPx=8逻辑sigma、refractionPx=4逻辑px、frequencyHz=2Hz、
highlight=0.2、opacity=0.75。blurPx范围0～8，refractionPx范围0～8，frequencyHz范围0～20，
highlight范围0～0.5，opacity范围0～1。将模糊、偏移和高光全部设为0才得到无处理背景采样。
偏移向量长度不超过refractionPx；Blur完整3sigma外扩及采样界为24px，后级外扩及采样界为8px，
图累计最大外扩32px。Blur域外clamp，后级域外transparent，不裁剪预算或省略采样声明。

shader在实际 materialRegion 的TargetRegionCoverage中产生预乘颜色，coverageMode=masked；
区域alpha只乘一次，宿主严格验证越界并应用独立源样式Alpha，不再次乘字形蒙版。
基础字与其注音使用真实同一族外观，描边使用真实外部描边区域；不改变几何、排版或锚点。

两个导出均明确materialFallback／identity：透明输出跳过整个背景依赖图，使用本包固定蓝色solid
`#3380CCBF`（约0.75alpha），保留真实源路径／MaterialHandle提示。fallback不是背景泄漏或运行shader的假成功。
opacity等图参数在透明模式仍严格验证，但被跳过图的参数不重新计算这份固定基础外观。
非法shader／ABI／参数／文件摘要仍拒绝，不由透明模式掩盖。

与RepeatEmphasis同工程组合时保留其绑定元素的已唱solid基线，本材质可用于未唱／描边或独立目标。
无C#、DLL或外部图像资源；SDK1.0.0为当前宿主精确协议值，不代表完整SDK已冻结。
图、独立程序、SkSL、README均进入清单摘要；修改后更新摘要，可独立分享 `.mkfx`／模板 `.mktpl`，
保存工程后无需原目录。此例是二维CPU模糊／折射近似，不承诺三维光学或性能基准。
