# 自定义未唱／描边波纹材质

纯资源 profile2 目录包：`makokara.custom-ink`，材质导出 `custom-ink`，允许 fill／stroke。
没有 C# 控制器或帧内编译；清单 SDK 1.0.0 是当前宿主精确协议值，不代表完整 SDK 已冻结。

在模板工坊导入本目录后，从“未唱”或“描边”材质来源选择“双色波纹”。参数来自清单：
frequencyHz 默认2Hz（0～20），amplitude 默认0.15（0～0.5），colorA／colorB 为直乘 sRGB RGBA。
波纹在固定逻辑画布坐标变化，读取 `host:timeSeconds`；预览和导出使用歌曲时间，不按显示器帧推进。
频率为0冻结时间项，幅度为0得到两色中点；颜色 Alpha 由 shader 预乘一次。

`coverageMode=unmasked`：shader 只产生颜色，宿主对真实字形／注音或外描边区域覆盖一次，
并按实际源样式应用局部及共同 Alpha。不改变字形、排版、几何或光标，不读取背景，没有采样偏移或外扩。
本包基础外观为蓝色纯色 `#3380CCBF`；该不依赖背景的图在透明输出时继续执行，不因透明而回退。

也能显式选择已唱槽，但公开 RepeatEmphasis 要求其绑定目标的已唱基线为 solid；
同工程组合时把本材质用于未唱／描边，或采用不同绑定，不采样某个像素假冒已唱基色。
保存模板／工程后持有完整闭包，可分享 `.mktpl`／`.mkfx`；资源离开原目录后仍按内容修订读取。
修改本 README、图、程序或 SkSL 后须更新 manifest.json 中相应 SHA-256；包不包含 DLL 或 native 缓存。

不声明组图、父捕获、owned overlay、跨输出、ImageOnly 或 MappedWarp。
