# 背景依赖二维玻璃材质（SDK v2）

纯资源 package3 包 `makokara.v2.glass-ink`，两个独立材质导出，均允许 fill／stroke：

- `glass-media`“媒体背景玻璃”：只读取实际 BackgroundMedia。
- `glass-backdrop`“真实下层玻璃”：读取 BackdropBelow，即当前目标之前已合成的真实下层。

背景先经 Blur 节点，再由 shader 以有界二维正弦偏移重采样并叠加高光。默认 blurPx=8、
refractionPx=4、frequencyHz=2、highlight=0.2、opacity=0.75；全部置 0 才是无处理背景采样。
`coverageMode=masked`，区域 alpha 只乘一次，不改变几何、排版或锚点。

透明输出跳过整个背景依赖图，改用包内固定蓝色 solid `#3380CCBF` 并留下诊断；
这不是背景泄漏，也不是 shader 的假成功。没有 C#，修改文件后须更新 manifest.json 中的 SHA-256。
